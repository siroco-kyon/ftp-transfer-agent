# Windows検証記録

2026-10-01に、修正後のWindowsプログラムを検証した記録です。本番環境の手動検証は依頼者の実施報告に基づきます。今回の自動検証では、隔離したファイルと試験用サーバーを使用しました。本番の設定やファイルは変更していません。

## 修正と根拠

| 対象 | 修正 | 主な証跡 |
|---|---|---|
| 監視パスの末尾の区切り文字 | 保存先の識別とロックを統一。旧保存先を統合し、競合時は上書きせず停止。旧表記で残ったスナップショットも起動時に掃除 | `path-before.trx`、`path-after.trx`、`probe-results.json`、`PersistentRecoveryTests` |
| 末尾に区切り文字がある取得先 | ルートの区切り文字を保持してパスを比較。正当な`..`を含む名前も取得可能 | `probe-results.json`、`WorkerDownloadTests` |
| 配信済みの旧ファイルによる新ファイルの先送り | 旧ファイルとENDを`StateDirectory/completed-files`へ保管し、次回に新ファイルを配信。保管の中断は保存した記録から再開 | `recovery-results.json`、`PersistentRecoveryTests`、`PerDestinationDeliveryTrackingTests` |
| 取得後の削除失敗 | データとENDの削除進捗を保存し、ENDが消えても次回バッチで削除を再開。リモートの差し替えは指紋で検知 | `recovery-results.json`、`PersistentRecoveryTests` |
| SFTPの一斉接続 | 宛先ごとの認証開始を同時4件までに制限。認証後は設定した並列数で転送 | 修正前の`small-c16-hashFalse-existsTrue-r1.txt`、`speed-final/`、`SftpClientDockerIntegrationTests` |
| 終了時のメール送信 | ロガーをDIのファクトリーで登録し、ホスト終了時に送信待機を実行 | 修正前の`smtp-console.txt`、`smtp-fixed/smtp-results.json`、`ProgramEmailIntegrationTests` |
| SSH.NETの脆弱性警告 | 2025.0.0から2026.0.0へ更新 | `build-final.txt`、`windows-release.trx` |

旧ファイルの保管領域は再送の対象にしません。保持する旧ファイルを自動削除しないため、運用者が保存期間と容量を管理してください。既定保存先の移行で同名ファイルが競合した場合は、両方を保持して終了します。

取得の削除記録は、監視フォルダーと接続先ごとに`StateDirectory/download-cleanup`へ保存します。未完了の記録がある間は、削除設定を変更せず再実行してください。リモートファイルが別世代へ変わった場合は旧記録の削除を取り消し、次のバッチで新世代を取得します。

## 結果

- Releaseの全件テストは443件成功、失敗0件、スキップ0件。`windows-release.trx`と`windows-release.txt`に記録。
- パスと起動8段階、再試行と削除復旧14段階、FTP障害6条件、SMTP通知4条件が成功。
- SFTPの8条件を各3回測定。24回すべて終了コード0、7,803ファイルすべてSHA256一致。
- 1,100件のデータと1,100件のENDを2宛先へ配信。初回の破損10件は再試行で復旧。全データがSHA512一致し、原本・配信マーカー・FTP一時ファイルの残存は0件。
- ビルドは警告0件、エラー0件。Windows向けの自己完結・単一ファイル形式を発行。

Windowsのフォルダーリンクを使う2件は、管理者権限が不要なジャンクションでReparsePointの経路を検証しました。試験用SFTPサーバーはDockerで用意しています。製品と検証クライアントはWindowsで実行しました。

## 測定条件

速度検証は同じPCから試験用SFTPサーバーへ接続し、人工的な遅延を加えていません。`speed-final/sshd-effective-config.txt`にはサーバー設定を保存しました。`MaxStartups`は既定の`10:30:100`です。

速度の秒数はプログラム起動から終了までの時間です。測定後に行う全件のSHA256照合は含みません。小・中ファイルは製品のハッシュ照合を無効にし、大ファイルはSHA256を有効にしました。全条件で測定後の照合を実施しています。3回の値と中央値は`speed-final/windows-speed-results.json`を参照してください。

大量配信の31.05秒には、プログラム実行後の全件照合も含みます。小さなファイル群での1回の値です。SFTPの表と直接比較する値ではありません。

## 手動検証の出典

`manual-results.json`は、依頼者の「Windows本番環境で全項目を確認してOK」という実施報告をCSVの行番号と対応づけた記録です。独自に本番環境へ接続して実施した記録ではありません。元CSVのSHA256も保存しています。対象221項目は正常系138項目、異常系83項目です。cronの216行目は対象外です。

## 再実行

リポジトリのルートから次のコマンドで実行します。Pythonには`pyftpdlib`と`paramiko`が必要です。SFTPの検証にはDockerを使用します。

