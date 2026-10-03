using System.Numerics;

namespace MinecraftSkinRender;

/// <summary>
/// 渲染器的<b>与图形 API 无关</b>的那一半：画布尺寸、皮肤类型、背景色、鼠标交互、位姿数学、
/// 内置走路动画的驱动。后端（OpenGL / 软件光栅化）继承它，自己负责"把三角形画到哪去"。
///
/// <para><b>为什么单独拆一层</b>：NegiCraft 有两个渲染后端 —— Windows 走 WPF，用自写软件光栅化
/// （不能有 <c>libSkiaSharp</c>）；macOS/Linux 走 Avalonia，用上游的 OpenGL 后端。
/// 而 <see cref="GetMatrix4"/> 里的位姿数学是 fork 自己改过的（手臂/腿各自独立旋转、蹲下折骨架、
/// 关节挪到 MC 的位置）。这份数学一旦有两份，两个后端就会慢慢视觉漂移。
/// 所以把它放在零依赖的 <c>MinecraftSkinRender.Core</c> 里，两边共用同一份。</para>
///
/// <para>上游的 <c>SkinRender</c> 把位姿逻辑和 <c>SKBitmap</c> 贴图字段混在一个类里，
/// 这里按"是否依赖 SkiaSharp"切开：本类不含任何 SkiaSharp 类型；
/// <c>SkinRender</c>（Skia 侧）只负责贴图字段与 <c>SetSkinTex</c> / <c>SetCapeTex</c>。</para>
/// </summary>
public abstract class SkinRenderBase
{
    protected bool _enableCape;
    protected bool _enableTop;
    protected bool _switchModel;
    protected bool _switchSkin;
    protected bool _switchType;
    protected bool _switchBack;
    protected bool _animation;

    protected SkinRenderType _renderType;
    protected Vector4 _backColor;
    protected SkinType _skinType = SkinType.Unkonw;

    protected double _time;

    protected float _dis = 1;

    protected int _fps;

    protected Vector2 _rotXY;
    protected Vector2 _diffXY;

    protected Vector2 _xy;
    protected Vector2 _saveXY;
    protected Vector2 _lastXY;

    protected Matrix4x4 _last;

    protected readonly SkinAnimation _skina;

    /// <summary>
    /// 渲染出错
    /// </summary>
    public event Action<object?, ErrorType>? Error;
    /// <summary>
    /// 渲染状态改变
    /// </summary>
    public event Action<object?, StateType>? State;

    /// <summary>
    /// 是否存在披风
    /// </summary>
    public bool HaveCape { get; protected set; }
    /// <summary>
    /// 是否存在皮肤
    /// </summary>
    public bool HaveSkin { get; protected set; }

    /// <summary>
    /// 画布宽度
    /// </summary>
    public int Width { get; set; }
    /// <summary>
    /// 画布高度
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// 渲染器信息
    /// </summary>
    public string Info { get; protected set; }

    /// <summary>
    /// 模型动画
    /// </summary>
    public bool Animation
    {
        get { return _animation; }
        set
        {
            if (value)
            {
                _skina.Run = true;
            }
            else
            {
                _skina.Run = false;
            }

            _animation = value;
        }
    }

    /// <summary>
    /// 皮肤类型
    /// </summary>
    public SkinType SkinType
    {
        get { return _skinType; }
        set
        {
            _skinType = value;
            _skina.SkinType = value;
            _switchModel = true;
        }
    }
    /// <summary>
    /// 背景色
    /// </summary>
    public Vector4 BackColor
    {
        get { return _backColor; }
        set
        {
            _backColor = value;
            _switchBack = true;
        }
    }

    /// <summary>
    /// 渲染类型
    /// </summary>
    public SkinRenderType RenderType
    {
        get { return _renderType; }
        set
        {
            _renderType = value;
            _switchType = true;
        }
    }

    /// <summary>
    /// 是否启用披风渲染
    /// </summary>
    public bool EnableCape
    {
        get { return _enableCape; }
        set
        {
            _enableCape = value;
            _switchType = true;
        }
    }
    /// <summary>
    /// 是否启用第二层渲染
    /// </summary>
    public bool EnableTop
    {
        get { return _enableTop; }
        set
        {
            _enableTop = value;
            _switchType = true;
        }
    }

