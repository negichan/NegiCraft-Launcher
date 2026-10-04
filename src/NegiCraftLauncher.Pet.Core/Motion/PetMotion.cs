using NegiCraftLauncher.Pet.Services;

namespace NegiCraftLauncher.Pet;

/// <summary>
/// 桌宠的操控 / 跟随鼠标 / 坐标导航物理，外加鼠标视线追踪与身体自动平滑转身。
///
/// <para><b>为什么在共享层</b>：这段逻辑（约 400 行）原来在 Avalonia 与 WPF 两份
/// <c>PetWindow</c> 里各写了一遍，除了"用什么单位"之外逐行相同。两份实现最大的风险不是
/// 冗余，而是<b>改了一边忘了另一边</b> —— 而这类偏差在截图上看不出来（桌宠就那么小一块），
/// 只会表现为"两个平台的手感微妙地不一样"。现在数值只有一份。</para>
///
/// <para><b>坐标一律 DIP</b>。Avalonia 的 <c>Window.Position</c> 是 <c>PixelPoint</c>（物理像素），
/// WPF 的 <c>Left/Top/Width/Height</c> 是 DIP —— 两边在边界处换算，内核里只认 DIP。
/// 速度 / 重力 / 跳跃高度按 Minecraft 原版的<b>「格」</b>给，用时乘
/// <see cref="DipPerBlock"/>（以及桌宠缩放）换成 DIP，所以两种 DPI 下的物理速度一致。</para>
///
/// <para><b>它不碰任何 UI 对象</b>。每帧由宿主喂一个 <see cref="PetMotionContext"/>，内核吐出：
/// 地面位置（<see cref="GroundX"/>/<see cref="GroundY"/>）、跳跃偏移、朝向增量
/// <see cref="YawDelta"/>、头部看向 <see cref="HeadPitch"/>/<see cref="HeadYaw"/>、
/// 以及 Walking/Sprinting/Jumping/Sneaking 四个动画标志。宿主负责把它们推给预览控件。</para>
///
/// <para><b>朝向是宿主的</b>：右键拖拽旋转、<c>ResetRotation</c>、调试动词 <c>pet-yaw</c>
/// 都直接改预览控件的 yaw，所以内核每帧从 <see cref="PetMotionContext.CurrentYawDeg"/> 读当前值、
/// 只吐增量。否则两边会各记一份 yaw 然后慢慢漂开。</para>
/// </summary>
public sealed class PetMotion
{
    // ---------------------------------------------------------------- 几何常量
    // 这里是**单位换算与舞台锚点**，不是手感参数 —— 手感参数全在 PetPhysicsProfile 里。
    // 数值与 Avalonia 版逐字相同。改动任何一条都会让两个平台的手感对不上。

    /// <summary>
    /// 1 个 Minecraft 方块等于多少 DIP（桌宠缩放 100% 时）。
    ///
    /// <para>设计稿就是"64x128 的模型"（见 <c>SkinPreviewControl</c> 的注释：模型 128 DIP 高、
    /// 占 160x250 舞台的 51%，头顶在 Y≈36.6、脚在 Y≈164），角色两格高 ⇒ 1 格 = 64 DIP。
    /// 拿内核自己的锚点验算：脚底在舞台 Y=<see cref="FeetStageY"/>=160、头心在
    /// Y=<see cref="HeadStageY"/>=52，头心在脚底上方 28/32 个身位 ⇒ 身高 = 108 ÷ 0.875 ≈ 123.4
    /// ⇒ 1 格 ≈ 61.7。两个算法差 4%，取设计稿的整数。</para>
    /// </summary>
    public const double DipPerBlock = 64.0;

    /// <summary>头部中心在舞台里的 Y（舞台单位）。</summary>
    public const double HeadStageY = 52.0;

    /// <summary>
    /// 脚底在舞台里的 Y（舞台单位）。**碰撞用的地面锚点就是它** ——
    /// 加 <c>Stage.StageOffsetY</c> 再乘缩放，就是脚底在窗口内的 Y（scale 1 时 = 235）。
    ///
    /// <para>这个数实测过：<c>pet-skinsnap</c> 抓一帧、按 alpha 找外接框，
    /// 视觉脚底与它只差约 1 DIP，可以直接拿来当碰撞锚点。</para>
    /// </summary>
    public const double FeetStageY = 160.0;

    /// <summary>
    /// 名牌在舞台里的顶边距（舞台单位）。镜像 <c>SkinPreviewControl.NametagTop</c> ——
    /// 和 <see cref="HeadStageY"/> / <see cref="FeetStageY"/> 是同一类舞台锚点。
    ///
    /// <para>用途：它是**模型位图向上偏移的余量**。舞台里最靠上的元素就是名牌，
    /// 所以 <c>(NametagTopStageY + StageOffsetY) × 缩放</c> 就是"再往上多少会被窗口顶边裁掉"。
    /// 超过它的位移必须改由**窗口**承担，见 <see cref="StepVertical"/>。</para>
    /// </summary>
    public const double NametagTopStageY = 6.0;

    /// <summary>碰撞半宽对应的格数。MC 玩家碰撞盒宽 0.6 格，桌宠实测视觉宽约 0.56 格。</summary>
    public const double CollisionWidthBlocks = 0.6;

    /// <summary>找支撑面时判"正好踩着"的浮点余量（DIP）。</summary>
    private const double SupportEpsilon = 1e-6;

    /// <summary>朝行进方向转身的增益（不是最大角速度，那个在 profile 里）。</summary>
    private const double MoveTurnGain = 14.0;