```powershell
dotnet build -c Release
dotnet test FtpTransferAgent.Tests/FtpTransferAgent.Tests.csproj -c Release --no-build
dotnet format --no-restore --verify-no-changes
python docs/evaluation/2026-10-01/probe.py
python docs/evaluation/2026-10-01/recovery_probe.py
python docs/evaluation/2026-10-01/fault_matrix.py
python docs/evaluation/2026-10-01/smtp_probe.py
python docs/evaluation/2026-10-01/stress_probe.py
python docs/evaluation/2026-10-01/windows_speed_probe.py
```

## 依存ライブラリと接続集中の参考

SSH.NETの修正版は[公式リリース2026.0.0](https://github.com/sshnet/SSH.NET/releases/tag/2026.0.0)を参照。2件の警告の対象と修正版は[GHSA-mggc-4xg6-vcxf](https://github.com/advisories/GHSA-mggc-4xg6-vcxf)と[GHSA-q939-rpr3-3284](https://github.com/advisories/GHSA-q939-rpr3-3284)で確認しました。

認証前の接続数による拒否は[OpenSSHのMaxStartups](https://man.openbsd.org/sshd_config#MaxStartups)に記載されています。今回の修正前の16並列測定では、SSH識別文字列を受信する前の切断で1回の測定が失敗しました。修正後は同じ再試行設定とサーバー設定で24回すべて成功しました。

## 第1.3版の大容量SFTP測定

1件4MiBのファイルを1,000件、1回あたり3.90625GiB転送しました。1・4・8・16並列を各3回、全12回を同じDドライブの作業領域で実行しています。製品のSHA256照合と転送後の存在確認は有効です。`Retry.MaxAttempts=1`は再試行の上限1回を意味し、`DelaySeconds=0`としました。今回のログで確認した再試行は合計0回です。

| 条件 | 3回の実測値・秒 | 中央値 | 確認結果 |
|---|---|---|---|
| 1並列 | 96.944 / 96.589 / 92.908 | 96.589秒 | 全3回成功・全件一致 |
| 4並列 | 62.504 / 61.438 / 69.322 | 62.504秒 | 全3回成功・全件一致 |
| 8並列 | 66.899 / 62.833 / 74.749 | 66.899秒 | 全3回成功・全件一致 |
| 16並列 | 73.640 / 72.901 / 73.341 | 73.341秒 | 全3回成功・全件一致 |

全12回の終了コードは0です。転送先の12,000ファイルすべてで、測定後に独立して算出したSHA256が元ファイルと一致しました。元ファイルの残存は全回0件です。秒数にはプログラムの起動から終了までを含み、測定後の独立した全件照合は含みません。

クライアントはWindows、Core i7-10700、物理メモリ約32GiBです。同じPCの隔離した試験用SFTPサーバーへ接続し、人工的な遅延は加えていません。試験用SFTPサーバーもWindows上のParamikoで実行しました。並列数ごとの速度比較であり、コード修正前後の速度比較ではありません。

結果は`speed-native/windows-speed-results.json`、条件は`speed-native/measurement-conditions.json`、各回の全件ハッシュは`speed-native/*-sha256.txt`に保存しました。`expected-sha256.json`には元ファイルのハッシュ、`run-progress.jsonl`には測定値を保存しています。再実行は`python docs/evaluation/2026-10-01/windows_native_sftp_speed_probe.py`です。約8GiBの元ファイルと転送先の領域が必要です。

初回はCドライブの一時フォルダーで5回の測定・照合を完了した後、元ファイルの生成中に容量不足となりました。Dドライブへ作業領域を移した試験では6回の測定・照合を完了しましたが、その後DockerのWSL基盤が応答しなくなりました。両方の途中記録は保存し、報告書の速度比較には使用していません。Windows上の試験用SFTPサーバーへ切り替え、1,000件×4MiBの同一条件で全12回を測り直しています。元ファイルと転送先はDドライブの隔離した一時領域です。インフラの停止は製品の不具合とは区別しています。

報告書は機能概要、不具合6件の現象・修正・再検証、自動テスト、本番の手動確認、速度測定の順に整理しました。`report-validation-v13.json`に構造と参照の検査結果を保存しています。手動221項目の明細は変更していません。

測定用の元ファイルと転送先のデータは削除しました。`speed-native/cleanup.json`で作業フォルダーの削除、試験用サーバーの停止、接続の終了を確認できます。中断時に残ったデータの削除は`interrupted-probe-cleanup.json`に記録しました。測定値、ログ、全件のハッシュ記録、製品の発行物は保存しています。

## 共有用の記録

このフォルダーには、報告書の根拠となる測定値、テスト結果、再実行スクリプトを収録しています。PCのユーザー名とマシン名は置き換えました。テストの判定、時刻、件数、処理時間、ハッシュ値は保持しています。再実行スクリプトは結果を`.audit-results/evaluation-rerun`へ保存します。元の測定記録は上書きしません。
