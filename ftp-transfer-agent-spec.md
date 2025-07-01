# FtpTransferAgent 詳細仕様書

## 1. 概要

FtpTransferAgent は、指定したローカルフォルダ内のファイルを FTP または SFTP サーバーに一括転送する .NET 8 ベースのコンソールアプリケーションです。**バッチ処理型**として設計されており、起動時に処理対象ファイルを検出し、設定に従って転送処理を実行後、自動的に終了します。転送後はハッシュ値による整合性チェックを行い、必要に応じて転送済みファイルの自動削除も可能です。双方向転送（アップロード/ダウンロード）にも対応しています。

### 主な特徴
- 🚀 **バッチ処理型**: 起動時に一回だけ処理を実行して終了（常駐アプリケーションではない）
- 📤📥 **FTP/SFTP 双方向転送**（アップロード/ダウンロード/両方向）
- 🔐 **SFTP 鍵認証対応**（パスワード認証と鍵認証の併用も可能）
- 🔒 **ハッシュ値による整合性検証**（MD5/SHA256）
- 🔄 **Polly による自動再試行機能**（指数バックオフ）
- 🗑️ **転送成功後の自動削除オプション**
- ⚡ **並列転送対応**（最大16ファイルの同時転送、Channel + TransferQueue 実装）
- 🎯 **ENDファイル制御機能**（ENDファイルがある場合のみ転送対象にする制御、順序保証付き）
- 📝 **ローリングファイルログ**（日付・サイズベースのローテーション）
- 📧 **エラー時のSMTPメール通知機能**
- ⚙️ **JSON による柔軟な設定**（DataAnnotations による検証）

### 動作モード
本ツールは**バッチ処理専用**のアプリケーションです。BackgroundService を継承していますが、ExecuteAsync メソッド内で一度だけ処理を実行し、完了後に `IHostApplicationLifetime.StopApplication()` を呼び出してアプリケーションを終了します。

**重要**: リアルタイムなフォルダ監視機能は実装されていません。`FolderWatcher` クラスは存在しますが、現在の実装では使用されていません。継続的な転送が必要な場合は、cron や Windows タスクスケジューラーでの定期実行を強く推奨します。

## 2. システム要件

### 必須要件
- **.NET 8 Runtime** または **.NET 8 SDK**
- **対応OS**: Windows、Linux、macOS
- **メモリ**: 最小 512MB（推奨 1GB 以上）
- **ネットワーク**: FTP/SFTP サーバーへのアクセス

### 依存パッケージ
- **FluentFTP 52.1.0** - FTP 通信ライブラリ
- **SSH.NET 2025.0.0** - SFTP 通信ライブラリ  
- **Polly 8.6.0** - 再試行処理ライブラリ
- **Microsoft.Extensions.Hosting 9.0.6** - ホスティングフレームワーク
- **Microsoft.Extensions.Options.DataAnnotations 9.0.6** - 設定検証

## 3. インストールと起動

### 3.1 ソースコードからのビルド

```bash
# リポジトリのクローン
git clone [repository-url]
cd FtpTransferAgent

# 依存関係の復元
dotnet restore

# ビルド（Release モード）
dotnet build --configuration Release

# 実行
dotnet run --project FtpTransferAgent
```

### 3.2 公開済みバイナリの作成と実行

```bash
# 自己完結型の実行ファイルを生成
dotnet publish -c Release -r win-x64 --self-contained
dotnet publish -c Release -r linux-x64 --self-contained
dotnet publish -c Release -r osx-x64 --self-contained

# 実行（Windows）
./FtpTransferAgent.exe

# 実行（Linux/macOS）
./FtpTransferAgent
```

### 3.3 定期実行の設定（強く推奨）

本アプリケーションは起動時に一度だけ処理を実行して終了するため、継続的な転送が必要な場合は必ず定期実行を設定してください。

#### Linux/macOS - cron による定期実行
```bash
# crontab を編集
crontab -e

# 5分ごとに実行
*/5 * * * * /opt/ftptransferagent/FtpTransferAgent

# 毎時0分に実行
0 * * * * /opt/ftptransferagent/FtpTransferAgent

# 重複実行防止（flock使用）
*/5 * * * * flock -n /var/lock/ftpagent.lock -c '/opt/ftptransferagent/FtpTransferAgent'

# エラー時のみメール通知
0 1,13 * * * /opt/ftptransferagent/FtpTransferAgent 2>&1 | grep -E "(Error|Critical)" | mail -s "FTP Transfer Error" admin@example.com
```

