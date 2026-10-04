using MinecraftSkinRender;

namespace NegiCraftLauncher.Raster.Rendering;

/// <summary>
/// 把「姿势 / 动画」这套数学从 <c>SkinRenderControl</c> 里拆出来，放进与 UI 框架无关的共享层。
///
/// <para>它只跟 <see cref="SkinRenderBase"/> 打交道（Tick / Rot / SetPos / 各部件旋转），
/// 所以 WPF 的启动器主页预览和 WPF 桌宠可以共用同一份；Avalonia 侧那份原样留在
/// <c>SkinRenderControl</c> 里不动 —— 它已经在跑，没有必要为一致性去动一个能用的东西。</para>
///
/// <para>数值全部照抄 Avalonia 版，改动任何一条都会让两个平台的姿态对不上：</para>
/// <list type="bullet">
/// <item>蹲下用 skinview3d 的 <c>|sin(k·π/2)|</c> 曲线，过渡 <c>SneakTransition</c> 秒。</item>
/// <item>待机呼吸 <c>0.02π + 0.03cos(2t)</c>，两条胳膊镜像外摆。</item>
/// <item>渲染器的旋转入参是"弧度 / 360"，所以度数要乘 <c>2π</c> 再喂进去。</item>
/// </list>
/// </summary>
public sealed class SkinPoseDriver
{
    // 渲染器的旋转输入先除以 360 再当弧度用。
    public const float RadToRotateInput = 360f;

    // skinview3d 的 CrouchAnimation，单位是皮肤像素；8 像素 = 渲染器里的 1 个模型单位。
    private const float CrouchBodyLean = 0.4537860552f;
    private const float CrouchBodyY = -2.103677462f;
    private const float CrouchBodyZ = -2.1244129f;
    private const float CrouchHeadY = -3.618325234674f;
    private const float CrouchArmLean = 0.410367746202f;
    private const float CrouchArmSplay = 0.1f;
    private const float CrouchArmY = -2.53943318f;
    private const float CrouchArmZ = 0.1682942f;
    private const float CrouchLegZ = -3.4500310377f;

    private const double SneakTransition = 0.12;
    private const double DangleTransition = 0.15;
    private const double WalkTransition = 0.08;
    private const double JumpTransition = 0.10;

    // 空手挥击在 Minecraft 里是 6 个游戏刻。
    private const double AttackDuration = 0.3;

    private readonly SkinRenderBase _renderer;

    private double _idleClock;
    private double _walkClock;
    private double _sneakK;
    private double _dangleK;
    private double _walkK;
    private double _jumpK;
    private double _attackT = 1.0;
    private bool _attackParked;

    private float _lastTargetPitchDeg;
    private float _lastTargetYawDeg;

    public SkinPoseDriver(SkinRenderBase renderer) => _renderer = renderer;

    /// <summary>模型当前朝向（度）。<c>RotateModel</c> 累加它，头部相对角由它算。</summary>
    public float CurrentYawDeg { get; private set; } = 24f;

    // 姿势 / 朝向被外部改过、但还没画出来的标记。与 IsAnimating 合起来就是"这一帧要不要重画"。
    private bool _dirty = true;

    private bool _sneaking;
    private bool _dangling;
    private bool _walking;
    private bool _jumping;
    private bool _sprinting;

    public bool Sneaking
    {
        get => _sneaking;
        set { if (_sneaking != value) { _sneaking = value; _dirty = true; } }
    }

    public bool Dangling
    {
        get => _dangling;
        set { if (_dangling != value) { _dangling = value; _dirty = true; } }
    }

    public bool Walking
    {
        get => _walking;
        set { if (_walking != value) { _walking = value; _dirty = true; } }
    }

    public bool Jumping
    {
        get => _jumping;
        set { if (_jumping != value) { _jumping = value; _dirty = true; } }
    }

    public bool Sprinting
    {
        get => _sprinting;
        set { if (_sprinting != value) { _sprinting = value; _dirty = true; } }
    }

