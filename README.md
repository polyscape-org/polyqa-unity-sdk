# PolyQA SDK for Unity

[PolyQA](https://polyqa.polyscape.io) の Driver から、Unity アプリの UI・内部状態を観測・操作するための SDK です。
**PolyQA のご契約者だけが利用できます**（[LICENSE.md](LICENSE.md)）。

## 導入

`Packages/manifest.json` に追加します。`#` の後ろは PolyQA の Driver と同じ版のタグにしてください
（版が違うと「プロトコルの版が一致しません」で接続できません）。

```json
"com.polyscape.polyqa": "https://github.com/polyscape-org/polyqa-unity-sdk.git#v1.26.1113.0"
```

- **Development Build** でビルドしてください。SDK は Editor・Development Build・定義シンボル `POLYQA_SDK` のときだけ含まれ、リリースビルドには入りません。
  **配信するビルドで `POLYQA_SDK` を定義しないでください。**
- シーンへの配置・コードの追加は不要です（起動時に自動で待ち受けます）。起動引数 `-polyqa-sdk-disable` で無効にできます。
- Android の Internet Access（`Player Settings > Internet Access: Require`）は導入時に自動で設定されます。SDK は端末内の 127.0.0.1 でだけ待ち受け、PC の Driver とは USB 経由で通信します。
- 同梱 DLL（MemoryPack.Core / System.Collections.Immutable / System.Runtime.CompilerServices.Unsafe）を NuGetForUnity などで既に入れている場合は、重複する方を削除してください。

## 任意の API

```csharp
#if POLYQA_SDK || DEVELOPMENT_BUILD || UNITY_EDITOR
PolyQA.Unity.Sdk.PolyQASdk.SetBusy("download", true);  // 処理中（Driver はこの間を操作可能とみなさない）
PolyQA.Unity.Sdk.PolyQASdk.Report("boss start");       // 記録に印を残す
#endif
```

画面に出ないゲームの値は、通常は PolyQA の Web の「Unity設定」で読み方を設定します（アプリの変更は不要）。
型・メンバーで特定しにくい値だけ、アプリ側から公開できます（リリースビルドでは何もしません）。

```csharp
using PolyQA.Unity;

[PolyQAValue("hp")] int currentHealth;
PolyQAValues.Watch("stage", () => StageService.Current.Id);
```

## ライセンス

[LICENSE.md](LICENSE.md)。同梱する第三者ソフトウェアは [Third Party Notices.md](Third%20Party%20Notices.md) を参照してください。

このリポジトリは PolyQA のリリースに合わせて自動で更新されます。Issue・Pull Request は受け付けていません。
