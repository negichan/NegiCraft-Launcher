using System.Numerics;
using MinecraftSkinRender;

namespace NegiCraftLauncher.Raster.Rendering;

/// <summary>
/// 一个要光栅化的面片：4 个模型空间顶点 + 各自绑定的 UV + 面法线（4 个顶点共用，平面着色）。
///
/// <para>顶点顺序是 <b>从面外侧看的 左上 → 右上 → 右下 → 左下</b>，也就是"从外侧看顺时针"。
/// 这是 <see cref="SoftwareRenderer"/> 的约定 —— <see cref="SkinModel"/> 负责把
/// <c>MinecraftSkinRender</c> 的原始顺序翻过来。</para>
/// </summary>
public readonly struct SkinQuad(
    Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
    Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3,
    Vector3 normal)
{
    public readonly Vector3 P0 = p0, P1 = p1, P2 = p2, P3 = p3;
    public readonly Vector2 T0 = t0, T1 = t1, T2 = t2, T3 = t3;
    public readonly Vector3 Normal = normal;
}

/// <summary>一个身体部件：它是什么、几何、以及贴图 UV 表。</summary>
public sealed record SkinPart(ModelPartType Type, float[] Model, float[] Uv);

/// <summary>
/// 把 <c>MinecraftSkinRender.Core</c> 的原始模型/UV 表转成 <see cref="SkinQuad"/> 列表。
///
/// <para><b>为什么要把顶点顺序翻过来</b>：上游的每个面按"从外侧看 左上→左下→右下→右上"给点
/// （它喂给 OpenGL，配合 <c>GL_CCW</c> 做正面判定）。本光栅器用的是相反的绕序，
/// 所以取 <c>v0, v3, v2, v1</c>，UV 跟着各自顶点走。翻错的话表现是<b>左右镜像、
/// 且第二层永远被本体挡住</b> —— 因为盒子的近端面和远端面投影到屏幕上形状一样，
/// 光看单张正面截图发现不了。</para>
///
/// <para>UV 表是按"纹素"给的，<c>GetTex</c> 里已经除过 64（老皮肤除 32），所以拿到的就是 0..1。
/// 老皮肤（64x32）的 V 按 32 归一化，采样时纹理高度要传 32。</para>
/// </summary>
public static class SkinModel
{
    /// <summary>
    /// 组装一层（本体或第二层）的所有部件。
    /// 老皮肤没有第二层，<c>GetSteveTop</c> 只会给出头部 —— 这里如实返回，不补齐。
    /// </summary>
    public static List<SkinPart> Build(SkinType type, bool top)
    {
        var model = top ? Steve3DModel.GetSteveTop(type) : Steve3DModel.GetSteve(type);
        var texture = top ? Steve3DTexture.GetSteveTextureTop(type) : Steve3DTexture.GetSteveTexture(type);

        var parts = new List<SkinPart>(7);

        Add(ModelPartType.Head, model.Head, texture.Head);
        Add(ModelPartType.Body, model.Body, texture.Body);
        Add(ModelPartType.LeftArm, model.LeftArm, texture.LeftArm);
        Add(ModelPartType.RightArm, model.RightArm, texture.RightArm);
        Add(ModelPartType.LeftLeg, model.LeftLeg, texture.LeftLeg);
        Add(ModelPartType.RightLeg, model.RightLeg, texture.RightLeg);
        Add(ModelPartType.Cape, model.Cape, texture.Cape);

        return parts;

        void Add(ModelPartType partType, CubeModelItemObj? part, float[]? uv)
        {
            // 老皮肤的第二层只有头；上游的 record 字段声明成非空但实际会给 null，别信。
            if (part?.Model == null || uv == null) return;
            parts.Add(new SkinPart(partType, part.Model, uv));
        }
    }

    /// <summary>把一个部件展开成 6 个面片（每面 2 个三角形由光栅器自己拆）。</summary>
    public static void AppendQuads(SkinPart part, List<SkinQuad> destination)
    {
        var pos = part.Model;
        var uv = part.Uv;

        // 24 个顶点 = 6 个面 × 4；索引固定 0..23（CubeModel.GetSquareIndicies 生成的就是它）。
        for (var face = 0; face < 6; face++)
        {
            var v = face * 4;

            var p0 = Vertex(pos, v + 0);
            var p1 = Vertex(pos, v + 1);
            var p2 = Vertex(pos, v + 2);
            var p3 = Vertex(pos, v + 3);

            // 上游顺序 (p0,p1,p2,p3) = 左上,左下,右下,右上；本光栅器要 左上,右上,右下,左下。
            destination.Add(new SkinQuad(
                p0, p3, p2, p1,
                Uv(uv, v + 0), Uv(uv, v + 3), Uv(uv, v + 2), Uv(uv, v + 1),
                Normal(v)));
        }
    }

    private static Vector3 Vertex(float[] positions, int index)
    {
        var i = index * 3;
        return new Vector3(positions[i], positions[i + 1], positions[i + 2]);
    }

    private static Vector2 Uv(float[] uvs, int index)
    {
        var i = index * 2;
        return new Vector2(uvs[i], uvs[i + 1]);
    }

    /// <summary>平面着色：一个面的 4 个顶点共用同一个法线，取该面第一个顶点的即可。</summary>
    private static Vector3 Normal(int faceVertexIndex) =>
        Vertex(CubeModel.Vertices, faceVertexIndex);
}
