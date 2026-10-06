using System.Numerics;
using MinecraftSkinRender;

namespace NegiCraftLauncher.Raster.Rendering;

/// <summary>
/// 软件光栅化的皮肤渲染器：WPF 侧的后端，替代上游的 <c>SkinRenderOpenGL</c>。
///
/// <para>继承 <see cref="SkinRenderBase"/> 而不是 <c>SkinRender</c> —— 后者持有
/// <c>SKBitmap</c>，而 WPF 迁移的全部意义就是甩掉 <c>libSkiaSharp</c>。
/// 位姿、动画、鼠标交互、画布尺寸都在基类里，两个后端共用同一份。</para>
///
/// <para><b>与 GL 后端的行为对齐点</b>（都照上游的绘制顺序来）：</para>
/// <list type="number">
/// <item>本体层<b>关混合</b>画（上游 <c>gl.Disable(GL_BLEND)</c>）：纹素 alpha 被忽略，原样覆盖。</item>
/// <item>第二层<b>开混合且不写深度</b>（上游 <c>DepthMask(false)</c> + <c>SRC_ALPHA</c>），
/// 否则那层自己的背面会把正面挡掉。</item>
/// <item>MVP = <c>self(部件) × model × view × proj</c>，对应 GLSL 里的
/// <c>projection * view * model * self * v</c>。</item>
/// <item>老皮肤（64x32）的 UV 的 V 按 32 归一化，所以采样纹理高度传 32。</item>
/// </list>
/// </summary>
public sealed class SkinRenderSoftware : SkinRenderBase
{
    private readonly SoftwareRenderer _renderer = new();

    private readonly List<(ModelPartType Part, SkinQuad[] Quads)> _baseLayer = [];
    private readonly List<(ModelPartType Part, SkinQuad[] Quads)> _topLayer = [];

    private uint[] _skinPixels = [];
    private int _textureWidth = 64;
    private int _textureHeight = 64;

    /// <summary>超采样倍数。2 表示按 2× 尺寸光栅化再降采样（实测只花 0.35ms）。</summary>
    public int SampleScale { get; set; } = 2;

    // 这里**刻意不做光照**，是有意为之：
    // 上游片元着色器本来会把每个纹素乘上 (0.15 + 漫反射)，但 Avalonia 的 GL 后端
    // 在 ShaderSource 里把那行替换成 vec3(1.0) 把光照抹掉了
    // （见 src/NegiCraftLauncher.Skin.Avalonia/Controls/OpenGL/AvaloniaApi.cs 的 Unlit()）。
    // 也就是说现行版本的皮肤预览本来就是平的。软件后端跟着平，两个平台才一致。
    // 实测：与 GL 截图对齐后，无光照平均差 R4.3/G4.1/B9.7，加光照反而涨到 R6.0/G9.0/B11.6。

    public SkinRenderSoftware()
    {
        Info = "Software rasterizer (NegiCraft)";
        BackColor = Vector4.Zero;
    }

    /// <summary>
    /// 设置皮肤贴图。<paramref name="explicitType"/> 给了就用它，否则按贴图尺寸自己判 ——
    /// 只看尺寸，别信 <c>IsSlim</c>（64x32 一律是 <see cref="SkinType.Old"/>）。
    /// </summary>
    public void SetSkin(SkinTexture texture, SkinType? explicitType = null)
    {
        var type = explicitType is { } t && t != SkinType.Unkonw
            ? t
            : ToLibType(texture.ResolveFormat(texture.IsSlimSkin()));

        if (type == SkinType.Unkonw)
        {
            HaveSkin = false;
            OnErrorChange(ErrorType.UnknowSkinType);
            return;
        }

        _skinPixels = texture.ToRenderPixels();
        _textureWidth = 64;
        // 老皮肤只有 32 行，且它的 UV 就是按 32 归一化的。
        _textureHeight = texture.IsLegacy ? 32 : 64;

        _skinType = type;
        _skina.SkinType = type;
        _switchModel = true;
        HaveSkin = true;

        // 老皮肤（64x32）**没有第二层覆盖贴图**。GL 后端在 SkinRenderControl.ApplySkinType
        // 里就是这条规则（`_skin.EnableTop = type != SkinType.Old`），软件后端必须照做。
        //
        // 不照做的后果：lib 的 GetSteveTop(Old) 会返回一个**放大的头**（enlarge 1.125），
        // 而 GetSteveTextureTop(Old) 的头 UV 落在 (32..64, 0..16) 那片 —— 在老皮肤布局里
        // 那块地方根本没定义。如果那张 PNG 在那一带不是全透明，就会在头顶套一个
        // 采错纹理的大方块；透明时看不出，所以这个 bug 是隐性的。
        EnableTop = type != SkinType.Old;
    }

    /// <summary>清掉皮肤（例如账号登出）。</summary>
    public void ClearSkin()
    {
        _skinPixels = [];
        HaveSkin = false;
        _switchModel = true;
    }

    /// <summary>
    /// 渲染一帧到 <paramref name="target"/>（尺寸须等于 <see cref="SkinRenderBase.Width"/> ×
    /// <see cref="SkinRenderBase.Height"/>）。调用前先 <see cref="SkinRenderBase.Tick"/>。
    /// </summary>
    public void RenderTo(PixelBuffer target)
    {
        if (!HaveSkin) return;
        if (Width <= 0 || Height <= 0) return;

        if (_switchModel)
        {
            BuildLayers();
            _switchModel = false;
        }

        _renderer.Configure(Width, Height, SampleScale);
        _renderer.Begin(ToArgb(_backColor));

        var model = GetMatrix4(ModelPartType.Model);
        var view = GetMatrix4(ModelPartType.View);
        var projection = GetMatrix4(ModelPartType.Proj);

        var viewportWidth = _renderer.InternalWidth;
        var viewportHeight = _renderer.InternalHeight;

        // 本体：不混合、写深度。
        DrawLayer(_baseLayer, model, view, projection, viewportWidth, viewportHeight,
            blend: false, depthWrite: true);

        // 第二层（帽子/外套）：混合、只测深度不写。
        if (_enableTop)
        {
            DrawLayer(_topLayer, model, view, projection, viewportWidth, viewportHeight,
                blend: true, depthWrite: false);
        }

        _renderer.ResolveTo(target);
    }

