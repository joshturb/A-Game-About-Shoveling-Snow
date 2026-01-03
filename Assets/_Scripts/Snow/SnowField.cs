// SnowField.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

[RequireComponent(typeof(SplineContainer))]
public class SnowField : MonoBehaviour
{
    private SplineContainer splineContainer;

    [Header("Chunking")]
    [SerializeField, Min(4)] private int chunkCells = 32;           // cells per chunk in X/Z
    [SerializeField, Min(0.01f)] private float edgeFadeWidth = 1.0f; // meters

    // ---- Runtime generated data ----
    private List<Vector3> polyWorld;        // closed polygon, world points (y ignored)
    private float detail;                  // grid spacing
    private float minX, minZ, maxX, maxZ;
    private int nx, nz;                    // grid cells
    private bool[,] insideMask;            // grid vertex inside polygon (static)
    private float[,] falloff;              // edge falloff at grid vertex (0..1, static)
    private float[,] rawDepth;             // editable depth value at grid vertex (dynamic)

    private Chunk[,] chunks;
    private int chunkCountX, chunkCountZ;
    private bool[,] dirty;
    private readonly List<Chunk> dirtyList = new();

    private bool initialized;

    private sealed class Chunk
    {
        public int cx, cz;
        public int x0, x1;   // cell range [x0, x1)
        public int z0, z1;   // cell range [z0, z1)

        public GameObject go;
        public Mesh mesh;
        public MeshFilter mf;
        public MeshRenderer mr;
        public MeshCollider mc;

        public Vector3[] verts;
        public int[] gxs;
        public int[] gzs;

        public int localVertCount;
    }

    private void Awake()
    {
        splineContainer = GetComponent<SplineContainer>();
    }

    public bool ContainsWorld(Vector3 worldPos)
    {
        if (!initialized) return false;
        WorldToGrid(worldPos, out int gx, out int gz);
        return insideMask[gx, gz];
    }

    public void Initialize()
    {
        if (initialized) return;

        if (SnowController.Instance == null)
        {
            Debug.LogError("[SnowField] SnowController.Instance is null.");
            return;
        }

        BuildFieldData();
        BuildChunks();

        initialized = true;
    }

    // Clears toward 0 with a soft edge (full clear inside inner radius).
    public void RemoveToZeroStamp(Vector3 worldCenter, float radius, float innerFullClear01, float amount)
    {
        if (!initialized) return;

        radius = Mathf.Max(0.001f, radius);
        innerFullClear01 = Mathf.Clamp01(innerFullClear01);
        amount = Mathf.Max(0f, amount);
        if (amount <= 0f) return;

        float innerR = radius * innerFullClear01;
        float denom = Mathf.Max(0.0001f, radius - innerR);
        float r2 = radius * radius;

        int minGX = Mathf.Clamp(Mathf.FloorToInt((worldCenter.x - radius - minX) / detail), 0, nx);
        int maxGX = Mathf.Clamp(Mathf.CeilToInt((worldCenter.x + radius - minX) / detail), 0, nx);
        int minGZ = Mathf.Clamp(Mathf.FloorToInt((worldCenter.z - radius - minZ) / detail), 0, nz);
        int maxGZ = Mathf.Clamp(Mathf.CeilToInt((worldCenter.z + radius - minZ) / detail), 0, nz);

        for (int gz = minGZ; gz <= maxGZ; gz++)
        {
            float wz = minZ + gz * detail;
            for (int gx = minGX; gx <= maxGX; gx++)
            {
                if (!insideMask[gx, gz]) continue;

                float wx = minX + gx * detail;

                float dx = wx - worldCenter.x;
                float dz = wz - worldCenter.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;

                float old = rawDepth[gx, gz];
                if (old <= 0f) continue;

                float d = Mathf.Sqrt(d2);

                float w;
                if (d <= innerR) w = 1f;
                else
                {
                    float t = (d - innerR) / denom;  // 0..1
                    w = Smooth01(1f - t);           // 1..0
                }

                rawDepth[gx, gz] = Mathf.Max(0f, old - amount * w);
            }
        }

        MarkDirtyByGridBounds(minGX, maxGX, minGZ, maxGZ);
        FlushDirtyChunks();
    }

