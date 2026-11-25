// UnityEngine に依存しない。
#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Polyscape.RPCLite;
using PolyQA.Unity.Protocol;

namespace PolyQA.Unity.Sdk.Core
{
    /// <summary>
    /// 127.0.0.1 だけで待ち受け、接続ごとに <see cref="SdkSession"/> を作る。
    /// RPCLite の RpcServerService はサービス実装を全接続で共有するため使わず、RpcChannel を直接使って接続ごとに実装を分ける
    /// （未認証の接続が認証済みの購読を操作できないようにする）。
    /// USB の端末は adb forward / go-ios forward で、PC・Editor は同じ PC の loopback で Driver から接続される。
    /// </summary>
    public sealed class SdkListener : IDisposable
    {
        private readonly ISdkHost _host;
        private readonly object _gate = new object();
        private readonly List<SdkSession> _sessions = new List<SdkSession>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private Socket? _listener;
        private SdkSession? _owner;

        public SdkListener(ISdkHost host, string token)
        {
            // 生成されたディスパッチャ・プロキシはインターフェースの静的コンストラクタで登録される。
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(IPolyQaSdk).TypeHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(IPolyQaDriverSink).TypeHandle);
            _host = host;
            Token = token;
        }

        public string Token { get; }
        public int Port { get; private set; }
        public int MaxConnections { get; set; } = 4;

        /// <summary>認証済みの接続があるか。</summary>
        public bool HasDriver { get { lock (_gate) return _owner != null; } }

        /// <summary>preferredPort が使用中なら空きポートで待ち受ける。待ち受けたポートを返す。</summary>
        public int Start(int preferredPort)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            // Windows では他プロセスに同じポートを奪われないようにする（Android / iOS の Mono・IL2CPP では未対応で例外になる）。
            try { socket.ExclusiveAddressUse = true; }
            catch (Exception e) when (e is SocketException || e is NotSupportedException || e is PlatformNotSupportedException) { }
            try { socket.Bind(new IPEndPoint(IPAddress.Loopback, preferredPort)); }
            catch (SocketException)
            {
                // 使用中なら新しいソケットで空きポートを使う（失敗した Bind の後のソケットは再利用しない）。
                socket.Dispose();
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }
            socket.Listen(8);
            _listener = socket;
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            _ = Task.Run(AcceptLoopAsync);
            return Port;
        }

        private async Task AcceptLoopAsync()
        {
            var listener = _listener!;
            while (!_lifetime.IsCancellationRequested)
            {
                Socket client;
                try { client = await listener.AcceptAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) when (_lifetime.IsCancellationRequested) { return; }
                catch (SocketException) { await Task.Delay(100).ConfigureAwait(false); continue; }
                lock (_gate)
                {
                    if (_sessions.Count >= MaxConnections)
                    {
                        client.Dispose();
                        continue;
                    }
                }
                client.NoDelay = true;
                Accept(client);
            }
        }

        private void Accept(Socket client)
        {
            var session = new SdkSession(_host, Token, Claim);
            var channel = new RpcChannel(client, (serviceId, methodId, payload, writer) => Dispatch(session, serviceId, methodId, payload, writer));
            session.Attach(RpcProxyFactory.CreateProxy<IPolyQaDriverSink>(channel));
            lock (_gate) _sessions.Add(session);
            session.Closed += closed => { var pending = channel.DisposeAsync(); };
            channel.Disconnected += () => Release(session);
            channel.StartReceiving(_lifetime.Token);
            // 一定時間内に認証しない接続は切る（つながったまま枠を埋めない）。
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => { if (!session.Authenticated) session.Dispose(); }, TaskScheduler.Default);
        }

        private static ValueTask Dispatch(SdkSession session, int serviceId, int methodId, ReadOnlyMemory<byte> payload, IBufferWriter<byte> writer)
        {
            if (serviceId != IPolyQaSdk.ServiceId) throw new InvalidOperationException("Service not found: " + serviceId);
            return RpcDispatcher.InvokeAsync(session, serviceId, methodId, payload, writer);
        }

        /// <summary>認証した接続を Driver とする。前の Driver の接続は閉じる（異常終了で切断を検知できないことがある）。</summary>
        private bool Claim(SdkSession session)
        {
            SdkSession? previous;
            lock (_gate)
            {
                previous = _owner;
                _owner = session;
            }
            if (previous != null && !ReferenceEquals(previous, session)) previous.Dispose();
            return true;
        }

        private void Release(SdkSession session)
        {
            lock (_gate)
            {
                _sessions.Remove(session);
                if (ReferenceEquals(_owner, session)) _owner = null;
            }
            session.Dispose();
        }

        /// <summary>認証済みの接続へイベントを送る。</summary>
        public void Broadcast(SdkEvent sdkEvent, bool isLog)
        {
            SdkSession? owner;
            lock (_gate) owner = _owner;
            if (owner is null || isLog && !owner.ForwardLogs) return;
            owner.Enqueue(sdkEvent);
        }

        public void WakeAll()
        {
            lock (_gate)
                foreach (var session in _sessions) session.Wake();
        }

        public void Dispose()
        {
            if (_lifetime.IsCancellationRequested) return;
            _lifetime.Cancel();
            _listener?.Dispose();
            SdkSession[] sessions;
            lock (_gate) sessions = _sessions.ToArray();
            foreach (var session in sessions) session.Dispose();
        }
    }
}
