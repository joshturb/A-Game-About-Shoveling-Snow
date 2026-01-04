using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

[Serializable]
public class Chunk
{
    public Voxel[,,] Voxels;
    public Vector3 Center;
    public Vector3Int Index;
    public GameObject Object;
    public Mesh Mesh;
    public static Action<Chunk> OnChunkLoaded;
    public static Action<Chunk> OnChunkUnloaded;

    public Chunk(Vector3 center, Vector3Int index, Voxel[,,] voxels)
    {
        Center = center;
        Voxels = voxels;
        Index = index;
    }
}

[Serializable]
public struct Voxel
{
    public float Density;

    public Voxel(byte density = 0)
    {
        Density = density;
    }
}

public struct Triangle {
    public Vector3 vertexA;
    public Vector3 vertexB;
    public Vector3 vertexC;

    public readonly Vector3 this [int i] {
        get {
            return i switch
            {
                0 => vertexA,
                1 => vertexB,
                _ => vertexC,
            };
        }
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct TriangleCBA
{
    public Vector3 vertexC;
    public Vector3 vertexB;
    public Vector3 vertexA;
}

public readonly struct SplineQuery
{
    public readonly List<Vector2> Poly;
    public readonly float IsoLevel;
    public readonly float AirDensity;
    public readonly float VoxelSize;
    public readonly float FalloffWidth;

    public SplineQuery(List<Vector2> poly, SnowSettings settings, float voxelSize, float falloffWidth)
    {
        Poly = poly;
        IsoLevel = settings.isoValue;
        AirDensity = settings.isoValue + 1f;
        VoxelSize = voxelSize;
        FalloffWidth = falloffWidth;
    }

    public bool Contains(Vector2 p)
    {
        bool inside = false;
        int j = Poly.Count - 1;

        for (int i = 0; i < Poly.Count; i++)
        {
            Vector2 a = Poly[i];
            Vector2 b = Poly[j];

            bool intersect = ((a.y > p.y) != (b.y > p.y)) &&
                             (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y + 1e-12f) + a.x);

            if (intersect) inside = !inside;
            j = i;
        }

        return inside;
    }

    public float InsideWeight(Vector2 p)
    {
        float d = DistanceToEdges(p);
        return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / FalloffWidth));
    }

    private static float DistPointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Vector2.Dot(p - a, ab) / (ab.sqrMagnitude + 1e-12f);
        t = Mathf.Clamp01(t);
        return Vector2.Distance(p, a + t * ab);
    }

    private float DistanceToEdges(Vector2 p)
    {
        float minD = float.PositiveInfinity;
        int count = Poly.Count;

        for (int i = 0; i < count; i++)
        {
            Vector2 a = Poly[i];
            Vector2 b = Poly[(i + 1) % count];
            float d = DistPointToSegment(p, a, b);
            if (d < minD) minD = d;
        }

        return minD;
    }
}

public class VoxelUtils
{
    public static void ComputeNormalsFromDensityGradient(
        Vector3[] verticesLocal,
        Vector3[] outNormals,
        Func<Vector3, float> sampleDensityLocal,
        float h)
    {
        for (int i = 0; i < verticesLocal.Length; i++)
        {
            Vector3 p = verticesLocal[i];

            float dx = sampleDensityLocal(p + new Vector3(h, 0, 0)) - sampleDensityLocal(p - new Vector3(h, 0, 0));
            float dy = sampleDensityLocal(p + new Vector3(0, h, 0)) - sampleDensityLocal(p - new Vector3(0, h, 0));
            float dz = sampleDensityLocal(p + new Vector3(0, 0, h)) - sampleDensityLocal(p - new Vector3(0, 0, h));

            Vector3 n = new Vector3(dx, dy, dz);

            // WAS: (-n.normalized)
            outNormals[i] = (n.sqrMagnitude > 1e-12f) ? n.normalized : Vector3.up;
        }
    }

