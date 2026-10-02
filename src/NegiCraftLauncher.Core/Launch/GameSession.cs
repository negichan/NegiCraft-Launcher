using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NegiCraftLauncher.Core.Launch;

/// <summary>
/// A running game process: streams its output to a log file, keeps the last lines for crash
/// diagnosis, and reports the exit code.
/// </summary>
public sealed class GameSession : IDisposable
{
    private const int TailLength = 400;

    private readonly Process _process;
    private readonly StreamWriter? _logWriter;
    private readonly Queue<string> _tail = new();
    private readonly object _sync = new();
    private readonly TaskCompletionSource<int> _exitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string InstanceId { get; }
    public string LogFilePath { get; }
    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;
    public DateTime StartedAt { get; } = DateTime.Now;

    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;

    internal GameSession(Process process, string instanceId, string logFilePath)
    {
        _process = process;
        InstanceId = instanceId;
        LogFilePath = logFilePath;

        try
        {
            NclPaths.EnsureDirectory(Path.GetDirectoryName(logFilePath)!);
            _logWriter = new StreamWriter(new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // A read-only log directory must not stop the game from starting.
            _logWriter = null;
        }

        _process.OutputDataReceived += OnData;
        _process.ErrorDataReceived += OnData;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;
    }

    internal void BeginReading()
    {
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void OnData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;

        lock (_sync)
        {
            _tail.Enqueue(e.Data);
            while (_tail.Count > TailLength) _tail.Dequeue();
        }

        try
        {
            _logWriter?.WriteLine(e.Data);
            _logWriter?.Flush();
        }
        catch (Exception)
        {
        }

        OutputReceived?.Invoke(e.Data);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int code;
        try
        {
            code = _process.ExitCode;
        }
        catch (Exception)
        {
            code = -1;
        }

        try
        {
            _logWriter?.Flush();
            _logWriter?.Dispose();
        }
        catch (Exception)
        {
        }

        _exitSource.TrySetResult(code);
        Exited?.Invoke(code);
    }

    public IReadOnlyList<string> Tail
    {
        get
        {
            lock (_sync) return _tail.ToArray();
        }
    }

    /// <summary>A short, actionable hint derived from the captured output; null when nothing looks wrong.</summary>
    public string? Diagnose()
    {
        var text = string.Join('\n', Tail);
        if (text.Length == 0) return null;

        if (text.Contains("Could not find or load main class"))
            return "游戏核心不完整：找不到主类，请删除该实例的版本文件夹后重新下载。";
        if (text.Contains("UnsupportedClassVersionError"))
            return "Java 版本过低：该实例需要更新的 Java 运行时。";
        if (text.Contains("java.lang.OutOfMemoryError"))
            return "内存不足：请在设置中调高分配给游戏的内存。";
        if (text.Contains("Pixel format not accelerated"))
            return "无法创建 OpenGL 上下文：通常是显卡驱动过旧或远程桌面环境导致，请更新显卡驱动。";
        if (text.Contains("UnsatisfiedLinkError") || text.Contains("no lwjgl"))
            return "原生库加载失败：请删除实例的 natives 目录后重试。";
        if (text.Contains("java.lang.NoClassDefFoundError"))
            return "缺少依赖库文件：请在下载页重新补全该实例的库文件。";

        var exceptionLine = Tail.LastOrDefault(l => l.Contains("Exception in thread"));
        return exceptionLine is null ? null : exceptionLine.Trim();
    }

    public Task<int> WaitForExitAsync(CancellationToken ct = default)
    {
        if (ct.CanBeCanceled)
        {
            ct.Register(() => _exitSource.TrySetCanceled(ct));
        }

        return _exitSource.Task;
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _process.OutputDataReceived -= OnData;
        _process.ErrorDataReceived -= OnData;
        _process.Exited -= OnProcessExited;
        _process.Dispose();
        _logWriter?.Dispose();
    }
}
