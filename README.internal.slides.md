---
marp: true
theme: default
paginate: true
size: 16:9
style: |
  section {
    font-family: "BIZ UDPGothic", "Yu Gothic", "Meiryo", sans-serif;
    font-size: 25px;
    padding: 54px 64px;
    color: #1f2f3d;
    background:
      radial-gradient(circle at top right, rgba(153, 201, 255, 0.30), transparent 32%),
      linear-gradient(180deg, #f9fbfd 0%, #edf3f8 100%);
  }
  section.lead {
    background:
      linear-gradient(135deg, rgba(11, 84, 140, 0.10), rgba(17, 136, 89, 0.08)),
      linear-gradient(180deg, #f8fbff 0%, #eaf2f8 100%);
  }
  h1, h2 {
    color: #0b548c;
    margin-bottom: 0.35em;
  }
  strong {
    color: #0f7a57;
  }
  p, li {
    line-height: 1.45;
  }
  code {
    font-size: 0.82em;
    background: rgba(11, 84, 140, 0.08);
  }
  pre {
    border: 1px solid #d4e0ea;
    border-radius: 10px;
  }
  table {
    font-size: 0.8em;
  }
  blockquote {
    border-left: 6px solid #0f7a57;
    background: rgba(255, 255, 255, 0.72);
    padding: 0.45em 0.8em;
  }
---

<!-- _class: lead -->

# FtpTransferAgent

社内説明向けスライド

ファイル転送業務を標準化する  
.NET 8 バッチツール

---

## これは何か

- FTP / SFTP でのファイル受け渡しを **定期実行ジョブとして標準化**するツール
- 指定フォルダからファイルを取得し、**転送から検証までを自動化**する
- 単発実行型なので、**ジョブスケジューラと組み合わせて運用**する前提

> 手作業転送を減らし、再送・検証・ログを仕組み化するための部品

---

## 想定している業務課題

- 手作業の FTP/SFTP 転送が残っていて、担当者依存になりやすい
- 転送できたかどうかの確認が目視や勘に寄りやすい
- 再送、完了判定、失敗通知のやり方が案件ごとにばらつく
- 同じようなバッチを案件単位で作り直すと保守コストが増える

---

## FtpTransferAgent でできること

- `put` / `get` / `both` によるアップロード・ダウンロード
- FTP / SFTP の両対応
- ハッシュ検証による転送後確認
- END ファイルを使った受け渡し制御
- 並列転送、再試行、転送後削除、ログ出力、メール通知

---

## 社内で使うメリット

- **運用手順を共通化**できる
- **設定中心で導入**でき、案件ごとの実装を減らせる
- **失敗時の見え方**が揃う
- **再試行と検証**を最初から備えている
- バッチ型なので、既存の運用基盤に **載せやすい**

---

## 実行イメージ

1. 設定を読み込む
2. 対象ファイルを集める
3. 必要なら END 条件で絞る
4. ファイルを転送する
5. ハッシュで整合性を確認する
6. 設定に応じて元ファイルやリモート側を削除する
7. ログを残して終了する

---

## 向いている用途

- 他システムとの **定時ファイル連携**
- 夜間や数分おきの **バッチ転送**
- 「データ本体 + 完了通知ファイル」で受け渡す連携
- 転送後に **監査可能なログ** を残したい運用

向いていない用途:

- 常時監視のリアルタイム同期
- 双方向の複雑な競合解決
- GUI 前提の手動オペレーション

---

## 運用上の前提

- 常駐アプリではなく、**1 回実行して終了**
- そのため運用は **cron / Windows タスクスケジューラ** が基本
- 設定エラーは起動時に検出し、**終了コード `1`** で異常終了
- 警告は出すが、致命的でなければ処理継続

---

## 設定で決めること

| 項目 | 例 | 意味 |
|---|---|---|
| 転送方式 | `ftp` / `sftp` | どのプロトコルを使うか |
| 転送方向 | `put` / `get` / `both` | どちら向きに流すか |
| 対象 | `Watch.Path`, `AllowedExtensions` | 何を拾うか |
| 安全性 | `Hash.Algorithm`, `HostKeyFingerprint` | どう検証するか |
| 運用 | `Concurrency`, `Retry`, `Cleanup` | どのくらい堅く回すか |

---

## 社内説明で押さえるべきポイント

- **FTP より SFTP を優先**する
- SFTP を使うなら **ホスト鍵検証** をできるだけ有効にする
- 秘密情報は `appsettings.json` 直書きより **環境変数やシークレット管理** を使う
- サブフォルダを扱う場合は **同名ファイル衝突** に注意する

---

## 事故になりやすい設定

`IncludeSubfolders: true` かつ  
`PreserveFolderStructure: false`

この組み合わせでは、別フォルダの同名ファイルが同じ場所に集まり、  
**上書き衝突** が起きる

例:

- `watch/A/result.csv`
- `watch/B/result.csv`

通常は `PreserveFolderStructure: true` を推奨

---

## END ファイル制御の意味

- 「データが揃ったら END を置く」という連携で使える
- `RequireEndFile: true` にすると、END があるものだけ転送する
- `TransferEndFiles: true` なら END ファイル自体も送れる
- 順序は **データ -> END** を保証する

> 外部連携で完了判定を揃えたいときに有効

---

## 導入時の最低確認

- 転送先は FTP か SFTP か
- 認証はパスワードか秘密鍵か
- 何分間隔で実行するか
- END ファイル運用が必要か
- 転送成功後に元データを消すか
- 障害時の通知先メールをどうするか

---

## テストと保守

- 単体・通常テストは `dotnet test` で実行可能
- FTP 統合テストあり
- SFTP 統合テストは Docker ベース
- バッチなので、保守では **設定差分のレビュー** が重要

保守観点:

- 設定変更時に連携仕様との差分を確認
- 実行ログとメール通知で障害を追える状態を維持

---

## まとめ

- FtpTransferAgent は **社内のファイル連携を共通部品化** するためのツール
- 価値は「転送」だけでなく、**検証・再試行・完了判定・監査性** にある
- 導入時は **SFTP 優先、設定レビュー、衝突回避** を押さえる

