// UnityEngine に依存しない。
#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using PolyQA.Unity;

namespace PolyQA.Unity.Sdk.Core
{
    /// <summary>ゲームのコードで [PolyQAValue] を付けたメンバー。起動後に一度だけ探す。</summary>
    public static class AnnotatedValues
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static Member[]? _cache;

        public static Member[] Discover()
        {
            if (_cache != null) return _cache;
            var result = new List<Member>();
            foreach (var type in TypeIndex.UserTypes())
            {
                MemberInfo[] members;
                try { members = type.GetMembers(All); }
                catch (Exception) { continue; }
                foreach (var member in members)
                {
                    PolyQAValueAttribute? attribute;
                    try { attribute = member.GetCustomAttribute<PolyQAValueAttribute>(true); }
                    catch (Exception) { continue; }
                    if (attribute is null) continue;
                    Func<object?, object?>? read = null;
                    var isStatic = false;
                    switch (member)
                    {
                        case FieldInfo field:
                            read = target => field.GetValue(target);
                            isStatic = field.IsStatic;
                            break;
                        case PropertyInfo property when property.CanRead && property.GetIndexParameters().Length == 0:
                            read = target => property.GetValue(target);
                            isStatic = property.GetGetMethod(true)?.IsStatic == true;
                            break;
                    }
                    if (read is null) continue;
                    var key = string.IsNullOrEmpty(attribute.Key) ? type.Name + "." + member.Name : attribute.Key!;
                    MemberPath? keyPath = null;
                    try { keyPath = MemberPath.Parse(attribute.KeyPath); }
                    catch (FormatException) { }
                    result.Add(new Member(key, type, isStatic, keyPath, attribute.Description, target =>
                    {
                        try { return read(target); }
                        catch (Exception) { return null; }
                    }));
                }
            }
            return _cache = result.ToArray();
        }

        /// <summary>テスト用。探し直す。</summary>
        public static void Reset() => _cache = null;

        public sealed class Member
        {
            public Member(string key, Type type, bool isStatic, MemberPath? keyPath, string? description, Func<object?, object?> read)
            {
                Key = key;
                Type = type;
                Static = isStatic;
                KeyPath = keyPath;
                Description = description;
                Read = read;
            }

            public string Key { get; }
            public Type Type { get; }
            public bool Static { get; }
            public MemberPath? KeyPath { get; }
            public string? Description { get; }
            public Func<object?, object?> Read { get; }
        }
    }
}
