# ローカル / UNC 宛先モード（Mode=local）詳細解説

> ローカルフォルダや LAN の共有フォルダ（UNC / SMB 共有）へ、本ツールの**アトミック転送・ハッシュ検証・複数宛先配信付き**でファイルを書き込む `Mode=local` の設計・背景・制約をまとめた資料。
> 主な実装: [`Services/LocalFileTransferClient.cs`](../FtpTransferAgent/Services/LocalFileTransferClient.cs) / [`Worker.cs`](../FtpTransferAgent/Worker.cs)（`CreateClientCore` / `BuildRemotePath` / `DescribeDestination`） / [`Configuration/ConfigurationValidator.cs`](../FtpTransferAgent/Configuration/ConfigurationValidator.cs)（`ValidatePrimaryModeRequirements`）

---

## 1. 何を解決する機能か

### 1.1 背景

旧ツール（FileTransFTP）は **CIFS** で「ローカル → LAN の共有フォルダへの書き込み」を行っていた。FtpTransferAgent は当初 FTP / SFTP のみ対応だったため、**転送先がファイルパス（共有フォルダ）になる用途**を直接は表現できなかった（`put` の宛先は FTP/SFTP サーバ固定）。

一方で「CIFS（= SMB1）」はセキュリティ的に非推奨（Microsoft が既定で無効化、WannaCry の感染経路）であり、**SMB1 プロトコルを再実装するのは避けたい**。

### 1.2 やりたいこと

- 旧ツールの「ローカル → 共有フォルダ書き込み」の用途を引き継ぐ。
- ただし **CIFS / SMB1 を一切実装・有効化しない**。
- 本ツールの強み（アトミック転送・ハッシュ検証・自動リトライ・複数宛先配信）を共有フォルダ宛でも享受する。

### 1.3 解決方法

