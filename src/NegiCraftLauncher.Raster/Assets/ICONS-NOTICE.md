# 第三方图标声明 / Third-party icon notice

本目录下的图分三类：

| 类别 | 文件 | 来源 | 许可 |
| --- | --- | --- | --- |
| 加载器官方标志 | `icon_fabric.png` | https://github.com/FabricMC/fabricmc.net | MIT |
| 加载器官方标志 | `icon_quilt.png` | https://github.com/QuiltMC/art | **未附 LICENSE**（GitHub 识别为 NOASSERTION） |
| 加载器官方标志 | `icon_neoforge.png` | https://github.com/neoforged/.github | Unlicense（公共领域） |
| 加载器官方标志 | `icon_neoforge_negi.png` | 本项目 `src/NegiCraftLauncher.App/Assets/app.png` | MIT（本项目） |
| **等距方块图标** | `block_*.png`（18 张） | **https://ccvaults.com/**（ccLeaf） | **© 2025-2026 ccLeaf，All Rights Reserved —— 非自由许可，见下文专节** |

---

## 等距方块图标（`block_*.png`）—— 来源与授权

**来源**：ccVaults（ccLeaf 旗下的 Minecraft 素材库，https://ccvaults.com/）的
**Blocks** 分类（`20. Blocks` 下的 `02. Planks` / `07. Logs` / `08. Material` /
`12. Bricks` / `18. Decoration` / `22. Redstone` / `23. Sand` / `33. Workplaces` /
`37. Nether`）。原图是 **1024×1024** 的预渲染等距方块图。

**⚠️ 授权状况（重要，不是自由许可）**：

- ccVaults 页脚为 `© 2025-2026 ccLeaf — All Rights Reserved`，没有任何 per-asset 许可声明。
- ccLeaf《Terms of Use》第 9 条明令禁止 *"Scrape, automate, or abuse the Service"*，
  第 8 条禁止再分发 / 提取资产；第 4.3 条把下载额度与订阅计划绑定。
- 全尺寸原图需要登录（HTTP 302），公开可取的只有缩略图。
- 也就是说：**这些图不能自由再分发**。放进一个会分发给别人的启动器里，
  严格看是踩在对方禁止项上的。

**花子 2026-10-20 明确拍板使用**（「没有关系，我们用 ccVaults 的就好，到时候再 README
提及一下用了他的资源」），并要求 README 注明来源。README 的「第三方资源」一节即为此设。

真要换掉的话，删掉 `Assets/block_*.png`、把 `MainWindowViewModel.BlockIconKeys` 清空即可 ——
`BlockIcon` 会退到程序化画的立方体兜底，其余代码不用动。

**处理方式（可复现）**：1024×1024 → **128×128**。

- **预乘 alpha 的盒式（面积平均）重采样**。直线 alpha 空间里做平均会在透明边缘攒出
  深色晕圈（半透明像素和「旁边是透明黑」平均）；预乘再除回来才对。
- 盒式而非双线性：这些是等距渲染图，要的是平均掉高频，不是重新采样出平滑渐变。
- **不裁内容外框**。17 张立方体的取景完全一致（`x64..959` / `y16..1007`，即
  896×992，占画布 87.5% × 96.9%）；铁砧天然小一圈（`x144..879` / `y48..959`，
  736×912）—— 这正是它该有的样子，裁齐反而失真。

**为什么是 128 而不是 80**：图标位 40 DIP，系统缩放 250% 时要 100 个物理像素，
80 就得被放大、方块棱线发糊；128 在任何常见缩放下都是缩小。18 张合计约 **146 KiB**。
`MainWindowViewModel.IconSize`（80）负责把它缩到显示尺寸。

**18 张的清单**（key = `block_<key>.png`）：

