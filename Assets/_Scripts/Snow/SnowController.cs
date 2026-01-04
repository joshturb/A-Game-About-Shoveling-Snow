using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

[System.Serializable]
public struct SnowSettings
{
    public float voxelSize;
    public float falloffWidth;
    public float snowStabilityThreshold;
    public int smoothingIterations;

    [Header("Layer 1 (Drifts)")]
    public FastNoiseLite.NoiseType noiseType1;
    public FastNoiseLite.FractalType fractalType1;
    public int octaves1;
    public float lacunarity1;
    public float gain1;
    public float scale1;
    public float weight1;
    public float power1;

    [Header("Layer 2 (Ripples)")]
    public FastNoiseLite.NoiseType noiseType2;
    public FastNoiseLite.FractalType fractalType2;
    public int octaves2;
    public float lacunarity2;
    public float gain2;
    public float scale2;
    public float weight2;
    public float power2;

    [Header("Layer 3 (Ridges/Peaks)")]
    public FastNoiseLite.NoiseType noiseType3;
    public FastNoiseLite.FractalType fractalType3;
    public int octaves3;
    public float lacunarity3;
    public float gain3;
    public float scale3;
    public float weight3;
    public float power3;

    [Header("Final Height")]
    public float minSnowHeight;
    public float maxSnowHeight;

    public float isoValue;
    public float chunkSize;
    public Material snowMaterial;

    public static SnowSettings Default => new()
    {
        voxelSize = 0.25f,
        falloffWidth = 2f,
        snowStabilityThreshold = .25f,

        noiseType1 = FastNoiseLite.NoiseType.OpenSimplex2,
        fractalType1 = FastNoiseLite.FractalType.FBm,
        octaves1 = 4,
        lacunarity1 = 2.0f,
        gain1 = 0.5f,
        scale1 = 120f,
        weight1 = 0.70f,
        power1 = 2.0f,

        noiseType2 = FastNoiseLite.NoiseType.OpenSimplex2,
        fractalType2 = FastNoiseLite.FractalType.FBm,
        octaves2 = 3,
        lacunarity2 = 2.3f,
        gain2 = 0.45f,
        scale2 = 28f,
        weight2 = 0.25f,
        power2 = 1.0f,

        noiseType3 = FastNoiseLite.NoiseType.OpenSimplex2,
        fractalType3 = FastNoiseLite.FractalType.Ridged,
        octaves3 = 2,
        lacunarity3 = 2.1f,
        gain3 = 0.65f,
        scale3 = 60f,
        weight3 = 0.12f,
        power3 = 1.0f,

        minSnowHeight = 0.1f,
        maxSnowHeight = 3f,
        isoValue = 0.5f,
        chunkSize = 8f,
        snowMaterial = null
    };
}

public class SnowController : MonoBehaviour
{
    public static SnowController Instance;

    [Header("Snow Settings")]
    [SerializeField] private SnowSettings snowSettings = SnowSettings.Default;
    [SerializeField] private ComputeShader marchingCubes;
    public GameObject chunkPrefab;

    [Header("Snow Stats")]
    [SerializeField] private float snowCleared;
    [SerializeField] private float snowRemaining;

    public SnowSettings GetSnowSettings() => snowSettings;
    public float GetSnowCleared() => snowCleared;
    public float GetSnowRemaining() => snowRemaining;

    private FastNoiseLite n1;
    private FastNoiseLite n2;
    private FastNoiseLite n3;
    private SnowField[] snowFields;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        int seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        n1 = new FastNoiseLite(seed);
        n2 = new FastNoiseLite(unchecked(seed ^ (int)0x9E3779B9));
        n3 = new FastNoiseLite(unchecked(seed ^ (int)0xBB67AE85));

