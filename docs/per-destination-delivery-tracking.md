# 宛先別配信トラッキング 詳細解説

> 複数宛先（ファンアウト）転送で「未配信の宛先だけ再送する」機能の、設計・対策・設定・懸念をまとめた資料。
> 仕様の要約は [ftp-transfer-agent-spec.md 5.9](../ftp-transfer-agent-spec.md) を参照。本書はその背後にある判断と細部を後から読み返せるようにするためのもの。

- 対象バージョン: 3.1.0
- 関連設定: `Transfer.PerDestinationDeliveryTracking` / `Transfer.StateDirectory` / `Transfer.RetryDirectory` / `Transfer.DeliverySignatureMode` / `Transfer.Name`（各宛先） / `Smtp.SuppressPerDestinationFailureDetailEmails`
- 主な実装: [`Services/DeliveryStateStore.cs`](../FtpTransferAgent/Services/DeliveryStateStore.cs) / [`Worker.cs`](../FtpTransferAgent/Worker.cs) / [`Logging/LogEvents.cs`](../FtpTransferAgent/Logging/LogEvents.cs)

---

## 1. 何を解決する機能か

### 1.1 背景

このツールは **ステートレスなバッチ**として動く。起動するたびに `Watch.Path` を端から列挙し、見つかったファイルを設定された宛先へ送り、終わったら終了する。常駐せず、前回実行の記憶をプロセス内には持たない。

複数宛先（`AdditionalDestinations`）を設定すると、1 ファイルを primary + 各追加宛先へ同時送信する（ファンアウト）。

### 1.2 従来（all-or-nothing）の問題

従来は「全宛先成功時のみローカル削除、1 つでも失敗したらローカル保持」という all-or-nothing だった。すると:

1. 宛先 A は成功、宛先 B はメンテで失敗 → ローカルファイルは保持される
2. 次回バッチでファイルを再列挙 → **また A と B の両方へ送信**
3. A はすでに受け取っているのに**重複配信**される

宛先 B が 1 日ダウンしていると、その間ずっと A へ重複配信が続く。受信側 A の運用によっては、これが事故（二重取り込み等）につながる。

### 1.3 やりたいこと

- 送れた宛先（A）には**二度と送らない**
- 送れていない宛先（B）には**送り直す**
- B が何日ダウンしていても安全に再送し続けられる

### 1.4 なぜ単純な実装では足りないか

バッチはステートレスなので、「A には送れた」という情報をプロセス内に持っても次回には消える。`Watch.Path` 上のファイルは「ある / ない」の 1bit しか表現できず、「A には送れたが B には未送」という**中間状態を表現する場所がない**。

→ 唯一の永続記憶であるディスクに、**(ファイル × 宛先) 単位の配信状態**を残す必要がある。これがマーカー方式の出発点。

---

## 2. 全体アーキテクチャ（マーカー方式）

元のデータファイルには手を付けず、配信できた (ファイル × 宛先) ごとに**小さなマーカーファイル**を状態ディレクトリに残す。END ファイルの「転送準備完了マーカー」と同じ発想の「配信完了マーカー」版。

```
watch/
  report.txt                ← 元データ。配信トラッキングは触らない

<StateDirectory>/
  <hash>.marker             ← 「report.txt は宛先 primary へ配信済み」を表す小さな JSON
```

マーカー（`*.marker`）の中身は JSON 1 つ:

```json
{
  "RelativePath": "report.txt",
  "DestinationName": "primary",
  "Signature": "st:11-638...ticks",
  "DeliveredAtUtc": "2026-06-18T01:23:45Z"
}
```

処理の流れ:

1. **起動時**: 状態ディレクトリを 1 回走査してマーカーをメモリへ読み込む（孤児・破損は掃除）。
2. **列挙時**: 各データファイルについて現在の指紋を計算し、「指紋が一致するマーカーを持つ宛先」を配信済みとみなす。**未配信の宛先だけ**をキューへ投入する。全宛先配信済みなら送信自体をスキップする。
3. **完了時**: 「今回成功した宛先 ∪ 既存マーカーの宛先」が全宛先を満たせば完了として、ローカルファイル・マーカーを削除（`DeleteAfterVerify` のとき）。満たさなければ、今回成功した宛先のマーカーを書いてローカルを保持する。

