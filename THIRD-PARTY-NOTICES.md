# 第三者のソフトウェア・資料に関する表記

YMM4 パペット変形改プラグイン（YMM4PuppetKai）の配布物（`YMM4PuppetKai.dll`）には、第三者のソフトウェアのコードやバイナリは含まれていません。
以下は、実行時に利用するもの、開発時に参考にしたものの一覧です。

## 実行時に利用するもの（配布物には含まれません）

以下はゆっくりMovieMaker4（YMM4）に同梱されているものを、YMM4 上で動作するときにそのまま利用します。本プラグインはこれらを再配布しません。

| 名称 | 提供元 | ライセンス |
|---|---|---|
| ゆっくりMovieMaker4 本体およびプラグインAPI（YukkuriMovieMaker.Plugin ほか） | 饅頭遣い | YMM4 の利用規約に従います |
| Vortice.Windows（Vortice.Direct2D1・Vortice.D3DCompiler ほか） | Amer Koleci and contributors | MIT License |
| SharpGen.Runtime | SharpGenTools contributors | MIT License |
| Newtonsoft.Json | James Newton-King | MIT License |
| .NET（WPF を含む） | Microsoft | MIT License |

## 開発時に参考にしたもの（コードは含みません）

### YMM4 本体のパペット変形
- 提供元: routersys（元は YMM4-PuppetDeformation、MIT License。今は YMM4 本体の `YukkuriMovieMaker.Plugin.Community.dll` に入っています）
- 仕組みと動きの確認のために読みました。コードは使っていません。

### 論文
- Schaefer, McPhail, Warren (2006) "Image Deformation Using Moving Least Squares"
- Igarashi, Moscovich, Hughes (2005) "As-Rigid-As-Possible Shape Manipulation"
- Sorkine, Alexa (2007) "As-Rigid-As-Possible Surface Modeling"
- Peng et al. (2018) "Anderson Acceleration for Geometry Optimization and Physics Simulation"
- Cuthill, McKee (1969) "Reducing the Bandwidth of Sparse Symmetric Matrices"

### 資料
- Microsoft Docs: Direct2D カスタムエフェクトの頂点シェーダー
