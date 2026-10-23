using System.Windows;

namespace NegiCraftLauncher.Theme;

/// <summary>
/// 动画调度层：决定"这次过渡怎么跑"（播 / 瞬时落地 / 加速），并登记在场过渡数。
///
/// <para><b>它不是动画引擎</b>：帧合成由 WPF 的动画时钟在渲染线程完成（相当于手机的
/// Choreographer），这层只管策略 —— 全局开关、速度倍率、可观测性。自己算帧会把动画
/// 从合成线程拖回 UI 线程，纯负收益。</para>
///
/// <para>速度倍率作用在"时长"上而不是全局时钟：XAML 触发器里已经跑起来的故事板拿不到
/// clock，统一折算时长才能让代码动画和模板动画表现一致。代价是倍率切换不影响已在飞的
/// 动画（手机系统同样是下次启动生效）。</para>
/// </summary>
public static class MotionEngine
{
    private static bool? _override;
    private static double _speedScale = 1;

    /// <summary>
    /// 全局开关。默认跟随 Windows"显示动画"设置（系统设置里关掉动画效果后整个应用不再动），
    /// 也可以被强制覆盖。
    ///
    /// <para><b>覆盖值为什么是可空的</b>：<c>null</c> = 跟随系统（别插手），
    /// <c>true</c>/<c>false</c> = 强制开/关。"跟随系统"必须**继续跟着系统走** ——
    /// 用户开着应用去改了 Windows 的"显示动画"，应用得跟上。所以"跟随"不是
    /// "把系统的当前值抄下来"，而是"别插手"—— 这正是可空覆盖值能表达、
    /// 而一个普通 bool 表达不了的区别。</para>
    ///
    /// <para>⚠️ 设置页那一档（跟随系统 / 总是开 / 总是关）由**应用层**算完之后写进来 ——
    /// 本工程刻意不引用 ViewModels（谁都能引它，包括桌宠），所以判据不放在这儿。
    /// 见 <c>MainWindow.ApplyMotionPreference</c>。</para>
    /// </summary>
    public static bool? Override
    {
        get => _override;
        set => _override = value;
    }

    /// <summary>
    /// 现在到底放不放动画 —— 覆盖值优先，没有覆盖就看系统。
    /// 业务代码一律问这个。
    /// </summary>
    public static bool Enabled => _override ?? SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// 全局速度倍率（0.5 = 全部动画快一倍，2 = 慢一倍），对应手机开发者选项里的"动画缩放"。
    /// </summary>
    public static double SpeedScale
    {
        get => _speedScale;
        set => _speedScale = value > 0 ? value : 1;
    }

    /// <summary>当前在飞的过渡数（只统计本层创建的）。调试看板用。</summary>
    public static int ActiveCount { get; private set; }

    /// <summary>
    /// 累计起过的过渡数。<b>只增不减</b>，用来量"这一轮操作里空转了多少条动画"。
    ///
    /// <para>为什么需要它：<see cref="ActiveCount"/> 只反映<b>此刻</b>在飞几条，
    /// 而空转的动画（起止值相同、视觉上什么都不会发生）生命周期很短，
    /// 采样时刻不同读数就不同。累计计数没有这个抖动 —— 同样的操作，
    /// 改前改后数出来的条数是一样的，能直接比。</para>
    /// </summary>
    public static int StartedCount { get; private set; }

    /// <summary>过渡开始/结束时触发。事件在 UI 线程。</summary>
    public static event Action? ActivityChanged;

    internal static void TweenStarted()
    {
        ActiveCount++;
        StartedCount++;
        ActivityChanged?.Invoke();
    }

    internal static void TweenEnded()
    {
        if (ActiveCount > 0) ActiveCount--;
        ActivityChanged?.Invoke();
    }

    /// <summary>按 <see cref="SpeedScale"/> 折算时长。非正时长原样返回。</summary>
    internal static TimeSpan Scale(TimeSpan duration)
    {
        var scale = _speedScale;
        if (scale <= 0 || Math.Abs(scale - 1) < 0.001 || duration <= TimeSpan.Zero) return duration;
        return TimeSpan.FromTicks((long)(duration.Ticks / scale));
    }
}
