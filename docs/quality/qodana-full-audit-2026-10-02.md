# Qodana 全件ソース監査 — 2026-10-02

対象リポジトリ: `NYGsatoshi/Coglatas`  
作業ブランチ: `ops/qodana-code-scanning-triage-20261002`

## 結果

前回未完了だった **未解決1,212件** と、`Response` 接尾辞を根拠に閉じた **1,048件**、計 **2,260件** を監査対象として固定し、全件をソースへ対応付けて判定台帳を作成した。
この監査は全件の棚卸し・分類と、シリアライズ経路／設定バインド／前回の誤判定の重点検証であり、2,260件すべてについて動作不具合の有無を実行試験で確定したものではない。

| 対象 | 判定 | 件数 |
| --- | --- | ---: |
| 前回の接尾辞によるDismiss | 実際の出力・シリアライズ経路を根拠としてDismissを維持 | 1,035 |
| 前回の接尾辞によるDismiss | 根拠不足を認めて再オープン | 13 |
| 前回の未解決警告 | 誤検知として新規Dismiss | 28 |
| 前回の未解決警告 | 分類と理由を記録してOpenを維持 | 1,184 |
| 合計 | 全対象IDを重複なく台帳化 | 2,260 |

実際のGitHub Code Scanningの状態変更は **28件のDismissと13件の再オープン、計41件**。
全件についてPATCH後のGETで確認し、保存した適用後スナップショットから監査対象2,260件の最終状態も独立に照合した。

- Qodana（`QDNETC`）Open: **1,212 → 1,197件**。
- Qodana Dismissed: **1,176 → 1,191件**。
- SonarCloud Open: **30件のまま**。対象外ツールの警告は変更していない。
- スナップショット間で観測した、列挙した41件以外の状態変更: **0件**。
- 最後に確認した変更時刻: **2026-10-02 21:41:19 JST**（12:41:19 UTC）。

## 再オープンした13件

| 警告ID | 型・メンバー | 件数 | 再判定の理由 |
| --- | --- | ---: | --- |
| 780–785 | `AnnouncementAttachmentResponse` の6プロパティ | 6 | サービス内の構築とDI登録は存在するが、実際の呼び出し元／HTTP・JSON出力経路を確認できない。型名だけでは誤検知の根拠にならない。 |
| 1098 | `FileDownloadResponse.SizeBytes` | 1 | JSON応答ではなくストリーム受け渡し用の型。ControllerはContent・ContentType・FileNameをファイル返却に利用するが、SizeBytesの利用を確認できない。 |
| 1776–1777 | `TenantFeatureResponse.IsEnabled` / `Key` | 2 | 本番ソースでは宣言だけが存在する。実際の`TenantFeaturesResponse`は文字列一覧を使い、この型を含まない。 |
| 1805–1808 | `TenantExportFileResponse` のCompletedAt・CreatedAt・Status・TenantId | 4 | ControllerはZIP本体を返す。別の`TenantExportJobResponse`がJSON化されることは、このストリーム用型のメタデータが利用される根拠にはならない。 |

これらの再オープンは、**前回の「誤検知」という断定の取り消し**であり、13件の動作不具合が確定したという意味ではない。API契約の削除やアプリケーションコードの変更はしていない。

その他の1,035件は179種類の出力型について、具体的なHTTP応答／JSONシリアライズ箇所から、入れ子の公開プロパティまでの経路を記録した。
`JsonIgnore`とgetterの公開範囲も照合し、接尾辞ではなくソース上の出力経路で判定を補強した。
CanonicalRedactionProjectionを通る応答は、許可された出力経路でCanonicalRedactionServiceが`SerializeToNode`を呼ぶ箇所まで確認している。

## 新たに誤検知として閉じた28件

| 警告ID | 検査 | 件数 | 根拠 |
| --- | --- | ---: | --- |
| 142–162 | `AutoPropertyCanBeMadeGetOnly.Global` | 21 | Tenancy・監査Export Worker・Announcement Publisher・Deadline Digest Worker・Realtimeの設定。実際のConfigure登録と各値の利用先があり、setter削除で設定値の上書きが失われる。 |
| 459–460 | `MemberCanBePrivate.Global` | 2 | Deadline DigestのCandidatePageSizeとRetrySeconds。公開プロパティとして構成バインダーから設定されるため、private化は適切でない。 |
| 1651–1655 | `NotAccessedPositionalProperty.Global` | 5 | `TaskSubresourceSummary`。サービスが非nullの集計値を構築し、`CanonicalTaskResponse.Subresources`に格納してControllerの`Ok(result.Value)`からJSON化する。 |

