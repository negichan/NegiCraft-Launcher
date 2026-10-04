namespace NegiCraftLauncher.Pet;

/// <summary>
/// 物理内核的<b>无头自测</b>：不开窗、不碰调试桥、不依赖真实时间。
///
/// <para><b>为什么需要它</b>：调试桥量不出「双击 W 疾跑」。一条调试命令的往返约 250ms
/// （邮箱 80ms 轮询 + UI 派发），而 <c>down→up→down</c> 要三条命令 ≈ 500ms，早就超出了
/// <see cref="PetPhysicsProfile.DoubleTapWindowMs"/> 的 350ms 窗口 —— 这不是实现问题，是量测手段的天花板。
/// 这里直接构造 <see cref="PetMotionContext"/>、按固定 dt 调 <see cref="PetMotion.Tick"/>，
/// 按键序列和帧长都自己控，于是「双击窗口边界」「跳跃高度与帧率无关」「松开 W 会不会掉」</para>
/// 这类断言才做得出来。
///
/// <para><b>断言的是「格」而不是 DIP</b>：物理参数按 Minecraft 原版的格给，乘
/// <see cref="PetMotion.DipPerBlock"/> 和缩放才成 DIP。自测同时跑 144/60/30fps 三档，
/// 顺带证明积分格式没有帧率依赖。</para>
///
/// <para><b>模型：自由移动 + 只用地面来跳跃</b>（见 <see cref="PetMotion.StepVertical"/>）。
/// 所以这里断言的是"松开 W 之后停在原地"、"走出平台边缘不掉"、"按 S 会停在地面上"，
/// 而不是"掉下去"。<b>移动速度用 A/D 在地面上量</b> —— 四条腿里只有水平那对是真在走。</para>
///
/// <para>出口是调试动词 <c>pet-physics</c>（两个平台的 <c>PetDebugCommands</c> 都转发到这里），
/// 所以 WPF 与 Avalonia 跑的是同一份断言。返回值刻意压成<b>一行</b> —— 调试桥的邮箱是单行文本。</para>
/// </summary>
public static class PetPhysicsSelfTest
{
    // 舞台几何取真实值：160x320 窗口、100% 缩放、StageOffsetY=75（桌宠把舞台压到窗口下方）。
    // **StageOffsetY 必须和真机一致** —— 它决定"模型位图还能往上飘多少"（余量 = 6 + 75 = 81 DIP），
    // 取 0 的话跳跃与"撞屏幕顶"的边界条件就测不到了。
    private const double StageWidth = 160.0;
    private const double StageHeight = 320.0;
    private const double Scale = 1.0;
    private const double StageOffsetY = 75.0;

    // 工作区给得足够大，保证测速期间不会被左右墙夹到。
    private static readonly PetWorkArea Room = new(0, 0, 4000, 4000);

    private static readonly PetStage StageGeometry = new(StageWidth, StageHeight, Scale, StageOffsetY);

    private const double StartX = 1000.0;

    /// <summary>脚底在窗口内的 Y（DIP）。</summary>
    private static double FeetInWindow => (PetMotion.FeetStageY + StageOffsetY) * Scale;

    /// <summary>房间地板的高度。</summary>
    private static double FloorTop => Room.Y + Room.Height;

    /// <summary>
    /// 起始窗口 Y —— 让脚底**正好踩在地板上**。桌宠是自由移动的、不会自己往下掉，
    /// 所以起点必须就是"站在地上"，否则后面量到的全是悬空状态。
    /// </summary>
    private static double StartY => FloorTop - FeetInWindow;

    /// <summary>自测帧长（秒）—— 60fps。</summary>
    public const double FrameSeconds = 1.0 / 60.0;

    /// <summary>测速前的热身帧数（让按键边沿判定先跑完）。</summary>
    private const int WarmupFrames = 12;

    /// <summary>测速用的采样帧数。</summary>
    private const int MeasureFrames = 120;

    /// <summary>浮点比较余量（DIP）。</summary>
    private const double Eps = 0.01;

    /// <summary>一次测速的结论。</summary>
    private readonly record struct Measure(double BlocksPerSecond, bool Sprinting, bool Sneaking);

    /// <summary>1 格等于多少 DIP（含缩放）。</summary>
    private static double DipPerBlock => PetMotion.DipPerBlock * Scale;

    /// <summary>断言用的参数集 —— 就是出厂默认那套。</summary>
    private static PetPhysicsProfile P => PetPhysicsProfile.Default;