    /// <summary>身体自动转向光标的最大角速度（deg/s）。</summary>
    public const double LookTurnSpeed = 260.0;

    private const double LookTurnGain = 7.0;

    /// <summary>头部扭角超过这个角度，身体才开始自己转过去。</summary>
    public const double LookTurnDeadZone = 25.0;

    private const double LookTurnRelax = 15.0;

    private const double LookYawFalloff = 420.0;
    private const double LookPitchFalloff = 380.0;
    private const double LookPitchLimit = 24.0;

    private const double MinStepSeconds = 0.002;
    private const double MaxStepSeconds = 0.04;

    // ---------------------------------------------------------------- 物理参数

    /// <summary>
    /// 手感参数：速度 / 重力 / 跳跃 / 各档阈值。默认 <see cref="PetPhysicsProfile.Default"/>。
    /// 想试另一套手感直接换一个 —— 逻辑侧不用改。暂不给 UI（见 profile 的注释）。
    /// </summary>
    public PetPhysicsProfile Profile { get; set; } = PetPhysicsProfile.Default;

    // ---------------------------------------------------------------- 状态

    private readonly List<PetPoint> _navQueue = [];
    private PetPoint _navTarget;
    private bool _hasNavTarget;

    private bool _keyW;
    private bool _keyA;
    private bool _keyS;
    private bool _keyD;
    private bool _keySpace;
    private bool _keyCtrl;
    private bool _keyShiftHeld;
    private bool _physicalShiftDown;
    private bool _sprintLocked;

    /// <summary>
    /// 上一帧 W/A/S/D 是不是按着（顺序 W,A,S,D）—— 双击疾跑靠它做边沿检测（见 <see cref="StepControl"/>）。
    /// </summary>
    private readonly bool[] _wasMoveDown = new bool[4];

    /// <summary>跳跃的竖直速度（DIP/s，**向下为正**）。**只在 <see cref="_jumping"/> 期间被重力累加。**</summary>
    private double _velocityY;

    /// <summary>
    /// 正在跳跃（模型往上飘、还没落回地面）。**重力只在它为真时跑** ——
    /// WASD 是自由移动，松开就停在原地，不受重力；地面只是"不许沉下去"的下界。
    /// </summary>
    private bool _jumping;

    /// <summary>
    /// 上一帧**夹取之后**的脚底 Y（屏幕 DIP）。找支撑面时和当前脚底取 <c>Min</c>：
    /// 只看移动后的位置的话，脚底一旦沉过台面一丁点，那块面就不再是候选（它跑到脚底以上了），
    /// 桌宠会直接从薄平台上**穿下去**。初值 +∞ ⇒ 第一帧退化成"用当前位置"。
    /// </summary>
    private double _standFeetY = double.PositiveInfinity;

    /// <summary>
    /// 内核自己的时间轴（秒），每帧 <c>+= dt</c>。<b>不用 <c>DateTime.UtcNow</c></b> ——
    /// 物理完全由 dt 驱动，墙钟却是独立走的：进程被卡住时（拖窗口、调试桥阻塞 UI 线程）
    /// dt 被夹到 40ms 而墙钟照走，两边一错开，靠墙钟做的判定就与模拟时间不符。
    /// 而且无头自测里帧跑得比真实时间快几个数量级，墙钟会让所有时间窗判定失效。
    /// </summary>
    private double _timeSeconds;

    /// <summary>
    /// 上一次「某个方向键刚按下」的内核时刻（秒，顺序 W,A,S,D），
    /// 见 <see cref="PetPhysicsProfile.DoubleTapWindowMs"/>。
    /// **四个键各记各的** —— 先点 W 再点 A 不算"双击"，那不是同一个方向。
    /// </summary>
    private readonly double[] _lastMovePressSeconds =
        [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];

    /// <summary>模式变了。宿主拿它更新菜单图标、切焦点、或在回自由待机时把窗口摆回去。</summary>
    public event Action<PetInteractionMode>? ModeChanged;

    /// <summary>当前交互模式。改它请走 <see cref="SetMode"/>。</summary>
    public PetInteractionMode Mode { get; private set; } = PetInteractionMode.Free;

    /// <summary>
    /// 窗口左上角（DIP）。物理直接推进它，宿主负责把它落到窗口上。
    ///
    /// <para><b>它和 <see cref="JumpOffsetY"/> 一起才构成"视觉位置"</b>：
    /// 模型在屏幕上的 Y = <c>GroundY + (FeetStageY + StageOffsetY) × 缩放 + JumpOffsetY</c>。
    /// <c>GroundY</c> 就是"脚踩在台面上"的那个位置 —— WASD 自由移动直接推它；
    /// <see cref="JumpOffsetY"/> 只在跳跃期间非零（模型往上飘、窗口不动）。</para>
    /// </summary>
    public double GroundX { get; set; }

    /// <inheritdoc cref="GroundX"/>
    public double GroundY { get; set; }

    /// <summary>
    /// 模型相对落脚位置的竖直偏移（DIP，向上为负，**永远不会为正**）。
    ///
    /// <para>为什么不直接挪窗口：桌宠的阴影是钉在地面上的（<c>SkinPreviewControl.SetJumpOffset</c>
    /// 只平移模型与名牌，阴影不动、还会随高度缩小变淡）。跳一下时窗口不动、只有模型往上飘，
    /// 那个"影子留在地上"的效果才成立。</para>
    ///
    /// <para>但模型位图向上最多只能挪 <c>(NametagTopStageY + StageOffsetY) × 缩放</c>，
    /// 再往上就被窗口顶边裁掉 —— 所以偏移到了这个余量就夹住（跳跃只有 1.25 格，正常够不到）。</para>
    /// </summary>
    public double JumpOffsetY { get; private set; }

