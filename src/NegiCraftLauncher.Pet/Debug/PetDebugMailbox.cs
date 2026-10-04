using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NegiCraftLauncher.Pet.Debug;

/// <summary>
/// 两个调试桥共用的**文件邮箱**传输层。
///
/// <para><b>本文件是 <c>Pet/Debug/PetDebugMailbox.cs</c> 的 WPF 移植版。</b>
/// 邮箱协议（<c>cmd.txt</c> / <c>reply.txt</c>、<c>--debug</c> / <c>--debug-box</c> 开关、
/// 回复写完再执行 <c>ExitAfterReply</c>）两边**逐字节一致** ——
/// 改这里必须同时改 Avalonia 那份，否则 <c>design/_dbg.ps1</c> 会在两个进程上表现不一致。</para>
///
/// <para>用文件邮箱而不是命名管道：没有常驻句柄要管，也不怕别的东西抢焦点或盖住界面。
/// 客户端写 <c>cmd.txt</c>，这个循环读到就删掉、执行动词、把结果落到 <c>reply.txt</c>。</para>
///
/// <para>每个进程在 <c>%TEMP%</c> 下有自己的邮箱目录（启动器 <c>ncl-debug</c>、
/// 独立桌宠 <c>ncl-pet-debug</c>），所以两边可以同时调试。要第三个盒子就传
/// <c>--debug-box &lt;名字&gt;</c>。</para>
///
/// <para><b>与 Avalonia 版的唯一差别</b>：<c>Avalonia.Threading.Dispatcher.UIThread</c>
/// → <see cref="System.Windows.Threading.Dispatcher"/>。WPF 没有全局静态的 UI 调度器，
/// 所以从 <see cref="Application.Current"/> 取；桥总是在 UI 线程上构造的，
/// 退路 <see cref="Dispatcher.CurrentDispatcher"/> 也落在同一个线程上。</para>
/// </summary>
public sealed class PetDebugMailbox
{
    private const string DebugFlag = "--debug";
    private const string BoxFlag = "--debug-box";

    private readonly string _cmdPath;
    private readonly string _replyPath;
    private readonly string _threadName;
    private readonly Func<string, Task<string>> _dispatch;

    private Action? _afterReply;

    /// <param name="boxName"><c>%TEMP%</c> 下承载本进程邮箱的目录名。</param>
    /// <param name="threadName">后台轮询线程的名字（调试器里能看到）。</param>
    /// <param name="dispatch">把一行命令变成一行回复。</param>
    public PetDebugMailbox(string boxName, string threadName, Func<string, Task<string>> dispatch)
    {
        var dir = Path.Combine(Path.GetTempPath(), boxName);
        Directory.CreateDirectory(dir);

        _cmdPath = Path.Combine(dir, "cmd.txt");
        _replyPath = Path.Combine(dir, "reply.txt");
        _threadName = threadName;
        _dispatch = dispatch;
    }

    /// <summary>本进程是否带 <c>--debug</c> 启动。否则什么都不暴露。</summary>
    public static bool IsEnabled => Array.IndexOf(Environment.GetCommandLineArgs(), DebugFlag) >= 0;

    /// <summary>
    /// 读 <c>--debug-box &lt;name&gt;</c>，让同一台机器上两个可调试进程各占一个邮箱；
    /// 开关缺失或格式不对时回退到 <paramref name="fallback"/>。
    /// </summary>
    public static string ResolveBoxName(string fallback)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, BoxFlag);
        return i >= 0 && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1])
            ? args[i + 1]
            : fallback;
    }

    /// <summary>启动后台轮询循环并返回它的线程。</summary>
    public Thread Start()
    {
        var loop = new Thread(Run) { IsBackground = true, Name = _threadName };
        loop.Start();
        return loop;
    }

    /// <summary>
    /// 让邮箱在当前这条回复落盘之后、马上在 UI 线程上跑 <paramref name="action"/>。
    /// 要拆掉进程的动词走这条路：先关掉的话进程会在回复写到一半时被杀掉，
    /// 客户端只能一直等到超时。
    /// </summary>
    public void ExitAfterReply(Action action) => _afterReply = () => UiDispatcher().BeginInvoke(action);

    private void Run()
    {
        while (true)
        {
            try
            {
                if (!File.Exists(_cmdPath))
                {
                    Thread.Sleep(80);
                    continue;
                }

                var line = File.ReadAllText(_cmdPath);
                File.Delete(_cmdPath);

                var reply = _dispatch(line).GetAwaiter().GetResult();
                var tmp = _replyPath + ".tmp";
                File.WriteAllText(tmp, reply);
                File.Move(tmp, _replyPath, overwrite: true);

                // 到这一步回复才算安全落盘，动词要求的关闭动作现在可以跑了。
                var afterReply = _afterReply;
                _afterReply = null;
                afterReply?.Invoke();
            }
            catch (Exception)
            {
                Thread.Sleep(80);   // 例如客户端还在写文件；下一拍重试
            }
        }
    }

    /// <summary>UI 线程的调度器。桥是在 UI 线程上建的，所以这两个来源都指向它。</summary>
    private static Dispatcher UiDispatcher() =>
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>在 UI 线程上跑 <paramref name="action"/> 并等它完成。</summary>
    public static Task Ui(Action action) => UiDispatcher().InvokeAsync(action).Task;

    /// <summary>在 UI 线程上跑 <paramref name="func"/> 并等它的返回值。</summary>
    public static Task<T> Ui<T>(Func<T> func) => UiDispatcher().InvokeAsync(func).Task;
}
