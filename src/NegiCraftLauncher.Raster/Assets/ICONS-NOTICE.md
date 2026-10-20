# 第三方图标声明 / Third-party icon notice

`icon_fabric.png`、`icon_forge.png`、`icon_quilt.png`、`icon_neoforge.png` 这四张是
**加载器官方标志**，不是本项目的作品；`icon_neoforge_negi.png` 是**本项目自己的美术**（见文末）。

| 文件 | 来源仓库 | 原始文件 | 许可 |
| --- | --- | --- | --- |
| `icon_fabric.png` | https://github.com/FabricMC/fabricmc.net | `assets/logo.png`（52×56） | MIT |
| `icon_forge.png` | https://github.com/MinecraftForge/Installer（分支 `1.x`） | `big_logo.png` 里的铁砧；实际取的是 Forge 打进 `forge-*-universal.jar` 的 `forge_icon.png`（48×48） | LGPL-2.1 |
| `icon_quilt.png` | https://github.com/QuiltMC/art | `brand/1024png/quilt_mini_icon_transparent.png`（1024×1024） | **未附 LICENSE**（GitHub 识别为 NOASSERTION） |
| `icon_neoforge.png` | https://github.com/neoforged/.github | `art/neoforged_icon_16x16.png`（16×16） | Unlicense（公共领域） |
| `icon_neoforge_negi.png` | 本项目 `src/NegiCraftLauncher.App/Assets/app.png` | 裁头部 | MIT（本项目） |

## 处理方式（可复现）

五张都被统一成 **80×80**（Forge 那张自带深蓝底板，其余透明底）。图标位显示尺寸最大 40 DIP，
这里出 2 倍图：DIP 要乘系统缩放才是物理像素，40 DIP 在 125% 下要 60 个物理像素，
出 40 的位图会被合成器非整数放大、棱线糊掉；80 在 200% 下正好 1:1，其余缩放档次都是缩小
（缩小永远比放大好看）。

- Fabric：52×56 → 按 80/56 等比盒式缩到 74×80（在预乘 alpha 空间做平均，不然透明边缘
  会糊出一圈黑边）→ 居中放进 80×80。
- Forge：48×48 → 等比盒式放到 76×76 → 居中（留 2px 边）。48 和 80 不整除，
  所以不追求整数倍：源图本身已经比 40 DIP 大，显示时是缩小。
  **为什么不用那条 256×64 的横幅**：裁出来的铁砧是 74×38，塞进方形槽位只剩一半高度，
  而且深色铁砧在深色 UI 上几乎看不见；`forge_icon.png` 是 Forge 自己给模组列表用的方形图标，
  深蓝底 + 白铁砧，40 DIP 下一眼就认得出。
- Quilt：1024×1024 里图形占 `(140,140)-(1020,1020)`（881×881），先裁掉这圈留白，
  再盒式缩到 70×70 居中（88%）。
- NeoForge（官方）：16×16 → 整数 5 倍最近邻扩到 80×80。不用小数倍缩放：16→80 恰好
  整数倍，最近邻不会啃出一半宽一半窄的像素边。
- NeoForge（自家）：`app.png`（512×512，橙狐狸叼葱）裁 `(157,56)-(505,312)` ——
  逐行游程实测后定的框：整颗头（耳朵 y≈62 起、脸的左右沿 x=157~413）加整根葱（伸到 x≈501），
  再盒式缩到 69×51、居中。**缩到 86% 而不是铺满**，否则头会顶到槽位圆角、看着像被切掉。

方块立方体同样出 **80×80**：立方体是 16×16 贴图铺出来的，尺寸取 16 的整数倍才是
「一个纹素占整数个像素」，80 = 16 × 5。

**NeoForge 为什么有两张**：NeoForge 的标志是只狐狸，本项目的 launcher 图标
（`app.png`／`NegiPet`）也是只狐狸。默认用**官方那张**（跟 PCL2 / HMCL 一致，玩家一眼
对得上号）；自家的橙狐狸留在包里，改 `MainWindowViewModel.NeoForgeIcon` 一个常量就能切到
`neoforge_negi`，让实例列表、停靠栏、桌宠是同一套形象。

**商标**：前四行的标志商标权仍归 FabricMC / MinecraftForge / QuiltMC / NeoForged。
这里只是「哪家加载器的实例就显示哪家的标志」，属于指明来源的用法。

**Quilt 那行的许可是硬伤**：`QuiltMC/art` 公开挂在 GitHub 上供社区取用，但整个仓库没写
LICENSE，严格说等于「保留所有权利」。这里按「指明来源」的合理使用处理 —— 只用来标出
「这个实例跑的是 Quilt Loader」，不修改、不声称归属。真要收紧的话，把这个文件从包里去掉、
让 `LoaderIconName` 不再映射 `quilt` 即可，其余代码不用动。

