# AGENTS.md

## 验证 / 测试 UI：用进程内调试桥，不要模拟鼠标

需要看界面、切页面、验证布局或状态时，**一律走应用自带的调试桥**，不要用 `design/_shot.ps1` 那种移动光标 / 发送合成点击 / 抢焦点的脚本——那会干扰用户正在做的事（比如全屏游戏）。

调试桥只在 `--debug` 启动时开启，通过 `%TEMP%` 下的文件邮箱收发命令，全程不动光标、不抢焦点、不改窗口层级，被全屏应用盖着也能用。两个进程各有各的邮箱，可同时开：

- 启动器 → `src/NegiCraftLauncher.App/Services/DebugBridge.cs`，邮箱 `ncl-debug`
- 独立桌宠 → `src/NegiCraftLauncher.Pet.App/StandaloneDebugBridge.cs`，邮箱 `ncl-pet-debug`

桌宠那批动词（`pet-*` / `shot-pet`）两边共用，实现在 `src/NegiCraftLauncher.Pet/Debug/PetDebugCommands.cs`；邮箱收发在 `PetDebugMailbox.cs`。启动器的桥只保留启动器专属动词（页面、账户、下载、托盘）。

用法：

1. 带 `--debug` 启动（可选 `--debug-box <名字>` 换邮箱）：
   `src\NegiCraftLauncher.App\bin\Debug\net10.0\NegiCraftLauncher.App.exe --debug`
   `src\NegiCraftLauncher.Pet.App\bin\Debug\net10.0\NegiPet.exe --debug`
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
- `pet-skinsnap <png绝对路径>` — 抓桌宠的实时 GL 帧。**必须连发两次**：`SaveSnapshot` 交回的是上一次请求捕获的帧，所以先发一次“上膛”，等约 100ms，再发一次存盘
- `pet-menu` / `pet-menu close` — 开/关右键菜单（返回 `IsOpen`）
- `pet-walk`、`pet-jump`、`pet-sneak`、`pet-control`、`pet-follow`、`pet-key`、`pet-name`、`pet-mode` … — 驱动动画与交互

典型流程：`--debug` 起进程 → `page settings` → `tab 下载` → `shot ...\x.png` → 读那张图。

## 工程结构

```
src/NegiCraftLauncher.App         启动器（Avalonia，WinExe）
src/NegiCraftLauncher.App.Wpf     启动器（原生 WPF，net10.0-windows，程序集名 NegiCraftLauncher）
src/NegiCraftLauncher.Core        与 UI 无关的核心逻辑
src/NegiCraftLauncher.ViewModels  UI 中立的 VM（零 UI 依赖，两个前端共用）
src/NegiCraftLauncher.Raster      软件光栅化 + 平台中立像素层（PixelBuffer / PngCodec / SkinRenderSoftware）
src/NegiCraftLauncher.Skin        皮肤解码 + OpenGL 渲染栈（App 与 Pet 共用）
src/NegiCraftLauncher.Pet         桌宠本体 + IPetHost 宿主契约
src/NegiCraftLauncher.Pet.App     独立桌宠（WinExe → NegiPet.exe）
src/NegiCraftLauncher.Theme       设计 token + 控件主题（Avalonia；App 与 Pet.App 共用）
src/NegiCraftLauncher.Theme.Wpf   设计 token + 控件主题（WPF；无逻辑，谁都能引）
libs/MinecraftSkinRender          vendored 渲染库（Skia 侧）
libs/MinecraftSkinRender.Core     vendored 渲染库的零依赖核心（位姿数学、几何表）
```

依赖方向：`Pet.App → Pet → Skin → MinecraftSkinRender`；`App → Pet`、`App → Skin`、`App → Theme`、
`Pet.App → Theme`。`Core` 不参与 UI。`App.Wpf` 与 `App` 平行，共用 `Core` / `ViewModels` / `Raster`。
`Raster` **只引用 `MinecraftSkinRender.Core`**（不引用 Skia 侧），否则 `libSkiaSharp.dll` 会被拖回 WPF 侧。
`Theme` 与 `Theme.Wpf` 是**两份独立的 view 层资产**（XAML 无共同编译器），只保证视觉一致，不保证文本一致。

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

## 跑一次 Avalonia 回归（T0-3）

```powershell
design\_smoke.ps1          # 起 Avalonia 版，四个主页面各截一张图到 %TEMP%\ncl-smoke-<page>.png
```

和 `_dbg.ps1` 一样，**必须在同一次 PowerShell 调用里跑完**（沙箱会随会话收掉 GUI 子进程）。

