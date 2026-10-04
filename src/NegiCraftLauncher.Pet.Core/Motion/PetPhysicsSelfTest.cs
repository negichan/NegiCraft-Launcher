namespace NegiCraftLauncher.Pet;

/// <summary>
/// 物理内核的<b>无头自测</b>：不开窗、不碰调试桥、不依赖真实时间。
///
/// <para><b>为什么需要它</b>：调试桥量不出「双击 W 疾跑」。一条调试命令的往返约 250ms
/// （邮箱 80ms 轮询 + UI 派发），而 <c>down→up→down</c> 要三条命令 ≈ 500ms，早就超出了
/// <see cref="PetPhysicsProfile.DoubleTapWindowMs"/> 的 350ms 窗口 —— 这不是实现问题，是量测手段的天花板。
/// 这里直接构造 <see cref="PetMotionContext"/>、按固定 dt 调 <see cref="PetMotion.Tick"/>，
/// 按键序列和帧长都自己控，于是「双击窗口边界」「跳跃高度与帧率无关」「松开 W 会不会掉」
/// 这类断言才做得出来。</para>
///
/// <para><b>断言的是「格」而不是 DIP</b>：物理参数按 Minecraft 原版的格给，乘
/// <see cref="PetMotion.DipPerBlock"/> 和缩放才成 DIP。自测同时跑 144/60/30fps 三档，
/// 顺带证明积分格式没有帧率依赖。</para>
///
/// <para><b>模型：自由移动 + 只用地面来跳跃</b>（见 <see cref="PetMotion.StepVertical"/>）。
/// 所以这里断言的是"松开 W 之后停在原地"、"走出平台边缘不掉"、"按 S 会停在地面上"，
/// 而不是"掉下去"。速度用 A/D 量（水平，碰不到地面下界）；双击则 <b>W/A/S/D 四个方向都要能起疾跑</b>。</para>
///
/// <para><b>拖拽抛出（P4）当前关着</b> —— <see cref="PetMotion.EndDrag"/> 里的注入点被注释掉了，
/// 所以这里断言的是"甩也不飞：松手只把桌宠放回你拖到的位置"。那 5 条抛出断言（速度上限 / 会飞 /
/// 落地弹跳 / 落在平台上 / 撞墙反弹）与它们的辅助函数一并注释在下面，打开抛出时一起恢复。</para>
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

    // 工作区必须**容得下测速期间的整段行程**，否则桌宠半路撞到墙 / 地板，稳态速度就被量低了。
    // 需求：StartX ≥ 行程（向左测速）且 Room.Width - StartX ≥ 行程（向右），
    // 其中 行程 = MeasureFrames × FrameSeconds × SprintBlocksPerSecond。
    // ⚠️ 疾跑从 5.612 提到 11.224 格/s 后，老房间（4000 宽、起点 x=1000）就不够了 ——
    // 症状是 2tap_a_333 / 2tap_s_333 两条莫名其妙 FAIL，而实现其实是对的。
    private static readonly PetWorkArea Room = new(0, 0, 8000, 8000);

    private static readonly PetStage StageGeometry = new(StageWidth, StageHeight, Scale, StageOffsetY);

    /// <summary>
    /// 起始窗口 X。放在房间正中 —— 左右两个方向都要留出 ≥ 行程 的空档
    /// （见 <see cref="Room"/> 的注释），所以不能贴着左边。
    /// </summary>
    private const double StartX = 4000.0;

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

        // ---------------------------------------------------------- 双击方向键的窗口边界
        // **W/A/S/D 四个方向都要能双击起疾跑**。四个键都是自由移动，所以期望的就是地面那两档速度。
        var windowFrames = (P.DoubleTapWindowMs / 1000.0) / FrameSeconds;
        var fastGap = (int)Math.Floor(windowFrames) - 3;   // 20 帧 = 333ms < 350 ⇒ 应当疾跑
        var slowGap = (int)Math.Ceiling(windowFrames) + 1; // 24 帧 = 400ms > 350 ⇒ 应当只是走

        foreach (var key in new[] { "w", "a", "s", "d" })
        {
            // S 要先把桌宠抬离地面：它起点就站在地板上，往下会被"不许沉到地面以下"夹住，
            // 速度会量成 0，看着像"双击 S 没生效"。
            // ⚠️ 抬升量必须 **大于测速行程**（= MeasureFrames × FrameSeconds × 疾跑速度 ≈ 1437 DIP），
            // 否则后半段被地板夹住、量出来的速度偏低。疾跑提速后 1500 就不够了。
            var startY = key == "s" ? StartY - 2500.0 : double.NaN;
            var fast = MeasureSpeed(DoubleTap(key, fastGap), 2 + fastGap + 8, startY);
            Check($"2tap_{key}_333", Math.Abs(fast.BlocksPerSecond - P.SprintBlocksPerSecond) < 0.01 && fast.Sprinting,
                $"{fast.BlocksPerSecond:F3}/{P.SprintBlocksPerSecond:F3}");
        }

        var slow = MeasureSpeed(DoubleTap("w", slowGap), 2 + slowGap + 8);
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
        // 一块离地板 1000 DIP 的平台（模拟某个窗口的标题栏），水平方向**罩住起始位置**
        // （StartX 落在 [3900, 4200] 里 —— 平台跟着 StartX 一起搬家，见 Room 的注释）。
        var ledge = new[] { new PetSurface(3900, 4200, FloorTop - 1000) };

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
        // 帧数按"走得完半个房间"给：1200 帧 × 1/60s × 5.612 格/s ≈ 7180 DIP > 4000（半宽）。
        {
            var m = NewMotion();
            Run(m, Hold("a"), 1200);
            var leftOk = Near(m.GroundX, Room.X);

            var m2 = NewMotion();
            Run(m2, Hold("d"), 1200);
            var rightOk = Near(m2.GroundX, Room.X + Room.Width - StageWidth);

            Check("walls", leftOk && rightOk);
        }

        // ---------------------------------------------------------- 拖拽松手
        // **「甩出去」当前是关的**（`PetMotion.EndDrag` 里那个注入点被注释掉了，P4 预留 ——
        // 见那里的注释：没有可落脚的窗口顶面时，甩出去只能在任务栏上来回弹）。
        // 所以这里断言的是"甩也不飞"：松手只把桌宠放回你拖到的位置。
        {
            var m = NewMotion();
            DragAndRelease(m, StartX, StartY, StartX + 800, StartY - 600, Room);
            var stayed = !m.IsThrown;
            var (vx, vy) = m.ThrowVelocity;
            Run(m, null, 30);
            Check("throw_off",
                stayed && vx == 0 && vy == 0
                && Near(m.GroundX, StartX + 800) && Near(m.GroundY, StartY - 600),
                $"thrown={!stayed}");
        }

        // 轻轻放下（地面上）：停在原地。
        {
            var m = NewMotion();
            DragAndRelease(m, StartX, StartY, StartX + 10, StartY, Room, frames: 12);
            var gentle = !m.IsThrown;
            Run(m, null, 60);
            Check("gentle_drop_stays",
                gentle && Near(m.GroundX, StartX + 10) && Near(m.GroundY, StartY), $"gentle={gentle}");
        }

        // 轻轻放在半空：**就停在半空**。
        // 这条是"自由移动"语义的哨兵：不主动甩就不受重力，悬着就悬着。
        {
            var m = NewMotion();
            var airY = StartY - 150;
            DragAndRelease(m, StartX, StartY, StartX, airY, Room, frames: 120);
            var gentle = !m.IsThrown;
            Run(m, null, 60);
            Check("gentle_drop_air", gentle && Near(m.GroundY, airY), $"gentle={gentle}");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  下面 5 条是「抛出打开后」用的，连同 Run() 外面的 NarrowRoom / StandYIn /
        //  NewMotionIn / Flight / Fly 一起注释着。恢复时把这几段与 PetMotion.EndDrag 里
        //  那个注入点一并放开即可（那时 `throw_off` 那条要删掉）。
        // ═══════════════════════════════════════════════════════════════════════════
        // // 甩得再快也会被夹在上限 —— 否则手一抖就把桌宠甩到屏幕外。
        // {
        //     var m = NewMotion();
        //     DragAndRelease(m, StartX, StartY, StartX + 4000, StartY - 4000, Room);
        //     var (vx, vy) = m.ThrowVelocity;
        //     var speed = Math.Sqrt((vx * vx) + (vy * vy)) / DipPerBlock;
        //     Check("throw_cap", m.IsThrown && Math.Abs(speed - P.ThrowMaxBlocksPerSecond) < 0.01,
        //         $"{speed:F3}/{P.ThrowMaxBlocksPerSecond:F3}");
        // }
        //
        // // 甩出去（竖直为主的一甩）：飞起来、落地弹几下、最后停回地板。
        // {
        //     const double lift = 240.0;
        //     var m = NewMotion();
        //     DragAndRelease(m, StartX, StartY, StartX + 60, StartY - lift, Room);
        //     var thrown = m.IsThrown;
        //     var f = Fly(m, Room);
        //     var rise = f.PeakHeight - lift;   // 松手点之上又涨了多少
        //
        //     Check("throw_flies",
        //         thrown && f.Settled && f.AirborneFrames > 40 && rise > 200
        //         && Near(f.FinalY, StartY) && f.FinalX > StartX + 40,
        //         $"{f.AirborneFrames}f rise={rise:F0} dx={f.FinalX - StartX:F0}");
        //
        //     // 弹跳：触地 ≥ 2 次说明落地后确实弹起来过（第一次落地 + 弹回来那一次）。
        //     Check("throw_bounces", thrown && f.Contacts >= 2, $"contacts={f.Contacts}");
        // }
        //
        // // 甩到平台上：从平台顶面往上一甩，必须落回**平台**，不能穿到下面的地板。
        // {
        //     const double lift = 240.0;
        //     var ledgeTop = FloorTop - 1000;
        //     var startY = ledgeTop - FeetInWindow;
        //     var m = NewMotion(startY);
        //     DragAndRelease(m, StartX, startY, StartX, startY - lift, Room);
        //     var thrown = m.IsThrown;
        //     var f = Fly(m, Room, ledge);
        //
        //     Check("throw_lands_on_ledge",
        //         thrown && f.Settled && f.AirborneFrames > 30 && f.PeakHeight - lift > 200
        //         && Near(m.GroundY + FeetInWindow, ledgeTop),
        //         $"{m.GroundY + FeetInWindow:F0}/{ledgeTop:F0} {f.AirborneFrames}f");
        // }
        //
        // // 边缘反弹：往右甩，必须被右墙挡回来（既不粘在墙上，也不飞出屏幕）。
        // {
        //     var m = NewMotionIn(NarrowRoom, 100, narrowFloorY);
        //     var rightEdgeX = NarrowRoom.X + NarrowRoom.Width - StageWidth;
        //     DragAndRelease(m, 100, narrowFloorY, 700, narrowFloorY - 200, NarrowRoom);
        //     var thrown = m.IsThrown;
        //     var f = Fly(m, NarrowRoom);
        //
        //     Check("throw_wall_bounce",
        //         thrown && f.Settled && f.MaxX <= rightEdgeX + Eps && f.MaxX >= rightEdgeX - Eps
        //         && f.MinX >= NarrowRoom.X - Eps && f.FinalX < f.MaxX - 100
        //         && Near(f.FinalY, narrowFloorY),
        //         $"max={f.MaxX:F0} final={f.FinalX:F0}");
        // }

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

    /// <summary>双击某个方向键：第 0 帧按下、第 2 帧抬起、第 <c>2+gap</c> 帧再按下并一直按住。</summary>
    private static Action<PetMotion, int> DoubleTap(string key, int gap) => (m, i) =>
    {
        if (i == 0) m.SetSimulatedKey(key, true);
        else if (i == 2) m.SetSimulatedKey(key, false);
        else if (i == 2 + gap) m.SetSimulatedKey(key, true);
    };

    // ---------------------------------------------------------------- 量测

    /// <summary>
    /// 按固定 dt 推进，量出稳态速度（格/s）。
    /// <paramref name="startY"/> 给定时用它当起始窗口 Y（默认 <see cref="StartY"/>，脚踩地板）——
    /// 量"往下"的速度时必须抬高，否则会被地面下界夹住、量成 0。
    /// </summary>
    private static Measure MeasureSpeed(Action<PetMotion, int> drive, int warmup, double startY = double.NaN)
    {
        var m = NewMotion(startY);

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

    // ---------------------------------------------------------------- 拖拽

    /// <summary>
    /// 模拟一次拖拽：把窗口从 (x0,y0) 匀速拖到 (x1,y1)，然后松手。
    /// <b>速度由 <paramref name="frames"/> 决定</b> —— 同样的位移、帧数越多速度越慢。
    ///
    /// <para>采样是在 <see cref="PetMotion.Tick"/> 的拖拽分支里做的（内核从 <c>ctx.Window</c> 自己记），
    /// 所以这里只要老老实实每帧把窗口位置喂进去即可 —— 不需要任何"报速度"的接口。</para>
    ///
    /// <para>⚠️ <paramref name="room"/> 与那两个坐标是**必须**的：窗口位置得真的动起来，
    /// 否则"窗口位置 = 地面位置"、采出来全是同一个点。</para>
    /// </summary>
    private static void DragAndRelease(PetMotion m, double x0, double y0, double x1, double y1,
                                       PetWorkArea room, int frames = 6)
    {
        m.IsDragging = true;
        for (var i = 0; i <= frames; i++)
        {
            var k = (double)i / frames;
            m.Tick(FrameSeconds, Context(m, null, room, x0 + ((x1 - x0) * k), y0 + ((y1 - y0) * k)));
        }
        m.IsDragging = false;
        m.EndDrag(x1, y1);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    //  下面这几个是「抛出打开后」才用得上的辅助，现在和那 5 条断言一起注释着。
    //  恢复时把它们与 PetMotion.EndDrag 里的注入点一并放开。
    //
    //  · NarrowRoom   抛出一组断言专用的窄房间（墙近，几十帧就能撞到右墙）
    //  · Flight/Fly   推进到停稳，记录飞行极值与触地次数（"弹跳"的哨兵）
    //  · StandYIn     某个工作区里"站在地板上"时窗口的 Y
    //  · NewMotionIn  在指定房间里、指定位置新建一个操控模式的内核
    // ═══════════════════════════════════════════════════════════════════════════════
    //
    // /// <summary>
    // /// 抛出一组断言专用的**窄房间**。墙近一点，几十帧就能撞到右墙 —— 用 4000 宽的房间
    // /// 得跑三秒多，而且中间会先落地，测不出"空中撞墙反弹"。
    // /// </summary>
    // private static readonly PetWorkArea NarrowRoom = new(0, 0, 900, 800);
    //
    // /// <summary>一次抛体飞行的观测结果。</summary>
    // private readonly record struct Flight(
    //     int AirborneFrames,
    //     int Contacts,
    //     double PeakHeight,
    //     double MaxX,
    //     double MinX,
    //     double FinalX,
    //     double FinalY,
    //     bool Settled);
    //
    // /// <summary>某个工作区里"站在地板上"时窗口的 Y。</summary>
    // private static double StandYIn(PetWorkArea room) => room.Y + room.Height - FeetInWindow;
    //
    // /// <summary>在指定房间里、指定位置新建一个操控模式的内核。</summary>
    // private static PetMotion NewMotionIn(PetWorkArea room, double x, double y)
    // {
    //     var m = new PetMotion();
    //     m.SetMode(PetInteractionMode.Control);
    //     m.BeginControlMode(x, y);
    //     return m;
    // }
    //
    // /// <summary>
    // /// 一直推进到桌宠停稳（或到帧数上限），同时记录飞行过程的极值与触地次数。
    // ///
    // /// <para><c>Contacts</c> 数的是"触地"：高度从 &gt;1 DIP 落到 ≤0.5 DIP 算一次，
    // /// <b>起始时按已触地计</b>（松手那一帧脚底本来就贴着台面，不该算一次落地）。
    // /// 于是"落地 → 弹起 → 再落地"会数到 2 —— 这就是"弹跳"的哨兵。</para>
    // /// </summary>
    // private static Flight Fly(PetMotion m, PetWorkArea room, IReadOnlyList<PetSurface>? surfaces = null,
    //                           int maxFrames = 900)
    // {
    //     var airborne = 0;
    //     var contacts = 0;
    //     var touched = true;
    //     var peak = 0.0;
    //     var minX = double.MaxValue;
    //     var maxX = double.MinValue;
    //
    //     for (var i = 0; i < maxFrames && (i == 0 || m.IsThrown); i++)
    //     {
    //         m.Tick(FrameSeconds, Context(m, surfaces, room));
    //
    //         minX = Math.Min(minX, m.GroundX);
    //         maxX = Math.Max(maxX, m.GroundX);
    //
    //         if (!m.IsThrown) break;
    //
    //         airborne++;
    //         var height = m.HeightAboveSupport;
    //         peak = Math.Max(peak, height);
    //
    //         if (height <= 0.5)
    //         {
    //             if (!touched) contacts++;
    //             touched = true;
    //         }
    //         else if (height > 1.0)
    //         {
    //             touched = false;
    //         }
    //     }
    //
    //     return new Flight(airborne, contacts, peak, maxX, minX, m.GroundX, m.GroundY, !m.IsThrown);
    // }

    /// <summary>
    /// 新建一个处在操控模式的内核。<paramref name="startY"/> 不给就落在 <see cref="StartY"/>
    /// （脚踩地板，桌宠的自然静止位置）。
    /// </summary>
    private static PetMotion NewMotion(double startY = double.NaN)
    {
        var m = new PetMotion();
        m.SetMode(PetInteractionMode.Control);
        m.BeginControlMode(StartX, double.IsNaN(startY) ? StartY : startY);
        return m;
    }

    /// <summary>
    /// 构造一帧上下文。窗口位置默认跟着地面走（<paramref name="windowX"/>/<paramref name="windowY"/>
    /// 给定时用给定的 —— 拖拽测试要靠它把窗口"搬"出去）。光标留空（视线追踪不影响位移）。
    /// </summary>
    private static PetMotionContext Context(
        PetMotion m,
        IReadOnlyList<PetSurface>? surfaces = null,
        PetWorkArea? room = null,
        double? windowX = null,
        double? windowY = null) =>
        new(StageGeometry,
            room ?? Room,
            new PetPoint(windowX ?? m.GroundX, windowY ?? m.GroundY),
            null,
            0f,
            surfaces);

    private static bool Near(double a, double b) => Math.Abs(a - b) < Eps;
}
