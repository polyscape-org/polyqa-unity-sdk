// UnityEngine に依存しない。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PolyQA.Unity.Sdk.Core
{
    /// <summary>型名（名前空間付き、または単純名）を読み込み済みアセンブリから探す。型を直接参照しないので、UI パッケージが無いアプリでも動く。</summary>
    public static class TypeIndex
    {
        private static readonly Dictionary<string, Type?> Resolved = new Dictionary<string, Type?>(StringComparer.Ordinal);

        public static Type? Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var key = name!.Trim();
            lock (Resolved)
            {
                if (Resolved.TryGetValue(key, out var cached)) return cached;
            }
            Type? found = null;
            // "Namespace.Type, Assembly" 形式
            if (key.IndexOf(',') >= 0)
            {
                try { found = Type.GetType(key, false); }
                catch (Exception e) when (e is ArgumentException || e is TypeLoadException || e is FileLoadException) { }
                if (found is null) key = key.Substring(0, key.IndexOf(',')).Trim();
            }
            if (found is null)
            {
                var simple = key.IndexOf('.') < 0;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!simple)
                    {
                        found = assembly.GetType(key, false);
                        if (found != null) break;
                        continue;
                    }
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null)!; }
                    foreach (var type in types)
                        if (type.Name == key) { found = type; break; }
                    if (found != null) break;
                }
            }
            lock (Resolved) Resolved[name!.Trim()] = found;
            return found;
        }

        /// <summary>型またはその基底型・インターフェースが名前に一致するか（名前空間付きか単純名）。</summary>
        public static bool Is(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (NameMatches(t, name)) return true;
            foreach (var face in type.GetInterfaces())
                if (NameMatches(face, name)) return true;
            return false;
        }

        /// <summary>一致した型の継承の深さ（0=その型）。一致しなければ -1。具体的な型ほど優先するのに使う。</summary>
        public static int Distance(Type type, string name)
        {
            var depth = 0;
            for (var t = type; t != null; t = t.BaseType, depth++)
                if (NameMatches(t, name)) return depth;
            foreach (var face in type.GetInterfaces())
                if (NameMatches(face, name)) return 1000;
            return -1;
        }

        private static Type[]? _userTypes;

        /// <summary>
        /// ゲームのコード（エンジン・標準ライブラリ・PolyQA 自身以外）のアセンブリにある型。監視値の候補探しと [PolyQAValue] の探索に使う。
        /// 読み込み済みアセンブリは実行中に増えない前提で、初回だけ数える。
        /// </summary>
        public static Type[] UserTypes()
        {
            if (_userTypes != null) return _userTypes;
            var result = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsUserAssembly(assembly.GetName().Name)) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null)!; }
                foreach (var type in types)
                    if (type != null && !type.IsGenericTypeDefinition && !type.Name.StartsWith("<", StringComparison.Ordinal)) result.Add(type);
            }
            return _userTypes = result.ToArray();
        }

        private static readonly string[] EnginePrefixes =
        {
            "UnityEngine", "UnityEditor", "Unity.", "System", "mscorlib", "netstandard", "Mono.", "Microsoft.", "PolyQA.", "RPCLite",
            "MemoryPack", "nunit", "NUnit", "Newtonsoft", "Bee.", "JetBrains", "ExCSS", "xunit", "Moq", "Castle.", "Kuebiko",
        };

        public static bool IsUserAssembly(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (IncludedAssemblies.Contains(name!)) return true;
            foreach (var prefix in EnginePrefixes)
                if (name!.StartsWith(prefix, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>除外の接頭辞に当たってもゲームのコードとして扱うアセンブリ（テスト用）。</summary>
        public static readonly HashSet<string> IncludedAssemblies = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>テスト用。型の一覧を数え直す。</summary>
        public static void ResetUserTypes(Type[]? types = null) => _userTypes = types;

        private static bool NameMatches(Type type, string name)
        {
            if (name.IndexOf('.') >= 0) return string.Equals(GenericlessName(type.FullName), name, StringComparison.Ordinal);
            return string.Equals(GenericlessName(type.Name), name, StringComparison.Ordinal);
        }

        private static string? GenericlessName(string? name)
        {
            if (name is null) return null;
            var tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }
    }
}
