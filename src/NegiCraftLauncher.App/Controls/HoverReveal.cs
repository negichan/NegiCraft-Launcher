using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace NegiCraftLauncher.App.Controls;

/// <summary>
/// 悬停一段时间后显示一块浮层，指针离开就从浮层和触发元素之间任一处收合。
///
/// <para><b>为什么轮询而不是 MouseEnter / MouseLeave 配对</b>：这是两处浮层各自踩过一遍的坑 ——
/// 指针从触发元素移到浮层的中途会"两边都不算悬停"（浮层贴边、或以
/// <c>Background="Transparent"</c> 把死区吃掉，都是为了让那段路不至于太长）。只认事件的话，
/// MouseLeave 一到就把浮层收掉，用户看到的是"鼠标放不过去"。所以判据改成每个 tick 现问
/// <c>IsMouseOver</c>：<see cref="Targets"/> 或浮层自己，只要有一个还算悬停就继续留着。</para>
///
/// <para><b>它只管开关，不管长相</b>：外观和补间由 <see cref="Pop"/>（或任何绑定到
/// <see cref="IsOpen"/> 的东西）负责。这样换一套视觉不用碰这里的时序。</para>
///
/// <para><b>外部打开的不接管</b>：调试动词（DebugBridge 的 <c>vol</c>）或
/// <c>CloseAllPopovers</c> 直接改 <see cref="IsOpen"/> 时不会启动轮询 —— 那种打开背后没有
/// 一次真实的悬停，一开轮询就会在下个 tick 被收掉。收合仍由调用方自己置 false。</para>
/// </summary>
public static class HoverReveal
{
    private static readonly ConditionalWeakTable<FrameworkElement, Runner> Runners = new();

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen", typeof(bool), typeof(HoverReveal),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOpenChanged));

    /// <summary>触发悬停的元素（可以只有一个）。浮层自己的 <c>MouseEnter</c> 也算。</summary>
    public static readonly DependencyProperty TargetsProperty = DependencyProperty.RegisterAttached(
        "Targets", typeof(FrameworkElement), typeof(HoverReveal), new PropertyMetadata(null, OnTargetsChanged));

