using System.Numerics;

namespace NegiCraftLauncher.Raster.Rendering;

/// <summary>一个四边形：4 个世界坐标点 + 对应 UV（UV 已归一化到 0..1）。</summary>
public struct Quad
{
    public Vector3 P0, P1, P2, P3;
    public Vector2 T0, T1, T2, T3;

    public Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3)
    {
        P0 = p0;
        P1 = p1;
        P2 = p2;
        P3 = p3;
        T0 = t0;
        T1 = t1;
        T2 = t2;
        T3 = t3;
    }
}

/// <summary>
/// 立方体网格生成：按 Minecraft 皮肤的**真实 UV 布局**把 6 个面贴到盒子上。
///
/// <para>P2 的 benchmark 与 P3 的正式皮肤模型都用它拼几何。</para>
///
/// <para><b>踩过的坑（两个都很难从画面上看出来）</b>：</para>
/// <list type="number">
/// <item><b>每个部件的十字展开位置都不一样</b>。只有头部是 正(8,8)/背(24,8)/左(16,8)/右(0,8)/
/// 顶(8,0)/底(16,0)；躯干是 顶(20,16)/底(28,16)/右(16,20)/正(20,20)/左(28,20)/背(32,20)。
/// 曾经给所有部件都套头部那套偏移，于是除头以外全在采错纹素 —— 表现是身上多出几道
/// 衬衫色的细线。布局由部件**像素尺寸** (w,h,d) 推出，见 <see cref="AddBox"/>。</item>
/// <item><b>顶点绕序必须按"从盒子外面看"给</b>，否则背面剔除会反（剔掉正面留下背面）：
/// 看到的是盒子<b>远端</b>那一面，模型左右镜像，而且外套/帽子那层**永远被本体挡住**。
/// 因为盒子的近端面和远端面投影到屏幕上形状一样，屏幕绕序正负全看作者怎么排点。</item>
/// </list>
///
/// <para>统一约定：每个面的 4 个点按"从盒外看"的 左上 → 右上 → 右下 → 左下 给，
/// 依次对应贴图区域的 左上 → 右上 → 右下 → 左下。这样 <see cref="SoftwareRenderer"/>
/// 里 <c>area &gt; 0</c> 就恰好等于"正面朝向相机"。</para>
/// </summary>
public static class BoxMesh
{
    private const float TexSize = 64f;

    /// <summary>
    /// 往 <paramref name="quads"/> 里追加一个盒子。
    /// </summary>
    /// <param name="uvOrigin">该部件在 64×64 皮肤贴图里的原点（像素）。</param>
    /// <param name="texWidth">贴图区域的宽（像素）。盒子的世界尺寸可以比它大 —— 外套层就是几何放大、
    /// 贴图区域仍与本体同尺寸。</param>
    /// <param name="texHeight">贴图区域的高（像素）。</param>
    /// <param name="texDepth">贴图区域的深（像素）。</param>
    public static void AddBox(
        List<Quad> quads,
        Vector3 min, Vector3 max,
        Vector2 uvOrigin,
        int texWidth, int texHeight, int texDepth)
    {
        var x0 = min.X; var y0 = min.Y; var z0 = min.Z;
        var x1 = max.X; var y1 = max.Y; var z1 = max.Z;

        var ox = (int)uvOrigin.X;
        var oy = (int)uvOrigin.Y;
        var w = texWidth;
        var h = texHeight;
        var d = texDepth;

        // 十字展开：顶/底 在上一行，右/正/左/背 在下一行。
        var topU = ox + d; var topV = oy;
        var bottomU = ox + d + w; var bottomV = oy;
        var rightU = ox; var sideV = oy + d;
        var frontU = ox + d; var frontV = oy + d;
        var leftU = ox + d + w; var leftV = oy + d;
        var backU = ox + d + w + d; var backV = oy + d;

        // +Z 正面：从 +Z 看，屏幕右 = +X、屏幕上 = +Y，贴图左缘接世界的 x0。
        AddFace(quads, new Vector3(x0, y1, z1), new Vector3(x1, y1, z1),
                       new Vector3(x1, y0, z1), new Vector3(x0, y0, z1),
            frontU, frontV, w, h);

        // -Z 背面：从 -Z 看，屏幕右 = -X，所以贴图左缘接 x1。
        AddFace(quads, new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
                       new Vector3(x0, y0, z0), new Vector3(x1, y0, z0),
            backU, backV, w, h);

        // +X 侧面（角色的左边）：从 +X 看，屏幕右 = -Z，贴图左缘接 z1。
        AddFace(quads, new Vector3(x1, y1, z1), new Vector3(x1, y1, z0),
                       new Vector3(x1, y0, z0), new Vector3(x1, y0, z1),
            leftU, leftV, d, h);

        // -X 侧面（角色的右边）：从 -X 看，屏幕右 = +Z，贴图左缘接 z0。
        AddFace(quads, new Vector3(x0, y1, z0), new Vector3(x0, y1, z1),
                       new Vector3(x0, y0, z1), new Vector3(x0, y0, z0),
            rightU, sideV, d, h);

        // +Y 顶面：贴图下缘接正面顶边（z1），左缘接 x0。
        AddFace(quads, new Vector3(x0, y1, z0), new Vector3(x1, y1, z0),
                       new Vector3(x1, y1, z1), new Vector3(x0, y1, z1),
            topU, topV, w, d);

        // -Y 底面：贴上缘接正面底边（z1），u 方向与正面一致。
        AddFace(quads, new Vector3(x0, y0, z1), new Vector3(x1, y0, z1),
                       new Vector3(x1, y0, z0), new Vector3(x0, y0, z0),
            bottomU, bottomV, w, d);
    }