    public static bool TryWorldToVoxel(
        Dictionary<Vector3Int, Chunk> chunks,
        Vector3 worldPos,
        float chunkWorldSize,
        int chunkVoxelsPerAxis,
        float voxelSize,
        out Chunk chunk,
        out Vector3Int chunkIndex,
        out int vx, out int vy, out int vz,
        bool nearest = true)
    {
        chunk = null;
        chunkIndex = default;
        vx = vy = vz = 0;

        if (chunks == null)
            return false;

        // Chunk index in world space
        int cx = Mathf.FloorToInt(worldPos.x / chunkWorldSize);
        int cy = Mathf.FloorToInt(worldPos.y / chunkWorldSize);
        int cz = Mathf.FloorToInt(worldPos.z / chunkWorldSize);
        chunkIndex = new Vector3Int(cx, cy, cz);

        if (!chunks.TryGetValue(chunkIndex, out chunk) || chunk == null || chunk.Voxels == null)
            return false;

        float half = chunkWorldSize * 0.5f;
        Vector3 origin = chunk.Center - new Vector3(half, half, half);

        Vector3 local = worldPos - origin;

        float fx = local.x / voxelSize;
        float fy = local.y / voxelSize;
        float fz = local.z / voxelSize;

        // Use nearest for “click feel”, floor for “cell containment”
        vx = nearest ? Mathf.RoundToInt(fx) : Mathf.FloorToInt(fx);
        vy = nearest ? Mathf.RoundToInt(fy) : Mathf.FloorToInt(fy);
        vz = nearest ? Mathf.RoundToInt(fz) : Mathf.FloorToInt(fz);

        vx = Mathf.Clamp(vx, 0, chunkVoxelsPerAxis);
        vy = Mathf.Clamp(vy, 0, chunkVoxelsPerAxis);
        vz = Mathf.Clamp(vz, 0, chunkVoxelsPerAxis);

        return true;
    }

    // Voxel center in world space (matches TryWorldToVoxel’s origin math)
    public static Vector3 VoxelCenterWorld(Chunk chunk, int x, int y, int z, float chunkWorldSize, float voxelSize)
    {
        float half = chunkWorldSize * 0.5f;
        Vector3 origin = chunk.Center - new Vector3(half, half, half);
        return origin + new Vector3((x + 0.5f) * voxelSize, (y + 0.5f) * voxelSize, (z + 0.5f) * voxelSize);
    }

    public static HashSet<int> FindEdgeVertices(int[] triangles)
    {
        var edgeCount = new Dictionary<(int, int), int>();
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i];
            int b = triangles[i + 1];
            int c = triangles[i + 2];

