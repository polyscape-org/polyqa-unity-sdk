// UnityEngine に依存しない。メインスレッドで呼ぶ。
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PolyQA.Unity.Protocol;

namespace PolyQA.Unity.Sdk.Core
{
    /// <summary>
    /// 監視値の候補探しのために、シーン内のゲームのスクリプトの型とメンバーを調べる。
    /// <list type="bullet">
    /// <item>読むのはフィールドだけ（自動実装プロパティは裏のフィールドをプロパティ名で返す）。プロパティの getter は副作用があり得るので呼ばない。
    /// 例外は [PolyQAValue] が付いたプロパティ（アプリが公開を明示したもの）。</item>
    /// <item>数値・文字列・真偽・列挙はそのまま、コレクションは Count、ゲームの型（ScriptableObject・データクラス）は 1 段（詳細は 2 段）だけ入れ子を辿る。</item>
    /// <item>値の例は伏せずに返すので、Driver 側で伏せ字にしてからモデル・記録へ渡す。</item>
    /// </list>
    /// </summary>
    public sealed class TypeCatalogBuilder
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private readonly ISdkHost _host;

        public TypeCatalogBuilder(ISdkHost host) => _host = host;

        public TypeCatalog Build(TypeQuery query)
        {
            var maxTypes = Math.Max(1, Math.Min(query.MaxTypes, 500));
            var maxMembers = Math.Max(1, Math.Min(query.MaxMembers, 200));
            var maxSamples = Math.Max(0, Math.Min(query.MaxSamples, 10));
            var catalog = new TypeCatalog { ActiveScene = _host.State().ActiveScene };
            // シーン内のスクリプトを型ごとにまとめる。
            var byType = new Dictionary<Type, List<object>>();
            foreach (var script in _host.SceneScripts())
            {
                if (script is null || _host.IsDestroyed(script)) continue;
                var type = script.GetType();
                if (!TypeIndex.IsUserAssembly(type.Assembly.GetName().Name)) continue;
                if (!byType.TryGetValue(type, out var list)) byType[type] = list = new List<object>();
                list.Add(script);
            }
            if (query.IncludeStatic || !string.IsNullOrEmpty(query.TypeName))
                foreach (var type in TypeIndex.UserTypes())
                    if (!byType.ContainsKey(type) && (Matches(type, query.TypeName) || query.IncludeStatic && HasStaticValues(type)))
                        byType[type] = new List<object>();

            var detail = !string.IsNullOrEmpty(query.TypeName);
            var types = new List<TypeSummary>();
            foreach (var pair in byType.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key.FullName, StringComparer.Ordinal))
            {
                var type = pair.Key;
                if (detail ? !Matches(type, query.TypeName) : !string.IsNullOrEmpty(query.Filter)
                    && (type.FullName ?? type.Name).IndexOf(query.Filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (types.Count >= maxTypes) { catalog.Truncated = true; break; }
                var samples = pair.Value.Take(maxSamples).ToArray();
                var members = new List<MemberSummary>();
                Collect(type, samples, "", detail ? 2 : 1, members, maxMembers, false);
                Collect(type, Array.Empty<object>(), "", 0, members, maxMembers, true);
                if (members.Count == 0 && pair.Value.Count == 0) continue;
                types.Add(new TypeSummary
                {
                    Name = type.FullName ?? type.Name,
                    Assembly = type.Assembly.GetName().Name ?? "",
                    BaseType = type.BaseType?.FullName,
                    Instances = pair.Value.Count,
                    InstanceNames = samples.Select(InstanceName).ToArray(),
                    Members = members.ToArray(),
                });
            }
            catalog.Types = types.ToArray();
            return catalog;
        }

        private static bool Matches(Type type, string? name)
            => !string.IsNullOrEmpty(name) && (string.Equals(type.FullName, name, StringComparison.Ordinal) || string.Equals(type.Name, name, StringComparison.Ordinal));

        private string InstanceName(object value)
            => _host.HierarchyOwner(value) is { } owner ? LastSegment(_host.HierarchyName(owner)) : value.GetType().Name;

        private static string LastSegment(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        private static bool HasStaticValues(Type type)
        {
            foreach (var field in type.GetFields(Static))
                if (!field.IsLiteral && !IsCompilerGenerated(field) && (IsLeaf(field.FieldType) || IsCollection(field.FieldType))) return true;
            return false;
        }

        /// <summary>型と基底型（ゲームのアセンブリの間だけ）のメンバーを集める。</summary>
        private void Collect(Type type, object[] samples, string prefix, int depth, List<MemberSummary> members, int max, bool statics)
        {
            for (var t = type; t != null && TypeIndex.IsUserAssembly(t.Assembly.GetName().Name); t = t.BaseType)
            {
                foreach (var field in t.GetFields(statics ? Static : Instance))
                {
                    if (members.Count >= max) return;
                    if (field.IsLiteral || IsEventBacking(field)) continue;
                    var name = MemberName(field);
                    if (name is null) continue;
                    var annotated = Annotation(field, t) is not null;
                    Add(field.FieldType, prefix + name, statics, field.IsPublic, annotated,
                        statics ? new[] { Safe(() => field.GetValue(null)) } : samples.Select(s => Safe(() => s is null ? null : field.GetValue(s))).ToArray(),
                        depth, members, max);
                }
                foreach (var property in t.GetProperties(statics ? Static : Instance))
                {
                    // getter は呼ばない。[PolyQAValue] で公開を明示したものだけ。
                    if (members.Count >= max) return;
                    if (property.GetIndexParameters().Length > 0 || !property.CanRead || property.GetCustomAttribute<PolyQAValueAttribute>(true) is null) continue;
                    Add(property.PropertyType, prefix + property.Name, statics, property.GetGetMethod() != null, true,
                        statics ? new[] { Safe(() => property.GetValue(null)) } : samples.Select(s => Safe(() => s is null ? null : property.GetValue(s))).ToArray(),
                        depth, members, max);
                }
                if (statics) return; // 静的メンバーは宣言した型だけ
            }
        }

        private void Add(Type memberType, string path, bool statics, bool isPublic, bool annotated, object?[] values, int depth, List<MemberSummary> members, int max)
        {
            // 自動実装プロパティは裏のフィールドと [PolyQAValue] のプロパティの両方から来る。先に来た方を使い、印だけ引き継ぐ。
            foreach (var existing in members)
                if (existing.Path == path || existing.Path == path + ".Count")
                {
                    existing.Annotated |= annotated;
                    return;
                }
            if (IsLeaf(memberType))
            {
                members.Add(new MemberSummary
                {
                    Path = path, Type = ShortName(memberType), Static = statics, Public = isPublic, Annotated = annotated,
                    Samples = values.Select(v => ValueFormat.ToText(v, 80) ?? "null").ToArray(),
                });
                return;
            }
            if (IsCollection(memberType))
            {
                members.Add(new MemberSummary
                {
                    Path = path + ".Count", Type = ShortName(memberType), Static = statics, Public = isPublic, Annotated = annotated,
                    Samples = values.Select(v => v is ICollection c ? c.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null").ToArray(),
                });
                return;
            }
            // ゲームの型（データクラス・ScriptableObject）だけ入れ子を辿る。コンポーネント同士の参照は辿らない（別の型として一覧に出る）。
            if (depth <= 0 || statics || !TypeIndex.IsUserAssembly(memberType.Assembly.GetName().Name) || IsComponent(memberType)) return;
            var nested = values.Select(v => v ?? (object?)null).ToArray();
            CollectNested(memberType, nested, path + ".", depth - 1, members, max);
        }

        private void CollectNested(Type type, object?[] values, string prefix, int depth, List<MemberSummary> members, int max)
        {
            for (var t = type; t != null && TypeIndex.IsUserAssembly(t.Assembly.GetName().Name); t = t.BaseType)
                foreach (var field in t.GetFields(Instance))
                {
                    if (members.Count >= max) return;
                    if (field.IsLiteral || IsEventBacking(field)) continue;
                    var name = MemberName(field);
                    if (name is null) continue;
                    Add(field.FieldType, prefix + name, false, field.IsPublic, Annotation(field, t) is not null,
                        values.Select(v => Safe(() => v is null || _host.IsDestroyed(v) ? null : field.GetValue(v))).ToArray(), depth, members, max);
                }
        }

        /// <summary>フィールド名。自動実装プロパティの裏のフィールドはプロパティ名にする（MemberPath はプロパティとして読める）。その他の生成フィールドは除く。</summary>
        private static string? MemberName(FieldInfo field)
        {
            var name = field.Name;
            if (name.Length > 0 && name[0] == '<')
            {
                var end = name.IndexOf(">k__BackingField", StringComparison.Ordinal);
                return end > 1 ? name.Substring(1, end - 1) : null;
            }
            return name;
        }

        private static bool IsCompilerGenerated(FieldInfo field) => field.Name.Length > 0 && field.Name[0] == '<' && MemberName(field) is null;

        private static bool IsEventBacking(FieldInfo field) => typeof(Delegate).IsAssignableFrom(field.FieldType);

        private static PolyQAValueAttribute? Annotation(MemberInfo member, Type declaring)
        {
            if (member.GetCustomAttribute<PolyQAValueAttribute>(true) is { } direct) return direct;
            // 自動実装プロパティに付けた属性は裏のフィールドに無い。
            if (member is FieldInfo field && MemberName(field) is { } name && name != field.Name)
                return declaring.GetProperty(name, Instance | Static)?.GetCustomAttribute<PolyQAValueAttribute>(true);
            return null;
        }

        internal static bool IsLeaf(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string) || underlying == typeof(decimal);
        }

        private static bool IsCollection(Type type) => typeof(ICollection).IsAssignableFrom(type) || type.IsArray;

        private static bool IsComponent(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (t.FullName == "UnityEngine.Component") return true;
            return false;
        }

        private static object? Safe(Func<object?> read)
        {
            try { return read(); }
            catch (Exception) { return null; }
        }

        internal static string ShortName(Type type)
        {
            if (Nullable.GetUnderlyingType(type) is { } inner) return ShortName(inner) + "?";
            if (type.IsArray) return ShortName(type.GetElementType()!) + "[]";
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Int32: return "int";
                case TypeCode.Int64: return "long";
                case TypeCode.Single: return "float";
                case TypeCode.Double: return "double";
                case TypeCode.Boolean: return "bool";
                case TypeCode.String: return "string";
            }
            if (!type.IsGenericType) return type.Name;
            var name = type.Name;
            var tick = name.IndexOf('`');
            return (tick < 0 ? name : name.Substring(0, tick)) + "<" + string.Join(", ", type.GetGenericArguments().Select(ShortName)) + ">";
        }
    }
}