---

## 3. 直面する問題と、入れてある対策（1 つずつ）

### 3.1 状態をどこに持つか → ディスク上のマーカー

- **問題**: ステートレスバッチなので状態をプロセス内に持てない。
- **対策**: `StateDirectory` 配下の (ファイル × 宛先) マーカーファイルで永続化。バッチを跨いでも、宛先が何日ダウンしても残る。
- **実装**: `DeliveryStateStore`（ロード／記録／削除／掃除）。

### 3.2 性能（毎回マーカーを探す／作るのは重くないか）→ 「部分失敗時だけ作る」「スキャンは 1 回」

- **問題**: ファイルごとにディスクを探索したり、毎回マーカーを作ると重い。
- **対策**:
  - マーカーは **部分失敗時にだけ**作る。全宛先成功（＝通常時）はマーカーを 1 つも作らず、従来どおりローカル削除するだけ。**通常時の追加コストはゼロ**。
  - 部分失敗したファイルは `RetryDirectory` へ移動する。未指定時の retry 先は `Watch.Path` の外に作られる。次回は retry 配下のファイルを元の相対パスとして扱うため、リモートパスやマーカーキーは Watch 配下にあったときと同じ。ユーザーが retry ファイルを削除した場合は、起動時に孤児マーカーも掃除される。
  - retry 移動前にデータファイルと関連 END ファイルの移動先をすべて検証する。移動途中で I/O エラー等が起きた場合は、完了済みの移動を逆順で Watch 側へ戻し、データと END が分断されたまま残るリスクを下げる。
  - 探索は**起動時に状態ディレクトリを 1 回だけ走査**してメモリ（`ConcurrentDictionary`）に載せ、以降はメモリ照合。ファイルごとのディスク探索はしない。マーカーは「詰まったファイル」分しか存在しないため軽量。
  - 仮にマーカー I/O が発生しても、空に近いファイルの作成・削除はメタデータ操作でマイクロ秒オーダー。ネットワーク転送＋ハッシュ（ミリ秒〜秒）に比べれば誤差。
- **実装**: `DeliveryStateStore.Initialize()`（1 回走査）、`Worker.HandleFanoutCompletionTracked`（全成功時はマーカーを書かない）。

### 3.3 宛先の取り違え → `Name` 必須かつ一意

- **問題**: マーカーは宛先を区別する「鍵」が要る。接続情報（host/port/path）を鍵にすると、サーバ移転やパス変更・typo 修正のたびに「別の宛先」とみなされ、全ファイルが再送（重複配信）されてしまう。逆に鍵が衝突すると、未送信なのに「送った」と誤判定（取りこぼし）する。
- **対策**: 接続情報と独立した**安定した `Name`** を鍵に使う。トラッキング有効時は **primary を含む全宛先で `Name` を必須かつ一意**とし、起動時バリデーションで強制（未設定・重複はエラーで停止）。
- **実装**: `DestinationOptions.Name`、`ConfigurationValidator.ValidateDeliveryTracking`。

### 3.4 同名ファイルの上書き → 指紋（シグネチャ）ガード

- **問題**: 配信途中（A 済み・B 未）のファイルが、別内容で同名上書きされた場合、「A のマーカーがある → A をスキップ」とすると **A には古い内容しか届かず、新内容が永遠に届かない**（静かなデータ欠落）。
- **対策**: マーカーに送信時のファイル**指紋**を記録し、現在の指紋と一致する宛先だけを「配信済み」と判断する。指紋が違えば（＝内容が差し替わった）配信済み扱いにせず**全宛先へ再送**し、古いマーカーは破棄する。
  - `sizetime`（既定）: サイズ + 最終更新時刻（UTC ticks）。軽量。
  - `hash`: `Hash.Algorithm` でファイルハッシュを算出。厳密。
