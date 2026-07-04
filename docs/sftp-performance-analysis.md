# SFTP 転送性能の調査・実験レポートとチューニングガイド

**日付**: 2026-07-04
**対象**: FtpTransferAgent v3.4.0 / SSH.NET 2025.0.0 / .NET 10
**前提**: ハッシュ検証は無効 (`Hash:Enabled=false`) 運用。転送の完全性は SSH トランスポート層
(パケットごとの HMAC / AEAD) と SFTP プロトコルの応答確認で担保する方針。

## 1. 結論（要約）

- **大ファイルの転送経路は既に最適に近い**。SSH.NET の `UploadFile` は SSH_FXP_WRITE を
  上限なしでパイプライン化しており、クライアント側で送信スループットを阻害する要素はない。
  暗号スイートも実測で現行既定 (aes128-ctr) が最速だった。
- **ボトルネックは 1 ファイルあたりの逐次往復数**（改善前: posix-rename 対応サーバで 10 往復 +
  書き込み、非対応サーバで 13 往復）。小ファイル多数 × 高レイテンシ回線で支配的になる。
- 改善として **(1) リモートディレクトリ存在確認のキャッシュ（常時有効）** と
  **(2) `Transfer:VerifyUploadedFileExists`（リネーム後の存在確認の省略オプション）** を実装し、
  RTT 10ms の回線で **1.4〜1.57 倍**の高速化を実測（整合性は全ケース SHA256 全数比較で確認）。
- `Transfer:BufferSizeKB` (1–64) も追加。ただし **OpenSSH 系サーバには効果がない**
  （サーバがチャネルパケット上限 32KB を告知するため）。32KB 超を受け付けるサーバでのみ有効。

## 2. 調査で確認した SSH.NET 2025.0.0 の内部動作

| 項目 | 事実 | 出典 |
|---|---|---|
| 書き込みチャンク | `min(BufferSize(既定32KB), サーバ告知のチャネルパケット上限) − 25 − ハンドル長`。OpenSSH / paramiko は 32768 を告知するため実質 ~32KB | `SftpSession.CalculateOptimalWriteLength` |
| パイプライン | `UploadFile` は SSH_FXP_WRITE を in-flight 数無制限で発行（フロー制御は SSH チャネルウィンドウのみ）| `SftpClient.InternalUploadFile` |
| トランスポート上限 | SSH パケット 68,536 バイト。SFTP メッセージをチャネル分割送信しないため、**BufferSize>64KB + 大パケット許容サーバの組合せは送信時例外** | `Session.SendMessage`（実験で例外を確認） |
| 隠れ往復 | 全公開 API が操作前に SSH_FXP_REALPATH を 1 往復発行。`Exists` は REALPATH + LSTAT の **2 往復** | `SftpSession.GetCanonicalPath` / `SftpClient.Exists` |
| 暗号既定順 | aes128-ctr → aes192/256-ctr → aes128/256-gcm → chacha20 → cbc 系 | `ConnectionInfo` |

## 3. 実験方法

- サーバ: paramiko 5.0 ベースの計測用 SFTP サーバ（SFTP リクエスト種別ごとの回数を記録。
  posix-rename@openssh.com 拡張の告知有無を切替可能）。資材は `.bench/v2/`
- レイテンシ: 自作 TCP 遅延プロキシで片道 5ms（RTT 10ms）を注入
  （注: Windows の asyncio はタイマ分解能 15.6ms の制約で並列時に遅延が消えるため、
  スレッド + `perf_counter` 期限方式で実装）
- **整合性検証: 全 run で転送前に全ファイルの SHA256 を記録し、転送後に宛先側と全数比較**。
  全 run 一致 (`integrity=OK`)、exit code 0
- ワークロード: 小=400×8KB / 中=200×512KB / 大=1×256MB、並列度 1/4/16

## 4. 実測結果

### 4.1 1 ファイルあたりの逐次往復数（アップロード、ハッシュ無効）

