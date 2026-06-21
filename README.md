# FtpTransferAgent

.NET 10 で動作するバッチ型のファイル転送ツールです。  
指定ディレクトリ内のファイルを FTP/SFTP で転送し、転送後にハッシュ検証を行います。

## 概要

- 起動時に 1 回だけ処理して終了します（常駐監視はしません）
- `put`（アップロード）/`get`（ダウンロード）に対応します
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
- **二重起動防止**（PID ロックファイル方式）
- **ファイル名ワイルドカード指定**（`*.txt`、`data_*.csv` など）
- **複数宛先への同時配信**（put 方向のファンアウト）
- **ログ保持日数指定**（古いログの自動削除）

## 動作環境

### 自己完結・単一ファイル発行時（`--self-contained`）

自己完結で発行した場合、サーバへの **.NET ランタイムのインストールは不要**です。  
ただし OS レベルの依存ライブラリは必要です。

#### Linux (x64 / arm64)

| 依存ライブラリ | 用途 | 備考 |
|---|---|---|
| **glibc 2.27 以上** | .NET 10 の動作基盤 | Ubuntu 22.04以降 / RHEL 8以降が対象（CentOS 7 は対象外） |
| **libssl (OpenSSL 1.1 または 3.x)** | FTP over TLS / SFTP (SSH.NET) | このアプリでは必須 |
| **libicu** | グローバリゼーション処理 | `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` で無効化可能 |
| **libz (zlib)** | 圧縮処理 | ほぼ全ディストロに標準搭載 |

> Alpine Linux など **musl-libc** ベースのディストロは glibc と互換性がありません。使用する場合は `-r linux-musl-x64` でビルドが必要です。

#### Windows

- **Windows 10 (1809) / Windows Server 2016 以降**
- Windows 7/8.1 は .NET 10 の対象外のため非対応（Windows Server 2012/2012 R2 は ESU 環境でのみ対応）

#### macOS

- **macOS 14 (Sonoma) 以降**

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

- **開発・ビルド時**: .NET 10 SDK
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
    "DeleteAfterVerify": true,
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
| `appsettings.backup.json` | されない | `appsettings.json` のバックアップコピー。アプリは参照せず、ビルド出力・publish にもコピーされない |
| `appsettings.invalid.json` | されない | 意図的に不正な JSON を含む設定バリデーションのテスト用サンプル。ビルド出力・publish にもコピーされない |

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
| `AllowedExtensions` | string[] | 任意 | `[]` | 対象ファイルのフィルタ。拡張子 (`"txt"` / `".txt"`) またはワイルドカード (`"*.txt"` / `"data_*.csv"`) を指定可能。空配列は全ファイル対象（起動時警告あり） |
| `RequireEndFile` | bool | 任意 | `false` | 対応する END ファイルがあるデータのみ転送 |
| `EndFileExtensions` | string[] | 任意 | `[".END", ".end"]` | END 拡張子一覧 |
| `TransferEndFiles` | bool | 任意 | `false` | 対応するデータ転送の成功後に END ファイル自体も転送する |

