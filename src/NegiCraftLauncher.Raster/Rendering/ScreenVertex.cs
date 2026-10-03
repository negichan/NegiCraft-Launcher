using System.Numerics;

namespace NegiCraftLauncher.Raster.Rendering;

/// <summary>
/// 已经变换到屏幕空间的顶点。光栅化器只吃这个，不关心上游用的是什么模型/矩阵，
/// 这样 P3 接 <c>MinecraftSkinRender</c> 的几何时不用改光栅器。
///
/// <para>透视校正所需的量都在这里预先除过 w：插值得到的是 <c>u/w</c>、<c>v/w</c>、<c>1/w</c>，
/// 屏幕空间线性，最后再除回去。</para>
/// </summary>
public struct ScreenVertex
{
    /// <summary>屏幕像素坐标，原点在左上角。</summary>
    public float X;
    public float Y;

    /// <summary>归一化深度 0..1（越小越近），已乘 <see cref="InvW"/>。</summary>
    public float DepthOverW;

    public float InvW;

    public float UOverW;
    public float VOverW;

    /// <summary>顶点是否落在近平面之后（w &lt;= 0）。</summary>
    public bool BehindNearPlane;
}

/// <summary>世界坐标 + UV → 屏幕空间顶点。行向量约定：<c>v' = v * M</c>（与 System.Numerics 一致）。</summary>
public static class Projector
{
    public static ScreenVertex Project(
        in Matrix4x4 mvp,
        float x, float y, float z,
        float u, float v,
        float viewportWidth, float viewportHeight)
    {
        var clip = Vector4.Transform(new Vector4(x, y, z, 1f), mvp);
        var w = clip.W;

        var vertex = new ScreenVertex();
        if (w <= 1e-6f)
        {
            vertex.BehindNearPlane = true;
            return vertex;
        }

        var invW = 1f / w;
        var ndcX = clip.X * invW;
        var ndcY = clip.Y * invW;
        var ndcZ = clip.Z * invW;

        vertex.X = (ndcX * 0.5f + 0.5f) * viewportWidth;
        // 屏幕 Y 轴向下，NDC 的 Y 轴向上，所以翻一下。
        vertex.Y = (0.5f - ndcY * 0.5f) * viewportHeight;
        vertex.InvW = invW;
        vertex.DepthOverW = (ndcZ * 0.5f + 0.5f) * invW;
        vertex.UOverW = u * invW;
        vertex.VOverW = v * invW;
        return vertex;
    }
}
