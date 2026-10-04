using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace NegiCraftLauncher.Pet.Avalonia.Debug;

/// <summary>
/// The file-mailbox transport both debug bridges talk over.
///
/// A file mailbox instead of a named pipe: no persistent handles to leak or lose, and it keeps
/// working no matter what else on the machine intercepts or covers the UI. The client writes
/// <c>cmd.txt</c>, this loop reads-and-deletes it, runs the verb, and drops the answer in
/// <c>reply.txt</c>.
///
/// Every process gets its own mailbox directory under <c>%TEMP%</c> (the launcher uses
/// <c>ncl-debug</c>, the standalone pet <c>ncl-pet-debug</c>), so both can be debugged at the same
/// time without racing over one command file. A caller that wants a third box passes
/// <c>--debug-box &lt;name&gt;</c>.
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

    /// <param name="boxName">Directory name under <c>%TEMP%</c> holding this process's mailbox.</param>
    /// <param name="threadName">Name for the background poll thread (shows up in a debugger).</param>
    /// <param name="dispatch">Turns one command line into one reply line.</param>
    public PetDebugMailbox(string boxName, string threadName, Func<string, Task<string>> dispatch)
    {
        var dir = Path.Combine(Path.GetTempPath(), boxName);
        Directory.CreateDirectory(dir);

        _cmdPath = Path.Combine(dir, "cmd.txt");
        _replyPath = Path.Combine(dir, "reply.txt");
        _threadName = threadName;
        _dispatch = dispatch;
    }

    /// <summary>True when this process was launched with <c>--debug</c>. Nothing is exposed otherwise.</summary>
    public static bool IsEnabled => Array.IndexOf(Environment.GetCommandLineArgs(), DebugFlag) >= 0;

    /// <summary>
    /// Reads <c>--debug-box &lt;name&gt;</c> so two debuggable processes on one machine can each own a
    /// mailbox, falling back to <paramref name="fallback"/> when the switch is absent or malformed.
    /// </summary>
    public static string ResolveBoxName(string fallback)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, BoxFlag);
        return i >= 0 && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1])
            ? args[i + 1]
            : fallback;
    }

    /// <summary>Starts the background poll loop and returns its thread.</summary>
    public Thread Start()
    {
        var loop = new Thread(Run) { IsBackground = true, Name = _threadName };
        loop.Start();
        return loop;
    }

    /// <summary>
    /// Asks the mailbox to run <paramref name="action"/> on the UI thread right after the current
    /// reply has been written to disk. Verbs that tear the process down use this: shutting down
    /// first would kill the process mid-reply and leave the client waiting until it times out.
    /// </summary>
    public void ExitAfterReply(Action action) => _afterReply = () => Dispatcher.UIThread.Post(action);

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

                // Only now is the answer safely on disk, so a shutdown requested by the verb can run.
                var afterReply = _afterReply;
                _afterReply = null;
                afterReply?.Invoke();
            }
            catch (Exception)
            {
                Thread.Sleep(80);   // e.g. client still mid-write; retry next tick
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and awaits completion.</summary>
    public static Task Ui(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    /// <summary>Runs <paramref name="func"/> on the UI thread and awaits its result.</summary>
    public static Task<T> Ui<T>(Func<T> func) => Dispatcher.UIThread.InvokeAsync(func).GetTask();
}