    /// <summary>指针停在触发元素上多久才显示（毫秒）。0 = 立刻。给"不许横扫侧栏就一路点亮"。</summary>
    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(int), typeof(HoverReveal), new PropertyMetadata(130));

    /// <summary>收合判定的轮询间隔（毫秒）。越小收得越快，代价是空转次数。</summary>
    public static readonly DependencyProperty PollIntervalProperty = DependencyProperty.RegisterAttached(
        "PollInterval", typeof(int), typeof(HoverReveal), new PropertyMetadata(140));

    /// <summary>
    /// 暂停：窗口拖动这类"鼠标被捕获、IsMouseOver 问不出真话"的时段用。
    /// 解除时若浮层还开着会重新开始轮询（否则它会一直挂在屏上）。
    /// </summary>
    public static readonly DependencyProperty SuspendedProperty = DependencyProperty.RegisterAttached(
        "Suspended", typeof(bool), typeof(HoverReveal), new PropertyMetadata(false, OnSuspendedChanged));

    public static bool GetIsOpen(DependencyObject target) => (bool)target.GetValue(IsOpenProperty);
    public static void SetIsOpen(DependencyObject target, bool value) => target.SetValue(IsOpenProperty, value);
    public static FrameworkElement? GetTargets(DependencyObject target) => (FrameworkElement?)target.GetValue(TargetsProperty);
    public static void SetTargets(DependencyObject target, FrameworkElement? value) => target.SetValue(TargetsProperty, value);
    public static int GetDelay(DependencyObject target) => (int)target.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject target, int value) => target.SetValue(DelayProperty, value);
    public static int GetPollInterval(DependencyObject target) => (int)target.GetValue(PollIntervalProperty);
    public static void SetPollInterval(DependencyObject target, int value) => target.SetValue(PollIntervalProperty, value);
    public static bool GetSuspended(DependencyObject target) => (bool)target.GetValue(SuspendedProperty);
    public static void SetSuspended(DependencyObject target, bool value) => target.SetValue(SuspendedProperty, value);

    private static Runner RunnerFor(FrameworkElement target) =>
        Runners.GetValue(target, static item => new Runner(item));

    private static void OnIsOpenChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is FrameworkElement element) RunnerFor(element).OnIsOpenChanged(args.NewValue is true);
    }

    private static void OnTargetsChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is FrameworkElement element) RunnerFor(element).SetTarget(args.NewValue as FrameworkElement);
    }

    private static void OnSuspendedChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is FrameworkElement element) RunnerFor(element).OnSuspendedChanged(args.NewValue is true);
    }

    private sealed class Runner
    {
        private readonly FrameworkElement _self;
        private readonly DispatcherTimer _delay;
        private readonly DispatcherTimer _poll;
        private FrameworkElement? _target;

        /// <summary>true 表示这次打开是本行为推上去的（而不是调试动词 / 批量关闭那类外部写入）。</summary>
        private bool _opening;

        public Runner(FrameworkElement self)
        {
            _self = self;
            _delay = new DispatcherTimer(DispatcherPriority.Background);
            _delay.Tick += (_, _) =>
            {
                _delay.Stop();
                Open();
            };
            _poll = new DispatcherTimer(DispatcherPriority.Background);
            _poll.Tick += (_, _) => Poll();
            self.MouseEnter += OnSelfEnter;
            self.Unloaded += (_, _) => StopAll();
        }

        public void SetTarget(FrameworkElement? target)
        {
            if (ReferenceEquals(_target, target)) return;
            if (_target is not null) _target.MouseEnter -= OnTargetEnter;
            _target = target;
            if (_target is not null) _target.MouseEnter += OnTargetEnter;
        }

        public void OnIsOpenChanged(bool open)
        {
            if (!open)
            {
                StopAll();
                return;
            }
            // 外部写进来的 true 不接管：见类注释"外部打开的不接管"。
            if (!_opening) return;
            StartPoll();
        }

        public void OnSuspendedChanged(bool suspended)
        {
            if (suspended)
            {
                StopAll();
                return;
            }
            if (GetIsOpen(_self)) StartPoll();
        }

        private void OnTargetEnter(object sender, MouseEventArgs e) => Arm();

        private void OnSelfEnter(object sender, MouseEventArgs e)
        {
            // 指针已经落到浮层上，犹豫期没有意义了：立刻显示，收合交给轮询。
            if (!_delay.IsEnabled) return;
            _delay.Stop();
            Open();
        }

        private void Arm()
        {
            if (GetSuspended(_self) || GetIsOpen(_self)) return;
            var delay = TimeSpan.FromMilliseconds(Math.Max(0, GetDelay(_self)));
            if (delay <= TimeSpan.Zero)
            {
                Open();
                return;
            }
            _delay.Stop();
            _delay.Interval = delay;
            _delay.Start();
        }

        private void Open()
        {
            if (GetSuspended(_self) || GetIsOpen(_self)) return;
            _opening = true;
            try
            {
                _self.SetValue(IsOpenProperty, true);
            }
            finally
            {
                _opening = false;
            }
        }

        private void Poll()
        {
            if (!GetIsOpen(_self) || GetSuspended(_self))
            {
                _poll.Stop();
                return;
            }
            if (_self.IsMouseOver || _target?.IsMouseOver == true) return;

            _poll.Stop();
            _self.SetValue(IsOpenProperty, false);
        }

        private void StartPoll()
        {
            _poll.Interval = TimeSpan.FromMilliseconds(Math.Max(16, GetPollInterval(_self)));
            _poll.Start();
        }

        private void StopAll()
        {
            _delay.Stop();
            _poll.Stop();
        }
    }
}
