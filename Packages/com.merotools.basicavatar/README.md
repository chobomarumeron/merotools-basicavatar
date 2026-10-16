# MeroTools BasicAvatar

VRChat アバターの基本セットアップを、チェックで選んで一括実行・一括削除する Unity エディタ拡張です。
Unity 2022.3 LTS 対応。

## できること

| 項目 | 内容 |
|---|---|
| Anchor Object | 指定ボーン(基本 Hips)と同じ位置・回転の空オブジェクトを作成 |
| MA MeshSettings | Anchor Override / Bounds を「設定」にして Anchor Object を指定。配下の MeshSettings もルートに同期 |
| AAO Trace And Optimize | ルートに追加 |
| VRCFury Fix Write Defaults | Auto モードで追加 |
| LAC TextureCompressor | High Quality プリセットで追加 |
| MA Floor Adjuster | 「床の位置に調整してね」オブジェクトに追加 (**靴底の高さに手で合わせてください**) |

導入していないツールの項目は、警告を出してスキップします。

## 必要なツール (使う項目だけ)

- Modular Avatar (Floor Adjuster は 1.17.0 以上): https://modular-avatar.nadena.dev/ja
- AAO: Avatar Optimizer: https://vpm.anatawa12.com/avatar-optimizer/ja/
- VRCFury: https://vrcfury.com/download
- LAC: Avatar Compressor: https://github.com/Limitex/avatar-compressor

## 使い方

1. メニュー `Tools > MeroTools > BasicAvatar Setup` を開く (Hierarchy 右クリック `MeroTools > Apply BasicAvatar Setup` でも可)
2. アバターを選び、必要ならアンカーボーンを変更
3. 実行したい項目にチェックを入れて「セットアップ実行」
4. 外したいときは、チェックを入れて「チェックした項目をアバターから削除」 (Ctrl+Z で戻せます)

## ライセンス

MIT
