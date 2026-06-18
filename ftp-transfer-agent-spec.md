# FtpTransferAgent 詳細仕様書

## 1. 概要

FtpTransferAgent は、指定したローカルフォルダ内のファイルを FTP または SFTP サーバーへ一括転送（または逆方向でダウンロード）する .NET 8 ベースのコンソールアプリケーションです。**バッチ処理型**として設計されており、起動時に処理対象ファイルを検出し、設定に従って転送・検証・後処理を実行してから自動的に終了します。

### 主な特徴
- 🚀 **バッチ処理型**: 起動時に一度だけ処理を実行して終了（常駐アプリケーションではない）
- 📤📥 **FTP/SFTP 転送**（1 回の実行につき `put`＝アップロード または `get`＝ダウンロードのいずれか）
- 🔐 **SFTP 認証**（パスワード認証・秘密鍵認証・両者併用、パスフレーズ付き鍵対応）
- 🛡️ **SFTP ホスト鍵フィンガープリント検証**（SHA-256 / MD5 指紋による MITM 対策）
- 🔒 **ハッシュ値による整合性検証**（SHA256 / SHA512、常にローカル計算・不一致は即時失敗）
- 🔄 **Polly による自動再試行**（指数バックオフ、リトライ可否を例外種別で判定）
- ⚡ **並列転送**（最大 16 並列、Channel + TransferQueue 実装）
- 🎯 **ENDファイル制御**（Put/Get 両対応、データ先行転送の順序保証、大文字小文字を保持）
- 📡 **複数宛先への同時配信**（put 方向のファンアウト、全宛先成功時のみローカル削除）
- 🎯 **宛先別配信トラッキング**（複数宛先で未配信の宛先だけ再送、上書き検出付き・任意で有効化）
- 🗑️ **転送成功後の自動削除オプション**（ローカル/リモート、ENDファイル個別制御）
- 🔁 **二重起動防止**（PID ロックファイル方式）
- 📝 **ローリングファイルログ**（日付・サイズベースのローテーション、保持日数による自動削除）
- 📧 **エラー時の SMTP メール通知**（送信数上限・終了時ドレイン付き）
- ⚙️ **JSON による柔軟な設定**（DataAnnotations + 独自バリデーターによる起動時検証）

### 動作モード
本ツールは**バッチ処理専用**のアプリケーションです。`BackgroundService` を継承していますが、`ExecuteAsync` 内で一度だけ処理を実行し、完了後に `IHostApplicationLifetime.StopApplication()` を呼び出して終了します。