        ConfigureNoise(n1, snowSettings.noiseType1, snowSettings.fractalType1, snowSettings.octaves1, snowSettings.lacunarity1, snowSettings.gain1, snowSettings.scale1);
        ConfigureNoise(n2, snowSettings.noiseType2, snowSettings.fractalType2, snowSettings.octaves2, snowSettings.lacunarity2, snowSettings.gain2, snowSettings.scale2);
        ConfigureNoise(n3, snowSettings.noiseType3, snowSettings.fractalType3, snowSettings.octaves3, snowSettings.lacunarity3, snowSettings.gain3, snowSettings.scale3);
    }

    private static void ConfigureNoise(FastNoiseLite n, FastNoiseLite.NoiseType type, FastNoiseLite.FractalType fractal, int oct, float lac, float gain, float scale)
    {
        n.SetNoiseType(type);
        n.SetFractalType(fractal);
        n.SetFractalOctaves(Mathf.Max(1, oct));
        n.SetFractalLacunarity(lac);
        n.SetFractalGain(gain);
        n.SetFrequency(1f / Mathf.Max(0.0001f, scale));
    }

    private void Start()
    {
        snowFields = FindObjectsByType<SnowField>(FindObjectsSortMode.None);
        foreach (var item in snowFields)
            item.Initialize();
    }

    public float SampleNoise(Vector3 worldPos)
    {
        float t1 = Mathf.Clamp01(n1.GetNoise(worldPos.x, worldPos.z) * 0.5f + 0.5f);
        float t2 = Mathf.Clamp01(n2.GetNoise(worldPos.x, worldPos.z) * 0.5f + 0.5f);
        float t3 = Mathf.Clamp01(n3.GetNoise(worldPos.x, worldPos.z) * 0.5f + 0.5f);

        if (snowSettings.power1 != 1f) t1 = Mathf.Pow(t1, Mathf.Max(0.0001f, snowSettings.power1));
        if (snowSettings.power2 != 1f) t2 = Mathf.Pow(t2, Mathf.Max(0.0001f, snowSettings.power2));
        if (snowSettings.power3 != 1f) t3 = Mathf.Pow(t3, Mathf.Max(0.0001f, snowSettings.power3));

        float w1 = Mathf.Max(0f, snowSettings.weight1);
        float w2 = Mathf.Max(0f, snowSettings.weight2);
        float w3 = Mathf.Max(0f, snowSettings.weight3);
        float wSum = Mathf.Max(1e-6f, w1 + w2 + w3);

        float t = (t1 * w1 + t2 * w2 + t3 * w3) / wSum;
        t = Mathf.Clamp01(t);

        return Mathf.Lerp(snowSettings.minSnowHeight, snowSettings.maxSnowHeight, t);
    }

    /// NEW SIGNATURE: needs the SnowField so normals can sample across chunk boundaries.
    public Mesh GenerateMesh(SnowField field, Chunk chunk)
    {
        if (field == null || chunk == null || chunk.Voxels == null)
            return new Mesh();

        Voxel[,,] voxels = chunk.Voxels;

        int numPointsPerAxis = voxels.GetLength(0);
        int numVoxelsPerAxis = numPointsPerAxis - 1;
        int numThreadsPerAxis = Mathf.CeilToInt(numVoxelsPerAxis / 8.0f);

        using var pointsBuffer = new ComputeBuffer(numPointsPerAxis * numPointsPerAxis * numPointsPerAxis, sizeof(float) * 4);
        using var trianglesBuffer = new ComputeBuffer(numVoxelsPerAxis * numVoxelsPerAxis * numVoxelsPerAxis * 5 * 3, sizeof(float) * 3 * 3, ComputeBufferType.Append);
        trianglesBuffer.SetCounterValue(0);

        float voxelSize = snowSettings.voxelSize;
        float half = (numPointsPerAxis - 1) * voxelSize * 0.5f;

        var points = new float4[numPointsPerAxis * numPointsPerAxis * numPointsPerAxis];
        for (int x = 0; x < numPointsPerAxis; x++)
        for (int y = 0; y < numPointsPerAxis; y++)
        for (int z = 0; z < numPointsPerAxis; z++)
        {
            float scaledX = x * voxelSize - half;
            float scaledY = y * voxelSize - half;
            float scaledZ = z * voxelSize - half;

            points[x + numPointsPerAxis * (y + numPointsPerAxis * z)] =
                new float4(scaledX, scaledY, scaledZ, voxels[x, y, z].Density);
        }

        pointsBuffer.SetData(points);
        marchingCubes.SetBuffer(0, "points", pointsBuffer);
        marchingCubes.SetBuffer(0, "triangles", trianglesBuffer);
        marchingCubes.SetInt("numPointsPerAxis", numPointsPerAxis);
        marchingCubes.SetFloat("isoLevel", snowSettings.isoValue);
        marchingCubes.Dispatch(0, numThreadsPerAxis, numThreadsPerAxis, numThreadsPerAxis);

        // Read back triangles
        using var countBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
        ComputeBuffer.CopyCount(trianglesBuffer, countBuffer, 0);
        int[] triangleCountArray = new int[1];
        countBuffer.GetData(triangleCountArray);
        int triangleCount = triangleCountArray[0];

        var triData = new Triangle[triangleCount];
        trianglesBuffer.GetData(triData, 0, 0, triangleCount);

        var vertices = new Vector3[triangleCount * 3];
        var meshTriangles = new int[triangleCount * 3];

        for (int i = 0; i < triangleCount; i++)
        {
            vertices[i * 3] = triData[i].vertexA;
            vertices[i * 3 + 1] = triData[i].vertexB;
            vertices[i * 3 + 2] = triData[i].vertexC;

            meshTriangles[i * 3] = i * 3;
            meshTriangles[i * 3 + 1] = i * 3 + 1;
            meshTriangles[i * 3 + 2] = i * 3 + 2;
        }

        // Keep your merge (optional; you previously saw artifacts from it)
        VoxelUtils.MergeVertices(ref vertices, ref meshTriangles, snowSettings.voxelSize * 0.001f);

        var mesh = new Mesh();
        if (vertices.Length > 65535)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        mesh.vertices = vertices;
        mesh.triangles = meshTriangles;

        // Smooth positions (your existing workflow)
        VoxelUtils.LaplacianSmooth(mesh, snowSettings.smoothingIterations);

        // NEW: normals from density gradient, sampled in WORLD across chunk borders.
        var smoothedVerts = mesh.vertices; // LaplacianSmooth changed these
        var normals = new Vector3[smoothedVerts.Length];

        System.Func<Vector3, float> sampleDensityLocal = (Vector3 localPos) =>
        {
            // Mesh is in chunk-local space centered at 0, and the object is placed at chunk.Center,
            // so local -> world is just Center + local.
            Vector3 worldPos = chunk.Center + localPos;
            return SampleDensityWorld(field, worldPos);
        };

        VoxelUtils.ComputeNormalsFromDensityGradient(
            smoothedVerts,
            normals,
            sampleDensityLocal,
            voxelSize
        );

        mesh.normals = normals;
        mesh.RecalculateBounds();

        return mesh;
    }

    // Trilinear sample the density field in world space, across chunks.
    // Returns 0 if chunk missing (treat as air).
    private float SampleDensityWorld(SnowField field, Vector3 worldPos)
    {
        if (field == null || field.chunkDictionary == null || field.chunkDictionary.Count == 0)
            return 0f;

        float cs = field.chunkWorldSize;
        float vs = snowSettings.voxelSize;
        int N = field.chunkVoxelsPerAxis;

        // Find owning chunk
        int cx = Mathf.FloorToInt(worldPos.x / cs);
        int cy = Mathf.FloorToInt(worldPos.y / cs);
        int cz = Mathf.FloorToInt(worldPos.z / cs);

        var cidx = new Vector3Int(cx, cy, cz);
        if (!field.chunkDictionary.TryGetValue(cidx, out var c) || c?.Voxels == null)
            return 0f;

        float half = cs * 0.5f;
        Vector3 origin = c.Center - new Vector3(half, half, half);
        Vector3 local = worldPos - origin;

        float gx = local.x / vs;
        float gy = local.y / vs;
        float gz = local.z / vs;

        // Clamp to valid cell range for trilinear (0..N-1), then +1 for the upper corner.
        int x0 = Mathf.Clamp(Mathf.FloorToInt(gx), 0, Mathf.Max(0, N - 1));
        int y0 = Mathf.Clamp(Mathf.FloorToInt(gy), 0, Mathf.Max(0, N - 1));
        int z0 = Mathf.Clamp(Mathf.FloorToInt(gz), 0, Mathf.Max(0, N - 1));

        int x1 = Mathf.Min(x0 + 1, N);
        int y1 = Mathf.Min(y0 + 1, N);
        int z1 = Mathf.Min(z0 + 1, N);

        float tx = Mathf.Clamp01(gx - x0);
        float ty = Mathf.Clamp01(gy - y0);
        float tz = Mathf.Clamp01(gz - z0);

        float d000 = c.Voxels[x0, y0, z0].Density;
        float d100 = c.Voxels[x1, y0, z0].Density;
        float d010 = c.Voxels[x0, y1, z0].Density;
        float d110 = c.Voxels[x1, y1, z0].Density;

        float d001 = c.Voxels[x0, y0, z1].Density;
        float d101 = c.Voxels[x1, y0, z1].Density;
        float d011 = c.Voxels[x0, y1, z1].Density;
        float d111 = c.Voxels[x1, y1, z1].Density;

        float d00 = Mathf.Lerp(d000, d100, tx);
        float d10 = Mathf.Lerp(d010, d110, tx);
        float d01 = Mathf.Lerp(d001, d101, tx);
        float d11 = Mathf.Lerp(d011, d111, tx);

        float d0 = Mathf.Lerp(d00, d10, ty);
        float d1 = Mathf.Lerp(d01, d11, ty);

        return Mathf.Lerp(d0, d1, tz);
    }

    public void PaintDensity(SnowField field, Vector3 worldPos, float radius, float targetDensity, float strength = 1f)
    {
        if (field.chunkDictionary == null || field.chunkDictionary.Count == 0)
            return;

        targetDensity = Mathf.Clamp01(targetDensity);
        strength = Mathf.Clamp01(strength);

        var dirty = new HashSet<Chunk>();

        Vector3 min = worldPos - Vector3.one * radius;
        Vector3 max = worldPos + Vector3.one * radius;

        int minCX = Mathf.FloorToInt(min.x / field.chunkWorldSize);
        int maxCX = Mathf.FloorToInt(max.x / field.chunkWorldSize);
        int minCY = Mathf.FloorToInt(min.y / field.chunkWorldSize);
        int maxCY = Mathf.FloorToInt(max.y / field.chunkWorldSize);
        int minCZ = Mathf.FloorToInt(min.z / field.chunkWorldSize);
        int maxCZ = Mathf.FloorToInt(max.z / field.chunkWorldSize);

        for (int cx = minCX; cx <= maxCX; cx++)
        for (int cy = minCY; cy <= maxCY; cy++)
        for (int cz = minCZ; cz <= maxCZ; cz++)
        {
            var cidx = new Vector3Int(cx, cy, cz);
            if (!field.chunkDictionary.TryGetValue(cidx, out var chunk) || chunk?.Voxels == null)
                continue;

            float half = field.chunkWorldSize * 0.5f;
            Vector3 origin = chunk.Center - new Vector3(half, half, half);

            int minVX = Mathf.Clamp(Mathf.FloorToInt((min.x - origin.x) / snowSettings.voxelSize), 0, field.chunkVoxelsPerAxis);
            int maxVX = Mathf.Clamp(Mathf.CeilToInt((max.x - origin.x) / snowSettings.voxelSize), 0, field.chunkVoxelsPerAxis);
            int minVY = Mathf.Clamp(Mathf.FloorToInt((min.y - origin.y) / snowSettings.voxelSize), 0, field.chunkVoxelsPerAxis);
            int maxVY = Mathf.Clamp(Mathf.CeilToInt((max.y - origin.y) / snowSettings.voxelSize), 0, field.chunkVoxelsPerAxis);
            int minVZ = Mathf.Clamp(Mathf.FloorToInt((min.z - origin.z) / snowSettings.voxelSize), 0, field.chunkVoxelsPerAxis);
            int maxVZ = Mathf.Clamp(Mathf.CeilToInt((max.z - origin.z) / snowSettings.voxelSize), 0, field.chunkVoxelsPerAxis);

            bool changed = false;

            for (int x = minVX; x <= maxVX; x++)
            for (int y = minVY; y <= maxVY; y++)
            for (int z = minVZ; z <= maxVZ; z++)
            {
                Vector3 vPos = VoxelUtils.VoxelCenterWorld(chunk, x, y, z, field.chunkWorldSize, snowSettings.voxelSize);
                float d = Vector3.Distance(vPos, worldPos);
                if (d > radius) continue;

                float w = (1f - (d / radius)) * strength;
                float cur = chunk.Voxels[x, y, z].Density;
                float next = Mathf.Lerp(cur, targetDensity, w);
                next = Mathf.Clamp01(next);

                if (!Mathf.Approximately(cur, next))
                {
                    chunk.Voxels[x, y, z].Density = next;
                    changed = true;
                }
            }

            if (changed)
            {
                dirty.Add(chunk);
                SnowGravity.Instance.MarkChunkDirty(field, chunk);
            }
        }
    }
}
