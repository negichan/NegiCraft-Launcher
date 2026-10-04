# AGENTS.md

## 验证 / 测试 UI：用进程内调试桥，不要模拟鼠标

需要看界面、切页面、验证布局或状态时，**一律走应用自带的调试桥**，不要用 `design/_shot.ps1` 那种移动光标 / 发送合成点击 / 抢焦点的脚本——那会干扰用户正在做的事（比如全屏游戏）。

调试桥只在 `--debug` 启动时开启，通过 `%TEMP%` 下的文件邮箱收发命令，全程不动光标、不抢焦点、不改窗口层级，被全屏应用盖着也能用。两个进程各有各的邮箱，可同时开：

| 进程 | Avalonia | WPF |
| --- | --- | --- |
| 启动器（邮箱 `ncl-debug`） | `App/Services/DebugBridge.cs` | `App.Wpf/Services/DebugBridge.cs` |
| 独立桌宠（邮箱 `ncl-pet-debug`） | `Pet.App/StandaloneDebugBridge.cs` | `Pet.App.Wpf/StandaloneDebugBridge.cs` |

桌宠那批动词（`pet-*` / `shot-pet` / `pet-dialog`）两边共用，Avalonia 侧实现在
`src/NegiCraftLauncher.Pet/Debug/PetDebugCommands.cs`，WPF 侧在
`src/NegiCraftLauncher.Pet.Wpf/Debug/PetDebugCommands.cs`；邮箱收发在各自的 `Debug/PetDebugMailbox.cs`。
**两套实现共享同一份线协议**（文件名、动词名、回复字符串逐字节一致），所以 `design/_dbg.ps1` 不用改。
启动器的桥只保留启动器专属动词（页面、账户、下载、托盘）。

用法：

1. 带 `--debug` 启动（可选 `--debug-box <名字>` 换邮箱）：

   ```bash
   src\NegiCraftLauncher.App\bin\Debug\net10.0\NegiCraftLauncher.App.exe --debug          # Avalonia 启动器
   src\NegiCraftLauncher.App.Wpf\bin\Debug\net10.0-windows\NegiCraftLauncher.exe --debug  # WPF 启动器
   src\NegiCraftLauncher.Pet.App\bin\Debug\net10.0\NegiPet.exe --debug                    # Avalonia 独立桌宠
   src\NegiCraftLauncher.Pet.App.Wpf\bin\Debug\net10.0-windows\NegiPet.exe --debug        # WPF 独立桌宠
   ```

   两个平台的同名进程**不能同时开**（都叫 `NegiCraftLauncher` / `NegiPet`），
   要并排对照就换邮箱：`--debug --debug-box ncl-pet-ava` / `--debug-box ncl-pet-wpf`。
2. 用客户端发命令：`design/_dbg.ps1 -Cmd "<verb> [arg]" [-Box ncl-pet-debug]`

命令：

- `page <home|instances|download|settings>` — 切主页面
- `tab <游戏|Java|下载|外观|关于>` — 切设置子页
- `pop <acc|inst|dl|bg|none>` — 开/关弹窗
- `threads <n>` — 设下载线程数
- `demo` — 塞 3 行示例下载任务，用来看任务行模板
- `state` — 打印当前页面 / 弹窗 / 线程 / 任务状态
- `shot <png绝对路径>` — 离屏渲染截图（`RenderTargetBitmap` 渲染 `Window.Content`，1:1 DIP，别预缩放，否则布局按错误的可用尺寸重排）
- `quit` — 关窗退出

独立版专属：`pet-close`（只关桌宠窗口，不退出进程，用于区分两条退出路径）、`quit`。

桌宠相关（桌宠窗口是独立顶层窗口，`shot` 抓不到它的 GL 画面）：