| key | ccVaults 文件 | 子分类 |
| --- | --- | --- |
| `grass` | `Grass_Block.png` | 18. Decoration |
| `dirt` | `Dirt.png` | 18. Decoration |
| `stone` | `Stone.png` | 18. Decoration |
| `cobble` | `Cobblestone.png` | 18. Decoration |
| `planks` | `Oak_Planks.png` | 02. Planks |
| `log` | `Oak_Log.png` | 07. Logs |
| `sand` | `Sand.png` | 23. Sand |
| `brick` | `Bricks.png` | 12. Bricks |
| `bookshelf` | `Bookshelf.png` | 18. Decoration |
| `crafting` | `Crafting_Table.png` | 33. Workplaces |
| `furnace` | `Furnace.png` | 33. Workplaces |
| `tnt` | `Tnt.png` | 22. Redstone |
| `gold` | `Gold_Block.png` | 08. Material |
| `diamond` | `Diamond_Block.png` | 08. Material |
| `emerald` | `Emerald_Block.png` | 08. Material |
| `redstone` | `Redstone_Block.png` | 22. Redstone |
| `glowstone` | `Glowstone.png` | 37. Nether |
| `anvil` | `Anvil.png` | 33. Workplaces |

候选池 = `MainWindowViewModel.BlockIconKeys` 整张表。**加一块方块**：往这个目录丢一张
`block_<key>.png`，再把 key 加进那张表（csproj 用的是 `Assets\block_*.png` 通配，
不用改工程文件）。

**商标**：方块造型与贴图的著作权归 Mojang / Microsoft。这里只是「哪家游戏文件的实例
就显示哪块方块的等距渲染」，属于指明来源的用法。

---

## 加载器标志（`icon_*.png`）

四张都被统一成 **80×80** 透明底。图标位显示尺寸最大 40 DIP，这里出 2 倍图：
DIP 要乘系统缩放才是物理像素，40 DIP 在 125% 下要 60 个物理像素，出 40 的位图会被
合成器非整数放大、棱线糊掉；80 在 200% 下正好 1:1，其余缩放档次都是缩小
（缩小永远比放大好看）。

- Fabric：52×56 → 按 80/56 等比盒式缩到 74×80（在预乘 alpha 空间做平均，不然透明边缘
  会糊出一圈黑边）→ 居中放进 80×80。
- Quilt：1024×1024 里图形占 `(140,140)-(1020,1020)`（881×881），先裁掉这圈留白，
  再盒式缩到 70×70 居中（88%）。
- NeoForge（官方）：16×16 → 整数 5 倍最近邻扩到 80×80。不用小数倍缩放：16→80 恰好
  整数倍，最近邻不会啃出一半宽一半窄的像素边。
- NeoForge（自家）：`app.png`（512×512，橙狐狸叼葱）裁 `(157,56)-(505,312)` ——
  逐行游程实测后定的框：整颗头（耳朵 y≈62 起、脸的左右沿 x=157~413）加整根葱（伸到 x≈501），
  再盒式缩到 69×51、居中。**缩到 86% 而不是铺满**，否则头会顶到槽位圆角、看着像被切掉。

**NeoForge 为什么有两张**：NeoForge 的标志是只狐狸，本项目的 launcher 图标
（`app.png`／`NegiPet`）也是只狐狸。默认用**官方那张**（跟 PCL2 / HMCL 一致，玩家一眼
对得上号）；自家的橙狐狸留在包里，改 `MainWindowViewModel.NeoForgeIcon` 一个常量就能切到
`neoforge_negi`，让实例列表、停靠栏、桌宠是同一套形象。

**商标**：表格里的标志商标权仍归 FabricMC / QuiltMC / NeoForged。
这里只是「哪家加载器的实例就显示哪家的标志」，属于指明来源的用法。

**Quilt 那行的许可是硬伤**：`QuiltMC/art` 公开挂在 GitHub 上供社区取用，但整个仓库没写
LICENSE，严格说等于「保留所有权利」。这里按「指明来源」的合理使用处理 —— 只用来标出
「这个实例跑的是 Quilt Loader」，不修改、不声称归属。真要收紧的话，把这个文件从包里去掉、
让 `LoaderIconName` 不再映射 `quilt` 即可，其余代码不用动。

---

## Forge 为什么用铁砧方块

曾经试过三条路，最后落在「内嵌一张按铁砧真实造型画的等距图」：

1. **从 256×64 词标横幅裁铁砧**（`MinecraftForge/Installer` 分支 `1.x` 的 `big_logo.png`）——
   裁出来 74×38，塞进方形槽位只剩一半高度，深色铁砧在深色 UI 上几乎看不见。
2. **用 Forge 打在 `forge-*-universal.jar` 里的 `forge_icon.png`**（48×48，深蓝底白铁砧）——
   能看清，但那块深蓝底板跟界面不搭，而且它毕竟是第三方位图。
