# Riot Accounts Lite

Windows 11（x64）向けの、Riot アカウント管理の簡易版です。ログインIDとパスワードを暗号化して保管し、ボタンひとつでコピーできます。LoL のランクは OP.GG（非公式）から取得して表示します。

- Riot API には接続しません。APIキーは不要です。
- Web版（自宅サーバー）などの外部サーバーにも接続しません。通信先は OP.GG の `lol-api-summoner.op.gg` だけで、ランク更新のボタンを押したときだけ通信します。
- ログイン自動入力や戦績分析を含むフル版は [RIOT-ACCOUNT-MANAGER](https://github.com/ryuheitakeda/RIOT-ACCOUNT-MANAGER) です。

## 主な機能

- アカウントの登録・編集・削除（管理名、ログインID、パスワード、Riot ID、サーバー、メモ）
- ログインID・パスワードのコピー（パスワードは30秒後にクリップボードから消去）
- OP.GG からのランク取得（Solo/Duo・Flex の段位、LP、勝敗、勝率）と、取得時刻・OP.GG 側の更新時刻の表示
- 「OP.GGで開く」で、選択中アカウントの OP.GG ページをブラウザで開く

## フル版との違い

| | Lite（このリポジトリ） | フル版 |
|---|---|---|
| ログイン情報の保管・コピー | あり | あり |
| Riot クライアントへの自動入力 | なし | あり |
| ランクの取得元 | OP.GG（非公式）のみ | Riot API / OP.GG / Web版 |
| APIキー | 不要 | Riot API を使う場合は必要 |
| 試合履歴・分析・推移グラフ・次戦の参考ランク帯 | なし | あり |
| 並べ替え・検索・接続の診断 | なし（登録順） | あり |
| 保存先 | `%LOCALAPPDATA%\RiotAccountsLite` | `%LOCALAPPDATA%\RiotAccounts` |

2つのアプリはデータを共有せず、同じ PC で並べて使えます。フル版から Lite へのデータ移行機能はないため、Lite ではアカウントを登録し直してください。

## 使い方

1. `RiotAccountsLite-win-x64.zip` をフォルダごと展開し、`RiotAccountsLite.exe` を起動します。.NET ランタイムは同梱しています。
2. 「アカウントを追加」で管理名、ログインID、パスワード、Riot ID（名前とタグ）、サーバーを登録します。ログインIDとゲーム内の Riot ID は別の項目です。初期サーバーは JP1 です。任意でメモ（用途など）も残せます。
3. 詳細画面の「ログインIDをコピー」「パスワードをコピー」でコピーし、Riot クライアントのログイン画面に貼り付けます。パスワードは30秒後にクリップボードから消去します（その間に別の内容をコピーした場合は消去しません）。
4. 「ランク更新」で選択中のアカウントの、「全ランク更新」で全アカウントのランクを OP.GG から取得します。
   - 全ランク更新は登録順に1件ずつ取得します。見つからないアカウントがあっても残りを続け、取得できなかった理由を一覧に表示します。OP.GG の取得制限が続いたときと、接続できないときは中断します。
   - 「中止」で処理を止められます。それまでに取得したランクは保存されます。
5. 「OP.GGで開く」で、選択中アカウントの OP.GG ページを既定のブラウザで開きます。

一覧には保存済みの最新ランク（Solo/Duo、なければ Flex）を表示します。一覧の表示では通信しません。

## OP.GG について（非公式）

- OP.GG からの取得は**非公式**です（OP.GG が公開していない Web 用の通信を利用）。予告なく仕様変更・遮断されて使えなくなることがあります。OP.GG の利用規約上も、個人利用の範囲にとどめてください。
- OP.GG 側でデータが更新されていないと、最新のランクが含まれません。このアプリから OP.GG に更新は要求しません。詳細画面に OP.GG 側の最終更新時刻を表示するので、古いときは「OP.GGで開く」から OP.GG のページで更新してから、もう一度「ランク更新」を押してください。
- リクエストは1秒に1回以下に抑えます。取得制限（HTTP 429）のときは、指示された時間だけ待って再試行します（最大3回）。
- OP.GG では Riot ID を検索し、名前とタグが完全に一致したアカウントだけを使います。見つかった OP.GG 側の ID は保存して、次回から検索を省きます。保存した ID が使えなくなっていた場合は、一度だけ検索し直します。

## 保存先・暗号化

- `%LOCALAPPDATA%\RiotAccountsLite\accounts.db` に保存します。
- ログインID・パスワードは Windows DPAPI（CurrentUser）で暗号化し、登録した Windows ユーザーだけが復号できます。別のユーザーや PC にファイルだけを移しても復号できません。
- 管理名・Riot ID・メモ・ランクは暗号化しません。メモにログイン情報を書かないでください。
- 取得の記録（経路・HTTP コード・所要時間）を `logs\diagnostics.log` に残します。Riot ID・OP.GG 側の ID・ログイン情報は記録しません。保存先とログの場所は「このアプリについて」でも確認できます。
- バックアップ時はアプリを閉じて、保存先フォルダ全体をコピーしてください。アカウントを削除すると、そのアカウントのログイン情報と保存済みランクも削除します。

## 開発・検証

必要環境：.NET 10 SDK。Windows UI の動作検証は Windows で行います（WPF のプロジェクトは macOS でもビルドできます）。

```powershell
dotnet test tests/RiotAccountsLite.Core.Tests -c Release
dotnet build src/RiotAccountsLite.App -c Release
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

配布版のオフライン診断（ダミーの一時データと、OP.GG を模した応答だけを使います。実際には通信しません）：

```powershell
$p = Start-Process .\RiotAccountsLite.exe -ArgumentList '--self-test', 'smoke-results.txt' -Wait -PassThru
Get-Content .\smoke-results.txt
$p.ExitCode
```

自動検証の内容と手動確認の項目は `docs/windows-validation.md` を参照してください。

## ライセンス

[MIT License](LICENSE)

## 免責

Riot Games・OP.GG の公式アプリではなく、両社の承認・推奨を受けたものではありません。

Riot Accounts Lite isn't endorsed by Riot Games and doesn't reflect the views or opinions of Riot Games or anyone officially involved in producing or managing Riot Games properties. Riot Games, and all associated properties are trademarks or registered trademarks of Riot Games, Inc.
