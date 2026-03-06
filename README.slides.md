---
marp: true
theme: default
paginate: true
size: 16:9
style: |
  section {
    font-family: "BIZ UDPGothic", "Yu Gothic", "Meiryo", sans-serif;
    font-size: 26px;
    padding: 52px 64px;
    color: #183142;
    background: linear-gradient(180deg, #f8fbff 0%, #eef5fb 100%);
  }
  section.lead {
    background:
      radial-gradient(circle at top right, rgba(111, 189, 255, 0.35), transparent 34%),
      linear-gradient(135deg, #f7fbff 0%, #dfeffc 48%, #edf7ff 100%);
  }
  h1, h2 {
    color: #0e4f83;
    margin-bottom: 0.35em;
  }
  p, li {
    line-height: 1.45;
  }
  strong {
    color: #0b6b8a;
  }
  code {
    font-size: 0.82em;
    background: rgba(15, 79, 131, 0.08);
  }
  pre {
    border: 1px solid #d4e3f1;
    border-radius: 10px;
  }
  table {
    font-size: 0.82em;
  }
  blockquote {
    border-left: 6px solid #71a8d5;
    background: rgba(255, 255, 255, 0.7);
    padding: 0.4em 0.8em;
  }
---

<!-- _class: lead -->

# FtpTransferAgent

README スライド

.NET 8 のバッチ型ファイル転送ツール  
FTP / SFTP / ハッシュ検証 / END ファイル制御

---

## このツールは何か

- 指定ディレクトリ内のファイルを **FTP / SFTP** で転送する
- 転送後に **SHA256 / SHA512** で整合性を検証する
- 常駐監視ではなく、**起動時に 1 回処理して終了**する
- そのため継続運用は **cron / タスクスケジューラ** 前提

> 定期実行するバッチ転送ジョブとして使う設計

---

## 主な機能

- 転送方向: `put` / `get` / `both`
- 認証: FTP パスワード、SFTP パスワード、SFTP 秘密鍵
- 安全性: SFTP ホスト鍵フィンガープリント検証
- 信頼性: 指数バックオフ再試行、最大 16 並列転送
- 運用性: END ファイル制御、転送後削除、ローリングログ、エラーメール

---

## 実行モデル

1. 設定を読み込み、起動時バリデーションを実施
2. 転送対象を列挙
3. フィルタと END 条件を適用
4. 転送キューへ投入し並列処理
5. ハッシュ検証
6. 設定に応じて転送元/転送先を削除
7. 処理完了後に終了

---

## クイックスタート

前提:

- `.NET 8 SDK` または Runtime
- FTP または SFTP サーバー

実行:

```bash
dotnet restore
dotnet build --configuration Release
dotnet run --project FtpTransferAgent
```

---

## 最小構成イメージ

```json
{
  "Watch": {
    "Path": "./watch",
    "AllowedExtensions": [".txt"]
  },
  "Transfer": {
    "Mode": "ftp",
    "Direction": "put",
    "Host": "ftp.example.com",
    "Port": 21,
    "Username": "user",
    "Password": "password",
    "RemotePath": "/upload"
  },
  "Hash": {
    "Algorithm": "SHA256"
  }
}
```

---

## 設定の見どころ

| セクション | 役割 | 主な項目 |
|---|---|---|
| `Watch` | 対象ファイルの選別 | `Path`, `AllowedExtensions`, `RequireEndFile` |
| `Transfer` | 接続先と転送方式 | `Mode`, `Direction`, `Host`, `Concurrency` |
| `Retry` / `Hash` | 信頼性の確保 | `MaxAttempts`, `DelaySeconds`, `Algorithm` |
| `Cleanup` | 後始末 | `DeleteAfterVerify`, `DeleteRemoteAfterDownload` |
| `Smtp` / `Logging` | 運用監視 | `Enabled`, `To`, `RollingFilePath`, `Level` |

---

## END ファイル制御

- `RequireEndFile: true` の場合、対応する END があるデータだけ転送
- `TransferEndFiles: true` なら END ファイル自体も転送
- 転送順序は **データ -> END** を保証
- 対応データのない END ファイルは転送しない

例:

```text
data.txt
data.txt.END
```

---

## 重要な注意点

`IncludeSubfolders: true` かつ  
`PreserveFolderStructure: false` は衝突リスクが高い

```text
watch/A/result.csv
watch/B/result.csv
```

この場合:

- `put` では両方が `/remote/result.csv` に集約される
- `get` では両方が `watch/result.csv` に集約される
- 同名上書きが起きるため、通常は `PreserveFolderStructure: true` を推奨

---

## 運用のポイント

- バッチ型なので **定期実行が前提**
- 設定エラー時は内容を出力して **終了コード `1`** で終了
- 警告のみなら処理継続
- コマンドライン上書きにも対応

```bash
dotnet run --project FtpTransferAgent -- --Transfer:Concurrency=4 --Hash:Algorithm=SHA512
```

---

## テストとトラブルシュート

テスト:

- 通常: `dotnet test FtpTransferAgent.sln --verbosity normal`
- FTP 統合: `pyftpdlib` を使ったローカル FTP サーバー
- SFTP 統合: Docker ベース。未使用環境では自動 Skip

よくある問題:

- 転送されない: `Watch.Path`, 拡張子, END 条件, ロック状態を確認
- 接続失敗: `Mode` と `Port`, 認証情報, ネットワーク疎通を確認
- ハッシュ不一致: サーバー側コマンド利用や同名衝突設定を確認

---

## セキュリティ推奨

- 可能な限り **`sftp`** を使う
- **`HostKeyFingerprint`** を設定して MITM リスクを下げる
- 秘密情報は `appsettings.json` 直書きより
  環境変数やシークレット管理を使う

依存ライブラリ:

- FluentFTP
- SSH.NET
- Polly
- Microsoft.Extensions.Hosting / Options / Logging

---

## まとめ

- FtpTransferAgent は **定期実行向けの堅実な転送バッチ**
- 転送だけでなく **検証、再試行、END 制御、後始末** までカバー
- 実運用では **SFTP + ホスト鍵検証 + 衝突回避設定** が重要

