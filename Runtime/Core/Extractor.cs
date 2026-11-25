// UnityEngine に依存しない。何を読むかはすべて SubscriptionSpec（Driver が送る）で決まる。
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using PolyQA.Unity.Protocol;

namespace PolyQA.Unity.Sdk.Core
{
    /// <summary>
    /// 購読内容に従ってオブジェクトを集め、ノードと監視値を作る。メインスレッドで呼ぶ。
    /// <list type="bullet">
    /// <item>components: シーン内の指定型のコンポーネント。同じ持ち主（GameObject）に複数一致すれば、優先度の高いルール 1 つにまとめる。</item>
    /// <item>objects / static: 根の値から ChildrenPath を辿る（UI Toolkit の VisualElement 木や独自UIの木）。</item>
    /// </list>
    /// 親子関係は「最も近いノードの祖先」。ルールに一致しない途中のオブジェクトは親子関係の計算にだけ使う。
    /// </summary>
    public sealed class Extractor
    {
        private const int MaxDepth = 64;
        private readonly ISdkHost _host;
        private readonly SubscriptionSpec _spec;
        private readonly List<CompiledSource> _sources = new List<CompiledSource>();
        private readonly List<CompiledRule> _rules = new List<CompiledRule>();
        private readonly List<CompiledWatch> _watches = new List<CompiledWatch>();
        private readonly ConditionalWeakTable<object, IdBox> _ids = new ConditionalWeakTable<object, IdBox>();
        private readonly Dictionary<Type, CompiledRule?> _ruleCache = new Dictionary<Type, CompiledRule?>();
        private int _nextId;
        private Dictionary<int, Target> _targets = new Dictionary<int, Target>();

        public IReadOnlyList<string> Diagnostics { get; }

        /// <param name="probe">監視値の試し読み（アプリが公開した値を混ぜない）。</param>
        public Extractor(ISdkHost host, SubscriptionSpec spec, bool probe = false)
        {
            _host = host;
            _spec = spec;
            MemberPath.IsDestroyedProbe = host.IsDestroyed;
            var diagnostics = new List<string>();
            for (var i = 0; i < spec.Sources.Length; i++)
            {
                var source = spec.Sources[i];
                var type = TypeIndex.Find(source.Type);
                if (type is null) { diagnostics.Add($"ソース {Label(source, i)}: 型 {source.Type} が見つかりません（このアプリでは使われていない UI の可能性）。"); continue; }
                try { _sources.Add(new CompiledSource(i, source, type)); }
                catch (FormatException e) { diagnostics.Add($"ソース {Label(source, i)}: {e.Message}"); }
            }
            var order = 0;
            foreach (var rule in spec.Rules)
            {
                try { _rules.Add(new CompiledRule(rule, order++)); }
                catch (FormatException e) { diagnostics.Add($"ルール {rule.Match}: {e.Message}"); }
            }
            foreach (var watch in spec.Values)
            {
                var type = TypeIndex.Find(watch.Type);
                if (type is null) { diagnostics.Add($"値 {watch.Key}: 型 {watch.Type} が見つかりません。"); continue; }
                try { _watches.Add(new CompiledWatch(watch, type)); }
                catch (FormatException e) { diagnostics.Add($"値 {watch.Key}: {e.Message}"); }
            }
            // アプリが公開した値（最終手段）は常に受け取る。試し読み（Inspect の監視値だけの抽出）では除く。
            if (!probe) _annotated = AnnotatedValues.Discover();
            _includeAppValues = !probe;
            Diagnostics = diagnostics;
        }

        private readonly AnnotatedValues.Member[] _annotated = Array.Empty<AnnotatedValues.Member>();
        private readonly bool _includeAppValues;

        private static string Label(NodeSource source, int index) => source.Label.Length > 0 ? source.Label : "#" + index;

        public sealed class Result
        {
            public Dictionary<int, NodeData> Nodes { get; } = new Dictionary<int, NodeData>();
            public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
            public bool Truncated { get; set; }
        }