    /// <summary>正在跳跃。宿主用它切跳跃姿势（也用于把移动速度换成"跳跃中"那一档）。</summary>
    public bool IsJumping => _jumping;

    /// <summary>
    /// 没在跳跃、可以起跳。**桌宠是自由移动的**（不受重力、松开就停在原地），
    /// 所以它不代表"脚底正踩着某个面"。
    /// </summary>
    public bool OnGround { get; private set; } = true;

    /// <summary>当前支撑面的台面高度（屏幕 DIP）。空中时是"将来会落到的那块"。</summary>
    public double SupportTop { get; private set; }

    /// <summary>脚底离当前支撑面多高（DIP，站在台面上时为 0）。跳跃高度就看它。</summary>
    public double HeightAboveSupport =>
        SupportTop - (GroundY + ((FeetStageY + LastStage.StageOffsetY) * LastStage.Scale) + JumpOffsetY);

    /// <summary>当前支撑面有几块候选（含地板）。诊断用 —— 验"站在窗口顶面"时看它有没有变多。</summary>
    public int SurfaceCount { get; private set; }

    /// <summary>正被抓握 / 拖拽：物理让位给挣扎晃头动画。</summary>
    public bool IsDragging { get; set; }

    public bool Walking { get; private set; }

    public bool Sprinting { get; private set; }

    /// <summary>下蹲中。手动开关 / 窗口 Shift / 物理 Shift 三者之一成立即为真。</summary>
    public bool Sneaking { get; private set; }

    /// <summary>菜单里的"潜行"开关是否打开（宿主用它决定图标显隐）。</summary>
    public bool ManualSneakToggle { get; private set; }

    /// <summary>本帧物理移动过窗口，宿主应当把窗口挪到 <see cref="GroundX"/>/<see cref="GroundY"/>。</summary>
    public bool PositionDirty { get; private set; }

    /// <summary>本帧应当累加到模型上的朝向增量（度）。</summary>
    public float YawDelta { get; private set; }

    /// <summary>本帧是否算出了头部看向（拖拽时不算）。</summary>
    public bool HasHeadLook { get; private set; }

    /// <summary>头部看向的俯仰（度，相对世界）。</summary>
    public float HeadPitch { get; private set; }

    /// <summary>头部看向的方位（度，相对世界）。</summary>
    public float HeadYaw { get; private set; }

    /// <summary>诊断串，<c>pet-track</c> 动词直接回它。</summary>
    public string DebugInfo { get; set; } = "";

    /// <summary>
    /// 上一帧宿主传进来的工作区（DIP）。<c>pet-area</c> 动词读它 —— 报的是
    /// <b>内核真正拿来夹取的</b>那个矩形，而不是宿主以为的，验多屏夹取才有意义。
    /// </summary>
    public PetWorkArea LastWorkArea { get; private set; }

    /// <summary>
    /// 上一帧宿主传进来的窗口左上角（DIP）。配合 <see cref="LastWorkArea"/>
    /// 判断桌宠落在哪块屏上。
    /// </summary>
    public PetPoint LastWindow { get; private set; }

    /// <summary>上一帧宿主传进来的舞台几何。<c>pet-motion</c> 用它把跳跃偏移换算成格。</summary>
    public PetStage LastStage { get; private set; }

    /// <summary>
    /// 诊断串，<c>pet-motion</c> 动词直接回它 —— 跳跃偏移（DIP）、这一帧「1 格 = 多少 DIP」、
    /// 三个移动标志、落脚位置、竖直速度、支撑面。调速度 / 跳跃高度 / 碰撞时靠它直接量。
    /// 按需拼，不每帧算。
    /// </summary>
    public string MotionDebugInfo =>
        $"mode={Mode} jump={JumpOffsetY:F2} block={DipPerBlock * LastStage.Scale:F2} " +
        $"walk={Walking} sprint={Sprinting} sneak={Sneaking} " +
        $"ground=({GroundX:F2},{GroundY:F2}) " +
        $"air={!OnGround} vy={_velocityY:F1} h={HeightAboveSupport:F2} " +
        $"support={SupportTop:F2} surf={SurfaceCount}";

    /// <summary>还有几个点没走到（当前目标算 1 个）。</summary>
    public int RemainingWaypointCount => (_hasNavTarget ? 1 : 0) + _navQueue.Count;

    /// <summary>当前有没有导航目标。</summary>
    public bool HasNavigationTarget => _hasNavTarget;

    // ---------------------------------------------------------------- 模式

    /// <summary>
    /// 切模式。内部会做状态清理：离开导航模式就丢掉途经点队列；离开操控模式就松开所有按键。
    /// 清理完触发 <see cref="ModeChanged"/>。
    /// </summary>
    public void SetMode(PetInteractionMode mode)
    {
        if (Mode == mode) return;

        Mode = mode;

        if (mode != PetInteractionMode.NavigateToCoord)
        {
            _navQueue.Clear();
            _hasNavTarget = false;
        }

        if (mode != PetInteractionMode.Control)
        {
            _keyW = false;
            _keyA = false;
            _keyS = false;
            _keyD = false;
            _keySpace = false;
            _keyCtrl = false;
            _sprintLocked = false;
            Array.Clear(_wasMoveDown);
            Array.Fill(_lastMovePressSeconds, double.NegativeInfinity);
        }

        ModeChanged?.Invoke(mode);
    }

