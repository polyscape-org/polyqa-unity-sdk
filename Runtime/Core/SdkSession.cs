// UnityEngine に依存しない。1 接続 = 1 セッション。
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polyscape.RPCLite;
using PolyQA.Unity.Protocol;

namespace PolyQA.Unity.Sdk.Core
{
    /// <summary>
    /// 接続ごとの SDK 実装。Driver の購読内容に従って抽出し、Driver が受信確認した状態との差分だけを送る。
    /// <list type="bullet">
    /// <item>送信中（応答待ち）の間は次の差分を作らない。Driver が遅くても差分が溜まらず、最新状態へ合流する。</item>
    /// <item>全量（購読開始・再同期）と大きな差分は <see cref="ChunkSize"/> ノードずつ分割する（RPCLite の受信バッファ上限 1MB）。</item>
    /// <item>認証前（Hello でトークン一致前）の呼び出しはすべて拒否する。</item>
    /// </list>
    /// </summary>
    public sealed class SdkSession : IPolyQaSdk, IDisposable
    {
        public const int ChunkSize = 500;
        private readonly ISdkHost _host;
        private readonly string _token;
        private readonly Func<SdkSession, bool> _claim;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly ConcurrentQueue<SdkEvent> _events = new ConcurrentQueue<SdkEvent>();
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, int.MaxValue);
        private readonly object _gate = new object();
        private IPolyQaDriverSink? _sink;
        private bool _authenticated;
        private Extractor? _extractor;
        private SubscriptionSpec? _spec;
        private Dictionary<int, NodeData> _sentNodes = new Dictionary<int, NodeData>();
        private Dictionary<string, string> _sentValues = new Dictionary<string, string>(StringComparer.Ordinal);
        private long _version;
        private bool _resetPending;
        private Task? _pump;
        private int _droppedEvents;

        public SdkSession(ISdkHost host, string token, Func<SdkSession, bool> claim)
        {
            _host = host;
            _token = token;
            _claim = claim;
        }

        public bool Authenticated => _authenticated;
        public bool ForwardLogs => _spec?.ForwardLogs == true;

        /// <summary>Driver への呼び出し口。接続直後に設定する。</summary>
        public void Attach(IPolyQaDriverSink sink) => _sink = sink;

        public UnaryResult<HelloReply> HelloAsync(HelloRequest request) => new UnaryResult<HelloReply>(HelloCoreAsync(request));

        private async Task<HelloReply> HelloCoreAsync(HelloRequest request)
        {
            // 認証前の応答にはアプリの情報（名前・package 名・Unity の版など）を含めない。
            if (request.ProtocolVersion != SdkProtocol.Version)
                return Rejected($"プロトコルの版が一致しません（SDK {SdkProtocol.Version} / Driver {request.ProtocolVersion}）。Driver と SDK を同じ版に揃えてください。");
            if (!FixedTimeEquals(request.Token, _token))
                return Rejected("トークンが一致しません。");
            // メインスレッドが止まっていても（OS のダイアログが前面・バックグラウンド）、認証だけは応答する。
            // 画面の情報は最後にメインスレッドで取った値を使う。
            var reply = _host.CachedDescribe() ?? await _host.RunOnMainThreadAsync(() => _host.Describe());
            reply.ProtocolVersion = SdkProtocol.Version;
            // 同じトークンを知る新しい Driver は、前の接続を引き継ぐ（前の Driver が異常終了して切断を検知できない場合に、二度とつながらなくなるのを防ぐ）。
            _claim(this);
            _authenticated = true;
            reply.Accepted = true;
            return reply;
        }

        private static HelloReply Rejected(string error) => new HelloReply { ProtocolVersion = SdkProtocol.Version, Error = error };

        private static bool FixedTimeEquals(string? a, string b)
        {
            var left = Encoding.UTF8.GetBytes(a ?? "");
            var right = Encoding.UTF8.GetBytes(b);
            var diff = left.Length ^ right.Length;
            for (var i = 0; i < Math.Min(left.Length, right.Length); i++) diff |= left[i] ^ right[i];
            return diff == 0 && left.Length > 0;
        }

        private void RequireAuthenticated()
        {
            if (!_authenticated) throw new InvalidOperationException("認証されていません。HelloAsync を先に呼んでください。");
        }

        public UnaryResult<Snapshot> SubscribeAsync(SubscriptionSpec spec) => new UnaryResult<Snapshot>(SubscribeCoreAsync(spec));

