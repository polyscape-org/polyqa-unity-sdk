#nullable enable
using System;
using System.IO;
using PolyQA.Unity.Protocol;
using PolyQA.Unity.Sdk.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PolyQA.Unity.Sdk
{
    /// <summary>
    /// PolyQA Driver から UI・内部状態を観測できるようにする。シーンへの配置は不要で、起動時に自動で待ち受ける。
    /// <para>
    /// このアセンブリは Editor・Development Build・定義シンボル <c>POLYQA_SDK</c> のときだけビルドに含まれる（asmdef の defineConstraints）。
    /// ゲーム側から <see cref="SetBusy"/> 等を呼ぶ場合は <c>#if POLYQA_SDK || DEVELOPMENT_BUILD || UNITY_EDITOR</c> で囲む。
    /// </para>
    /// <para>
    /// 待ち受けは 127.0.0.1 のみ。接続情報（ポート・トークン）は <see cref="DiscoveryPath"/>（Android はアプリの内部領域）に書き、
    /// Driver が USB（adb run-as / go-ios）または同じ PC のファイルとして読む。トークンは起動ごとに変わる。
    /// </para>
    /// </summary>
    public static class PolyQASdk
    {
        public const string Version = "1.26.1126.0";
        private static UnitySdkHost? _host;
        private static SdkListener? _listener;
        private static GameObject? _runner;

        /// <summary>待ち受け中のポート。未起動なら 0。</summary>
        public static int Port => _listener?.Port ?? 0;
        public static bool IsRunning => _listener != null;
        /// <summary>Driver が接続して認証済みか。</summary>
        public static bool IsConnected => _listener?.HasDriver == true;
        /// <summary>
        /// 接続情報の書き出し先。Android はアプリ専用の内部領域（Context.getFilesDir、他アプリ・adb shell から読めず run-as でだけ読める）。
        /// persistentDataPath は外部ストレージ（/storage/emulated/0/Android/data/…）になり adb shell から誰でも読めるため、トークンを置かない。
        /// </summary>
        public static string DiscoveryPath => Path.Combine(DiscoveryDirectory(), SdkProtocol.DiscoveryFileName);

        private static string? _discoveryDirectory;

        private static string DiscoveryDirectory()
        {
            if (_discoveryDirectory != null) return _discoveryDirectory;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var files = activity.Call<AndroidJavaObject>("getFilesDir");
                return _discoveryDirectory = files.Call<string>("getAbsolutePath");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PolyQA] アプリの内部領域を取得できません: " + e.Message);
            }
#endif
            return _discoveryDirectory = Application.persistentDataPath;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        [UnityEngine.Scripting.Preserve]
        private static void AutoStart()
        {
            if (Environment.GetCommandLineArgs() is { } args && Array.IndexOf(args, "-polyqa-sdk-disable") >= 0) return;
            try { Start(); }
            catch (Exception e)
            {
                // ゲームの起動を妨げない。
                Debug.LogError("[PolyQA] SDK を開始できません: " + e);
            }
        }

        /// <summary>待ち受けを開始する。既に動いていれば何もしない。</summary>
        public static void Start(int preferredPort = SdkProtocol.DefaultPort)
        {
            if (_listener != null) return;
            _host = new UnitySdkHost();
            _host.BindMainThread();
            var listener = new SdkListener(_host, SdkSession.NewToken());
            int port;
            try { port = listener.Start(preferredPort); }
            catch (Exception e) when (e is System.Net.Sockets.SocketException || e is NotSupportedException)
            {
                Debug.LogWarning("[PolyQA] SDK の待ち受けを開始できません: " + e.Message);
                listener.Dispose();
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PolyQA] SDK の待ち受けを開始できません: " + e.Message);
                listener.Dispose();
                return;
            }
            _listener = listener;
            WriteDiscovery(port, listener.Token);
            _runner = new GameObject("[PolyQA SDK]") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_runner);
            _runner.AddComponent<PolyQASdkRunner>();
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            Application.logMessageReceivedThreaded += OnLog;
            Application.quitting += Stop;
            Debug.Log($"[PolyQA] SDK {Version} が 127.0.0.1:{port} で待ち受けています。");
        }

        public static void Stop()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            Application.logMessageReceivedThreaded -= OnLog;
            Application.quitting -= Stop;
            _listener?.Dispose();
            _listener = null;
            try { File.Delete(DiscoveryPath); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            if (_runner != null) UnityEngine.Object.Destroy(_runner);
            _runner = null;
        }

        /// <summary>処理中（通信・読み込み・演出など）を報告する。処理中の間、Driver は画面を操作可能（Ready）とみなさない。</summary>
        public static void SetBusy(string name, bool busy)
        {
            _host?.SetBusy(name, busy);
            _listener?.WakeAll();
        }

        /// <summary>任意のイベントを Driver の記録へ送る（ゲーム内の出来事の印）。</summary>
        public static void Report(string message, string? detail = null)
            => _listener?.Broadcast(new SdkEvent { Kind = SdkEventKinds.Custom, Message = message, Detail = detail, Time = SdkSession.Now() }, false);

        internal static void Tick(float unscaledDeltaTime) => _host?.Pump(unscaledDeltaTime);
        internal static void Focus(bool focused) => _host?.SetFocused(focused);

        private static void WriteDiscovery(int port, string token)
        {
            try
            {
                var json = "{\"protocol\":" + SdkProtocol.Version + ",\"port\":" + port + ",\"token\":\"" + token + "\",\"pid\":"
                    + System.Diagnostics.Process.GetCurrentProcess().Id + ",\"identifier\":\"" + Escape(Application.identifier)
                    + "\",\"product\":\"" + Escape(Application.productName) + "\",\"editor\":" + (Application.isEditor ? "true" : "false")
                    + ",\"startedAt\":" + SdkSession.Now() + "}";
                Directory.CreateDirectory(DiscoveryDirectory());
                var temp = DiscoveryPath + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(DiscoveryPath)) File.Delete(DiscoveryPath);
                File.Move(temp, DiscoveryPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning("[PolyQA] 接続情報を書き出せません: " + e.Message);
            }
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => SceneEvent(SdkEventKinds.SceneLoaded, scene.name);
        private static void OnSceneUnloaded(Scene scene) => SceneEvent(SdkEventKinds.SceneUnloaded, scene.name);
        private static void OnActiveSceneChanged(Scene previous, Scene next) => SceneEvent(SdkEventKinds.SceneLoaded, next.name);

        private static void SceneEvent(string kind, string name)
        {
            _host?.SceneChanged();
            _listener?.Broadcast(new SdkEvent { Kind = kind, Message = name, Time = SdkSession.Now() }, false);
            _listener?.WakeAll();
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Log || condition.StartsWith("[PolyQA]", StringComparison.Ordinal)) return;
            _listener?.Broadcast(new SdkEvent
            {
                Kind = SdkEventKinds.Log,
                Level = type.ToString(),
                Message = condition.Length > 4000 ? condition.Substring(0, 4000) : condition,
                Detail = string.IsNullOrEmpty(stackTrace) ? null : stackTrace.Length > 8000 ? stackTrace.Substring(0, 8000) : stackTrace,
                Time = SdkSession.Now(),
            }, true);
        }
    }

    /// <summary>メインスレッドの処理（抽出・操作）をフレームごとに実行する。</summary>
    [AddComponentMenu("")]
    internal sealed class PolyQASdkRunner : MonoBehaviour
    {
        private void Update() => PolyQASdk.Tick(Time.unscaledDeltaTime);
        private void OnApplicationFocus(bool focus) => PolyQASdk.Focus(focus);
    }
}
