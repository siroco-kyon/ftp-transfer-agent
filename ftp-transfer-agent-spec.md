# FtpTransferAgent 詳細仕様書

## 1. 概要

FtpTransferAgent は、指定したローカルフォルダを監視し、新しいファイルを自動的に FTP または SFTP サーバーに転送する .NET 8 ベースのバックグラウンドサービスです。転送後はハッシュ値による整合性チェックを行い、必要に応じて転送済みファイルの自動削除も可能です。

### 主な特徴
- 🔍 リアルタイムフォルダ監視
- 📤 FTP/SFTP 自動転送
- 🔐 ハッシュ値による整合性検証
- 🔄 自動再試行機能
- 🗑️ 転送成功後の自動削除オプション
- ⚙️ JSON による柔軟な設定

## 2. システム要件

### 必須要件
- **.NET 8 Runtime** または **.NET 8 SDK**
- **対応OS**: Windows、Linux、macOS
- **メモリ**: 最小 512MB（推奨 1GB 以上）
- **ネットワーク**: FTP/SFTP サーバーへのアクセス

### 依存パッケージ
- FluentFTP 45.0.0（FTP 通信）
- SSH.NET 2020.0.1（SFTP 通信）
- Polly 7.2.3（再試行処理）
- Microsoft.Extensions.Hosting 8.0.1（ホスティング）
- Microsoft.Extensions.Options.DataAnnotations 8.0.0（設定検証）

## 3. インストールと起動

### 3.1 ソースコードからのビルド

```bash
# リポジトリのクローン
git clone [repository-url]
cd FtpTransferAgent

# 依存関係の復元
dotnet restore

# ビルド（Debug モード）
dotnet build

# ビルド（Release モード）
dotnet build --configuration Release

# 実行
dotnet run --project FtpTransferAgent
```

### 3.2 公開済みバイナリの実行

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

### 3.3 Windows サービスとしての登録

```powershell
# サービスの作成
sc create FtpTransferAgent binPath= "C:\path\to\FtpTransferAgent.exe"

# サービスの開始
sc start FtpTransferAgent

# サービスの停止
sc stop FtpTransferAgent
```

### 3.4 Linux systemd サービスとしての登録

```bash
# サービスファイルの作成
sudo nano /etc/systemd/system/ftptransferagent.service
```

```ini
[Unit]
Description=FTP Transfer Agent
After=network.target

[Service]
Type=notify
ExecStart=/opt/ftptransferagent/FtpTransferAgent
WorkingDirectory=/opt/ftptransferagent
Restart=always
RestartSec=10
SyslogIdentifier=ftptransferagent
User=ftpagent
Environment="DOTNET_ENVIRONMENT=Production"

[Install]
WantedBy=multi-user.target
```

```bash
# サービスの有効化と開始
sudo systemctl enable ftptransferagent
sudo systemctl start ftptransferagent
```

## 4. 設定ファイル（appsettings.json）

設定ファイルは JSON 形式で、アプリケーションと同じディレクトリに配置します。

### 4.1 設定ファイルの全体構造

```json
{
  "Watch": { ... },      // フォルダ監視設定
  "Transfer": { ... },   // 転送設定
  "Retry": { ... },      // 再試行設定
  "Hash": { ... },       // ハッシュ検証設定
  "Cleanup": { ... },    // クリーンアップ設定
  "Smtp": { ... },       // SMTP設定（未実装）
  "Logging": { ... }     // ログ設定（未実装）
}
```

### 4.2 各セクションの詳細

#### 4.2.1 Watch（フォルダ監視設定）

監視対象のフォルダと条件を設定します。

```json
"Watch": {
  "Path": "./watch",
  "IncludeSubfolders": false,
  "AllowedExtensions": [".txt", ".csv", ".xml"]
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 |
|------|-----|------|------|--------------|
| Path | string | ✓ | 監視対象フォルダのパス（相対/絶対パス） | なし |
| IncludeSubfolders | boolean | - | サブフォルダも監視するか | false |
| AllowedExtensions | string[] | - | 転送対象の拡張子リスト（空の場合は全て） | [] |

**使用例：**
```json
// 例1: 特定フォルダのテキストファイルのみ
"Watch": {
  "Path": "C:\\DataExport",
  "IncludeSubfolders": false,
  "AllowedExtensions": [".txt"]
}

// 例2: サブフォルダ含む全ファイル
"Watch": {
  "Path": "/home/user/uploads",
  "IncludeSubfolders": true,
  "AllowedExtensions": []
}
```

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
  "RemotePath": "/upload"
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 | 制約 |
|------|-----|------|------|--------------|------|
| Mode | string | ✓ | 転送プロトコル | "ftp" | "ftp" または "sftp" |
| Direction | string | ✓ | 転送方向 | "put" | "get" / "put" / "both" |
| Host | string | ✓ | サーバーのホスト名/IPアドレス | なし | - |
| Port | integer | - | ポート番号 | 21 | - |
| Username | string | ✓ | ログインユーザー名 | なし | - |
| Password | string | ✓ | ログインパスワード | なし | - |
| RemotePath | string | ✓ | リモートの転送先パス | なし | - |
| Concurrency | integer | - | 同時転送数 | 1 | 1以上 |

**使用例：**
```json
// 例1: FTP サーバー
"Transfer": {
  "Mode": "ftp",
  "Direction": "put",
  "Host": "192.168.1.100",
  "Port": 21,
  "Username": "upload_user",
  "Password": "secure_password",
  "RemotePath": "/data/incoming",
  "Concurrency": 2
}

