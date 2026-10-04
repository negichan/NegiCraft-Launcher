namespace NegiCraftLauncher.Pet;

/// <summary>
/// 物理内核的<b>无头自测</b>：不开窗、不碰调试桥、不依赖真实时间。
///
/// <para><b>为什么需要它</b>：调试桥量不出「双击 W 疾跑」。一条调试命令的往返约 250ms
/// （邮箱 80ms 轮询 + UI 派发），而 <c>down→up→down</c> 要三条命令 ≈ 500ms，早就超出了
/// <see cref="PetMotion.DoubleTapWindowMs"/> 的 350ms 窗口 —— 这不是实现问题，是量测手段的天花板。
/// 这里直接构造 <see cref="PetMotionContext"/>、按固定 dt 调 <see cref="PetMotion.Tick"/>，
/// 按键序列和帧长都自己控，于是「双击窗口边界」「跳跃高度与帧率无关」这类断言才做得出来。</para>
///
/// <para><b>断言的是「格」而不是 DIP</b>：物理参数按 Minecraft 原版的格给，乘
/// <see cref="PetMotion.DipPerBlock"/> 和缩放才成 DIP。自测同时跑 144/60/30fps 三档，
/// 顺带证明积分格式没有帧率依赖。</para>
///
/// <para>出口是调试动词 <c>pet-physics</c>（两个平台的 <c>PetDebugCommands</c> 都转发到这里），
/// 所以 WPF 与 Avalonia 跑的是同一份断言。返回值刻意压成<b>一行</b> —— 调试桥的邮箱是单行文本。</para>
/// </summary>
public static class PetPhysicsSelfTest
{
    // 舞台几何取真实值：160x320 舞台、100% 缩放、皮肤不额外竖直偏移。
    private const double StageWidth = 160.0;
    private const double StageHeight = 320.0;
    private const double Scale = 1.0;
    private const double StageOffsetY = 0.0;

    // 工作区给得足够大，保证测量期间不会被 ClampToWorkArea 夹到（否则速度会被量小）。
    private static readonly PetWorkArea Room = new(0, 0, 4000, 4000);

    private static readonly PetStage StageGeometry = new(StageWidth, StageHeight, Scale, StageOffsetY);

    // 起始位置取非零 —— GroundX/GroundY 同时为 0 时 Tick 会做一次「对齐到窗口」的特判。
    private const double StartX = 1000.0;
    private const double StartY = 1000.0;

    /// <summary>自测帧长（秒）—— 60fps。</summary>
    public const double FrameSeconds = 1.0 / 60.0;

    /// <summary>测速前的热身帧数（让按键边沿判定先跑完）。</summary>
    private const int WarmupFrames = 12;

    /// <summary>测速用的采样帧数。</summary>
    private const int MeasureFrames = 120;

    /// <summary>一次测速的结论。</summary>
    private readonly record struct Measure(double BlocksPerSecond, bool Sprinting, bool Sneaking);

    /// <summary>1 格等于多少 DIP（含缩放）。</summary>
    private static double DipPerBlock => PetMotion.DipPerBlock * Scale;

