# FtpTransferAgent

.NET 8 で構築されたバッチ型ファイル転送ツールで、指定フォルダ内のファイルを FTP/SFTP 経由で確実に転送します。

## 主な特徴

- 🚀 **バッチ処理型**: 起動時に一回だけ処理を実行して終了（常駐しない）
- 📤📥 **双方向転送**: アップロード・ダウンロード・両方向転送に対応
- 🔐 **SFTP 鍵認証**: パスワード認証と鍵認証の両方をサポート
- 🔒 **整合性検証**: SHA256/SHA512/MD5 ハッシュによる転送後検証
- 🔄 **自動再試行**: Polly による指数バックオフ再試行
- ⚡ **並列転送**: 最大16ファイルの同時転送
- 🎯 **ENDファイル制御**: ENDファイルがある場合のみ転送する制御機能（Put/Get両対応、順序保証付き）
- 📤 **ENDファイル転送**: ENDファイル自体の転送制御と順序保証（Put/Get両対応、TransferEndFiles）
- 📝 **ローリングログ**: 日付・サイズベースのログローテーション
- 📧 **エラー通知**: SMTP によるエラーメール送信
- 🗑️ **自動削除**: 転送成功後の任意のファイル削除

## システム要件

- .NET 8 Runtime または SDK
- 対応OS: Windows、Linux、macOS
- メモリ: 最小 512MB（推奨 1GB 以上）

## クイックスタート

### 1. ビルドと実行

```bash
# 依存関係の復元
dotnet restore

# ビルド
dotnet build --configuration Release

# 実行
dotnet run --project FtpTransferAgent
```

### 2. 設定ファイルの作成

`appsettings.json` を作成して転送設定を記述：

```json
{
  "Watch": {
    "Path": "./watch",
    "IncludeSubfolders": false,
    "AllowedExtensions": [".txt", ".csv"],
    "RequireEndFile": false,
    "TransferEndFiles": true,
    "EndFileExtensions": [".END", ".end", ".TRG", ".trg"]
  },
  "Transfer": {
    "Mode": "ftp",
    "Direction": "put",
    "Host": "ftp.example.com",
    "Port": 21,
    "Username": "user",
    "Password": "password",
    "RemotePath": "/upload",
    "Concurrency": 2,
    "PreserveFolderStructure": false
  },
  "Hash": {
    "Algorithm": "SHA256"
  },
  "Cleanup": {
    "DeleteAfterVerify": false,
    "DeleteRemoteAfterDownload": false,
    "DeleteRemoteEndFiles": false
  }
}
```

### 3. 定期実行の設定（推奨）

本ツールはバッチ処理型のため、継続的な転送には定期実行を設定してください。

**Linux/macOS (cron):**
```bash
# 5分ごとに実行
*/5 * * * * /path/to/FtpTransferAgent

# 毎時0分に実行  
0 * * * * /path/to/FtpTransferAgent
```

**Windows (タスクスケジューラー):**
```powershell
# 5分間隔で実行するタスクを作成
schtasks /create /tn "FtpTransferAgent" /tr "C:\path\to\FtpTransferAgent.exe" /sc minute /mo 5
```

## 設定項目

### 転送設定（Transfer）

| 項目 | 必須 | 説明 | 例 |
|------|------|------|-----|
| Mode | ✓ | プロトコル ("ftp" または "sftp") | "sftp" |
| Direction | ✓ | 転送方向 ("get"/"put"/"both") | "both" |
| Host | ✓ | サーバーホスト名 | "ftp.example.com" |
| Username | ✓ | ユーザー名 | "ftpuser" |
| Password | △ | パスワード (FTP必須、SFTP条件付き) | "password" |
| PrivateKeyPath | - | SFTP秘密鍵ファイルパス | "./id_ed25519" |
| RemotePath | ✓ | リモートディレクトリ | "/upload" |
| Concurrency | - | 並列転送数 (1-16) | 4 |
| PreserveFolderStructure | - | サブフォルダ構造を維持して転送（`IncludeSubfolders: true` と併用推奨） | true |

### SFTP 鍵認証の設定

```bash
# 鍵ペアの生成
ssh-keygen -t ed25519 -f id_ed25519

# 公開鍵をサーバーに登録
ssh-copy-id -i id_ed25519.pub user@server

# 設定ファイルで秘密鍵を指定
"PrivateKeyPath": "./id_ed25519"
```

### その他の主要設定