        private async Task<Snapshot> SubscribeCoreAsync(SubscriptionSpec spec)
        {
            RequireAuthenticated();
            var extractor = await _host.RunOnMainThreadAsync(() => new Extractor(_host, spec));
            var state = await _host.RunOnMainThreadAsync(() => _host.State());
            lock (_gate)
            {
                _spec = spec;
                _extractor = extractor;
                _resetPending = true;
            }
            if (_pump is null) _pump = Task.Run(PumpAsync);
            Wake();
            return new Snapshot { SubscriptionId = spec.SubscriptionId, Version = Interlocked.Read(ref _version), State = state, Diagnostics = extractor.Diagnostics.ToArray() };
        }

        public UnaryResult<Snapshot> ResyncAsync(int subscriptionId) => new UnaryResult<Snapshot>(ResyncCoreAsync(subscriptionId));

        private async Task<Snapshot> ResyncCoreAsync(int subscriptionId)
        {
            RequireAuthenticated();
            lock (_gate)
            {
                if (_spec is null || _spec.SubscriptionId != subscriptionId) throw new InvalidOperationException("購読が見つかりません。");
                _resetPending = true;
            }
            Wake();
            var state = await _host.RunOnMainThreadAsync(() => _host.State());
            return new Snapshot { SubscriptionId = subscriptionId, Version = Interlocked.Read(ref _version), State = state, Diagnostics = _extractor?.Diagnostics.ToArray() ?? Array.Empty<string>() };
        }

        public UnaryResult<SdkActionResult> ActAsync(SdkAction action) => new UnaryResult<SdkActionResult>(ActCoreAsync(action));

        private async Task<SdkActionResult> ActCoreAsync(SdkAction action)
        {
            RequireAuthenticated();
            var extractor = _extractor ?? throw new InvalidOperationException("購読していません。");
            var result = await _host.RunOnMainThreadAsync(() =>
            {
                if (!extractor.TryGetTarget(action.NodeId, out var target) || _host.IsDestroyed(target.Value))
                    return new SdkActionResult { Error = "要素が更新または削除されました。最新の観測を参照してください。" };
                string? error;
                switch (action.Kind)
                {
                    case SdkActionKinds.SetText:
                        if (target.Rule.SetText is null) return new SdkActionResult { Error = "この要素は文字を入力できません。" };
                        return target.Rule.SetText.TrySet(target.Value, action.Text ?? "", out error)
                            ? new SdkActionResult { Ok = true } : new SdkActionResult { Error = error };
                    case SdkActionKinds.Activate:
                        if (target.Rule.Activate is null) return new SdkActionResult { Error = "この要素は SDK から押せません。" };
                        MemberPath.ClearError();
                        target.Rule.Activate.Evaluate(target.Value);
                        return MemberPath.LastError is { } failure
                            ? new SdkActionResult { Error = failure } : new SdkActionResult { Ok = true };
                    default:
                        return new SdkActionResult { Error = "未対応の操作です: " + action.Kind };
                }
            });
            Wake();
            return result;
        }

        public UnaryResult<InspectReply> InspectAsync(InspectRequest request) => new UnaryResult<InspectReply>(InspectCoreAsync(request));

        private async Task<InspectReply> InspectCoreAsync(InspectRequest request)
        {
            RequireAuthenticated();
            var extractor = _extractor;
            return await _host.RunOnMainThreadAsync(() =>
            {
                var values = new List<NamedValue>();
                try
                {
                    if (request.NodeId != 0)
                    {
                        if (extractor is null || !extractor.TryGetTarget(request.NodeId, out var target))
                            return new InspectReply { Error = "要素が見つかりません。" };
                        foreach (var path in request.Paths)
                            values.Add(NamedValue.Of(path, ValueFormat.ToText(MemberPath.Parse(path)?.Evaluate(target.Value)) ?? "null"));
                    }
                    else if (request.Watch != null)
                    {
                        var spec = new SubscriptionSpec { Values = new[] { request.Watch } };
                        var probe = new Extractor(_host, spec, probe: true);
                        if (probe.Diagnostics.Count > 0) return new InspectReply { Error = string.Join("\n", probe.Diagnostics) };
                        foreach (var pair in probe.Extract().Values) values.Add(NamedValue.Of(pair.Key, pair.Value));
                    }
                }
                catch (FormatException e) { return new InspectReply { Error = e.Message }; }
                return new InspectReply { Values = values.ToArray() };
            });
        }

        public UnaryResult<TypeCatalog> DescribeTypesAsync(TypeQuery query) => new UnaryResult<TypeCatalog>(DescribeTypesCoreAsync(query));

        private async Task<TypeCatalog> DescribeTypesCoreAsync(TypeQuery query)
        {
            RequireAuthenticated();
            return await _host.RunOnMainThreadAsync(() =>
            {
                try { return new TypeCatalogBuilder(_host).Build(query ?? new TypeQuery()); }
                catch (Exception e) when (!(e is OutOfMemoryException)) { return new TypeCatalog { Error = "型を調べられません: " + e.Message }; }
            });
        }

