#nullable enable
using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PolyQA.Unity.Sdk.Editor
{
    /// <summary>
    /// Android で SDK が loopback の TCP で待ち受けるために必要な INTERNET 権限
    /// （Player Settings &gt; Internet Access: Require）を自動で有効にする。
    /// パッケージの導入・Editor の起動時と、SDK を含む Android ビルドの直前に確認する。
    /// </summary>
    [InitializeOnLoad]
    internal sealed class PolyQAAndroidSettings : IPreprocessBuildWithReport
    {
        const string CheckedKey = "PolyQA.Unity.Sdk.AndroidInternetChecked";

        static PolyQAAndroidSettings()
        {
            // ドメインのリロードごとに書き換えない（Editor のセッションで 1 回）。
            if (SessionState.GetBool(CheckedKey, false)) return;
            SessionState.SetBool(CheckedKey, true);
            // アセットの読み込み中に設定を保存しないよう、Editor の準備ができてから行う。
            EditorApplication.delayCall += () => Ensure("パッケージの導入時");
        }

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android) return;
            if (!IncludesSdk(report)) return;
            Ensure("ビルド時");
        }

        static bool IncludesSdk(BuildReport report)
        {
            if ((report.summary.options & BuildOptions.Development) != 0) return true;
            var defined = PlayerSettings.GetScriptingDefineSymbols(
                NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup)).Split(';');
            return Array.IndexOf(defined, "POLYQA_SDK") >= 0;
        }

        static void Ensure(string when)
        {
            if (PlayerSettings.Android.forceInternetPermission) return;
            PlayerSettings.Android.forceInternetPermission = true;
            AssetDatabase.SaveAssets();
            Debug.Log($"[PolyQA] {when}に Android の Internet Access を Require にしました（SDK が端末内の 127.0.0.1 で待ち受けるため）。");
        }
    }
}