    private void DrawLayer(
        List<(ModelPartType Part, SkinQuad[] Quads)> layer,
        in Matrix4x4 model, in Matrix4x4 view, in Matrix4x4 projection,
        float viewportWidth, float viewportHeight,
        bool blend, bool depthWrite)
    {
        foreach (var (part, quads) in layer)
        {
            var mvp = GetMatrix4(part) * model * view * projection;

            // 躯干与整段四肢支持逐顶点自由形变：
            // 躯干沿高度扭转，四肢在关节处做平滑连续弯曲。
            var spine = part == ModelPartType.Body && SpineFlexible;
            var limb = LimbFlexible && (part == ModelPartType.LeftArm || part == ModelPartType.RightArm ||
                                       part == ModelPartType.LeftLeg || part == ModelPartType.RightLeg);

            foreach (var quad in quads)
            {
                var p0 = quad.P0;
                var p1 = quad.P1;
                var p2 = quad.P2;
                var p3 = quad.P3;

                if (spine)
                {
                    p0 = SpinePoint(p0);
                    p1 = SpinePoint(p1);
                    p2 = SpinePoint(p2);
                    p3 = SpinePoint(p3);
                }
                else if (limb)
                {
                    p0 = LimbPoint(part, p0);
                    p1 = LimbPoint(part, p1);
                    p2 = LimbPoint(part, p2);
                    p3 = LimbPoint(part, p3);
                }

                var a = Projector.Project(mvp, p0.X, p0.Y, p0.Z, quad.T0.X, quad.T0.Y, viewportWidth, viewportHeight);
                var b = Projector.Project(mvp, p1.X, p1.Y, p1.Z, quad.T1.X, quad.T1.Y, viewportWidth, viewportHeight);
                var c = Projector.Project(mvp, p2.X, p2.Y, p2.Z, quad.T2.X, quad.T2.Y, viewportWidth, viewportHeight);
                var d = Projector.Project(mvp, p3.X, p3.Y, p3.Z, quad.T3.X, quad.T3.Y, viewportWidth, viewportHeight);

                _renderer.DrawQuad(a, b, c, d, _skinPixels, _textureWidth, _textureHeight, blend, depthWrite);
            }
        }
    }

    private void BuildLayers()
    {
        _baseLayer.Clear();
        _topLayer.Clear();

        var spineSegs = SpineFlexible ? SpineSegments : 1;
        var limbSegs = LimbFlexible ? LimbSegments : 1;

        foreach (var part in SkinModel.Build(_skinType, top: false, LimbJoints, spineSegs, limbSegs))
        {
            // 披风用的是**另一张贴图**（不是皮肤），这里还没做披风支持。
            // 不跳过的话它会拿皮肤贴图去采样 (0..22, 1..17) 那一片头部区域，
            // 在身后糊出一大块棕色板子 —— 正面被本体挡住看不出来，一转 180° 就露馅。
            if (part.Type == ModelPartType.Cape) continue;

            _baseLayer.Add((part.Type, ToQuads(part)));
        }

        // 老皮肤（64x32）的贴图里只有头部那层覆盖，Build 会如实只返回头 —— 不用特判。
        foreach (var part in SkinModel.Build(_skinType, top: true, LimbJoints, spineSegs, limbSegs))
        {
            _topLayer.Add((part.Type, ToQuads(part)));
        }
    }

    private static SkinQuad[] ToQuads(SkinPart part)
    {
        var quads = new List<SkinQuad>(6);
        SkinModel.AppendQuads(part, quads);
        return [.. quads];
    }

    private static SkinType ToLibType(SkinFormat format) => format switch
    {
        SkinFormat.Old => SkinType.Old,
        SkinFormat.New => SkinType.New,
        SkinFormat.NewSlim => SkinType.NewSlim,
        _ => SkinType.Unkonw,
    };

    /// <summary>
    /// 诊断用：把一帧真正用到的矩阵取出来。视觉对不上时先看这个 ——
    /// "矩阵错了"和"UV 错了"在截图上长得挺像，但修法完全不同。
    /// </summary>
    public (Matrix4x4 Model, Matrix4x4 View, Matrix4x4 Projection, Matrix4x4 Head, Matrix4x4 LeftArm) DebugMatrices() =>
        (GetMatrix4(ModelPartType.Model), GetMatrix4(ModelPartType.View), GetMatrix4(ModelPartType.Proj),
         GetMatrix4(ModelPartType.Head), GetMatrix4(ModelPartType.LeftArm));

    /// <summary>GL 的 <c>ClearColor(r, g, b, a)</c> 对应 <c>_backColor</c> 的 X/Y/Z/W。</summary>
    private static uint ToArgb(Vector4 color) =>
        ((uint)Channel(color.W) << 24) |
        ((uint)Channel(color.X) << 16) |
        ((uint)Channel(color.Y) << 8) |
        (uint)Channel(color.Z);

    private static int Channel(float value) => (int)Math.Clamp(value * 255f + 0.5f, 0f, 255f);
}