        /// <summary>ログ等をキューに入れる（任意のスレッド）。上限を超えたら古いものから捨てる。</summary>
        public void Enqueue(SdkEvent sdkEvent)
        {
            if (!_authenticated) return;
            _events.Enqueue(sdkEvent);
            while (_events.Count > 500 && _events.TryDequeue(out _)) Interlocked.Increment(ref _droppedEvents);
            Wake();
        }

        /// <summary>シーンの切り替えなど、すぐに確認したいとき（任意のスレッド）。</summary>
        public void Wake()
        {
            if (_wake.CurrentCount < 2) _wake.Release();
        }

        private async Task PumpAsync()
        {
            var token = _lifetime.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var interval = Math.Max(30, _spec?.IntervalMs ?? 150);
                    await _wake.WaitAsync(interval, token).ConfigureAwait(false);
                    var sink = _sink;
                    if (sink is null) continue;
                    while (_events.TryDequeue(out var sdkEvent))
                        await sink.OnEventAsync(sdkEvent).ConfigureAwait(false);
                    if (Interlocked.Exchange(ref _droppedEvents, 0) is var dropped && dropped > 0)
                        await sink.OnEventAsync(new SdkEvent { Kind = SdkEventKinds.Log, Level = "Warning", Message = $"PolyQA SDK: ログを {dropped} 件省略しました。", Time = Now() }).ConfigureAwait(false);
                    await SendChangesAsync(sink).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // 送信失敗は切断とみなす。Driver は再接続して購読し直す。
                Dispose();
            }
        }

        private async Task SendChangesAsync(IPolyQaDriverSink sink)
        {
            Extractor? extractor;
            SubscriptionSpec? spec;
            bool reset;
            lock (_gate)
            {
                extractor = _extractor;
                spec = _spec;
                reset = _resetPending;
                _resetPending = false;
            }
            if (extractor is null || spec is null) return;
            var watch = Stopwatch.StartNew();
            var (result, state) = await _host.RunOnMainThreadAsync(() => (extractor.Extract(), _host.State())).ConfigureAwait(false);
            state.ExtractMs = (float)watch.Elapsed.TotalMilliseconds;
            state.Truncated = result.Truncated;
            if (!ReferenceEquals(extractor, _extractor)) return; // 購読し直された

            var upserts = new List<NodeData>();
            var removed = new List<int>();
            var values = new List<NamedValue>();
            var removedValues = new List<string>();
            if (reset)
            {
                upserts.AddRange(result.Nodes.Values.OrderBy(n => n.Order));
                values.AddRange(result.Values.Select(v => NamedValue.Of(v.Key, v.Value)));
            }
            else
            {
                NodeDiff.Compute(_sentNodes, result.Nodes, upserts, removed);
                NodeDiff.ComputeValues(_sentValues, result.Values, values, removedValues);
                if (upserts.Count == 0 && removed.Count == 0 && values.Count == 0 && removedValues.Count == 0 && !StateChanged(state)) return;
            }

            var baseVersion = Interlocked.Read(ref _version);
            var version = baseVersion + 1;
            var chunks = Math.Max(1, (upserts.Count + ChunkSize - 1) / ChunkSize);
            for (var i = 0; i < chunks; i++)
            {
                var last = i == chunks - 1;
                await sink.OnDeltaAsync(new Delta
                {
                    SubscriptionId = spec.SubscriptionId,
                    BaseVersion = i == 0 ? baseVersion : version,
                    Version = version,
                    Reset = reset && i == 0,
                    More = !last,
                    Upserts = upserts.Skip(i * ChunkSize).Take(ChunkSize).ToArray(),
                    Removed = i == 0 ? removed.ToArray() : Array.Empty<int>(),
                    Values = last ? values.ToArray() : Array.Empty<NamedValue>(),
                    RemovedValues = last ? removedValues.ToArray() : Array.Empty<string>(),
                    State = state,
                }).ConfigureAwait(false);
            }
            Interlocked.Exchange(ref _version, version);
            _sentNodes = result.Nodes;
            _sentValues = result.Values;
            _lastState = state;
        }

        private FrameState? _lastState;

        private bool StateChanged(FrameState state)
        {
            var last = _lastState;
            return last is null || last.ActiveScene != state.ActiveScene || last.SceneGeneration != state.SceneGeneration
                || !last.Busy.SequenceEqual(state.Busy) || last.Truncated != state.Truncated || last.Focused != state.Focused
                || last.Screen.Width != state.Screen.Width || last.Screen.Height != state.Screen.Height;
        }

        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static string NewToken()
        {
            var bytes = new byte[24];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public event Action<SdkSession>? Closed;

        public void Dispose()
        {
            if (_lifetime.IsCancellationRequested) return;
            _lifetime.Cancel();
            Closed?.Invoke(this);
        }
    }
}