**共有フォルダへの書き込みに専用プロトコルは要らない**。`\\server\share` のような UNC パスや `Z:\`（マップドライブ）は、OS が SMB を透過処理する**ただのファイルパス**である。したがって `Mode=local` のクライアントは、通信プロトコルではなく **OS のファイル I/O** で完結する。

- 実際にネットワークを流れる SMB の版は **OS が自動交渉**する（現代の Windows 同士なら SMB2/3）。本ツールは SMB1 を持ち込まない。
- 「`\\server\share` を `RemotePath` に書く」という自然な指定で、共有フォルダが転送先になる。

---

## 2. 使い方

### 2.1 単一宛先（ローカル → 共有フォルダ）

```json
{
  "Watch": { "Path": "C:\\export", "AllowedExtensions": [ ".csv" ] },
  "Transfer": {
    "Mode": "local",
    "Direction": "put",
    "RemotePath": "\\\\fileserver\\share\\incoming"
  },
  "Hash": { "Enabled": true, "Algorithm": "SHA256" },
  "Cleanup": { "DeleteAfterVerify": true }
}
```

- `Mode`: `local`
- `RemotePath`: 書き込み先ディレクトリ（**絶対パスまたは UNC パス**）。例: `\\fileserver\share\incoming`、`D:\out`、`/mnt/share/in`
- `Host` / `Username` / `Password` / `Port`: **不要**（OS の実行ユーザー権限でアクセスする）

### 2.2 ファンアウトの一宛先として混在

primary を SFTP、追加宛先を共有フォルダ、のような混在も可能（put のみ）。

```json
{
  "Transfer": {
    "Name": "primary",
    "Mode": "sftp",
    "Direction": "put",
    "Host": "sftp.example.com", "Port": 22, "Username": "svc", "PrivateKeyPath": "/keys/id_ed25519",
    "RemotePath": "/incoming",
    "AdditionalDestinations": [
      { "Name": "lan-share", "Mode": "local", "RemotePath": "\\\\fileserver\\share\\incoming" }
    ]
  }
}
```

> 複数宛先 put では宛先別配信トラッキングが自動で有効になるため、**全宛先で `Name` が必須かつ一意**（local 宛先も同様）。

---

## 3. 実装

### 3.1 LocalFileTransferClient

[`IFileTransferClient`](../FtpTransferAgent/Services/IFileTransferClient.cs) を OS のファイル I/O で実装する（FTP/SFTP ラッパーと同じ抽象に乗る = Strategy パターン）。

| メソッド | 実装 |
|---|---|
| `UploadAsync` | 一時名（`<dest>.tmp.<guid>`）へストリームコピー → `File.Move(overwrite)` で原子的にリネーム。失敗時は一時ファイルを掃除 |
| `DownloadAsync` | 同様に一時名経由でコピー |
| `GetRemoteHashAsync` | 宛先ファイルを `HashUtil` で計算（サーバーコマンドの概念はない） |
| `ListFilesAsync` | `Directory.EnumerateFiles`。存在しないディレクトリは FTP/SFTP と挙動を揃えて `DirectoryNotFoundException` |
| `ExistsAsync` / `DeleteAsync` | `File.Exists` / `File.Delete` |
| `Dispose` | 接続を持たないため no-op |

- **アトミック転送**: FTP/SFTP と同じ「一時名 → リネーム」。相手のバッチが書きかけを拾わない。同一ボリュームなら一瞬、別ボリューム（UNC 等）でも `File.Move` が面倒を見る。
- **ステートレス**: 接続を持たないので、ワーカー間で安全に再利用できる（各操作はユニークな一時名で互いに干渉しない）。

### 3.2 Worker の統合点

- `CreateClientCore`: `"local" => LocalFileTransferClient` を追加。
- `BuildRemotePath`: local モードは `/` 連結ではなく **OS ネイティブのパス**として組み立てる（`Path.Combine(RemotePath, 相対名)`）。UNC 先頭の `\\` を維持。
- `DescribeDestination`: ログ用ラベルを `local:<path>` 表記に。

### 3.3 バリデーション

local 追加のため `DestinationOptions.Host` / `Username` の `[Required]` を外し、方式別の必須を `ConfigurationValidator` 側へ移した。

- **primary**（`ValidatePrimaryModeRequirements`）:
  - `ftp` / `sftp`: `Host`・`Username` 必須。
  - `local`: `RemotePath` 必須、絶対/UNC でなければ警告、`Direction=get` はエラー、SMB セキュリティの Info を出す。
- **追加宛先**（`ValidateAdditionalDestinations`）: `local` を許可。`Host`/`Username` は ftp/sftp のみ必須、`RemotePath` は全方式必須。
- **監視フォルダとの重なり防止**（`ValidateLocalDestinationNotInsideWatch`, primary / 追加宛先の両方に適用）: `local` の `RemotePath` が `Watch.Path` と **同一**、その **配下**、またはその **祖先** の場合は起動時エラー。
  - 同一だと転送先がアップロード元と一致し、`Cleanup.DeleteAfterVerify`（既定 true）が唯一のコピーを削除して**データ消失**する。
  - 配下だと書き出したファイルが（特に `IncludeSubfolders` 有効時に）後続実行で**再取り込み**される。
  - 祖先だとフォルダ構造保持 + `IncludeSubfolders` 有効時に、相対パスの連結で出力が監視ツリー内へ書き戻り、同様に**再取り込み**される。
  - 対策: 宛先は `Watch.Path` ツリーの外側を指定する。

---

## 4. セキュリティ

- 本モードは **CIFS / SMB1 を一切実装しない**。UNC への書き込みは OS が SMB を透過処理し、使われる版は OS が交渉する（現代環境では SMB2/3）。
- 起動時に Info を出す: 「`local` は OS のファイルシステム（SMB/UNC 共有を含む）経由で書き込む。SMB1 は使用しない。盗聴対策が必要なら**サーバ側で SMB1 を無効化し、SMB 暗号化を有効化**すること」。
- on-the-wire の暗号化を厳密に保証したい場合は、共有側に SFTP サーバを立てて `Mode=sftp` に寄せる方が確実（SMB 暗号化はサーバ設定依存で既定オフのことが多い）。

参考: CIFS/SMB のセキュリティと SFTP との違いは、本リポジトリの会話・README の「動作環境」節も参照。

---

## 5. 制約（既知の限界）

1. **put 専用**。`Direction=get` + `Mode=local` は起動時エラー。get 経路はリモートパスを `/` 前提で解決するため、ローカル/UNC からの取得（共有 → watch）は現状未対応（将来拡張の余地あり。共有 → ローカルの取り込みは `Watch.Path` を共有にして他経路で行うか、OS のコピーで代替可能）。
2. **on-the-wire 暗号化は OS/サーバ設定依存**。SMB 暗号化が有効でなければ LAN 上は平文になり得る（§4）。
3. **権限は OS 実行ユーザー依存**。共有への書き込み権限は、本ツールを動かすアカウントに付与しておく必要がある（`Username`/`Password` での認証は行わない）。

---

## 6. テスト

| テスト | 内容 |
|---|---|
| [`LocalFileTransferClientTests`](../FtpTransferAgent.Tests/LocalFileTransferClientTests.cs) | アップロード（ディレクトリ自動作成・一時名→リネーム・上書き・`/`混在パス・一時ファイルを残さない）、ダウンロード、ハッシュ一致、一覧（再帰有無・存在しないDirで例外）、Exists/Delete |
| [`WorkerLocalDestinationTests`](../FtpTransferAgent.Tests/WorkerLocalDestinationTests.cs) | Worker put をエンドツーエンド: 単一宛先（ハッシュ検証+元削除）、サブフォルダ構造保持、END ファイル転送、**ローカル2宛先のファンアウト**（両方受信・全成功でマーカー残らず元削除） |
| [`LocalDestinationValidationTests`](../FtpTransferAgent.Tests/LocalDestinationValidationTests.cs) | local put が有効・SMB Info 出力、get+local 拒否、相対 RemotePath 警告、ftp/sftp の Host/Username 必須が ConfigurationValidator で担保される回帰、追加 local 宛先（Host無しOK / RemotePath無しNG） |
| [`WorkerClientFactoryTests`](../FtpTransferAgent.Tests/WorkerClientFactoryTests.cs) | `Mode=local` で `LocalFileTransferClient` が生成される |

すべてネットワーク不要（テンポラリディレクトリに対して検証）。