| 構成 | REALPATH | LSTAT | OPEN/CLOSE/RENAME | 計 |
|---|---|---|---|---|
| 改善前 (posix-rename 対応サーバ) | 5 | 2 | 3 | **10 + 書込** |
| 改善前 (非対応サーバ: 削除+rename フォールバック) | 6 | 3〜 | 4 | **13 + 書込** |
| 改善後・既定 (ディレクトリキャッシュ) | 4 | 1 | 3 | **8 + 書込** ※初回のみ +2 |
| 改善後・`VerifyUploadedFileExists=false` | 3 | 0 | 3 | **6 + 書込** |

RTT 10ms 環境の実測 127ms/ファイル ≒ 11 往復 × 11.3ms と理論値が一致。

### 4.2 転送時間（400×8KB、RTT 10ms、整合性全数確認済み）

| 構成 | conc=1 | conc=4 |
|---|---|---|
| 改善前 | 51.0s | 13.1s |
| 改善後・既定 | 41.7s | − |
| 改善後・`VerifyUploadedFileExists=false` | **32.5s (1.57x)** | **8.7s (1.5x)** |
| （参考）改善前 conc=16 | 4.3s | ← 並列化はほぼ線形に効く |

中ファイル (200×512KB, conc4, RTT10ms): 7.24s → 5.13s (**1.41x**)。
大ファイル (256MB 単発): 変化なし（往復数はファイルサイズに依存しないため。想定どおり）。

### 4.3 BufferSizeKB（サーバの告知上限に依存）

| サーバ告知上限 | BufferSize 32KB | 64KB | 128KB+ |
|---|---|---|---|
| 300KB (寛容なサーバ) | 69 MB/s | **192 MB/s (2.8x)** | SSH.NET 例外（送信不可）|
| 32KB (OpenSSH 相当) | 69 MB/s | 69 MB/s（効果なし） | − |

※ localhost + Python サーバのためサーバ処理コスト分が誇張されている。実サーバでは差は縮む。
※ 例外リスクがあるため設定範囲を 1–64 に制限（64KB は上限 68,536B 未満で安全）。

### 4.4 暗号スイート（256MB アップロード、64KB チャンク）

| 暗号 | スループット |
|---|---|
| aes128-ctr + hmac-sha2-256（現行既定）| **191 MB/s** |
| aes256-ctr + hmac-sha2-256 | 197 MB/s |
| aes128-gcm@openssh.com | 100 MB/s |
| aes256-gcm@openssh.com | 96 MB/s |

→ **既定順序の変更は不要**（この構成では GCM が約 2 倍遅い。理論と逆だが、クライアント/サーバ
いずれかの GCM 実装が遅いため。順序変更による改善余地なしと判断）。

## 5. チューニングガイド

| 状況 | 推奨設定 |
|---|---|
| 小ファイル多数 × WAN（レイテンシ大）| `Concurrency` を上げる（16 まででほぼ線形）。さらに `VerifyUploadedFileExists: false` で往復 2 削減 |
| 大ファイル中心 | クライアント側の調整余地なし（帯域と暗号 CPU に律速）。回線と CPU を確認 |
| サーバが 32KB 超パケット対応（OpenSSH 以外の一部製品）| `BufferSizeKB: 64` |
| 完全性の考え方 | 書込/クローズ/リネームは全て SFTP 応答 (SSH_FX_OK) を確認済み。改行後も一時名アップロード → アトミックリネームの設計は不変。`VerifyUploadedFileExists=false` はリネーム応答成功後の「追加の stat 確認」を省くだけで、プロトコルレベルの完了保証は変わらない |

## 6. 実装変更点 (v3.4.0)

1. `SftpClientWrapper`: リモートディレクトリ存在キャッシュ（常時有効、転送失敗時に無効化して
   リトライで再作成可能）
2. `DestinationOptions.VerifyUploadedFileExists`（既定 `true` = 従来動作）
3. `DestinationOptions.BufferSizeKB`（既定 `32`、範囲 1–64、SFTP で 32 超は起動時警告）
4. バリデーター / config.schema.json / appsettings.json / 仕様書を更新
