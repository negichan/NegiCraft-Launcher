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
/// 所以速度常量（180 DIP/s 等）在两种 DPI 下的<b>物理速度</b>一致。</para>
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
    // ---------------------------------------------------------------- 常量
    // 这些数值与 Avalonia 版逐字相同。改动任何一条都会让两个平台的手感对不上。

    /// <summary>潜行速度（DIP/s）。</summary>
    public const double SneakSpeed = 80.0;

    /// <summary>行走速度（DIP/s）。</summary>
    public const double WalkSpeed = 180.0;

    /// <summary>疾跑速度（DIP/s）。</summary>
    public const double SprintSpeed = 270.0;

    /// <summary>跳跃中行走（DIP/s）。</summary>
    public const double JumpWalkSpeed = 200.0;

    /// <summary>跳跃中疾跑（DIP/s）。</summary>
    public const double JumpSprintSpeed = 290.0;

    /// <summary>起跳初速度（DIP/s，向上为负）。滞空约 0.38s、高度约 44 DIP。</summary>
    public const double JumpVelocity = -460.0;

    /// <summary>重力（DIP/s²）。</summary>
    public const double Gravity = 2400.0;

    /// <summary>朝行进方向转身的最大角速度（deg/s）。</summary>
    public const double MoveTurnSpeed = 720.0;

    private const double MoveTurnGain = 14.0;

    /// <summary>身体自动转向光标的最大角速度（deg/s）。</summary>
    public const double LookTurnSpeed = 260.0;

    private const double LookTurnGain = 7.0;

    /// <summary>头部扭角超过这个角度，身体才开始自己转过去。</summary>
    public const double LookTurnDeadZone = 25.0;

    private const double LookTurnRelax = 15.0;

    /// <summary>跟随鼠标的停靠半径（DIP）：光标在身旁这么近就停下。</summary>
    public const double FollowDockRadius = 85.0;

    /// <summary>跟随鼠标时超过这个距离就疾跑（DIP）。</summary>
    public const double FollowSprintDistance = 360.0;

    /// <summary>导航时超过这个距离就疾跑（DIP）。</summary>
    public const double NavSprintDistance = 350.0;

    /// <summary>导航到达判定半径（DIP）。</summary>
    public const double NavArriveDistance = 8.0;

    /// <summary>头部中心在舞台里的 Y（舞台单位）。</summary>
    public const double HeadStageY = 52.0;

    /// <summary>脚底在舞台里的 Y（舞台单位）。</summary>
    public const double FeetStageY = 160.0;

    private const double LookYawFalloff = 420.0;
    private const double LookPitchFalloff = 380.0;
    private const double LookPitchLimit = 24.0;

    /// <summary>双击 W 触发疾跑锁定的时间窗（毫秒）。</summary>
    public const double DoubleTapWindowMs = 350.0;

    private const double MinStepSeconds = 0.002;
    private const double MaxStepSeconds = 0.04;

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

    private double _jumpVelocityY;
    private DateTime _lastWPressTime = DateTime.MinValue;

    /// <summary>模式变了。宿主拿它更新菜单图标、切焦点、或在回自由待机时把窗口摆回去。</summary>
    public event Action<PetInteractionMode>? ModeChanged;

    /// <summary>当前交互模式。改它请走 <see cref="SetMode"/>。</summary>
    public PetInteractionMode Mode { get; private set; } = PetInteractionMode.Free;

    /// <summary>窗口左上角的地面位置（DIP）。物理直接推进它，宿主负责落到窗口上。</summary>
    public double GroundX { get; set; }

    /// <inheritdoc cref="GroundX"/>
    public double GroundY { get; set; }

    /// <summary>起跳造成的竖直偏移（DIP，向上为负）。</summary>
    public double JumpOffsetY { get; private set; }

    /// <summary>是否在空中。</summary>
    public bool IsJumping { get; private set; }

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
        }

        ModeChanged?.Invoke(mode);
    }

    /// <summary>切到操控模式时调用：把地面位置对齐到窗口当前位置，并清掉跳跃状态。</summary>
    public void BeginControlMode(double windowX, double windowY)
    {
        GroundX = windowX;
        GroundY = windowY;
        JumpOffsetY = 0;
        _jumpVelocityY = 0;
        IsJumping = false;
    }

    /// <summary>回到自由待机时调用：停掉全部运动动画，并把窗口摆回地面位置。</summary>
    public void EnterFreeIdle()
    {
        Walking = false;
        Sprinting = false;
        IsJumping = false;
        JumpOffsetY = 0;
        _jumpVelocityY = 0;
    }

    /// <summary>松手落地：地面位置对齐到窗口当前落点，清掉跳跃。</summary>
    public void EndDrag(double windowX, double windowY)
    {
        GroundX = windowX;
        GroundY = windowY;
        IsJumping = false;
        JumpOffsetY = 0;
        _jumpVelocityY = 0;
    }

    // ---------------------------------------------------------------- 输入

    /// <summary>窗口收到按键按下。负责双击 W 的疾跑锁定、Shift 的下蹲刷新、Esc 取消导航。</summary>
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
                var now = DateTime.UtcNow;
                if ((now - _lastWPressTime).TotalMilliseconds < DoubleTapWindowMs) _sprintLocked = true;
                _lastWPressTime = now;
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
                _sprintLocked = false;
                break;

            case PetMotionKey.A: _keyA = false; break;
            case PetMotionKey.S: _keyS = false; break;
            case PetMotionKey.D: _keyD = false; break;
            case PetMotionKey.Space: _keySpace = false; break;
        }
    }

    /// <summary>
    /// 调试桥的模拟按键，绕开"窗口拿不到焦点"这件事。名字与 <c>_dbg.ps1</c> 传的一致。
    /// 注意它<b>不做</b>双击 W 的疾跑锁定 —— 与原实现一致。
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

        if (dt < MinStepSeconds) return;
        if (dt > MaxStepSeconds) dt = MaxStepSeconds;

        // 被抓握或在空中拖拽时，以挣扎晃头动画为先
        if (IsDragging)
        {
            IsJumping = false;
            JumpOffsetY = 0;
            _jumpVelocityY = 0;
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

        // 3. 鼠标视线追踪与身体自动平滑转向
        StepMouseLook(dt, ctx, ref yaw);

        YawDelta = Normalize(yaw - ctx.CurrentYawDeg);
    }

    // ---------------------------------------------------------------- 三种模式

    /// <summary>操控模式：WASD 走位 + 空格起跳 + Shift 潜行 + Ctrl（或双击 W）疾跑。</summary>
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

        // 疾跑：按住 Ctrl 或双击 W，且不在下蹲
        var isSprinting = isMoving && (ctrlDown || _sprintLocked) && !Sneaking;
        Sprinting = isSprinting;

        if (isMoving)
        {
            var len = Math.Sqrt((moveX * moveX) + (moveY * moveY));
            var dirX = moveX / len;
            var dirY = moveY / len;

            // 潜行 ~80 / 行走 ~180 / 疾跑 ~270 / 跳跃中 200（走）290（疾跑），单位 DIP/s
            double speed;
            if (Sneaking) speed = SneakSpeed;
            else if (isSprinting) speed = IsJumping ? JumpSprintSpeed : SprintSpeed;
            else speed = IsJumping ? JumpWalkSpeed : WalkSpeed;

            GroundX += dirX * speed * dt;
            GroundY += dirY * speed * dt;
            ClampToWorkArea(ctx);

            yaw += TurnToward(dirX, dirY, yaw, dt);
        }

        // Minecraft 风格起跳模拟
        if (spaceDown && !IsJumping)
        {
            IsJumping = true;
            _jumpVelocityY = JumpVelocity;
        }

        if (IsJumping)
        {
            JumpOffsetY += _jumpVelocityY * dt;
            _jumpVelocityY += Gravity * dt;

            if (JumpOffsetY >= 0)
            {
                JumpOffsetY = 0;
                _jumpVelocityY = 0;
                IsJumping = false;

                // 连跳检测（按住空格落地继续跳）
                if (spaceDown)
                {
                    IsJumping = true;
                    _jumpVelocityY = JumpVelocity;
                }
            }
        }

        PositionDirty = true;
    }

    /// <summary>跟随鼠标：朝光标走，进了停靠半径就停。</summary>
    private void StepFollowMouse(double dt, in PetMotionContext ctx, ref float yaw)
    {
        if (ctx.Cursor is not { } cursor) return;

        var (dx, dy, dist) = OffsetToFeet(ctx, cursor);
        if (dist <= FollowDockRadius)
        {
            Walking = false;
            Sprinting = false;
            return;
        }

        Walking = true;
        var isSprint = dist > FollowSprintDistance;
        Sprinting = isSprint;

        var dirX = dx / dist;
        var dirY = dy / dist;
        yaw += TurnToward(dirX, dirY, yaw, dt);

        var speed = isSprint ? SprintSpeed : WalkSpeed;
        var moveDist = Math.Min(speed * dt, dist - FollowDockRadius + 2.0);
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

        if (dist <= NavArriveDistance)
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
        var isSprint = dist > NavSprintDistance;
        Sprinting = isSprint;

        var dirX = dx / dist;
        var dirY = dy / dist;
        yaw += TurnToward(dirX, dirY, yaw, dt);

        var speed = isSprint ? SprintSpeed : WalkSpeed;
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
    private static float TurnToward(double dirX, double dirY, float yaw, double dt)
    {
        var targetYaw = (float)(Math.Atan2(dirX, dirY) * (180.0 / Math.PI));
        var diff = Normalize(targetYaw - yaw);
        var maxTurn = (float)(MoveTurnSpeed * dt);
        return Math.Clamp(diff * (float)MoveTurnGain * (float)dt, -maxTurn, maxTurn);
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>从桌宠脚底指向 <paramref name="target"/> 的向量与距离（DIP）。</summary>
    private (double Dx, double Dy, double Dist) OffsetToFeet(in PetMotionContext ctx, PetPoint target)
    {
        var feetX = GroundX + (ctx.Stage.Width / 2.0);
        var feetY = GroundY + ((FeetStageY + ctx.Stage.StageOffsetY) * ctx.Stage.Scale);
        var dx = target.X - feetX;
        var dy = target.Y - feetY;
        return (dx, dy, Math.Sqrt((dx * dx) + (dy * dy)));
    }

    /// <summary>把地面位置夹在屏幕工作区内，别让桌宠走出屏幕。</summary>
    private void ClampToWorkArea(in PetMotionContext ctx)
    {
        var area = ctx.WorkArea;
        GroundX = Math.Clamp(GroundX, area.X, area.X + area.Width - ctx.Stage.Width);
        GroundY = Math.Clamp(GroundY, area.Y, area.Y + area.Height - ctx.Stage.Height);
    }

    /// <summary>把角度归一化到 (-180, 180]。</summary>
    private static float Normalize(float degrees)
    {
        while (degrees > 180f) degrees -= 360f;
        while (degrees < -180f) degrees += 360f;
        return degrees;
    }
}