    /// <summary>
    /// 这一帧需不需要重画？—— 动画还在跑（<see cref="IsAnimating"/>），或者姿势 / 朝向被外部改过
    /// （旋转、转头、蹲/走/跳/跑开关、挥击、被拎起来）。
    ///
    /// <para><b>宿主用它来"静止时停渲染"</b>：桌面上的桌宠大部分时间是不动的，让它每秒白跑
    /// 60 次软件光栅化没有意义 —— 这是计划 §9 的性能预算里那条"idle 占用：静止时 CPU ≈ 0"。
    /// 唯一的代价是待机呼吸会停在当前相位（幅度 ±1.7°、约 1 像素，看不出来）。</para>
    /// </summary>
    public bool NeedsRepaint => _dirty || IsAnimating;

    /// <summary>启动一次挥臂；传 <paramref name="parkAt"/> 则把动画定格在那个进度（0-1）。</summary>
    public void TriggerAttack(double? parkAt = null)
    {
        _attackT = parkAt ?? 0.0;
        _attackParked = parkAt.HasValue;
        _dirty = true;
    }

    /// <summary>把模型摆回初始姿态：位置归零、朝向复位。</summary>
    public void Reset()
    {
        _renderer.ResetPos();
        // 抬 0.3 个单位，头落在 Y≈36px、脚在 Y≈209px。
        _renderer.SetPos(0, 0.3f);
        _renderer.Rot(0, CurrentYawDeg * 2f * (float)Math.PI);
        UpdateHeadRotate();
        UpdatePose();
        _dirty = true;
    }

    /// <summary>绕竖直轴转 <paramref name="deltaYawDeg"/> 度。</summary>
    public void RotateModel(float deltaYawDeg)
    {
        CurrentYawDeg += deltaYawDeg;
        while (CurrentYawDeg > 180f) CurrentYawDeg -= 360f;
        while (CurrentYawDeg < -180f) CurrentYawDeg += 360f;

        _renderer.Rot(0, deltaYawDeg * 2f * (float)Math.PI);
        UpdateHeadRotate();
        _dirty = true;
    }

    /// <summary>头部看向某个方向（相对世界，度）。</summary>
    public void SetHeadLookAt(float targetPitchDeg, float targetYawDeg)
    {
        // 目标没变就什么都不做 —— 宿主的心跳是每 16ms 喂一次同样的值，
        // 不挡住的话脏标记会一直被点亮，"静止时停渲染"永远生效不了。
        if (_lastTargetPitchDeg == targetPitchDeg && _lastTargetYawDeg == targetYawDeg) return;

        _lastTargetPitchDeg = targetPitchDeg;
        _lastTargetYawDeg = targetYawDeg;
        UpdateHeadRotate();
        _dirty = true;
    }

    /// <summary>推进 <paramref name="dt"/> 秒，然后 Tick 渲染器并重算姿态。跑完即清掉脏标记。</summary>
    public void Update(double dt)
    {
        _idleClock += dt;

        _sneakK = Math.Clamp(_sneakK + ((Sneaking ? 1 : -1) * dt / SneakTransition), 0, 1);
        _dangleK = Math.Clamp(_dangleK + ((Dangling ? 1 : -1) * dt / DangleTransition), 0, 1);
        _walkK = Math.Clamp(_walkK + ((Walking ? 1 : -1) * dt / WalkTransition), 0, 1);

        if (_walkK > 0)
        {
            var walkFrequency = Sprinting ? 16.5 : 12.0 * (1.0 - 0.45 * _sneakK);
            var groundProgress = 1.0 - 0.85 * _jumpK;
            _walkClock += dt * walkFrequency * groundProgress;
        }

        _jumpK = Math.Clamp(_jumpK + ((Jumping ? 1 : -1) * dt / JumpTransition), 0, 1);
        if (_attackT < 1.0 && !_attackParked) _attackT = Math.Min(1.0, _attackT + dt / AttackDuration);

        _renderer.Tick(dt);
        UpdateHeadRotate();
        UpdatePose();
        _dirty = false;
    }

    /// <summary>
    /// 还有动画在跑吗？没有的话上层可以停下来省点 CPU。
    ///
    /// <para><b>与 Avalonia 侧 <c>SkinRenderControl.OnOpenGlRender</c> 末尾那段判据逐条一致</b>
    /// （那边是生产在跑的版本，这里是它的具名版）—— 两端的渲染节奏要对齐。
    /// 蹲下只在**过渡中**算"在动"：蹲稳之后姿势是定的，没必要继续重画。</para>
    ///
    /// <para>注意它**不含待机呼吸**（那是常驻的），所以"静止"是真的静止 ——
    /// 这也是为什么 <see cref="NeedsRepaint"/> 要另外记一份 <c>_dirty</c>。</para>
    /// </summary>
    public bool IsAnimating =>
        _dangleK > 0 || Dangling ||
        (_sneakK > 0 && _sneakK < 1) ||
        _walkK > 0 || Walking ||
        _jumpK > 0 || Jumping ||
        (_attackT < 1.0 && !_attackParked);