- `pet open` / `pet close` — 开/关桌宠
- `pet-state` — 桌宠的**实时**状态：它正在渲染的名字、模式、Topmost、菜单开合、账号列表
- `pet-hwnd` — 只读打印桌宠窗口的 Win32 `style` / `ex-style` / owner / owner 是否可见
- `pet-skinsnap <png绝对路径>` — 抓桌宠的实时帧。**Avalonia 侧必须连发两次**：GL 的
  `SaveSnapshot` 交回的是上一次请求捕获的帧，先发一次“上膛”、等约 100ms、再发一次存盘。
  **WPF 侧发一次就够** —— 软件光栅化是同步确定性的（这正是换后端的红利之一）。
- `pet-menu` / `pet-menu close` — 开/关右键菜单（返回 `IsOpen`）
- `pet-dialog <name|coord|close> [png绝对路径]` — 打开改名对话框 / 坐标选点遮罩 / 关掉两者；
  带路径时顺手把该对话框内容渲染成 PNG（对话框是独立顶层窗口，`shot` 抓不到）
- `pet-walk`、`pet-jump`、`pet-sneak`、`pet-control`、`pet-follow`、`pet-key`、`pet-name`、`pet-mode` … — 驱动动画与交互

典型流程：`--debug` 起进程 → `page settings` → `tab 下载` → `shot ...\x.png` → 读那张图。

## 工程结构

```
src/NegiCraftLauncher.App         启动器（Avalonia，WinExe）
src/NegiCraftLauncher.App.Wpf     启动器（原生 WPF，net10.0-windows，程序集名 NegiCraftLauncher）
src/NegiCraftLauncher.Core        与 UI 无关的核心逻辑
src/NegiCraftLauncher.ViewModels  UI 中立的 VM（零 UI 依赖，两个前端共用）
src/NegiCraftLauncher.Raster      软件光栅化 + 平台中立像素层（PixelBuffer / PngCodec / SkinRenderSoftware）
src/NegiCraftLauncher.Skin        皮肤解码 + OpenGL 渲染栈（Avalonia 侧，App 与 Pet 共用）
src/NegiCraftLauncher.Skin.Wpf    皮肤解码 + 软件光栅预览控件（WPF 侧，App.Wpf 与 Pet.Wpf 共用）
src/NegiCraftLauncher.Pet         桌宠本体 + IPetHost 宿主契约（Avalonia）
src/NegiCraftLauncher.Pet.Core    框架无关的桌宠共用件（IPetHost / PetSettings / 全局键鼠钩子 / Motion 物理内核）
src/NegiCraftLauncher.Pet.Wpf     桌宠本体（WPF，含自己的调试桥与对话框）
src/NegiCraftLauncher.Pet.App     独立桌宠（Avalonia WinExe → NegiPet.exe）
src/NegiCraftLauncher.Pet.App.Wpf 独立桌宠（WPF WinExe → NegiPet.exe）
src/NegiCraftLauncher.Theme       设计 token + 控件主题（Avalonia；App 与 Pet.App 共用）
src/NegiCraftLauncher.Theme.Wpf   设计 token + 控件主题（WPF；无逻辑，谁都能引）
libs/MinecraftSkinRender          vendored 渲染库（Skia 侧）
libs/MinecraftSkinRender.Core     vendored 渲染库的零依赖核心（位姿数学、几何表）
```

依赖方向：`Pet.App → Pet → {Pet.Core, Skin → MinecraftSkinRender}`；`App → {Pet, Skin, Theme, Core}`；
`Pet.Wpf → {Pet.Core, Skin.Wpf, Theme.Wpf}`；`Pet.App.Wpf → {Pet.Wpf, Theme.Wpf}`；
`App.Wpf → {Pet.Wpf, Skin.Wpf, Theme.Wpf, Core, ViewModels, Raster}`。`Core` 不参与 UI。
`App.Wpf` 与 `App` 平行，共用 `Core` / `ViewModels` / `Raster`。
`Raster` **只引用 `MinecraftSkinRender.Core`**（不引用 Skia 侧），否则 `libSkiaSharp.dll` 会被拖回 WPF 侧。
`Theme` 与 `Theme.Wpf` 是**两份独立的 view 层资产**（XAML 无共同编译器），只保证视觉一致，不保证文本一致。

