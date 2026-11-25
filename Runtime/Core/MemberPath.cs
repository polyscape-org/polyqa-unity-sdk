// UnityEngine に依存しない。Driver のテストでも同じソースをコンパイルして検証する。
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace PolyQA.Unity.Sdk.Core
{
    /// <summary>
    /// Driver から受け取ったメンバーパスをリフレクションで評価する。UI フレームワークの型は知らない。
    /// <code>
    /// path   := alt ('|' alt)*          最初に null・空文字以外を返した候補を使う
    /// alt    := chain (('==' | '!=') literal)?   比較すると bool（ToString を大文字小文字無視で比較）
    /// chain  := '$self' | segment ('.' segment)*
    /// segment:= 名前 | 名前 '()'        プロパティ・フィールド（非公開を含む）・引数なしメソッド
    /// </code>
    /// 途中で null や例外になれば null。例外の内容は <see cref="LastError"/> に残す（診断用）。
    /// </summary>
    public sealed class MemberPath
    {
        private readonly Alternative[] _alternatives;
        public string Source { get; }
        [ThreadStatic] private static string? _lastError;
        /// <summary>このスレッドで最後に起きた評価の例外（診断用）。</summary>
        public static string? LastError { get => _lastError; private set => _lastError = value; }

        private MemberPath(string source, Alternative[] alternatives)
        {
            Source = source;
            _alternatives = alternatives;
        }

        private static readonly Dictionary<string, MemberPath> Cache = new Dictionary<string, MemberPath>(StringComparer.Ordinal);

        /// <summary>空・null なら null を返す。構文の誤りは <see cref="FormatException"/>。</summary>
        public static MemberPath? Parse(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var text = path!.Trim();
            lock (Cache)
            {
                if (Cache.TryGetValue(text, out var cached)) return cached;
            }
            var parts = text.Split('|');
            var alternatives = new Alternative[parts.Length];
            for (var i = 0; i < parts.Length; i++) alternatives[i] = Alternative.Parse(parts[i].Trim(), text);
            var parsed = new MemberPath(text, alternatives);
            lock (Cache) Cache[text] = parsed;
            return parsed;
        }

        /// <summary>インスタンスを根に評価する。</summary>
        public object? Evaluate(object? root) => EvaluateCore(root, null);

        /// <summary>型の静的メンバーを根に評価する（最初の名前は静的メンバー）。</summary>
        public object? EvaluateStatic(Type type) => EvaluateCore(null, type);

        private object? EvaluateCore(object? root, Type? staticType)
        {
            object? last = null;
            foreach (var alternative in _alternatives)
            {
                var value = alternative.Evaluate(root, staticType);
                if (value is null) continue;
                if (value is string s && s.Length == 0) { last = value; continue; }
                return value;
            }
            return last;
        }

        public string? EvaluateString(object? root) => ValueFormat.ToText(Evaluate(root));

        public bool? EvaluateBool(object? root)
        {
            var value = Evaluate(root);
            switch (value)
            {
                case null: return null;
                case bool b: return b;
                case string s when bool.TryParse(s, out var parsed): return parsed;
                case IConvertible c:
                    try { return Convert.ToDouble(c, CultureInfo.InvariantCulture) != 0; }
                    catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException) { return true; }
                default: return true;
            }
        }

        /// <summary>IEnumerable を返すメンバーの要素を列挙する（文字列は列挙しない）。</summary>
        public IEnumerable<object> EvaluateMany(object? root)
        {
            if (Evaluate(root) is IEnumerable items && !(items is string))
            {
                foreach (var item in items)
                    if (item != null) yield return item;
            }
        }

        /// <summary>
        /// 最後のメンバーに値を設定する。書き込めるプロパティ・フィールド、または 1 引数のメソッド（SetTextWithoutNotify など）。
        /// 成功したら true。
        /// </summary>
        public bool TrySet(object root, object? value, out string? error)
        {
            if (_alternatives.Length != 1) { error = "設定先のパスに候補（|）は使えません。"; return false; }
            return _alternatives[0].TrySet(root, value, out error);
        }

        internal static void ReportError(string message) => LastError = message;
        public static void ClearError() => LastError = null;

        private sealed class Alternative
        {
            private readonly Segment[] _segments;
            private readonly string? _compare;
            private readonly bool _negate;

            private Alternative(Segment[] segments, string? compare, bool negate)
            {
                _segments = segments;
                _compare = compare;
                _negate = negate;
            }

            public static Alternative Parse(string text, string whole)
            {
                string? compare = null;
                var negate = false;
                var eq = text.IndexOf("==", StringComparison.Ordinal);
                var ne = text.IndexOf("!=", StringComparison.Ordinal);
                if (ne >= 0 && (eq < 0 || ne < eq)) { compare = text.Substring(ne + 2).Trim(); negate = true; text = text.Substring(0, ne).Trim(); }
                else if (eq >= 0) { compare = text.Substring(eq + 2).Trim(); text = text.Substring(0, eq).Trim(); }
                if (text.Length == 0) throw new FormatException($"メンバーパスが空です: {whole}");
                if (text == "$self") return new Alternative(Array.Empty<Segment>(), compare, negate);
                var names = text.Split('.');
                var segments = new Segment[names.Length];
                for (var i = 0; i < names.Length; i++)
                {
                    var name = names[i].Trim();
                    var call = name.EndsWith("()", StringComparison.Ordinal);
                    if (call) name = name.Substring(0, name.Length - 2);
                    if (name.Length == 0 || !IsIdentifier(name)) throw new FormatException($"メンバー名が不正です: {whole}");
                    segments[i] = new Segment(name, call);
                }
                return new Alternative(segments, compare, negate);
            }

            private static bool IsIdentifier(string name)
            {
                if (!(char.IsLetter(name[0]) || name[0] == '_' || name[0] == '<')) return false;
                foreach (var c in name)
                    if (!(char.IsLetterOrDigit(c) || c == '_' || c == '<' || c == '>' || c == '`')) return false;
                return true;
            }

            public object? Evaluate(object? root, Type? staticType)
            {
                object? current = root;
                try
                {
                    for (var i = 0; i < _segments.Length; i++)
                    {
                        if (i == 0 && staticType != null) current = _segments[i].Read(null, staticType);
                        else
                        {
                            if (current is null || IsDestroyed(current)) return Finish(null);
                            current = _segments[i].Read(current, current.GetType());
                        }
                    }
                }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                    ReportError(inner.GetType().Name + ": " + inner.Message);
                    return Finish(null);
                }
                return Finish(current);
            }

            private object? Finish(object? value)
            {
                if (_compare is null) return value;
                var equal = string.Equals(ValueFormat.ToText(value) ?? "null", _compare, StringComparison.OrdinalIgnoreCase);
                return _negate ? !equal : equal;
            }

            public bool TrySet(object root, object? value, out string? error)
            {
                error = null;
                if (_segments.Length == 0 || _compare != null) { error = "設定先のメンバーを指定してください。"; return false; }
                object? target = root;
                try
                {
                    for (var i = 0; i < _segments.Length - 1; i++)
                    {
                        if (target is null) { error = "設定先が見つかりません。"; return false; }
                        target = _segments[i].Read(target, target.GetType());
                    }
                    if (target is null) { error = "設定先が見つかりません。"; return false; }
                    return _segments[_segments.Length - 1].Write(target, value, out error);
                }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                    error = inner.GetType().Name + ": " + inner.Message;
                    return false;
                }
            }
        }

        /// <summary>破棄済みの UnityEngine.Object は == null が true になる（参照は残る）。型名で判定して UnityEngine に依存しない。</summary>
        internal static Func<object, bool> IsDestroyedProbe = _ => false;
        private static bool IsDestroyed(object value) => IsDestroyedProbe(value);

        private sealed class Segment
        {
            private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            private readonly string _name;
            private readonly bool _call;
            private readonly Dictionary<Type, Accessor> _accessors = new Dictionary<Type, Accessor>();

            public Segment(string name, bool call)
            {
                _name = name;
                _call = call;
            }

            public object? Read(object? target, Type type)
            {
                var accessor = Resolve(type, target is null);
                if (accessor.Read is null) throw new MissingMemberException(type.FullName, _name);
                return accessor.Read(target);
            }

            public bool Write(object target, object? value, out string? error)
            {
                error = null;
                var type = target.GetType();
                for (var t = type; t != null; t = t.BaseType)
                {
                    if (!_call)
                    {
                        var property = t.GetProperty(_name, Instance | BindingFlags.DeclaredOnly);
                        if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0)
                        {
                            property.SetValue(target, ConvertTo(value, property.PropertyType));
                            return true;
                        }
                        var field = t.GetField(_name, Instance | BindingFlags.DeclaredOnly);
                        if (field != null && !field.IsInitOnly)
                        {
                            field.SetValue(target, ConvertTo(value, field.FieldType));
                            return true;
                        }
                    }
                    foreach (var method in t.GetMethods(Instance | BindingFlags.DeclaredOnly))
                    {
                        if (method.Name != _name) continue;
                        var parameters = method.GetParameters();
                        if (parameters.Length != 1) continue;
                        method.Invoke(target, new[] { ConvertTo(value, parameters[0].ParameterType) });
                        return true;
                    }
                }
                error = $"{type.FullName} に書き込めるメンバー {_name} がありません。";
                return false;
            }

            private static object? ConvertTo(object? value, Type type)
            {
                if (value is null || type.IsInstanceOfType(value)) return value;
                if (type.IsEnum && value is string s) return Enum.Parse(type, s, true);
                return Convert.ChangeType(value, Nullable.GetUnderlyingType(type) ?? type, CultureInfo.InvariantCulture);
            }

            private Accessor Resolve(Type type, bool isStatic)
            {
                lock (_accessors)
                {
                    if (_accessors.TryGetValue(type, out var cached)) return cached;
                }
                var flags = isStatic ? Static : Instance;
                Func<object?, object?>? read = null;
                for (var t = type; t != null && read is null; t = isStatic ? null : t.BaseType)
                {
                    if (_call)
                    {
                        var method = t.GetMethod(_name, flags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (method != null) read = target => method.Invoke(target, null);
                    }
                    else
                    {
                        var property = t.GetProperty(_name, flags | BindingFlags.DeclaredOnly);
                        if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                        {
                            var getter = property.GetGetMethod(true);
                            if (getter != null) read = target => getter.Invoke(target, null);
                        }
                        if (read is null)
                        {
                            var field = t.GetField(_name, flags | BindingFlags.DeclaredOnly);
                            if (field != null) read = target => field.GetValue(target);
                        }
                    }
                }
                // インターフェースの明示的実装・インターフェース型（IEnumerable の Children など）
                if (read is null && !isStatic)
                {
                    foreach (var face in type.GetInterfaces())
                    {
                        if (_call)
                        {
                            var method = face.GetMethod(_name, Type.EmptyTypes);
                            if (method != null) { read = target => method.Invoke(target, null); break; }
                        }
                        else if (face.GetProperty(_name) is { CanRead: true } property && property.GetIndexParameters().Length == 0)
                        {
                            read = target => property.GetValue(target);
                            break;
                        }
                    }
                }
                var accessor = new Accessor(read);
                lock (_accessors) _accessors[type] = accessor;
                return accessor;
            }

            private sealed class Accessor
            {
                public Accessor(Func<object?, object?>? read) => Read = read;
                public Func<object?, object?>? Read { get; }
            }
        }
    }

    public static class ValueFormat
    {
        /// <summary>送信する文字列に変える。数値は InvariantCulture、列挙は名前。長すぎる値は切り詰める。</summary>
        public static string? ToText(object? value, int max = 2000)
        {
            string? text;
            switch (value)
            {
                case null: return null;
                case string s: text = s; break;
                case bool b: text = b ? "true" : "false"; break;
                case float f: text = f.ToString("0.###", CultureInfo.InvariantCulture); break;
                case double d: text = d.ToString("0.###", CultureInfo.InvariantCulture); break;
                case IFormattable formattable: text = formattable.ToString(null, CultureInfo.InvariantCulture); break;
                default: text = value.ToString(); break;
            }
            if (text is null) return null;
            return text.Length > max ? text.Substring(0, max) : text;
        }
    }
}