## 为什么只有 OptiFine 没有标志

**四家加载器现在都有标志了**，只有 OptiFine 还是按 id 散列的方块 —— 它闭源，没有仓库、
官网也没有可当方形标志用的图，唯一的官方视觉是「OptiFine」这几个字。拿不到就是拿不到，
不拿方块硬凑（试过的都在下面这张表里）。

Forge 和 Quilt 是后来补上的，当初卡住的两个点在下面留档：

- **Forge**：一开始只找到 `MinecraftForge/Installer` 的 `big_logo.png`（256×64 的
  「铁砧 + FORGE」词标）。裁出来的铁砧是 74×38，塞进方形槽位只剩一半高度；
  深色铁砧在深色 UI 上也看不清。**后来发现 Forge 自己往 `forge-*-universal.jar` 里塞了
  `forge_icon.png`（48×48，深蓝底白铁砧）** —— 这才是它给模组列表用的图标，直接用它。
  Installer 仓库是 LGPL-2.1，连带声明写在上面那张表里。
- **Quilt**：`QuiltMC/art` 有全套品牌图，其中 `quilt_mini_icon_transparent.png` 就是为
  小尺寸准备的（三个色块 + 一个菱形）。但仓库**没写 LICENSE**，见上一节的说明。

也试过给它们各配一块有象征意义的方块（PCL2 就是这么干的：它的
`Plain Craft Launcher 2/Images/Blocks/Anvil.png` 拿铁砧方块当 Forge 的图标）。实测在
40×40 下都不成立，所以没走这条路：

| 试过的 | 结果 |
| --- | --- |
| 铁砧 `anvil` | `anvil.png` 是深铁色，渲染出来是一块几乎纯黑的立方体 |
| 熔炉 `furnace` | 顶面 + 侧面都是石头灰，只有正面有炉口，看不出是熔炉 |
| 锻造台 `smithing_table` | 深色近黑，40px 下糊成一团 |

结论：OptiFine 与**原版**走**按实例 id 散列挑方块**；有标志的四家一律挂自己的标志。

## 方块图标从哪来

**从玩家自己电脑上的游戏 jar 里现读现画**，项目里不存任何方块图片。

```
<GameRoot>/versions/<版本>/<版本>.jar → assets/minecraft/textures/block/*.png
```

16×16 的原生贴图 + **真等距投影**（顶面菱形 + 左右两个侧面，分别乘 1.0 / 0.78 / 0.62 的
明暗）当场渲染成 80×80 的立方体。好处是拿到了玩家那个版本（乃至他自己改过的资源包）的真
贴图，而且一张美术资源都不用打包、没有任何授权问题。

**几何用 √3:1，不是像素画那套 2:1**：真等距投影三个轴在屏幕上夹角 120°，顶面菱形
宽:高 = √3 : 1，总宽只有总高的 √3/2 ≈ **0.866 倍**。总高吃满 80 之后菱形高与侧面高
都是 40，只有水平方向收窄，于是 bbox 是 70×80 而不是 80×80 —— 那 15% 就是「方不方」的差别。
2:1 的好处是斜边能落在像素网格上，代价是总宽 = 总高、整块看着像压扁的盒子；这里选了体积感。

PCL2 / HMCL 的 64×64 方块图走的是另一条路 —— 顶面菱形宽 32、侧面各 32 高，正好 64×64，
即「16×16 贴图 → 4 倍 2:1 等距立方体」，比真等距宽 15%。它们把成品打进包里，
是为了在游戏还没下载时也能显示。

候选方块共 30 块（土石木、带图案的、矿物块、异界/深海/末地四组），同一个实例永远同一块，
不同实例尽量不一样。表在 `MainWindowViewModel.BlockTextureFiles`，加方块只改那一处。

挑选时踩过的两个坑，留着当记录：

- **`anvil` / `obsidian` 这类深色贴图在 40px 下就是黑块**，已经剔掉。
- **原版方块贴图有 623/990 张是 4 位索引色**（`anvil_top`、`glowstone` 都是），
  `PngCodec` 原来只认 8/16 位，读它们会抛异常、静默退化成程序化画的假方块 ——
  4 位支持是这一轮补上的。

## 想换成自己的图

把 `Assets/icon_*.png` 换成 80×80（或 2 的整数倍）的 PNG、同名即可，代码不用动 ——
`BuiltinIcons` 只按文件名找资源；找不到就回退到方块立方体，不会开天窗。