    // NegiCraft fork. Poses such as the Minecraft crouch move both arms (or both legs) the same
    // way, which upstream could not express: it drove both sides from one shared vector and
    // negated it for the right side, so only an antiphase walk swing was reachable. Each side
    // is now its own input, and every part also takes an offset.
    //
    // Rotation keeps upstream's convention: X rolls about Z, Y pitches about X, Z yaws about Y,
    // and the value is divided by 360 before being used as radians. Offsets are model units
    // (8 skin pixels = 1 unit) with +Y up and +Z toward the viewer.

    public Vector3 HeadRotate { get; set; }
    public Vector3 LeftArmRotate { get; set; }
    public Vector3 RightArmRotate { get; set; }
    public Vector3 LeftLegRotate { get; set; }
    public Vector3 RightLegRotate { get; set; }
    public Vector3 BodyRotate { get; set; }

    public Vector3 BodyPos { get; set; }
    public Vector3 HeadPos { get; set; }
    public Vector3 LeftArmPos { get; set; }
    public Vector3 RightArmPos { get; set; }
    public Vector3 LeftLegPos { get; set; }
    public Vector3 RightLegPos { get; set; }

    /// <summary>
    /// FPS刷新
    /// </summary>
    public event Action<object?, int>? FpsUpdate;

    protected SkinRenderBase()
    {
        _skina = new();
        _last = Matrix4x4.Identity;
    }

    /// <summary>
    /// 鼠标按下
    /// </summary>
    /// <param name="type"></param>
    /// <param name="point"></param>
    public void PointerPressed(KeyType type, Vector2 point)
    {
        if (type == KeyType.Left)
        {
            _diffXY.X = point.X;
            _diffXY.Y = -point.Y;
        }
        else if (type == KeyType.Right)
        {
            _lastXY.X = point.X;
            _lastXY.Y = point.Y;
        }
    }

    /// <summary>
    /// 鼠标松开
    /// </summary>
    /// <param name="type"></param>
    /// <param name="point"></param>
    public void PointerReleased(KeyType type, Vector2 point)
    {
        if (type == KeyType.Right)
        {
            _saveXY.X = _xy.X;
            _saveXY.Y = _xy.Y;
        }
    }

    /// <summary>
    /// 鼠标移动
    /// </summary>
    /// <param name="type"></param>
    /// <param name="point"></param>
    public void PointerMoved(KeyType type, Vector2 point)
    {
        if (type == KeyType.Left)
        {
            _rotXY.Y = point.X - _diffXY.X;
            _rotXY.X = point.Y + _diffXY.Y;
            _rotXY.Y *= 2;
            _rotXY.X *= 2;
            _diffXY.X = point.X;
            _diffXY.Y = -point.Y;
        }
        else if (type == KeyType.Right)
        {
            _xy.X = -(_lastXY.X - point.X) / 100 + _saveXY.X;
            _xy.Y = (_lastXY.Y - point.Y) / 100 + _saveXY.Y;
        }
    }

    /// <summary>
    /// 滚轮
    /// </summary>
    /// <param name="ispost"></param>
    public void PointerWheelChanged(bool ispost)
    {
        if (ispost)
        {
            _dis += 0.1f;
        }
        else
        {
            _dis -= 0.1f;
        }
    }

    /// <summary>
    /// 旋转
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    public void Rot(float x, float y)
    {
        _rotXY.X += x;
        _rotXY.Y += y;
    }

    /// <summary>
    /// 移动
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    public void Pos(float x, float y)
    {
        _xy.X += x;
        _xy.Y += y;
    }

    /// <summary>
    /// 设置绝对坐标
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    public void SetPos(float x, float y)
    {
        _xy.X = x;
        _xy.Y = y;
    }

    /// <summary>
    /// 缩放
    /// </summary>
    /// <param name="x"></param>
    public void AddDis(float x)
    {
        _dis += x;
    }

    /// <summary>
    /// 重置模型
    /// </summary>
    public void ResetPos()
    {
        _dis = 1;
        _diffXY.X = 0;
        _diffXY.Y = 0;
        _xy.X = 0;
        _xy.Y = 0;
        _saveXY.X = 0;
        _saveXY.Y = 0;
        _lastXY.X = 0;
        _lastXY.Y = 0;
        _last = Matrix4x4.Identity;
    }

    /// <summary>
    /// 模型逻辑
    /// </summary>
    /// <param name="time"></param>
    public void Tick(double time)
    {
        if (_animation)
        {
            _skina.Tick(time);
        }

        if (_rotXY.X != 0 || _rotXY.Y != 0)
        {
            _last *= Matrix4x4.CreateRotationX(_rotXY.X / 360)
                    * Matrix4x4.CreateRotationY(_rotXY.Y / 360);
            _rotXY.X = 0;
            _rotXY.Y = 0;
        }

        _fps++;
        _time += time;
        if (_time > 1)
        {
            _time -= 1;
            FpsUpdate?.Invoke(this, _fps);
            _fps = 0;
        }
    }