**`Skin.Wpf` 为什么是独立工程**：`SkinPreviewControl` 要被启动器和桌宠共用。
放在 `Pet.Wpf` 里会形成 `App.Wpf → Pet.Wpf → App.Wpf` 的循环引用，所以它必须自己一个程序集。

**`Pet.Core` 装什么**：`IPetHost` / `PetSettings` / 全局键鼠钩子（`Services/`），
以及**桌宠的操控 / 跟随 / 导航物理内核**（`Motion/PetMotion.cs`，671 行 —— 434 行代码 + 113 行注释）。
零 `PackageReference`、不引用任何 UI 类型 —— 命名空间仍是 `NegiCraftLauncher.Pet`。
两端的 `PetWindow` 各留一份 view 装配，物理只有这一份：

- **输入**：`PetMotionContext` 值类型（`Stage` / `WorkArea` / `Window` / `Cursor?` / `CurrentYawDeg`），**全是 DIP**。
  Avalonia 的 `Position` 与 `WorkingArea` 是物理像素，宿主负责除一次 `RenderScaling`（见 `PetWindow.Scaling`）；
  WPF 的 `Left/Top` 与 `SystemParameters.WorkArea` 本来就是 DIP，直接喂。
- **输出**：`GroundX/GroundY`、`JumpOffsetY`、`YawDelta`、`HeadPitch/HeadYaw`、四个动画标志、`HasHeadLook`。
- **朝向只吐增量**（`YawDelta`）而不是绝对值：右键拖拽旋转 / `ResetRotation` / `pet-yaw` 都直接改预览控件的 yaw，
  内核每帧从 context 读当前值、只回增量，两边各记一份一定会漂开。
- **每帧应用顺序定死**（`ApplyMotionToView`）：walking/sprinting/jumping/sneaking → `SetJumpOffset`
  → 应用 `YawDelta` → `SetHeadLookAt` → `MoveWindowTo`。顺序错了会看到抖动。
- 切模式走 `SetMode()`，内部清队列 / 松按键后触发 `ModeChanged` 事件，
  UI 侧的菜单勾选与 `BeginControlMode` / `EnterFreeIdle` 都挂在这个事件上（对应原来的 `UpdateModeUi`）。
- 下蹲是三个来源的并集：`Sneaking = ManualSneakToggle || _keyShiftHeld || _physicalShiftDown`
  （`RecomputeSneak()`）。物理 Shift 走 `PetNativeKeys.IsDown(VK_SHIFT)`，**每帧在 `Tick` 里轮询一次**；
  合成 Shift（`pet-key` / `OnKeyDown`）走 `_keyShiftHeld`，**事件驱动**、立刻重算。
  两条路都要触发重算，少一条就会看到"下蹲要么慢一帧、要么根本不动"。

**Windows 走 WPF、macOS/Linux 走 Avalonia** 是既定方向；view 层各写各的（XAML 无共同编译器），
共享的是 `Core` / `ViewModels` / `Raster`。详见 `docs/wpf-migration-plan.md`（不入库）。

## 验证皮肤渲染（不用开窗口）

WPF 前端的皮肤预览走软件光栅化，可以完全无 UI 出图，用来跟 GL 版逐像素对齐：

```bash
EXE=src/NegiCraftLauncher.App.Wpf/bin/Release/net10.0-windows/NegiCraftLauncher.exe
$EXE --skin <皮肤.png> --out %TEMP%\x.png     # 四视角拼图 + x-front.png 单正面
$EXE --skin <皮肤.png> --angle 180            # 只出某个角度
$EXE --compare a.png --with b.png             # 逐像素对比
```

报告分别写 `%TEMP%\ncl-skin-preview.txt`（含 model/view/proj/head/leftArm 矩阵）与
`%TEMP%\ncl-skin-compare.txt`。

几个要点：

- **验证锚点：四视角图里 180° 必须是纯棕发的后脑勺、不能有脸。** 绕序错了会左右镜像 +
  第二层（外套/帽子）永远被本体挡住，而**单张正面截图看不出来**。
