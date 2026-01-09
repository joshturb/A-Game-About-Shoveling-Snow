// SnowHeightfieldCollider.cs
using System;
using System.Collections.Generic;
using UnityEngine;

/// Heightfield-only collider for grid-like meshes (one Y per XZ).
/// Builds XZ indexing once; Refresh() after Y edits.
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

    private float[] _xs, _zs;
    private int _w, _h;
    private int[] _grid;                // [x + z*w] -> vertex index or -1

    private float _x0, _z0;
    private float _dx, _dz;
    private bool _uniform;
    private float _eps;

    private const float HUGE_Y = 100000f;

    public SnowHeightfieldCollider(Transform snowTransform, Mesh mesh)
    {
        _tf = snowTransform ? snowTransform : throw new ArgumentNullException(nameof(snowTransform));
        _mesh = mesh ? mesh : throw new ArgumentNullException(nameof(mesh));
        _verts = _mesh.vertices ?? throw new InvalidOperationException("Mesh has no vertices.");

        BuildIndex();
    }

    public void Refresh(Vector3[] verts)
    {
        _verts = verts ?? throw new InvalidOperationException("Mesh has no vertices.");
    }

    public bool ContainsPoint(Vector3 worldPoint)
    {
        Vector3 lp = _tf.InverseTransformPoint(worldPoint);
        if (!TryCell(lp, out int cx, out int cz, out float fx, out float fz)) return false;
        return lp.y <= SampleY(cx, cz, fx, fz);
    }

    public bool TryGetHeightWorld(Vector3 worldPoint, out float heightWorld)
    {
        heightWorld = 0f;

        Vector3 lp = _tf.InverseTransformPoint(worldPoint);
        if (!TryCell(lp, out int cx, out int cz, out float fx, out float fz))
            return false;

        float yLocal = SampleY(cx, cz, fx, fz);

        Vector3 wp = _tf.TransformPoint(new Vector3(lp.x, yLocal, lp.z));
        heightWorld = wp.y;
        return true;
    }

    public bool Raycast(Ray worldRay, out Hit hit, float maxDistance = 500f)
    {
        hit = default;

        Vector3 ro = _tf.InverseTransformPoint(worldRay.origin);
        Vector3 rd = _tf.InverseTransformDirection(worldRay.direction);

        Bounds slab = new Bounds(
            new Vector3(_x0 + (_w - 1) * _dx * 0.5f, 0f, _z0 + (_h - 1) * _dz * 0.5f),
            new Vector3((_w - 1) * _dx, HUGE_Y * 2f, (_h - 1) * _dz)
        );

        if (!RayAabb(ro, rd, slab, out float tEnter, out float tExit)) return false;
        if (tExit < 0f) return false;

        float t0 = Mathf.Max(0f, tEnter);
        float t1 = Mathf.Min(tExit, maxDistance);
        if (t1 < t0) return false;

        return _uniform
            ? RaycastDDA(worldRay, ro, rd, t0, t1, out hit)
            : RaycastFallback(worldRay, ro, rd, t0, t1, out hit);
    }

    // ---------- index build ----------

    private void BuildIndex()
    {
        var b = _mesh.bounds;
        float span = Mathf.Max(b.size.x, b.size.z);
        _eps = Mathf.Max(1e-6f, span * 1e-5f);

        var xKeys = new Dictionary<int, float>(256);
        var zKeys = new Dictionary<int, float>(256);

        for (int i = 0; i < _verts.Length; i++)
        {
            int kx = Mathf.RoundToInt(_verts[i].x / _eps);
            int kz = Mathf.RoundToInt(_verts[i].z / _eps);
            if (!xKeys.ContainsKey(kx)) xKeys.Add(kx, _verts[i].x);
            if (!zKeys.ContainsKey(kz)) zKeys.Add(kz, _verts[i].z);
        }

        _xs = new float[xKeys.Count];
        _zs = new float[zKeys.Count];

        int xi = 0; foreach (var kv in xKeys) _xs[xi++] = kv.Value;
        int zi = 0; foreach (var kv in zKeys) _zs[zi++] = kv.Value;

        Array.Sort(_xs);
        Array.Sort(_zs);

        _w = _xs.Length;
        _h = _zs.Length;
        if (_w < 2 || _h < 2) throw new InvalidOperationException($"Invalid grid: w={_w}, h={_h}");

        _x0 = _xs[0];
        _z0 = _zs[0];

        _dx = _xs[1] - _xs[0];
        _dz = _zs[1] - _zs[0];

        _uniform = IsUniform(_xs, _eps) && IsUniform(_zs, _eps) && Mathf.Abs(_dx) > _eps && Mathf.Abs(_dz) > _eps;

        var xIndex = new Dictionary<int, int>(_w);
        var zIndex = new Dictionary<int, int>(_h);

        for (int i = 0; i < _w; i++) xIndex[Mathf.RoundToInt(_xs[i] / _eps)] = i;
        for (int i = 0; i < _h; i++) zIndex[Mathf.RoundToInt(_zs[i] / _eps)] = i;

        _grid = new int[_w * _h];
        for (int i = 0; i < _grid.Length; i++) _grid[i] = -1;

        for (int vi = 0; vi < _verts.Length; vi++)
        {
            int kx = Mathf.RoundToInt(_verts[vi].x / _eps);
            int kz = Mathf.RoundToInt(_verts[vi].z / _eps);

            if (!xIndex.TryGetValue(kx, out int gx)) continue;
            if (!zIndex.TryGetValue(kz, out int gz)) continue;

            int gi = gx + gz * _w;
            if (_grid[gi] == -1) _grid[gi] = vi;
        }
    }

    private static bool IsUniform(float[] a, float eps)
    {
        float d0 = a[1] - a[0];
        for (int i = 2; i < a.Length; i++)
            if (Mathf.Abs((a[i] - a[i - 1]) - d0) > eps * 10f) return false;
        return true;
    }

    private int V(int x, int z) => _grid[x + z * _w];

    // ---------- sampling ----------

    private bool TryCell(Vector3 lp, out int cx, out int cz, out float fx, out float fz)
    {
        cx = cz = 0; fx = fz = 0f;

        if (lp.x < _xs[0] || lp.x > _xs[_w - 1] || lp.z < _zs[0] || lp.z > _zs[_h - 1])
            return false;

        if (_uniform)
        {
            float gx = (lp.x - _x0) / _dx;
            float gz = (lp.z - _z0) / _dz;

            cx = Mathf.Clamp(Mathf.FloorToInt(gx), 0, _w - 2);
            cz = Mathf.Clamp(Mathf.FloorToInt(gz), 0, _h - 2);

            fx = Mathf.Clamp01(gx - cx);
            fz = Mathf.Clamp01(gz - cz);
            return true;
        }

        cx = FindLower(_xs, lp.x);
        cz = FindLower(_zs, lp.z);

        cx = Mathf.Clamp(cx, 0, _w - 2);
        cz = Mathf.Clamp(cz, 0, _h - 2);

        float x0 = _xs[cx], x1 = _xs[cx + 1];
        float z0 = _zs[cz], z1 = _zs[cz + 1];

        fx = Mathf.Clamp01((lp.x - x0) / Mathf.Max(1e-6f, (x1 - x0)));
        fz = Mathf.Clamp01((lp.z - z0) / Mathf.Max(1e-6f, (z1 - z0)));
        return true;
    }

    private static int FindLower(float[] a, float v)
    {
        int idx = Array.BinarySearch(a, v);
        if (idx >= 0) return idx;
        idx = ~idx;
        return idx - 1;
    }

    private float SampleY(int cx, int cz, float fx, float fz)
    {
        int i00 = V(cx, cz);
        int i10 = V(cx + 1, cz);
        int i01 = V(cx, cz + 1);
        int i11 = V(cx + 1, cz + 1);
        if (i00 < 0 || i10 < 0 || i01 < 0 || i11 < 0) return 0f;

        float y00 = _verts[i00].y;
        float y10 = _verts[i10].y;
        float y01 = _verts[i01].y;
        float y11 = _verts[i11].y;

        float ya = Mathf.Lerp(y00, y10, fx);
        float yb = Mathf.Lerp(y01, y11, fx);
        return Mathf.Lerp(ya, yb, fz);
    }

    // ---------- raycast ----------

    private bool RaycastDDA(Ray worldRay, Vector3 ro, Vector3 rd, float t0, float t1, out Hit hit)
    {
        hit = default;

        Vector3 pEnter = ro + rd * t0;

        int cx = Mathf.Clamp(Mathf.FloorToInt((pEnter.x - _x0) / _dx), 0, _w - 2);
        int cz = Mathf.Clamp(Mathf.FloorToInt((pEnter.z - _z0) / _dz), 0, _h - 2);

        int stepX = (rd.x >= 0f) ? 1 : -1;
        int stepZ = (rd.z >= 0f) ? 1 : -1;

        float nextX = (stepX > 0) ? (_x0 + (cx + 1) * _dx) : (_x0 + cx * _dx);
        float nextZ = (stepZ > 0) ? (_z0 + (cz + 1) * _dz) : (_z0 + cz * _dz);

        float tMaxX = (Mathf.Abs(rd.x) < 1e-8f) ? float.PositiveInfinity : (nextX - pEnter.x) / rd.x;
        float tMaxZ = (Mathf.Abs(rd.z) < 1e-8f) ? float.PositiveInfinity : (nextZ - pEnter.z) / rd.z;

        float tDeltaX = (Mathf.Abs(rd.x) < 1e-8f) ? float.PositiveInfinity : _dx / Mathf.Abs(rd.x);
        float tDeltaZ = (Mathf.Abs(rd.z) < 1e-8f) ? float.PositiveInfinity : _dz / Mathf.Abs(rd.z);

        int safety = (_w + _h) * 4;

        for (int iter = 0; iter < safety; iter++)
        {
            if (RaycastCell(ro, rd, cx, cz, out float cellT, out Vector3 cellN))
            {
                if (cellT >= t0 && cellT <= t1)
                {
                    Vector3 localHit = ro + rd * cellT;
                    hit.pointWorld = _tf.TransformPoint(localHit);
                    hit.normalWorld = _tf.TransformDirection(cellN).normalized;
                    hit.distanceWorld = Vector3.Distance(worldRay.origin, hit.pointWorld);
                    return true;
                }
            }

            float absTMaxX = t0 + tMaxX;
            float absTMaxZ = t0 + tMaxZ;

            if (absTMaxX < absTMaxZ)
            {
                cx += stepX;
                tMaxX += tDeltaX;
                if (cx < 0 || cx >= _w - 1) break;
            }
            else
            {
                cz += stepZ;
                tMaxZ += tDeltaZ;
                if (cz < 0 || cz >= _h - 1) break;
            }

            if (Mathf.Min(absTMaxX, absTMaxZ) > t1) break;
        }

        return false;
    }

    private bool RaycastFallback(Ray worldRay, Vector3 ro, Vector3 rd, float t0, float t1, out Hit hit)
    {
        hit = default;

        const int steps = 512;
        float dt = (t1 - t0) / steps;
        if (dt <= 0f) return false;

        for (int s = 0; s <= steps; s++)
        {
            float t = t0 + dt * s;
            Vector3 p = ro + rd * t;

            if (!TryCell(p, out int cx, out int cz, out _, out _))
                continue;

            if (RaycastCell(ro, rd, cx, cz, out float cellT, out Vector3 cellN) && cellT >= t0 && cellT <= t1)
            {
                Vector3 localHit = ro + rd * cellT;
                hit.pointWorld = _tf.TransformPoint(localHit);
                hit.normalWorld = _tf.TransformDirection(cellN).normalized;
                hit.distanceWorld = Vector3.Distance(worldRay.origin, hit.pointWorld);
                return true;
            }
        }

        return false;
    }

    // Tests BOTH possible diagonals (4 tris) to avoid mismatch with render split.
    private bool RaycastCell(Vector3 ro, Vector3 rd, int cx, int cz, out float bestT, out Vector3 bestN)
    {
        bestT = float.PositiveInfinity;
        bestN = Vector3.up;

        int i00 = V(cx, cz);
        int i10 = V(cx + 1, cz);
        int i01 = V(cx, cz + 1);
        int i11 = V(cx + 1, cz + 1);
        if (i00 < 0 || i10 < 0 || i01 < 0 || i11 < 0) return false;

        Vector3 v00 = _verts[i00];
        Vector3 v10 = _verts[i10];
        Vector3 v01 = _verts[i01];
        Vector3 v11 = _verts[i11];

        // 00->11
        TryTri(ro, rd, v00, v10, v11, ref bestT, ref bestN);
        TryTri(ro, rd, v00, v11, v01, ref bestT, ref bestN);

        // 10->01
        TryTri(ro, rd, v00, v10, v01, ref bestT, ref bestN);
        TryTri(ro, rd, v10, v11, v01, ref bestT, ref bestN);

        return bestT != float.PositiveInfinity;
    }

    private static void TryTri(Vector3 ro, Vector3 rd, Vector3 a, Vector3 b, Vector3 c, ref float bestT, ref Vector3 bestN)
    {
        if (RayTri(ro, rd, a, b, c, out float t, out Vector3 n) && t < bestT)
        {
            bestT = t;
            bestN = n;
        }
    }

    private static bool RayTri(Vector3 ro, Vector3 rd, Vector3 a, Vector3 b, Vector3 c, out float t, out Vector3 n)
    {
        t = 0f; n = Vector3.up;

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