    /// <summary>
    /// 切到操控模式时调用：把落脚位置对齐到窗口当前位置，清掉竖直速度与跳跃状态。
    /// 之后由 WASD **自由移动**；<see cref="StepVertical"/> 只负责"不许沉到地面以下"与跳跃。
    /// </summary>
    public void BeginControlMode(double windowX, double windowY)
    {
        GroundX = windowX;
        GroundY = windowY;
        JumpOffsetY = 0;
        _velocityY = 0;
        _jumping = false;
        _standFeetY = double.PositiveInfinity;
        OnGround = true;
    }

    /// <summary>
    /// 回到自由待机时调用：停掉全部运动动画。<b>位置原样保留</b> —— 桌宠是自由移动的，
    /// 悬在半空就悬在半空，自由待机不是"把它拽下来"。
    /// </summary>
    public void EnterFreeIdle()
    {
        Walking = false;
        Sprinting = false;
        _velocityY = 0;
        _jumping = false;
        _standFeetY = double.PositiveInfinity;
        OnGround = true;
    }

    /// <summary>松手落地：落脚位置对齐到窗口当前落点，清掉跳跃状态（P4 会在这里把拖拽采样到的
    /// 速度交进去，实现"甩出去"）。</summary>
    public void EndDrag(double windowX, double windowY)
    {
        GroundX = windowX;
        GroundY = windowY;
        JumpOffsetY = 0;
        _velocityY = 0;
        _jumping = false;
        _standFeetY = double.PositiveInfinity;
        OnGround = true;
    }

    // ---------------------------------------------------------------- 输入

    /// <summary>
    /// 窗口收到按键按下。负责 Shift 的下蹲刷新与 Esc 取消导航。
    ///
    /// <para><b>双击方向键的疾跑锁定不在这里</b> —— 桌宠窗口带 <c>WS_EX_NOACTIVATE</c>、基本拿不到
    /// 焦点，这个回调实际是死的。双击靠 <see cref="StepControl"/> 里对<b>每帧轮询的物理键状态</b>
    /// 做边沿检测（顺带把模拟按键也覆盖了）。</para>
    /// </summary>
    public void OnKeyDown(PetMotionKey key)
    {
        switch (key)
        {
            case PetMotionKey.Shift:
                _keyShiftHeld = true;
                RecomputeSneak();
                break;

            case PetMotionKey.Ctrl:
                _keyCtrl = true;
                break;

            case PetMotionKey.W:
                _keyW = true;
                break;

            case PetMotionKey.A: _keyA = true; break;
            case PetMotionKey.S: _keyS = true; break;
            case PetMotionKey.D: _keyD = true; break;
            case PetMotionKey.Space: _keySpace = true; break;

            case PetMotionKey.Escape:
                ClearNavigation();
                SetMode(PetInteractionMode.Free);
                break;
        }
    }

    /// <summary>窗口收到按键抬起。</summary>
    public void OnKeyUp(PetMotionKey key)
    {
        switch (key)
        {
            case PetMotionKey.Shift:
                _keyShiftHeld = false;
                RecomputeSneak();
                break;

            case PetMotionKey.Ctrl: _keyCtrl = false; break;

            case PetMotionKey.W:
                _keyW = false;
                break;

            case PetMotionKey.A: _keyA = false; break;
            case PetMotionKey.S: _keyS = false; break;
            case PetMotionKey.D: _keyD = false; break;
            case PetMotionKey.Space: _keySpace = false; break;
        }
    }

    /// <summary>
    /// 调试桥的模拟按键，绕开"窗口拿不到焦点"这件事。名字与 <c>_dbg.ps1</c> 传的一致。
    /// 模拟的 W 走的是和物理键同一条路，所以 <c>pet-key w</c> 连按两次一样能触发疾跑。
    /// </summary>
    public void SetSimulatedKey(string key, bool down)
    {
        switch (key.ToLowerInvariant())
        {
            case "w": _keyW = down; break;
            case "a": _keyA = down; break;
            case "s": _keyS = down; break;
            case "d": _keyD = down; break;
            case "space": _keySpace = down; break;
            case "ctrl": _keyCtrl = down; break;
            case "shift":
                _keyShiftHeld = down;
                RecomputeSneak();
                break;
        }
    }

    /// <summary>双击桌宠切换的手动潜行开关。</summary>
    public void ToggleManualSneak()
    {
        ManualSneakToggle = !ManualSneakToggle;
        RecomputeSneak();
    }

    private void RecomputeSneak() =>
        Sneaking = ManualSneakToggle || _keyShiftHeld || _physicalShiftDown;

    // ---------------------------------------------------------------- 导航

    /// <summary>设一个导航目标（DIP），丢掉已有队列，并切到导航模式。</summary>
    public void SetNavigationTarget(double x, double y)
    {
        _navQueue.Clear();
        _navTarget = new PetPoint(x, y);
        _hasNavTarget = true;
        SetMode(PetInteractionMode.NavigateToCoord);
    }

    /// <summary>
    /// 追加一个途经点（DIP）。当前不在导航中（或没有目标）时，这一下就是"设目标"。
    /// 选点遮罩按住 Ctrl 连续点多个点走的就是这条路。
    /// </summary>
    public void AddNavigationTarget(double x, double y)
    {
        var target = new PetPoint(x, y);
        if (Mode != PetInteractionMode.NavigateToCoord || !_hasNavTarget)
        {
            _navQueue.Clear();
            _navTarget = target;
            _hasNavTarget = true;
            SetMode(PetInteractionMode.NavigateToCoord);
        }
        else
        {
            _navQueue.Add(target);
        }
    }

