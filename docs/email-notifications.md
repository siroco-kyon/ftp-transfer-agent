# エラーメールの設定と運用

### エラーメールの件名と実行元・転送先の表示

複数のサーバーに配置する場合は、業務名と実行元サーバー名を件名に表示できます。転送先・取得元が分かるエラーでは、その名前と失敗内容も表示します。本番では環境の文字を付けず、検証環境だけ接頭辞を付けられます。

#### 最小設定と件名の例

すでに SMTP 接続・宛先を設定している場合は、`Smtp` に業務名を追加するだけで新形式に切り替わります。

```json
"JobName": "売上データ連携"
```

実行元の OS ホスト名が `APP01`、失敗した転送先の `DisplayName` が「大阪FTP」の場合、件名は次のようになります。

```text
[売上データ連携][実行元: APP01] 大阪FTPへ転送失敗
```

`From` は送信元メールアドレス、`RelayHost` はメールを中継する SMTP サーバーです。どちらもツールが動いているサーバー名ではありません。実行元には `SourceServerName` または OS ホスト名を使います。

#### 設定例

既存の `appsettings.json` の該当箇所へ追加・変更する例です。転送先の認証情報や監視フォルダーなど、ここにない既存設定は引き続き必要です。

```json
{
  "Smtp": {
    "Enabled": true,
    "RelayHost": "smtp.example.com",
    "RelayPort": 25,
    "UseTls": false,
    "From": "agent@example.com",
    "To": ["operations@example.com"],
    "JobName": "売上データ連携",
    "SubjectPrefix": "",
    "SourceServerName": "",
    "SubjectTemplate": "[{JobName}][実行元: {SourceServerName}] {Summary}"
  },
  "Transfer": {
    "Name": "primary",
    "DisplayName": "大阪FTP",
    "Host": "ftp-osaka.example.com",
    "AdditionalDestinations": [
      {
        "Name": "tokyo",
        "DisplayName": "東京SFTP",
        "Mode": "sftp",
        "Host": "sftp-tokyo.example.com",
        "Port": 22
      }
    ]
  }
}
```

#### 設定項目と省略時の動作

| 設定 | 型 | 省略・空欄の場合 | 用途 |
|---|---|---|---|
| `Smtp.JobName` | string | 新形式では `FtpTransferAgent` | 業務を識別する名前 |
| `Smtp.SubjectPrefix` | string | 何も付けない | `検証` などの目印。角括弧は自動で付くため、値には含めない |
| `Smtp.SourceServerName` | string | OS のホスト名 | ツールの実行元。分かりやすい表示名で上書きできる |
| `Smtp.SubjectTemplate` | string | `[{JobName}][実行元: {SourceServerName}] {Summary}` | 件名の並び方。新形式への切替後に適用する既定値 |
| `Transfer.DisplayName` | string | `Name`、それも空なら FTP/SFTP は `Host`、local は `RemotePath` | メイン転送先のメール用表示名 |
| `Transfer.AdditionalDestinations[].DisplayName` | string | 各宛先の `Name`、次に `Host` または `RemotePath` | 追加転送先のメール用表示名 |

4つの `Smtp` 件名設定がすべて未指定・空文字・空白のみなら、従来の `[ログカテゴリ] Error` と既存の本文を維持します。いずれかに空白以外の文字を設定すると新形式になります。`DisplayName` だけの追加では新形式に切り替わりません。`Smtp.Enabled: false` の場合はメールを送りません。

`Name` は配信済み状態を管理する識別子、`DisplayName` はメールに表示する名前です。表示だけを変える場合は `DisplayName` を変更してください。`Name` の変更は配信管理に影響します。`DisplayName` を変更しても配信済み記録・転送先・再送対象は変わりません。同じ表示名を複数宛先に使うと件名では区別できないため、異なる名前を推奨します。本文には接続先ホスト・ポート・パスも載ります。

#### 検証環境と実行元の表示名