### Transfer

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `Mode` | string | 必須 | `"ftp"` | `ftp` / `sftp` |
| `Direction` | string | 必須 | `"put"` | `put` / `get` |
| `Host` | string | 必須 | `""` | 接続先ホスト |
| `Port` | int | 任意 | `21` | 接続ポート（SFTP は通常 22） |
| `Username` | string | 必須 | `""` | 認証ユーザー |
| `Password` | string | 条件付き | `null` | FTP では必須。SFTP は鍵認証のみなら省略可 |
| `PrivateKeyPath` | string | 条件付き | `null` | SFTP 秘密鍵パス（SFTP では Password かどちらか必須） |
| `PrivateKeyPassphrase` | string | 任意 | `null` | 鍵のパスフレーズ |
| `HostKeyFingerprint` | string | 任意 | `null` | SFTP サーバー鍵指紋（未設定だと検証スキップ警告）。`SHA256:` プレフィックス付きで OpenSSH 形式の SHA-256 指紋（`ssh-keygen -lf` の出力）、プレフィックス無しで MD5 16 進指紋として照合 |
| `RemotePath` | string | 必須 | `""` | リモート基準パス |
| `Concurrency` | int | 任意 | `1` | primary 宛先の並列転送数（1-16）。`get` と primary への `put` に適用。`AdditionalDestinations` は各要素の `Concurrency` を個別に使用 |
| `PreserveFolderStructure` | bool | 任意 | `false` | サブフォルダ構造を維持して転送 |
| `TimeoutSeconds` | int | 任意 | `120` | 接続・転送タイムアウト秒（1-3600） |
| `KeepAliveSeconds` | int | 任意 | `0` | 接続再利用時のアイドル切断防止（秒、0-3600、`0`=無効）。`>0` で SFTP は `KeepAliveInterval`、FTP は NOOP 送信（`NoopInterval`）+ TCP KeepAlive を有効化。`AdditionalDestinations` の各宛先にも個別適用 |
| `Name` | string | 複数宛先で必須 | `null` | 宛先の安定識別子。**複数宛先（ファンアウト）の put では primary・追加宛先すべてで必須かつ一意**（配信トラッキングのマーカーキー）。接続情報と独立した名前にする |
| `AdditionalDestinations` | object[] | 任意 | `[]` | put 方向の追加送信先。各要素は Transfer と同じ接続系プロパティを持つ（`Direction` / `AdditionalDestinations` を除く）。各宛先の `Name` / `Concurrency` / `TimeoutSeconds` / 認証設定はその宛先に個別適用される。1 ファイルをメイン + 追加宛先の全てへ同時に送信する。**複数宛先では配信トラッキングが常時有効**で、部分失敗時は成功済み宛先を記録し、次回は未配信の宛先だけへ再送する（成功済み宛先への一括再送はしない）。同一の mode/host/port/remote path に複数宛先が向く設定は起動時に警告される |
| `PerDestinationDeliveryTracking` | bool | 任意 | `false` | **単一宛先**で配信トラッキングを明示的に有効化する場合に使用。複数宛先では常時有効のためこのフラグに関わらずトラッキングされる（put 方向のみ） |
| `StateDirectory` | string | 任意 | `""` | 配信トラッキングのマーカー保存先。空なら `LocalApplicationData/FtpTransferAgent/delivery-state/<hash>` を使用 |
| `RetryDirectory` | string/null | 任意 | `null` | 配信トラッキングで部分失敗したファイルの移動先。未指定/null は `LocalApplicationData/FtpTransferAgent/delivery-retry/<watch hash>` を使う。相対パスを明示した場合は `Watch.Path` 配下として解決し、空文字で移動を無効化する |
| `DeliverySignatureMode` | string | 任意 | `"sizetime"` | 配信トラッキングの上書き検出方式。`sizetime`（サイズ+更新時刻）または `hash`（ファイルハッシュ、厳密） |
| `EnableUploadSnapshot` | bool | 任意 | `false` | 各宛先へ確実に同一内容を届けるため、転送前にデータ/関連 END ファイルを一時スナップショットへ複製する。既定 `false` ではライブのソースを直接読み一時コピーの I/O を避ける（転送中の変更は転送後に検出して保持・次回持ち越し）。トラッキング有効時のみ作用する |

複数宛先の put、または単一宛先で `PerDestinationDeliveryTracking: true` のとき、一部宛先だけ失敗すると成功済み宛先のマーカーを残したうえで対象ファイルを `RetryDirectory` へ退避します。関連 END ファイルも同時に扱い、移動途中で失敗した場合は完了済みの移動を Watch 側へ戻します。全宛先への配信が完了したら、`DeleteAfterVerify: true`（既定）ではファイルを削除し、`false` では `Watch.Path` の元の位置へ復元します（隠しフォルダに取り残しません）。`sizetime` 署名は退避・復元でファイルの更新時刻が変わらないよう、移動時に元の更新時刻を保持します。`DeleteAfterVerify: false` でローカルを残す場合、保持マーカーの指紋は次回列挙時に再計算される指紋に合わせて記録するため、成功後に END ファイルを削除する構成（`TransferEndFiles` / `DeleteLocalSkippedEndFiles`）でも配信済みファイルが毎回再送されることはありません。

