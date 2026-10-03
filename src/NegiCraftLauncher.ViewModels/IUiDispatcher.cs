namespace NegiCraftLauncher.ViewModels;

/// <summary>
/// UI 线程调度抽象。VM 里所有「从后台线程回到 UI 线程」的地方都走它，
/// 这样共享层既不需要 Avalonia 的 <c>Dispatcher.UIThread</c>，也不需要 WPF 的 <c>Dispatcher</c>。
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);

    Task InvokeAsync(Action action);
}

/// <summary>
/// 全局调度器入口。平台层在创建 VM **之前**把它换成自己的实现
/// （Avalonia: <c>Dispatcher.UIThread</c>；WPF: <c>Application.Current.Dispatcher</c>）。
/// 默认实现捕获第一个非空的 <see cref="SynchronizationContext"/>，够用且不会把活干到后台线程上。
/// </summary>
public static class AppDispatcher
{
    private static IUiDispatcher _current = new SynchronizationContextDispatcher();

    public static IUiDispatcher Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    private sealed class SynchronizationContextDispatcher : IUiDispatcher
    {
        private SynchronizationContext? _context;

        private SynchronizationContext? Context => _context ??= SynchronizationContext.Current;

        public void Post(Action action)
        {
            var ctx = Context;
            if (ctx is null)
            {
                action();
                return;
            }

            ctx.Post(_ => action(), null);
        }

        public Task InvokeAsync(Action action)
        {
            var ctx = Context;
            if (ctx is null || ReferenceEquals(ctx, SynchronizationContext.Current))
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.Post(_ =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, null);
            return tcs.Task;
        }
    }
}