    /// <summary>取消导航。正导航中就回自由待机。</summary>
    public void ClearNavigation()
    {
        _navQueue.Clear();
        _hasNavTarget = false;
        if (Mode == PetInteractionMode.NavigateToCoord) SetMode(PetInteractionMode.Free);
    }

    // ---------------------------------------------------------------- 每帧

    /// <summary>
    /// 推进一步。<paramref name="dt"/> 会被夹到 [2ms, 40ms]；小于 2ms 直接跳过（同一帧被
    /// 重复触发时不要重复推进）。
    /// </summary>
    public void Tick(double dt, in PetMotionContext ctx)
    {
        YawDelta = 0f;
        HasHeadLook = false;
        PositionDirty = false;

        // 记在 dt 早退之前：即便这一帧被跳过，诊断串报的也该是最新的平台信息。
        LastWorkArea = ctx.WorkArea;
        LastWindow = ctx.Window;
        LastStage = ctx.Stage;

        if (dt < MinStepSeconds) return;
        if (dt > MaxStepSeconds) dt = MaxStepSeconds;

        _timeSeconds += dt;

        // 被抓握或在空中拖拽时，以挣扎晃头动画为先。**竖直速度清零** ——
        // 松手后由 StepVertical 接管（P4 会在这里塞入拖拽采样到的速度，实现"甩出去"）。
        if (IsDragging)
        {
            JumpOffsetY = 0;
            _velocityY = 0;
            _jumping = false;
            OnGround = true;
            GroundX = ctx.Window.X;
            GroundY = ctx.Window.Y;
            Walking = false;
            Sprinting = false;
            return;
        }

        // 窗口被别处挪过（首次显示、用户拖动、切模式），地面位置还没跟上的话对齐一次。
        if (GroundX == 0 && GroundY == 0 && (ctx.Window.X != 0 || ctx.Window.Y != 0))
        {
            GroundX = ctx.Window.X;
            GroundY = ctx.Window.Y;
        }

        // 1. Shift 下蹲状态检测（物理键盘）
        var shiftDown = PetNativeKeys.IsDown(PetNativeKeys.VkShift);
        if (shiftDown != _physicalShiftDown)
        {
            _physicalShiftDown = shiftDown;
            RecomputeSneak();
        }

        // 2. 模式检测与物理模拟
        var yaw = ctx.CurrentYawDeg;
        switch (Mode)
        {
            case PetInteractionMode.Control:
                StepControl(dt, ctx, ref yaw);
                break;
            case PetInteractionMode.FollowMouse:
                StepFollowMouse(dt, ctx, ref yaw);
                break;
            case PetInteractionMode.NavigateToCoord:
                StepNavigation(dt, ctx, ref yaw);
                break;
            case PetInteractionMode.Free:
            default:
                break;
        }

        // 3. 竖直物理：跳跃的重力 / "不许沉到地面以下" / 支撑面重算。**所有模式都跑**，
        //    但重力只在跳跃期间生效 —— WASD 是自由移动，松开就停在原地。
        StepVertical(dt, ctx);

        // 4. 鼠标视线追踪与身体自动平滑转向
        StepMouseLook(dt, ctx, ref yaw);

        YawDelta = Normalize(yaw - ctx.CurrentYawDeg);
    }

    // ---------------------------------------------------------------- 三种模式

    /// <summary>操控模式：WASD 走位 + 空格起跳 + Shift 潜行 + Ctrl（或双击任意方向键）疾跑。</summary>
    private void StepControl(double dt, in PetMotionContext ctx, ref float yaw)
    {
        var wDown = PetNativeKeys.IsDown(PetNativeKeys.VkW) || _keyW;
        var aDown = PetNativeKeys.IsDown(PetNativeKeys.VkA) || _keyA;
        var sDown = PetNativeKeys.IsDown(PetNativeKeys.VkS) || _keyS;
        var dDown = PetNativeKeys.IsDown(PetNativeKeys.VkD) || _keyD;
        var spaceDown = PetNativeKeys.IsDown(PetNativeKeys.VkSpace) || _keySpace;
        var ctrlDown = PetNativeKeys.IsDown(PetNativeKeys.VkControl) || _keyCtrl;

        double moveX = 0;
        double moveY = 0;
        if (dDown) moveX += 1;
        if (aDown) moveX -= 1;
        if (sDown) moveY += 1;
        if (wDown) moveY -= 1;

        var isMoving = moveX != 0 || moveY != 0;
        Walking = isMoving;

        // 双击 **W/A/S/D 任意一个**方向键都起疾跑；松开全部方向键就退出疾跑。
        // **必须在每帧从轮询到的键状态里自己做边沿检测**：桌宠窗口带 WS_EX_NOACTIVATE、
        // 基本拿不到焦点，窗口的 KeyDown 收不到。
        // 四个键各记各的"上次按下时刻" —— 先点 W 再点 A 不算双击（那不是同一个方向）。
        TapDouble(wDown, ref _wasMoveDown[0], ref _lastMovePressSeconds[0]);
        TapDouble(aDown, ref _wasMoveDown[1], ref _lastMovePressSeconds[1]);
        TapDouble(sDown, ref _wasMoveDown[2], ref _lastMovePressSeconds[2]);
        TapDouble(dDown, ref _wasMoveDown[3], ref _lastMovePressSeconds[3]);

        // 和 Minecraft 一致：停下来就退出疾跑（按住 Ctrl 的疾跑不受影响）。
        if (!isMoving) _sprintLocked = false;

        // 疾跑：按住 Ctrl 或双击方向键，且不在下蹲
        var isSprinting = isMoving && (ctrlDown || _sprintLocked) && !Sneaking;
        Sprinting = isSprinting;

        if (isMoving)
        {
            var len = Math.Sqrt((moveX * moveX) + (moveY * moveY));
            var dirX = moveX / len;
            var dirY = moveY / len;

            // 速度按「格/s」给，乘 dipPerBlock 换成 DIP（dipPerBlock 里含桌宠缩放）。
            double blocksPerSecond;
            if (Sneaking) blocksPerSecond = Profile.SneakBlocksPerSecond;
            else if (isSprinting) blocksPerSecond = IsJumping ? Profile.JumpSprintBlocksPerSecond : Profile.SprintBlocksPerSecond;
            else blocksPerSecond = IsJumping ? Profile.JumpWalkBlocksPerSecond : Profile.WalkBlocksPerSecond;

            var speed = blocksPerSecond * DipPerBlock * ctx.Stage.Scale;

            // **自由移动**：两个方向都直接推位置，竖直方向不跑重力 —— 松开就停在原地。
            // 地面只是"不许沉下去"的下界（见 StepVertical），所以按 S 会停在地板 / 窗口顶面上。
            GroundX += dirX * speed * dt;
            GroundY += dirY * speed * dt;
            ClampHorizontally(ctx);

            yaw += TurnToward(dirX, dirY, yaw, dt);
        }

        // 起跳：站在地面上按空格（连跳 = 按住不放，落地即再起跳）。
        // 重力与落地判定都在 StepVertical 里，且**只在跳跃期间**生效。
        if (spaceDown && OnGround)
        {
            _jumping = true;
            _velocityY = Profile.JumpVelocityBlocksPerSecond * DipPerBlock * ctx.Stage.Scale;
        }

        PositionDirty = true;
    }

