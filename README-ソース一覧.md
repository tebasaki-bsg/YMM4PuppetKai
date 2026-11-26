# パペット変形改（YMM4PuppetKai）ソース一覧

- 版：**v1.0.1**（2026-11-26。輪郭の細かい画像・ほぼ透明な点が散らばった画像で遅くなる不具合と、三角形が上限を超えて一部が描かれない不具合の修正。輪郭を間引く基準を 0.75px → 1.5px にし、上限を超えるときは内側の間隔ではなく輪郭を段階的に粗くする（`AlphaOutline.Coarser`）。YMM4 上での確認待ち。v1.0.0 は YMM4 v4.57.0.1 Lite 上で確認済み）
- ユーザーのパソコンの git のフォルダー（`Documents\YMM関係\YMM4PuppetKai\`、GitHub の tebasaki-bsg/YMM4PuppetKai）と同じ内容です（仕様書の写しはそちらでは `docs/`）。
- この `claude/source/YMM4PuppetKai/` 以下は、いちばん新しくビルドした版のソースです。ビルドするたびに丸ごと入れ替えます（古い版は残しません）。フォルダの形は作業用のフォルダと同じです。
- 仕様書は Claude Docs の「パペット変形改（YMM4）仕様書（たたき台）」が正本です。このプロジェクトには写しを `claude/specs/YMM4PuppetKai-仕様書.md` に置いています。
- 入れていないもの：
  - テスト用の手の画像の α（`tests/PuppetKai.Tests/hand_mask.raw`、560×600 の 8bit、336,000 バイト）。バイナリなので入れていません。テストの `--bench` と `--tol` で使います（ふつうのテストには不要）。
  - `nuget.config`（作業環境用。パッケージの取得先を空にしているだけ）、ビルドで作られるもの（bin・obj・tests/out・.ymme）。
  - 開発ノート（正本はプロジェクトの `claude/ymm4-plugin-notes.md`）。
- `Directory.Build.props` は各自の設定ではなく、言語の版・Nullable・unsafe の共通設定なので入れています。YMM4 のフォルダは csproj の `YMM4DirPath`（ビルドのときに `-p:YMM4DirPath="C:\YMM4\"` で渡す。末尾の `\` を忘れずに）。
- ビルド：`dotnet build src/YMM4PuppetKai/YMM4PuppetKai.csproj -c Release -p:YMM4DirPath=…`（Linux でも可。WPF の DLL も YMM4 のフォルダから参照）。できた DLL と `dist/` の txt を `YMM4PuppetKai/` フォルダに入れて zip にし、拡張子を `.ymme` にします。
- テスト：`dotnet run --project tests/PuppetKai.Tests -c Release`（40 件。YMM4 に依存しない計算部分 `PuppetKai.Core`）。
- `README.md` は GitHub のリポジトリのトップに出る説明（利用者向けの説明と開発者向けのビルド方法）。配布物の説明は `dist/README.txt`。`LICENSE`・`THIRD-PARTY-NOTICES.md` はリポジトリのトップ用（高速PSD図形と同じ位置）で、`dist/` の txt は同じ内容の配布物用。
- 構成：`PuppetKai.Core`（輪郭・網目・MLS・ARAP・ボーン。YMM4 に依存しない）を、`YMM4PuppetKai.csproj` がソースのまま取り込み、DLL を1つにしています。

## ファイル（SHA-256 の先頭12文字）

| ファイル | 大きさ（バイト） | SHA-256 |
|---|---:|---|
| `Directory.Build.props` | 278 | `b332144931c5` |
| `LICENSE` | 1065 | `ef29adbac208` |
| `README.md` | 12555 | `7942210987cd` |
| `THIRD-PARTY-NOTICES.md` | 1986 | `f0b696c68833` |
| `dist/LICENSE.txt` | 1089 | `e5c155c2603a` |
| `dist/README.txt` | 7811 | `55fd2d3bcfc7` |
| `dist/THIRD-PARTY-NOTICES.txt` | 967 | `712428a8edd6` |
| `src/PuppetKai.Core/AlphaOutline.cs` | 15505 | `abbe4caa9825` |
| `src/PuppetKai.Core/ArapSolver.cs` | 15649 | `fef6ae7e2fdb` |
| `src/PuppetKai.Core/BandCholesky.cs` | 10012 | `f85b672ba159` |
| `src/PuppetKai.Core/Delaunay.cs` | 8585 | `2143b78f274b` |
| `src/PuppetKai.Core/MeshBuilder.cs` | 14319 | `57967a5a1b7b` |
| `src/PuppetKai.Core/Mls.cs` | 5422 | `4ed8159eee50` |
| `src/PuppetKai.Core/PuppetDeformer.cs` | 7192 | `8cb081dce811` |
| `src/PuppetKai.Core/PuppetKai.Core.csproj` | 179 | `49101f0d0abe` |
| `src/PuppetKai.Core/PuppetMesh.cs` | 8488 | `255e79a31c38` |
| `src/PuppetKai.Core/Rig.cs` | 5749 | `6d31d41c1c87` |
| `src/YMM4PuppetKai/KaiCanvas.cs` | 30314 | `185ecd2791d4` |
| `src/YMM4PuppetKai/KaiEditor.cs` | 28327 | `d50ea2f2c3be` |
| `src/YMM4PuppetKai/KaiLog.cs` | 752 | `1c458a999224` |
| `src/YMM4PuppetKai/KaiMeshEffect.cs` | 9140 | `c7718acda9ed` |
| `src/YMM4PuppetKai/KaiProcessor.cs` | 12126 | `4d0baae78be2` |
| `src/YMM4PuppetKai/KaiStats.cs` | 3717 | `cd7b124a757c` |
| `src/YMM4PuppetKai/Models.cs` | 10138 | `bab714cd9753` |
| `src/YMM4PuppetKai/PuppetKaiEffect.cs` | 4430 | `fa74e3845c7f` |
| `src/YMM4PuppetKai/Shaders/MeshPS.hlsl` | 288 | `e82d764f8054` |
| `src/YMM4PuppetKai/Shaders/MeshVS.hlsl` | 1021 | `fb506a4af76b` |
| `src/YMM4PuppetKai/YMM4PuppetKai.csproj` | 2842 | `604fbb5e2e7d` |
| `tests/PuppetKai.Tests/Program.cs` | 29144 | `6e6bae7250f0` |
| `tests/PuppetKai.Tests/PuppetKai.Tests.csproj` | 273 | `6edae39855e2` |