            AddEdge(edgeCount, a, b);
            AddEdge(edgeCount, b, c);
            AddEdge(edgeCount, c, a);
        }

        var edgeVertices = new HashSet<int>();
        foreach (var edge in edgeCount)
        {
            if (edge.Value == 1)
            {
                edgeVertices.Add(edge.Key.Item1);
                edgeVertices.Add(edge.Key.Item2);
            }
        }

        return edgeVertices;
    }

    private static void AddEdge(Dictionary<(int, int), int> edges, int a, int b)
    {
        if (a > b) (a, b) = (b, a);
        edges.TryGetValue((a, b), out int count);
        edges[(a, b)] = count + 1;
    }

    public static void LaplacianSmooth(Mesh mesh, int iterations)
    {
        var vertices = mesh.vertices;
        var triangles = mesh.triangles;
        var edgeVertices = FindEdgeVertices(triangles);

        // Build neighbors
        List<int>[] neighbors = new List<int>[vertices.Length];
        for (int i = 0; i < neighbors.Length; i++)
            neighbors[i] = new List<int>();

        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            neighbors[a].Add(b); neighbors[a].Add(c);
            neighbors[b].Add(a); neighbors[b].Add(c);
            neighbors[c].Add(a); neighbors[c].Add(b);
        }

        var tempVertices = new Vector3[vertices.Length];
        for (int iter = 0; iter < iterations; iter++)
        {
            vertices.CopyTo(tempVertices, 0);
            for (int i = 0; i < vertices.Length; i++)
            {
                // Skip smoothing on edge vertices
                if (edgeVertices.Contains(i))
                    continue;

                var list = neighbors[i];
                if (list.Count == 0)
                    continue;

                Vector3 avg = Vector3.zero;
                foreach (int neighbor in list)
                    avg += tempVertices[neighbor];
                avg /= list.Count;

                // Move halfway towards the average position
                vertices[i] = Vector3.Lerp(vertices[i], avg, 0.5f);
            }
        }

        mesh.vertices = vertices;
    }

    public static void MergeVertices(ref Vector3[] vertices, ref int[] triangles, float threshold)
    {
        float inv = 1f / threshold;
        int[] map = new int[vertices.Length];
        var list = new List<Vector3>(vertices.Length);
        var dict = new Dictionary<int, int>(vertices.Length);

        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i];
            int key = HashVertex(v, inv);
            if (!dict.TryGetValue(key, out int idx))
            {
                idx = list.Count;
                list.Add(v);
                dict[key] = idx;
            }
            map[i] = idx;
        }

        for (int i = 0; i < triangles.Length; i++)
            triangles[i] = map[triangles[i]];

        vertices = list.ToArray();
    }

    private static int HashVertex(Vector3 v, float inv)
    {
        int x = Mathf.FloorToInt(v.x * inv);
        int y = Mathf.FloorToInt(v.y * inv);
        int z = Mathf.FloorToInt(v.z * inv);
        return (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);
    }

    public static Vector3 GetChunkCenter(int cx, int cy, int cz, float chunkWorldSize)
    {
        return new Vector3(
            (cx + 0.5f) * chunkWorldSize,
            (cy + 0.5f) * chunkWorldSize,
            (cz + 0.5f) * chunkWorldSize
        );
    }

    public static Vector3Int GetChunkIndexFromPosition(Vector3 position, int chunkSize, float voxelSize)
    {
        return new Vector3Int(
            Mathf.FloorToInt(position.x / (chunkSize * voxelSize)),
            Mathf.FloorToInt(position.y / (chunkSize * voxelSize)),
            Mathf.FloorToInt(position.z / (chunkSize * voxelSize))
        );
    }

    public static Vector3Int GetChunkCoord(Vector3 position, int chunkSize, float voxelSize)
    {
        return new Vector3Int(
            Mathf.FloorToInt(position.x / (chunkSize * voxelSize)),
            Mathf.FloorToInt(position.y / (chunkSize * voxelSize)),
            Mathf.FloorToInt(position.z / (chunkSize * voxelSize))
        );
    }

    public static Vector3 GetVoxelPosition(Chunk chunk, int x, int y, int z, float voxelSize)
    {
        return new Vector3(
            chunk.Center.x + x * voxelSize,
            chunk.Center.y + y * voxelSize,
            chunk.Center.z + z * voxelSize
        );
    }

    public static bool IsFloatingVoxel(Voxel[,,] voxels, int x, int y, int z)
    {
        int sizeX = voxels.GetLength(0);
        int sizeY = voxels.GetLength(1);
        int sizeZ = voxels.GetLength(2);

        // Check if the voxel is within bounds
        if (x <= 0 || x >= sizeX - 1 || y <= 0 || y >= sizeY - 1 || z <= 0 || z >= sizeZ - 1)
            return false;

        // Check if the voxel itself has a density of 0
        if (voxels[x, y, z].Density != 0)
            return false;

        // Check the six neighbors
        if (voxels[x - 1, y, z].Density == 1 &&
            voxels[x + 1, y, z].Density == 1 &&
            voxels[x, y - 1, z].Density == 1 &&
            voxels[x, y + 1, z].Density == 1 &&
            voxels[x, y, z - 1].Density == 1 &&
            voxels[x, y, z + 1].Density == 1)
        {
            return true;
        }

        return false;
    }

    public static void GetChunkSizing(SnowSettings settings, out float voxelSize, out int chunkVoxelsPerAxis, out float chunkWorldSize)
    {
        voxelSize = Mathf.Max(0.001f, settings.voxelSize);

        float desiredChunkWorldSize = Mathf.Max(voxelSize, settings.chunkSize);
        chunkVoxelsPerAxis = Mathf.Max(1, Mathf.RoundToInt(desiredChunkWorldSize / voxelSize));
        chunkWorldSize = chunkVoxelsPerAxis * voxelSize;
    }

    public static void GetChunkRange
    (
        float minX, float maxX,
        float minZ, float maxZ,
        float chunkWorldSize,
        out int minChunkX, out int maxChunkX,
        out int minChunkZ, out int maxChunkZ)
    {
        minChunkX = Mathf.FloorToInt(minX / chunkWorldSize);
        maxChunkX = Mathf.CeilToInt(maxX / chunkWorldSize) - 1;

        minChunkZ = Mathf.FloorToInt(minZ / chunkWorldSize);
        maxChunkZ = Mathf.CeilToInt(maxZ / chunkWorldSize) - 1;
    }

    public static bool TryBuildChunkVoxels(
        Vector3 chunkCenter,
        float groundY,
        int chunkVoxelsPerAxis,
        float chunkWorldSize,
        float voxelSize,
        SplineQuery query,
        out Voxel[,,] voxels)
    {
        voxels = new Voxel[chunkVoxelsPerAxis + 1, chunkVoxelsPerAxis + 1, chunkVoxelsPerAxis + 1];

        Vector3 half = Vector3.one * (chunkWorldSize * 0.5f);
        bool anyInside = false;

        for (int x = 0; x <= chunkVoxelsPerAxis; x++)
        {
            float wx = chunkCenter.x - half.x + x * voxelSize;

            for (int z = 0; z <= chunkVoxelsPerAxis; z++)
            {
                float wz = chunkCenter.z - half.z + z * voxelSize;
                Vector2 p2 = new Vector2(wx, wz);

                if (!query.Contains(p2))
                {
                    FillColumnAir(voxels, x, z, chunkVoxelsPerAxis, query.AirDensity);
                    continue;
                }

                anyInside = true;

                float w = query.InsideWeight(p2);
                float thickness = SnowController.Instance.SampleNoise(new Vector3(wx, 0f, wz));
                float surfaceY = groundY + thickness;

                for (int y = 0; y <= chunkVoxelsPerAxis; y++)
                {
                    float wy = chunkCenter.y - half.y + y * voxelSize;

                    float field = wy - surfaceY;
                    voxels[x, y, z].Density = Mathf.Lerp(query.AirDensity, field, w);
                }
            }
        }

        return anyInside;
    }

    private static void FillColumnAir(Voxel[,,] voxels, int x, int z, int maxY, float airDensity)
    {
        for (int y = 0; y <= maxY; y++)
            voxels[x, y, z].Density = airDensity;
    }

    public static bool TryGetSplinePolygonAndBoundsXZ(
        SplineContainer splineContainer,
        out List<Vector2> poly,
        out float groundY,
        out float minX, out float maxX,
        out float minZ, out float maxZ)
    {
        poly = null;
        groundY = 0f;
        minX = minZ = float.PositiveInfinity;
        maxX = maxZ = float.NegativeInfinity;

        if (splineContainer == null || splineContainer.Splines == null || splineContainer.Splines.Count == 0)
            return false;

        var spline = splineContainer.Splines[0];

        const int splineSamples = 256;
        poly = new List<Vector2>(splineSamples);

        float ySum = 0f;

        for (int i = 0; i < splineSamples; i++)
        {
            float t = (float)i / splineSamples;
            Unity.Mathematics.float3 lp = spline.EvaluatePosition(t);
            Vector3 wp = splineContainer.transform.TransformPoint(new Vector3(lp.x, lp.y, lp.z));

            poly.Add(new Vector2(wp.x, wp.z));
            ySum += wp.y;

            if (wp.x < minX) minX = wp.x;
            if (wp.x > maxX) maxX = wp.x;
            if (wp.z < minZ) minZ = wp.z;
            if (wp.z > maxZ) maxZ = wp.z;
        }

        groundY = ySum / Mathf.Max(1, splineSamples);
        return poly.Count >= 3;
    }
}