using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using MinecraftSkinRender;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.Raster.Rendering;

namespace NegiCraftLauncher.Skin.Wpf.Rendering;

/// <summary>
/// <b>GPU 后端</b>：继承 <see cref="SkinRenderBase"/>，但不光栅化任何东西 ——
/// 它只把「模型几何 + 每部件矩阵 + 贴图」准备好，交给 <see cref="SkinGpuViewport"/>
/// 用 WPF 的 <c>Viewport3D</c> 走 D3D 硬件管线画。
///
/// <para><b>为什么能这么干</b>：<c>SkinRenderBase</c> 里那半份（位姿数学、画布尺寸、皮肤类型）
/// 是跟图形 API 无关的，<see cref="Raster.Rendering.SkinRenderSoftware"/> 和本类各取所需。
/// 于是 <c>SkinPoseDriver</c> 一行都不用改就能驱动 GPU 后端 —— 姿势天然与软件后端逐条一致。</para>
///
/// <para><b>与软件后端刻意保持一致的三条</b>（不一致就会两套画面对不上）：</para>
/// <list type="number">
/// <item>老皮肤（64x32）关掉第二层（<c>EnableTop = type != Old</c>）—— 理由见
/// <see cref="Raster.Rendering.SkinRenderSoftware.SetSkin"/> 的长注释。</item>
/// <item>披风不画（没有披风贴图，硬画会在身后糊一块棕色板子）。</item>
/// <item>贴图高度：老皮肤 32、新皮肤 64（UV 的 V 就是按各自高度归一化的）。</item>
/// </list>
///
/// <para><b>本体层 vs 第二层的 alpha</b>：软件后端里本体层是关着混合画的（纹素 alpha 被忽略），
/// 第二层才做 src-over。所以这里也准备<b>两张贴图</b>：本体那张强制 A=255，第二层那张保留 alpha。</para>
/// </summary>
public sealed class SkinRenderGpu : SkinRenderBase
{
    private readonly List<(ModelPartType Part, MeshGeometry3D Mesh)> _baseLayer = [];
    private readonly List<(ModelPartType Part, MeshGeometry3D Mesh)> _topLayer = [];

    private uint[] _skinPixels = [];
    private int _textureWidth = 64;
    private int _textureHeight = 64;
    private bool _modelBuilt;

    /// <summary>
    /// 贴图的最近邻预放大倍数。
    ///
    /// <para><b>为什么要它</b>：WPF 的 3D 纹理采样固定是线性过滤，没有任何 API 能改成点采样
    /// （<c>RenderOptions.BitmapScalingMode</c> 只管 2D）。直接拿 64x64 的皮肤上去，
    /// 像素画的边缘会被糊掉。先把贴图按整数倍最近邻放大，每个"逻辑纹素"就占了 N 个真实纹素，
    /// 线性过滤的模糊半径（1 个真实纹素）相对逻辑纹素就缩到 1/N —— 看上去就锐了。</para>
    ///
    /// <para>2 倍已经够：模型在屏上约 110 DIP 宽，一个逻辑纹素本来就占 ~1.5 个屏幕像素，
    /// 再放大 2 倍就是"放大"而不是"缩小"，没有 mipmap 也不会闪。</para>
    /// </summary>
    public int TextureUpscale { get; set; } = 2;

    /// <summary>模型版本号。贴图 / 模型类型 / 第二层开关变了就 +1，视图层据此重建可视树。</summary>
    public int ModelVersion { get; private set; }

    public IReadOnlyList<(ModelPartType Part, MeshGeometry3D Mesh)> BaseLayer => _baseLayer;

    public IReadOnlyList<(ModelPartType Part, MeshGeometry3D Mesh)> TopLayer => _topLayer;

    /// <summary>本体层贴图（A 已被强制成 255）。</summary>
    public ImageSource? BaseTexture { get; private set; }

    /// <summary>第二层贴图（保留原始 alpha）。</summary>
    public ImageSource? TopTexture { get; private set; }

    public SkinRenderGpu()
    {
        Info = "WPF Viewport3D (NegiCraft)";
        BackColor = Vector4.Zero;
    }

    /// <summary>设置皮肤贴图。语义与 <see cref="Raster.Rendering.SkinRenderSoftware.SetSkin"/> 完全一致。</summary>
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

        // 老皮肤没有第二层 —— 与 GL 后端 SkinRenderControl.ApplySkinType 同一条规则。
        EnableTop = type != SkinType.Old;

        BaseTexture = ToTexture(_skinPixels, _textureWidth, _textureHeight, forceOpaque: true, TextureUpscale);
        TopTexture = ToTexture(_skinPixels, _textureWidth, _textureHeight, forceOpaque: false, TextureUpscale);

