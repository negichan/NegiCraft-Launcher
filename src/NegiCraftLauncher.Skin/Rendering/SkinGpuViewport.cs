using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using MinecraftSkinRender;

namespace NegiCraftLauncher.Skin.Rendering;

/// <summary>
/// 把 <see cref="SkinRenderGpu"/> 的几何 + 位姿矩阵搬进一棵 WPF <see cref="Viewport3D"/>。
///
/// <para><b>结构</b>：一个根 <see cref="ModelVisual3D"/>，每个身体部件一个子
/// <see cref="GeometryModel3D"/>（本体层一份、第二层一份）。每帧只改各部件的
/// <see cref="MatrixTransform3D"/> 与相机矩阵 —— 真正的光栅化在 GPU 上，CPU 这边
/// 一帧只有十几次矩阵乘法。</para>
///
/// <para><b>材质用 <c>DiffuseMaterial</c> + 一盏白色 <c>AmbientLight</c></b>。
/// 这条路是<b>刻意不做光照</b>的（GL 侧 <c>Unlit()</c> 把 ambient+diffuse 换成了
/// <c>vec3(1.0)</c>，软件后端跟着平）—— 场景里只放环境光、不放任何方向光/点光，
/// 于是 <c>DiffuseMaterial</c> 的输出就恒等于贴图颜色本身，正好是平光。</para>
///
/// <para><b>为什么不用 <c>EmissiveMaterial</c></b>（那是更"字面"的平光写法）：
/// 这台机器上 <c>EmissiveMaterial</c> 整条路径是坏的。<c>--gpu3d</c> 探针的 8 组对照实测：</para>
/// <list type="bullet">
/// <item><c>Emissive</c> + 冻结纯色（青）→ 纯白；<c>Emissive</c> + 未冻纯色（橙）→ 惨白的粉；
/// <c>Emissive</c> + 冻结纯色（红）→ <b>整个三角形根本没画出来</b>。</item>
/// <item><c>Emissive</c> + 冻结贴图（棋盘）→ 纯白。</item>
/// <item><c>Diffuse</c> + 白色环境光：纯色（红/蓝）与贴图（青黄棋盘，冻结/未冻/Stretch 三变体）
/// <b>全部颜色正确</b>，且分层透明窗与不透明窗表现一致。</item>
/// </list>
/// <para>所以不是"哪种材质语义更贴切"的选择题，是 <c>Emissive</c> 在这台机器上不能用。</para>
///
/// <para><b>矩阵转换是 1:1，不用转置</b>：<c>System.Numerics.Matrix4x4</c> 与 WPF 的
/// <c>Matrix3D</c> 都是行主序、都用行向量约定（<c>v' = v · M</c>）、平移都在第 4 行
/// （<c>M41..M43</c> / <c>OffsetX..OffsetZ</c>）。</para>
/// </summary>
public sealed class SkinGpuViewport
{
    private readonly MatrixCamera _camera = new();
    private readonly ModelVisual3D _root = new();
    private readonly Dictionary<ModelPartType, MatrixTransform3D> _transforms = [];
    private readonly List<ModelPartType> _parts = [];

    private int _version = -1;
    private bool _topEnabled;

    public SkinGpuViewport()
    {
        // Viewport3D 本身没有 Background（它不是 Control）—— 只画 3D 内容，
        // 其余地方自然透明，桌宠的分层窗就能透出阴影与桌面。
        View = new Viewport3D { Camera = _camera };
        View.Children.Add(_root);
    }

    public Viewport3D View { get; }

    /// <summary>
    /// 每帧调一次：模型/贴图变了就重建可视树，然后刷新所有位姿矩阵。
    /// 调用前记得把 <c>Width</c> / <c>Height</c> 设成视口尺寸 —— 投影矩阵的宽高比取自它。
    /// </summary>
    public void Sync(SkinRenderGpu renderer)
    {
        if (!renderer.HaveSkin)
        {
            if (_version != -1)
            {
                _root.Children.Clear();
                _transforms.Clear();
                _parts.Clear();
                _version = -1;
            }

            return;
        }

        renderer.EnsureModel();

        if (_version != renderer.ModelVersion || _topEnabled != renderer.EnableTop)
        {
            Rebuild(renderer);
            _version = renderer.ModelVersion;
            _topEnabled = renderer.EnableTop;
        }

        // 躯干是逐顶点变形的，矩阵换不出来 —— 每帧先把那份网格重写一遍。
        renderer.UpdateSpine();

        // mvp = self(部件) × model × view × proj（行向量顺序）。
        // 把 model 并进部件矩阵，相机就只需要 view / proj，省得依赖"父级 Transform 会下传"。
        var model = renderer.MatrixOf(ModelPartType.Model);
        foreach (var part in _parts)
        {
            _transforms[part].Matrix = ToMatrix3D(renderer.MatrixOf(part) * model);
        }

        _camera.ViewMatrix = ToMatrix3D(renderer.MatrixOf(ModelPartType.View));
        _camera.ProjectionMatrix = ToMatrix3D(renderer.MatrixOf(ModelPartType.Proj));
    }

