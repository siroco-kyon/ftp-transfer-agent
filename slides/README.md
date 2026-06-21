# スライド（Marp）ビルド手順

`slides/` 配下の Marp スライドを PDF / PNG / PPTX に書き出す手順をまとめる。

対象:

- `FtpTransferAgent_社内説明スライド.md` — 社内説明用
- 図版は `slides/images/*.svg`（`![](images/xxx.svg)` で参照）

## ⚠️ 最重要: ローカル画像を含む書き出しは `--allow-local-files` が必須

このスライドは図を **ローカルの SVG ファイル**（`images/` 配下）として参照している。
Marp CLI は PDF / PNG / PPTX への書き出し時、セキュリティのため **既定ではローカルファイルを読み込まない**。
`--allow-local-files` を付け忘れると、**図が描画されず代わりに alt テキストだけが出る**ので注意。

> HTML 出力（`--html`）では不要。PDF / PNG / PPTX のときだけ必要。

## 書き出しコマンド

```bash
cd slides

# PDF（配布・印刷用）
marp FtpTransferAgent_社内説明スライド.md --pdf --allow-local-files

# PNG（1 スライド = 1 枚）
marp FtpTransferAgent_社内説明スライド.md --images png --allow-local-files

# PPTX（PowerPoint で編集したい場合）
marp FtpTransferAgent_社内説明スライド.md --pptx --allow-local-files

# HTML（プレビュー。ローカル画像でもフラグ不要）
marp FtpTransferAgent_社内説明スライド.md --html
```

Marp CLI を常設したくない場合は `npx` で都度実行できる:

```bash
npx -y @marp-team/marp-cli@latest FtpTransferAgent_社内説明スライド.md --pdf --allow-local-files
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
