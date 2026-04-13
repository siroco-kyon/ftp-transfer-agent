# FtpTransferAgent

.NET 8 で動作するバッチ型のファイル転送ツールです。  
指定ディレクトリ内のファイルを FTP/SFTP で転送し、転送後にハッシュ検証を行います。

## 概要

- 起動時に 1 回だけ処理して終了します（常駐監視はしません）
- `put`（アップロード）/`get`（ダウンロード）/`both`（双方向）に対応します
- 並列転送、再試行、ENDファイル制御、転送後クリーンアップに対応します

## 主な機能

- FTP / SFTP 転送
- SFTP のパスワード認証・秘密鍵認証
- SFTP ホスト鍵フィンガープリント検証
- ハッシュ検証（`SHA256` / `SHA512`）
- 指数バックオフ再試行（Polly）
- 最大 16 並列転送
- ENDファイル制御（Put/Get 両対応、データファイル先行転送）
- ローリングファイルログ + エラーメール通知

## 動作環境

### 自己完結・単一ファイル発行時（`--self-contained`）

自己完結で発行した場合、サーバへの **.NET ランタイムのインストールは不要**です。  
ただし OS レベルの依存ライブラリは必要です。

#### Linux (x64 / arm64)

| 依存ライブラリ | 用途 | 備考 |
|---|---|---|
| **glibc 2.17 以上** | .NET 8 の動作基盤 | Ubuntu 18.04以降、CentOS 7以降は満たす |
| **libssl (OpenSSL 1.1 または 3.x)** | FTP over TLS / SFTP (SSH.NET) | このアプリでは必須 |
| **libicu** | グローバリゼーション処理 | `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` で無効化可能 |
| **libz (zlib)** | 圧縮処理 | ほぼ全ディストロに標準搭載 |

> Alpine Linux など **musl-libc** ベースのディストロは glibc と互換性がありません。使用する場合は `-r linux-musl-x64` でビルドが必要です。

#### Windows

- **Windows 10 / Windows Server 2016 以降**
- Windows 7/8.1 は .NET 8 の対象外のため非対応

#### macOS

- **macOS 12 (Monterey) 以降**

### 発行コマンド例

```bash
# Linux (x64)
dotnet publish -c Release -r linux-x64 --self-contained /p:PublishSingleFile=true

# Windows (x64)
dotnet publish -c Release -r win-x64 --self-contained /p:PublishSingleFile=true

# macOS (x64)
dotnet publish -c Release -r osx-x64 --self-contained /p:PublishSingleFile=true
```

## 実行モデル

1. 設定を読み込み、起動時バリデーションを実施
2. 転送対象を列挙（ローカルまたはリモート）
3. フィルタ・END条件を適用
4. 転送キューに投入して並列処理
5. ハッシュ検証
6. 設定に応じて削除
7. 処理完了後に終了

## クイックスタート

### 1. 前提

