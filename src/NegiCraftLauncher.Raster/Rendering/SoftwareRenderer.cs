namespace NegiCraftLauncher.Raster.Rendering;

/// <summary>
/// 三角形光栅化器：逐扫描线 x 跨度 + 透视校正插值 + z-buffer + 最近邻纹理采样 + 超采样抗锯齿。
///
/// <para>为什么值得自己写（而不是 WPF 的 <c>Viewport3D</c> 或 <c>HwndHost</c> + OpenGL）：</para>
/// <list type="bullet">
/// <item>模型只有 6~12 个立方体、贴图 64×64，GPU 是浪费；</item>
/// <item>逐像素 alpha 与桌宠的透明分层窗天然契合，不用折腾预乘；</item>
/// <item><b>输出是确定性的</b> —— 同一份输入永远得到逐字节相同的图，才能做像素级回归；</item>
/// <item>零依赖，RDP / 无 GPU 虚拟机 / 老驱动都不会黑屏。</item>
/// </list>
///
/// <para>性能上做过两处关键取舍（都有 P2 的实测数据支撑）：</para>
/// <list type="number">
/// <item><b>逐扫描线求 x 跨度</b>，而不是扫三角形的包围盒。旋转中的立方体有一半的面片
/// 是近侧视的薄条，包围盒扫描会把绝大部分算力花在三角形外面的像素上。</item>
/// <item>每个像素<b>只做一次除法</b>（透视除法的倒数），其余全用乘法。</item>
/// </list>
///
/// <para>渲染路径上零分配：内部缓冲只在 <see cref="Configure"/> 里按需扩容，之后每帧复用。</para>
/// </summary>
public sealed class SoftwareRenderer
{
    private int _outputWidth;
    private int _outputHeight;
    private int _scale = 1;
    private int _width;
    private int _height;

    private uint[] _color = Array.Empty<uint>();
    private float[] _depth = Array.Empty<float>();

    public int OutputWidth => _outputWidth;

    public int OutputHeight => _outputHeight;

    /// <summary>超采样倍数（1 = 关，2 = 2×2）。</summary>
    public int SampleScale => _scale;

    public int InternalWidth => _width;

    public int InternalHeight => _height;

    public void Configure(int outputWidth, int outputHeight, int sampleScale = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);

        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        _scale = Math.Max(1, sampleScale);
        _width = _outputWidth * _scale;
        _height = _outputHeight * _scale;