    protected void OnErrorChange(ErrorType data)
    {
        Error?.Invoke(this, data);
    }

    protected void OnStateChange(StateType data)
    {
        State?.Invoke(this, data);
    }

    protected Matrix4x4 GetMatrix4(ModelPartType type)
    {
        var value = _skinType == SkinType.NewSlim ? 1.375f : 1.5f;
        bool enable = _animation;

        // The built-in walk animation drives both sides from one mirrored vector; manual posing
        // drives each side on its own.
        var head = enable ? _skina.Head : HeadRotate;
        var leftArm = enable ? _skina.Arm : LeftArmRotate;
        var rightArm = enable ? -_skina.Arm : RightArmRotate;
        var leftLeg = enable ? _skina.Leg : LeftLegRotate;
        var rightLeg = enable ? -_skina.Leg : RightLegRotate;

        // Part geometry from Steve3DModel is centred on its own origin. Each matrix places that
        // centre at `rest`, rotates about Minecraft's joint (`pivot`, measured from the centre),
        // and then shifts the whole part by its pose offset. Upstream pivoted the limbs at their
        // free end and put the pose offset before the rotation, which made a fold read as a fall.
        var armPivotX = value * CubeModel.Value;

        return type switch
        {
            ModelPartType.Body => Pose(Vector3.Zero, Vector3.Zero, BodyRotate, BodyPos),
            ModelPartType.Head => Pose(
              new Vector3(0, CubeModel.Value * 2.5f, 0),
              new Vector3(0, -CubeModel.Value, 0), head, HeadPos),
            ModelPartType.LeftArm => Pose(
              new Vector3(armPivotX, 0, 0),
              new Vector3(0, CubeModel.Value, 0), leftArm, LeftArmPos),
            ModelPartType.RightArm => Pose(
              new Vector3(-armPivotX, 0, 0),
              new Vector3(0, CubeModel.Value, 0), rightArm, RightArmPos),
            ModelPartType.LeftLeg => Pose(
              new Vector3(CubeModel.Value * 0.5f, -CubeModel.Value * 3f, 0),
              new Vector3(0, CubeModel.Value * 1.5f, 0), leftLeg, LeftLegPos),
            ModelPartType.RightLeg => Pose(
              new Vector3(-CubeModel.Value * 0.5f, -CubeModel.Value * 3f, 0),
              new Vector3(0, CubeModel.Value * 1.5f, 0), rightLeg, RightLegPos),
            ModelPartType.Proj => Matrix4x4.CreatePerspectiveFieldOfView(
              (float)(Math.PI / 4), (float)Width / Height, 0.1f, 10.0f),
            ModelPartType.View => Matrix4x4.CreateLookAt(new(0, 0, 7), new(), new(0, 1, 0)),
            ModelPartType.Model => _last
            * Matrix4x4.CreateTranslation(new(_xy.X, _xy.Y, 0))
            * Matrix4x4.CreateScale(_dis),
            ModelPartType.Cape => Matrix4x4.CreateTranslation(0, -2f * CubeModel.Value, -CubeModel.Value * 0.1f) *
               Matrix4x4.CreateRotationX((float)((enable ? 11.8 + _skina.Cape : 6.3) * Math.PI / 180)) *
               Matrix4x4.CreateTranslation(0, 1.6f * CubeModel.Value, -CubeModel.Value * 0.5f),
            _ => Matrix4x4.Identity
        };
    }

    // Matrices are applied left to right (System.Numerics row-vector order, uploaded to GLSL as
    // `M * v`), so this reads: move the geometry so its joint sits at the origin, rotate, then
    // put the joint where the part belongs.
    private static Matrix4x4 Pose(Vector3 rest, Vector3 pivot, Vector3 rotate, Vector3 offset) =>
        Matrix4x4.CreateTranslation(-pivot) *
        Rotation(rotate) *
        Matrix4x4.CreateTranslation(pivot + rest + offset);

    private static Matrix4x4 Rotation(Vector3 rotate) =>
        Matrix4x4.CreateRotationZ(rotate.X / 360) *
        Matrix4x4.CreateRotationX(rotate.Y / 360) *
        Matrix4x4.CreateRotationY(rotate.Z / 360);
}