        /// <summary>操作対象として引き直すための情報（最新の抽出時点）。</summary>
        public sealed class Target
        {
            public Target(object value, CompiledRule rule) { Value = value; Rule = rule; }
            public object Value { get; }
            public CompiledRule Rule { get; }
        }

        public bool TryGetTarget(int id, out Target target) => _targets.TryGetValue(id, out target!);

        public Result Extract()
        {
            var result = new Result();
            var entries = new List<Entry>();
            var targets = new Dictionary<int, Target>();
            var byOwner = new Dictionary<object, Entry>(ReferenceEqualityComparer.Instance);
            var limit = Math.Max(1, _spec.MaxNodes);
            foreach (var source in _sources)
            {
                if (entries.Count >= limit) { result.Truncated = true; break; }
                if (source.Kind == SourceKinds.Components) CollectComponents(source, entries, byOwner, limit, result);
                else CollectTree(source, entries, limit, result);
            }

            // 文書順に並べ、親を「最も近いノードの祖先」に決める。
            entries.Sort((a, b) => CompareKeys(a.Key, b.Key));
            var nodeByOwner = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            foreach (var entry in entries)
                if (entry.Owner != null && !nodeByOwner.ContainsKey(entry.Owner)) nodeByOwner[entry.Owner] = entry.Node.Id;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                entry.Node.Order = i;
                if (entry.TreeParent != 0) entry.Node.Parent = entry.TreeParent;
                else if (entry.Owner != null)
                {
                    foreach (var ancestor in _host.HierarchyAncestors(entry.Owner))
                        if (nodeByOwner.TryGetValue(ancestor, out var parent) && parent != entry.Node.Id) { entry.Node.Parent = parent; break; }
                }
                result.Nodes[entry.Node.Id] = entry.Node;
                targets[entry.Node.Id] = new Target(entry.Value, entry.Rule);
            }
            _targets = targets;
            CollectValues(result.Values);
            return result;
        }

        private void CollectComponents(CompiledSource source, List<Entry> entries, Dictionary<object, Entry> byOwner, int limit, Result result)
        {
            foreach (var candidate in _host.FindObjects(source.Type, source.Spec.IncludeInactive))
            {
                if (_host.IsDestroyed(candidate)) continue;
                if (!source.Spec.IncludeInactive && !_host.IsActive(candidate)) continue;
                if (source.Visible != null && source.Visible.EvaluateBool(candidate) == false) continue;
                var rule = RuleFor(candidate.GetType());
                if (rule is null || rule.Spec.Skip || !rule.Required(candidate)) continue;
                var owner = _host.HierarchyOwner(candidate) ?? candidate;
                if (byOwner.TryGetValue(owner, out var existing))
                {
                    if (!rule.Beats(existing.Rule, candidate.GetType(), existing.Value.GetType())) continue;
                    entries.Remove(existing);
                }
                if (entries.Count >= limit) { result.Truncated = true; return; }
                var node = Build(candidate, rule, source, null, "unity:/" + _host.HierarchyName(owner));
                var entry = new Entry(node, candidate, rule, owner, _host.SiblingPath(owner), 0);
                byOwner[owner] = entry;
                entries.Add(entry);
            }
        }