        var count = _width * _height;
        if (_color.Length < count)
        {
            _color = new uint[count];
            _depth = new float[count];
        }
    }

    public void Begin(uint clearColor = 0)
    {
        var count = _width * _height;
        _color.AsSpan(0, count).Fill(clearColor);
        _depth.AsSpan(0, count).Fill(float.MaxValue);
    }

    /// <summary>
    /// 四边形按 <c>a-b-c-d</c> 环绕给点：<b>从面外侧看</b>的 左上 → 右上 → 右下 → 左下。
    /// 屏幕 Y 轴向下，所以正面朝向相机时屏幕空间绕序为正、<c>area &gt; 0</c>。
    /// </summary>
    public void DrawQuad(
        in ScreenVertex a, in ScreenVertex b, in ScreenVertex c, in ScreenVertex d,
        uint[] texture, int textureWidth, int textureHeight)
    {
        // 近平面裁剪先做最简的：任何一个点跑到相机后面，整个面丢掉。
        // 皮肤预览里模型不会穿到相机背后，够用；真需要时再上完整的多边形裁剪。
        if (a.BehindNearPlane || b.BehindNearPlane || c.BehindNearPlane || d.BehindNearPlane) return;

        DrawTriangle(a, b, c, texture, textureWidth, textureHeight);
        DrawTriangle(a, c, d, texture, textureWidth, textureHeight);
    }

    private void DrawTriangle(
        in ScreenVertex a, in ScreenVertex b, in ScreenVertex c,
        uint[] texture, int textureWidth, int textureHeight)
    {
        // 显式拷到局部：`in` 参数是只读引用，反复读字段会阻止 JIT 把它们提到寄存器里。
        var v0 = a;
        var v1 = b;
        var v2 = c;

        var area = Edge(v0.X, v0.Y, v1.X, v1.Y, v2.X, v2.Y);

        // area <= 0 = 背面或退化面。背面剔除放在这里，省掉整片扫描。
        if (area <= 1e-4f) return;

        var invArea = 1f / area;

        // 三个插值量都乘上 1/area，之后每像素少 3 次乘法。
        var iw0 = v0.InvW * invArea; var iw1 = v1.InvW * invArea; var iw2 = v2.InvW * invArea;
        var dw0 = v0.DepthOverW * invArea; var dw1 = v1.DepthOverW * invArea; var dw2 = v2.DepthOverW * invArea;
        var uw0 = v0.UOverW * invArea; var uw1 = v1.UOverW * invArea; var uw2 = v2.UOverW * invArea;
        var vw0 = v0.VOverW * invArea; var vw1 = v1.VOverW * invArea; var vw2 = v2.VOverW * invArea;

        // 三条边在每条扫描线上的 x。比包围盒扫描省掉大量"三角形外"的像素测试。
        Span<float> edgeX = stackalloc float[3];
        Span<float> edgeDx = stackalloc float[3];
        Span<float> edgeYMin = stackalloc float[3];
        Span<float> edgeYMax = stackalloc float[3];

        var yTop = MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y));
        var yBottom = MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y));

        var yStart = Math.Max(0, (int)MathF.Ceiling(yTop - 0.5f));
        var yEnd = Math.Min(_height - 1, (int)MathF.Floor(yBottom - 0.5f));
        if (yStart > yEnd) return;

        var sampleY = yStart + 0.5f;
        SetupEdge(v0.X, v0.Y, v1.X, v1.Y, sampleY,
            out edgeX[0], out edgeDx[0], out edgeYMin[0], out edgeYMax[0]);
        SetupEdge(v1.X, v1.Y, v2.X, v2.Y, sampleY,
            out edgeX[1], out edgeDx[1], out edgeYMin[1], out edgeYMax[1]);
        SetupEdge(v2.X, v2.Y, v0.X, v0.Y, sampleY,
            out edgeX[2], out edgeDx[2], out edgeYMin[2], out edgeYMax[2]);

        var color = _color;
        var depthBuffer = _depth;

        for (var y = yStart; y <= yEnd; y++, sampleY += 1f)
        {
            // 当前扫描线上三角形的左右边界。
            var left = float.MaxValue;
            var right = float.MinValue;

            for (var e = 0; e < 3; e++)
            {
                // x(y) 是线性的，所以三条边每行都要推进；是否参与定界只看这一行在不在它的 y 范围内。
                if (edgeYMin[e] <= edgeYMax[e] && sampleY >= edgeYMin[e] && sampleY <= edgeYMax[e])
                {
                    var x = edgeX[e];
                    if (x < left) left = x;
                    if (x > right) right = x;
                }

                edgeX[e] += edgeDx[e];
            }

            if (left > right) continue;

            var x0 = Math.Max(0, (int)MathF.Ceiling(left - 0.5f));
            var x1 = Math.Min(_width - 1, (int)MathF.Floor(right - 0.5f));
            if (x0 > x1) continue;

            var py = sampleY;
            var row = y * _width;

            // 边函数对 x 线性，逐像素递推。Edge 对 px 求导得 (ay - by)。
            var w0 = Edge(v1.X, v1.Y, v2.X, v2.Y, x0 + 0.5f, py);
            var w1 = Edge(v2.X, v2.Y, v0.X, v0.Y, x0 + 0.5f, py);
            var d0 = v1.Y - v2.Y;
            var d1 = v2.Y - v0.Y;

            for (var x = x0; x <= x1; x++, w0 += d0, w1 += d1)
            {
                if (w0 < 0f || w1 < 0f) continue;
                var w2 = area - w0 - w1;
                if (w2 < 0f) continue;

                var invW = w0 * iw0 + w1 * iw1 + w2 * iw2;
                if (invW <= 0f) continue;

                // 每个像素只做一次除法：透视除法的倒数算一遍，后面全用乘法。
                var oneOverW = 1f / invW;
                var depth = (w0 * dw0 + w1 * dw1 + w2 * dw2) * oneOverW;

                var index = row + x;
                if (depth >= depthBuffer[index]) continue;

                var u = (w0 * uw0 + w1 * uw1 + w2 * uw2) * oneOverW;
                var v = (w0 * vw0 + w1 * vw1 + w2 * vw2) * oneOverW;

                var tu = (int)(u * textureWidth);
                var tv = (int)(v * textureHeight);
                // 最近邻 + 环绕，避免采样越界。
                tu = ((tu % textureWidth) + textureWidth) % textureWidth;
                tv = ((tv % textureHeight) + textureHeight) % textureHeight;

                var texel = texture[tv * textureWidth + tu];
                var alpha = texel >> 24;
                if (alpha == 0) continue;

                if (alpha == 255)
                {
                    color[index] = texel;
                    depthBuffer[index] = depth;
                    continue;
                }

                // 半透明覆盖层：src-over，源按预乘处理。
                var dst = color[index];
                var inverse = 255u - alpha;
                var r = ((texel >> 16) & 0xFF) + (((dst >> 16) & 0xFF) * inverse) / 255;
                var g = ((texel >> 8) & 0xFF) + (((dst >> 8) & 0xFF) * inverse) / 255;
                var bl = (texel & 0xFF) + ((dst & 0xFF) * inverse) / 255;
                if (r > 255) r = 255;
                if (g > 255) g = 255;
                if (bl > 255) bl = 255;

                var outAlpha = alpha + (((dst >> 24) & 0xFF) * inverse) / 255;
                if (outAlpha > 255) outAlpha = 255;

                color[index] = (outAlpha << 24) | (r << 16) | (g << 8) | bl;
            }
        }

    }

    private static void SetupEdge(
        float px, float py, float qx, float qy, float firstSampleY,
        out float x, out float dx, out float yMin, out float yMax)
    {
        var dy = qy - py;
        if (MathF.Abs(dy) < 1e-6f)
        {
            // 水平边：不定界（另两条边已经给出跨度）。yMin > yMax 即"非活动"。
            x = 0f;
            dx = 0f;
            yMin = 1f;
            yMax = 0f;
            return;
        }

        dx = (qx - px) / dy;
        x = px + (firstSampleY - py) * dx;
        yMin = MathF.Min(py, qy);
        yMax = MathF.Max(py, qy);
    }

    /// <summary>把超采样缓冲盒式降采样到 <paramref name="target"/>（尺寸须等于 Output 尺寸）。</summary>
    public void ResolveTo(PixelBuffer target)
    {
        if (target.Width != _outputWidth || target.Height != _outputHeight)
        {
            throw new ArgumentException($"目标应为 {_outputWidth}x{_outputHeight}。", nameof(target));
        }

        var dst = target.Pixels;

        if (_scale == 1)
        {
            Array.Copy(_color, dst, dst.Length);
            return;
        }

        var color = _color;

        for (var y = 0; y < _outputHeight; y++)
        {
            var srcRow = y * _scale * _width;
            var dstRow = y * _outputWidth;

            for (var x = 0; x < _outputWidth; x++)
            {
                var column = srcRow + x * _scale;
                var sum = color[column];

                // 逐字节求平均，不拆通道。
                for (var sx = 1; sx < _scale; sx++) sum = Average(sum, color[column + sx]);

                for (var sy = 1; sy < _scale; sy++)
                {
                    var row = srcRow + sy * _width + x * _scale;
                    var rowSum = color[row];
                    for (var sx = 1; sx < _scale; sx++) rowSum = Average(rowSum, color[row + sx]);
                    sum = Average(sum, rowSum);
                }

                dst[dstRow + x] = sum;
            }
        }
    }

    /// <summary>逐字节取平均（不做进位，误差 ≤1/255，视觉上看不出来）。</summary>
    private static uint Average(uint a, uint b) =>
        (a & b) + (((a ^ b) & 0xFEFEFEFEu) >> 1);

    private static float Edge(float ax, float ay, float bx, float by, float px, float py) =>
        (bx - ax) * (py - ay) - (by - ay) * (px - ax);
}