```json
{
  "Retry": {
    "MaxAttempts": 3,        // 最大再試行回数
    "DelaySeconds": 5        // 再試行間隔（秒）
  },
  "Hash": {
    "Algorithm": "SHA256",   // ハッシュアルゴリズム (MD5/SHA256/SHA512)
    "UseServerCommand": false // サーバーコマンド使用フラグ
  },
  "Cleanup": {
    "DeleteAfterVerify": true,           // アップロード成功後ローカル削除
    "DeleteRemoteAfterDownload": false,  // ダウンロード成功後リモート削除
    "DeleteRemoteEndFiles": false        // 転送先ENDファイルの削除制御
  },
  "Logging": {
    "Level": "Information",              // ログレベル
    "RollingFilePath": "logs/app-.log",  // ログファイルパス
    "MaxBytes": 10485760                 // ローテーションサイズ
  }
}
```

### 同名ファイル衝突の警告（重要）

`Watch.IncludeSubfolders: true` かつ `Transfer.PreserveFolderStructure: false` の場合、サブフォルダが無視され、ファイル名だけで転送されます。

- 例:
  - `watch/A/result.csv`
  - `watch/B/result.csv`
- アップロード時: どちらも `/remote/result.csv` になり、後から転送された方で上書きされる可能性があります
- ダウンロード時: どちらも `watch/result.csv` になり、上書きされる可能性があります

この組み合わせでは、設定検証時に次の警告が表示されます。

- `IncludeSubfolders is enabled for upload but PreserveFolderStructure is disabled. All files will be uploaded to remote root directory.`
- `IncludeSubfolders is enabled for download but PreserveFolderStructure is disabled. Files from subdirectories will be saved to root directory and may overwrite each other.`

推奨設定（サブフォルダを使う場合）:

```json
{
  "Watch": {
    "IncludeSubfolders": true
  },
  "Transfer": {
    "PreserveFolderStructure": true
  }
}
```

意図的に `PreserveFolderStructure: false` を使う場合は、サブフォルダを含めた全体でファイル名が重複しない運用にしてください。

## 動作フロー

1. **起動時**: 設定ファイル読み込み、FTP/SFTPクライアント初期化
2. **ファイル列挙**: Watch.Path内のファイル検出（アルファベット順ソート）、またはリモートファイル一覧取得
3. **フィルタリング**: 拡張子フィルター、ENDファイル制御による対象ファイル絞り込み
4. **2段階キューイング**: データファイルを先に転送キューに追加、その後でENDファイルを追加（TransferEndFiles有効時）
5. **並列転送**: 指定並列度でファイル転送実行（パフォーマンス監視付き）
6. **整合性検証**: ハッシュ値比較による転送結果検証
7. **後処理**: 設定に応じてファイル削除（転送元ENDファイルは転送成功後自動削除、転送先ENDファイルは設定で制御）
8. **終了**: 全処理完了後に監視タスクを停止してアプリケーション終了

## テスト

統合テストでは Python の FTP サーバーを使用します。

```bash
# Python FTP サーバーのインストール
pip install pyftpdlib

# テスト実行
dotnet test --configuration Release --verbosity normal
```

## 公開・配布

```bash
# プラットフォーム別の自己完結型実行ファイル作成
dotnet publish -c Release -r win-x64 --self-contained
dotnet publish -c Release -r linux-x64 --self-contained
dotnet publish -c Release -r osx-x64 --self-contained
```

## ENDファイル制御機能

ENDファイル機能を使用すると、対応するENDファイルが存在する場合のみファイルを転送対象にできます。

### 設定方法

```json
{
  "Watch": {
    "RequireEndFile": true,
    "EndFileExtensions": [".END", ".end", ".TRG", ".trg"],
    "TransferEndFiles": false
  }
}
```

### 基本動作例（TransferEndFiles: false）

以下のファイルがある場合：
- `data1.txt` + `data1.txt.END` → `data1.txt` が転送される（ENDファイルは転送されない）
- `data2.txt` + `data2.txt.end` → `data2.txt` が転送される（ENDファイルは転送されない）
- `data3.txt` + `data3.txt.TRG` → `data3.txt` が転送される（ENDファイルは転送されない）
- `data4.txt` （ENDファイルなし） → 転送されない

### ENDファイル転送機能（TransferEndFiles: true）