- **開発・ビルド時**: .NET 8 SDK
- **本番サーバ**: 自己完結発行（`--self-contained`）であれば .NET ランタイム不要（OS 依存ライブラリのみ必要、[動作環境](#動作環境) 参照）
- 転送先/転送元の FTP または SFTP サーバー

### 2. 設定例（最小）

`FtpTransferAgent/appsettings.json`:

```json
{
  "Watch": {
    "Path": "./watch",
    "IncludeSubfolders": false,
    "AllowedExtensions": [".txt"]
  },
  "Transfer": {
    "Mode": "ftp",
    "Direction": "put",
    "Host": "ftp.example.com",
    "Port": 21,
    "Username": "user",
    "Password": "password",
    "RemotePath": "/upload",
    "Concurrency": 1,
    "PreserveFolderStructure": false,
    "TimeoutSeconds": 120
  },
  "Retry": {
    "MaxAttempts": 3,
    "DelaySeconds": 5
  },
  "Hash": {
    "Enabled": true,
    "Algorithm": "SHA256",
    "UseServerCommand": false
  },
  "Cleanup": {
    "DeleteAfterVerify": false,
    "DeleteRemoteAfterDownload": false,
    "DeleteRemoteEndFiles": false
  },
  "Smtp": {
    "Enabled": false,
    "RelayHost": "localhost",
    "RelayPort": 25,
    "UseTls": false,
    "Username": "",
    "Password": "",
    "From": "noreply@example.com",
    "To": ["ops@example.com"]
  },
  "Logging": {
    "Level": "Information",
    "RollingFilePath": "logs/ftp-transfer-.log",
    "MaxBytes": 10485760
  }
}
```

### 3. 実行

```bash
dotnet restore
dotnet build --configuration Release
dotnet run --project FtpTransferAgent
```

### 4. 定期実行（推奨）

バッチ型のため、継続運用はスケジューラ実行を推奨します。

Linux/macOS (`cron`) 例:

```bash
*/5 * * * * /path/to/FtpTransferAgent
```

Windows（タスクスケジューラ）例:

```powershell
schtasks /create /tn "FtpTransferAgent" /tr "C:\path\to\FtpTransferAgent.exe" /sc minute /mo 5
```

## 設定ファイル構成

プロジェクトには複数の appsettings ファイルが存在しますが、役割はそれぞれ異なります。

| ファイル | 自動読み込み | 役割 |
|---|---|---|
| `appsettings.json` | 常時 | ベース設定。全環境共通の設定値を定義する **必須ファイル** |
| `appsettings.Development.json` | 開発時のみ | `DOTNET_ENVIRONMENT=Development` のときだけ `appsettings.json` に上書きされる。開発用の接続先など |
| `appsettings.backup.json` | されない | `appsettings.json` のバックアップコピー。アプリは参照しない |
| `appsettings.invalid.json` | されない | 意図的に不正な JSON を含む設定バリデーションのテスト用サンプル |

### 環境による読み込み挙動

```
appsettings.json            ← 常に読み込まれる（ベース）
    ↓ 上書き
appsettings.{環境名}.json  ← DOTNET_ENVIRONMENT の値と一致するときのみ読み込まれる
```

`dotnet run` では `launchSettings.json` により自動的に `DOTNET_ENVIRONMENT=Development` が設定されるため、開発時は `appsettings.Development.json` も読み込まれます。

### 本番デプロイ時

**`appsettings.json` のみを配置**してください。接続先ホスト名・パスワードなどの実環境の値に書き換えてから配置します。`appsettings.Development.json`・`appsettings.backup.json`・`appsettings.invalid.json` は不要です。

## 設定リファレンス

### Watch

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `Path` | string | 必須 | `""` | 監視/保存ディレクトリ |
| `IncludeSubfolders` | bool | 任意 | `false` | サブフォルダも対象にする |
| `AllowedExtensions` | string[] | 任意 | `[]` | 対象拡張子（例: `.txt`）。空配列は全拡張子対象（起動時警告あり） |
| `RequireEndFile` | bool | 任意 | `false` | 対応する END ファイルがあるデータのみ転送 |
| `EndFileExtensions` | string[] | 任意 | `[".END", ".end"]` | END 拡張子一覧 |
| `TransferEndFiles` | bool | 任意 | `false` | END ファイル自体も転送キューに入れる |

### Transfer

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `Mode` | string | 必須 | `"ftp"` | `ftp` / `sftp` |
| `Direction` | string | 必須 | `"put"` | `put` / `get` / `both` |
| `Host` | string | 必須 | `""` | 接続先ホスト |
| `Port` | int | 任意 | `21` | 接続ポート（SFTP は通常 22） |
| `Username` | string | 必須 | `""` | 認証ユーザー |
| `Password` | string | 条件付き | `null` | FTP では必須。SFTP は鍵認証のみなら省略可 |
| `PrivateKeyPath` | string | 条件付き | `null` | SFTP 秘密鍵パス（SFTP では Password かどちらか必須） |
| `PrivateKeyPassphrase` | string | 任意 | `null` | 鍵のパスフレーズ |
| `HostKeyFingerprint` | string | 任意 | `null` | SFTP サーバー鍵指紋（未設定だと検証スキップ警告） |
| `RemotePath` | string | 必須 | `""` | リモート基準パス |
| `Concurrency` | int | 任意 | `1` | 並列転送数（1-16） |
| `PreserveFolderStructure` | bool | 任意 | `false` | サブフォルダ構造を維持して転送 |
| `TimeoutSeconds` | int | 任意 | `120` | 接続・転送タイムアウト秒（1-3600） |

### Retry

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `MaxAttempts` | int | 任意 | `3` | 再試行回数（0 以上） |
| `DelaySeconds` | int | 任意 | `5` | 初回再試行待ち秒（指数バックオフの基準） |

### Hash

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `Enabled` | bool | 任意 | `true` | ハッシュ検証を有効にする。`false` にするとリモートへのハッシュ取得通信が省略されネットワーク負荷が約半分になる |
| `Algorithm` | string | 必須 | `"SHA256"` | `SHA256` / `SHA512` |
| `UseServerCommand` | bool | 任意 | `false` | FTP: サーバー側ハッシュコマンドを試行。SFTP: 無効扱いでローカル計算 |

注意:

- `Enabled: false` のときは `DeleteAfterVerify: true` と組み合わせられません（起動時バリデーションでエラー終了）。
- `MD5` は無効です（起動時バリデーションでエラー終了）。
- SFTP は転送レイヤーで HMAC による整合性保証があるため、`Enabled: false` にしても実用上の問題は少ないです。

### 推奨設定テンプレート

#### 1. サブフォルダを安全にアップロード

```json
{
  "Watch": {
    "IncludeSubfolders": true
  },
  "Transfer": {
    "Direction": "put",
    "PreserveFolderStructure": true,
    "Concurrency": 4
  }
}
```

#### 2. SFTP 鍵認証 + ホスト鍵検証

```json
{
  "Transfer": {
    "Mode": "sftp",
    "Port": 22,
    "Username": "user",
    "PrivateKeyPath": "./id_ed25519",
    "HostKeyFingerprint": "a1b2c3d4..."
  }
}
```

#### 3. ENDファイル必須（END自体は転送しない）

```json
{
  "Watch": {
    "RequireEndFile": true,
    "TransferEndFiles": false,
    "EndFileExtensions": [".END", ".TRG"]
  }
}
```

### Cleanup

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `DeleteAfterVerify` | bool | 任意 | `false` | `put` 成功後、ローカルファイルを削除 |
| `DeleteRemoteAfterDownload` | bool | 任意 | `false` | `get` 成功後、リモートファイルを削除 |
| `DeleteRemoteEndFiles` | bool | 任意 | `false` | END ファイル成功時のリモート END ファイル削除 |

### Smtp

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `Enabled` | bool | 任意 | `false` | エラーメール通知有効化 |
| `RelayHost` | string | 任意 | `""` | SMTP リレー先 |
| `RelayPort` | int | 任意 | `25` | SMTP ポート |
| `UseTls` | bool | 任意 | `false` | TLS 使用 |
| `Username` | string | 任意 | `""` | SMTP 認証ユーザー |
| `Password` | string | 任意 | `""` | SMTP 認証パスワード |
| `From` | string | 任意 | `""` | 送信元メールアドレス |
| `To` | string[] | 実質必須 | `[]` | 宛先（1件以上。`Enabled: false` でも起動時検証対象） |

### Logging

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `Level` | string | 必須 | `"Information"` | `Trace`〜`None` |
| `RollingFilePath` | string | 必須 | `""` | ログファイル基準名（例: `logs/ftp-transfer-.log`） |
| `MaxBytes` | long | 任意 | `10485760` | ローテーション上限バイト（1024以上） |

## 重要な警告: 同名ファイル衝突

`Watch.IncludeSubfolders: true` かつ `Transfer.PreserveFolderStructure: false` では、  
サブフォルダを無視してファイル名だけで転送されるため、上書き衝突が起きます。

例:

- `watch/A/result.csv`
- `watch/B/result.csv`

この場合:

- `put`: どちらも `/remote/result.csv`
- `get`: どちらも `watch/result.csv`

起動時の設定警告にも次が出ます。

- `IncludeSubfolders is enabled for upload but PreserveFolderStructure is disabled. All files will be uploaded to remote root directory.`
- `IncludeSubfolders is enabled for download but PreserveFolderStructure is disabled. Files from subdirectories will be saved to root directory and may overwrite each other.`

推奨:

- サブフォルダを扱うなら `PreserveFolderStructure: true`
- もしくは全体でファイル名が重複しない命名規約にする

関連警告:

- `TransferEndFiles: true` かつ `RequireEndFile: false` の組み合わせでは、起動時に警告が表示されます。

## ENDファイル制御

`RequireEndFile: true` の場合、`data.txt.END` のように「データファイル名 + END拡張子」が必要です。

```json
{
  "Watch": {
    "RequireEndFile": true,
    "EndFileExtensions": [".END", ".TRG"],
    "TransferEndFiles": true
  }
}
```

挙動:

- データファイルは END 存在時のみ転送
- `TransferEndFiles: true` のとき END ファイルも転送
- 順序は「データ -> END」を保証
- 対応データがない END は転送しない

## 起動時バリデーションと終了コード

- DataAnnotations + 独自 `ConfigurationValidator` を起動時に実施
- エラーがある場合は内容を標準出力して終了コード `1` で終了
- 警告のみの場合は処理継続（警告を表示）

## コマンドライン上書き

`dotnet run` 時に設定を上書きできます。

```bash
dotnet run --project FtpTransferAgent -- --Transfer:Concurrency=4 --Hash:Algorithm=SHA512
```

## テスト

### 通常テスト

```bash
dotnet test FtpTransferAgent.sln --verbosity normal
```

### FTP統合テスト（ローカルFTPサーバー）

```bash
python -m pip install pyftpdlib
dotnet test FtpTransferAgent.Tests/FtpTransferAgent.Tests.csproj --verbosity normal
```

### SFTP統合テスト（Docker）

Docker Desktop 起動後に実行:

```bash
dotnet test FtpTransferAgent.Tests/FtpTransferAgent.Tests.csproj --filter "FullyQualifiedName~SftpClientDockerIntegrationTests"
```

補足:

- Docker が使えない環境では SFTP Docker テストは自動で Skip されます。

## よくある問題

### ファイルが転送されない

- `Watch.Path` が正しいか
- `AllowedExtensions` に対象拡張子が含まれるか
- `RequireEndFile: true` で対応 END が存在するか
- ファイルが他プロセスでロックされていないか

### 接続エラー

- `Mode` と `Port` の組み合わせ（FTP: 21 / SFTP: 22 が一般的）
- 認証情報（FTP は Password 必須、SFTP は Password か鍵）
- ファイアウォール/ネットワーク疎通

### ハッシュ不一致

- `UseServerCommand: false` で再確認
- 同名ファイル衝突設定になっていないか確認
- 転送中の上書き競合がないか確認

## セキュリティ推奨

- 可能な限り `sftp` を使用
- `HostKeyFingerprint` を設定して MITM リスクを下げる
- 秘密情報は `appsettings.json` 直書きより環境変数やシークレット管理を推奨

## 依存ライブラリ

- FluentFTP
- SSH.NET
- Polly
- Microsoft.Extensions.Hosting / Options / Logging

## ライセンス

MIT
