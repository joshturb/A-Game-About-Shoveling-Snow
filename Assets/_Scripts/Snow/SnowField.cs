using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Splines;

    // marching cubes. using noise, make voxels check underneath and at a threshold it will 
    // smoothly (with variable for grav speed) transfer its density down until its base is over threshold
    // pushing will take density from selected voxels and transfer them to the front ones. and it will keep going until
    // it is all dense voxels, then it will stack and the voxels will then ^ wrap back and fall behind! realistically.
    // it also will slow you down depending on voxel density and height in front of shovel point

    // First thing is laying voxels into the splines and then generating the chunkDictionary with the noise for height
[RequireComponent(typeof(SplineContainer))]
public class SnowField : MonoBehaviour
{
    private SnowController snowController;
    private SplineContainer splineContainer;
    public List<Chunk> Chunks => chunkDictionary.Values.ToList();
    public Dictionary<Vector3Int, Chunk> chunkDictionary = new();

    public int chunkVoxelsPerAxis;
    public float chunkWorldSize;


    void Awake()
    {
        splineContainer = GetComponent<SplineContainer>();
    }

    public void Initialize()
    {
        snowController = SnowController.Instance;
        chunkDictionary = GenerateChunks();
        for (int i = 0; i < Chunks.Count; i++)
        {
            RenderChunk(Chunks[i]);
        }
    }

    private Dictionary<Vector3Int, Chunk> GenerateChunks()
    {
        var dict = new Dictionary<Vector3Int, Chunk>();

        if (!VoxelUtils.TryGetSplinePolygonAndBoundsXZ(
                splineContainer,
                out List<Vector2> poly,
                out float groundY,
                out float minX, out float maxX,
                out float minZ, out float maxZ))
            return dict;

        var settings = SnowController.Instance.GetSnowSettings();
        VoxelUtils.GetChunkSizing(settings, out float voxelSize, out int chunkVoxelsPerAxis, out float chunkWorldSize);
        this.chunkVoxelsPerAxis = chunkVoxelsPerAxis;
        this.chunkWorldSize = chunkWorldSize;

        var query = new SplineQuery(poly, settings, voxelSize, settings.falloffWidth);

        VoxelUtils.GetChunkRange(minX, maxX, minZ, maxZ, chunkWorldSize, out int minChunkX, out int maxChunkX, out int minChunkZ, out int maxChunkZ);

        int chunkY = Mathf.FloorToInt(groundY / chunkWorldSize);

        for (int cx = minChunkX; cx <= maxChunkX; cx++)
        {
            for (int cz = minChunkZ; cz <= maxChunkZ; cz++)
            {
                Vector3Int chunkIndex = new Vector3Int(cx, chunkY, cz);
                Vector3 chunkCenter = VoxelUtils.GetChunkCenter(cx, chunkY, cz, chunkWorldSize);

                if (VoxelUtils.TryBuildChunkVoxels(chunkCenter, groundY, chunkVoxelsPerAxis, chunkWorldSize, voxelSize, query, out Voxel[,,] voxels))
                {
                    dict[chunkIndex] = new Chunk(chunkCenter, chunkIndex, voxels);
                    SnowGravity.Instance.MarkChunkDirty(this, dict[chunkIndex]);
                }
            }
        }

        return dict;
    }

    private void RenderChunk(Chunk chunk)
    {
        chunk.Mesh = snowController.GenerateMesh(this, chunk);
        chunk.Object = Instantiate(snowController.chunkPrefab, chunk.Center, Quaternion.identity, transform);
        chunk.Object.GetComponent<MeshFilter>().mesh = chunk.Mesh;
        chunk.Object.GetComponent<MeshCollider>().sharedMesh = chunk.Mesh;
    }

    public void RegenerateChunk(Chunk chunk)
    {
        chunk.Mesh = snowController.GenerateMesh(this, chunk);
        var mf = chunk.Object.GetComponent<MeshFilter>();
        mf.sharedMesh = chunk.Mesh;

        if (chunk.Object.TryGetComponent<MeshCollider>(out var mc)) mc.sharedMesh = chunk.Mesh;
    }


#region Gizmos

    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private bool drawChunkBounds = true;
    [SerializeField] private bool drawVoxels = true;
    [SerializeField] private int gizmoChunkLimit = 64;        // safety cap
    [SerializeField] private int gizmoVoxelLimitPerChunk = 8000; // safety cap
    [SerializeField] private float voxelDrawSizeMultiplier = 0.35f;
    [SerializeField] private bool drawOnlyNearCamera = true;
    [SerializeField] private float cameraMaxDistance = 80f;

