# スライド（Marp）ビルド手順

`slides/` 配下の Marp スライドを PDF / PNG / PPTX に書き出す手順をまとめる。

> 📄 **HTML 版の仕様確認資料**: 上長への仕様確認・承認用の非スライド形式の HTML 資料が
> [docs/html/internal-overview.html](../docs/html/internal-overview.html) にある（図はインライン SVG・単一ファイルで配布可）。
> あわせて [docs/html/spec.html](../docs/html/spec.html)（詳細仕様書）/
> [docs/html/user-guide.html](../docs/html/user-guide.html)（ユーザーガイド）も参照。

対象:

- `FtpTransferAgent_社内説明スライド.md` — 社内説明用
- 図版は `slides/images/*.svg`（`![](images/xxx.svg)` で参照）

## ⚠️ 書き出し時に付けるフラグ（2 つ）

| フラグ | 区分 | 役割 |
|---|---|---|
| `--allow-local-files` | **必須** | ローカルの SVG（`images/` 配下）を読み込む。無いと図が描画されず **alt テキストだけ**になる。 |
| `--html` | **推奨** | markdown 中の **生 HTML タグ**（`<div class="columns">`・`.placeholder` など）を有効化する。 |

補足:

- `--html` は「**生 HTML タグを許可する**」フラグであって、「HTML 形式で出力する」フラグではない。出力形式は `--pdf` / `--images` / `--pptx` や出力ファイルの拡張子で決まる。
- 本スライドは段組み（`<div class="columns">`）や画像枠（`<div class="placeholder">`）に生 HTML を使う。現行の Marp ではこれらは既定でも描画されるが、Marp の `html` 既定値はバージョン・設定で変わりうるため、**書き出しコマンドに `--html` も付けておくと環境差に左右されず確実**。
- `--allow-local-files` が要るのは PDF / PNG / PPTX のときだけ（HTML 出力では不要）。

## 書き出しコマンド

```bash
cd slides

# PDF（配布・印刷用）
marp FtpTransferAgent_社内説明スライド.md --pdf       --allow-local-files --html

# PNG（1 スライド = 1 枚）
marp FtpTransferAgent_社内説明スライド.md --images png --allow-local-files --html

# PPTX（PowerPoint で編集したい場合）
marp FtpTransferAgent_社内説明スライド.md --pptx      --allow-local-files --html

# HTML（プレビュー。出力形式は拡張子で指定。--html は生 HTML タグの有効化）
marp FtpTransferAgent_社内説明スライド.md -o preview.html --html
```

Marp CLI を常設したくない場合は `npx` で都度実行できる:

```bash
npx -y @marp-team/marp-cli@latest FtpTransferAgent_社内説明スライド.md --pdf --allow-local-files --html
```

PDF / PNG / PPTX 出力には Chromium 系ブラウザ（Chrome / Edge / Firefox のいずれか）が必要。
未インストールなら次で取得し、`CHROME_PATH` を渡す:

```bash
npx -y puppeteer browsers install chrome
export CHROME_PATH="$HOME/.cache/puppeteer/chrome/<version>/chrome-linux64/chrome"
```

## 図版（SVG）について

- 図は GitHub・Marp の双方で表示できるよう **SVG ファイル**で管理している。
- スライドの配色（`#0b548c` / `#0f7a57` / `#15729e`）に合わせて手書きで作成。
- 図を差し替えるときは `slides/images/*.svg` を編集する。整形式 XML であること（`xmllint --noout` 等で検証）。

> 参考: 技術詳細ドキュメント `docs/fanout-and-parallelism.md` では、GitHub 上でそのまま描画される
> **Mermaid** で同じ概念を図示している。「GitHub で読む資料 = Mermaid、スライド配布 = SVG」という使い分け。