// 例2: SFTP サーバー
"Transfer": {
  "Mode": "sftp",
  "Direction": "put",
  "Host": "sftp.company.com",
  "Port": 22,
  "Username": "sftpuser",
  "Password": "ssh_password",
  "RemotePath": "/home/sftpuser/uploads",
  "Concurrency": 4
}
```

#### 4.2.3 Retry（再試行設定）

転送エラー時の再試行動作を設定します。

```json
"Retry": {
  "MaxAttempts": 3,
  "DelaySeconds": 5
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 |
|------|-----|------|------|--------------|
| MaxAttempts | integer | - | 最大再試行回数 | 3 |
| DelaySeconds | integer | - | 再試行間隔（秒） | 5 |

**使用例：**
```json
// 例1: 高速再試行
"Retry": {
  "MaxAttempts": 5,
  "DelaySeconds": 2
}

// 例2: 長期間隔での再試行
"Retry": {
  "MaxAttempts": 10,
  "DelaySeconds": 30
}
```

#### 4.2.4 Hash（ハッシュ検証設定）

転送後の整合性検証に使用するハッシュアルゴリズムを設定します。

```json
"Hash": {
  "Algorithm": "MD5"
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 | 制約 |
|------|-----|------|------|--------------|------|
| Algorithm | string | ✓ | ハッシュアルゴリズム | "MD5" | "MD5" または "SHA256" |

**使用例：**
```json
// 例1: MD5（高速）
"Hash": {
  "Algorithm": "MD5"
}

// 例2: SHA256（より安全）
"Hash": {
  "Algorithm": "SHA256"
}
```

#### 4.2.5 Cleanup（クリーンアップ設定）

転送成功後のローカルファイル処理を設定します。

```json
"Cleanup": {
  "DeleteAfterVerify": false
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 |
|------|-----|------|------|--------------|
| DeleteAfterVerify | boolean | - | 検証成功後にローカルファイルを削除 | false |

**使用例：**
```json
// 例1: ファイルを残す（バックアップ推奨）
"Cleanup": {
  "DeleteAfterVerify": false
}

// 例2: 自動削除（ディスク容量節約）
"Cleanup": {
  "DeleteAfterVerify": true
}
```

### 4.3 完全な設定例

#### 開発環境用設定
```json
{
  "Watch": {
    "Path": "./test-files",
    "IncludeSubfolders": false,
    "AllowedExtensions": [".txt", ".log"]
  },
  "Transfer": {
    "Mode": "ftp",
    "Direction": "put",
    "Host": "localhost",
    "Port": 21,
  "Username": "testuser",
  "Password": "testpass",
  "RemotePath": "/test",
  "Concurrency": 1
  },
  "Retry": {
    "MaxAttempts": 2,
    "DelaySeconds": 3
  },
  "Hash": {
    "Algorithm": "MD5"
  },
  "Cleanup": {
    "DeleteAfterVerify": false
  }
}
```

#### 本番環境用設定
```json
{
  "Watch": {
    "Path": "/opt/data/export",
    "IncludeSubfolders": true,
    "AllowedExtensions": [".csv", ".xml", ".json"]
  },
  "Transfer": {
    "Mode": "sftp",
    "Direction": "put",
    "Host": "secure-ftp.company.com",
    "Port": 22,
    "Username": "prod_transfer",
  "Password": "${SFTP_PASSWORD}",
  "RemotePath": "/imports/automated",
  "Concurrency": 4
  },
  "Retry": {
    "MaxAttempts": 5,
    "DelaySeconds": 10
  },
  "Hash": {
    "Algorithm": "SHA256"
  },
  "Cleanup": {
    "DeleteAfterVerify": true
  }
}
```

## 5. 動作仕様

### 5.1 基本的な処理フロー

1. **起動時の初期化**
   - 設定ファイルの読み込みと検証
   - FTP/SFTP クライアントの初期化
   - フォルダ監視の開始

2. **ファイル検出時の処理**
   - 新規ファイルまたはリネームされたファイルを検出
   - 拡張子フィルターによる対象ファイルの判定
   - 転送キューへの追加

3. **転送処理**
   - キューからファイルを取得
   - 一時ファイル名（.tmp）で転送
   - 転送完了後に正式なファイル名にリネーム

4. **検証処理**
   - ローカルファイルのハッシュ値計算
   - リモートファイルのハッシュ値取得
   - ハッシュ値の比較

5. **後処理**
   - 検証成功時：設定に応じてローカルファイルを削除
   - 検証失敗時：エラーログを出力

### 5.2 エラーハンドリング

#### 自動再試行が行われるエラー
- ネットワーク接続エラー
- FTP/SFTP サーバーの一時的な応答エラー
- ファイルアクセスエラー（他プロセスによるロック等）

#### 再試行されないエラー
- 設定ファイルの検証エラー
- 認証エラー（ユーザー名/パスワード不正）
- 権限エラー（書き込み権限なし）

### 5.3 ログ出力

現在の実装では、標準的なコンソールログが出力されます。

#### ログレベル
- **Information**: 正常な処理の進行状況
- **Warning**: 再試行などの警告情報
- **Error**: エラー情報

#### ログ例
```
info: FtpTransferAgent.Worker[0]
      [a1b2c3d4-e5f6-7890-abcd-ef1234567890] Uploading /watch/data.txt to /remote/data.txt
info: FtpTransferAgent.Worker[0]
      [a1b2c3d4-e5f6-7890-abcd-ef1234567890] Verified hash for /watch/data.txt
info: FtpTransferAgent.Worker[0]
      [a1b2c3d4-e5f6-7890-abcd-ef1234567890] Deleted /watch/data.txt
```

## 6. トラブルシューティング

### 6.1 よくある問題と解決方法

#### ファイルが転送されない
- **原因1**: 拡張子フィルターに一致しない
  - **解決**: `AllowedExtensions` を確認し、必要な拡張子を追加
- **原因2**: フォルダパスが間違っている
  - **解決**: `Watch.Path` が正しいパスを指していることを確認
- **原因3**: ファイルが他のプロセスで使用中
  - **解決**: ファイルの書き込みが完了してから転送されるまで待つ

#### 転送エラーが発生する
- **原因1**: ネットワーク接続の問題
  - **解決**: FTP/SFTP サーバーへの接続を確認
- **原因2**: 認証情報が間違っている
  - **解決**: ユーザー名とパスワードを確認
- **原因3**: リモートパスが存在しない
  - **解決**: リモートサーバー上にパスを作成

#### ハッシュ検証エラー
- **原因1**: FTP サーバーがハッシュコマンドをサポートしていない
  - **解決**: 別の FTP サーバーを使用するか、SFTP に切り替える
- **原因2**: 転送中にファイルが破損
  - **解決**: ネットワークの安定性を確認

### 6.2 設定の検証

アプリケーション起動時に設定の検証が行われます。エラーがある場合は起動に失敗します。

```
Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException: 
DataAnnotation validation failed for 'TransferOptions' members: 'Host' with the error: 'The Host field is required.'
```

### 6.3 デバッグモード

開発環境では `DOTNET_ENVIRONMENT=Development` を設定することで、より詳細なログが出力されます。

```bash
# Windows
set DOTNET_ENVIRONMENT=Development
FtpTransferAgent.exe

# Linux/macOS
export DOTNET_ENVIRONMENT=Development
./FtpTransferAgent
```

## 7. セキュリティに関する注意事項

### 7.1 パスワードの管理
- 設定ファイルにパスワードを平文で保存することは推奨されません
- 環境変数や秘密管理ツールの使用を検討してください

### 7.2 ファイル権限
- 設定ファイルは適切な権限で保護してください
- Linux/macOS: `chmod 600 appsettings.json`
- Windows: 管理者とサービスアカウントのみにアクセス権限を付与

### 7.3 通信の暗号化
- FTP は暗号化されていないため、機密データには SFTP の使用を推奨
- SFTP を使用する場合は、ポート 22 を指定

## 8. パフォーマンスに関する考慮事項

### 8.1 大量ファイルの処理
- 一度に大量のファイルが作成される場合、キューに登録して順次処理されます
- `Transfer.Concurrency` を 2 以上に設定することで並列転送が可能です

### 8.2 大容量ファイルの転送
- ファイルサイズに制限はありませんが、大容量ファイルは転送に時間がかかります
- ネットワーク帯域幅を考慮して運用してください

### 8.3 ハッシュ計算の負荷
- SHA256 は MD5 より計算負荷が高いため、パフォーマンスを重視する場合は MD5 を使用
- セキュリティを重視する場合は SHA256 を推奨

## 9. 制限事項と今後の実装予定

### 9.1 現在の制限事項
- **転送方向**: 現在は "get"/"put"/"both" の 3 つをサポート
- **SMTP 通知**: 未実装
- **ローリングログ**: 実装済み（ファイルサイズに応じたローテーション）
- **転送の中断/再開**: 未対応

### 9.2 実装予定の機能
- SMTP によるエラー通知
- ローリングファイルログ
- 転送履歴の記録
- Web UI による管理画面

## 10. ライセンス

本ソフトウェアは MIT ライセンスの下で公開されています。

---

**更新日**: 2025年6月14日  
**バージョン**: 1.0.0