- 对比结果**必须先做平移对齐再看**（`--compare` 已内置 ±24px 搜索）。GL 侧取景由 Avalonia
  控件决定，整体错开十几像素就能让"差异 >8"冲到 25%。SoloFox 对齐后是 `R4.5/G4.7/B10`、>8 占 10%。
- 软件后端**刻意不做光照** —— `AvaloniaApi.ShaderSource` 里的 `Unlit()` 把 GLSL 的
  `ambient + diffuse` 换成了 `vec3(1.0)`，现行 GL 预览本来就是平的。别照抄 `OpenGLShader.cs`。
- 披风用的是**另一张贴图**，`SkinRenderSoftware.BuildLayers` 里显式跳过 `ModelPartType.Cape`。
  不跳的话它会拿皮肤贴图去采样头部区域，在身后糊出一块棕色板子（正面看不出来，180° 露馅）。
- 性能结论一律以 **Release** 为准：Debug 下 Roslyn 不内联，同一份代码慢近一倍。

## 验证 WPF 主题

```bash
src/NegiCraftLauncher.App.Wpf/bin/Debug/net10.0-windows/NegiCraftLauncher.exe --theme
```

把主题画廊起在屏幕外、截完图就关掉（不动光标、不抢焦点）。产物：

- `%TEMP%\ncl-wpf-theme.png` —— 整张画廊（2× 光栅化）
- `%TEMP%\ncl-wpf-theme-<小节>.png` —— 8 个分小节图（1×，一个 DIP 一个像素）
- `%TEMP%\ncl-wpf-theme.txt` —— **逐控件的样式命中检查**（`Template.FindName`）+ 主题资源值

要点：

- **不要只看截图**。样式没命中时 WPF 会悄悄退回系统默认外观，是"有点怪"不是"报错"。
  报告里的 `Root` / `PART_ContentHost` / `Track` / `PART_Track` / `PART_LayoutRoot` 都必须是 `True`。
- 画廊要**分小节出图**：整张 1014×2602 缩略后，4px 的滚动条、6px 的进度条全糊成一根线。
- `RenderTargetBitmap.Render(子元素)` 会带上元素相对父级的偏移 → 小尺寸位图整张空白。
  分小节图走 `VisualBrush` + `DrawingVisual` 在原点重画（`ThemeGallery.RenderAtOrigin`）。

WPF 与 Avalonia 有几处**静默出错**的渲染差异（不报错、只是画得不对），改主题前先看
`docs/wpf-migration-plan.md` §15.2。最容易踩的三个：

- **圆角不会被夹到一半**。Avalonia 写 `999` 表示胶囊；WPF 的 `Border` 不夹，直接画成**椭圆**。
  按实际像素高度写具体值（6px 高写 3、4px 高写 2、16×16 写 8）。
- **`ScrollBar` 的 `MinWidth` 被静态构造覆盖**成系统滚动条宽度（≈17.33），
  光设 `Width="10"` 会被撑回去 —— 必须显式 `MinWidth="0"`。
- **`ProgressBar` 默认 `BorderThickness=1`** 且边框色来自系统主题，要清成 0/Transparent。

## WPF 侧的静默坑（P6 / P7 新增）

都是“不报错、只是行为不对”那一类，踩一次就够了：

- **`WindowInteropHelper.Handle` 不能在非 UI 线程求值。** 调试桥的 `Dispatch` 跑在轮询线程上，
  在里面现取 HWND 会让**每个动词**都回 `ERR 调用线程无法访问此对象`。正确做法是把它提升成
  `readonly IntPtr`，在 `StartIfNeeded`（UI 线程）里一次性取好。
- **对话框高度不要写死。** WPF 的默认字体是 Segoe UI，行高比 Avalonia 的 Inter 大一截；
  Avalonia 版写 `Height = 180` 刚好的四段内容，在 WPF 里量出来约 199px，最下面那排按钮会被裁掉一半。
  改用 `SizeToContent = SizeToContent.Height`。
