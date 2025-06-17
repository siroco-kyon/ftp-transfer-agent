# FtpTransferAgent 詳細仕様書

## 1. 概要

FtpTransferAgent は、指定したローカルフォルダ内のファイルを FTP または SFTP サーバーに一括転送する .NET 8 ベースのコンソールアプリケーションです。起動時にフォルダ内のファイルを検出し、設定に従って転送処理を実行します。転送後はハッシュ値による整合性チェックを行い、必要に応じて転送済みファイルの自動削除も可能です。双方向転送（アップロード/ダウンロード）にも対応しています。

### 主な特徴
- 📤📥 FTP/SFTP 双方向転送（アップロード/ダウンロード）
- 🚀 起動時の一括転送処理（バッチ処理型）
- 🔐 SFTP 鍵認証対応（パスワード認証と鍵認証の両方をサポート）
- 🔒 ハッシュ値による整合性検証（MD5/SHA256）
- 🔄 自動再試行機能（Polly による）
- 🗑️ 転送成功後の自動削除オプション
- ⚡ 並列転送対応（最大16ファイルの同時転送）
- 📝 ローリングファイルログ（日付・サイズベース）
- 📧 エラー時のメール通知機能
- ⚙️ JSON による柔軟な設定

### 動作モード
本ツールは**バッチ処理型**のアプリケーションとして設計されており、起動時に一度だけ転送処理を実行して終了します。継続的な監視が必要な場合は、cron や Windows タスクスケジューラーでの定期実行を推奨します。

**注意**: `FolderWatcher` クラスは実装されていますが、現在は使用されていません。将来的なリアルタイム監視機能の実装に備えたコードとして残されています。

## 2. システム要件

### 必須要件
- **.NET 8 Runtime** または **.NET 8 SDK**
- **対応OS**: Windows、Linux、macOS
- **メモリ**: 最小 512MB（推奨 1GB 以上）
- **ネットワーク**: FTP/SFTP サーバーへのアクセス

### 依存パッケージ
- FluentFTP 52.1.0（FTP 通信）
- SSH.NET 2025.0.0（SFTP 通信）
- Polly 8.6.0（再試行処理）
- Microsoft.Extensions.Hosting 9.0.6（ホスティング）
- Microsoft.Extensions.Options.DataAnnotations 9.0.6（設定検証）

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

### 3.3 定期実行の設定（推奨）

本アプリケーションは起動時に一度だけ処理を実行して終了するため、定期的な転送が必要な場合は以下のように設定します。

#### Windows タスクスケジューラー
```powershell
# 毎日午前2時に実行するタスクを作成
schtasks /create /tn "FtpTransferAgent" /tr "C:\path\to\FtpTransferAgent.exe" /sc daily /st 02:00

# 30分ごとに実行
schtasks /create /tn "FtpTransferAgent" /tr "C:\path\to\FtpTransferAgent.exe" /sc minute /mo 30
```

#### Linux cron
```bash
# crontab を編集
crontab -e

# 毎時0分に実行
0 * * * * /opt/ftptransferagent/FtpTransferAgent

# 5分ごとに実行
*/5 * * * * /opt/ftptransferagent/FtpTransferAgent
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
  "AllowedExtensions": [".txt", ".csv", ".xml"]
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 |
|------|-----|------|------|--------------|
| Path | string | ✓ | 転送対象フォルダのパス（相対/絶対パス） | なし |
| IncludeSubfolders | boolean | - | サブフォルダも含めるか | false |
| AllowedExtensions | string[] | - | 転送対象の拡張子リスト（空の場合は全て）<br>ドット付きでもなしでも可 | [] |

**重要**: 
- アプリケーション起動時に、このフォルダ内のファイルが一度だけ処理されます
- リアルタイムのフォルダ監視機能は実装されていません
- 新しいファイルを転送するには、アプリケーションを再度実行する必要があります

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
  "PrivateKeyPath": null,
  "PrivateKeyPassphrase": null,
  "RemotePath": "/upload",
  "Concurrency": 1
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

**転送方向（Direction）の詳細：**
- **"put"**: ローカル → リモート（Watch.Path 内のファイルをアップロード）
- **"get"**: リモート → ローカル（RemotePath 内のファイルを Watch.Path にダウンロード）
- **"both"**: 先に get を実行してから put を実行

**認証方式の制約：**
- **FTP モード**: Password が必須
- **SFTP モード**: Password または PrivateKeyPath のいずれかが必須（両方指定も可）

**使用例：**
```json
// 例1: FTP サーバー（シングルスレッド）
"Transfer": {
  "Mode": "ftp",
  "Direction": "put",
  "Host": "192.168.1.100",
  "Port": 21,
  "Username": "upload_user",
  "Password": "secure_password",
  "RemotePath": "/data/incoming",
  "Concurrency": 1
}

