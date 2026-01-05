using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class SnowHeightfieldCollider
{
    public struct Hit
    {
        public Vector3 pointWorld;
        public Vector3 normalWorld;
        public float distanceWorld;
    }

    private readonly Transform _tf;
    private readonly Mesh _mesh;
    private Vector3[] _verts;

    private float[] _xs;         // sorted unique x positions (local)
    private float[] _zs;         // sorted unique z positions (local)
    private int _w, _h;          // vertex grid dims
    private int[] _grid;         // size w*h -> vertex index (or -1)

    private float _xMin, _zMin, _xMax, _zMax;
    private float _stepX, _stepZ; // uniform steps (optional)
    private bool _uniform;        // if true, use fast mapping; else binary search mapping

    public SnowHeightfieldCollider(Transform snowTransform, Mesh mesh)
    {
        _tf = snowTransform ? snowTransform : throw new ArgumentNullException(nameof(snowTransform));
        _mesh = mesh ? mesh : throw new ArgumentNullException(nameof(mesh));

        _verts = _mesh.vertices ?? throw new InvalidOperationException("Mesh has no vertices.");
        BuildXZIndexing();
    }

    /// Cheap: call after you modify mesh.vertices (Y changes). No rebuild of XZ indexing.
    public void Refresh()
    {
        _verts = _mesh.vertices ?? throw new InvalidOperationException("Mesh has no vertices.");
    }

    public bool ContainsPoint(Vector3 worldPoint)
    {
        Vector3 lp = _tf.InverseTransformPoint(worldPoint);
        if (!TryGetCell(lp, out int cx, out int cz, out float fx, out float fz))
            return false;

        float surfaceY = SampleSurfaceY(cx, cz, fx, fz);
        return lp.y <= surfaceY;
    }

    /// Accurate raycast (tests only the hit cell’s 2 triangles).
    /// Works best for typical “click from camera” rays; still valid generally.
    public bool Raycast(Ray worldRay, out Hit hit, float maxDistance = 500f)
    {
        hit = default;

        // World -> Local ray
        Vector3 ro = _tf.InverseTransformPoint(worldRay.origin);
        Vector3 rd = _tf.InverseTransformDirection(worldRay.direction);

        // Intersect ray with XZ slab bounds first (fast reject)
        Bounds slab = new Bounds(
            new Vector3((_xMin + _xMax) * 0.5f, 0f, (_zMin + _zMax) * 0.5f),
            new Vector3((_xMax - _xMin), 100000f, (_zMax - _zMin))
        );

        if (!RayAabb(ro, rd, slab, out float tEnter, out float tExit))
            return false;

        if (tExit < 0f) return false;

        float t0 = Mathf.Max(0f, tEnter);
        float t1 = Mathf.Min(tExit, maxDistance);

        // Sample a point along the ray to find the cell, then test that cell’s triangles.
        // We try a few steps from entry to exit to find the first triangle hit.
        const int steps = 32;
        float dt = (t1 - t0) / steps;
        if (dt <= 0f) return false;

        float bestT = float.PositiveInfinity;
        Vector3 bestN = Vector3.up;

        for (int s = 0; s <= steps; s++)
        {
            float t = t0 + dt * s;
            Vector3 p = ro + rd * t;

            if (!TryGetCell(p, out int cx, out int cz, out _, out _))
                continue;

            if (RaycastCell(ro, rd, cx, cz, out float cellT, out Vector3 cellN))
            {
                if (cellT >= 0f && cellT < bestT && cellT <= maxDistance)
                {
                    bestT = cellT;
                    bestN = cellN;
                    break; // first hit along ray is enough for clicking
                }
            }
        }

        if (bestT == float.PositiveInfinity)
            return false;

        Vector3 localHit = ro + rd * bestT;

        hit.pointWorld = _tf.TransformPoint(localHit);
        hit.normalWorld = _tf.TransformDirection(bestN).normalized;
        hit.distanceWorld = Vector3.Distance(worldRay.origin, hit.pointWorld);
        return true;
    }

    // ---------- internals ----------

    private void BuildXZIndexing()
    {
        var b = _mesh.bounds;
        _xMin = b.min.x; _xMax = b.max.x;
        _zMin = b.min.z; _zMax = b.max.z;

        // Quantize keys so floating noise doesn’t create tons of “unique” coords.
        float span = Mathf.Max(b.size.x, b.size.z);
        float eps = Mathf.Max(1e-6f, span * 1e-5f);

        var xMap = new Dictionary<int, float>(256);
        var zMap = new Dictionary<int, float>(256);

        for (int i = 0; i < _verts.Length; i++)
        {
            int kx = Mathf.RoundToInt(_verts[i].x / eps);
            int kz = Mathf.RoundToInt(_verts[i].z / eps);

            if (!xMap.ContainsKey(kx)) xMap.Add(kx, _verts[i].x);
            if (!zMap.ContainsKey(kz)) zMap.Add(kz, _verts[i].z);
        }

        _xs = new float[xMap.Count];
        _zs = new float[zMap.Count];

        int xi = 0; foreach (var kv in xMap) _xs[xi++] = kv.Value;
        int zi = 0; foreach (var kv in zMap) _zs[zi++] = kv.Value;

        Array.Sort(_xs);
        Array.Sort(_zs);

        _w = _xs.Length;
        _h = _zs.Length;

        if (_w < 2 || _h < 2)
            throw new InvalidOperationException($"Grid dims invalid: w={_w}, h={_h}. Mesh is not a usable XZ grid.");

        // Check uniformity (optional optimization)
        _stepX = _xs[1] - _xs[0];
        _stepZ = _zs[1] - _zs[0];
        _uniform = IsUniform(_xs, eps) && IsUniform(_zs, eps) && Mathf.Abs(_stepX) > eps && Mathf.Abs(_stepZ) > eps;

        // Build coordinate -> index lookup
        var xIndex = new Dictionary<int, int>(_w);
        var zIndex = new Dictionary<int, int>(_h);

        for (int i = 0; i < _w; i++) xIndex[Mathf.RoundToInt(_xs[i] / eps)] = i;
        for (int i = 0; i < _h; i++) zIndex[Mathf.RoundToInt(_zs[i] / eps)] = i;

        _grid = new int[_w * _h];
        for (int i = 0; i < _grid.Length; i++) _grid[i] = -1;

        // Fill grid mapping
        for (int vi2 = 0; vi2 < _verts.Length; vi2++)
        {
            int kx = Mathf.RoundToInt(_verts[vi2].x / eps);
            int kz = Mathf.RoundToInt(_verts[vi2].z / eps);

            if (!xIndex.TryGetValue(kx, out int gx)) continue;
            if (!zIndex.TryGetValue(kz, out int gz)) continue;

            int gi = gx + gz * _w;
            if (_grid[gi] == -1) _grid[gi] = vi2; // keep first
        }
    }

    private static bool IsUniform(float[] coords, float eps)
    {
        float d0 = coords[1] - coords[0];
        for (int i = 2; i < coords.Length; i++)
        {
            float d = coords[i] - coords[i - 1];
            if (Mathf.Abs(d - d0) > eps * 10f) return false;
        }
        return true;
    }

    private bool TryGetCell(Vector3 lp, out int cx, out int cz, out float fx, out float fz)
    {
        cx = cz = 0;
        fx = fz = 0f;

        if (lp.x < _xs[0] || lp.x > _xs[_w - 1] || lp.z < _zs[0] || lp.z > _zs[_h - 1])
            return false;

        if (_uniform)
        {
            float gx = (lp.x - _xs[0]) / _stepX;
            float gz = (lp.z - _zs[0]) / _stepZ;

            cx = Mathf.Clamp(Mathf.FloorToInt(gx), 0, _w - 2);
            cz = Mathf.Clamp(Mathf.FloorToInt(gz), 0, _h - 2);

            fx = Mathf.Clamp01(gx - cx);
            fz = Mathf.Clamp01(gz - cz);
            return true;
        }

        // non-uniform: binary search
        cx = FindLowerIndex(_xs, lp.x);
        cz = FindLowerIndex(_zs, lp.z);

        cx = Mathf.Clamp(cx, 0, _w - 2);
        cz = Mathf.Clamp(cz, 0, _h - 2);

        float x0 = _xs[cx], x1 = _xs[cx + 1];
        float z0 = _zs[cz], z1 = _zs[cz + 1];

        fx = (lp.x - x0) / Mathf.Max(1e-6f, (x1 - x0));
        fz = (lp.z - z0) / Mathf.Max(1e-6f, (z1 - z0));

        fx = Mathf.Clamp01(fx);
        fz = Mathf.Clamp01(fz);
        return true;
    }

    private static int FindLowerIndex(float[] a, float v)
    {
        int idx = Array.BinarySearch(a, v);
        if (idx >= 0) return idx;
        idx = ~idx; // insertion point
        return idx - 1;
    }

    private int V(int x, int z) => _grid[x + z * _w];

    private float SampleSurfaceY(int cx, int cz, float fx, float fz)
    {
        int i00 = V(cx, cz);
        int i10 = V(cx + 1, cz);
        int i01 = V(cx, cz + 1);
        int i11 = V(cx + 1, cz + 1);

        // If mapping is incomplete, fall back to 0.
        if (i00 < 0 || i10 < 0 || i01 < 0 || i11 < 0) return 0f;

        // Split along diagonal (00->11). If your render mesh uses other diagonal, swap condition.
        if (fx + fz <= 1f)
        {
            float y00 = _verts[i00].y;
            float y10 = _verts[i10].y;
            float y01 = _verts[i01].y;
            return y00 + (y10 - y00) * fx + (y01 - y00) * fz;
        }
        else
        {
            float y11 = _verts[i11].y;
            float y01 = _verts[i01].y;
            float y10 = _verts[i10].y;
            float u = 1f - fx;
            float v = 1f - fz;
            return y11 + (y01 - y11) * u + (y10 - y11) * v;
        }
    }

    private bool RaycastCell(Vector3 ro, Vector3 rd, int cx, int cz, out float bestT, out Vector3 bestN)
    {
        bestT = float.PositiveInfinity;
        bestN = Vector3.up;

        int i00 = V(cx, cz);
        int i10 = V(cx + 1, cz);
        int i01 = V(cx, cz + 1);
        int i11 = V(cx + 1, cz + 1);

        if (i00 < 0 || i10 < 0 || i01 < 0 || i11 < 0)
            return false;

        // Two tris. Diagonal (00->11).
        if (RayTri(ro, rd, _verts[i00], _verts[i10], _verts[i11], out float tA, out Vector3 nA))
        {
            bestT = tA; bestN = nA;
        }
        if (RayTri(ro, rd, _verts[i00], _verts[i11], _verts[i01], out float tB, out Vector3 nB))
        {
            if (tB < bestT) { bestT = tB; bestN = nB; }
        }

        return bestT != float.PositiveInfinity;
    }

    // Möller–Trumbore
    private static bool RayTri(Vector3 ro, Vector3 rd, Vector3 a, Vector3 b, Vector3 c, out float t, out Vector3 n)
    {
        t = 0f;
        n = Vector3.up;

        Vector3 e1 = b - a;
        Vector3 e2 = c - a;
        Vector3 p = Vector3.Cross(rd, e2);
        float det = Vector3.Dot(e1, p);

        if (Mathf.Abs(det) < 1e-8f) return false;
        float invDet = 1f / det;

        Vector3 s = ro - a;
        float u = Vector3.Dot(s, p) * invDet;
        if (u < 0f || u > 1f) return false;

        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(rd, q) * invDet;
        if (v < 0f || (u + v) > 1f) return false;

        float tt = Vector3.Dot(e2, q) * invDet;
        if (tt <= 0f) return false;

        t = tt;
        n = Vector3.Normalize(Vector3.Cross(e1, e2));
        return true;
    }

    private static bool RayAabb(Vector3 ro, Vector3 rd, Bounds b, out float tMin, out float tMax)
    {
        tMin = 0f;
        tMax = float.PositiveInfinity;

        Vector3 min = b.min;
        Vector3 max = b.max;

        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? ro.x : axis == 1 ? ro.y : ro.z;
            float d = axis == 0 ? rd.x : axis == 1 ? rd.y : rd.z;
            float mn = axis == 0 ? min.x : axis == 1 ? min.y : min.z;
            float mx = axis == 0 ? max.x : axis == 1 ? max.y : max.z;

            if (Mathf.Abs(d) < 1e-8f)
            {
                if (o < mn || o > mx) return false;
                continue;
            }

            float inv = 1f / d;
            float t1 = (mn - o) * inv;
            float t2 = (mx - o) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);

            tMin = Mathf.Max(tMin, t1);
            tMax = Mathf.Min(tMax, t2);
            if (tMin > tMax) return false;
        }

        return true;
    }
}