```json
{
  "Watch": {
    "RequireEndFile": true,
    "TransferEndFiles": true,
    "EndFileExtensions": [".END", ".end"]
  }
}
```

以下のファイルがある場合：
- `data1.txt` + `data1.txt.END` → `data1.txt` を先に転送、その後 `data1.txt.END` を転送
- `data2.txt` + `data2.txt.end` → `data2.txt` を先に転送、その後 `data2.txt.end` を転送
- `data3.txt` （ENDファイルなし） → 転送されない
- `orphan.END` （対応するデータファイルなし） → 転送されない

### 重要な特徴

- この機能は**アップロード（Direction: "put"）とダウンロード（Direction: "get"）の両方で有効**です
- 双方向転送（Direction: "both"）でも適切に動作します
- **転送順序保証**: データファイルが必ずENDファイルより先に転送されます（Put/Get両対応）
- **2段階キューイング**: データファイルを先にキューに追加し、その後でENDファイルを追加
- **対応関係チェック**: ENDファイルは対応するデータファイルが転送される場合のみ転送されます
- **自動削除機能**: 転送元ENDファイルは転送成功後に自動的に削除、転送先ENDファイルはDeleteRemoteEndFiles設定で制御（通常ファイルはDeleteAfterVerify設定に従う）
- **パス検証強化**: ダウンロード時のパストラバーサル攻撃対策を実装
- **設定検証**: `TransferEndFiles`が有効でも`RequireEndFile`が無効の場合は警告が表示されます
- **セキュリティ検証**: 悪意のある拡張子やパストラバーサルは設定・実行時に検証されます
- **アプリケーション終了**: 処理完了後にパフォーマンス監視タスクを適切に終了してアプリケーションが正常終了
- カスタム拡張子は `EndFileExtensions` で設定可能

## よくある問題

### ファイルが転送されない
- `AllowedExtensions` の設定を確認
- `Watch.Path` が正しいパスか確認
- ファイルが他プロセスで使用中でないか確認
- `RequireEndFile` が `true` の場合、対応するENDファイルが存在するか確認（Put/Get両方）
- `TransferEndFiles` が `true` の場合、ENDファイルの転送順序が正しいか確認
- ダウンロード時：リモートパスでのパス区切り文字が正しいか確認

### 同名ファイルが上書きされる
- `Watch.IncludeSubfolders: true` と `Transfer.PreserveFolderStructure: false` の組み合わせになっていないか確認
- サブフォルダ内で同名ファイル（例: `A/result.csv` と `B/result.csv`）がないか確認
- 回避策:
  - `Transfer.PreserveFolderStructure` を `true` にする
  - または、重複しないファイル命名規則に統一する

### 接続エラー
- ホスト名・ポート番号・認証情報を確認
- ネットワーク接続とファイアウォール設定を確認

### ハッシュ検証エラー
- `Hash.UseServerCommand` を `false` に設定してローカル計算を試行
- ネットワークの安定性を確認
- セキュリティ強化のためMD5よりもSHA256またはSHA512の使用を推奨

### アプリケーションが終了しない
- パフォーマンス監視タスクが正常に終了していない可能性があります
- 最新バージョンではこの問題は修正済みです

## 依存関係

- [FluentFTP](https://github.com/robinrodricks/FluentFTP) 52.1.0 - FTP クライアント
- [SSH.NET](https://github.com/sshnet/SSH.NET) 2025.0.0 - SFTP クライアント  
- [Polly](https://github.com/App-vNext/Polly) 8.6.0 - 再試行ライブラリ
- Microsoft.Extensions.Hosting 9.0.6 - ホスティングフレームワーク
- Microsoft.Extensions.Options.DataAnnotations 9.0.6 - 設定検証

## ライセンス

このプロジェクトは MIT ライセンスの下で公開されています。

## 注意事項

- **バッチ処理型**: 本ツールは起動時に一度だけ処理を実行して終了します
- **定期実行推奨**: 継続的な転送には cron やタスクスケジューラーでの定期実行を設定してください
- **リアルタイム監視なし**: ファイルの変更をリアルタイムで監視する機能はありません
- **セキュリティ**: パスワードは環境変数や秘密管理ツールでの管理を推奨します
- **ハッシュアルゴリズム**: MD5、SHA256、SHA512をサポート（セキュリティ強化のためSHA256/SHA512を推奨）
- **パフォーマンス監視**: 転送処理の進捗とメモリ使用量をリアルタイム監視し、処理完了後に適切に終了
