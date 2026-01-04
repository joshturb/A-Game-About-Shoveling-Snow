using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public sealed class SnowGravity : MonoBehaviour
{
    public static SnowGravity Instance { get; private set; }

    [Header("Timing")]
    [SerializeField] private float stepInterval = 0.08f;

    [Header("Falling")]
    [SerializeField] private float fallRate = 3.5f;

    [Range(0f, 1f)]
    [SerializeField] private float supportThreshold = 0.99f;

    [SerializeField] private float epsilon = 0.0001f;

    [SerializeField] private int iterationsPerStep = 8;

    [Header("Performance")]
    [Tooltip("Max dirty chunks processed per tick.")]
    [SerializeField] private int maxDirtyChunksPerStep = 16;

    [Tooltip("Process most recently dirtied chunks first (feels snappier for painting).")]
    [SerializeField] private bool processMostRecentFirst = true;

    private int chunkVoxelsPerAxis;

    // Dedup dirty requests across (field, chunkIndex)
    private readonly HashSet<DirtyKey> dirtySet = new(new DirtyKeyComparer());

    // Use as stack (LIFO) when processMostRecentFirst = true, otherwise behaves like FIFO by popping from front.
    private readonly List<DirtyKey> dirtyList = new(256);

    // Reused scratch buffers (avoid per-tick allocations)
    private readonly List<(SnowField field, Chunk chunk)> meshDirty = new(128);
    private readonly HashSet<Chunk> meshDirtySet = new();
    private readonly List<DirtyKey> requeue = new(128);

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        var settings = SnowController.Instance.GetSnowSettings();
        float voxelSize = Mathf.Max(0.001f, settings.voxelSize);
        float desiredChunkWorldSize = Mathf.Max(voxelSize, settings.chunkSize);
        chunkVoxelsPerAxis = Mathf.Max(1, Mathf.RoundToInt(desiredChunkWorldSize / voxelSize));
    }

    private void OnEnable() => StartCoroutine(GravityLoop());

    /// Call when you edit voxels in a chunk (dig/paint/plow/etc).
    /// IMPORTANT: Do NOT regenerate meshes here. Gravity owns mesh regen.
    public void MarkChunkDirty(SnowField field, Vector3Int chunkIndex)
    {
        if (field == null) return;

        var key = new DirtyKey(field, chunkIndex);
        if (dirtySet.Add(key))
            dirtyList.Add(key);
    }

    public void MarkChunkDirty(SnowField field, Chunk chunk)
    {
        if (field == null || chunk == null) return;
        MarkChunkDirty(field, chunk.Index);
    }

    private IEnumerator GravityLoop()
    {
        var wait = new WaitForSeconds(stepInterval);
        while (true)
        {
            StepGravity(stepInterval);
            yield return wait;
        }
    }

    private void StepGravity(float dt)
    {
        if (dirtyList.Count == 0)
            return;

        float maxMove = Mathf.Clamp01(fallRate * dt);
        int processed = 0;

        meshDirty.Clear();
        meshDirtySet.Clear();
        requeue.Clear();

        int limit = Mathf.Max(1, maxDirtyChunksPerStep);

        while (dirtyList.Count > 0 && processed < limit)
        {
            DirtyKey key = PopDirty();
            dirtySet.Remove(key);

            SnowField field = key.Field;
            if (field == null)
            {
                processed++;
                continue;
            }

            var chunks = field.chunkDictionary;
            if (chunks == null || chunks.Count == 0)
            {
                processed++;
                continue;
            }

            if (!chunks.TryGetValue(key.ChunkIndex, out var chunk) || chunk?.Voxels == null)
            {
                processed++;
                continue;
            }

            // NEW: Always regenerate this dirty chunk at least once (even if nothing falls).
            AddMeshDirty(field, chunk);

            bool moved = SimulateChunkFall(field, key.ChunkIndex, chunk, maxMove);

            if (moved)
                requeue.Add(key);

            processed++;
        }

        // If something moved, neighbors may now be unsupported / need restitch.
        for (int i = 0; i < requeue.Count; i++)
        {
            var k = requeue[i];
            EnqueueIfExists(k.Field, k.ChunkIndex);
            EnqueueIfExists(k.Field, k.ChunkIndex + Vector3Int.down);
            EnqueueIfExists(k.Field, k.ChunkIndex + Vector3Int.up);
            EnqueueIfExists(k.Field, k.ChunkIndex + Vector3Int.left);
            EnqueueIfExists(k.Field, k.ChunkIndex + Vector3Int.right);
            EnqueueIfExists(k.Field, k.ChunkIndex + Vector3Int.forward);
            EnqueueIfExists(k.Field, k.ChunkIndex + Vector3Int.back);
        }

        // Regenerate meshes (unique)
        for (int i = 0; i < meshDirty.Count; i++)
        {
            var pair = meshDirty[i];
            if (pair.field != null && pair.chunk != null)
                pair.field.RegenerateChunk(pair.chunk);
        }
    }

    private DirtyKey PopDirty()
    {
        if (processMostRecentFirst)
        {
            int last = dirtyList.Count - 1;
            DirtyKey k = dirtyList[last];
            dirtyList.RemoveAt(last);
            return k;
        }
        else
        {
            // FIFO: pop from front (O(n)). Only use if you really want FIFO.
            DirtyKey k = dirtyList[0];
            dirtyList.RemoveAt(0);
            return k;
        }
    }

    private void AddMeshDirty(SnowField field, Chunk chunk)
    {
        if (chunk == null) return;
        if (meshDirtySet.Add(chunk))
            meshDirty.Add((field, chunk));
    }

    private void EnqueueIfExists(SnowField field, Vector3Int idx)
    {
        if (field == null) return;

        var chunks = field.chunkDictionary;
        if (chunks == null || !chunks.ContainsKey(idx))
            return;

        MarkChunkDirty(field, idx);
    }

    private bool SimulateChunkFall(
        SnowField field,
        Vector3Int chunkIndex,
        Chunk chunk,
        float maxMove)
    {
        var vox = chunk.Voxels;
        int max = chunkVoxelsPerAxis;

        int iters = Mathf.Max(1, iterationsPerStep);
        float movePerIter = Mathf.Clamp01(maxMove / iters);

        bool movedOverall = false;

        for (int iter = 0; iter < iters; iter++)
        {
            bool movedAny = false;

            for (int x = 0; x <= max; x++)
            {
                for (int z = 0; z <= max; z++)
                {
                    // Inverted Y: "down" is y+1, bottom is y==max.
                    for (int y = max; y >= 0; y--)
                    {
                        float src = vox[x, y, z].Density;
                        if (src <= epsilon)
                            continue;

                        if (!TryGetVoxelRefBelow(field, chunkIndex, x, y, z,
                                out var belowChunk, out int bx, out int by, out int bz))
                            continue;

                        float dst = belowChunk.Voxels[bx, by, bz].Density;

                        if (dst >= supportThreshold)
                            continue;

                        float space = 1f - dst;
                        if (space <= epsilon)
                            continue;

                        float move = Mathf.Min(src, space, movePerIter);
                        if (move <= epsilon)
                            continue;

                        vox[x, y, z].Density = src - move;
                        belowChunk.Voxels[bx, by, bz].Density = dst + move;

                        movedAny = true;
                        movedOverall = true;

                        // Neighbor chunk mesh can change too
                        AddMeshDirty(field, chunk);
                        AddMeshDirty(field, belowChunk);
                    }
                }
            }

            if (!movedAny)
                break;
        }

        return movedOverall;
    }

    private bool TryGetVoxelRefBelow(
        SnowField field,
        Vector3Int chunkIndex,
        int x, int y, int z,
        out Chunk belowChunk,
        out int bx, out int by, out int bz)
    {
        bx = x;
        bz = z;
        int max = chunkVoxelsPerAxis;

        var chunks = field.chunkDictionary;

        if (y < max)
        {
            belowChunk = chunks[chunkIndex];
            by = y + 1;
            return true;
        }

        Vector3Int belowIndex = chunkIndex + Vector3Int.down;

        if (!chunks.TryGetValue(belowIndex, out belowChunk) || belowChunk?.Voxels == null)
        {
            belowChunk = null;
            by = 0;
            return false;
        }

        by = 0;
        return true;
    }

    private readonly struct DirtyKey
    {
        public readonly SnowField Field;
        public readonly Vector3Int ChunkIndex;

        public DirtyKey(SnowField field, Vector3Int idx)
        {
            Field = field;
            ChunkIndex = idx;
        }
    }

    private sealed class DirtyKeyComparer : IEqualityComparer<DirtyKey>
    {
        public bool Equals(DirtyKey a, DirtyKey b) => a.Field == b.Field && a.ChunkIndex == b.ChunkIndex;

        public int GetHashCode(DirtyKey k)
        {
            int h = k.Field != null ? k.Field.GetHashCode() : 0;
            h = (h * 397) ^ k.ChunkIndex.GetHashCode();
            return h;
        }
    }
}
