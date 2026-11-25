#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PolyQA.Unity.Protocol;
using PolyQA.Unity.Sdk.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PolyQA.Unity.Sdk
{
    /// <summary>
    /// UnityEngine のコア API（GameObject / Transform / Camera / Screen / SceneManager）だけを使う窓口。
    /// uGUI・UI Toolkit・TextMeshPro の型は参照しない。Canvas の描画モードなど UI 固有の情報もリフレクションで読む。
    /// </summary>
    internal sealed class UnitySdkHost : ISdkHost
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private readonly HashSet<string> _busy = new HashSet<string>(StringComparer.Ordinal);
        private int _sceneGeneration;
        private int _mainThread;
        private float _fps;
        private bool _focused = true;
        private int _frame = -1;
        private readonly Dictionary<int, int> _sceneOrder = new Dictionary<int, int>();

        public void BindMainThread()
        {
            _mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Describe();
        }

        public Task<T> RunOnMainThreadAsync<T>(Func<T> action)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThread) return Task.FromResult(action());
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(() =>
            {
                try { completion.SetResult(action()); }
                catch (Exception e) { completion.SetException(e); }
            });
            return completion.Task;
        }

        /// <summary>メインスレッドで毎フレーム呼ぶ。</summary>
        public void Pump(float unscaledDeltaTime)
        {
            if (unscaledDeltaTime > 0) _fps = _fps <= 0 ? 1f / unscaledDeltaTime : _fps * 0.9f + 0.1f / unscaledDeltaTime;
            var deadline = Time.realtimeSinceStartup + 0.008f;
            while (_queue.TryDequeue(out var action))
            {
                action();
                if (Time.realtimeSinceStartup > deadline) break;
            }
        }

        public void SceneChanged() => _sceneGeneration++;
        public void SetFocused(bool focused) => _focused = focused;

        public void SetBusy(string name, bool busy)
        {
            lock (_busy)
            {
                if (busy) _busy.Add(name);
                else _busy.Remove(name);
            }
        }

        private HelloReply? _described;

        public HelloReply? CachedDescribe()
        {
            var cached = Volatile.Read(ref _described);
            if (cached is null) return null;
            // 呼び出し側が書き換えるため写しを返す。
            return new HelloReply
            {
                UnityVersion = cached.UnityVersion, Platform = cached.Platform, ProductName = cached.ProductName, Identifier = cached.Identifier,
                SdkVersion = cached.SdkVersion, Screen = cached.Screen, ProcessId = cached.ProcessId, IsEditor = cached.IsEditor,
            };
        }

        public HelloReply Describe()
        {
            var reply = DescribeCore();
            Volatile.Write(ref _described, reply);
            return CachedDescribe()!;
        }

        private HelloReply DescribeCore() => new HelloReply
        {
            UnityVersion = Application.unityVersion,
            Platform = Application.platform.ToString(),
            ProductName = Application.productName,
            Identifier = Application.identifier,
            SdkVersion = PolyQASdk.Version,
            Screen = ScreenInfo(),
            ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
            IsEditor = Application.isEditor,
        };

        private static ScreenInfo ScreenInfo()
        {
            var safe = Screen.safeArea;
            return new ScreenInfo
            {
                Width = Screen.width,
                Height = Screen.height,
                Dpi = Screen.dpi,
                SafeX = safe.x,
                SafeY = Screen.height - safe.yMax,
                SafeWidth = safe.width,
                SafeHeight = safe.height,
                Orientation = Screen.orientation.ToString(),
            };
        }

        public FrameState State()
        {
            var scenes = new string[SceneManager.sceneCount];
            for (var i = 0; i < scenes.Length; i++) scenes[i] = SceneManager.GetSceneAt(i).name;
            string[] busy;
            lock (_busy)
            {
                busy = new string[_busy.Count];
                _busy.CopyTo(busy);
            }
            var loading = false;
            for (var i = 0; i < SceneManager.sceneCount; i++)
                if (!SceneManager.GetSceneAt(i).isLoaded) loading = true;
            if (loading) Array.Resize(ref busy, busy.Length + 1);
            if (loading) busy[busy.Length - 1] = "sceneLoading";
            return new FrameState
            {
                ActiveScene = SceneManager.GetActiveScene().name,
                Scenes = scenes,
                SceneGeneration = _sceneGeneration,
                Busy = busy,
                Screen = ScreenInfo(),
                Fps = _fps,
                Focused = _focused,
            };
        }

        public IEnumerable<object> FindObjects(Type type, bool includeInactive)
        {
            if (!typeof(Object).IsAssignableFrom(type)) return Array.Empty<object>();
            return Object.FindObjectsByType(type, includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        }

        public IEnumerable<object> SceneScripts()
            => Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        public bool IsActive(object value) => value switch
        {
            Behaviour behaviour => behaviour != null && behaviour.isActiveAndEnabled,
            Component component => component != null && component.gameObject.activeInHierarchy,
            GameObject gameObject => gameObject != null && gameObject.activeInHierarchy,
            _ => true,
        };

        public bool IsDestroyed(object value) => value is Object unityObject && unityObject == null;

        public object? HierarchyOwner(object value) => value switch
        {
            Component component when component != null => component.gameObject,
            GameObject gameObject when gameObject != null => gameObject,
            _ => null,
        };

        public IEnumerable<object> HierarchyAncestors(object owner)
        {
            if (!(owner is GameObject gameObject) || gameObject == null) yield break;
            for (var parent = gameObject.transform.parent; parent != null; parent = parent.parent) yield return parent.gameObject;
        }

        public int[] SiblingPath(object owner)
        {
            if (!(owner is GameObject gameObject) || gameObject == null) return new[] { int.MaxValue };
            RefreshSceneOrder();
            var depth = 0;
            for (var t = gameObject.transform; t != null; t = t.parent) depth++;
            var key = new int[depth + 1];
            key[0] = _sceneOrder.TryGetValue(gameObject.scene.handle, out var order) ? order : int.MaxValue - 2;
            var index = depth;
            for (var t = gameObject.transform; t != null; t = t.parent) key[index--] = t.GetSiblingIndex();
            return key;
        }

        private void RefreshSceneOrder()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _sceneOrder.Clear();
            for (var i = 0; i < SceneManager.sceneCount; i++) _sceneOrder[SceneManager.GetSceneAt(i).handle] = i;
        }

        public string HierarchyName(object owner)
        {
            if (!(owner is GameObject gameObject) || gameObject == null) return owner.GetType().Name;
            var names = new List<string>();
            for (var t = gameObject.transform; t != null; t = t.parent) names.Add(t.name.Replace('/', '_'));
            names.Reverse();
            return string.Join("/", names);
        }

        public bool TryScreenRect(object value, string space, object? panelRoot, out ScreenRect rect)
        {
            rect = default;
            try
            {
                switch (space)
                {
                    case BoundsSpaces.RectTransform: return RectTransformRect(value, out rect);
                    case BoundsSpaces.World: return WorldRect(value, out rect);
                    case BoundsSpaces.Panel: return PanelRect(value, panelRoot, out rect);
                    case BoundsSpaces.ScreenBottomLeft:
                        if (!ReadRect(value, out rect)) return false;
                        rect.Y = Screen.height - rect.Y - rect.Height;
                        return true;
                    default: return ReadRect(value, out rect);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                return false;
            }
        }

        private static readonly Vector3[] Corners = new Vector3[4];

        /// <summary>RectTransform を画面へ投影する。Canvas の描画モード（Overlay / Camera / World）はリフレクションで読む。</summary>
        private static bool RectTransformRect(object value, out ScreenRect rect)
        {
            rect = default;
            var transform = value switch
            {
                RectTransform r => r,
                Component c when c != null => c.transform as RectTransform,
                GameObject g when g != null => g.transform as RectTransform,
                _ => null,
            };
            if (transform == null)
            {
                return value is Component component && component != null ? WorldRect(component, out rect) : false;
            }
            transform.GetWorldCorners(Corners);
            var camera = CanvasCamera(transform);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (var i = 0; i < 4; i++)
            {
                var point = camera == null ? Corners[i] : camera.WorldToScreenPoint(Corners[i]);
                if (camera != null && point.z < 0) return false;
                minX = Math.Min(minX, point.x);
                minY = Math.Min(minY, point.y);
                maxX = Math.Max(maxX, point.x);
                maxY = Math.Max(maxY, point.y);
            }
            rect = new ScreenRect(minX, Screen.height - maxY, maxX - minX, maxY - minY);
            return true;
        }

        private static Type? _canvasType;
        private static PropertyInfo? _renderMode;
        private static PropertyInfo? _worldCamera;
        private static PropertyInfo? _rootCanvas;

        /// <summary>Overlay なら null（ワールド座標＝画面ピクセル）。Camera / World なら投影に使うカメラ。</summary>
        private static Camera? CanvasCamera(Transform transform)
        {
            _canvasType ??= TypeIndex.Find("UnityEngine.Canvas");
            if (_canvasType is null) return Camera.main;
            var canvas = transform.GetComponentInParent(_canvasType, true);
            if (canvas == null) return Camera.main;
            _rootCanvas ??= _canvasType.GetProperty("rootCanvas");
            if (_rootCanvas?.GetValue(canvas) is Component root && root != null) canvas = root;
            _renderMode ??= _canvasType.GetProperty("renderMode");
            _worldCamera ??= _canvasType.GetProperty("worldCamera");
            var mode = Convert.ToInt32(_renderMode?.GetValue(canvas) ?? 0);
            if (mode == 0) return null; // ScreenSpaceOverlay
            return _worldCamera?.GetValue(canvas) as Camera ?? (mode == 1 ? null : Camera.main);
        }

        private static readonly Vector3[] BoxCorners = new Vector3[8];

        private static bool WorldRect(object value, out ScreenRect rect)
        {
            rect = default;
            var camera = Camera.main;
            if (camera == null) return false;
            Bounds bounds;
            switch (value)
            {
                case Bounds b: bounds = b; break;
                case Renderer renderer when renderer != null: bounds = renderer.bounds; break;
                case Collider collider when collider != null: bounds = collider.bounds; break;
                case Collider2D collider2D when collider2D != null: bounds = collider2D.bounds; break;
                case GameObject gameObject when gameObject != null:
                    if (gameObject.GetComponentInChildren<Renderer>() is { } child && child != null) bounds = child.bounds;
                    else bounds = new Bounds(gameObject.transform.position, Vector3.zero);
                    break;
                case Component component when component != null:
                    if (component.GetComponentInChildren<Renderer>() is { } r && r != null) bounds = r.bounds;
                    else bounds = new Bounds(component.transform.position, Vector3.zero);
                    break;
                case Vector3 position: bounds = new Bounds(position, Vector3.zero); break;
                default: return false;
            }
            var min = bounds.min;
            var max = bounds.max;
            var n = 0;
            for (var x = 0; x < 2; x++)
                for (var y = 0; y < 2; y++)
                    for (var z = 0; z < 2; z++)
                        BoxCorners[n++] = new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var corner in BoxCorners)
            {
                var point = camera.WorldToScreenPoint(corner);
                if (point.z < 0) return false;
                minX = Math.Min(minX, point.x);
                minY = Math.Min(minY, point.y);
                maxX = Math.Max(maxX, point.x);
                maxY = Math.Max(maxY, point.y);
            }
            rect = new ScreenRect(minX, Screen.height - maxY, maxX - minX, maxY - minY);
            return true;
        }

        /// <summary>UI Toolkit の worldBound（パネル座標・左上原点）を、パネル全体（根の矩形）が画面全体に対応するとして拡大する。</summary>
        private static bool PanelRect(object value, object? panelRoot, out ScreenRect rect)
        {
            rect = default;
            if (!ReadRect(value, out var panel)) return false;
            if (panelRoot != null && ReadRect(panelRoot, out var root) && root.Width > 0 && root.Height > 0)
            {
                var sx = Screen.width / root.Width;
                var sy = Screen.height / root.Height;
                rect = new ScreenRect((panel.X - root.X) * sx, (panel.Y - root.Y) * sy, panel.Width * sx, panel.Height * sy);
                return true;
            }
            rect = panel;
            return true;
        }

        /// <summary>Rect・Rect を持つ値（x / y / width / height の数値メンバー）を読む。</summary>
        private static bool ReadRect(object value, out ScreenRect rect)
        {
            rect = default;
            if (value is Rect r)
            {
                rect = new ScreenRect(r.x, r.y, r.width, r.height);
                return !float.IsNaN(r.width) && !float.IsNaN(r.height);
            }
            var x = Number(value, "x");
            var y = Number(value, "y");
            var width = Number(value, "width");
            var height = Number(value, "height");
            if (x is null || y is null || width is null || height is null) return false;
            rect = new ScreenRect(x.Value, y.Value, width.Value, height.Value);
            return !float.IsNaN(rect.Width) && !float.IsNaN(rect.Height);
        }

        private static float? Number(object value, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
            var type = value.GetType();
            object? raw = type.GetProperty(name, flags)?.GetValue(value) ?? type.GetField(name, flags)?.GetValue(value);
            return raw is IConvertible c ? Convert.ToSingle(c, System.Globalization.CultureInfo.InvariantCulture) : (float?)null;
        }
    }
}
