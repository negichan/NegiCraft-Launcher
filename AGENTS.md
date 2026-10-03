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

典型流程：`--debug` 起进程 → `page settings` → `tab 下载` → `shot ...\x.png` → 读那张图。

## 构建前

`design/_dbg.ps1` 起的实例不会自动退出；`dotnet build` 前先 `taskkill //F //IM NegiCraftLauncher.App.exe`，否则 exe 被占用报 MSB3027。
