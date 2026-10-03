# AGENTS.md

## 验证 / 测试 UI：用进程内调试桥，不要模拟鼠标

需要看界面、切页面、验证布局或状态时，**一律走应用自带的调试桥**，不要用 `design/_shot.ps1` 那种移动光标 / 发送合成点击 / 抢焦点的脚本——那会干扰用户正在做的事（比如全屏游戏）。

调试桥：`src/NegiCraftLauncher.App/Services/DebugBridge.cs`，只在 `--debug` 启动时开启，通过 `%TEMP%\ncl-debug` 文件邮箱收发命令，全程不动光标、不抢焦点、不改窗口层级，被全屏应用盖着也能用。

用法：

1. 带 `--debug` 启动：
   `src\NegiCraftLauncher.App\bin\Debug\net10.0\NegiCraftLauncher.App.exe --debug`
2. 用客户端发命令：`design/_dbg.ps1 -Cmd "<verb> [arg]"`

命令：

- `page <home|instances|download|settings>` — 切主页面
- `tab <游戏|Java|下载|外观|关于>` — 切设置子页
- `pop <acc|inst|dl|bg|none>` — 开/关弹窗
- `threads <n>` — 设下载线程数
- `demo` — 塞 3 行示例下载任务，用来看任务行模板
- `state` — 打印当前页面 / 弹窗 / 线程 / 任务状态
- `shot <png绝对路径>` — 离屏渲染截图（`RenderTargetBitmap` 渲染 `Window.Content`，1:1 DIP，别预缩放，否则布局按错误的可用尺寸重排）
- `quit` — 关窗退出

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
src/NegiCraftLauncher.App       启动器（WinExe）
src/NegiCraftLauncher.Core      与 UI 无关的核心逻辑
src/NegiCraftLauncher.Skin      皮肤解码 + OpenGL 渲染栈（App 与 Pet 共用）
src/NegiCraftLauncher.Pet       桌宠本体 + IPetHost 宿主契约
src/NegiCraftLauncher.Pet.App   独立桌宠（WinExe → NegiPet.exe）
src/NegiCraftLauncher.Theme     设计 token + 控件主题（App 与 Pet.App 共用）
libs/MinecraftSkinRender        vendored 渲染库
```

依赖方向：`Pet.App → Pet → Skin → MinecraftSkinRender`；`App → Pet`、`App → Skin`、`App → Theme`、`Pet.App → Theme`。`Core` 不参与 UI。

桌宠有两种宿主：启动器进程内托管（`App/Services/PetHostAdapter.cs` 把 VM 适配成 `IPetHost`），以及独立 exe（`Pet.App/StandalonePetHost.cs`）。**独立版没有启动器专属功能**：隐藏“打开启动器”、账号换肤菜单为空。两边名字各存各的（启动器在 `settings.json`，独立版在 `%APPDATA%\NCL\pet.json`）。

## 构建 / 运行独立版桌宠

```bash
dotnet build src/NegiCraftLauncher.Pet.App/NegiCraftLauncher.Pet.App.csproj
src/NegiCraftLauncher.Pet.App/bin/Debug/net10.0/NegiPet.exe          # 可选：NegiPet.exe <名字>
```

注意两点：

- `AssemblyName` 是 `NegiPet`，所以**图标等 `avares://` 前缀要用程序集名 `NegiPet`**，不是工程名 `NegiCraftLauncher.Pet.App`。写错会在启动时 `FileNotFoundException` 直接崩。
- 独立版**没有调试桥**（桥在 `App` 工程里、依赖启动器的 VM）。验证它只能从进程外做：`Get-Process NegiPet`（`MainWindowHandle` 为 0 是正常的，因为窗口带 `WS_EX_TOOLWINDOW` 且有 owner），或另写一个小的 P/Invoke 探针读 `GetWindowLongW`。

## 桌宠窗口为什么不是“普通窗口”

`ShowInTaskbar=false` 只去掉任务栏按钮（Avalonia 的做法是挂到隐藏离屏父窗口 + 清 `WS_EX_APPWINDOW`），窗口仍会进 Alt+Tab / 任务视图、点它仍抢前台。修复靠 `Services/PetShellStyle.cs`：用 `Win32Properties.AddWindowStylesCallback` 注入 `WS_EX_TOOLWINDOW(0x80) | WS_EX_NOACTIVATE(0x08000000)`。回调返回值是**整体替换** style/ex-style（不是 OR 合并），所以必须把传进来的值原样带上。详见 `docs/pet-window-shell-visibility.md`。

## 构建前

`design/_dbg.ps1` 起的实例不会自动退出；`dotnet build` 前先 `taskkill //F //IM NegiCraftLauncher.App.exe`，否则 exe 被占用报 MSB3027。

跑独立版时同理，先 `taskkill //F //IM NegiPet.exe`。