    // Mass-conserving plow: moves depth from a blade footprint forward into a berm.
    public void Plow(Vector3 playerPos, Vector3 forwardDir, float halfWidth, float length, float moveAmount, float depositForwardDistance, float sidewaysSpill)
    {
        if (!initialized) return;

        forwardDir.y = 0f;
        if (forwardDir.sqrMagnitude < 1e-6f) return;
        forwardDir.Normalize();

        halfWidth = Mathf.Max(0.01f, halfWidth);
        length = Mathf.Max(0.01f, length);
        moveAmount = Mathf.Max(0f, moveAmount);
        depositForwardDistance = Mathf.Max(0.01f, depositForwardDistance);
        sidewaysSpill = Mathf.Clamp(sidewaysSpill, 0f, 0.5f);

        Vector3 right = Vector3.Cross(Vector3.up, forwardDir).normalized;

        Vector3 bladeCenter = playerPos + forwardDir * (length * 0.5f);
        Vector3 depositCenter = bladeCenter + forwardDir * depositForwardDistance;

        float halfL = length * 0.5f;

        // Blade AABB in XZ
        float minBX = bladeCenter.x - halfWidth;
        float maxBX = bladeCenter.x + halfWidth;
        float minBZ = bladeCenter.z - halfL;
        float maxBZ = bladeCenter.z + halfL;

        int minGX = Mathf.Clamp(Mathf.FloorToInt((minBX - minX) / detail), 0, nx);
        int maxGX = Mathf.Clamp(Mathf.CeilToInt((maxBX - minX) / detail), 0, nx);
        int minGZ = Mathf.Clamp(Mathf.FloorToInt((minBZ - minZ) / detail), 0, nz);
        int maxGZ = Mathf.Clamp(Mathf.CeilToInt((maxBZ - minZ) / detail), 0, nz);

        float movedTotal = 0f;

        // Remove under blade
        for (int gz = minGZ; gz <= maxGZ; gz++)
        {
            float wz = minZ + gz * detail;
            for (int gx = minGX; gx <= maxGX; gx++)
            {
                if (!insideMask[gx, gz]) continue;

                float wx = minX + gx * detail;

                Vector3 p = new Vector3(wx, 0f, wz);
                Vector3 to = p - new Vector3(bladeCenter.x, 0f, bladeCenter.z);

                float lx = Vector3.Dot(to, right);
                float lz = Vector3.Dot(to, forwardDir);

                if (Mathf.Abs(lx) > halfWidth) continue;
                if (Mathf.Abs(lz) > halfL) continue;

                float w = (1f - (Mathf.Abs(lx) / halfWidth)) * (1f - (Mathf.Abs(lz) / halfL));
                if (w <= 0f) continue;

                float old = rawDepth[gx, gz];
                if (old <= 0f) continue;

                float remove = moveAmount * w;
                float newV = Mathf.Max(0f, old - remove);
                float actual = old - newV;

                if (actual <= 0f) continue;

                rawDepth[gx, gz] = newV;
                movedTotal += actual;
            }
        }

        if (movedTotal > 0f)
        {
            float forwardPortion = Mathf.Clamp01(1f - sidewaysSpill * 2f);
            float forwardAmt = movedTotal * forwardPortion;
            float sideAmt = movedTotal * sidewaysSpill;

            float depositRadius = Mathf.Max(halfWidth * 0.9f, detail * 1.5f);

            AddStamp(depositCenter, depositRadius, forwardAmt);
            if (sidewaysSpill > 0f)
            {
                AddStamp(depositCenter + right * (halfWidth * 0.9f), depositRadius, sideAmt);
                AddStamp(depositCenter - right * (halfWidth * 0.9f), depositRadius, sideAmt);
            }
        }

        // Mark dirty: blade + deposit zones
        MarkDirtyByWorldAABB(
            bladeCenter.x - halfWidth, bladeCenter.x + halfWidth,
            bladeCenter.z - halfL, bladeCenter.z + halfL);

        float depR = Mathf.Max(halfWidth * 0.9f, detail * 1.5f) + (halfWidth * 1.0f);
        MarkDirtyByWorldAABB(
            depositCenter.x - depR, depositCenter.x + depR,
            depositCenter.z - depR, depositCenter.z + depR);

        FlushDirtyChunks();
    }