既定では各宛先はライブのソースを直接読みます。`EnableUploadSnapshot: true` を設定すると、各宛先へ確実に同一内容を届けるために列挙時のデータ/関連 END ファイルを一時スナップショット化してからアップロードします（転送中にソースが変更されても宛先間で内容が割れません）。関連 END ファイルがある場合は END 側の指紋も配信トラッキングの判定に含めます。スナップショットの有無にかかわらず、転送完了処理前、または全宛先配信済みとして送信をスキップした後の cleanup 前に、元ファイルや関連 END ファイルが変更された場合、その実行ではローカル削除や retry 退避を行わず、終了コード `1` で次回実行に持ち越します（スナップショット無効時は、その回に限り宛先間で内容が割れ得ます）。マーカー書き込みなど完了処理の失敗も転送失敗として扱われます。

正常終了・例外終了では一時ファイル（アップロードスナップショット、ダウンロードの検証用一時ファイル）はその実行内で削除されます。プロセスが強制終了・電源断で異常終了した場合に残った一時ファイルは、次回起動時に掃除します。いずれもエージェント専用ディレクトリに隔離して作るため、起動時の掃除は専用ディレクトリの残骸だけを対象にし、**利用者ファイルには一切触れません**（名前が一時ファイルの命名規則に似ていても削除しません）。アップロードスナップショットは `Watch.Path` ごとに分離したテンポラリ配下（`<TEMP>/FtpTransferAgent/upload-snapshots/<watch パスのハッシュ>`）に作られ、起動時に同一構成の残骸だけを削除します。ダウンロード（get）の検証用一時ファイルは `Watch.Path` 配下の専用ディレクトリ（`.ftptransferagent-tmp`）に作られ（最終ファイルと同一ボリュームのためアトミックなリネームは維持）、起動時にこのディレクトリの残骸だけを削除します。いずれも二重起動防止ロックにより同一構成の並走が無いため、進行中の転送を壊しません。

### App

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `LockFilePath` | string | 任意 | `""` | 二重起動防止用のロックファイルパス。空の場合は `Watch.Path` ごとに分離した既定パス（`LocalApplicationData/FtpTransferAgent/locks/<watch パスのハッシュ>/ftp-transfer-agent.lock`、取得できない環境ではテンポラリ配下）を使用する。異なる監視フォルダを別スケジュールで並走させても相互ブロックしない |

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