    private void OnDrawGizmos()
    {
        if (!drawGizmos)
            return;

        if (Application.isPlaying)
        {
            if (chunkDictionary == null || chunkDictionary.Count == 0)
                return;

            DrawChunksAndVoxelsGizmos(chunkDictionary);
            return;
        }

        // In edit mode, try to generate preview if we can.
        // (Avoid if SnowController isn't ready.)
        try
        {
            var preview = GenerateChunks();
            if (preview != null && preview.Count > 0)
                DrawChunksAndVoxelsGizmos(preview);
        }
        catch
        {
            // ignore gizmo errors in edit mode
        }
    }

    private void DrawChunksAndVoxelsGizmos(Dictionary<Vector3Int, Chunk> dict)
    {
        var settings = SnowController.Instance != null ? SnowController.Instance.GetSnowSettings() : default;
        float voxelSize = Mathf.Max(0.001f, settings.voxelSize);
        float desiredChunkWorldSize = Mathf.Max(voxelSize, settings.chunkSize);
        int chunkVoxelsPerAxis = Mathf.Max(1, Mathf.RoundToInt(desiredChunkWorldSize / voxelSize));
        float chunkWorldSize = chunkVoxelsPerAxis * voxelSize;

        Camera cam = Camera.current != null ? Camera.current : Camera.main;

        int drawnChunks = 0;
        foreach (var kvp in dict)
        {
            if (drawnChunks++ >= gizmoChunkLimit)
                break;

            Chunk c = kvp.Value;
            if (c == null || c.Voxels == null)
                continue;

            if (drawOnlyNearCamera && cam != null)
            {
                if (Vector3.Distance(cam.transform.position, c.Center) > cameraMaxDistance)
                    continue;
            }

            // --- Chunk bounds
            if (drawChunkBounds)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireCube(c.Center, Vector3.one * chunkWorldSize);
            }

            if (!drawVoxels)
                continue;

            // --- Voxels
            int sizeX = c.Voxels.GetLength(0);
            int sizeY = c.Voxels.GetLength(1);
            int sizeZ = c.Voxels.GetLength(2);

            Vector3 half = Vector3.one * (chunkWorldSize * 0.5f);
            Vector3 origin = c.Center - half;

            float cubeSize = voxelSize * Mathf.Clamp(voxelDrawSizeMultiplier, 0.05f, 1f);

            int drawnVoxels = 0;

            // Draw only "near surface" voxels to reduce spam:
            // your field is (wy - surfaceY), so close to isoLevel means close to surface.
            float iso = settings.isoValue;
            float band = voxelSize * 1.25f;

            for (int x = 0; x < sizeX; x++)
            {
                float wx = origin.x + x * voxelSize;

                for (int y = 0; y < sizeY; y++)
                {
                    float wy = origin.y + y * voxelSize;

                    for (int z = 0; z < sizeZ; z++)
                    {
                        if (drawnVoxels++ >= gizmoVoxelLimitPerChunk)
                            goto NextChunk;

                        float d = c.Voxels[x, y, z].Density;

                        // Draw:
                        // - near the surface band (|d - iso| small) in yellow
                        // - "solid side" (d < iso) faint white
                        // - "air side" (d > iso) faint gray
                        float delta = Mathf.Abs(d - iso);

                        if (delta <= band)
                            Gizmos.color = Color.yellow;
                        else if (d < iso)
                            Gizmos.color = new Color(1f, 1f, 1f, 0.12f);
                        else
                            Gizmos.color = new Color(0.6f, 0.6f, 0.6f, 0.04f);

                        Vector3 pos = new Vector3(wx, wy, origin.z + z * voxelSize);
                        Gizmos.DrawCube(pos, Vector3.one * cubeSize);
                    }
                }
            }

        NextChunk:
            continue;
        }
    }
    #endregion
}