- **`UIElement.IsVisible` 在 WPF 里是只读的**（Avalonia 那边可写）。恢复显示要 `Show()`，
  不能照抄 `IsVisible = true`。
- **`ContextMenu` 挂在 `Popup` 里是独立可视树**，`RelativeSource AncestorType=Window` 找不到，
  要改走 `PlacementTarget.Tag`。
- **WPF 的窗口坐标全是 DIP**（`Left/Top/Width/Height`、`SystemParameters.WorkArea`、
  `VirtualScreen*`），而全局鼠标钩子给的是**物理像素** —— 只除一次 DPI 即可。
  Avalonia 的 `Window.Position` 是 `PixelPoint`，所以那边到处 `* RenderScaling`，照抄会错一倍。
- **`.ico` 不在 WPF SDK 默认的 `Resource` 通配里**（详见上面“构建 / 运行独立版桌宠”）。

## 验证 WPF 启动器主窗口（`--ui`）

```bash
src/NegiCraftLauncher.App.Wpf/bin/Debug/net10.0-windows/NegiCraftLauncher.exe --ui
```

把**真正的主窗口**摆在屏幕外（`Left/Top=-32000`、`ShowActivated=false`，不抢焦点）逐页出图，截完自退。产物：

- `%TEMP%\ncl-wpf-{home,instances,download,settings}.png` —— 四个主页面，**1180×720**
- `%TEMP%\ncl-wpf-settings-<游戏|Java|下载|外观|关于>.png` —— 五个设置子页
- `%TEMP%\ncl-wpf-pop-{acc,inst,dl}.png` —— 三个弹层
- `%TEMP%\ncl-wpf-ui.txt` —— 页面容器审计 + 带名字元素清单 + **绑定错误**

要点：

- **画布和窗口是解耦的**。屏幕比 1180 窄时窗口管理器会把窗口夹小（1024×768 的会话里被夹到 1044，
  且改 `Window.Width` 压不住），所以截图前对内容根强制 `Measure`/`Arrange` 到 1180×720，
  并在截图前 `await Dispatcher.Yield(DispatcherPriority.Render)` 让渲染管线跑一轮 ——
  **少了这一步第一次截图右边会缺一条**。详见计划 §16.4。
- **默认走直接渲染**（`RenderTargetBitmap.Render(content)`），与 Avalonia 的 `shot` 同一条路，
  出图可直接逐像素叠。传 `--visualbrush` 切回归零重画的老路径。
- 报告里必须看两处：`绑定/渲染警告 0 条`（转换器返回类型不匹配时 WPF 不报错、只静默退回默认值），
  以及四个页面容器审计全是 `OK`（同一时刻只该有一个 `Visible`）。

### 和 Avalonia 版做像素级对照

基准是 `design\_smoke.ps1` 出的 `%TEMP%\ncl-smoke-<page>.png`。对照工具：

```bash
python design/_diff.py "$TEMP/ncl-wpf-home.png" "$TEMP/ncl-smoke-home.png" --shift 1
```

会做 ±N 平移搜索、打印 >8/>24/>48 的差异占比与平均绝对差，并写出 `<prefix>-heat.png`（热力图）
和 `<prefix>-side.png`（左 WPF / 右 Avalonia）。

**期望值**：四页都对齐在 `dx=0 dy=0`，>48 占比在 0.5%–2% 之间（download 页最高，因为字最多）。
残余差异**只应出现在文字字形、图标、皮肤模型**这三类上；背景、面板、卡片描边、行分隔线、
滚动条、窗口圆角应当逐像素相同。要是热力图上出现了**成片的色块**，那是布局问题，不是抗锯齿。

顺带：`--ui` 连跑两次出图应当**几乎 byte-identical**（实测 0–2 个像素差），
所以它可以直接当 P7 的像素回归基线。

## 跑一次 Avalonia 回归（T0-3）

```powershell
design\_smoke.ps1          # 起 Avalonia 版，四个主页面各截一张图到 %TEMP%\ncl-smoke-<page>.png
```