    /// <summary>跑全部断言，回一行结果。</summary>
    public static string Run()
    {
        var parts = new List<string> { $"block={DipPerBlock:F2}" };
        var total = 0;
        var passed = 0;

        void Speed(string name, Action<PetMotion, int> drive, int warmup, double expected)
        {
            var m = MeasureSpeed(drive, warmup);
            total++;
            var ok = Math.Abs(m.BlocksPerSecond - expected) < 0.01;
            if (ok) passed++;
            parts.Add($"{name}={m.BlocksPerSecond:F3}/{expected:F3}{(ok ? "" : " FAIL")}");
        }

        // ---- 三档移动速度 ----
        Speed("walk", Hold("w"), WarmupFrames, PetMotion.WalkBlocksPerSecond);
        Speed("sneak", Hold("shift", "w"), WarmupFrames, PetMotion.SneakBlocksPerSecond);
        Speed("sprint_ctrl", Hold("ctrl", "w"), WarmupFrames, PetMotion.SprintBlocksPerSecond);

        // ---- 双击 W 的窗口边界 ----
        // 按键时刻相差 (2+gap) 帧，所以 gap 直接决定落在 350ms 的哪一边。
        var windowFrames = (PetMotion.DoubleTapWindowMs / 1000.0) / FrameSeconds;
        var fastGap = (int)Math.Floor(windowFrames) - 3;   // 20 帧 = 333ms < 350 ⇒ 应当疾跑
        var slowGap = (int)Math.Ceiling(windowFrames) + 1; // 24 帧 = 400ms > 350 ⇒ 应当只是走

        var fast = MeasureSpeed(DoubleTapW(fastGap), 2 + fastGap + 8);
        total++;
        var fastOk = Math.Abs(fast.BlocksPerSecond - PetMotion.SprintBlocksPerSecond) < 0.01 && fast.Sprinting;
        if (fastOk) passed++;
        parts.Add($"2tap_333ms={fast.BlocksPerSecond:F3}/{PetMotion.SprintBlocksPerSecond:F3}" +
                  $"{(fastOk ? "" : " FAIL")}");

        var slow = MeasureSpeed(DoubleTapW(slowGap), 2 + slowGap + 8);
        total++;
        var slowOk = Math.Abs(slow.BlocksPerSecond - PetMotion.WalkBlocksPerSecond) < 0.01 && !slow.Sprinting;
        if (slowOk) passed++;
        parts.Add($"2tap_400ms={slow.BlocksPerSecond:F3}/{PetMotion.WalkBlocksPerSecond:F3}" +
                  $"{(slowOk ? "" : " FAIL")}");

        // ---- 跳跃高度：三档帧率都必须是 1.25 格 ----
        foreach (var (label, dt) in new[]
                 {
                     ("jump144", 1.0 / 144.0),
                     ("jump60", 1.0 / 60.0),
                     ("jump30", 1.0 / 30.0),
                 })
        {
            var blocks = JumpApexBlocks(dt);
            total++;
            var ok = Math.Abs(blocks - PetMotion.JumpHeightBlocks) < 0.01;
            if (ok) passed++;
            parts.Add($"{label}={blocks:F3}/{PetMotion.JumpHeightBlocks:F3}{(ok ? "" : " FAIL")}");
        }

        parts.Add($"RESULT={passed}/{total}{(passed == total ? " OK" : " FAIL")}");
        return string.Join("; ", parts);
    }

    // ---------------------------------------------------------------- 驱动

    /// <summary>整段按住若干键。</summary>
    private static Action<PetMotion, int> Hold(params string[] keys) => (m, _) =>
    {
        foreach (var k in keys) m.SetSimulatedKey(k, true);
    };

    /// <summary>双击 W：第 0 帧按下、第 2 帧抬起、第 <c>2+gap</c> 帧再按下并一直按住。</summary>
    private static Action<PetMotion, int> DoubleTapW(int gap) => (m, i) =>
    {
        if (i == 0) m.SetSimulatedKey("w", true);
        else if (i == 2) m.SetSimulatedKey("w", false);
        else if (i == 2 + gap) m.SetSimulatedKey("w", true);
    };

    // ---------------------------------------------------------------- 量测

    /// <summary>按固定 dt 推进，量出稳态速度（格/s）。</summary>
    private static Measure MeasureSpeed(Action<PetMotion, int> drive, int warmup)
    {
        var m = NewMotion();

        for (var i = 0; i < warmup; i++)
        {
            drive(m, i);
            m.Tick(FrameSeconds, Context(m));
        }

        var x0 = m.GroundX;
        var y0 = m.GroundY;

        for (var i = warmup; i < warmup + MeasureFrames; i++)
        {
            drive(m, i);
            m.Tick(FrameSeconds, Context(m));
        }

        var dx = m.GroundX - x0;
        var dy = m.GroundY - y0;
        var dip = Math.Sqrt((dx * dx) + (dy * dy));

        return new Measure(dip / (MeasureFrames * FrameSeconds) / DipPerBlock, m.Sprinting, m.Sneaking);
    }

    /// <summary>按住空格连跳，回最高点的格数。</summary>
    private static double JumpApexBlocks(double dt)
    {
        var m = NewMotion();
        m.SetSimulatedKey("space", true);

        var peak = 0.0;
        var frames = (int)Math.Ceiling(0.6 / dt); // 滞空约 0.52s，够跳满一次
        for (var i = 0; i < frames; i++)
        {
            m.Tick(dt, Context(m));
            peak = Math.Max(peak, Math.Abs(m.JumpOffsetY));
        }

        return peak / DipPerBlock;
    }

    private static PetMotion NewMotion()
    {
        var m = new PetMotion();
        m.SetMode(PetInteractionMode.Control);
        m.BeginControlMode(StartX, StartY);
        return m;
    }

    /// <summary>构造一帧上下文。窗口位置跟着地面走，光标留空（视线追踪不影响位移）。</summary>
    private static PetMotionContext Context(PetMotion m) =>
        new(StageGeometry, Room, new PetPoint(m.GroundX, m.GroundY), null, 0f);
}