    private void UpdateHeadRotate()
    {
        // 相对脖子转角：headYaw = 目标朝向 - 身体朝向。
        var diffYaw = _lastTargetYawDeg - CurrentYawDeg;
        while (diffYaw > 180f) diffYaw -= 360f;
        while (diffYaw < -180f) diffYaw += 360f;

        var headYaw = Math.Clamp(diffYaw, -55f, 55f);
        var headPitch = Math.Clamp(_lastTargetPitchDeg, -25f, 25f);
        var headRoll = 0f;

        // 被拎起来的时候摇头抗议：约 16 rad/s、±28°，同时抬头看光标。
        var dk = (float)_dangleK;
        if (dk > 0)
        {
            var shakeYaw = (float)(Math.Sin(_idleClock * 16.0) * 28.0);
            var shakePitch = -18f + (float)(Math.Sin(_idleClock * 8.0) * 4.0);
            var shakeRoll = (float)(Math.Sin((_idleClock * 16.0) + (Math.PI / 4)) * 6.0);

            headYaw = ((1 - dk) * headYaw) + (dk * shakeYaw);
            headPitch = ((1 - dk) * headPitch) + (dk * shakePitch);
            headRoll = dk * shakeRoll;
        }

        _renderer.HeadRotate = new System.Numerics.Vector3(
            headRoll * 2.0f * (float)Math.PI,
            headPitch * 2.0f * (float)Math.PI,
            headYaw * 2.0f * (float)Math.PI);
    }