主なソース根拠:
- 設定登録: `src/Coglatas.Web/Program.cs:70-72`、`src/Coglatas.Web/Extensions/DependencyInjection.cs:30-32`。
- タスク集計値の構築と受け渡し: `src/Coglatas.Application/Projects/TaskCommandService.cs:1297-1298`。
- タスク応答のシリアライズ境界: `src/Coglatas.Web/Controllers/ProjectsController.cs:99-100,374-376`。
- 警告IDごとの宣言・実際の読み出し箇所は実行マニフェストに保存した。

## Openを維持した1,184件の分類

| 分類 | 件数 | 扱い |
| --- | ---: | --- |
| 変更可能性・可視性の設計判断 | 677 | init-only 597件、残りのget-only 45件、private化35件。Entity／DTOであることだけを理由に誤検知とはしない。 |
| スタイル・簡略化の提案 | 186 | パターン統合、コレクション式、命名等。リファクタリング候補として維持。 |
| 未使用・契約・呼び出し経路の確認 | 146 | 未使用メンバー、引数、戻り値、内部レコード等。暗黙の利用が裏付けられないものは閉じない。 |
| enum値の互換性確認 | 105 | DB値・通信値・別名・将来用予約値を区別する必要がある。未使用表示だけで削除しない。 |
| Nullable注釈と入力境界の確認 | 34 | JSON入力や不正値への防御を、注釈だけで削除しない。境界テストが必要。 |
| 非同期・キャンセル・破棄順序の確認 | 18 | 非同期化で順序や寿命が変わる可能性があるため、機械的に修正しない。 |
| テストのassertion／前提条件の確認 | 18 | 検証用引数・fakeの意図を確認する。テストの検証を消して警告を減らさない。 |
| 合計 | 1,184 | すべてにID別のソース位置・分類・維持理由を記録。 |

再オープンした13件を加え、現在のOpenは1,197件である。**1,197件すべてが確定したバグという意味ではない。**

機能上の確認を優先する候補は、`ApiTokenValidationResult`のTenantId／Scopes等（1231–1234）、`FileStorageOptions`の未読getter（2935・2973・2974）、`FeatureOptions`とその未使用フラグ（216・3121–3130）、未読の`IClock`依存（1957）。
未使用表示だけで認可の欠陥や機能の故障を断定はしない。未接続の機能なのか、現在の呼び出し経路に必要な情報が落ちているのかを、次の実装レビューで区別する対象である。

## 監査方法と限界

読み取り専用でOpen・Dismissed・Fixedのスナップショットと、コミット固定のソースを取得した。
独立したRoslynインデクサーで720個のC#ファイルを解析し、構文エラーは0件だった。
アプリケーションのプロジェクトはビルドも実行もしていない。
全対象IDの位置、診断、宣言コンテキストと、必要な出力経路を照合し、台帳生成時に287ファイルのSHA-256を検証した。

RoslynインデックスはSDKの参照を使った**合成コンパイル**であり、元の全NuGet参照を解決したプロジェクト単位の解析ではない。
未解決依存や参照欠落があり得るため、参照件数0を「絶対に未使用」の証明とは扱っていない。
一部のField宣言のインデックスは包含型を返すため、台帳では診断と該当宣言行からフィールド名を補い、包含型への参照をフィールドの利用証拠には使っていない。

本監査で完了したのは、全2,260件の棚卸し・ソース対応付け・分類、前回の1,048件のシリアライズ根拠の再監査、および確実な41件の状態訂正である。
**すべてのOpen警告の修正可否や、アプリケーション全体の動作安全性まで確定したわけではない。**
新しいQodanaスキャン、アプリの.NETビルド、統合テストはこの監査では実行していない。
前回から存在する、それ以外のDismiss済み128件は今回指定された1,048件の再監査範囲には含めていない。

## 適用の安全策と検証

既存の`apply-reviewed-qodana-alerts.py`をGit blob SHA固定で再利用し、今回の41件だけを列挙した承認コードを追加した。
宣言・根拠24ファイルのSHA-256、警告ID、ルール、パス、行、診断文、ツール、解析カテゴリ、解析コミットを照合し、全件の事前検証が通るまで書き込まない。
再オープンンは前回のbotと判定コメントが一致するものに限定する。
各PATCHの直前にもmainのSHAを確認し、更新後に別GETで状態を確認する。

