// UnityEngine に依存しない。Unity 側の実装は Runtime/Unity/UnitySdkHost.cs、テストでは偽の実装を使う。
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PolyQA.Unity.Protocol;

namespace PolyQA.Unity.Sdk.Core
{
    /// <summary>
    /// エンジン（Unity）への最小限の窓口。UI フレームワーク（uGUI / UI Toolkit / TextMeshPro / 独自UI）の知識は持たない。
    /// オブジェクトの探索・階層・座標変換だけを提供し、何を読むかは Driver の <see cref="SubscriptionSpec"/> が決める。
    /// <see cref="RunOnMainThreadAsync{T}"/> 以外はメインスレッドから呼ぶ。
    /// </summary>
    public interface ISdkHost
    {
        Task<T> RunOnMainThreadAsync<T>(Func<T> action);

        HelloReply Describe();

        /// <summary>最後にメインスレッドで取得した <see cref="Describe"/> の写し（任意のスレッドから呼べる）。まだ無ければ null。</summary>
        HelloReply? CachedDescribe();

        FrameState State();

        /// <summary>シーン内の指定型のオブジェクト。エンジンのオブジェクト以外の型なら空。</summary>
        IEnumerable<object> FindObjects(Type type, bool includeInactive);

        /// <summary>シーン内の有効なゲームのスクリプト（MonoBehaviour）すべて。型の調査（監視値の候補探し）に使う。</summary>
        IEnumerable<object> SceneScripts();

        /// <summary>有効で表示対象か（GameObject.activeInHierarchy / Behaviour.isActiveAndEnabled）。エンジン以外のオブジェクトは true。</summary>
        bool IsActive(object value);

        /// <summary>破棄済みのエンジンオブジェクトか。</summary>
        bool IsDestroyed(object value);

        /// <summary>階層の持ち主（コンポーネントなら GameObject）。階層を持たなければ null。</summary>
        object? HierarchyOwner(object value);

        /// <summary>持ち主の祖先（近い順）。</summary>
        IEnumerable<object> HierarchyAncestors(object owner);

        /// <summary>文書順の比較キー（シーンの読み込み順・兄弟の位置）。</summary>
        int[] SiblingPath(object owner);

        /// <summary>階層の名前の連結（例: Canvas/Panel/Button）。</summary>
        string HierarchyName(object owner);

        /// <summary>値を Screen ピクセル（左上原点）の矩形に変換する。<paramref name="panelRoot"/> は panel 座標の根の矩形。</summary>
        bool TryScreenRect(object value, string space, object? panelRoot, out ScreenRect rect);
    }

    public struct ScreenRect
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;

        public ScreenRect(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }
}