- **実装**: `DeliveryStateStore.ComputeSignatureAsync` / `GetDeliveredDestinations`（指紋不一致のマーカーをその場で削除）。

### 3.5 完了判定（次回はキューに未送先しか入らない）→ 「今回成功 ∪ 既存マーカー」で判定

- **問題**: Run2 では未配信の宛先（B）だけをキューへ入れるため、ファンアウトのグループは {B} だけになる。それだけ見て「全部成功」を判断すると、A の状態を取りこぼす。
- **対策**: 完了判定は「**今回の成功した宛先 ∪ ディスク上の既存マーカーの宛先**」が全宛先を満たすかで行う。列挙時点で確定した「配信済み集合」を完了ハンドラまで持ち回す（`DeliveryTrackingContext`）。
- **実装**: `Worker.DeliveryTrackingContext`（`AllDestinationNames` / `AlreadyDelivered`）、`Worker.HandleFanoutCompletionTracked`。

### 3.6 クラッシュ安全性 → アトミック書き込みと順序

- **問題**: 書きかけのマーカーを読んだり、配信前にマーカーが残ると危険。
- **対策**:
  - マーカーは一時ファイルへ書いてから本番名へ **`File.Move(overwrite)` でリネーム**（アトミック）。書きかけが読まれない。
  - マーカーは**検証成功（＝その宛先への配信成功）後にだけ**書く。逆（配信前にマーカー）は起こさない。
  - 「配信完了したがマーカー記録前にクラッシュ」した場合は、次回その宛先へ**再送**される。一時名→本番名のアトミック転送なので“上書き”で済み、安全側に倒れる（重複はするが破損しない）。
- **実装**: `DeliveryStateStore.RecordDelivered`（temp→Move）。

### 3.7 マーカーのゴミ → 孤児・陳腐化の掃除

- **問題**: 元ファイルが手動削除されたり内容が変わると、マーカーがゴミとして残る。
- **対策**:
  - **孤児**（対応する元ファイルが存在しない）マーカーは**起動時走査で削除**。
  - **陳腐化**（指紋不一致）マーカーは列挙時の配信済み判定の中で削除。
  - 全宛先配信完了でローカルを削除する際、そのファイルのマーカーも `RemoveAll` で掃除。
- **実装**: `DeliveryStateStore.Initialize` / `GetDeliveredDestinations` / `RemoveAll`。

### 3.8 メール通知が鳴り続ける → 「宛先失敗」メールだけ選択的に抑制

- **問題**: 宛先 B が 1 日ダウンしていると、バッチのたびに「転送失敗」エラーログ → メール送信 → 終了コード 1、が繰り返され、アラート疲れを起こす。
- **対策**: 「複数宛先での宛先失敗」ログに専用の `EventId` を付け、`Smtp.SuppressPerDestinationFailureDetailEmails: true` のときその EventId の詳細メールだけ抑制する。**設定不備・認証エラーなど他のエラーメールは送信を継続**する。
  - 失敗ログは 2 か所から出るので**両方**にタグを付けている:
    1. 個々の転送の最終失敗（`TransferQueue`）→ `EventId 1001 MultiDestinationTransferFailure`
    2. ファイル単位の部分失敗サマリ（`Worker`）→ `EventId 1002 MultiDestinationPartialFailure`
  - 単一宛先（追加宛先なし）の失敗は従来どおりの EventId なので**抑制されない**（普通にメールが飛ぶ）。
- **実装**: `LogEvents`、`TransferQueue`（最終失敗ログに EventId 付与）、`ErrorEmailLogger.Log`（抑制判定）。

### 3.9 終了コードはメールと独立

- **問題**: メールを止めると「異常を検知できなくなる」のでは。
- **対策**: メール抑制は**通知だけ**を止める。ファイルログには引き続き記録され、**終了コードは失敗があれば 1 のまま**（`Worker` は失敗件数で判定し、ログレベルとは無関係）。Task Scheduler・監視からは引き続き「異常あり」と分かる。