#### Windows - タスクスケジューラーによる定期実行
```powershell
# 5分間隔で実行するタスクを作成
schtasks /create /tn "FtpTransferAgent" /tr "C:\FtpTransferAgent\FtpTransferAgent.exe" /sc minute /mo 5

# PowerShell での詳細設定
$action = New-ScheduledTaskAction -Execute "C:\FtpTransferAgent\FtpTransferAgent.exe" -WorkingDirectory "C:\FtpTransferAgent"
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration ([TimeSpan]::MaxValue)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
Register-ScheduledTask -TaskName "FtpTransferAgent" -Action $action -Trigger $trigger -Settings $settings -User "SYSTEM" -RunLevel Highest
```

## 4. 設定ファイル（appsettings.json）

設定ファイルは **appsettings.json** という名前で、アプリケーションの実行ファイルと同じディレクトリに配置する必要があります。開発環境では **appsettings.Development.json** を使用することもできます。

### 4.1 設定ファイルの全体構造

```json
{
  "Watch": { ... },      // ローカルフォルダ設定
  "Transfer": { ... },   // 転送設定
  "Retry": { ... },      // 再試行設定
  "Hash": { ... },       // ハッシュ検証設定
  "Cleanup": { ... },    // クリーンアップ設定
  "Smtp": { ... },       // メール通知設定
  "Logging": { ... }     // ログ設定
}
```

### 4.2 各セクションの詳細

#### 4.2.1 Watch（ローカルフォルダ設定）

転送対象のローカルフォルダと条件を設定します。