    /// <summary>跑全部断言，回一行结果。</summary>
    public static string Run()
    {
        var parts = new List<string> { $"block={DipPerBlock:F2}" };
        var total = 0;
        var passed = 0;

        void Check(string name, bool ok, string detail = "")
        {
            total++;
            if (ok) passed++;
            parts.Add($"{name}{(detail.Length > 0 ? "=" + detail : "")}{(ok ? "" : " FAIL")}");
        }

        void Speed(string name, Action<PetMotion, int> drive, int warmup, double expected)
        {
            var m = MeasureSpeed(drive, warmup);
            var ok = Math.Abs(m.BlocksPerSecond - expected) < 0.01;
            Check(name, ok, $"{m.BlocksPerSecond:F3}/{expected:F3}");
        }

        // ---------------------------------------------------------- 地面三档速度
        // 用 A/D 量 —— 四条腿里只有水平那对是"贴地走"。
        Speed("walk", Hold("d"), WarmupFrames, P.WalkBlocksPerSecond);
        Speed("sneak", Hold("shift", "d"), WarmupFrames, P.SneakBlocksPerSecond);
        Speed("sprint_ctrl", Hold("ctrl", "d"), WarmupFrames, P.SprintBlocksPerSecond);

        // ---------------------------------------------------------- 双击 W 的窗口边界
        // W 现在也是自由移动（往上走），所以期望的就是地面那两档速度。
        var windowFrames = (P.DoubleTapWindowMs / 1000.0) / FrameSeconds;
        var fastGap = (int)Math.Floor(windowFrames) - 3;   // 20 帧 = 333ms < 350 ⇒ 应当疾跑
        var slowGap = (int)Math.Ceiling(windowFrames) + 1; // 24 帧 = 400ms > 350 ⇒ 应当只是走

        var fast = MeasureSpeed(DoubleTapW(fastGap), 2 + fastGap + 8);
        Check("2tap_333ms", Math.Abs(fast.BlocksPerSecond - P.SprintBlocksPerSecond) < 0.01 && fast.Sprinting,
            $"{fast.BlocksPerSecond:F3}/{P.SprintBlocksPerSecond:F3}");

        var slow = MeasureSpeed(DoubleTapW(slowGap), 2 + slowGap + 8);
        Check("2tap_400ms", Math.Abs(slow.BlocksPerSecond - P.WalkBlocksPerSecond) < 0.01 && !slow.Sprinting,
            $"{slow.BlocksPerSecond:F3}/{P.WalkBlocksPerSecond:F3}");

        // ---------------------------------------------------------- 跳跃高度：三档帧率都必须是 1.25 格
        foreach (var (label, dt) in new[]
                 {
                     ("jump144", 1.0 / 144.0),
                     ("jump60", 1.0 / 60.0),
                     ("jump30", 1.0 / 30.0),
                 })
        {
            var blocks = JumpApexBlocks(dt);
            Check(label, Math.Abs(blocks - P.JumpHeightBlocks) < 0.01, $"{blocks:F3}/{P.JumpHeightBlocks:F3}");
        }

        // ---------------------------------------------------------- 自由移动（不受重力）
        // 按住 W 升上去 → 松开 → 必须停在原地。这条是"自由移动"与"平台跳跃"的分水岭。
        {
            var m = NewMotion();
            Run(m, HoldFor(40, "w"), 60);
            var raised = m.GroundY < StartY - 100;
            var held = m.GroundY;
            Run(m, null, 120);
            Check("free_holds", raised && Near(m.GroundY, held), $"rose={StartY - held:F0}");
        }

        // 起跳只影响模型偏移，不影响窗口位置；落地后回到原高度。
        {
            var m = NewMotion();
            var y0 = m.GroundY;
            m.SetSimulatedKey("space", true);
            m.Tick(FrameSeconds, Context(m));
            m.SetSimulatedKey("space", false);
            var jumped = m.IsJumping && m.JumpOffsetY < -1;

            var peak = 0.0;
            for (var i = 0; i < 90; i++)
            {
                m.Tick(FrameSeconds, Context(m));
                peak = Math.Min(peak, m.JumpOffsetY);
            }

            Check("jump_in_place",
                jumped && !m.IsJumping && Near(m.GroundY, y0) && Math.Abs((peak / DipPerBlock) + P.JumpHeightBlocks) < 0.01,
                $"{peak / DipPerBlock:F3}");
        }

        // ---------------------------------------------------------- 地面（下界）
        // 一块离地板 1000 DIP 的平台（模拟某个窗口的标题栏），水平方向罩住起始位置。
        var ledge = new[] { new PetSurface(900, 1200, FloorTop - 1000) };

        // 升到平台上方再按 S 沉下来 → 应当停在平台顶面，而不是穿过去。
        {
            var m = NewMotion();
            Run(m, HoldFor(300, "w"), 310, ledge);
            var above = m.GroundY + FeetInWindow < FloorTop - 1000 - 1;
            Run(m, Hold("s"), 400, ledge);
            Check("rests_on_ledge", above && Near(m.GroundY + FeetInWindow, FloorTop - 1000));
        }

        // 走出平台边缘**不掉**（自由移动，不是平台跳跃）：高度必须原样不动。
        {
            var m = NewMotion();
            Run(m, HoldFor(300, "w"), 310, ledge);
            Run(m, Hold("s"), 400, ledge);
            var onLedge = Near(m.GroundY + FeetInWindow, FloorTop - 1000);
            var y0 = m.GroundY;
            Run(m, Hold("d"), 120, ledge);
            Run(m, null, 120, ledge);
            Check("no_ledge_fall", onLedge && Near(m.GroundY, y0));
        }

        // 一直按 S 会停在工作区地板（地面永远存在，桌宠不会掉出屏幕）。
        {
            var m = NewMotion();
            Run(m, HoldFor(40, "w"), 60);
            var raised = m.GroundY < StartY - 100;
            Run(m, Hold("s"), 400);
            Check("sinks_to_floor", raised && Near(m.GroundY, StartY));
        }

        // ---------------------------------------------------------- 左右墙
        {
            var m = NewMotion();
            Run(m, Hold("a"), 700);
            var leftOk = Near(m.GroundX, Room.X);

            var m2 = NewMotion();
            Run(m2, Hold("d"), 700);
            var rightOk = Near(m2.GroundX, Room.X + Room.Width - StageWidth);

            Check("walls", leftOk && rightOk);
        }

        parts.Add($"RESULT={passed}/{total}{(passed == total ? " OK" : " FAIL")}");
        return string.Join("; ", parts);
    }