和 `_dbg.ps1` 一样，**必须在同一次 PowerShell 调用里跑完**（沙箱会随会话收掉 GUI 子进程）。

桌宠有两种宿主：启动器进程内托管（`App/Services/PetHostAdapter.cs` 把 VM 适配成 `IPetHost`），以及独立 exe（`Pet.App/StandalonePetHost.cs`）。**独立版没有启动器专属功能**：隐藏“打开启动器”、账号换肤菜单为空。两边名字各存各的（启动器在 `settings.json`，独立版在 `%APPDATA%\NCL\pet.json`）。

## 构建 / 运行独立版桌宠

```bash
# Avalonia 版
dotnet build src/NegiCraftLauncher.Pet.App/NegiCraftLauncher.Pet.App.csproj
src/NegiCraftLauncher.Pet.App/bin/Debug/net10.0/NegiPet.exe          # 可选：NegiPet.exe <名字>

# WPF 版（同一个 exe 名，别同时开）
dotnet build src/NegiCraftLauncher.Pet.App.Wpf/NegiCraftLauncher.Pet.App.Wpf.csproj
src/NegiCraftLauncher.Pet.App.Wpf/bin/Debug/net10.0-windows/NegiPet.exe
```

注意几点：

- `AssemblyName` 是 `NegiPet`，所以**图标等 `avares://` 前缀要用程序集名 `NegiPet`**，不是工程名 `NegiCraftLauncher.Pet.App`。写错会在启动时 `FileNotFoundException` 直接崩。
- 独立版**有自己的调试桥**（`StandaloneDebugBridge.cs`，邮箱 `ncl-pet-debug`），和启动器那套同一个协议、同一批桌宠动词。验证它也可以从进程外做：`Get-Process NegiPet`（`MainWindowHandle` 为 0 是正常的，因为窗口带 `WS_EX_TOOLWINDOW` 且有 owner）。
- 独立版 `ShutdownMode` 是 `OnLastWindowClose`：桌宠是唯一窗口，关掉它就该退出进程。别改回 `OnExplicitShutdown`，否则关窗后进程还活着（没窗口、没托盘，只能去任务管理器杀）。
- 注意沙箱：从 Bash 的 `run_in_background` 或 PowerShell 的 `Start-Process` 起 GUI 进程后，**一旦发起命令的 shell 会话结束，子进程会被一起收掉**（job object）。所以起进程和发调试命令必须在**同一次** PowerShell 调用里做完，不能分两次。
- **WPF 版的 `.ico` 必须显式写进 csproj**：WPF SDK 的默认 `Resource` 通配只收
  `bmp/jpg/jpeg/png/tif/tiff/gif/wdp/jpc/jfif`，**不含 `.ico`**。不写 `<Resource Include="Assets\NegiPet.ico" />`
  的话，`Window.Icon` 那句 `pack://` 取图会在启动时抛 `FileNotFoundException` —— 进程还没上屏就死，
  而且**增量构建不会重生成 `.g.resources`**，改完要 `rm -rf obj bin` 全量重建才生效。
  （`ApplicationIcon` 走的是 Win32 资源那套，不受影响，所以 exe 图标看着是好的，更容易误判。）

## 验证 WPF 桌宠

桌宠窗口是独立顶层窗口，启动器的 `shot` 抓不到它；WPF 侧的对话窗（改名 / 坐标选点）又是另外两个顶层窗口。
两端都用同一批 `pet-*` 动词驱动：

```bash
# 起 WPF 独立桌宠（换邮箱，方便与 Avalonia 版并排）
src\NegiCraftLauncher.Pet.App.Wpf\bin\Debug\net10.0-windows\NegiPet.exe --debug --debug-box ncl-pet-wpf
design\_dbg.ps1 -Cmd "pet-state" -Box ncl-pet-wpf
design\_dbg.ps1 -Cmd "pet-skinsnap $env:TEMP\w.png" -Box ncl-pet-wpf   # WPF 侧发一次即可
design\_dbg.ps1 -Cmd "pet-dialog name $env:TEMP\w-name.png" -Box ncl-pet-wpf
design\_dbg.ps1 -Cmd "pet-dialog close" -Box ncl-pet-wpf
```