    /// <summary>
    /// 一个方向键的边沿检测：这一帧刚按下、且距它**上次**按下还在双击窗口内 ⇒ 解锁疾跑。
    ///
    /// <para>为什么要自己做：桌宠窗口带 <c>WS_EX_NOACTIVATE</c>、基本拿不到键盘焦点，
    /// 窗口的 <c>KeyDown</c> 是死的 —— 只能每帧轮询物理键状态、自己找上升沿
    /// （顺带把调试桥的模拟按键也覆盖了）。</para>
    ///
    /// <para>四个键各传自己那份 <paramref name="wasDown"/> / <paramref name="lastPressSeconds"/>，
    /// 所以"先点 W 再点 A"不会被算成一次双击。</para>
    /// </summary>
    private void TapDouble(bool down, ref bool wasDown, ref double lastPressSeconds)
    {
        var rising = down && !wasDown;
        wasDown = down;
        if (!rising) return;

        if (_timeSeconds - lastPressSeconds < Profile.DoubleTapWindowMs / 1000.0) _sprintLocked = true;
        lastPressSeconds = _timeSeconds;
    }

    /// <summary>跟随鼠标：朝光标走，进了停靠半径就停。</summary>
    private void StepFollowMouse(double dt, in PetMotionContext ctx, ref float yaw)
    {
        if (ctx.Cursor is not { } cursor) return;

        var (dx, dy, dist) = OffsetToFeet(ctx, cursor);
        if (dist <= Profile.FollowDockRadius)
        {
            Walking = false;
            Sprinting = false;
            return;
        }

        Walking = true;
        var isSprint = dist > Profile.FollowSprintDistance;
        Sprinting = isSprint;

        var dirX = dx / dist;
        var dirY = dy / dist;
        yaw += TurnToward(dirX, dirY, yaw, dt);

        var speed = (isSprint ? Profile.SprintBlocksPerSecond : Profile.WalkBlocksPerSecond)
                    * DipPerBlock * ctx.Stage.Scale;
        var moveDist = Math.Min(speed * dt, dist - Profile.FollowDockRadius + 2.0);
        GroundX += dirX * moveDist;
        GroundY += dirY * moveDist;

        ClampToWorkArea(ctx);
        PositionDirty = true;
    }

    /// <summary>坐标导航：逐个吃掉途经点，走完自动回自由待机。</summary>
    private void StepNavigation(double dt, in PetMotionContext ctx, ref float yaw)
    {
        if (!_hasNavTarget)
        {
            Walking = false;
            Sprinting = false;
            SetMode(PetInteractionMode.Free);
            return;
        }

        var (dx, dy, dist) = OffsetToFeet(ctx, _navTarget);

        if (dist <= Profile.NavArriveDistance)
        {
            if (_navQueue.Count > 0)
            {
                // 还有后续途经点：出队下一个点继续移动
                _navTarget = _navQueue[0];
                _navQueue.RemoveAt(0);
                (dx, dy, dist) = OffsetToFeet(ctx, _navTarget);
            }
            else
            {
                // 全部途经点到达完成，停下脚步，自动切回自由待机模式
                _hasNavTarget = false;
                Walking = false;
                Sprinting = false;
                SetMode(PetInteractionMode.Free);
                return;
            }
        }

        Walking = true;
        var isSprint = dist > Profile.NavSprintDistance;
        Sprinting = isSprint;

        var dirX = dx / dist;
        var dirY = dy / dist;
        yaw += TurnToward(dirX, dirY, yaw, dt);

        var speed = (isSprint ? Profile.SprintBlocksPerSecond : Profile.WalkBlocksPerSecond)
                    * DipPerBlock * ctx.Stage.Scale;
        var moveDist = Math.Min(speed * dt, dist);
        GroundX += dirX * moveDist;
        GroundY += dirY * moveDist;

        ClampToWorkArea(ctx);
        PositionDirty = true;
    }