    // ----- Build -----

    private void BuildFieldData()
    {
        var settings = SnowController.Instance.GetSnowSettings();
        detail = Mathf.Max(0.05f, settings.snowDetail);

        if (splineContainer.Splines.Count == 0)
        {
            Debug.LogError("[SnowField] SplineContainer has no splines.");
            return;
        }

        Spline spline = splineContainer.Splines[0];
        polyWorld = SampleSplineWorldPolygon(spline, detail);

        // Bounds in XZ
        minX = float.PositiveInfinity; maxX = float.NegativeInfinity;
        minZ = float.PositiveInfinity; maxZ = float.NegativeInfinity;
        for (int i = 0; i < polyWorld.Count; i++)
        {
            Vector3 p = polyWorld[i];
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z;
            if (p.z > maxZ) maxZ = p.z;
        }

        nx = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / detail));
        nz = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / detail));

        insideMask = new bool[nx + 1, nz + 1];
        falloff = new float[nx + 1, nz + 1];
        rawDepth = new float[nx + 1, nz + 1];

        for (int gz = 0; gz <= nz; gz++)
        {
            float wz = minZ + gz * detail;
            for (int gx = 0; gx <= nx; gx++)
            {
                float wx = minX + gx * detail;

                bool inside = PointInPolygonXZ(polyWorld, wx, wz);
                insideMask[gx, gz] = inside;

                if (!inside)
                {
                    falloff[gx, gz] = 0f;
                    rawDepth[gx, gz] = 0f;
                    continue;
                }

                float dist = MinDistanceToPolygonEdgesXZ(polyWorld, wx, wz);
                falloff[gx, gz] = Smooth01(dist / Mathf.Max(0.0001f, edgeFadeWidth));
                rawDepth[gx, gz] = SnowController.Instance.SampleWorld(new Vector3(wx, 0f, wz));
            }
        }

        chunkCountX = Mathf.Max(1, Mathf.CeilToInt(nx / (float)chunkCells));
        chunkCountZ = Mathf.Max(1, Mathf.CeilToInt(nz / (float)chunkCells));

        chunks = new Chunk[chunkCountX, chunkCountZ];
        dirty = new bool[chunkCountX, chunkCountZ];
        dirtyList.Clear();
    }

    private void BuildChunks()
    {
        // Clean old chunk children
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform c = transform.GetChild(i);
            if (c.name.StartsWith("SnowChunk_"))
                Destroy(c.gameObject);
        }

        var settings = SnowController.Instance.GetSnowSettings();

        for (int cz = 0; cz < chunkCountZ; cz++)
        {
            for (int cx = 0; cx < chunkCountX; cx++)
            {
                int x0 = cx * chunkCells;
                int z0 = cz * chunkCells;
                int x1 = Mathf.Min(nx, x0 + chunkCells);
                int z1 = Mathf.Min(nz, z0 + chunkCells);

                Chunk ch = new Chunk
                {
                    cx = cx, cz = cz,
                    x0 = x0, x1 = x1,
                    z0 = z0, z1 = z1
                };

                ch.go = new GameObject($"SnowChunk_{cx}_{cz}");
                ch.go.transform.SetParent(transform, false);
                ch.mf = ch.go.AddComponent<MeshFilter>();
                ch.mr = ch.go.AddComponent<MeshRenderer>();
                ch.mc = ch.go.AddComponent<MeshCollider>();

                if (settings.snowMaterial != null)
                    ch.mr.sharedMaterial = settings.snowMaterial;

                ch.mesh = BuildChunkMesh(ch);
                ch.mf.sharedMesh = ch.mesh;

                ch.mc.sharedMesh = null;
                ch.mc.sharedMesh = ch.mesh;

                chunks[cx, cz] = ch;
            }
        }
    }

    private Mesh BuildChunkMesh(Chunk ch)
    {
        // Vertex range (grid vertices) for this chunk includes +1 border
        int vx0 = ch.x0;
        int vz0 = ch.z0;
        int vx1 = ch.x1; // inclusive vertices end
        int vz1 = ch.z1;

        int vxCount = (vx1 - vx0) + 1;
        int vzCount = (vz1 - vz0) + 1;

        int[,] localMap = new int[vxCount, vzCount];
        for (int i = 0; i < vxCount; i++)
            for (int j = 0; j < vzCount; j++)
                localMap[i, j] = -1;

        var verts = new List<Vector3>(vxCount * vzCount);
        var uvs = new List<Vector2>(vxCount * vzCount);
        var tris = new List<int>((ch.x1 - ch.x0) * (ch.z1 - ch.z0) * 6);

        var gxs = new List<int>(vxCount * vzCount);
        var gzs = new List<int>(vxCount * vzCount);

        int GetVert(int gx, int gz)
        {
            int lx = gx - vx0;
            int lz = gz - vz0;

            int id = localMap[lx, lz];
            if (id != -1) return id;

            float wx = minX + gx * detail;
            float wz = minZ + gz * detail;

            float y = rawDepth[gx, gz] * falloff[gx, gz];

            Vector3 world = new Vector3(wx, transform.position.y + y, wz);
            Vector3 local = transform.InverseTransformPoint(world);

            id = verts.Count;
            verts.Add(local);

            float u = (maxX - minX) > 0.0001f ? (wx - minX) / (maxX - minX) : 0f;
            float v = (maxZ - minZ) > 0.0001f ? (wz - minZ) / (maxZ - minZ) : 0f;
            uvs.Add(new Vector2(u, v));

            gxs.Add(gx);
            gzs.Add(gz);

            localMap[lx, lz] = id;
            return id;
        }

        // Build triangles for cells whose corners intersect polygon (OR rule)
        for (int x = ch.x0; x < ch.x1; x++)
        {
            for (int z = ch.z0; z < ch.z1; z++)
            {
                bool i00 = insideMask[x, z];
                bool i10 = insideMask[x + 1, z];
                bool i01 = insideMask[x, z + 1];
                bool i11 = insideMask[x + 1, z + 1];

                if (!(i00 || i10 || i01 || i11))
                    continue;

                int v00 = GetVert(x, z);
                int v10 = GetVert(x + 1, z);
                int v01 = GetVert(x, z + 1);
                int v11 = GetVert(x + 1, z + 1);

                tris.Add(v00); tris.Add(v01); tris.Add(v10);
                tris.Add(v10); tris.Add(v01); tris.Add(v11);
            }
        }

        // If no triangles, still return an empty mesh (safe)
        Mesh mesh = new Mesh
        {
            name = $"SnowChunkMesh_{ch.cx}_{ch.cz}",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };

        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        ch.verts = mesh.vertices;
        ch.gxs = gxs.ToArray();
        ch.gzs = gzs.ToArray();
        ch.localVertCount = ch.verts.Length;

        return mesh;
    }

    // ----- Chunk updates -----

    private void MarkDirtyByWorldAABB(float minWX, float maxWX, float minWZ, float maxWZ)
    {
        int minGX = Mathf.Clamp(Mathf.FloorToInt((minWX - minX) / detail), 0, nx);
        int maxGX = Mathf.Clamp(Mathf.CeilToInt((maxWX - minX) / detail), 0, nx);
        int minGZ = Mathf.Clamp(Mathf.FloorToInt((minWZ - minZ) / detail), 0, nz);
        int maxGZ = Mathf.Clamp(Mathf.CeilToInt((maxWZ - minZ) / detail), 0, nz);
        MarkDirtyByGridBounds(minGX, maxGX, minGZ, maxGZ);
    }

    private void MarkDirtyByGridBounds(int minGX, int maxGX, int minGZ, int maxGZ)
    {
        // Expand by 1 vertex to cover border-duplicated vertices across neighbor chunks
        minGX = Mathf.Clamp(minGX - 1, 0, nx);
        maxGX = Mathf.Clamp(maxGX + 1, 0, nx);
        minGZ = Mathf.Clamp(minGZ - 1, 0, nz);
        maxGZ = Mathf.Clamp(maxGZ + 1, 0, nz);

        int minCX = Mathf.Clamp(minGX / chunkCells, 0, chunkCountX - 1);
        int maxCX = Mathf.Clamp(maxGX / chunkCells, 0, chunkCountX - 1);
        int minCZ = Mathf.Clamp(minGZ / chunkCells, 0, chunkCountZ - 1);
        int maxCZ = Mathf.Clamp(maxGZ / chunkCells, 0, chunkCountZ - 1);

        for (int cz = minCZ; cz <= maxCZ; cz++)
        {
            for (int cx = minCX; cx <= maxCX; cx++)
            {
                if (dirty[cx, cz]) continue;
                dirty[cx, cz] = true;
                dirtyList.Add(chunks[cx, cz]);
            }
        }
    }

    private void FlushDirtyChunks()
    {
        if (dirtyList.Count == 0) return;

        for (int i = 0; i < dirtyList.Count; i++)
        {
            Chunk ch = dirtyList[i];
            if (ch == null || ch.mesh == null) continue;

            // Update only this chunk's vertex heights from global grid
            for (int v = 0; v < ch.localVertCount; v++)
            {
                int gx = ch.gxs[v];
                int gz = ch.gzs[v];
                float y = rawDepth[gx, gz] * falloff[gx, gz];

                Vector3 p = ch.verts[v];
                p.y = y;
                ch.verts[v] = p;
            }

            ch.mesh.vertices = ch.verts;
            ch.mesh.RecalculateNormals();
            ch.mesh.RecalculateBounds();

            ch.mc.sharedMesh = null;
            ch.mc.sharedMesh = ch.mesh;

            dirty[ch.cx, ch.cz] = false;
        }

        dirtyList.Clear();
    }

    // ----- Deposit helper -----

    private void AddStamp(Vector3 worldCenter, float radius, float totalAmount)
    {
        radius = Mathf.Max(0.001f, radius);
        totalAmount = Mathf.Max(0f, totalAmount);
        if (totalAmount <= 0f) return;

        int minGX = Mathf.Clamp(Mathf.FloorToInt((worldCenter.x - radius - minX) / detail), 0, nx);
        int maxGX = Mathf.Clamp(Mathf.CeilToInt((worldCenter.x + radius - minX) / detail), 0, nx);
        int minGZ = Mathf.Clamp(Mathf.FloorToInt((worldCenter.z - radius - minZ) / detail), 0, nz);
        int maxGZ = Mathf.Clamp(Mathf.CeilToInt((worldCenter.z + radius - minZ) / detail), 0, nz);

        float r2 = radius * radius;

        float weightSum = 0f;
        for (int gz = minGZ; gz <= maxGZ; gz++)
        {
            float wz = minZ + gz * detail;
            for (int gx = minGX; gx <= maxGX; gx++)
            {
                if (!insideMask[gx, gz]) continue;

                float wx = minX + gx * detail;
                float dx = wx - worldCenter.x;
                float dz = wz - worldCenter.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;

                float t = Mathf.Sqrt(d2) / radius;
                float w = (1f - t);
                w *= w;
                weightSum += w;
            }
        }

        if (weightSum <= 0f) return;

        float perWeight = totalAmount / weightSum;

        for (int gz = minGZ; gz <= maxGZ; gz++)
        {
            float wz = minZ + gz * detail;
            for (int gx = minGX; gx <= maxGX; gx++)
            {
                if (!insideMask[gx, gz]) continue;

                float wx = minX + gx * detail;
                float dx = wx - worldCenter.x;
                float dz = wz - worldCenter.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;

                float t = Mathf.Sqrt(d2) / radius;
                float w = (1f - t);
                w *= w;

                float add = perWeight * w;
                if (add <= 0f) continue;

                rawDepth[gx, gz] += add;
            }
        }

        MarkDirtyByGridBounds(minGX, maxGX, minGZ, maxGZ);
    }

    // ----- Grid mapping -----

    private void WorldToGrid(Vector3 worldPos, out int gx, out int gz)
    {
        gx = Mathf.Clamp(Mathf.RoundToInt((worldPos.x - minX) / detail), 0, nx);
        gz = Mathf.Clamp(Mathf.RoundToInt((worldPos.z - minZ) / detail), 0, nz);
    }

    // ----- Polygon sampling + geometry -----

    private List<Vector3> SampleSplineWorldPolygon(Spline spline, float detailStep)
    {
        int steps = 256;
        try
        {
            float len = spline.GetLength();
            steps = Mathf.Clamp(Mathf.CeilToInt(len / Mathf.Max(0.001f, detailStep)), 64, 4096);
        }
        catch { }

        var points = new List<Vector3>(steps + 1);
        for (int i = 0; i < steps; i++)
        {
            float t = i / (float)steps;
            Vector3 local = spline.EvaluatePosition(t);
            Vector3 world = splineContainer.transform.TransformPoint(local);
            points.Add(world);
        }

        if ((points[0] - points[^1]).sqrMagnitude > 0.0001f)
            points.Add(points[0]);

        return points;
    }

    private static bool PointInPolygonXZ(IReadOnlyList<Vector3> poly, float x, float z)
    {
        bool inside = false;
        int j = poly.Count - 1;

        for (int i = 0; i < poly.Count; i++)
        {
            float xi = poly[i].x, zi = poly[i].z;
            float xj = poly[j].x, zj = poly[j].z;

            bool crosses = ((zi > z) != (zj > z)) &&
                           (x < (xj - xi) * (z - zi) / ((zj - zi) + 1e-9f) + xi);

            if (crosses) inside = !inside;
            j = i;
        }

        return inside;
    }

    private static float MinDistanceToPolygonEdgesXZ(IReadOnlyList<Vector3> poly, float x, float z)
    {
        float best = float.PositiveInfinity;
        int last = poly.Count - 1;

        for (int i = 0; i < last; i++)
        {
            Vector3 a = poly[i];
            Vector3 b = poly[i + 1];
            float d = DistancePointToSegmentXZ(x, z, a.x, a.z, b.x, b.z);
            if (d < best) best = d;
        }

        if ((poly[0] - poly[last]).sqrMagnitude > 0.0001f)
        {
            Vector3 a = poly[last];
            Vector3 b = poly[0];
            float d = DistancePointToSegmentXZ(x, z, a.x, a.z, b.x, b.z);
            if (d < best) best = d;
        }

        return best;
    }

    private static float DistancePointToSegmentXZ(float px, float pz, float ax, float az, float bx, float bz)
    {
        float abx = bx - ax;
        float abz = bz - az;

        float apx = px - ax;
        float apz = pz - az;

        float abLen2 = (abx * abx) + (abz * abz);
        if (abLen2 <= 1e-9f)
            return Mathf.Sqrt((apx * apx) + (apz * apz));

        float t = ((apx * abx) + (apz * abz)) / abLen2;
        t = Mathf.Clamp01(t);

        float cx = ax + abx * t;
        float cz = az + abz * t;

        float dx = px - cx;
        float dz = pz - cz;

        return Mathf.Sqrt((dx * dx) + (dz * dz));
    }

    private static float Smooth01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }
}