        _modelBuilt = false;
        ModelVersion++;
    }

    /// <summary>清掉皮肤（例如账号登出）。</summary>
    public void ClearSkin()
    {
        _skinPixels = [];
        HaveSkin = false;
        _switchModel = true;
        BaseTexture = null;
        TopTexture = null;
        _modelBuilt = false;
        ModelVersion++;
    }

    /// <summary>按需建几何。便宜（12 个盒子），但也没必要每帧建。</summary>
    public void EnsureModel()
    {
        if (_modelBuilt) return;

        _baseLayer.Clear();
        _topLayer.Clear();

        foreach (var part in SkinModel.Build(_skinType, top: false))
        {
            // 披风用的是另一张贴图，这里没有 —— 不跳过会在身后糊一块棕色板子。
            if (part.Type == ModelPartType.Cape) continue;
            _baseLayer.Add((part.Type, BuildMesh(part)));
        }

        foreach (var part in SkinModel.Build(_skinType, top: true))
        {
            _topLayer.Add((part.Type, BuildMesh(part)));
        }

        _modelBuilt = true;
    }

    /// <summary>
    /// 取某个部件这一帧的位姿矩阵。<c>GetMatrix4</c> 在基类里是 <c>protected</c>，
    /// 视图层要读，所以在这里开个口子。
    /// </summary>
    public Matrix4x4 MatrixOf(ModelPartType type) => GetMatrix4(type);

    /// <summary>
    /// 把 <see cref="SkinQuad"/> 拼成 WPF 的 <see cref="MeshGeometry3D"/>。
    ///
    /// <para><b>绕序</b>：<see cref="SkinModel.AppendQuads"/> 给的是"从盒外看 左上→右上→右下→左下"，
    /// 在 Y 向上的坐标系里是<b>顺时针</b>；而 WPF 3D 的正面是<b>逆时针</b>。
    /// 所以索引要反着发（<c>0,2,1</c> / <c>0,3,2</c>）—— 发错的表现是模型里外翻转：
    /// 看到的是盒子远端那一面、左右镜像，而且第二层永远被本体挡住。</para>
    /// </summary>
    private static MeshGeometry3D BuildMesh(SkinPart part)
    {
        var quads = new List<SkinQuad>(6);
        SkinModel.AppendQuads(part, quads);

        var positions = new Point3DCollection(quads.Count * 4);
        var texCoords = new PointCollection(quads.Count * 4);
        var indices = new Int32Collection(quads.Count * 6);

        foreach (var q in quads)
        {
            var b = positions.Count;

            positions.Add(new Point3D(q.P0.X, q.P0.Y, q.P0.Z));
            positions.Add(new Point3D(q.P1.X, q.P1.Y, q.P1.Z));
            positions.Add(new Point3D(q.P2.X, q.P2.Y, q.P2.Z));
            positions.Add(new Point3D(q.P3.X, q.P3.Y, q.P3.Z));

            texCoords.Add(new Point(q.T0.X, q.T0.Y));
            texCoords.Add(new Point(q.T1.X, q.T1.Y));
            texCoords.Add(new Point(q.T2.X, q.T2.Y));
            texCoords.Add(new Point(q.T3.X, q.T3.Y));

            // 反向绕序：逆时针朝外。
            indices.Add(b + 0); indices.Add(b + 2); indices.Add(b + 1);
            indices.Add(b + 0); indices.Add(b + 3); indices.Add(b + 2);
        }

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            TextureCoordinates = texCoords,
            TriangleIndices = indices,
        };
        mesh.Freeze();
        return mesh;
    }

    /// <summary>
    /// 直通 ARGB → 预乘 Pbgra32 的 <see cref="BitmapSource"/>，顺带做整数倍最近邻放大。
    ///
    /// <para>WPF 的 <c>Pbgra32</c> 是<b>预乘</b>的（和 <see cref="PixelBuffer"/> 同一套约定），
    /// 而 <see cref="SkinTexture"/> 给的是直通 alpha，所以这里要乘一次。</para>
    /// </summary>
    private static BitmapSource ToTexture(
        uint[] pixels, int width, int height, bool forceOpaque, int upscale)
    {
        upscale = Math.Max(1, upscale);

        var w = width * upscale;
        var h = height * upscale;
        var stride = w * 4;
        var data = new byte[stride * h];

        for (var y = 0; y < h; y++)
        {
            var sy = y / upscale;
            for (var x = 0; x < w; x++)
            {
                var c = pixels[sy * width + (x / upscale)];

                var a = forceOpaque ? 255u : (c >> 24) & 0xFF;
                var r = (c >> 16) & 0xFF;
                var g = (c >> 8) & 0xFF;
                var b = c & 0xFF;

                if (a != 255)
                {
                    r = (r * a + 127) / 255;
                    g = (g * a + 127) / 255;
                    b = (b * a + 127) / 255;
                }

                var o = (y * stride) + (x * 4);
                data[o] = (byte)b;
                data[o + 1] = (byte)g;
                data[o + 2] = (byte)r;
                data[o + 3] = (byte)a;
            }
        }

        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, data, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static SkinType ToLibType(SkinFormat format) => format switch
    {
        SkinFormat.Old => SkinType.Old,
        SkinFormat.New => SkinType.New,
        SkinFormat.NewSlim => SkinType.NewSlim,
        _ => SkinType.Unkonw,
    };
}
