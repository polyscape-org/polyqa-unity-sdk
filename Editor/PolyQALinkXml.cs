#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;

namespace PolyQA.Unity.Sdk.Editor
{
    /// <summary>
    /// IL2CPP のコード削除で、SDK・RPC の生成コードと、Driver がリフレクションで読む UI の型を消さないようにする。
    /// パッケージ内の link.xml はビルドに使われないため、ビルド時に生成して渡す。
    /// SDK を含まないビルド（リリース）では何もしない。
    /// </summary>
    internal sealed class PolyQALinkXml : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            var development = (report.summary.options & BuildOptions.Development) != 0;
            var defined = UnityEditor.PlayerSettings.GetScriptingDefineSymbols(
                NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup)).Split(';');
            if (!development && Array.IndexOf(defined, "POLYQA_SDK") < 0) return "";
            var directory = Path.GetFullPath(Path.Combine("Library", "PolyQA"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "polyqa-sdk-link.xml");
            // UI の型は存在するアセンブリだけが対象（無いアセンブリの指定は無視される）。
            // 型ごと残す（preserve="all"）と大きいため、Driver の既定の読み方（UnityPresets）で使うものに限る。
            File.WriteAllText(path, @"<linker>
  <assembly fullname=""PolyQA.Unity.Sdk"" preserve=""all"" />
  <assembly fullname=""PolyQA.Unity.Protocol"" preserve=""all"" />
  <assembly fullname=""RPCLite"" preserve=""all"" />
  <assembly fullname=""MemoryPack.Core"" preserve=""all"" />
  <assembly fullname=""UnityEngine.UI"">
    <type fullname=""UnityEngine.EventSystems.UIBehaviour"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.Selectable"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.Button"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.Button/ButtonClickedEvent"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.Toggle"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.Slider"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.Scrollbar"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.Dropdown"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.InputField"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.ScrollRect"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.Graphic"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.MaskableGraphic"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.Text"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.Image"" preserve=""all"" />
    <type fullname=""UnityEngine.UI.RawImage"" preserve=""all"" />
  </assembly>
  <assembly fullname=""Unity.TextMeshPro"">
    <type fullname=""TMPro.TMP_Text"" preserve=""all"" />
    <type fullname=""TMPro.TextMeshProUGUI"" preserve=""all"" />
    <type fullname=""TMPro.TMP_InputField"" preserve=""all"" />
    <type fullname=""TMPro.TMP_Dropdown"" preserve=""all"" />
  </assembly>
  <assembly fullname=""UnityEngine.UIElementsModule"">
    <type fullname=""UnityEngine.UIElements.UIDocument"" preserve=""all"" />
    <type fullname=""UnityEngine.UIElements.VisualElement"" preserve=""all"" />
    <type fullname=""UnityEngine.UIElements.VisualElement/Hierarchy"" preserve=""all"" />
    <type fullname=""UnityEngine.UIElements.TextElement"" preserve=""all"" />
    <type fullname=""UnityEngine.UIElements.Label"" preserve=""all"" />
    <type fullname=""UnityEngine.UIElements.Button"" preserve=""all"" />
    <type fullname=""UnityEngine.UIElements.Toggle"" preserve=""all"" />
    <type fullname=""UnityEngine.UIElements.TextField"" preserve=""all"" />
    <type fullname=""UnityEngine.UIElements.IResolvedStyle"" preserve=""all"" />
  </assembly>
  <assembly fullname=""UnityEngine.UIModule"">
    <type fullname=""UnityEngine.Canvas"" preserve=""all"" />
  </assembly>
</linker>");
            return path;
        }
    }
}
