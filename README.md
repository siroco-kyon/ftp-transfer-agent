# FtpTransferAgent

## 概要

`FtpTransferAgent` は .NET 8 で実装されたバックグラウンドサービスで、指定したフォルダを監視し、FTP もしくは SFTP 経由でファイルを転送するためのツールです。転送後はハッシュ値を検証し、必要に応じてローカルファイルの削除も行います。アプリケーションの各種挙動は `appsettings.json` によって設定できます。

## 主な構成ファイル

- **Program.cs** - アプリケーションのエントリーポイント。各種設定クラスを DI コンテナに登録し、`Worker` サービスを起動します。
- **Worker.cs** - 実際の処理を行うバックグラウンドサービス。以下の流れで動作します。
  1. `IFileTransferClient`（FTP または SFTP のラッパークラス）を生成
  2. `TransferQueue` を開始し、キュー内のファイルをアップロード
  3. `FolderWatcher` で指定フォルダを監視し、新規ファイルをキューに追加
  4. 転送後にハッシュ値を比較し、一致すれば `CleanupOptions` に従い削除
- **Services/**
  - `AsyncFtpClientWrapper`・`SftpClientWrapper` - それぞれ FTP/SFTP 用のクライアントを実装。アップロード・ダウンロード処理とハッシュ取得機能を提供します。
  - `FolderWatcher` - `FileSystemWatcher` を利用しフォルダの変化を監視します。対象拡張子のフィルタリングも行います。
  - `TransferQueue` - `Channel` と `Polly` を使った再試行付きの処理キューです。
  - `HashUtil` - ファイルの MD5/SHA256 ハッシュを計算するユーティリティ。
- **Configuration/** - 各種設定項目を表すクラス群。`appsettings.json` からバインドされます。
- **config.schema.json** - 設定ファイルの JSON スキーマ。必須項目や値の制約が記載されています。

## 設定ファイル例

`appsettings.json` では次のように設定を記述します（一部抜粋）。
```json
{"Watch": {"Path": "./watch", "IncludeSubfolders": false, "AllowedExtensions": [".txt"]}, "Transfer": {"Mode": "ftp", "Direction": "put", "Host": "localhost", "Port": 21, "Username": "user", "Password": "pass", "RemotePath": "/remote"}}
```
詳細は同ファイルおよび `config.schema.json` を参照してください。

## ログ設定

`Logging.RollingFilePath` でファイルログの保存先を指定できます。さらに
`MaxBytes` を設定すると、同一日のログファイルがこのサイズを超えた際に
自動的にローテーションされます。

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

## ライセンス

このプロジェクトは MIT ライセンスの下で公開されています。