    // ---------------------------------------------------------------- 视线与转身

    /// <summary>
    /// 头看向光标；身体在静止且头扭不过来（|diffYaw| &gt; 25°）时自己平滑转过去 ——
    /// 这样就不会出现"头卡在极限角度、身体不肯转"的别扭感。
    /// </summary>
    private void StepMouseLook(double dt, in PetMotionContext ctx, ref float yaw)
    {
        if (IsDragging)
        {
            DebugInfo = "LOOK_DRAGGING";
            return;
        }

        // 没有可用光标时兜底为"屏幕正前方"
        var curX = ctx.Cursor?.X ?? GroundX + (ctx.Stage.Width / 2.0);
        var curY = ctx.Cursor?.Y ?? GroundY - 100.0;

        var headCenterX = GroundX + (ctx.Stage.Width / 2.0);
        var headCenterY = GroundY + ((HeadStageY + ctx.Stage.StageOffsetY) * ctx.Stage.Scale);

        var dx = curX - headCenterX;
        var dy = curY - headCenterY;

        var targetLookYaw = (float)(Math.Atan2(dx, LookYawFalloff) * (180.0 / Math.PI));
        var targetPitchDeg = (float)Math.Clamp(
            Math.Atan2(dy, LookPitchFalloff) * (180.0 / Math.PI), -LookPitchLimit, LookPitchLimit);

        var diffYaw = Normalize(targetLookYaw - yaw);

        DebugInfo =
            $"cursor=({curX:F0},{curY:F0}) head=({(int)headCenterX},{(int)headCenterY}) " +
            $"dx={dx:F0} dy={dy:F0} targetYaw={targetLookYaw:F1} curYaw={yaw:F1} " +
            $"diffYaw={diffYaw:F1} isWalking={Walking}";

        if (!Walking)
        {
            var absDiff = Math.Abs(diffYaw);
            if (absDiff > LookTurnDeadZone)
            {
                var deficit = absDiff - (float)LookTurnRelax;
                var maxTurn = (float)(LookTurnSpeed * dt);
                var turnStep = Math.Clamp(deficit * (float)LookTurnGain * (float)dt, -maxTurn, maxTurn)
                               * Math.Sign(diffYaw);
                yaw += turnStep;
            }
        }

        HasHeadLook = true;
        HeadPitch = targetPitchDeg;
        HeadYaw = targetLookYaw;
    }