    private void Rebuild(SkinRenderGpu renderer)
    {
        _root.Children.Clear();
        _transforms.Clear();
        _parts.Clear();

        // 场景里唯一的灯。不能少 —— 没有它 DiffuseMaterial 是全黑的（= 模型整个消失）。
        // 也不能多：再加方向光/点光就会带上明暗，与另外两个后端"不做光照"的约定不符。
        _root.Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });

        AddLayer(renderer.BaseLayer, renderer.BaseTexture);
        if (renderer.EnableTop) AddLayer(renderer.TopLayer, renderer.TopTexture);
    }

    private void AddLayer(
        IReadOnlyList<(ModelPartType Part, MeshGeometry3D Mesh)> layer,
        ImageSource? texture)
    {
        if (texture is null) return;

        var material = new DiffuseMaterial(BuildTextureBrush(texture));
        material.Freeze();

        foreach (var (part, mesh) in layer)
        {
            // 同一个部件在本体层与第二层各有一份几何，但位姿矩阵是同一个 —— 共用变换。
            if (!_transforms.TryGetValue(part, out var transform))
            {
                transform = new MatrixTransform3D();
                _transforms[part] = transform;
                _parts.Add(part);
            }

            _root.Children.Add(new ModelVisual3D
            {
                Content = new GeometryModel3D(mesh, material),
                Transform = transform,
            });
        }
    }

    /// <summary>
    /// 贴图刷子。<b>必须显式指定绝对单位的 Viewbox / Viewport</b>，否则整张贴图会被压进
    /// 每个部件自己的 UV 包围盒里。
    ///
    /// <para>为什么：<c>TileBrush</c> 的 <c>ViewboxUnits</c> / <c>ViewportUnits</c> 默认是
    /// <c>RelativeToBoundingBox</c>，而 3D 材质这里"包围盒"取的是<b>这个 <c>GeometryModel3D</c>
    /// 自己的纹理坐标包围盒</b>。每个身体部件都是一个独立的 <c>GeometryModel3D</c>，
    /// 它的 UV 只占 0..1 空间的一小块（头是 (0..0.5, 0..0.25) 这种），
    /// 于是默认设置会把整张 64x64 皮肤<b>拉伸着塞进那一小块</b> ——
    /// 每个部件都显示一遍完整贴图，糊成一团。</para>
    ///
    /// <para>实测表现（用 R=u、G=v 的标定贴图喂进去）：软件后端头部是暗红（v 小 = 贴图上方），
    /// GPU 是亮绿（v 大），且整个模型都偏绿 —— 就是"被压进去 + 采样点整体偏移"的样子。</para>
    ///
    /// <para><b>只动 <c>Viewport</c>，不要动 <c>Viewbox</c></b>：<c>Viewbox</c> 是"从图像里取哪一块"，
    /// 绝对单位下是按图像像素算的 —— 写成 <c>(0,0,1,1)</c> 就只剩左上角一个像素了。
    /// 保持它相对单位的默认值 <c>(0,0,1,1)</c>（= 整张图）。</para>
    ///
    /// <para><c>TileMode.Tile</c> 是为了跟软件后端对齐：软件采样时对纹素下标取了模
    /// （插值可能让 UV 略微越界），这里也让它在 UV 平面上平铺，行为一致。</para>
    /// </summary>
    private static ImageBrush BuildTextureBrush(ImageSource texture)
    {
        var brush = new ImageBrush(texture)
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            Stretch = Stretch.Fill,
            TileMode = TileMode.Tile,
        };

        // 不设这句就是 WPF 默认的 Linear：64x64 的皮肤被双线性抹开，整只桌宠看着就是糊的
        // （软件后端是 (int)(u * 宽) 直接取纹素 = 最近邻，两条后端必须一样）。
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        return brush;
    }

    private static Matrix3D ToMatrix3D(in Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
}
