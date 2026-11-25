using UnityEngine.Scripting;

// どのシーン・スクリプトからも参照されないアセンブリは、IL2CPP のコード削除でアセンブリごと除かれる
// （RuntimeInitializeOnLoadMethod だけでは残らない）。SDK はアプリから参照されなくても自動で起動するため、常にリンクする。
// パッケージ内の link.xml はビルドに使われないので、属性で指定する。
[assembly: AlwaysLinkAssembly]
[assembly: Preserve]