本番は `SubjectPrefix` を空欄にします。検証環境では `Smtp.SubjectPrefix` を `検証` にすると、次の件名になります。

```text
[検証][売上データ連携][実行元: TEST01] 大阪FTPへ転送失敗
```

`Smtp.SourceServerName` を `大阪・連携サーバー01` に設定した例です。

```text
[売上データ連携][実行元: 大阪・連携サーバー01] 大阪FTPへ転送失敗
```

OS ホスト名を使えば、サーバーごとに実行元名を書き換える必要はありません。表示名を指定する場合は、配置先に合わせて設定してください。環境変数からは `Smtp__JobName`、`Smtp__SubjectPrefix`、`Smtp__SourceServerName`、`Smtp__SubjectTemplate` でも上書きできます。`DOTNET_ENVIRONMENT` から接頭辞を自動設定することはありません。開発用設定では `appsettings.Development.json` に接頭辞だけを上書きできます。

#### テンプレートと固定件名

置換できる変数は、大文字小文字も含めて `{JobName}`、`{SourceServerName}`、`{Summary}` の3つです。`{Summary}` はアプリが処理の情報から作ります。次の例のように並び順を変更できます。

```json
"SubjectTemplate": "{Summary} / {JobName} / {SourceServerName}"
```

固定件名も指定できます。接頭辞を設定している場合は、固定件名にも付加します。

```json
"SubjectTemplate": "売上データ連携のエラー通知"
```

表示値に含まれる変数のような文字列は再展開しません。未知の変数、不正な波括弧、改行・制御文字を含むテンプレート、1,000文字を超えるテンプレートは警告を標準エラー出力に残して標準テンプレートに戻します。テンプレートの不備だけで転送処理を止めません。

表示値の改行・制御文字は空白へ置き換えます。件名は最大200文字で、超過分は末尾を `…` にして省略します。業務名・実行元・接続先・概要の詳細は本文で確認できます。テンプレートを変えても、本文の識別情報は残ります。

#### 送信・取得・部分配信の違い

| 状況 | 件名の例 |
|---|---|
| 大阪FTPへの送信失敗 | `[売上データ連携][実行元: APP01] 大阪FTPへ転送失敗` |
| 東京SFTPからの取得失敗（`Direction: get`） | `[売上データ連携][実行元: APP01] 東京SFTPからの取得失敗` |
| 一部の宛先へ未配信 | `[売上データ連携][実行元: APP01] 大阪FTPへ未配信` |
| 共有フォルダーへの送信失敗 | `[売上データ連携][実行元: APP01] 大阪共有へ転送失敗` |
| 宛先を特定できないエラー | `[売上データ連携][実行元: APP01] 転送処理でエラー` |

共有フォルダーでは `Mode: local`、`RemotePath: "\\\\fileserver\\sales"` とし、`DisplayName: "大阪共有"` を指定できます。`get` の接続先は取得元を意味し、使われない `AdditionalDestinations` は件名にも使いません。複数宛先の部分配信メールには、そのファイルの未配信先を列挙します。

本文の先頭には業務名・実行元・転送先または取得元・接続先・概要を表示し、続けて元のログと例外詳細を載せます。原因や対象を特定できない場合は推測せず、ログの詳細を確認できる形にします。

#### 通知条件と移行時の確認

今回の設定は通知の表示を変えるものです。Error 以上のログに対する送信、`MaxEmailsPerRun`、`SuppressPerDestinationFailureDetailEmails`、終了時の最大15秒の送信待機は従来どおりです。再試行途中の Error も引き続き通知対象です。実行全体を1通にまとめる機能や、未配信ファイルの件数集約は行いません。起動エラーを新たに通知する機能も追加していません。

移行時は、まず `JobName` と宛先の `DisplayName` を設定し、必要なら検証環境の接頭辞を追加してください。新しい件名に切り替わるため、受信側で件名を条件にしている振り分け・監視ルールも確認してください。従来の件名に戻す場合は、4つの `Smtp` 件名設定を削除するか、すべて空欄にします。