进程内托管那条路走启动器的邮箱：WPF 启动器 `--debug` 起来后 `pet open` → `pet-state`
（`accounts=[...]` 非空即说明 `App.Wpf/Services/PetHostAdapter.cs` 接上了）→ `pet close`。

验收锚点：

- `pet-hwnd` 必须回 `toolwindow=True noactivate=True appwindow=False layered=True topmost=True`。
- `pet-state` 里 `skintype=Old top=False`（64x32 老皮肤不能开第二层覆盖贴图）。
- 关掉桌宠后 `pet-state` 回 `ERR no pet window`，且进程**没有**残留（独立版靠 `OnLastWindowClose` 退）。

## 跨平台像素回归（T4 / T5）

```powershell
design\_p7regress.ps1                 # 主窗口 4 页 + 桌宠 1 帧
design\_p7regress.ps1 -Repeat 2       # 顺带验 WPF 探针的确定性
design\_p7regress.ps1 -Only pet       # 只跑桌宠段
```

**为什么用“跨平台对照”而不是提交一份参考图集**：Avalonia 版是**同一套逻辑的既有实现**，
它的截图就是活基准。每次跑都把两边的图重新生成再互比 —— 参考图永远不会过期，
也不用往仓库里塞二进制。两个平台共用 `Core` / `Raster` / `ViewModels`，任何一边改了布局或渲染这个脚本都会红。

段与判据：

| 段 | 做法 | 判据 |
| --- | --- | --- |
| 确定性 | WPF 探针连跑 N 遍，第 2..N 遍与第 1 遍比 | `>48 ≤ 0.05%` |
| 主窗口 | `ncl-wpf-<page>.png` vs `ncl-smoke-<page>.png` | 平移对齐后 `dx=dy=0` 且 `>48 ≤ 3%` |
| 桌宠 | `pet-skinsnap` 各抓一帧互比 | 平移对齐后 `dx=dy=0` 且 `>48 ≤ 4%` |

踩过的坑（写脚本时都修了）：

- **判据不能用文件哈希**。首页的 3D 预览有随时间推进的待机动画，两遍必然差 1–3 个像素。
- **`_diff.py` 的中文输出经 PowerShell 管道回来会被控制台编码拆掉**，正则匹配不上。
  所以 `_diff.py` 另出一行纯 ASCII 的 `SUMMARY dx=.. dy=.. gt8=.. gt24=.. gt48=.. mean=..`，
  脚本只认这一行（加 `--no-images` 省掉热力图/并排图）。
- **别用固定 `sleep` 等 `--ui` 探针**：睡短了会把上一轮留在 `%TEMP%` 的旧图当成本轮结果。
  用 `$proc.WaitForExit(90000)`，超时才强杀。
- **`Remove-Item` 在不存在的路径上会 fail-closed 抛异常**（沙箱包装器，`-ErrorAction SilentlyContinue` 也挡不住），
  先 `Test-Path`。
- **沙箱的 `Remove-Item` 包装器不接受管道输入**。`$files | Remove-Item -Force` 会抛
  `ParameterBindingException`「输入对象无法绑定到该命令的任何参数」——
  看着像语法错，其实是包装器签名问题。一律
  `foreach ($f in $files) { Remove-Item -LiteralPath $f.FullName -Force }`。
- **PowerShell 里 `"$name:"` 会被当成作用域限定符**报“变量引用无效”，要写 `"${name}:"`。
- 桌宠段要把两边动画相位钉死再抓帧：`pet-mouse 800 100` + `pet-yaw 0` + `pet-walk off`，
  否则差异里混进的是时间而不是代码。
- **整段必须一次跑完**，且跑之前先确认没有别的实例占着同名 exe（`taskkill //F //IM NegiPet.exe`）。