- `Enabled: false` と `DeleteAfterVerify: true` は併用できます。この場合ハッシュ検証は行われず、転送（一時名→本番名へのリネーム）成功後にローカルファイルが削除されます。整合性チェックを伴わない削除になるため、FTP など転送レイヤーで整合性保証のない経路では、ハッシュ検証の有効化または SFTP の利用を推奨します。
- `MD5` は使用できません。`Hash.Algorithm` は `SHA256` / `SHA512` のみ受け付け、それ以外は起動時バリデーション（DataAnnotations）でエラー終了します。
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
    "HostKeyFingerprint": "SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og"
  }
}
```

`HostKeyFingerprint` は `ssh-keygen -lf <ホスト鍵ファイル>` で表示される SHA-256 形式 (`SHA256:...`) を推奨します。従来の MD5 16 進指紋（コロン区切り可）も後方互換として使用できます。

#### 3. ENDファイル必須（END自体は転送しない）

```json
{
  "Watch": {
    "RequireEndFile": true,
    "TransferEndFiles": false,
    "EndFileExtensions": [".END", ".TRG"]
  },
  "Cleanup": {
    "DeleteLocalSkippedEndFiles": true
  }
}
```

- `TransferEndFiles: false` のとき、END ファイルは転送されずローカルに残る
- `DeleteLocalSkippedEndFiles: true` を併用すると、対応するデータ転送が成功した後に END ファイルもローカルから削除される

#### 4. ワイルドカードでファイル指定

```json
{
  "Watch": {
    "AllowedExtensions": ["*.txt", "data_*.csv", "log"]
  }
}
```

- `*.txt` / `data_*.csv` のようなグロブと、従来の拡張子指定 (`"log"` / `".log"`) を混在可能
- 大文字小文字は区別しない

#### 5. 複数宛先への同時配信（put のみ）

```json
{
  "Transfer": {
    "Name": "primary",
    "Mode": "sftp",
    "Direction": "put",
    "Host": "primary.example.com",
    "Port": 22,
    "Username": "user",
    "PrivateKeyPath": "./id_ed25519",
    "RemotePath": "/in",
    "AdditionalDestinations": [
      {
        "Name": "backup",
        "Mode": "ftp",
        "Host": "backup.example.com",
        "Port": 21,
        "Username": "user",
        "Password": "pass",
        "RemotePath": "/inbox",
        "Concurrency": 2,
        "TimeoutSeconds": 60
      }
    ]
  }
}
```

- メイン + 追加宛先の**全宛先**へ同時に送信
- **複数宛先では宛先別配信トラッキングが常時有効**: 各宛先に**一意な `Name` が必須**で、配信済みの宛先を記録し、次回バッチでは**未配信の宛先だけ**へ再送する（成功済み宛先への一括再送はしない）
- 既定ではライブのソースを直接読む。`EnableUploadSnapshot: true` で、各宛先へ同一内容を送るために列挙時のデータ/関連 END ファイルを一時スナップショット化してから転送する（転送中の変更でも宛先間で内容が割れない）
- **全宛先成功時に**ローカルファイルを削除（`Cleanup.DeleteAfterVerify: true`、既定）。`false` の場合は元ファイルを保持
- 一部宛先だけ失敗した場合、対象ファイルは `RetryDirectory` へ退避し、未配信宛先へ再送し続ける。全宛先完了後、`DeleteAfterVerify: false` なら `Watch.Path` の元の位置へ戻す
- 転送完了処理前に元ファイルや関連 END ファイルが変更された場合は、ローカル削除・retry 退避を行わず、終了コード `1` で次回実行に持ち越す（スナップショット無効時はその回に限り宛先間で内容が割れ得る）
- 同一の mode/host/port/remote path に複数宛先が向く設定は、同時上書きや END ファイル重複配信の恐れがあるため起動時に警告される
- `Direction: get` では追加宛先は使用されず、警告が表示される
- 詳細は [宛先別配信トラッキングのドキュメント](docs/per-destination-delivery-tracking.md) を参照
- 実装（ファンアウトのキュー構成・並列処理の仕組み・マーカーの作成条件と保存先）は [ファンアウトと並列処理 実装詳細](docs/fanout-and-parallelism.md) を参照

### Cleanup

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `DeleteAfterVerify` | bool | 任意 | `true` | `put` 成功後（複数宛先では全宛先成功後）、ローカルファイルを削除。`false` で元ファイルを保持 |
| `DeleteRemoteAfterDownload` | bool | 任意 | `false` | `get` 成功後、リモートファイルを削除 |
| `DeleteRemoteEndFiles` | bool | 任意 | `false` | END ファイル成功時のリモート END ファイル削除 |
| `DeleteLocalSkippedEndFiles` | bool | 任意 | `false` | `put` で `TransferEndFiles=false` のとき、転送成功したデータに対応する未転送 END ファイルをローカルから削除する |

### Smtp

接続・宛先の必須チェックは `Enabled: true` の場合のみ行われます。メール通知を使わない場合は `Smtp` セクションを省略しても起動できます。

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `Enabled` | bool | 任意 | `false` | エラーメール通知有効化 |
| `RelayHost` | string | Enabled 時必須 | `""` | SMTP リレー先 |
| `RelayPort` | int | 任意 | `25` | SMTP ポート（1-65535） |
| `UseTls` | bool | 任意 | `false` | TLS 使用 |
| `Username` | string | 任意 | `""` | SMTP 認証ユーザー |
| `Password` | string | 任意 | `""` | SMTP 認証パスワード |
| `From` | string | Enabled 時必須 | `""` | 送信元メールアドレス |
| `To` | string[] | Enabled 時必須 | `[]` | 宛先（1件以上） |
| `MaxEmailsPerRun` | int | 任意 | `100` | 1 回のバッチ実行で送信するエラーメールの上限（メール洪水防止）。0 以下で無制限 |

エラーメールは非同期送信ですが、プロセス終了時には送信中のメールの完了を最大 15 秒待機するため、バッチ終了直前のエラー通知も失われません。

### Logging

| 項目 | 型 | 必須 | 既定値 | 説明 |
|---|---|---|---|---|
| `Level` | string | 必須 | `"Information"` | `Trace`〜`None` |
| `RollingFilePath` | string | 任意 | `""` | ログファイル基準名（例: `logs/ftp-transfer-.log`）。空にするとファイルログ無効（コンソールのみ） |
| `MaxBytes` | long | 任意 | `10485760` | ローテーション上限バイト（1024以上） |
| `Retention.Enabled` | bool | 任意 | `false` | 起動時に古いログを削除する。`false` なら削除しない |
| `Retention.RetentionDays` | int | 任意 | `30` | 保持日数（1-3650）。この日数より古いログファイルと、空になった年/月フォルダを起動時に削除する |

ログファイル名の日付・ローテーション・保持期間の判定は、ログ行のタイムスタンプと同じローカル時刻基準です。

## 重要な警告: 同名ファイル衝突

`Watch.IncludeSubfolders: true` かつ `Transfer.PreserveFolderStructure: false` では、  
サブフォルダを無視してファイル名だけで転送されるため、上書き衝突が起きます。

例:

- `watch/A/result.csv`
- `watch/B/result.csv`

この場合:

- `put`: どちらも `/remote/result.csv`
- `get`: どちらも `watch/result.csv`

起動時の設定エラーとして次が出ます。

- `IncludeSubfolders cannot be enabled for upload when any destination has PreserveFolderStructure=false (...). Local files with the same name in different subdirectories may overwrite each other remotely.`
- `IncludeSubfolders cannot be enabled for download when PreserveFolderStructure is false. Remote files from different subdirectories may overwrite the same local file.`

推奨:

- サブフォルダを扱うなら `PreserveFolderStructure: true`
- もしくは全体でファイル名が重複しない命名規約にする

関連警告:

- `TransferEndFiles: true` かつ `RequireEndFile: false` の組み合わせでは、起動時に警告が表示されます。対応するデータが転送対象になった END ファイルだけが転送されます。

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
- `TransferEndFiles: true` のとき END ファイルも転送（転送成功後にローカルの END ファイルを削除）
- 順序は「データ -> END」を保証
- 対応データがない END は転送しない
- `TransferEndFiles: false` かつ `Cleanup.DeleteLocalSkippedEndFiles: true` のとき、転送成功したデータに対応する END ファイルをローカルから削除する

## 二重起動防止（ロックファイル）

タスクスケジューラから短い間隔で実行する場合、前回実行分と重ならないよう PID ロックファイルで二重起動を防止します。

- 起動時に `App.LockFilePath`（未指定なら `Watch.Path` ごとに分離した既定パス `LocalApplicationData/FtpTransferAgent/locks/<watch ハッシュ>/ftp-transfer-agent.lock`、取得できない環境ではテンポラリ配下）を確認
- 既存ロックがあり、書かれている PID のプロセスが**生存中**なら終了コード `2` で即終了
- 死んだ PID のロックは自動的に上書きして起動を継続
- 正常終了・異常終了のいずれでも `Dispose` でロックファイルは削除される

```json
{
  "App": {
    "LockFilePath": "C:/ProgramData/FtpTransferAgent/agent.lock"
  }
}
```

## ログ保持日数

ローリングログは `logs/yyyy/MM/` 配下に日次蓄積されるため、長期運用ではディスク圧迫の要因になります。`Logging.Retention` で起動時の自動削除を有効化できます。

```json
{
  "Logging": {
    "RollingFilePath": "logs/ftp-transfer-.log",
    "Retention": {
      "Enabled": true,
      "RetentionDays": 30
    }
  }
}
```

- 起動時、ファイル名中の `YYYYMMDD` をパースして保持日数より古いファイルを削除
- パース不能なファイル名や、プレフィックスが異なるファイルはスキップ
- 削除により空になった月・年フォルダも併せて除去
- `Enabled: false` （既定）なら従来どおり削除せず蓄積を継続

## 起動時バリデーションと終了コード

- DataAnnotations + 独自 `ConfigurationValidator` を起動時に実施
- エラーがある場合は内容を標準出力して終了コード `1` で終了
- 警告のみの場合は処理継続（警告を表示）
- 転送処理で 1 件でも失敗が記録された場合は終了コード `1` で終了
- 二重起動検出時は終了コード `2` で終了

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
