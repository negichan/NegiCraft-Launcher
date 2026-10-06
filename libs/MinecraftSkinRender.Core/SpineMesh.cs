namespace MinecraftSkinRender;

/// <summary>
/// 把一个盒子的四个侧壁沿"贴图 V 方向"细分成若干段，得到一张可以逐顶点变形的网格。
///
/// <para><b>为什么要细分</b>：一个矩形面只有 4 个顶点、渲染时拆成 2 个三角形。扭它的时候
/// 四个角各自转不同角度，面就不再是平的了 —— 可只有 4 个角可动，结果会折成一个明显的屋脊，
/// 看着仍然是一块盒子在拧，不是"腰扭过去了"。侧壁竖着切几段，扭转才有地方铺开。</para>
///
/// <para><b>顶面和底面不切</b>：它们各自是一个横截面，扭转里整个截面只做一个刚体旋转
/// （截面所在高度的转角是常量），4 个角够了。</para>
///
/// <para><b>细分是从现成的 <see cref="CubeModel.GetSquare"/> 结果上做的</b>，不是另写一套几何 ——
/// 所以 <c>segments = 1</c> 时输出的顶点、UV 和以前逐字节一样，可以当回归基线。</para>
/// </summary>
public static class SpineMesh
{
    /// <summary>一个面在顶点表里占 4 个顶点 = 12 个 float。</summary>
    private const int FaceFloats = 12;
    private const int FaceUvFloats = 8;

    /// <summary>面序（与 <see cref="CubeModel"/> 一致）：back, front, left, right, top, bottom。</summary>
    private const int SideFaceCount = 4;

    private const int TopFace = 4;
    private const int BottomFace = 5;

    /// <summary>
    /// 生成细分后的网格。<paramref name="cube"/> / <paramref name="uv"/> 是上游给一个部件的
    /// 24 顶点位置和它对应的 24 组 UV。
    /// </summary>
    /// <returns>
    /// <c>Model</c>：每 4 个顶点一个面，顺序仍是"左上,左下,右下,右上"（上游的顺序，
    /// 绕序由 <c>SkinModel</c> 去翻）；<c>Point</c> 是 0..n 的索引；<c>Uv</c> 与 <c>Model</c> 一一对应。
    /// </returns>
    public static (float[] Model, ushort[] Point, float[] Uv) Build(
        float[] cube, float[] uv, int segments)
    {
        if (segments < 1) segments = 1;

        var faceCount = SideFaceCount * segments + 2;
        var model = new float[faceCount * FaceFloats];
        var uvs = new float[faceCount * FaceUvFloats];
        var point = new ushort[faceCount * 6];

        var quad = 0;

        for (var f = 0; f < SideFaceCount; f++)
        {
            var b = f * FaceFloats;
            var ub = f * FaceUvFloats;

            // 上游每个面的四个点是 左上(p0) 左下(p1) 右下(p2) 右上(p3)，
            // 所以"竖直方向"是 p0→p1 和 p3→p2 这两条边。
            for (var i = 0; i < segments; i++)
            {
                var t0 = (float)i / segments;
                var t1 = (float)(i + 1) / segments;

                Write(model, uvs, ref quad,
                    Lerp3(cube, b, 0, 1, t0), Lerp3(cube, b, 0, 1, t1),
                    Lerp3(cube, b, 3, 2, t1), Lerp3(cube, b, 3, 2, t0),
                    Lerp2(uv, ub, 0, 1, t0), Lerp2(uv, ub, 0, 1, t1),
                    Lerp2(uv, ub, 3, 2, t1), Lerp2(uv, ub, 3, 2, t0));
            }
        }

        // 顶、底两个盖子原样搬过来。
        foreach (var f in new[] { TopFace, BottomFace })
        {
            var b = f * FaceFloats;
            var ub = f * FaceUvFloats;

            Write(model, uvs, ref quad,
                Read3(cube, b, 0), Read3(cube, b, 1), Read3(cube, b, 2), Read3(cube, b, 3),
                Read2(uv, ub, 0), Read2(uv, ub, 1), Read2(uv, ub, 2), Read2(uv, ub, 3));
        }

        for (var v = 0; v < faceCount; v++)
        {
            var o = v * 6;
            var p = (ushort)(v * 4);
            // 与 CubeModel.GetSquareIndicies 同一套绕序。
            point[o] = p;
            point[o + 1] = (ushort)(p + 1);
            point[o + 2] = (ushort)(p + 2);
            point[o + 3] = p;
            point[o + 4] = (ushort)(p + 2);
            point[o + 5] = (ushort)(p + 3);
        }

        return (model, point, uvs);
    }

    private static void Write(
        float[] model, float[] uvs, ref int quad,
        System.Numerics.Vector3 p0, System.Numerics.Vector3 p1,
        System.Numerics.Vector3 p2, System.Numerics.Vector3 p3,
        System.Numerics.Vector2 t0, System.Numerics.Vector2 t1,
        System.Numerics.Vector2 t2, System.Numerics.Vector2 t3)
    {
        var m = quad * FaceFloats;
        var u = quad * FaceUvFloats;

        Put(model, m, p0); Put(model, m + 3, p1); Put(model, m + 6, p2); Put(model, m + 9, p3);
        Put(uvs, u, t0); Put(uvs, u + 2, t1); Put(uvs, u + 4, t2); Put(uvs, u + 6, t3);

        quad++;
    }

    private static void Put(float[] target, int at, System.Numerics.Vector3 v)
    {
        target[at] = v.X;
        target[at + 1] = v.Y;
        target[at + 2] = v.Z;
    }

    private static void Put(float[] target, int at, System.Numerics.Vector2 v)
    {
        target[at] = v.X;
        target[at + 1] = v.Y;
    }

    private static System.Numerics.Vector3 Read3(float[] a, int faceBase, int vertex)
    {
        var i = faceBase + vertex * 3;
        return new System.Numerics.Vector3(a[i], a[i + 1], a[i + 2]);
    }

    private static System.Numerics.Vector2 Read2(float[] a, int faceBase, int vertex)
    {
        var i = faceBase + vertex * 2;
        return new System.Numerics.Vector2(a[i], a[i + 1]);
    }

    private static System.Numerics.Vector3 Lerp3(float[] a, int faceBase, int v0, int v1, float t)
    {
        var p = Read3(a, faceBase, v0);
        var q = Read3(a, faceBase, v1);
        return p + (q - p) * t;
    }

    private static System.Numerics.Vector2 Lerp2(float[] a, int faceBase, int v0, int v1, float t)
    {
        var p = Read2(a, faceBase, v0);
        var q = Read2(a, faceBase, v1);
        return p + (q - p) * t;
    }
}