        private void CollectTree(CompiledSource source, List<Entry> entries, int limit, Result result)
        {
            IEnumerable<object> holders = source.Kind == SourceKinds.Static
                ? new object[] { source.Type }
                : _host.FindObjects(source.Type, source.Spec.IncludeInactive);
            var holderIndex = 0;
            foreach (var holder in holders)
            {
                if (!(holder is Type) && (_host.IsDestroyed(holder) || !source.Spec.IncludeInactive && !_host.IsActive(holder))) continue;
                var root = holder is Type staticType
                    ? source.Path?.EvaluateStatic(staticType)
                    : source.Path is null ? holder : source.Path.Evaluate(holder);
                if (root is null) continue;
                object? panelRoot = source.PanelRoot is null ? null : holder is Type t ? source.PanelRoot.EvaluateStatic(t) : source.PanelRoot.Evaluate(holder);
                var owner = holder is Type ? null : _host.HierarchyOwner(holder) ?? holder;
                var baseKey = owner is null ? new[] { int.MaxValue - 1, source.Index, holderIndex } : _host.SiblingPath(owner);
                var basePath = owner is null ? "unity:/" + source.Type.Name : "unity:/" + _host.HierarchyName(owner);
                holderIndex++;
                var sequence = 0;
                var stack = new Stack<(object Value, int Parent, string Path, int Depth)>();
                stack.Push((root, 0, basePath, 0));
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                while (stack.Count > 0)
                {
                    var (value, parent, path, depth) = stack.Pop();
                    if (!visited.Add(value)) continue;
                    if (source.Visible != null && source.Visible.EvaluateBool(value) == false) continue;
                    var rule = RuleFor(value.GetType());
                    var segment = path;
                    var nodeId = parent;
                    if (rule != null && !rule.Spec.Skip && rule.Required(value))
                    {
                        if (entries.Count >= limit) { result.Truncated = true; return; }
                        segment = path + "/" + PathSegment(value, rule);
                        var node = Build(value, rule, source, panelRoot, segment);
                        var key = new int[baseKey.Length + 2];
                        Array.Copy(baseKey, key, baseKey.Length);
                        key[baseKey.Length] = int.MaxValue; // 持ち主の子 GameObject より後（UI Toolkit は uGUI より手前に描かれることが多い）
                        key[baseKey.Length + 1] = sequence++;
                        entries.Add(new Entry(node, value, rule, depth == 0 ? owner : null, key, parent));
                        nodeId = node.Id;
                    }
                    if (source.Children is null || depth >= MaxDepth) continue;
                    var children = source.Children.EvaluateMany(value).ToList();
                    for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], nodeId, segment, depth + 1));
                }
            }
        }

        private string PathSegment(object value, CompiledRule rule)
        {
            var id = rule.TestId?.EvaluateString(value);
            return string.IsNullOrEmpty(id) ? value.GetType().Name : id!.Replace('/', '_');
        }

        private NodeData Build(object value, CompiledRule rule, CompiledSource source, object? panelRoot, string path)
        {
            var node = new NodeData
            {
                Id = IdOf(value),
                Role = rule.Spec.Role,
                NativeType = value.GetType().FullName ?? value.GetType().Name,
                Path = path,
                Source = source.Index,
                Clickable = rule.Spec.Clickable,
                Scrollable = rule.Spec.Scrollable,
                Editable = rule.SetText != null,
                Activatable = rule.Activate != null,
            };
            node.Password = rule.Password?.EvaluateBool(value) == true;
            node.Name = Clip(rule.Name?.EvaluateString(value));
            node.Text = node.Password ? "" : Clip(rule.Text?.EvaluateString(value));
            node.Placeholder = rule.Placeholder?.EvaluateString(value);
            node.TestId = rule.TestId?.EvaluateString(value);
            node.Enabled = rule.Enabled?.EvaluateBool(value) ?? true;
            node.Visible = (rule.Visible?.EvaluateBool(value) ?? true) && (source.Kind != SourceKinds.Components || _host.IsActive(value));
            var isChecked = rule.Checked?.EvaluateBool(value);
            node.Checked = isChecked is null ? (sbyte)-1 : isChecked.Value ? (sbyte)1 : (sbyte)0;
            var boundsValue = rule.Bounds is null ? value : rule.Bounds.Evaluate(value);
            var space = rule.Spec.BoundsSpace ?? source.Spec.BoundsSpace ?? (source.Kind == SourceKinds.Components ? BoundsSpaces.RectTransform : BoundsSpaces.Screen);
            if (boundsValue != null && _host.TryScreenRect(boundsValue, space, panelRoot, out var rect))
            {
                node.X = rect.X;
                node.Y = rect.Y;
                node.Width = rect.Width;
                node.Height = rect.Height;
            }
            if (rule.Attributes.Length > 0)
            {
                var attributes = new List<NamedValue>(rule.Attributes.Length);
                foreach (var (name, attributePath) in rule.Attributes)
                    if (attributePath.EvaluateString(value) is { } attribute) attributes.Add(NamedValue.Of(name, Clip(attribute, 300)));
                node.Attributes = attributes.ToArray();
            }
            return node;
        }

        private static string Clip(string? text, int max = 500)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var trimmed = text!.Trim();
            return trimmed.Length > max ? trimmed.Substring(0, max) : trimmed;
        }

        private void CollectValues(Dictionary<string, string> values)
        {
            foreach (var watch in _watches)
            {
                if (watch.Spec.Kind == SourceKinds.Static)
                {
                    if (watch.Path.EvaluateStatic(watch.Type) is { } value) values[watch.Spec.Key] = ValueFormat.ToText(value) ?? "";
                    continue;
                }
                var count = 0;
                foreach (var instance in _host.FindObjects(watch.Type, false))
                {
                    if (count >= Math.Max(1, watch.Spec.MaxInstances)) break;
                    if (_host.IsDestroyed(instance)) continue;
                    var value = ValueFormat.ToText(watch.Path.Evaluate(instance));
                    if (value is null) continue;
                    var suffix = watch.KeyPath?.EvaluateString(instance);
                    var key = watch.Spec.Key + "." + (string.IsNullOrEmpty(suffix) ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : suffix);
                    values[key] = value;
                    count++;
                }
            }
            if (!_includeAppValues) return;
            // アプリが公開した値（最終手段）。タイトルの設定と同じキーなら設定を優先する。
            foreach (var member in _annotated)
            {
                if (member.Static)
                {
                    if (!values.ContainsKey(member.Key) && member.Read(null) is { } value) values[member.Key] = ValueFormat.ToText(value) ?? "";
                    continue;
                }
                var count = 0;
                foreach (var instance in _host.FindObjects(member.Type, false))
                {
                    if (count >= 32) break;
                    if (_host.IsDestroyed(instance)) continue;
                    var text = ValueFormat.ToText(member.Read(instance));
                    if (text is null) continue;
                    var suffix = member.KeyPath?.EvaluateString(instance)
                        ?? (_host.HierarchyOwner(instance) is { } owner ? LastSegment(_host.HierarchyName(owner)) : null);
                    var key = member.Key + "." + (string.IsNullOrEmpty(suffix) ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : suffix);
                    if (!values.ContainsKey(key)) values[key] = text;
                    count++;
                }
            }
            foreach (var entry in PolyQA.Unity.PolyQAValues.Snapshot())
            {
                if (values.ContainsKey(entry.Key)) continue;
                object? value;
                try { value = entry.Getter(); }
                catch (Exception) { continue; } // アプリの関数の失敗で観測を止めない
                if (ValueFormat.ToText(value) is { } text) values[entry.Key] = text;
            }
        }

        private static string LastSegment(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        private CompiledRule? RuleFor(Type type)
        {
            lock (_ruleCache)
            {
                if (_ruleCache.TryGetValue(type, out var cached)) return cached;
            }
            CompiledRule? best = null;
            var bestDistance = int.MaxValue;
            foreach (var rule in _rules)
            {
                var distance = TypeIndex.Distance(type, rule.Spec.Match);
                if (distance < 0) continue;
                if (best is null || rule.Spec.Priority > best.Spec.Priority
                    || rule.Spec.Priority == best.Spec.Priority && distance < bestDistance)
                {
                    best = rule;
                    bestDistance = distance;
                }
            }
            lock (_ruleCache) _ruleCache[type] = best;
            return best;
        }

        public int IdOf(object value)
        {
            var box = _ids.GetValue(value, _ => new IdBox(++_nextId));
            return box.Id;
        }

        private static int CompareKeys(int[] a, int[] b)
        {
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        }

        private sealed class IdBox
        {
            public IdBox(int id) => Id = id;
            public int Id { get; }
        }

        private sealed class Entry
        {
            public Entry(NodeData node, object value, CompiledRule rule, object? owner, int[] key, int treeParent)
            {
                Node = node;
                Value = value;
                Rule = rule;
                Owner = owner;
                Key = key;
                TreeParent = treeParent;
            }

            public NodeData Node { get; }
            public object Value { get; }
            public CompiledRule Rule { get; }
            public object? Owner { get; }
            public int[] Key { get; }
            public int TreeParent { get; }
        }

        private sealed class CompiledSource
        {
            public CompiledSource(int index, NodeSource spec, Type type)
            {
                Index = index;
                Spec = spec;
                Type = type;
                Kind = spec.Kind;
                Path = MemberPath.Parse(spec.Path);
                Children = MemberPath.Parse(spec.ChildrenPath);
                PanelRoot = MemberPath.Parse(spec.PanelRootPath);
                Visible = MemberPath.Parse(spec.VisiblePath);
            }

            public int Index { get; }
            public NodeSource Spec { get; }
            public Type Type { get; }
            public string Kind { get; }
            public MemberPath? Path { get; }
            public MemberPath? Children { get; }
            public MemberPath? PanelRoot { get; }
            public MemberPath? Visible { get; }
        }

        public sealed class CompiledRule
        {
            public CompiledRule(NodeRule spec, int order)
            {
                Spec = spec;
                Order = order;
                Name = MemberPath.Parse(spec.NamePath);
                Text = MemberPath.Parse(spec.TextPath);
                Enabled = MemberPath.Parse(spec.EnabledPath);
                Visible = MemberPath.Parse(spec.VisiblePath);
                Checked = MemberPath.Parse(spec.CheckedPath);
                Bounds = MemberPath.Parse(spec.BoundsPath);
                TestId = MemberPath.Parse(spec.TestIdPath);
                SetText = MemberPath.Parse(spec.SetTextPath);
                Activate = MemberPath.Parse(spec.ActivatePath);
                Password = MemberPath.Parse(spec.PasswordPath);
                Placeholder = MemberPath.Parse(spec.PlaceholderPath);
                Require = MemberPath.Parse(spec.RequirePath);
                Attributes = spec.Attributes.Select(a => (a.Name, MemberPath.Parse(a.Path)))
                    .Where(a => a.Item2 != null).Select(a => (a.Name, a.Item2!)).ToArray();
            }

            public NodeRule Spec { get; }
            public int Order { get; }
            public MemberPath? Name { get; }
            public MemberPath? Text { get; }
            public MemberPath? Enabled { get; }
            public MemberPath? Visible { get; }
            public MemberPath? Checked { get; }
            public MemberPath? Bounds { get; }
            public MemberPath? TestId { get; }
            public MemberPath? SetText { get; }
            public MemberPath? Activate { get; }
            public MemberPath? Password { get; }
            public MemberPath? Placeholder { get; }
            public (string Name, MemberPath Path)[] Attributes { get; }
            public MemberPath? Require { get; }

            public bool Required(object value)
            {
                if (Require is null) return true;
                var result = Require.Evaluate(value);
                return result switch
                {
                    null => false,
                    bool b => b,
                    string s => s.Length > 0,
                    _ => true,
                };
            }

            /// <summary>同じ持ち主で別の候補より優先するか（優先度 → 具体的な型 → 記述順）。</summary>
            public bool Beats(CompiledRule other, Type mine, Type theirs)
            {
                if (Spec.Priority != other.Spec.Priority) return Spec.Priority > other.Spec.Priority;
                if (Spec.Role != "text" && other.Spec.Role == "text") return true;
                if (Spec.Role == "text" && other.Spec.Role != "text") return false;
                var a = TypeIndex.Distance(mine, Spec.Match);
                var b = TypeIndex.Distance(theirs, other.Spec.Match);
                if (a != b) return a < b;
                return Order < other.Order;
            }
        }

        private sealed class CompiledWatch
        {
            public CompiledWatch(ValueWatch spec, Type type)
            {
                Spec = spec;
                Type = type;
                Path = MemberPath.Parse(spec.Path) ?? throw new FormatException("Path を指定してください。");
                KeyPath = MemberPath.Parse(spec.KeyPath);
            }

            public ValueWatch Spec { get; }
            public Type Type { get; }
            public MemberPath Path { get; }
            public MemberPath? KeyPath { get; }
        }
    }

    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