    private void UpdatePose()
    {
        // 蹲下用 |sin(k·π/2)| 过渡。
        var e = (float)Math.Sin(_sneakK * Math.PI / 2);
        var px = e / 8f;

        var dk = (float)_dangleK;
        var wk = (float)_walkK * (1.0f - dk);
        var jk = (float)_jumpK * (1.0f - dk);

        // 挥击：躯干先拧再回、手臂四次缓出前劈，1:1 复刻原版人形动作。
        float bodyTurn = 0f;
        float attackPitch = 0f;
        float attackYaw = 0f;
        float attackRoll = 0f;
        float attackBodyYaw = 0f;

        if (_attackT < 1.0)
        {
            var p = (float)_attackT;

            bodyTurn = -(float)Math.Sin(Math.Sqrt(p) * Math.PI * 2.0) * 0.18f;
            attackBodyYaw = bodyTurn * RadToRotateInput * (1 - dk);

            var f = 1.0f - p;
            f = 1.0f - (f * f * f * f);
            var f1 = (float)Math.Sin(f * Math.PI);
            var headPitchRad = _lastTargetPitchDeg * ((float)Math.PI / 180f);
            var f2 = (float)Math.Sin(p * Math.PI) * -(headPitchRad - 0.7f) * 0.75f;

            attackPitch = -(f1 * 1.25f + f2) * RadToRotateInput * (1 - dk);
            attackYaw = bodyTurn * 1.8f * RadToRotateInput * (1 - dk);
            attackRoll = (float)Math.Sin(p * Math.PI) * 0.40f * RadToRotateInput * (1 - dk);
        }

        var shoulderOffsetZ = (float)(0.75 * Math.Sin(bodyTurn)) * (1 - dk);

        _renderer.BodyPos = new System.Numerics.Vector3(0, CrouchBodyY * px, CrouchBodyZ * px);
        _renderer.HeadPos = new System.Numerics.Vector3(0, CrouchHeadY * px, 0);
        _renderer.LeftArmPos = new System.Numerics.Vector3(0, CrouchArmY * px, (CrouchArmZ * px) - shoulderOffsetZ);
        _renderer.RightArmPos = new System.Numerics.Vector3(0, CrouchArmY * px, (CrouchArmZ * px) + shoulderOffsetZ);
        _renderer.LeftLegPos = new System.Numerics.Vector3(0, 0, CrouchLegZ * px);
        _renderer.RightLegPos = _renderer.LeftLegPos;

        // 待机呼吸；蹲下时再加一份外摆并把胳膊往后带，让它们垂着而不是荡着。
        var idle = (float)((0.02 * Math.PI) + (0.03 * Math.Cos(2 * _idleClock))) * RadToRotateInput;
        var splay = idle + (CrouchArmSplay * e * RadToRotateInput);
        var armLean = CrouchArmLean * e * RadToRotateInput;

        // 走路 / 疾跑摆臂摆腿（腾空时压掉地面摆动）。
        // ⚠️ 这个值**不要再乘 RadToRotateInput** —— 它在下面 BodyRotate.Y 里已经乘过一次了。
        // 早先这里多乘了一次，导致疾跑前倾变成 0.07×360×360（≈25.2 rad = 4 整圈）：
        // 一按疾跑、一起跳，躯干就"唰"地转 4 圈再转回来，看着像胳膊旁边有东西一闪而过。
        var sprintBodyLean = Sprinting ? 0.07f * wk * (1.0f - jk) : 0f;
        var sprintFactor = Sprinting ? 1.25f : 1.0f;
        var airLimbFactor = 1.0f - (jk * 0.85f);
        var legSwing = (float)(Math.Sin(_walkClock) * 0.62 * RadToRotateInput) * wk * sprintFactor * airLimbFactor;
        var armSwing = (float)(Math.Sin(_walkClock) * 0.52 * RadToRotateInput) * wk * sprintFactor * airLimbFactor;
        var jumpLegSwing = 0.22f * RadToRotateInput * jk;

        var walkBob = (float)(Math.Abs(Math.Sin(_walkClock)) * 0.025) * wk * (1.0f - jk);
        _renderer.SetPos(0, 0.3f + (0.12f * dk) + walkBob);

        // 被拎起来：胳膊举过头顶去抓光标，还带点扑腾。
        var flutter = (float)(Math.Sin(_idleClock * 16.0) * 0.08 * RadToRotateInput);
        var dangleArmPitch = (-2.75f * RadToRotateInput) + flutter;
        var dangleArmLX = (-0.15f * RadToRotateInput) + (flutter * 0.2f);
        var dangleArmRX = (0.15f * RadToRotateInput) - (flutter * 0.2f);

        var normalArmLY = armLean + armSwing;
        var normalArmRY = armLean - armSwing;

        var finalArmLX = ((1 - dk) * splay) + (dk * dangleArmLX);
        var finalArmRX = ((1 - dk) * -splay) + (dk * dangleArmRX);
        var finalArmLY = ((1 - dk) * normalArmLY) + (dk * dangleArmPitch);
        var finalArmRY = ((1 - dk) * normalArmRY) + (dk * dangleArmPitch);

        var leftArmYaw = bodyTurn * 0.8f * RadToRotateInput * (1 - dk);

        _renderer.LeftArmRotate = new System.Numerics.Vector3(finalArmLX, finalArmLY, leftArmYaw);
        _renderer.RightArmRotate = new System.Numerics.Vector3(finalArmRX + attackRoll, finalArmRY + attackPitch, attackYaw);

        // 腿：走路摆动 + 起跳分开 + 被拎起来时乱蹬。
        var kick = (float)(Math.Sin(_idleClock * 13.5) * 0.65 * RadToRotateInput);
        var legSplay = 0.12f * RadToRotateInput;

        var leftLegY = ((1 - dk) * ((legSwing * (1 - (jk * 0.5f))) + jumpLegSwing)) + (kick * dk);
        var rightLegY = ((1 - dk) * ((-legSwing * (1 - (jk * 0.5f))) - jumpLegSwing)) - (kick * dk);

        _renderer.LeftLegRotate = new System.Numerics.Vector3(legSplay * dk, leftLegY, 0);
        _renderer.RightLegRotate = new System.Numerics.Vector3(-legSplay * dk, rightLegY, 0);

        var bodySwing = (float)(Math.Sin(_idleClock * 13.5) * 0.05 * RadToRotateInput);
        var walkBodyTilt = (float)(Math.Sin(_walkClock) * 0.03 * RadToRotateInput) * wk * (1.0f - jk);

        _renderer.BodyRotate = new System.Numerics.Vector3(
            0,
            ((CrouchBodyLean * e) - (0.06f * dk) + sprintBodyLean) * RadToRotateInput,
            (bodySwing * dk) + walkBodyTilt + attackBodyYaw);
    }
}