通常のpushは読み取り専用inventoryのみ。書き込み権限のjobは承認済みの完全一致コミットメッセージでのみ実行され、applierでも同じ条件を確認する。
再実行時は適用済み状態を再変更しない。

ローカルでは16検証を通過した。内容は41件のfake API適用、再適用の書き込み0件、ID・ツール・ルール・ref・カテゴリ・解析SHA・パス・行・診断・前回の判定理由／判定者／コメントの改ざん拒否、重複承認の拒否、ソース改ざんの拒否。
これはapplierの検証であり、アプリケーションのテスト成功件数ではない。

最初の適用用ワークフローはYAML条件式の構文エラーでjob開始前に停止した（run 37007728917）。
修正後のrun 37007931193ではinventory・apply-reviewedとも成功し、全41件の反映を確認した。

## コミット・実行証拠

- 解析・インデックス対象: `9b78b24467a5a2dbc1ffd882bc4e049ff3f6aa78`。
- 適用時main: `32d62663e9b0b7f944ca216be5b4a41cee603a34`。
- この2コミットの差分は配置・CI・文書等で、対象C#ソースに変更がないことを比較で確認。24ファイルのハッシュも実際の適用時checkoutで一致。
- 全件インデックスrun: [37005393730](https://github.com/NYGsatoshi/Coglatas/actions/runs/37005393730)。
- インデックスartifact: 11225403967 / SHA-256 `0e0a15b0854cb9b97aff78dbb2e2fba9012a2d830c9d0ae02c514a2f48c27840`。
- 適用コミット: `d901688f6d2e3d64d239a26268cb8bb703966bc0`。
- 成功した適用run: [37007931193](https://github.com/NYGsatoshi/Coglatas/actions/runs/37007931193)。
- 適用job: 110840665804。
- [適用前artifact 11225994677](https://github.com/NYGsatoshi/Coglatas/actions/runs/37007931193/artifacts/11225994677): SHA-256 `676014e8b7efbfef95220727c8c15d89c0a8bd58ddfdcfc5d3878758c9181839`。
- [結果artifact 11227145337](https://github.com/NYGsatoshi/Coglatas/actions/runs/37007931193/artifacts/11227145337): SHA-256 `4e408b9c52914cc745089c0e6577f1f0024559920a69083b2424673b73bbbac5`。
- 実行マニフェストSHA-256: `dd61fbe80e3e048ad32b42d50c73afb5f9560f916bf90c650e70d6e6de07a295`。
- 配布JSON台帳SHA-256: `4aec1c895e9f7c9ee2cf21565c0c8f695da77459425adaa161acdeffd7549bc0`。
- GitHub Actionsのartifact保持期間は14日。会話で配布する監査一式ZIPには、再生成に必要なインデックスartifactと結果artifactも同梱する。

## 成果物と変更範囲

ブランチには本報告、読み取り専用Roslynインデクサー、今回41件の明示的な承認コード、適用を限定するworkflowを記録した。
元のbatch 2判定マニフェストとapplierを保持した。
アプリケーションソース、Qodanaルール設定、inspection baseline、mainには今回の監査変更を加えていない。ブランチは未マージ。

会話で配布する成果物:
- `qodana-full-audit-ledger.json`: 全2,260件の診断、初期状態、判定、ソースコンテキスト、根拠。
- `qodana-full-audit-ledger.md`: 全件の閲覧用一覧。
- `reviewed-output-type-proofs.json`: 179種類の出力型へのソース上の経路。
- `correction-evidence.json` / `executed-review-manifest.json`: 41件の訂正根拠・実際の承認内容。
- `review-receipt.json` / `verification.json`: 実際の反映結果と独立照合。
- `reproduce-audit-ledger.py`: 標準Pythonだけで台帳を再生成するスクリプト。

技術上の判断は、Microsoftの[Options pattern](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options?view=aspnetcore-10.0)、[System.Text.Jsonのプロパティ選択](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/ignore-properties)、JetBrainsの[get-only検査説明](https://www.jetbrains.com/help/resharper/AutoPropertyCanBeMadeGetOnly.Global.html)も参照した。これらの一般仕様だけで個別警告を閉じず、上記の具体的な登録・利用経路と併せて判断した。