```json
"Watch": {
  "Path": "./watch",
  "IncludeSubfolders": false,
  "AllowedExtensions": [".txt", ".csv", ".xml"],
  "RequireEndFile": false,
  "EndFileExtensions": [".END", ".end", ".TRG", ".trg"],
  "TransferEndFiles": false
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 |
|------|-----|------|------|--------------|
| Path | string | ✓ | 転送対象フォルダのパス（相対/絶対パス） | なし |
| IncludeSubfolders | boolean | - | サブフォルダも含めるか | false |
| AllowedExtensions | string[] | - | 転送対象の拡張子リスト（空の場合は全て）<br>ドット付きでもなしでも可 | [] |
| RequireEndFile | boolean | - | ENDファイルがある場合のみ転送対象にするか | false |
| EndFileExtensions | string[] | - | ENDファイルの拡張子リスト<br>ドット付きでもなしでも可 | [".END", ".end"] |

**重要**: 
- アプリケーション起動時に、このフォルダ内のファイルが一度だけ `Directory.EnumerateFiles` で処理されます
- **リアルタイムのフォルダ監視機能は実装されていません**
- 新しいファイルを転送するには、アプリケーションを再度実行する必要があります
- `FolderWatcher` クラスは存在しますが、現在の `Worker` 実装では使用されていません

**ENDファイル機能（RequireEndFile）の詳細**: 
- `RequireEndFile` が `true` の場合、対応するENDファイルが存在するファイルのみが転送対象になります
- 例：`test.txt` を転送するには `test.END` または `test.end` が同じディレクトリに存在する必要があります
- ENDファイルの拡張子は `EndFileExtensions` で設定可能（デフォルト: `.END`, `.end`）
- この機能は**アップロード処理（Direction: "put"）でのみ有効**で、ダウンロード処理では無視されます
- カスタム拡張子例：`.TRG`, `.trg`, `.DONE`, `.done` なども設定可能
- **ENDファイル自体は転送されません**（`IsEndFile`メソッドで自動判定・除外）
- **転送順序保証**: ファイルはアルファベット順でソートされ、ENDファイルが先に処理されることを防ぎます
- **セキュリティ検証**: 悪意のある拡張子（パストラバーサルや長すぎる拡張子）は設定時に警告されます

#### 4.2.2 Transfer（転送設定）

FTP/SFTP サーバーへの接続情報と転送設定です。

```json
"Transfer": {
  "Mode": "ftp",
  "Direction": "put",
  "Host": "ftp.example.com",
  "Port": 21,
  "Username": "ftpuser",
  "Password": "password",
  "PrivateKeyPath": null,
  "PrivateKeyPassphrase": null,
  "RemotePath": "/upload",
  "Concurrency": 1,
  "PreserveFolderStructure": false
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 | 制約 |
|------|-----|------|------|--------------|------|
| Mode | string | ✓ | 転送プロトコル | "ftp" | "ftp" または "sftp" |
| Direction | string | ✓ | 転送方向 | "put" | "get" / "put" / "both" |
| Host | string | ✓ | サーバーのホスト名/IPアドレス | なし | - |
| Port | integer | - | ポート番号 | 21 | - |
| Username | string | ✓ | ログインユーザー名 | なし | - |
| Password | string | △ | ログインパスワード<br>（FTP必須、SFTP条件付き） | null | - |
| PrivateKeyPath | string | - | SFTP 鍵認証で使用する秘密鍵パス | null | - |
| PrivateKeyPassphrase | string | - | 秘密鍵のパスフレーズ | null | - |
| RemotePath | string | ✓ | リモートの転送先パス | なし | - |
| Concurrency | integer | - | 同時転送数 | 1 | 1～16 |
| PreserveFolderStructure | boolean | - | サブフォルダの構成を保持してアップロード | false | - |

**転送方向（Direction）の詳細：**
- **"put"**: ローカル → リモート（Watch.Path 内のファイルをアップロード）
- **"get"**: リモート → ローカル（RemotePath 内のファイルを Watch.Path にダウンロード）
- **"both"**: 先に get を実行してから put を実行

**認証方式の制約（TransferOptions.Validate() メソッドで実装）：**
- **FTP モード**: Password が必須
- **SFTP モード**: Password または PrivateKeyPath のいずれかが必須（両方指定も可）

#### 4.2.3 Retry（再試行設定）

転送エラー時の再試行動作を設定します。Polly ライブラリの指数バックオフ機能を使用しています。

```json
"Retry": {
  "MaxAttempts": 3,
  "DelaySeconds": 5
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 | 制約 |
|------|-----|------|------|--------------|------|
| MaxAttempts | integer | - | 最大再試行回数 | 3 | 0以上 |
| DelaySeconds | integer | - | 初回再試行間隔（秒）<br>指数バックオフで次回以降は2倍ずつ増加 | 5 | 0以上 |

**実際の再試行間隔**: DelaySeconds × 2^(試行回数-1)

#### 4.2.4 Hash（ハッシュ検証設定）

転送後の整合性検証に使用するハッシュアルゴリズムと取得方法を設定します。

```json
"Hash": {
  "Algorithm": "MD5",
  "UseServerCommand": false
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 | 制約 |
|------|-----|------|------|--------------|------|
| Algorithm | string | ✓ | ハッシュアルゴリズム | "MD5" | "MD5" または "SHA256" |
| UseServerCommand | boolean | - | FTP サーバーのハッシュ計算コマンドを利用するか | false | - |

**重要**: 
- `UseServerCommand` が `false` の場合、サーバー側でハッシュ計算コマンドを使用せず、常にファイルをダウンロードしてローカルでハッシュを計算します
- SFTP では常にファイルストリームを取得してローカル計算が行われ、この設定は無視されます
- 現在の実装では確実性を重視し、デフォルトで `UseServerCommand: false` となっています

#### 4.2.5 Cleanup（クリーンアップ設定）

転送成功後のファイル削除に関する設定を行います。

```json
"Cleanup": {
  "DeleteAfterVerify": false,
  "DeleteRemoteAfterDownload": false
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 |
|------|-----|------|------|--------------|
| DeleteAfterVerify | boolean | - | アップロード＆検証成功後にローカルファイルを削除 | false |
| DeleteRemoteAfterDownload | boolean | - | ダウンロード＆検証成功後にリモートファイルを削除 | false |

#### 4.2.6 Smtp（メール通知設定）

エラー発生時のメール通知を設定します。LogLevel.Error 以上のログが発生した際にメールが送信されます。

```json
"Smtp": {
  "Enabled": false,
  "RelayHost": "smtp.example.com",
  "RelayPort": 25,
  "UseTls": false,
  "Username": "",
  "Password": "",
  "From": "noreply@example.com",
  "To": ["admin@example.com"]
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 |
|------|-----|------|------|--------------|
| Enabled | boolean | - | メール通知を有効にするか | false |
| RelayHost | string | - | SMTP サーバーのホスト名 | "" |
| RelayPort | integer | - | SMTP サーバーのポート番号 | 25 |
| UseTls | boolean | - | TLS/SSL を使用するか | false |
| Username | string | - | SMTP 認証のユーザー名 | "" |
| Password | string | - | SMTP 認証のパスワード | "" |
| From | string | △ | 送信元メールアドレス | "" |
| To | string[] | ✓ | 送信先メールアドレス（複数可） | [] |

#### 4.2.7 Logging（ログ設定）

ログ出力の詳細を設定します。

```json
"Logging": {
  "Level": "Information",
  "RollingFilePath": "logs/ftp-transfer-.log",
  "MaxBytes": 10485760
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 |
|------|-----|------|------|--------------|
| Level | string | - | 最小ログレベル | "Information" |
| RollingFilePath | string | - | ログファイルのパス<br>（空の場合はファイル出力なし） | "" |
| MaxBytes | long | - | ログファイルの最大サイズ（バイト） | 10485760 (10MB) |

**ログレベル**: Trace / Debug / Information / Warning / Error / Critical

**ローリングログの動作**:
- 日付ごとにファイルが作成される（例：`ftp-transfer-20250614.log`）
- 同日内でファイルサイズが MaxBytes を超えると、新しいファイルが作成される（例：`ftp-transfer-20250614_1.log`）

## 5. 動作仕様

### 5.1 基本的な処理フロー

Worker.cs の ExecuteAsync メソッドで実装されている処理フローは以下の通りです：

1. **起動時の初期化**
   - 設定ファイル（appsettings.json）の読み込みと DataAnnotations による検証
   - ログシステムの初期化（コンソール + ローリングファイル + メール通知）
   - FTP/SFTP クライアントの初期化（`CreateClient()` メソッド）
   - 転送キューの初期化（`Channel<TransferItem>` + `TransferQueue`）

2. **ファイル列挙**（Direction に応じて実行）
   - **"put" モード**: Watch.Path 内のファイルを `Directory.EnumerateFiles` で列挙
     - ファイルはアルファベット順でソートされ、転送順序が安定化される
     - ENDファイル機能が有効な場合、ENDファイル自体は除外される
     - ENDファイル検証が有効な場合、対応するENDファイルが存在するファイルのみが対象
   - **"get" モード**: RemotePath 内のファイルリストを `client.ListFilesAsync` で取得
   - **"both" モード**: 先に get のファイル列挙、その後 put のファイル列挙
   - 拡張子フィルターが設定されている場合は、該当するファイルのみを対象とする

3. **キューへの登録**
   - 列挙されたファイルを `Channel<TransferItem>` に登録
   - 各ファイルには転送方向（Upload/Download）が設定される
   - `channel.Writer.Complete()` で登録完了を通知

4. **転送処理**（並列実行、TransferQueue 実装）
   - 指定された並列度（Concurrency）でワーカースレッドが起動
   - 各ワーカーはキューからファイルを取得して転送
   - 重複処理防止機能（`ConcurrentDictionary<string, bool>` で管理）
   - 一時ファイル名（`.tmp.{GUID}`）で転送し、完了後に正式なファイル名にリネーム
   - 各転送には固有の ID (GUID) が割り当てられ、ログで追跡可能

5. **検証処理**
   - ローカルファイルのハッシュ値計算（`HashUtil.ComputeHashAsync`）
   - リモートファイルのハッシュ値取得
     - FTP: サーバーコマンド または ストリーム取得してローカル計算
     - SFTP: 常にストリーム取得してローカル計算
   - ハッシュ値の比較（大文字小文字を無視、`StringComparison.OrdinalIgnoreCase`）

6. **後処理**
   - 検証成功時：
     - アップロード時：設定に応じてローカルファイルを削除
     - ダウンロード時：設定に応じてリモートファイルを削除
   - 検証失敗時：エラーログを出力（メール通知も送信）

7. **終了**
   - すべての転送処理が完了したら `_lifetime.StopApplication()` を呼び出し
   - アプリケーションが自動的に終了

### 5.2 並列転送の仕組み（Channel + TransferQueue）

`Transfer.Concurrency` で指定された数のワーカースレッドが起動し、`Channel<TransferItem>` からファイルを取得して並列に転送処理を行います。

```
Channel<TransferItem> → TransferQueue → ワーカー1 → FTP/SFTP転送
                                    → ワーカー2 → FTP/SFTP転送
                                    → ワーカー3 → FTP/SFTP転送
                                    → ワーカー4 → FTP/SFTP転送
```

**実装の特徴：**
- **重複処理防止**: `ConcurrentDictionary` で処理済みアイテムを管理
- **Polly 再試行**: 指数バックオフによる再試行処理
- **Graceful エラーハンドリング**: 一部のファイルが失敗しても他の処理は継続

### 5.3 エラーハンドリングと再試行

#### 自動再試行が行われるエラー（Polly Policy）
- ネットワーク接続エラー
- FTP/SFTP サーバーの一時的な応答エラー
- ファイルアクセスエラー（他プロセスによるロック等）
- タイムアウトエラー

#### 再試行されないエラー
- 設定ファイルの検証エラー（起動時に失敗）
- 認証エラー（ユーザー名/パスワード不正）
- 権限エラー（書き込み権限なし）
- ディスク容量不足

## 6. 実装の詳細と制限事項

### 6.1 現在の実装状況
- ✅ **転送方向**: "get"/"put"/"both" の 3 つをサポート
- ✅ **並列転送**: 最大 16 並列まで対応（TransferQueue + Channel 実装）
- ✅ **ローリングログ**: 日付とサイズベースのローテーション実装済み
- ✅ **SMTP 通知**: エラーログをメールで送信
- ✅ **SFTP 鍵認証**: パスフレーズ付き鍵ファイルにも対応
- ✅ **一括転送**: 起動時の一括転送処理（バッチ型）
- ❌ **リアルタイム監視**: FolderWatcher クラスは存在するが Worker では未使用
- ❌ **転送の中断/再開**: 未対応
- ❌ **転送履歴の永続化**: 未対応
- ❌ **重複チェック**: 同じファイルを再度転送する可能性あり

### 6.2 既知の制限
- **バッチ処理専用**: アプリケーションは一度の実行で処理を完了して終了する（常駐しない）
- **リアルタイム監視なし**: 新しいファイルを転送するには再度実行する必要がある
- **FolderWatcher 未使用**: クラスは実装されているが、実際には使用されていない
- **SFTP ハッシュ取得**: ファイル全体のダウンロードが必要（プロトコルの制限）
- **同時接続数制限**: サーバー側の制限に依存

### 6.3 推奨される使用方法
- **定期実行**: cron や Windows タスクスケジューラーで定期的に実行（**強く推奨**）
- **ワークフロー統合**: 他のバッチ処理の一部として組み込む
- **イベントドリブン**: ファイル生成プロセスの完了後に実行
- **重複転送の回避**: 転送済みファイルを別フォルダに移動するなどの運用上の工夫

## 7. トラブルシューティング

### 7.1 よくある問題と解決方法

#### ファイルが転送されない
- **原因1**: 拡張子フィルターに一致しない → `AllowedExtensions` を確認
- **原因2**: フォルダパスが間違っている → `Watch.Path` を確認
- **原因3**: 前回の実行で既に転送済み → 本アプリケーションは起動時に存在するファイルのみを処理

#### 転送エラーが発生する
- **原因1**: ネットワーク接続の問題 → FTP/SFTP サーバーへの接続を確認
- **原因2**: 認証情報が間違っている → ユーザー名とパスワード/鍵ファイルを確認
- **原因3**: 並列接続数が多すぎる → `Concurrency` を減らす

#### ハッシュ検証エラー
- **原因1**: FTP サーバーがハッシュコマンドをサポートしていない → `Hash.UseServerCommand` を `false` に設定

### 7.2 デバッグモード

開発環境では `DOTNET_ENVIRONMENT=Development` を設定することで、より詳細なログが出力されます。

```bash
# Windows
set DOTNET_ENVIRONMENT=Development
FtpTransferAgent.exe

# Linux/macOS
export DOTNET_ENVIRONMENT=Development
./FtpTransferAgent
```

## 8. セキュリティに関する注意事項

### 8.1 認証情報の管理
- 設定ファイルにパスワードを平文で保存することは推奨されません
- 環境変数や秘密管理ツールの使用を検討してください
- SFTP では鍵認証の使用を強く推奨

### 8.2 ファイル権限
```bash
# 設定ファイルの保護
chmod 600 appsettings.json

# 秘密鍵ファイルの保護
chmod 600 id_ed25519
```

### 8.3 通信の暗号化
- FTP は暗号化されていないため、機密データには SFTP の使用を推奨
- SMTP でメール送信する場合は `UseTls: true` を推奨

## 9. 今後の開発予定

以下の機能は将来的な実装を検討しています：

1. **リアルタイムフォルダ監視**
   - FolderWatcher クラスを活用した継続的な監視機能
   - ファイル作成・変更時の即時転送

2. **転送履歴の管理**
   - 転送済みファイルのデータベース記録
   - 重複転送の防止機能

3. **Web UI / API**
   - 転送状況の確認
   - 設定の動的変更
   - 転送履歴の参照

4. **高度な転送制御**
   - 転送の一時停止・再開
   - 優先度付きキュー
   - 帯域制限機能

## 10. ライセンス

本ソフトウェアは MIT ライセンスの下で公開されています。

---

**更新日**: 2025年6月22日  
**バージョン**: 2.1.2  
**主な更新内容**:
- 現在の実装（Worker.cs, TransferQueue.cs等）に合わせて仕様を全面的に見直し
- FolderWatcher が未使用であることを明確化
- バッチ処理専用アプリケーションとしての特性を強調
- Channel + TransferQueue による並列処理の詳細を追加
- 定期実行での使用を前提とした説明に変更
- 実装されていない機能と制限事項を正確に記載