    /// <summary>朝 (dirX, dirY) 平滑转身一步，返回本步的朝向增量（度）。</summary>
    private float TurnToward(double dirX, double dirY, float yaw, double dt)
    {
        var targetYaw = (float)(Math.Atan2(dirX, dirY) * (180.0 / Math.PI));
        var diff = Normalize(targetYaw - yaw);
        var maxTurn = (float)(Profile.MoveTurnSpeed * dt);
        return Math.Clamp(diff * (float)MoveTurnGain * (float)dt, -maxTurn, maxTurn);
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>从桌宠脚底指向 <paramref name="target"/> 的向量与距离（DIP）。</summary>
    private (double Dx, double Dy, double Dist) OffsetToFeet(in PetMotionContext ctx, PetPoint target)
    {
        var feetX = GroundX + (ctx.Stage.Width / 2.0);
        var feetY = GroundY + ((FeetStageY + ctx.Stage.StageOffsetY) * ctx.Stage.Scale) + JumpOffsetY;
        var dx = target.X - feetX;
        var dy = target.Y - feetY;
        return (dx, dy, Math.Sqrt((dx * dx) + (dy * dy)));
    }

    // ---------------------------------------------------------------- 竖直物理

    /// <summary>
    /// 竖直物理：跳跃的重力、落回地面、以及"不许沉到地面以下"。
    ///
    /// <para><b>桌宠是自由移动的</b>：WASD 直接推 <see cref="GroundX"/>/<see cref="GroundY"/>，
    /// 竖直方向不跑重力 —— 松开就停在原地。重力<b>只在跳跃期间</b>生效，所以这里的主线是
    /// "起跳时把模型往上飘，再落回地面"。</para>
    ///
    /// <para><b>地面是"跟着走"的</b>：每帧在脚底正下方重新找一次最高的台面（工作区地板，
    /// 将来还会有别的窗口顶面）。它只当<b>下界</b>用 —— 脚底不许沉到它下面，于是按住 S 会
    /// 停在地板上 / 窗口顶面上。桌宠<b>不会</b>因为走出平台边缘而掉下去：那是自由移动，
    /// 不是平台跳跃。</para>
    ///
    /// <para><b>位置是"脚底 Y"这一个量</b>，最后再拆成"窗口位置 + 模型偏移"：跳跃时窗口不动、
    /// 只有模型往上飘，阴影留在地面上才是跳跃该有的样子。模型位图向上最多只能挪
    /// <c>(NametagTopStageY + StageOffsetY) × 缩放</c>，再往上会被窗口顶边裁掉 ——
    /// 跳跃只有 1.25 格，100% 缩放时正好落在余量以内。</para>
    /// </summary>
    private void StepVertical(double dt, in PetMotionContext ctx)
    {
        // 飞行模式：窗口就是当前位置，没有空中偏移，也不积累竖直速度。
        if (Mode is PetInteractionMode.FollowMouse or PetInteractionMode.NavigateToCoord)
        {
            if (JumpOffsetY != 0)
            {
                JumpOffsetY = 0;
                PositionDirty = true;
            }

            _velocityY = 0;
            _jumping = false;
            _standFeetY = double.PositiveInfinity;
            OnGround = true;
            return;
        }

        var stage = ctx.Stage;
        var scale = stage.Scale;
        var feetInWindow = (FeetStageY + stage.StageOffsetY) * scale;
        var headroom = (NametagTopStageY + stage.StageOffsetY) * scale;

        // 头顶离脚底多远 —— 把"模型不许出屏幕上沿"换算成脚底的下限。
        var bodyHeight = (FeetStageY - NametagTopStageY) * scale;

        var feetX = GroundX + (stage.Width / 2.0);
        var halfWidth = CollisionWidthBlocks * DipPerBlock * scale / 2.0;

        // 站立时的脚底（屏幕 DIP）。跳跃时模型往上飘，但**窗口不跟着动** —— 见下面第 4 步。
        var standFeetY = GroundY + feetInWindow;

        // 1. 脚下最高的台面（含工作区地板）。地面每帧重算一次 —— "随时跟着走"。
        //    用**上一帧停稳的位置**和当前位置里更低的那一个去找（见 _standFeetY）。
        var supportTop = FindSupportTop(ctx, feetX, halfWidth, Math.Min(_standFeetY, standFeetY));

        // 2. 下界：脚底不许沉到地面以下 ⇒ 按住 S 会停在地板上 / 窗口顶面上。
        if (standFeetY > supportTop) standFeetY = supportTop;

        // 3. 撞屏幕顶：模型头顶不许越出工作区上沿，否则窗口顶边会把头切掉。
        var minStandFeetY = ctx.WorkArea.Y + bodyHeight;
        if (standFeetY < minStandFeetY) standFeetY = minStandFeetY;

        // 4. 跳跃：**只有跳跃期间才跑重力**。梯形积分（velocity-Verlet）—— 位置按「本步平均速度」
        //    推进，匀加速下是精确解，所以跳跃高度与帧长无关，恒等于 Profile.JumpHeightBlocks。
        //    （显式欧拉会过冲、且过冲量正比于 dt；半隐式欧拉欠冲。）
        var offset = 0.0;
        if (_jumping)
        {
            var gravity = Profile.GravityBlocksPerSecondSquared * DipPerBlock * scale;
            offset = JumpOffsetY + ((_velocityY + (0.5 * gravity * dt)) * dt);
            _velocityY += gravity * dt;

            if (offset >= 0)
            {
                // 落回地面
                offset = 0;
                _velocityY = 0;
                _jumping = false;
            }
            else if (offset < -headroom)
            {
                // 模型位图向上最多飘这么多，再高就被窗口顶边裁掉（1.25 格通常够不到）
                offset = -headroom;
                if (_velocityY < 0) _velocityY = 0;
            }
        }
        else
        {
            _velocityY = 0;
        }

        var groundY = standFeetY - feetInWindow;

        if (groundY != GroundY || offset != JumpOffsetY) PositionDirty = true;

        GroundY = groundY;
        JumpOffsetY = offset;
        SupportTop = supportTop;
        _standFeetY = standFeetY;
        OnGround = !_jumping;
    }

    /// <summary>
    /// 脚下最高的那块台面（含工作区地板）。只认"在脚底以下、且水平方向与脚有交叠"的。
    /// 返回台面高度（屏幕 DIP）。顺带把候选数记进 <see cref="SurfaceCount"/> 供诊断。
    /// </summary>
    private double FindSupportTop(in PetMotionContext ctx, double feetX, double halfWidth, double feetY)
    {
        var area = ctx.WorkArea;
        var best = area.Y + area.Height;   // 地板永远存在，所以桌宠不会掉出屏幕
        var count = 1;

        if (ctx.Surfaces is { Count: > 0 } surfaces)
        {
            var left = feetX - halfWidth;
            var right = feetX + halfWidth;

            foreach (var s in surfaces)
            {
                count++;
                if (s.Top < feetY - SupportEpsilon) continue;          // 在脚底以上 —— 那是天花板，不是台面
                if (!s.OverlapsHorizontally(left, right)) continue;
                if (s.Top < best) best = s.Top;
            }
        }

        SurfaceCount = count;
        return best;
    }

    /// <summary>把窗口夹在屏幕工作区内，别让桌宠走出屏幕。<b>跟随 / 导航这两个飞行模式用。</b></summary>
    private void ClampToWorkArea(in PetMotionContext ctx)
    {
        var area = ctx.WorkArea;
        GroundX = Math.Clamp(GroundX, area.X, area.X + area.Width - ctx.Stage.Width);
        GroundY = Math.Clamp(GroundY, area.Y, area.Y + area.Height - ctx.Stage.Height);
    }

    /// <summary>
    /// 只夹左右。<b>操控模式用</b> —— 竖直方向交给 <see cref="StepVertical"/> 的支撑面，
    /// 在这里夹 <see cref="GroundY"/> 会把"掉到台面以下再被推回来"的落地判定挡住。
    /// </summary>
    private void ClampHorizontally(in PetMotionContext ctx)
    {
        var area = ctx.WorkArea;
        GroundX = Math.Clamp(GroundX, area.X, area.X + area.Width - ctx.Stage.Width);
    }

    /// <summary>把角度归一化到 (-180, 180]。</summary>
    private static float Normalize(float degrees)
    {
        while (degrees > 180f) degrees -= 360f;
        while (degrees < -180f) degrees += 360f;
        return degrees;
    }
}
