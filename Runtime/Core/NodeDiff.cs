// UnityEngine に依存しない。
#nullable enable
using System;
using System.Collections.Generic;
using PolyQA.Unity.Protocol;

namespace PolyQA.Unity.Sdk.Core
{
    /// <summary>Driver が受信確認した状態と最新の抽出結果の差分。</summary>
    public static class NodeDiff
    {
        /// <summary>矩形の変化をこれ未満なら無視する（Screen ピクセル）。アニメーションの微小な揺れで差分を出し続けない。</summary>
        public const float BoundsTolerance = 1.5f;

        public static bool SameNode(NodeData a, NodeData b)
            => a.Parent == b.Parent && a.Order == b.Order && a.Role == b.Role && a.NativeType == b.NativeType
               && a.Name == b.Name && a.Text == b.Text && a.TestId == b.TestId && a.Path == b.Path
               && a.Enabled == b.Enabled && a.Visible == b.Visible && a.Checked == b.Checked
               && a.Editable == b.Editable && a.Activatable == b.Activatable && a.Clickable == b.Clickable
               && a.Scrollable == b.Scrollable && a.Password == b.Password && a.Placeholder == b.Placeholder && a.Source == b.Source
               && Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Width, b.Width) && Near(a.Height, b.Height)
               && SameAttributes(a.Attributes, b.Attributes);

        private static bool Near(float a, float b) => Math.Abs(a - b) < BoundsTolerance;

        private static bool SameAttributes(NamedValue[] a, NamedValue[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i].Name != b[i].Name || a[i].Value != b[i].Value) return false;
            return true;
        }

        public static void Compute(IReadOnlyDictionary<int, NodeData> sent, IReadOnlyDictionary<int, NodeData> current,
            List<NodeData> upserts, List<int> removed)
        {
            foreach (var pair in current)
                if (!sent.TryGetValue(pair.Key, out var old) || !SameNode(old, pair.Value)) upserts.Add(pair.Value);
            foreach (var key in sent.Keys)
                if (!current.ContainsKey(key)) removed.Add(key);
        }

        public static void ComputeValues(IReadOnlyDictionary<string, string> sent, IReadOnlyDictionary<string, string> current,
            List<NamedValue> changed, List<string> removed)
        {
            foreach (var pair in current)
                if (!sent.TryGetValue(pair.Key, out var old) || old != pair.Value) changed.Add(NamedValue.Of(pair.Key, pair.Value));
            foreach (var key in sent.Keys)
                if (!current.ContainsKey(key)) removed.Add(key);
        }
    }
}