    // ---------------------------------------------------------------- 驱动

    /// <summary>整段按住若干键（测速用 —— 稳态速度不需要松开）。</summary>
    private static Action<PetMotion, int> Hold(params string[] keys) => (m, _) =>
    {
        foreach (var k in keys) m.SetSimulatedKey(k, true);
    };

    /// <summary>
    /// 按住前 <paramref name="frames"/> 帧、之后松开。
    /// <b>别用 <see cref="Hold"/> 做"按完再松手"的测试</b> —— 它的委托每帧都被调用、只会写 down，
    /// 键永远松不掉（上一版就是栽在这里：<c>land_on_ledge</c> 一直按住 W，桌宠根本没机会落地）。
    /// </summary>
    private static Action<PetMotion, int> HoldFor(int frames, params string[] keys) => (m, i) =>
    {
        var down = i < frames;
        foreach (var k in keys) m.SetSimulatedKey(k, down);
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
        var y0 = m.GroundY + m.JumpOffsetY;

        for (var i = warmup; i < warmup + MeasureFrames; i++)
        {
            drive(m, i);
            m.Tick(FrameSeconds, Context(m));
        }

        // 量的是**视觉位置** = 窗口位置 + 模型偏移。只看 GroundY 会被"模型往上飘、窗口不动"骗到。
        var dx = m.GroundX - x0;
        var dy = (m.GroundY + m.JumpOffsetY) - y0;
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
            // 量"离地面多高"，不是量 JumpOffsetY —— 后者在余量处会饱和。
            peak = Math.Max(peak, m.HeightAboveSupport);
        }

        return peak / DipPerBlock;
    }

    /// <summary>连续推进若干帧。</summary>
    private static void Run(PetMotion m, Action<PetMotion, int>? drive, int frames,
                            IReadOnlyList<PetSurface>? surfaces = null)
    {
        for (var i = 0; i < frames; i++)
        {
            // **每帧先松开全部键**，再由 drive 按下这一帧该按的。
            // 否则上一段 Run 按住的键会漏进下一段 —— no_ledge_fall 就是这么挂的：
            // 前一段沉底的 S 没松，桌宠一边往右走一边还在往下沉，高度当然对不上。
            ReleaseAll(m);
            drive?.Invoke(m, i);
            m.Tick(FrameSeconds, Context(m, surfaces));
        }
    }

    /// <summary>松开全部模拟键。</summary>
    private static void ReleaseAll(PetMotion m)
    {
        m.SetSimulatedKey("w", false);
        m.SetSimulatedKey("a", false);
        m.SetSimulatedKey("s", false);
        m.SetSimulatedKey("d", false);
        m.SetSimulatedKey("space", false);
        m.SetSimulatedKey("ctrl", false);
        m.SetSimulatedKey("shift", false);
    }

    private static PetMotion NewMotion()
    {
        var m = new PetMotion();
        m.SetMode(PetInteractionMode.Control);
        m.BeginControlMode(StartX, StartY);
        return m;
    }

    /// <summary>构造一帧上下文。窗口位置跟着地面走，光标留空（视线追踪不影响位移）。</summary>
    private static PetMotionContext Context(PetMotion m, IReadOnlyList<PetSurface>? surfaces = null) =>
        new(StageGeometry, Room, new PetPoint(m.GroundX, m.GroundY), null, 0f, surfaces);

    private static bool Near(double a, double b) => Math.Abs(a - b) < Eps;
}
