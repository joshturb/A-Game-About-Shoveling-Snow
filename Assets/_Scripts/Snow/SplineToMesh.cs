using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public sealed class SplineToMesh : MonoBehaviour
{
    [SerializeField] private SplineContainer splineContainer;

    [Tooltip("Quad edge length in world units. Smaller = more detail.")]
    [SerializeField, Min(0.01f)] private float detail = 0.25f;

    private Mesh _mesh;
    private MeshFilter _mf;

    // Reused buffers (no GC during rebuild)
    private readonly List<Vector2> _polyXZ = new(512);
    private readonly List<Vector3> _gridVerts = new(8192);
    private readonly List<Vector2> _gridUVs = new(8192);
    private readonly List<int> _tris = new(16384);
    private readonly List<float> _xHits = new(256);

    private const float EPS = 1e-6f;

    private void OnEnable()
    {
        _mf = GetComponent<MeshFilter>();
        EnsureMesh();
        Rebuild();
    }

    [ContextMenu("Rebuild")]
    public void Rebuild()
    {
        if (splineContainer == null || splineContainer.Splines == null || splineContainer.Splines.Count == 0)
        {
            ClearMesh();
            return;
        }

        Spline spline = splineContainer.Splines[0];
        float step = Mathf.Max(0.01f, detail);

        _polyXZ.Clear();
        if (!SampleSplinePolygonLocalXZ(spline, step, _polyXZ, out float flatY))
        {
            ClearMesh();
            return;
        }

        CleanupConsecutiveDuplicates(_polyXZ, Mathf.Max(1e-5f, step * 0.02f));
        if (_polyXZ.Count < 3)
        {
            ClearMesh();
            return;
        }

        EnsureMesh();

        if (!BuildQuadGridFillScanline(_polyXZ, flatY, step, _gridVerts, _gridUVs, _tris, _xHits))
        {
            ClearMesh();
            return;
        }

        _mesh.indexFormat = (_gridVerts.Count > 65535)
            ? IndexFormat.UInt32
            : IndexFormat.UInt16;

        _mesh.Clear(false);
        _mesh.SetVertices(_gridVerts);
        _mesh.SetUVs(0, _gridUVs);
        _mesh.SetTriangles(_tris, 0, true);
        _mesh.RecalculateNormals();     // once
        _mesh.RecalculateBounds();
    }

    private bool SampleSplinePolygonLocalXZ(Spline spline, float step, List<Vector2> outPolyXZ, out float flatY)
    {
        flatY = 0f;

        float length = SafeSplineLengthWorld(spline);
        int samples = (length > EPS)
            ? Mathf.Clamp(Mathf.CeilToInt(length / Mathf.Max(step * 0.5f, 0.01f)), 64, 8192)
            : 256;

        Transform cTr = splineContainer.transform;

        bool closed = spline.Closed;
        int count = closed ? samples : (samples + 1); // include endpoint if open

        float sumY = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (count == 1) ? 0f : (i / (float)(count - 1)); // [0,1] if open, [0,1) if closed handled below
            if (closed) t = i / (float)samples; // [0,1)

            float3 localPos = SplineUtility.EvaluatePosition(spline, t);
            Vector3 worldPos = cTr.TransformPoint((Vector3)localPos);
            Vector3 localToThis = transform.InverseTransformPoint(worldPos);

            outPolyXZ.Add(new Vector2(localToThis.x, localToThis.z));
            sumY += localToThis.y;
        }

        if (outPolyXZ.Count < 3) return false;

        // Force a closed loop for filling
        if ((outPolyXZ[0] - outPolyXZ[^1]).sqrMagnitude > 1e-10f)
            outPolyXZ.Add(outPolyXZ[0]);

        flatY = sumY / Mathf.Max(1, count);
        return true;
    }

    private float SafeSplineLengthWorld(Spline spline)
    {
        try
        {
            float4x4 worldFromLocal = ToFloat4x4(splineContainer.transform.localToWorldMatrix);
            return SplineUtility.CalculateLength(spline, worldFromLocal);
        }
        catch
        {
            return 0f;
        }
    }

    // Builds a full shared grid, then fills quads per row using scanline intersections (robust for long/complex concave shapes).
    private static bool BuildQuadGridFillScanline(
        List<Vector2> poly, float y, float step,
        List<Vector3> outVerts, List<Vector2> outUVs, List<int> outTris,
        List<float> tempXHits)
    {
        outVerts.Clear();
        outUVs.Clear();
        outTris.Clear();
        tempXHits.Clear();

        // poly is closed (last == first)
        int pc = poly.Count;
        if (pc < 4) return false;

        // bounds
        float minX = poly[0].x, maxX = poly[0].x;
        float minZ = poly[0].y, maxZ = poly[0].y;
        for (int i = 1; i < pc; i++)
        {
            Vector2 p = poly[i];
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minZ) minZ = p.y;
            if (p.y > maxZ) maxZ = p.y;
        }

        float w = Mathf.Max(maxX - minX, EPS);
        float h = Mathf.Max(maxZ - minZ, EPS);

        int nx = Mathf.Clamp(Mathf.CeilToInt(w / step), 1, 4096);
        int nz = Mathf.Clamp(Mathf.CeilToInt(h / step), 1, 4096);

        float dx = (maxX - minX) / nx;
        float dz = (maxZ - minZ) / nz;

        float maxX2 = maxX;
        float maxZ2 = maxZ;

        float invW = 1f / Mathf.Max(maxX2 - minX, EPS);
        float invH = 1f / Mathf.Max(maxZ2 - minZ, EPS);

        int vertW = nx + 1;
        int vertH = nz + 1;

        int totalVerts = vertW * vertH;
        outVerts.Capacity = Mathf.Max(outVerts.Capacity, totalVerts);
        outUVs.Capacity = Mathf.Max(outUVs.Capacity, totalVerts);

        // full grid vertices (shared)
        for (int iz = 0; iz < vertH; iz++)
        {
            float z = minZ + iz * dz;
            float v = (z - minZ) * invH;

            for (int ix = 0; ix < vertW; ix++)
            {
                float x = minX + ix * dx;
                float u = (x - minX) * invW;

                outVerts.Add(new Vector3(x, y, z));
                outUVs.Add(new Vector2(u, v));
            }
        }

        // scanline fill per row (cell centers)
        for (int iz = 0; iz < nz; iz++)
        {
            float zCenter = minZ + (iz + 0.5f) * dz;

            tempXHits.Clear();

            // intersections with polygon edges (even-odd rule)
            for (int i = 0; i < pc - 1; i++)
            {
                Vector2 a = poly[i];
                Vector2 b = poly[i + 1];

                float z0 = a.y;
                float z1 = b.y;

                // half-open to avoid double hits at vertices
                bool crosses = (z0 <= zCenter && z1 > zCenter) || (z1 <= zCenter && z0 > zCenter);
                if (!crosses) continue;

                float denom = (z1 - z0);
                if (Mathf.Abs(denom) < 1e-12f) continue;

                float t = (zCenter - z0) / denom;
                float x = a.x + (b.x - a.x) * t;
                tempXHits.Add(x);
            }

            if (tempXHits.Count < 2) continue;

            tempXHits.Sort();

            for (int k = 0; k + 1 < tempXHits.Count; k += 2)
            {
                float xL = tempXHits[k];
                float xR = tempXHits[k + 1];
                if (xR <= xL) continue;

                // Convert [xL, xR] interval to cell indices whose centers lie inside
                float startF = ((xL - minX) / dx) - 0.5f;
                float endF   = ((xR - minX) / dx) - 0.5f;

                int ixStart = Mathf.CeilToInt(startF - 1e-6f);
                int ixEnd   = Mathf.FloorToInt(endF + 1e-6f);

                if (ixEnd < 0 || ixStart > nx - 1) continue;

                ixStart = Mathf.Clamp(ixStart, 0, nx - 1);
                ixEnd   = Mathf.Clamp(ixEnd, 0, nx - 1);

                for (int ix = ixStart; ix <= ixEnd; ix++)
                {
                    int a = iz * vertW + ix;           // (x0,z0)
                    int b = iz * vertW + (ix + 1);     // (x1,z0)
                    int c = (iz + 1) * vertW + ix;     // (x0,z1)
                    int d = (iz + 1) * vertW + (ix + 1); // (x1,z1)

                    // +Y facing
                    outTris.Add(a); outTris.Add(d); outTris.Add(b);
                    outTris.Add(a); outTris.Add(c); outTris.Add(d);
                }
            }
        }

        return outTris.Count > 0;
    }

    private static void CleanupConsecutiveDuplicates(List<Vector2> poly, float eps)
    {
        float eps2 = eps * eps;
        for (int i = poly.Count - 1; i >= 1; i--)
        {
            if ((poly[i] - poly[i - 1]).sqrMagnitude <= eps2)
                poly.RemoveAt(i);
        }
        // keep closure if present
        if (poly.Count >= 3 && (poly[0] - poly[^1]).sqrMagnitude > 1e-10f)
            poly.Add(poly[0]);
    }

    private void EnsureMesh()
    {
        _mf ??= GetComponent<MeshFilter>();

        if (_mesh == null)
        {
            _mesh = new Mesh { name = "SplineQuadFillMesh" };
            _mesh.MarkDynamic();
        }

        if (_mf.sharedMesh != _mesh)
            _mf.sharedMesh = _mesh;
    }

    private void ClearMesh()
    {
        EnsureMesh();
        _mesh.Clear(false);
    }

    private static float4x4 ToFloat4x4(Matrix4x4 m)
    {
        return new float4x4(
            new float4(m.m00, m.m01, m.m02, m.m03),
            new float4(m.m10, m.m11, m.m12, m.m13),
            new float4(m.m20, m.m21, m.m22, m.m23),
            new float4(m.m30, m.m31, m.m32, m.m33)
        );
    }
}