// 例2: SFTP サーバー（鍵認証・並列転送）
"Transfer": {
  "Mode": "sftp",
  "Direction": "both",
  "Host": "sftp.company.com",
  "Port": 22,
  "Username": "sftpuser",
  "Password": null,
  "PrivateKeyPath": "./id_ed25519",
  "PrivateKeyPassphrase": null,
  "RemotePath": "/home/sftpuser/data",
  "Concurrency": 4
}

// 例3: SFTP サーバー（パスワード＋鍵認証）
"Transfer": {
  "Mode": "sftp",
  "Direction": "put",
  "Host": "secure.example.com",
  "Port": 22,
  "Username": "admin",
  "Password": "password123",
  "PrivateKeyPath": "./keys/id_rsa",
  "PrivateKeyPassphrase": "keypass",
  "RemotePath": "/uploads",
  "Concurrency": 2
}
```

**SFTP 鍵認証の設定方法：**
```bash
# 鍵ペアの生成（Ed25519 推奨）
ssh-keygen -t ed25519 -f id_ed25519

# または RSA 鍵
ssh-keygen -t rsa -b 4096 -f id_rsa

# 公開鍵をサーバーに登録
ssh-copy-id -i id_ed25519.pub sftpuser@sftp.company.com

# 秘密鍵ファイルの権限設定（Linux/macOS）
chmod 600 id_ed25519
```

#### 4.2.3 Retry（再試行設定）

転送エラー時の再試行動作を設定します。

```json
"Retry": {
  "MaxAttempts": 3,
  "DelaySeconds": 5
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 | 制約 |
|------|-----|------|------|--------------|------|
| MaxAttempts | integer | - | 最大再試行回数 | 3 | 0以上 |
| DelaySeconds | integer | - | 再試行間隔（秒） | 5 | 0以上 |

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

転送後の整合性検証に使用するハッシュアルゴリズムと取得方法を設定します。

```json
"Hash": {
  "Algorithm": "MD5"
}
```

| 項目 | 型 | 必須 | 説明 | デフォルト値 | 制約 |
|------|-----|------|------|--------------|------|
| Algorithm | string | ✓ | ハッシュアルゴリズム | "MD5" | "MD5" または "SHA256" |
| UseServerCommand | boolean | - | FTP サーバーのハッシュ計算コマンドを利用するか | true | - |

`UseServerCommand` が `false` の場合、サーバー側でハッシュ計算コマンドが利用でき
ないときでもファイルを取得してローカルでハッシュを計算します。SFTP では常にローカ
ル計算が行われ、この設定は無視されます。

**使用例：**
```json
// 例1: MD5（高速、互換性重視）
"Hash": {
  "Algorithm": "MD5"
}

// 例2: SHA256（より安全）
"Hash": {
  "Algorithm": "SHA256",
  "UseServerCommand": false
}
```

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

**動作の詳細**: 
- `DeleteAfterVerify`: アップロード時のみ有効。ハッシュ検証成功後にローカルファイルを削除
- `DeleteRemoteAfterDownload`: ダウンロード時のみ有効。ハッシュ検証成功後にリモートファイルを削除

**使用例：**
```json
// 例1: ファイルを残す（バックアップ推奨）
"Cleanup": {
  "DeleteAfterVerify": false,
  "DeleteRemoteAfterDownload": false
}

// 例2: 自動削除（ディスク容量節約）
"Cleanup": {
  "DeleteAfterVerify": true,
  "DeleteRemoteAfterDownload": true
}
```

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

**使用例：**
```json
// 例1: Gmail を使用
"Smtp": {
  "Enabled": true,
  "RelayHost": "smtp.gmail.com",
  "RelayPort": 587,
  "UseTls": true,
  "Username": "your-email@gmail.com",
  "Password": "app-specific-password",
  "From": "your-email@gmail.com",
  "To": ["admin@example.com", "support@example.com"]
}

// 例2: 社内 SMTP サーバー（認証なし）
"Smtp": {
  "Enabled": true,
  "RelayHost": "mail.company.local",
  "RelayPort": 25,
  "UseTls": false,
  "Username": "",
  "Password": "",
  "From": "ftpagent@company.local",
  "To": ["it-ops@company.local"]
}
```

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

**ログレベル：**
- **Trace**: 詳細なデバッグ情報
- **Debug**: デバッグ情報
- **Information**: 正常な処理の進行状況
- **Warning**: 再試行などの警告情報
- **Error**: エラー情報
- **Critical**: 致命的なエラー

**ローリングログの動作：**
- 日付ごとにファイルが作成される（例：`ftp-transfer-20250614.log`）
- 同日内でファイルサイズが MaxBytes を超えると、新しいファイルが作成される（例：`ftp-transfer-20250614_1.log`）

**使用例：**
```json
// 例1: 詳細ログ（小さいファイルサイズ）
"Logging": {
  "Level": "Debug",
  "RollingFilePath": "logs/debug-.log",
  "MaxBytes": 5242880  // 5MB
}

// 例2: エラーのみ（大きいファイルサイズ）
"Logging": {
  "Level": "Error",
  "RollingFilePath": "/var/log/ftpagent/error-.log",
  "MaxBytes": 104857600  // 100MB
}
```

### 4.3 設定ファイルの配置場所

設定ファイル **appsettings.json** は以下の場所に配置します：

- **Windows**: `FtpTransferAgent.exe` と同じフォルダ
- **Linux/macOS**: `FtpTransferAgent` 実行ファイルと同じディレクトリ

開発環境では、`DOTNET_ENVIRONMENT=Development` を設定することで **appsettings.Development.json** が優先的に読み込まれます。

### 4.4 完全な設定例

#### 開発環境用設定（appsettings.Development.json）
```json
{
  "Watch": {
    "Path": "D:\\Github\\temp\\watch",
    "IncludeSubfolders": false,
    "AllowedExtensions": [".txt"]
  },
  "Transfer": {
    "Mode": "ftp",
    "Direction": "put",
    "Host": "localhost",
    "Port": 21,
    "Username": "user",
    "Password": "pass",
    "RemotePath": "/remote",
    "Concurrency": 1
  },
  "Retry": {
    "MaxAttempts": 3,
    "DelaySeconds": 5
  },
  "Hash": {
    "Algorithm": "MD5",
    "UseServerCommand": false
  },
  "Cleanup": {
    "DeleteAfterVerify": false,
    "DeleteRemoteAfterDownload": false
  },
  "Smtp": {
    "Enabled": false,
    "RelayHost": "localhost",
    "RelayPort": 25,
    "UseTls": false,
    "Username": "",
    "Password": "",
    "From": "noreply@example.com",
    "To": ["admin@example.com"]
  },
  "Logging": {
    "Level": "Information",
    "RollingFilePath": "logs/ftp-transfer-.log"
  }
}
```

#### 本番環境用設定（appsettings.json）
```json
{
  "Watch": {
    "Path": "/opt/data/export",
    "IncludeSubfolders": true,
    "AllowedExtensions": [".csv", ".xml", ".json"]
  },
  "Transfer": {
    "Mode": "sftp",
    "Direction": "both",
    "Host": "secure-ftp.company.com",
    "Port": 22,
    "Username": "prod_transfer",
    "Password": null,
    "PrivateKeyPath": "/opt/ftpagent/keys/id_ed25519",
    "PrivateKeyPassphrase": null,
    "RemotePath": "/imports/automated",
    "Concurrency": 4
  },
  "Retry": {
    "MaxAttempts": 5,
    "DelaySeconds": 10
  },
  "Hash": {
    "Algorithm": "SHA256",
    "UseServerCommand": false
  },
  "Cleanup": {
    "DeleteAfterVerify": true,
    "DeleteRemoteAfterDownload": true
  },
  "Smtp": {
    "Enabled": true,
    "RelayHost": "smtp.company.com",
    "RelayPort": 587,
    "UseTls": true,
    "Username": "notifications@company.com",
    "Password": "smtp-password",
    "From": "ftpagent@company.com",
    "To": ["it-ops@company.com", "data-team@company.com"]
  },
  "Logging": {
    "Level": "Information",
    "RollingFilePath": "/var/log/ftpagent/transfer-.log",
    "MaxBytes": 52428800
  }
}
```

## 5. 動作仕様

### 5.1 基本的な処理フロー

1. **起動時の初期化**
   - 設定ファイル（appsettings.json）の読み込みと検証
   - ログシステムの初期化（コンソール + ローリングファイル + メール通知）
   - FTP/SFTP クライアントの初期化
   - 転送キューの初期化（並列度の設定）

2. **ファイル列挙**（Direction に応じて実行）
   - **"put" モード**: Watch.Path 内のファイルを `Directory.EnumerateFiles` で列挙
   - **"get" モード**: RemotePath 内のファイルリストを取得
   - **"both" モード**: 先に get のファイル列挙、その後 put のファイル列挙
   - 拡張子フィルターが設定されている場合は、該当するファイルのみを対象とする

3. **キューへの登録**
   - 列挙されたファイルを `Channel<TransferItem>` に登録
   - 各ファイルには転送方向（Upload/Download）が設定される

4. **転送処理**（並列実行）
   - 指定された並列度（Concurrency）でワーカースレッドが起動
   - 各ワーカーはキューからファイルを取得して転送
   - 一時ファイル名（.tmp）で転送し、完了後に正式なファイル名にリネーム
   - 各転送には固有の ID (GUID) が割り当てられ、ログで追跡可能

5. **検証処理**
   - ローカルファイルのハッシュ値計算（HashUtil.ComputeHashAsync）
   - リモートファイルのハッシュ値取得
     - `Hash.UseServerCommand` が `true` かつ FTP サーバーが対応している場合はサーバーのハッシュコマンドを使用
     - それ以外はファイルを取得してローカルで計算（SFTP は常にこちら）
   - ハッシュ値の比較（大文字小文字を無視）

6. **後処理**
   - 検証成功時：
     - アップロード時：設定に応じてローカルファイルを削除
     - ダウンロード時：設定に応じてリモートファイルを削除
   - 検証失敗時：エラーログを出力（メール通知も送信）

7. **終了**
   - すべての転送処理が完了したらアプリケーションを終了
   - BackgroundService として実装されているが、実質的にワンショット処理

### 5.2 並列転送の仕組み

`Transfer.Concurrency` で指定された数のワーカースレッドが起動し、キューからファイルを取得して並列に転送処理を行います。

```
キュー → ワーカー1 → FTP/SFTP転送
      → ワーカー2 → FTP/SFTP転送
      → ワーカー3 → FTP/SFTP転送
      → ワーカー4 → FTP/SFTP転送
```

**並列転送のメリット：**
- 小さなファイルが多数ある場合の転送効率向上
- ネットワーク帯域の有効活用
- 1つのファイルの転送が遅延しても他のファイルは影響を受けない

**注意事項：**
- サーバー側の同時接続数制限に注意
- 大容量ファイルの場合は並列度を下げることを推奨

### 5.3 エラーハンドリング

#### 自動再試行が行われるエラー
- ネットワーク接続エラー
- FTP/SFTP サーバーの一時的な応答エラー
- ファイルアクセスエラー（他プロセスによるロック等）
- タイムアウトエラー

#### 再試行されないエラー
- 設定ファイルの検証エラー（起動時に失敗）
- 認証エラー（ユーザー名/パスワード不正）
- 権限エラー（書き込み権限なし）
- ディスク容量不足

### 5.4 ログ出力とメール通知

#### ログ出力先
1. **コンソール出力**：タイムスタンプ付きで標準出力に表示
2. **ファイル出力**：ローリングファイルとして保存（設定時のみ）
3. **メール通知**：エラー発生時にメール送信（設定時のみ）

#### ログ形式
```
2025-06-14 10:15:30 [Information] FtpTransferAgent.Worker [a1b2c3d4-e5f6-7890-abcd-ef1234567890] Uploading /watch/data.txt to /remote/data.txt
2025-06-14 10:15:32 [Information] FtpTransferAgent.Worker [a1b2c3d4-e5f6-7890-abcd-ef1234567890] Verified hash for /watch/data.txt
2025-06-14 10:15:32 [Information] FtpTransferAgent.Worker [a1b2c3d4-e5f6-7890-abcd-ef1234567890] Deleted /watch/data.txt
2025-06-14 10:15:33 [Warning] FtpTransferAgent.Services.TransferQueue Retry 1
2025-06-14 10:15:35 [Error] FtpTransferAgent.Worker [b2c3d4e5-f6a7-8901-bcde-f23456789012] Hash mismatch for /watch/error.csv
```

#### メール通知の内容
エラーログが発生すると、以下の形式でメールが送信されます：
- **件名**: `[FtpTransferAgent.Worker] Error`
- **本文**: エラーメッセージと例外情報

## 6. トラブルシューティング

### 6.1 よくある問題と解決方法

#### ファイルが転送されない
- **原因1**: 拡張子フィルターに一致しない
  - **解決**: `AllowedExtensions` を確認し、必要な拡張子を追加
- **原因2**: フォルダパスが間違っている
  - **解決**: `Watch.Path` が正しいパスを指していることを確認
- **原因3**: ファイルが他のプロセスで使用中
  - **解決**: ファイルの書き込みが完了してから実行する
- **原因4**: 前回の実行で既に転送済み
  - **解決**: 本アプリケーションは起動時に存在するファイルのみを処理します

#### 転送エラーが発生する
- **原因1**: ネットワーク接続の問題
  - **解決**: FTP/SFTP サーバーへの接続を確認
- **原因2**: 認証情報が間違っている
  - **解決**: ユーザー名とパスワード/鍵ファイルを確認
- **原因3**: リモートパスが存在しない
  - **解決**: リモートサーバー上にパスを作成
- **原因4**: 並列接続数が多すぎる
  - **解決**: `Concurrency` を減らす

#### ハッシュ検証エラー
- **原因1**: FTP サーバーがハッシュコマンドをサポートしていない
  - **解決**: `Hash.UseServerCommand` を `false` にしてローカル計算を行う
- **原因2**: 転送中にファイルが破損
  - **解決**: ネットワークの安定性を確認

#### ログファイルが作成されない
- **原因1**: `RollingFilePath` が設定されていない
  - **解決**: 有効なファイルパスを設定
- **原因2**: ディレクトリへの書き込み権限がない
  - **解決**: ログディレクトリの権限を確認

#### メール通知が届かない
- **原因1**: `Smtp.Enabled` が false
  - **解決**: true に設定
- **原因2**: SMTP サーバー設定が間違っている
  - **解決**: ホスト、ポート、認証情報を確認
- **原因3**: ファイアウォールでブロックされている
  - **解決**: SMTP ポートへの接続を許可

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
- .NET User Secrets（開発環境）の活用も可能
- SFTP では鍵認証の使用を推奨

### 7.2 ファイル権限
- 設定ファイルは適切な権限で保護してください
- Linux/macOS: `chmod 600 appsettings.json`
- Windows: 管理者とサービスアカウントのみにアクセス権限を付与
- 秘密鍵ファイルも同様に保護: `chmod 600 id_ed25519`

### 7.3 通信の暗号化
- FTP は暗号化されていないため、機密データには SFTP の使用を推奨
- SFTP を使用する場合は、ポート 22 を指定
- SMTP でメール送信する場合は `UseTls: true` を推奨

### 7.4 ログファイルのセキュリティ
- ログファイルにはユーザー名などの情報が含まれる可能性があります
- 適切なアクセス権限を設定してください
- メール通知にもエラー内容が含まれるため、送信先は信頼できるアドレスに限定

## 8. パフォーマンスに関する考慮事項

### 8.1 大量ファイルの処理
- 起動時に指定フォルダ内のすべてのファイルがキューに登録されます
- `Transfer.Concurrency` を 2 以上に設定することで並列転送が可能です（最大16）
- CPU コア数とネットワーク帯域を考慮して並列度を設定してください

### 8.2 大容量ファイルの転送
- ファイルサイズに制限はありませんが、大容量ファイルは転送に時間がかかります
- ネットワーク帯域幅を考慮して運用してください
- 大容量ファイルの場合は並列度を下げることを推奨

### 8.3 ハッシュ計算の負荷
- SHA256 は MD5 より計算負荷が高いため、パフォーマンスを重視する場合は MD5 を使用
- セキュリティを重視する場合は SHA256 を推奨
- 大容量ファイルの場合、ハッシュ計算に時間がかかることがあります

### 8.4 メモリ使用量
- 並列転送数が多いほどメモリ使用量が増加します
- ファイルは一時的にメモリにバッファリングされません（ストリーム処理）
- SFTP でのハッシュ値取得時は、ファイル全体をメモリに読み込むため注意が必要

## 9. 実装の詳細と制限事項

### 9.1 現在の実装状況
- ✅ **転送方向**: "get"/"put"/"both" の 3 つをサポート
- ✅ **並列転送**: 最大 16 並列まで対応
- ✅ **ローリングログ**: 日付とサイズベースのローテーション実装済み
- ✅ **SMTP 通知**: エラーログをメールで送信
- ✅ **SFTP 鍵認証**: パスフレーズ付き鍵ファイルにも対応
- ✅ **一括転送**: 起動時の一括転送処理
- ❌ **リアルタイム監視**: 実装されていない（FolderWatcher クラスは未使用）
- ❌ **転送の中断/再開**: 未対応
- ❌ **転送履歴の永続化**: 未対応
- ❌ **重複チェック**: 同じファイルを再度転送する可能性あり

### 9.2 既知の制限
- アプリケーションは一度の実行で処理を完了して終了する（常駐しない）
- 新しいファイルを転送するには再度実行する必要がある
- FolderWatcher クラスは実装されているが、実際には使用されていない
- SFTP でのハッシュ取得はファイル全体のダウンロードが必要（プロトコルの制限）
- Windows でのファイルパス長は 260 文字まで（.NET の制限）
- 同時接続数はサーバー側の制限に依存

### 9.3 推奨される使用方法
- **定期実行**: cron や Windows タスクスケジューラーで定期的に実行
- **ワークフロー統合**: 他のバッチ処理の一部として組み込む
- **イベントドリブン**: ファイル生成プロセスの完了後に実行
- **重複転送の回避**: 転送済みファイルを別フォルダに移動するなどの運用上の工夫

### 9.4 定期実行の詳細設定例

#### Linux cron での高度な設定
```bash
# 5分ごとに実行（ロック機構付き）
*/5 * * * * flock -n /var/lock/ftpagent.lock -c '/opt/ftptransferagent/FtpTransferAgent'

# 毎日午前1時と午後1時に実行（エラー時のみメール通知）
0 1,13 * * * /opt/ftptransferagent/FtpTransferAgent 2>&1 | grep -E "(Error|Critical)" | mail -s "FTP Transfer Error" admin@example.com

# 平日の業務時間中、30分ごとに実行（ログ付き）
*/30 8-18 * * 1-5 /opt/ftptransferagent/FtpTransferAgent >> /var/log/ftpagent-cron.log 2>&1

# 実行前に新しいファイルの存在をチェック
*/10 * * * * [ -n "$(find /path/to/watch -type f -mmin -10)" ] && /opt/ftptransferagent/FtpTransferAgent
```

#### Windows タスクスケジューラーでの詳細設定
```xml
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>FTP Transfer Agent - 定期的にファイルを転送</Description>
  </RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <Repetition>
        <Interval>PT5M</Interval>
        <Duration>P1D</Duration>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <StartBoundary>2025-01-01T00:00:00</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal>
      <UserId>SYSTEM</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
  </Settings>
  <Actions>
    <Exec>
      <Command>C:\FtpTransferAgent\FtpTransferAgent.exe</Command>
      <WorkingDirectory>C:\FtpTransferAgent</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
```

#### PowerShell での設定
```powershell
# 5分ごとに実行するタスクを作成
$action = New-ScheduledTaskAction -Execute "C:\FtpTransferAgent\FtpTransferAgent.exe" -WorkingDirectory "C:\FtpTransferAgent"
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration ([TimeSpan]::MaxValue)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
Register-ScheduledTask -TaskName "FtpTransferAgent" -Action $action -Trigger $trigger -Settings $settings -User "SYSTEM" -RunLevel Highest
```

## 10. 今後の開発予定

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

## 11. ライセンス

本ソフトウェアは MIT ライセンスの下で公開されています。

---

**更新日**: 2025年6月17日
**バージョン**: 2.1.1
**主な更新内容**:
- 現在の実装に合わせて仕様を全面的に見直し
- FolderWatcher が未使用であることを明確化
- バッチ処理型アプリケーションとしての特性を強調
- 定期実行での使用を前提とした説明に変更
- 各設定項目の動作詳細を実装に基づいて修正
- 定期実行の設定例を大幅に拡充
- 今後の開発予定セクションを追加