    /// <summary>
    /// 追加一个面。点按"从盒外看"的 左上→右上→右下→左下 给，UV 依次对应
    /// 贴图区域 <paramref name="u"/>,<paramref name="v"/> 起的 <paramref name="w"/>×<paramref name="h"/> 块。
    /// </summary>
    private static void AddFace(
        List<Quad> quads,
        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
        int u, int v, int w, int h)
    {
        var u0 = u / TexSize;
        var v0 = v / TexSize;
        var u1 = (u + w) / TexSize;
        var v1 = (v + h) / TexSize;

        quads.Add(new Quad(
            p0, p1, p2, p3,
            new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(u0, v1)));
    }

    /// <summary>每像素对应多少世界单位：8 px = 0.5 m。</summary>
    public const float PixelsPerMeter = 16f;

    /// <summary>外套/帽子那层相对本体的外扩量：0.25 px。</summary>
    public const float OverlayInflate = 0.25f / PixelsPerMeter;

    /// <summary>
    /// P2 benchmark 用的"类人形"模型：6 个部件 + 一层外扩 0.25px 的覆盖层，
    /// 合计 12 个盒子 = 72 个四边形，跟真实皮肤模型一个量级。
    ///
    /// <para>比例照 MC 来：脚在 y=0、头在 y=1.9，8 px = 0.5 m。部件 UV 原点就是
    /// 64×64 皮肤里各块的标准位置。</para>
    /// </summary>
    public static List<Quad> BuildBenchmarkCharacter(bool withOverlay = true)
    {
        var quads = new List<Quad>(80);

        // 像素尺寸从世界尺寸推，所以外套层只要把几何外扩、贴图尺寸仍传本体的。
        void Part(float cx, float cy, float cz, float sx, float sy, float sz, Vector2 uv, bool overlay = false)
        {
            var pw = (int)MathF.Round(sx * PixelsPerMeter);
            var ph = (int)MathF.Round(sy * PixelsPerMeter);
            var pd = (int)MathF.Round(sz * PixelsPerMeter);

            var half = new Vector3(sx, sy, sz) * 0.5f;
            if (overlay) half += new Vector3(OverlayInflate);

            var center = new Vector3(cx, cy, cz);
            AddBox(quads, center - half, center + half, uv, pw, ph, pd);
        }

        Part(0f, 1.65f, 0f, 0.5f, 0.5f, 0.5f, new Vector2(0, 0));          // 头
        Part(0f, 1.05f, 0f, 0.5f, 0.75f, 0.25f, new Vector2(16, 16));      // 身体
        Part(-0.375f, 1.05f, 0f, 0.25f, 0.75f, 0.25f, new Vector2(40, 16)); // 右臂
        Part(0.375f, 1.05f, 0f, 0.25f, 0.75f, 0.25f, new Vector2(32, 48));  // 左臂
        Part(-0.125f, 0.375f, 0f, 0.25f, 0.75f, 0.25f, new Vector2(0, 16)); // 右腿
        Part(0.125f, 0.375f, 0f, 0.25f, 0.75f, 0.25f, new Vector2(16, 48)); // 左腿

        if (withOverlay)
        {
            // 覆盖层：几何外扩 0.25 px，alpha 由贴图决定（老皮肤那层是空的，自然看不见）。
            Part(0f, 1.65f, 0f, 0.5f, 0.5f, 0.5f, new Vector2(32, 0), overlay: true);          // 帽子
            Part(0f, 1.05f, 0f, 0.5f, 0.75f, 0.25f, new Vector2(16, 32), overlay: true);       // 外套
            Part(-0.375f, 1.05f, 0f, 0.25f, 0.75f, 0.25f, new Vector2(40, 32), overlay: true); // 右臂外套
            Part(0.375f, 1.05f, 0f, 0.25f, 0.75f, 0.25f, new Vector2(48, 48), overlay: true);  // 左臂外套
            Part(-0.125f, 0.375f, 0f, 0.25f, 0.75f, 0.25f, new Vector2(0, 32), overlay: true); // 右腿外套
            Part(0.125f, 0.375f, 0f, 0.25f, 0.75f, 0.25f, new Vector2(0, 48), overlay: true);  // 左腿外套
        }

        return quads;
    }
}
