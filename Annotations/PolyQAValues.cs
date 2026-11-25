// ゲームのコードから参照する公開 API。リリースビルドにも含まれる（参照するコードがビルドできるように）が、
// Watch / Unwatch の呼び出しは [Conditional] でリリースビルドの呼び出し側から消える。属性は付けたままでも何もしない。
// 基本はゲームのコードを変えずに、タイトルの設定（型名とメンバー名）で値を読む。これは型名で特定しにくい値のための最終手段。
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PolyQA.Unity
{
    /// <summary>
    /// PolyQA に値を公開する印。フィールド・プロパティ（静的も可）に付ける。
    /// インスタンスメンバーは、シーン内のその型のインスタンスごとに「キー.GameObject 名」で記録される。
    /// <code>
    /// [PolyQAValue("hp")] int currentHealth;
    /// [PolyQAValue("wave")] static int Wave { get; set; }
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class PolyQAValueAttribute : Attribute
    {
        /// <param name="key">記録のキー（英数字・ドット・ハイフン・アンダースコア）。省略すると「型名.メンバー名」。</param>
        public PolyQAValueAttribute(string? key = null) => Key = key;

        public string? Key { get; }

        /// <summary>インスタンスを区別する名前のメンバーパス。既定は gameObject.name。</summary>
        public string? KeyPath { get; set; }

        /// <summary>何の値か（候補探し・設定画面の説明）。</summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// 型やメンバーで特定しにくい値（計算結果・別のシステムが持つ値など）を、取得関数で公開する。
    /// 取得関数は SDK がメインスレッドで、変化の確認ごと（既定 150ms）に呼ぶ。重い処理を入れない。
    /// </summary>
    public static class PolyQAValues
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static int _revision;

        /// <summary>値を公開する。同じキーで呼ぶと置き換える。リリースビルドでは呼び出しごと消える。</summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("POLYQA_SDK")]
        public static void Watch(string key, Func<object?> getter, string? description = null)
        {
            if (string.IsNullOrEmpty(key) || getter is null) return;
            lock (Gate)
            {
                Entries[key] = new Entry(key, getter, description);
                _revision++;
            }
        }

        /// <summary>公開をやめる（シーンの破棄時など）。リリースビルドでは呼び出しごと消える。</summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("POLYQA_SDK")]
        public static void Unwatch(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate)
            {
                if (Entries.Remove(key)) _revision++;
            }
        }

        /// <summary>登録中の値（SDK が読む）。</summary>
        public static Entry[] Snapshot()
        {
            lock (Gate)
            {
                var result = new Entry[Entries.Count];
                Entries.Values.CopyTo(result, 0);
                return result;
            }
        }

        /// <summary>登録・解除のたびに増える。</summary>
        public static int Revision
        {
            get { lock (Gate) return _revision; }
        }

        /// <summary>テスト用。すべて解除する。</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Entries.Clear();
                _revision++;
            }
        }

        public sealed class Entry
        {
            public Entry(string key, Func<object?> getter, string? description)
            {
                Key = key;
                Getter = getter;
                Description = description;
            }

            public string Key { get; }
            public Func<object?> Getter { get; }
            public string? Description { get; }
        }
    }
}
