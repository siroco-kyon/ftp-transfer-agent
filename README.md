# FtpTransferAgent

## 概要

`FtpTransferAgent` は .NET 8 で実装されたコンソールツールで、指定したフォルダに存在するファイルを FTP もしくは SFTP 経由で転送します。アップロードだけでなくダウンロードにも対応しており、転送後はハッシュ値を検証し、必要に応じてローカルファイルやリモートファイルの削除も行います。アプリケーションの各種挙動は `appsettings.json` によって設定できます。

## 主な構成ファイル

- **Program.cs** - アプリケーションのエントリーポイント。各種設定クラスを DI コンテナに登録し、`Worker` サービスを起動します。
- **Worker.cs** - 実際の処理を行うクラス。以下の流れで動作します。
  1. `IFileTransferClient`（FTP または SFTP のラッパークラス）を生成
  2. `TransferQueue` を開始し、キュー内のファイルをアップロードまたはダウンロード
  3. 指定フォルダに存在するファイルを列挙してキューに追加
  4. 転送後にハッシュ値を比較し、一致すれば `CleanupOptions` に従い削除
- **Services/**
  - `AsyncFtpClientWrapper`・`SftpClientWrapper` - それぞれ FTP/SFTP 用のクライアントを実装。アップロード・ダウンロード処理とハッシュ取得機能を提供します。
  - ~~`FolderWatcher`~~ - かつてフォルダ監視に使用していたコンポーネント（現在は未使用）。
  - `TransferQueue` - `Channel` と `Polly` を使った再試行付きの処理キューです。並列転送数を指定できます。
  - `HashUtil` - ファイルの MD5/SHA256 ハッシュを計算するユーティリティ。
- **Configuration/** - 各種設定項目を表すクラス群。`appsettings.json` からバインドされます。
- **config.schema.json** - 設定ファイルの JSON スキーマ。必須項目や値の制約が記載されています。

## 設定ファイル例

`appsettings.json` では次のように設定を記述します（一部抜粋）。
```json
{
  "Watch": {
    "Path": "./watch",
    "IncludeSubfolders": false,
    "AllowedExtensions": [".txt"]
  },
  "Transfer": {
    "Mode": "sftp",
    "Direction": "both",
    "Host": "localhost",
    "Port": 22,
    "Username": "user",
    "PrivateKeyPath": "./id_ed25519",
    "RemotePath": "/remote",
    "Concurrency": 2
  }
}
```
`PrivateKeyPath` には SFTP 接続に使用する秘密鍵ファイルを指定します。鍵はアプリケーションから読み取り可能な場所に保存してください。鍵の生成例は以下の通りです。
```bash
$ ssh-keygen -t ed25519 -f id_ed25519
$ ssh-copy-id -i id_ed25519.pub user@host
```
`Transfer.Concurrency` を増やすと同時に処理する転送数を変更できます。詳細は同ファイルおよび `config.schema.json` を参照してください。

## ログ設定

`Logging.RollingFilePath` でファイルログの保存先を指定できます。さらに
`MaxBytes` を設定すると、同一日のログファイルがこのサイズを超えた際に
自動的にローテーションされます。

## エラーメール通知

`Smtp.Enabled` を `true` にすると、`LogLevel.Error` 以上のログが出力された際に
設定した宛先へメールが送信されます。SMTP サーバー情報や送信元/送信先は
`Smtp` セクションで指定します。

## ビルドと実行

.NET 8 SDK がインストールされた環境で以下を実行します。
```bash
# 依存関係の復元
$ dotnet restore

# ビルド
$ dotnet build --configuration Release

# 実行
$ dotnet run --project FtpTransferAgent
```

## テスト

統合テストでは Python 製の FTP サーバーを利用するため、事前に `pyftpdlib` をインストールしておきます。

```bash
$ pip install pyftpdlib
$ dotnet build
$ dotnet test --no-build --verbosity normal
```

## ライセンス

このプロジェクトは MIT ライセンスの下で公開されています。