3. **铁砧方块**（当前方案）—— 和原版实例那套方块图标同一套观感，
   玩家认知里 Forge 就是「铁砧」。

代价是铁砧整体偏深，所以它**不进按 id 散列的那个方块池**（`BlockIcons` 里排掉了），
只会在「确定是 Forge」的时候出现。表在 `MainWindowViewModel.BlockIconKeys`。

### 铁砧不能按完整方块画（历史记录）

铁砧在游戏里**就不是一个完整方块**。jar 里 `models/block/template_anvil.json` 把它
拆成四段（坐标单位 1/16 格）：底座 `(2,0,2)-(14,4,14)`、收腰 `(4,4,3)-(12,5,13)`、
砧颈 `(6,5,4)-(10,10,12)`、砧面 `(3,10,0)-(13,16,16)`。硬塞进立方体模板会得到一块
「塌了口的盒子」：顶面砧盘中间是空的，侧面正视图又被斜切，铁砧的造型全丢。

早先因此另写了一条 `PixelArt.CreateAnvil`，按上面四段几何现拼（真等距投影，
明暗 1.0 / 0.78 / 0.62，与立方体同一套）。**这条路已经废弃**，原因是投影公式对偏移量
极其敏感：少加一个 `sideH`，整个铁砧就上移半个画布、顶部被裁，`FillFrame` 只能按被切过的
内容裁齐，于是形状崩坏；80px 下因数值巧合看着还行，160px 一放大就露馅。
现在改用 ccVaults 的预渲染 `block_anvil.png`，几何由对方保证。

**旁证**：PCL2 与 HMCL 也都用铁砧当 Forge 图标，而且都是这个形状 ——
`PCL2/Plain Craft Launcher 2/Images/Blocks/Anvil.png`（64×64）与
`HMCL/HMCL/src/main/resources/assets/img/forge.png`（32×32）。
两家的图分别是 LGPL-3.0 与 GPL-3.0，**不能拿来用**。

---

## 为什么只有 OptiFine 没有标志

**五家加载器里四家认得出来**：Fabric / Quilt / NeoForge 挂自己的官方标志，
Forge 挂游戏里的铁砧方块；只有 OptiFine 还是按 id 散列的方块 —— 它闭源，没有仓库、
官网也没有可当方形标志用的图，唯一的官方视觉是「OptiFine」这几个字。拿不到就是拿不到，
不拿方块硬凑。

Quilt 是后来补上的，当初卡住的点在下面留档：

- **Quilt**：`QuiltMC/art` 有全套品牌图，其中 `quilt_mini_icon_transparent.png` 就是为
  小尺寸准备的（三个色块 + 一个菱形）。但仓库**没写 LICENSE**，见上一节的说明。
- **Forge** 的弯路（横幅 → jar 里的 `forge_icon.png` → 铁砧方块）记在上一节。

也试过给 Forge / OptiFine 各配一块有象征意义的方块。当年 40px 出图加最近邻缩放时
这几块都不成立，所以搁置了：

| 试过的 | 结果 |
| --- | --- |
| 铁砧 `anvil` | `anvil.png` 是深铁色，当年渲染出来是一块几乎纯黑的立方体 |
| 熔炉 `furnace` | 顶面 + 侧面都是石头灰，只有正面有炉口，看不出是熔炉 |
| 锻造台 `smithing_table` | 深色近黑，40px 下糊成一团 |

（后来图标改成 80px 出图 + HighQuality 缩小，铁砧才重新可用 —— 也就是现在 Forge 的方案。）

结论：OptiFine 与**原版**走**按实例 id 散列挑方块**；其余四家各认各的。

---

## 兜底与替换

`BlockAssets.Load` 读不到资源时返回 `null`，`MainWindowViewModel.BlockIcon` 会退到
`PixelArt.CreateBlockCube` 程序化画一个立方体，不让图标位开天窗。
`PixelArt.CreateBlockCube(string, int)` 现在**只服务这条兜底路径**，正常显示走 `block_*.png`。

**想换成自己的图**：把 `Assets/block_*.png` 或 `Assets/icon_*.png` 同名替换即可，
代码不用动 —— 加载器只按文件名找资源。