## 桌宠窗口为什么不是“普通窗口”

`ShowInTaskbar=false` 只去掉任务栏按钮（Avalonia 的做法是挂到隐藏离屏父窗口 + 清 `WS_EX_APPWINDOW`），窗口仍会进 Alt+Tab / 任务视图、点它仍抢前台。修复靠 `Pet/Services/PetShellStyle.cs`（Avalonia）与 `Pet.Wpf/Services/PetShellStyle.cs`（WPF）：注入 `WS_EX_TOOLWINDOW(0x80) | WS_EX_NOACTIVATE(0x08000000)`。返回值是**整体替换** style/ex-style（不是 OR 合并），所以必须把传进来的值原样带上。Avalonia 侧用 `Win32Properties.AddWindowStylesCallback`，WPF 侧在 `SourceInitialized` 里直接 `SetWindowLong`。详见 `docs/pet-window-shell-visibility.md`。

由此带来两个后果：桌宠宿主**永远不是活动窗口**，所以桌宠里的对话框（改名 / 坐标选点）
**不能用 `ShowDialog`**（模态循环会等一个永远不会来的激活），只能 `Show()` + `TaskCompletionSource`；
挂 `Owner` 也要先判断宿主是否真的可激活（`owner.ShowActivated || owner.IsActive`）。

## 皮肤格式：64x32 老皮肤必须走 `SkinType.Old`

皮肤有**两种 UV 布局**，跟"宽/细"是两码事：

- **64x64**（1.8+）：`New`（Steve 宽臂）/ `NewSlim`（Alex 细臂）
- **64x32**（1.7）：`Old` —— V 坐标按 32 而不是 64 归一化，手臂/腿的 UV 偏移完全不同，而且**没有第二层覆盖贴图**

`libs/MinecraftSkinRender` 三种都实现了。踩过的坑：`SkinRenderControl.LoadFromUsername` 曾经只看 `IsSlim` 就填 `New`/`NewSlim`，于是把 64x32 皮肤喂给了 1.8 模型 —— 表现是**贴图整体错位、缺胳膊少腿**，还会把老皮肤里根本没定义的覆盖层画成乱七八糟的三角面。

现在统一走 `SkinService.ResolveSkinType(bytes, isSlim)`：先看贴图尺寸，64x32 一律 `Old`，只有 64x64 才用 `isSlim` 区分 New/NewSlim。`IsSlim` 对老皮肤没有意义（`.model` 里存的值也一样），别拿它决定格式。

诊断用：`state` 会打印 `skintype=` 和 `top=`（`top=False` 说明关掉了第二层，老皮肤应当如此）。

**`Old` 必须同时关掉第二层覆盖贴图**。GL 侧在 `SkinRenderControl.ApplySkinType` 里写的是
`_skin.EnableTop = type != SkinType.Old`；软件光栅后端（`SkinRenderSoftware.SetSkin`）一开始漏了这条。
不照做的后果很隐蔽：lib 的 `GetSteveTop(Old)` 会返回一个**放大的头**（enlarge 1.125），
而 `GetSteveTextureTop(Old)` 的头 UV 落在 (32..64, 0..16) —— 那块在老皮肤布局里根本没定义。
那张 PNG 在那一带只要不是全透明，头顶就会多一个采错纹理的大方块；全透明时完全看不出来。
所以 `top=` 这个诊断字段必须一起看，光看图会被骗。

另外：渲染器只吃**宽度 64** 的贴图（`SetSkinTex` 对 `width != 64` 直接抛异常）。128x128 以上的高清皮肤要先经 `SkinRenderControl.NormalizeSkinBitmap` 缩到 64 宽（最近邻；2:1 的缩成 64x32 保持 `Old`），否则会被静默丢掉。

## 构建前

`design/_dbg.ps1` 起的实例不会自动退出；`dotnet build` 前先 `taskkill //F //IM NegiCraftLauncher.App.exe`，否则 exe 被占用报 MSB3027。

跑独立版时同理，先 `taskkill //F //IM NegiPet.exe`。
