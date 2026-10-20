# 第三方图标声明 / Third-party icon notice

`icon_fabric.png`、`icon_neoforge.png` 这两张是**加载器官方标志**，不是本项目的作品；
`icon_neoforge_negi.png` 是**本项目自己的美术**（见文末）。

| 文件 | 来源仓库 | 原始文件 | 许可 |
| --- | --- | --- | --- |
| `icon_fabric.png` | https://github.com/FabricMC/fabricmc.net | `assets/logo.png`（52×56） | MIT |
| `icon_neoforge.png` | https://github.com/neoforged/.github | `art/neoforged_icon_16x16.png`（16×16） | Unlicense（公共领域） |
| `icon_neoforge_negi.png` | 本项目 `src/NegiCraftLauncher.App/Assets/app.png` | 裁头部 | MIT（本项目） |

## 处理方式（可复现）

三张都被统一成 **80×80 透明底**。图标位显示尺寸最大 40 DIP，这里出 2 倍图：
DIP 要乘系统缩放才是物理像素，40 DIP 在 125% 下要 60 个物理像素，出 40 的位图会被
合成器非整数放大、棱线糊掉；80 在 200% 下正好 1:1，其余缩放档次都是缩小（缩小永远比放大好看）。

- Fabric：52×56 → 按 80/56 等比盒式缩到 74×80（在预乘 alpha 空间做平均，不然透明边缘
  会糊出一圈黑边）→ 居中放进 80×80。
- NeoForge（官方）：16×16 → 整数 5 倍最近邻扩到 80×80。不用小数倍缩放：16→80 恰好
  整数倍，最近邻不会啃出一半宽一半窄的像素边。
- NeoForge（自家）：`app.png`（512×512，橙狐狸叼葱）裁 `(157,56)-(505,312)` ——
  逐行游程实测后定的框：整颗头（耳朵 y≈62 起、脸的左右沿 x=157~413）加整根葱（伸到 x≈501），
  再盒式缩到 69×51、居中。**缩到 86% 而不是铺满**，否则头会顶到槽位圆角、看着像被切掉。

方块立方体同样出 **80×80**：立方体是 16×16 贴图铺出来的，尺寸取 16 的整数倍才是
「一个纹素占整数个像素」，80 = 16 × 5。

**NeoForge 为什么有两张**：NeoForge 的标志是只狐狸，本项目的 launcher 图标
（`app.png`／`NegiPet`）也是只狐狸。默认用自家的那只（`MainWindowViewModel.NeoForgeIcon`
里一个常量就能换回官方版），这样实例列表、停靠栏、桌宠是同一套形象。

**商标**：前两行的标志商标权仍归 FabricMC 与 NeoForged。这里只是「哪家加载器的实例就显示
哪家的标志」，属于指明来源的用法。

## 为什么没有 Forge / Quilt / OptiFine 的标志

去翻过各自的项目，结论是这三家都拿不到能直接塞进 MIT 仓库的方形标志：

- **Forge**：官方铁砧只存在于词标横幅里（`MinecraftForge/Installer` 的 `big_logo.png`
  是 256×64 的「铁砧 + FORGE」，`MinecraftForge/Web` 只有 svg 且该仓库没写许可）。
  裁出铁砧是 76×41，塞进 40×40 只剩 40×22，一米粒大的铁砧。而且 Installer 仓库是
  LGPL-2.1 —— 能用但得连带声明，不如换条路。
- **Quilt**：`QuiltMC/art` 有 `brand/1024png/quilt_logo_dark.png`，但整个仓库
  **没有 LICENSE**（GitHub 识别为 NOASSERTION），README 是空的，没法主张任何权利。
- **OptiFine**：闭源，没有仓库也没有可再分发的素材。

也试过给它们各配一块有象征意义的方块（PCL2 就是这么干的：它的
`Plain Craft Launcher 2/Images/Blocks/Anvil.png` 拿铁砧方块当 Forge 的图标）。实测在
40×40 下都不成立，所以放弃了：

| 试过的 | 结果 |
| --- | --- |
| 铁砧 `anvil` | `anvil.png` 是深铁色，渲染出来是一块几乎纯黑的立方体 |
| 熔炉 `furnace` | 顶面 + 侧面都是石头灰，只有正面有炉口，看不出是熔炉 |
| 锻造台 `smithing_table` | 深色近黑，40px 下糊成一团 |

结论：这三家（以及原版）都走**按实例 id 散列挑方块**，不为加载器编造语义。

## 方块图标从哪来

**从玩家自己电脑上的游戏 jar 里现读现画**，项目里不存任何方块图片。

```
<GameRoot>/versions/<版本>/<版本>.jar → assets/minecraft/textures/block/*.png
```

16×16 的原生贴图 + 2:1 等距投影（顶面菱形 + 左右两个侧面，分别乘 1.0 / 0.78 / 0.62 的
明暗）当场渲染成 80×80 的立方体。好处是拿到了玩家那个版本（乃至他自己改过的资源包）的真
贴图，而且一张美术资源都不用打包、没有任何授权问题。

PCL2 / HMCL 那些 64×64 的方块图也是这么来的 —— 顶面菱形宽 32、侧面各 32 高，正好 64×64，
就是「16×16 贴图 → 4 倍等距立方体」。它们把成品打进包里，是为了在游戏还没下载时也能显示。

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