桌宠有两种宿主：启动器进程内托管（`App/Services/PetHostAdapter.cs` 把 VM 适配成 `IPetHost`），以及独立 exe（`Pet.App/StandalonePetHost.cs`）。**独立版没有启动器专属功能**：隐藏“打开启动器”、账号换肤菜单为空。两边名字各存各的（启动器在 `settings.json`，独立版在 `%APPDATA%\NCL\pet.json`）。

## 构建 / 运行独立版桌宠

```bash
dotnet build src/NegiCraftLauncher.Pet.App/NegiCraftLauncher.Pet.App.csproj
src/NegiCraftLauncher.Pet.App/bin/Debug/net10.0/NegiPet.exe          # 可选：NegiPet.exe <名字>
```

注意两点：

- `AssemblyName` 是 `NegiPet`，所以**图标等 `avares://` 前缀要用程序集名 `NegiPet`**，不是工程名 `NegiCraftLauncher.Pet.App`。写错会在启动时 `FileNotFoundException` 直接崩。
- 独立版**有自己的调试桥**（`StandaloneDebugBridge.cs`，邮箱 `ncl-pet-debug`），和启动器那套同一个协议、同一批桌宠动词。验证它也可以从进程外做：`Get-Process NegiPet`（`MainWindowHandle` 为 0 是正常的，因为窗口带 `WS_EX_TOOLWINDOW` 且有 owner）。
- 独立版 `ShutdownMode` 是 `OnLastWindowClose`：桌宠是唯一窗口，关掉它就该退出进程。别改回 `OnExplicitShutdown`，否则关窗后进程还活着（没窗口、没托盘，只能去任务管理器杀）。
- 注意沙箱：从 Bash 的 `run_in_background` 或 PowerShell 的 `Start-Process` 起 GUI 进程后，**一旦发起命令的 shell 会话结束，子进程会被一起收掉**（job object）。所以起进程和发调试命令必须在**同一次** PowerShell 调用里做完，不能分两次。

## 桌宠窗口为什么不是“普通窗口”

`ShowInTaskbar=false` 只去掉任务栏按钮（Avalonia 的做法是挂到隐藏离屏父窗口 + 清 `WS_EX_APPWINDOW`），窗口仍会进 Alt+Tab / 任务视图、点它仍抢前台。修复靠 `Services/PetShellStyle.cs`：用 `Win32Properties.AddWindowStylesCallback` 注入 `WS_EX_TOOLWINDOW(0x80) | WS_EX_NOACTIVATE(0x08000000)`。回调返回值是**整体替换** style/ex-style（不是 OR 合并），所以必须把传进来的值原样带上。详见 `docs/pet-window-shell-visibility.md`。

## 皮肤格式：64x32 老皮肤必须走 `SkinType.Old`

皮肤有**两种 UV 布局**，跟"宽/细"是两码事：

- **64x64**（1.8+）：`New`（Steve 宽臂）/ `NewSlim`（Alex 细臂）
- **64x32**（1.7）：`Old` —— V 坐标按 32 而不是 64 归一化，手臂/腿的 UV 偏移完全不同，而且**没有第二层覆盖贴图**

`libs/MinecraftSkinRender` 三种都实现了。踩过的坑：`SkinRenderControl.LoadFromUsername` 曾经只看 `IsSlim` 就填 `New`/`NewSlim`，于是把 64x32 皮肤喂给了 1.8 模型 —— 表现是**贴图整体错位、缺胳膊少腿**，还会把老皮肤里根本没定义的覆盖层画成乱七八糟的三角面。

现在统一走 `SkinService.ResolveSkinType(bytes, isSlim)`：先看贴图尺寸，64x32 一律 `Old`，只有 64x64 才用 `isSlim` 区分 New/NewSlim。`IsSlim` 对老皮肤没有意义（`.model` 里存的值也一样），别拿它决定格式。

诊断用：`state` 会打印 `skintype=` 和 `top=`（`top=False` 说明关掉了第二层，老皮肤应当如此）。

另外：渲染器只吃**宽度 64** 的贴图（`SetSkinTex` 对 `width != 64` 直接抛异常）。128x128 以上的高清皮肤要先经 `SkinRenderControl.NormalizeSkinBitmap` 缩到 64 宽（最近邻；2:1 的缩成 64x32 保持 `Old`），否则会被静默丢掉。

## 构建前

`design/_dbg.ps1` 起的实例不会自动退出；`dotnet build` 前先 `taskkill //F //IM NegiCraftLauncher.App.exe`，否则 exe 被占用报 MSB3027。

跑独立版时同理，先 `taskkill //F //IM NegiPet.exe`。