### 3.10 状態ディレクトリが転送対象に混ざらないか → 既定は watch 外 + 列挙除外

- **問題**: 状態ディレクトリを `Watch.Path` 配下に置き、かつ `IncludeSubfolders: true` だと、マーカーが転送対象として列挙されかねない。
- **対策**:
  - 既定の状態ディレクトリは `LocalApplicationData/FtpTransferAgent/delivery-state/<watch パスのハッシュ>`（watch 外）。watch パスのハッシュを挟むことで、別の watch フォルダのマーカーが相対パスで衝突しない。
  - 念のため、列挙時に**状態ディレクトリ配下のファイルは常に除外**する（明示的に watch 内へ置いた場合の保険）。
- **実装**: `DeliveryStateStore.ResolveStateDirectory`、`Worker` の列挙フィルタ。

### 3.11 多重起動 → 既存の ProcessLock で担保

- 複数プロセスが同じ状態ディレクトリのマーカーを取り合う心配は、既存の二重起動防止（`ProcessLock`、終了コード 2）で排除されている。単一インスタンス内では (ファイル × 宛先) ごとに独立なので競合しない。

---

## 4. 設定で変更できる部分

| 設定キー | 既定 | 説明 |
|---|---|---|
| `Transfer.PerDestinationDeliveryTracking` | `false` | 機能の ON/OFF。OFF なら従来の all-or-nothing。 |
| `Transfer.StateDirectory` | `""` | マーカー保存先。空なら `LocalApplicationData/FtpTransferAgent/delivery-state/<hash>`。watch 外推奨。 |
| `Transfer.RetryDirectory` | `null` | 部分失敗したファイルの移動先。未指定/null は `LocalApplicationData/FtpTransferAgent/delivery-retry/<hash>`。相対パスを明示した場合は `Watch.Path` 配下として解決。空文字で移動を無効化。 |
| `Transfer.DeliverySignatureMode` | `"sizetime"` | 上書き検出の指紋方式。`sizetime`（軽量）/ `hash`（厳密）。 |
| `Transfer.Name`（各宛先） | `null` | 宛先の安定識別子。トラッキング有効時は全宛先で**必須かつ一意**。 |
| `Smtp.SuppressPerDestinationFailureDetailEmails` | `false` | 複数宛先の個別宛先失敗・部分失敗の詳細メールを抑制。他のエラーメール・終了コードには影響しない。 |

### 設定例

```json
{
  "Transfer": {
    "Name": "primary",
    "Mode": "sftp",
    "Direction": "put",
    "Host": "primary.example.com",
    "Port": 22,
    "Username": "svc",
    "PrivateKeyPath": "/keys/id_ed25519",
    "RemotePath": "/incoming",
    "PerDestinationDeliveryTracking": true,
    "StateDirectory": "/var/lib/ftp-transfer-agent/state",
    "RetryDirectory": "/var/lib/ftp-transfer-agent/retry",
    "DeliverySignatureMode": "sizetime",
    "AdditionalDestinations": [
      {
        "Name": "backup-osaka",
        "Mode": "sftp",
        "Host": "backup.example.com",
        "Port": 22,
        "Username": "svc",
        "PrivateKeyPath": "/keys/id_ed25519",
        "RemotePath": "/incoming"
      }
    ]
  },
  "Smtp": {
    "Enabled": true,
    "SuppressPerDestinationFailureDetailEmails": true
  }
}
```

---

## 5. 動作シナリオ

宛先 = primary, backup の 2 つ。`DeleteAfterVerify: true`。