**重要**: リアルタイムなフォルダ監視機能はありません。新しく追加されたファイルを転送するには、アプリケーションを再度実行してください。継続運用には cron や Windows タスクスケジューラーでの定期実行を強く推奨します（[3.3](#33-定期実行の設定強く推奨) 参照）。なお、二重起動防止のためのロック機構を内蔵しているため、定期実行が前回分と重なっても安全です。

## 2. システム要件

### 必須要件
- **.NET 8 ランタイム** または **.NET 8 SDK**（自己完結発行時はランタイム不要、OS 依存ライブラリのみ）
- **対応OS**: Windows 10 / Windows Server 2016 以降、Linux（glibc 2.17 以上）、macOS 12 以降
- **メモリ**: 最小 512MB（推奨 1GB 以上）
- **ネットワーク**: FTP/SFTP サーバーへのアクセス

### 依存パッケージ
- **FluentFTP 52.1.0** - FTP 通信ライブラリ
- **SSH.NET 2025.0.0** - SFTP 通信ライブラリ
- **Polly 8.6.0** - 再試行・レジリエンス
- **Microsoft.Extensions.Hosting 9.0.6** - ホスティングフレームワーク
- **Microsoft.Extensions.Options.DataAnnotations 9.0.6** - 設定検証

## 3. インストールと起動

### 3.1 ソースコードからのビルド

```bash
# 依存関係の復元
dotnet restore

# ビルド（Release モード）
dotnet build --configuration Release

# 実行
dotnet run --project FtpTransferAgent
```

### 3.2 公開済みバイナリの作成と実行

```bash
# 自己完結型の実行ファイルを生成（サーバへ .NET ランタイム不要）
dotnet publish -c Release -r win-x64 --self-contained /p:PublishSingleFile=true
dotnet publish -c Release -r linux-x64 --self-contained /p:PublishSingleFile=true
dotnet publish -c Release -r osx-x64 --self-contained /p:PublishSingleFile=true

# 実行（Windows）
./FtpTransferAgent.exe

# 実行（Linux/macOS）
./FtpTransferAgent
```

> 自己完結発行でもOSレベルの依存ライブラリ（Linux の libssl / libicu / zlib など）は必要です。musl-libc ベース（Alpine 等）は `-r linux-musl-x64` でビルドしてください。

### 3.3 定期実行の設定（強く推奨）

本アプリケーションは起動時に一度だけ処理して終了するため、継続的な転送が必要な場合は定期実行を設定してください。二重起動はロックファイルで防止されます（[5.7](#57-二重起動防止ロックファイル) 参照）。

#### Linux/macOS - cron
```bash
# 5分ごとに実行
*/5 * * * * /opt/ftptransferagent/FtpTransferAgent
```

#### Windows - タスクスケジューラー
```powershell
schtasks /create /tn "FtpTransferAgent" /tr "C:\FtpTransferAgent\FtpTransferAgent.exe" /sc minute /mo 5
```

## 4. 設定ファイル（appsettings.json）

設定ファイルは **appsettings.json** という名前で、実行ファイルと同じディレクトリに配置します。`DOTNET_ENVIRONMENT=Development` のときは **appsettings.Development.json** が上書き読み込みされます（`dotnet run` では既定で Development）。本番デプロイ時は実値に書き換えた **appsettings.json のみ** を配置してください。

設定値は `--Section:Key=Value` 形式でコマンドラインから上書きできます。

```bash
dotnet run --project FtpTransferAgent -- --Transfer:Concurrency=4 --Hash:Algorithm=SHA512
```

### 4.1 設定ファイルの全体構造

```json
{
  "Watch": { ... },      // ローカルフォルダ設定
  "Transfer": { ... },   // 転送設定（接続先・方向・追加宛先）
  "App": { ... },        // アプリ全般（ロックファイル）
  "Retry": { ... },      // 再試行設定
  "Hash": { ... },       // ハッシュ検証設定
  "Cleanup": { ... },    // クリーンアップ設定
  "Smtp": { ... },       // メール通知設定
  "Logging": { ... }     // ログ設定
}
```

### 4.2 各セクションの詳細

#### 4.2.1 Watch（ローカルフォルダ設定）

| 項目 | 型 | 必須 | 既定値 | 説明 |
|------|-----|------|--------|------|
| Path | string | ✓ | なし | 監視（put）/保存（get）ディレクトリ |
| IncludeSubfolders | bool | - | false | サブフォルダも対象にする |
| AllowedExtensions | string[] | - | `[]` | 対象ファイルのフィルタ。拡張子（`"txt"` / `".txt"`）またはワイルドカード（`"*.txt"` / `"data_*.csv"` / `"?.log"`）を指定可能。空配列は全ファイル対象（起動時に警告）。大文字小文字は区別しない |
| RequireEndFile | bool | - | false | 対応する END ファイルがあるデータのみ転送対象にする |
| EndFileExtensions | string[] | - | `[".END", ".end"]` | END ファイルの拡張子一覧（ドット有無いずれも可） |
| TransferEndFiles | bool | - | false | データ転送の成功後に END ファイル自体も転送する |

**ENDファイル機能の詳細**は [5.4](#54-end-ファイル制御) を参照してください。

#### 4.2.2 Transfer（転送設定）

| 項目 | 型 | 必須 | 既定値 | 制約 | 説明 |
|------|-----|------|--------|------|------|
| Name | string | △ | null | - | 宛先の安定識別子。`PerDestinationDeliveryTracking` 有効時は primary・追加宛先のすべてで**必須かつ一意**。接続情報と独立した名前にする（[5.9](#59-宛先別配信トラッキングput) 参照） |
| Mode | string | ✓ | "ftp" | "ftp" / "sftp" | 転送プロトコル |
| Direction | string | ✓ | "put" | "put" / "get" | 転送方向（1 回の実行で片方向のみ） |
| Host | string | ✓ | なし | - | サーバーのホスト名/IP |
| Port | int | - | 21 | 1–65535 | 接続ポート（SFTP は通常 22） |
| Username | string | ✓ | なし | - | 認証ユーザー名 |
| Password | string | △ | null | - | FTP は必須。SFTP は鍵認証のみなら省略可 |
| PrivateKeyPath | string | - | null | - | SFTP 秘密鍵パス（SFTP では Password か本項目のいずれか必須） |
| PrivateKeyPassphrase | string | - | null | - | 秘密鍵のパスフレーズ |
| HostKeyFingerprint | string | - | null | - | SFTP サーバー鍵指紋。`SHA256:` プレフィックス付きは OpenSSH 形式の SHA-256 指紋（`ssh-keygen -lf` 出力）、プレフィックス無しは MD5 16 進指紋として照合。未設定時は検証スキップ警告 |
| RemotePath | string | ✓ | なし | - | リモート基準パス |
| Concurrency | int | - | 1 | 1–16 | primary 宛先の並列転送数 |
| PreserveFolderStructure | bool | - | false | - | サブフォルダ構造を保持して転送 |
| TimeoutSeconds | int | - | 120 | 1–3600 | 接続・転送タイムアウト秒 |
| PerDestinationDeliveryTracking | bool | - | false | - | 複数宛先で「未配信の宛先だけ再送」する宛先別配信トラッキングを有効化（put 方向のみ）。[5.9](#59-宛先別配信トラッキングput) 参照 |
| StateDirectory | string | - | `""` | - | 配信トラッキングのマーカー保存先。空のとき `LocalApplicationData/FtpTransferAgent/delivery-state/<watch パスのハッシュ>` を使用。`Watch.Path` の外に置くことを推奨 |
| DeliverySignatureMode | string | - | "sizetime" | "sizetime" / "hash" | 配信トラッキングで上書き検出に使う指紋方式。`sizetime`=サイズ+更新時刻（軽量）、`hash`=ファイルハッシュ（厳密だが保持ファイルごとに計算コスト） |
| AdditionalDestinations | object[] | - | `[]` | - | put 方向の追加送信先（[5.3](#53-複数宛先への同時配信ファンアウト) 参照）。各要素は Transfer と同じ接続系プロパティを持つ（`Direction` / `AdditionalDestinations` を除く）。`Name` / `Concurrency` / `TimeoutSeconds` / 認証はその宛先に個別適用 |

**認証方式の制約（DataAnnotations + バリデーターで実装）**:
- **FTP モード**: Password が必須
- **SFTP モード**: Password または PrivateKeyPath のいずれかが必須（両方指定も可）

#### 4.2.3 App（アプリ全般）

| 項目 | 型 | 必須 | 既定値 | 説明 |
|------|-----|------|--------|------|
| LockFilePath | string | - | `""` | 二重起動防止用ロックファイルパス。空の場合は `LocalApplicationData/FtpTransferAgent/ftp-transfer-agent.lock`（取得不能環境ではテンポラリ配下）を使用 |

#### 4.2.4 Retry（再試行設定）

転送エラー時の再試行動作を設定します。Polly の `WaitAndRetryAsync` による指数バックオフを使用します。

| 項目 | 型 | 必須 | 既定値 | 制約 | 説明 |
|------|-----|------|--------|------|------|
| MaxAttempts | int | - | 3 | 0 以上 | 最大再試行回数（初回試行に追加して行う回数。3 なら最大 4 回実行） |
| DelaySeconds | int | - | 5 | 0 以上 | 初回再試行の待機秒（指数バックオフの基準） |

**実際の待機時間**: `DelaySeconds × 2^(再試行番号−1)`（1 回目の再試行＝`DelaySeconds`、2 回目＝`×2`、3 回目＝`×4`…）。**上限は 300 秒**でキャップされ、オーバーフローを防ぎます。再試行はリトライ可能な例外に限り行われます（[5.6](#56-エラーハンドリングと再試行) 参照）。

#### 4.2.5 Hash（ハッシュ検証設定）

転送後の整合性検証に使用します。リモート・ローカル双方のハッシュを**常にローカルで計算**して比較し、不一致時は即時に転送失敗（再試行対象）とします。

| 項目 | 型 | 必須 | 既定値 | 制約 | 説明 |
|------|-----|------|--------|------|------|
| Enabled | bool | - | true | - | ハッシュ検証を有効にする。`false` にするとリモートハッシュ取得通信が省略され、ネットワーク負荷が約半分になる |
| Algorithm | string | ✓ | "SHA256" | "SHA256" / "SHA512" | ハッシュアルゴリズム。`MD5` 等は受け付けず起動時にエラー終了 |
| UseServerCommand | bool | - | false | - | FTP ではサーバー側ハッシュコマンドを試行（失敗時はローカル計算へフォールバック）。SFTP では無効扱いで常にローカル計算 |

**重要**:
- `Enabled: false` と `Cleanup.DeleteAfterVerify: true` は**併用できます**。この場合ハッシュ検証は行われず、転送（一時名→本番名へのリネーム）成功後にローカルファイルが削除されます。整合性チェックを伴わない削除になるため、FTP など転送レイヤーで整合性保証のない経路では、ハッシュ検証の有効化または SFTP の利用を推奨します。
- `MD5` は `Hash.Algorithm` として使用できません（`SHA256` / `SHA512` のみ）。
- SFTP は転送レイヤーで HMAC による整合性保証があるため、`Enabled: false` でも実用上の問題は少なくなります。

#### 4.2.6 Cleanup（クリーンアップ設定）

| 項目 | 型 | 必須 | 既定値 | 説明 |
|------|-----|------|--------|------|
| DeleteAfterVerify | bool | - | false | `put` 成功後にローカルファイルを削除。ファンアウト時は**全宛先成功時のみ** |
| DeleteRemoteAfterDownload | bool | - | false | `get` 成功後にリモートファイルを削除 |
| DeleteRemoteEndFiles | bool | - | false | END ファイル転送/取得の成功後にリモート END ファイルを削除 |
| DeleteLocalSkippedEndFiles | bool | - | false | `put` で `TransferEndFiles=false` のとき、転送成功したデータに対応する未転送 END ファイルをローカルから削除 |

#### 4.2.7 Smtp（メール通知設定）

`LogLevel.Error` 以上のログが発生した際にメール送信します。必須項目チェックは `Enabled: true` の場合のみ行われます。

| 項目 | 型 | 必須 | 既定値 | 説明 |
|------|-----|------|--------|------|
| Enabled | bool | - | false | メール通知を有効にするか |
| RelayHost | string | Enabled 時必須 | `""` | SMTP リレー先 |
| RelayPort | int | - | 25 | SMTP ポート（1–65535） |
| UseTls | bool | - | false | TLS を使用するか |
| Username | string | - | `""` | SMTP 認証ユーザー |
| Password | string | - | `""` | SMTP 認証パスワード |
| From | string | Enabled 時必須 | `""` | 送信元メールアドレス |
| To | string[] | Enabled 時必須 | `[]` | 宛先（1 件以上） |
| MaxEmailsPerRun | int | - | 100 | 1 回のバッチ実行で送信するエラーメール上限（洪水防止）。0 以下で無制限 |
| SuppressPerDestinationFailureDetailEmails | bool | - | false | 複数宛先での個別宛先失敗・部分失敗の詳細エラーメールを抑制する。ある宛先がメンテ等で継続的に失敗しても詳細通知を止め、設定不備・認証など他のエラーメールは送信し続ける。ファイルログ・終了コードには影響しない（[5.9](#59-宛先別配信トラッキングput) 参照） |

エラーメールは非同期送信ですが、プロセス終了時に送信中メールの完了を最大 15 秒待機するため、終了直前の通知も失われません。

#### 4.2.8 Logging（ログ設定）

| 項目 | 型 | 必須 | 既定値 | 説明 |
|------|-----|------|--------|------|
| Level | string | - | "Information" | 最小ログレベル（`Trace`〜`None`） |
| RollingFilePath | string | - | `""` | ログファイル基準名（例: `logs/ftp-transfer-.log`）。空でファイルログ無効（コンソールのみ） |
| MaxBytes | long | - | 10485760 | ローテーション上限バイト（1024 以上） |
| Retention.Enabled | bool | - | false | 起動時に古いログを削除するか |
| Retention.RetentionDays | int | - | 30 | 保持日数（1–3650）。これより古いログファイルと空になった年/月フォルダを起動時に削除 |

**ローリングログの動作**:
- ログは `logs/yyyy/MM/` 配下に日次で蓄積される
- 同日内でサイズが MaxBytes を超えると連番ファイルが作成される
- 日付・ローテーション・保持期間の判定はログ行のタイムスタンプと同じローカル時刻基準

## 5. 動作仕様

### 5.1 基本的な処理フロー

`Program.cs` と `Worker.cs` で実装される処理フローは以下のとおりです。

1. **起動時の初期化と検証**
   - 設定の読み込みと DataAnnotations 検証（`ValidateOnStart`）
   - 独自 `ConfigurationValidator` による横断的な整合性チェック（エラー/警告/情報）
   - ログ（コンソール + ローリングファイル + メール）の初期化、ログ保持クリーンアップ
   - 二重起動防止ロックの取得（取得失敗時は終了コード `2`）

2. **ファイル列挙**（Direction に応じて実行）
   - **"put"**: `Watch.Path` 内を `Directory.EnumerateFiles` で列挙し、ファイル名でソート
     - END ファイルは別管理。データファイルにのみフィルタ（拡張子/ワイルドカード）と END 必須条件を適用
   - **"get"**: `RemotePath` のファイル一覧を取得し、正規化パスでソート
     - END 判定・対応 END の存在確認はリモート一覧内で実施

3. **キューへの登録**
   - 列挙したデータファイルを容量制限付き `Channel<TransferItem>` に投入
   - put でファンアウト時は 1 データファイルを全宛先分のアイテムとして投入

4. **転送処理**（並列実行、`TransferQueue`）
   - 各宛先の `Concurrency` 数だけワーカーが起動し、キューから取得して転送
   - 転送ごとに専用クライアントを生成。重複処理を防止
   - 一時ファイル名（`.tmp.{GUID}`）で転送し、完了後に本番名へリネーム（**アトミック転送**）
   - 各転送に固有 ID を付与してログで追跡

5. **検証処理**（`Hash.Enabled: true` のとき）
   - ローカル/リモート双方のハッシュをローカル計算（FTP は `UseServerCommand` 有効時のみサーバーコマンドを試行）
   - `StringComparison.OrdinalIgnoreCase` で比較し、不一致は `HashMismatchException`（再試行対象）

6. **後処理**
   - 検証/転送成功時: 設定に従いローカル/リモートファイルや END ファイルを削除（ファンアウト時は全宛先成功が条件）
   - 失敗時: エラーログ出力（メール通知）。ファンアウト部分失敗時はローカルを保持し次回実行で再送

7. **終了**
   - パフォーマンス監視タスクを停止し `StopApplication()` を呼び出して終了
   - 転送失敗が 1 件でもあればプロセス終了コードを `1` に設定（[5.8](#58-起動時バリデーションと終了コード) 参照）

### 5.2 並列転送の仕組み（Channel + TransferQueue）

`Concurrency` 数のワーカーが `Channel<TransferItem>` からアイテムを取得し、並列に転送します。

- **重複処理防止**: 処理中アイテムを管理し二重処理を回避
- **Polly 再試行**: リトライ可能な例外のみ指数バックオフで再試行
- **例外分離**: 一部のファイルが失敗しても他の処理は継続（ワーカー単位で隔離）
- **リアルタイム監視**: 1 分間隔で進捗・メモリ使用量・長時間実行アイテムをログ出力

### 5.3 複数宛先への同時配信（ファンアウト）

`Transfer.AdditionalDestinations` を設定すると、**put 方向**で 1 ファイルを primary + 追加宛先の**全宛先**へ同時に送信します。

- 各宛先は専用のキューとクライアントを持ち、`Concurrency` を個別に適用
- **全宛先成功時のみ**ローカルファイルを削除（`Cleanup.DeleteAfterVerify: true` のとき）
- 1 つでも失敗した場合はローカルを保持し ERROR ログを出力 → 次回起動で再送
- `Direction: get` では追加宛先は使用されず、警告が表示される

**部分失敗時の再送ポリシーは 2 通りから選べます。**

- **既定（all-or-nothing）**: `PerDestinationDeliveryTracking: false`。部分失敗時、失敗宛先だけでなく**成功済み宛先にも再送**する。シンプルだが、ある宛先が長時間ダウンすると成功済み宛先へ重複配信が続く。
- **宛先別配信トラッキング**: `PerDestinationDeliveryTracking: true`。配信済みの宛先を記録し、次回バッチでは**未配信の宛先にだけ**再送する。詳細は [5.9](#59-宛先別配信トラッキングput) を参照。

### 5.4 END ファイル制御

END ファイルは「データファイル名 + END 拡張子」（例: `data.txt.END`）で、データの転送準備完了を示すマーカーです。

- **RequireEndFile: true**: 対応する END ファイルが存在するデータのみ転送対象にする
- **対象方向**: アップロード（put）/ダウンロード（get）の両方で有効
- **順序保証**: ファイル名でソートし、データファイルを先に処理する
- **対応データのない END ファイルは転送しない**
- **TransferEndFiles: true**: データ転送の成功後に END ファイル自体も転送する
  - 転送先・転送元の END ファイル名は**ディスク（またはリモート一覧）上の実際の大文字小文字を保持**する。設定 `EndFileExtensions` に大文字 `.END` が含まれていても、実体が小文字 `.end` なら `.end` のまま転送される
  - 転送成功後、ローカルの END ファイルは削除される
  - `Cleanup.DeleteRemoteEndFiles: true` で転送先 END ファイルも削除
- **TransferEndFiles: false**: END ファイルは転送されずローカルに残る。`Cleanup.DeleteLocalSkippedEndFiles: true` を併用すると、対応データの転送成功後に END ファイルをローカルから削除する
- **セキュリティ**: 異常に長い拡張子やパス区切りを含む END 拡張子は起動時に検出

### 5.5 整合性検証（ハッシュ）

- リモート・ローカルのハッシュを**常にローカルで計算**して比較（確実性重視）
- FTP は `UseServerCommand: true` のときのみサーバー側ハッシュコマンドを試行し、失敗時はローカル計算へフォールバック。SFTP はプロトコル上ファイル全体を取得してローカル計算
- 不一致時は `HashMismatchException` を送出。これは転送中の一過性破損で起こり得るため**再試行対象**
- `Hash.Enabled: false` のときは検証をスキップ（転送成功＝リネーム成功で判定）

### 5.6 エラーハンドリングと再試行

リトライ可否は `RetryableExceptionClassifier` が例外種別で判定します。

**再試行する例外（一時的・回復見込みあり）**:
- ネットワーク系: `SocketException` / `TimeoutException` / `HttpRequestException` / `SshConnectionException` / `SshOperationTimeoutException`
- `HashMismatchException`（再転送で回復し得る）
- 一時的な FTP エラー（メッセージに timeout / connection / network / busy / unavailable 等を含む。判定不能時は安全側でリトライ）
- 一時的な I/O エラー（共有違反・ロック違反・ディスクフル等。Win32 / errno 双方のコードを判定）、`UnauthorizedAccessException`（ファイルロック等の可能性）

**再試行しない例外（設定・セキュリティ・恒久的）**:
- `ArgumentException` / `ArgumentNullException` / `InvalidOperationException` / `DirectoryNotFoundException` / `SecurityException`
- 認証・権限・構文系の FTP エラー（メッセージに login / authentication / permission / not found / syntax 等を含む）

その他の例外は `InnerException` を再帰的に辿って判定します。

### 5.7 二重起動防止（ロックファイル）

短い間隔での定期実行が前回分と重ならないよう、PID ロックファイルで二重起動を防止します。

- 起動時に `App.LockFilePath`（未指定なら既定パス）を確認
- 既存ロックがあり、記録された PID のプロセスが**生存中**なら終了コード `2` で即終了
- 死んだ PID のロックは自動的に上書きして起動を継続
- 正常終了・異常終了のいずれでも `Dispose` でロックファイルを削除

### 5.8 起動時バリデーションと終了コード

- DataAnnotations + 独自 `ConfigurationValidator` を起動時に実施
- **終了コード `0`**: 正常終了
- **終了コード `1`**: 設定バリデーションエラー、または転送処理で 1 件以上の失敗を記録
- **終了コード `2`**: 二重起動を検出（ロック取得失敗）

警告のみの場合は処理を継続し、警告を表示します。

### 5.9 宛先別配信トラッキング（put）

複数宛先（[5.3](#53-複数宛先への同時配信ファンアウト)）で、ある宛先がメンテナンス等で一時的にダウンしても、**復旧済みの宛先には再送せず、未配信の宛先だけに送り直す**ための仕組みです。`Transfer.PerDestinationDeliveryTracking: true` で有効化します（put 方向のみ）。

**動作原理（マーカー方式）**:
- バッチはステートレスに毎回ファイルを列挙するため、「どのファイルをどの宛先まで送れたか」を `StateDirectory` 配下の小さな**マーカーファイル**で永続化します。
- マーカーは **部分失敗時にだけ** 作成されます（全宛先成功時は作られないため、通常時の追加コストはありません）。
- 起動時に状態ディレクトリを 1 回だけ走査してメモリに読み込み、以降はメモリ照合します（ファイルごとのディスク探索は行いません）。
- 次回バッチでは、各ファイルについて「配信済みの宛先」を除いた**未配信の宛先だけ**へ送信します。
- 全宛先のマーカーが揃った時点で、ローカルファイル・マーカーを削除します（`Cleanup.DeleteAfterVerify: true` のとき）。

**上書き（同名・別内容）への対応**:
- マーカーにはファイルの**指紋**（`DeliverySignatureMode`）を記録します。次回バッチで現在のファイル指紋とマーカーの指紋が一致する宛先のみ「配信済み」と判断します。
- 指紋が一致しない（＝内容が差し替わった）場合は配信済み扱いにせず、**全宛先へ再送**します（古いマーカーは破棄）。これにより、同名ファイルが上書きされたのに古い内容しか届いていない、という取りこぼしを防ぎます。
- `sizetime`（既定）はサイズと最終更新時刻で判定するため軽量ですが、**サイズが同一でタイムスタンプも据え置きの上書き**は検出できません。これを厳密に扱いたい場合は `hash` を選択してください（保持ファイルごとにハッシュ計算が必要）。

**宛先の識別（Name 必須）**:
- マーカーは宛先の `Name` をキーにします。そのため有効時は **primary を含む全宛先で `Name` が必須かつ一意**です（未設定・重複は起動時にエラー）。
- 接続情報（ホスト・ポート・パス）ではなく独立した `Name` を使うことで、サーバ移転やパス変更後も「同じ宛先」と認識し続けます。

**通知・終了コード**:
- 宛先失敗は既定どおりエラーとして記録され、エラーメール送信と終了コード `1` の対象になります。
- ある宛先が継続的に失敗してメールが煩わしい場合は `Smtp.SuppressPerDestinationFailureDetailEmails: true` で、個別宛先失敗・部分失敗の詳細メールを抑制できます。設定不備・認証エラーなど他のエラーメールは送信され続けます。**ファイルログと終了コードには影響しません**（障害は引き続き記録・検知できます）。

**注意・既知の限界**:
- `Cleanup.DeleteAfterVerify: false` の場合、全宛先へ配信済みでもローカルを残すため、マーカーも保持され続けます（その分は再送をスキップします）。
- クラッシュで「配信完了したがマーカー記録前」に終了した場合、次回その宛先へ再送されます（一時名→本番名のアトミック転送のため上書きで済み、安全側に倒れます）。
- 詳細な設計・対策・懸念は別冊 **[docs/per-destination-delivery-tracking.md](docs/per-destination-delivery-tracking.md)** を参照してください。

## 6. 実装状況と制限事項

### 6.1 実装済みの機能
- ✅ **転送方向**: put / get（1 実行で片方向）
- ✅ **並列転送**: 最大 16 並列（TransferQueue + Channel）
- ✅ **複数宛先ファンアウト**: put 方向で全宛先同時配信、all-or-nothing 再送
- ✅ **宛先別配信トラッキング**: 未配信の宛先だけ再送、指紋による上書き検出、宛先失敗メールの選択的抑制（任意で有効化）
- ✅ **ENDファイル制御**: Put/Get 両対応、順序保証、大文字小文字保持
- ✅ **ハッシュ検証**: SHA256 / SHA512、ローカル計算、不一致は再試行、Enabled 切替
- ✅ **スマート再試行**: 例外種別でリトライ可否を判定
- ✅ **二重起動防止**: PID ロックファイル
- ✅ **ローリングログ + 保持日数**: 日付/サイズローテーション、古いログの自動削除
- ✅ **SMTP 通知**: 送信数上限・終了時ドレイン
- ✅ **SFTP 鍵認証・ホスト鍵検証**

### 6.2 既知の制限
- **バッチ処理専用**: 一度の実行で完了して終了する（常駐しない）
- **リアルタイム監視なし**: 新規ファイルの転送には再実行が必要
- **転送の中断/再開・履歴の永続化は未対応**（部分失敗は次回実行で再送。複数宛先では `PerDestinationDeliveryTracking` で未配信先のみ再送可能）
- **配信トラッキングの上書き検出は指紋方式に依存**: `sizetime` はサイズ・更新時刻据え置きの上書きを検出できない（`hash` で厳密化可能、[5.9](#59-宛先別配信トラッキングput) 参照）
- **SFTP ハッシュ取得**: プロトコル上ファイル全体の取得が必要
- **同時接続数**: サーバー側の制限に依存

### 6.3 推奨される使用方法
- **定期実行**: cron や Windows タスクスケジューラーで定期実行（**強く推奨**）
- **ワークフロー統合**: 他バッチ処理の一部として組み込む
- **イベントドリブン**: ファイル生成完了後に実行（END ファイル制御の併用）

## 7. トラブルシューティング

### 7.1 ファイルが転送されない
- 拡張子フィルタ（`AllowedExtensions`）に一致しているか
- `Watch.Path` が正しいか
- `RequireEndFile: true` で対応する END ファイルが存在するか
- ファイルが他プロセスでロックされていないか

### 7.2 すぐに終了して何も転送されない（終了コード 2）
- 二重起動防止ロックが取得できていない可能性。前回プロセスが生存中か、`App.LockFilePath` の権限を確認

### 7.3 転送エラーが発生する
- `Mode` と `Port` の組み合わせ（FTP: 21 / SFTP: 22 が一般的）
- 認証情報（FTP は Password 必須、SFTP は Password か鍵）
- ファイアウォール/ネットワーク疎通
- 並列接続数が多すぎる場合は `Concurrency` を下げる

### 7.4 ハッシュ不一致
- `UseServerCommand: false` で再確認
- 同名ファイル衝突設定（`IncludeSubfolders: true` かつ `PreserveFolderStructure: false`）になっていないか
- 転送中の上書き競合がないか

### 7.5 デバッグ
`DOTNET_ENVIRONMENT=Development` の設定、または `--Logging:Level=Debug` でより詳細なログを出力できます。

## 8. セキュリティに関する注意事項

- **認証情報**: appsettings.json への平文保存は避け、環境変数やシークレット管理の利用を推奨
- **暗号化**: 機密データには FTP ではなく SFTP を推奨。SMTP は `UseTls: true` を推奨
- **ホスト鍵検証**: SFTP では `HostKeyFingerprint` を設定して MITM リスクを低減（`SHA256:` 形式を推奨）
- **パストラバーサル対策**: ダウンロード時のローカルパスはセグメント単位で `..` を検査し、監視ディレクトリ外への書き込みを拒否
- **ファイル権限**: `chmod 600 appsettings.json` / `chmod 600 <秘密鍵>` 等で保護

## 9. 今後の開発予定

以下は将来的な検討項目です。

1. **リアルタイムフォルダ監視**（ファイル作成・変更時の即時転送）
2. **転送履歴の永続化**（重複転送の防止、再開）
3. **Web UI / API**（転送状況の確認、設定の動的変更）
4. **高度な転送制御**（優先度付きキュー、帯域制限）

## 10. ライセンス

本ソフトウェアは MIT ライセンスの下で公開されています。

---

**更新日**: 2026年6月18日
**バージョン**: 3.1.0
**主な更新内容 (3.1.0)**:
- **宛先別配信トラッキング**（[5.9](#59-宛先別配信トラッキングput)）を追加。複数宛先で未配信の宛先だけを再送する任意機能。マーカー方式・指紋による上書き検出・`Name` 必須/一意・`StateDirectory`/`DeliverySignatureMode` 設定を追記
- `Smtp.SuppressPerDestinationFailureDetailEmails`（複数宛先の個別宛先失敗・部分失敗の詳細メール抑制）を追記
- 設計の詳細・対策・懸念は別冊 [docs/per-destination-delivery-tracking.md](docs/per-destination-delivery-tracking.md) を参照

**主な更新内容 (3.0.0)**:
- 実装に合わせて仕様書を全面改訂
- `Hash.Enabled` 設定を追記。`Hash.Enabled: false` と `Cleanup.DeleteAfterVerify: true` は併用可能（ハッシュ検証なしで転送成功後に削除）であることを明記
- ENDファイル転送が**ディスク上の実際の大文字小文字を保持**する仕様を明記
- 複数宛先ファンアウト、二重起動防止（終了コード 2）、SFTP ホスト鍵フィンガープリント検証、ログ保持日数、`AllowedExtensions` のワイルドカード、`Smtp.MaxEmailsPerRun`、`Cleanup.DeleteLocalSkippedEndFiles`、`Transfer.TimeoutSeconds`/`AdditionalDestinations` を追記
- 再試行の指数バックオフ（上限 300 秒）とリトライ可否の例外分類を明記
- 実態に存在しない `FolderWatcher` への言及を削除し、終了コード仕様を整理