| 実行 | 状況 | primary | backup | ローカル | マーカー |
|---|---|---|---|---|---|
| Run1 | backup がメンテ | 成功 | 失敗 | 保持 | primary のみ |
| Run2 | backup まだ落ちてる | **送らない** | 失敗 | 保持 | primary のみ |
| Run3 | backup 復活 | **送らない** | 成功 | 削除 | 全削除 |
| 上書き | Run1 後に内容差し替え | 指紋不一致 → 再送 | 再送 | （完了後）削除 | （完了後）削除 |
| 通常 | 最初から全宛先成功 | 成功 | 成功 | 削除 | **作られない** |

`DeleteAfterVerify: false` の場合: 全宛先配信済みでもローカルを残すため、全宛先分のマーカーを保持し続け、次回以降は送信をスキップする（再送はしない）。

---

## 6. 既知の限界・残る懸念

1. **`sizetime` の取りこぼし**: サイズが同一かつ最終更新時刻も据え置きで上書きされると、指紋が変わらず「配信済み」と誤判定し得る（静かな欠落）。タイムスタンプを保持してコピーするツール等で発生し得る。厳密さが要る場合は `DeliverySignatureMode: "hash"`。
2. **クラッシュ時の重複再送**: 「配信完了 → マーカー記録前」にクラッシュすると、その宛先へ次回再送される。アトミック転送のため上書きで済むが、受信側が重複に弱い運用では留意。
3. **`DeleteAfterVerify: false` + トラッキング**: 完了してもローカルとマーカーが残り続ける（再送はしない）。状態ディレクトリのサイズは保持ファイル数に比例する。
4. **`hash` モードのコスト**: 保持（詰まった）ファイルごとにハッシュ計算が必要。ファイル数・サイズが大きいと相応の負荷。
5. **状態ディレクトリの可搬性**: マーカーは相対パス + `Name` をキーにする。`Watch.Path` を変えたり `Name` を付け替えると、過去のマーカーは孤児化し（または別宛先扱いになり）、再送が発生し得る。

---

## 7. 実装マップ

| 関心事 | 場所 |
|---|---|
| マーカーのロード／指紋／配信済み判定／記録／削除／掃除 | [`Services/DeliveryStateStore.cs`](../FtpTransferAgent/Services/DeliveryStateStore.cs) |
| 列挙で未送先のみ投入・完了判定・削除・トラッキング初期化 | [`Worker.cs`](../FtpTransferAgent/Worker.cs)（`HandleFanoutCompletionTracked` / `HandleAlreadyDelivered` / `DeleteLocalAfterSuccess` / `DeliveryTrackingContext`） |
| 宛先結果に Name を載せる | [`Services/FanoutCoordinator.cs`](../FtpTransferAgent/Services/FanoutCoordinator.cs)（`DestinationResult.DestinationName`） |
| 最終失敗ログへの EventId 付与 | [`Services/TransferQueue.cs`](../FtpTransferAgent/Services/TransferQueue.cs)（`finalFailureEventId`） |
| EventId 定義・抑制判定述語 | [`Logging/LogEvents.cs`](../FtpTransferAgent/Logging/LogEvents.cs) |
| メール抑制 | [`Logging/ErrorEmailLogger.cs`](../FtpTransferAgent/Logging/ErrorEmailLogger.cs) |
| 設定モデル | `Configuration/DestinationOptions.cs` / `TransferOptions.cs` / `SmtpOptions.cs` |
| バリデーション | [`Configuration/ConfigurationValidator.cs`](../FtpTransferAgent/Configuration/ConfigurationValidator.cs)（`ValidateDeliveryTracking`） |

## 8. テスト

| テスト | 内容 |
|---|---|
| `DeliveryStateStoreTests` | 指紋計算（sizetime/hash）、配信済み判定、指紋不一致での陳腐化削除、孤児掃除、永続化、`RemoveAll` |
| `PerDestinationDeliveryTrackingTests` | バッチ 2 回実行で「未送先だけ再送」「配信済みへ再送しない」、上書き時の全宛先再送、全成功時にマーカーが作られずローカル削除 |
| `DeliveryTrackingValidationTests` | `Name` 必須・一意・署名モード・get 方向警告のバリデーション、`LogEvents.IsMultiDestinationFailure` の判定 |
