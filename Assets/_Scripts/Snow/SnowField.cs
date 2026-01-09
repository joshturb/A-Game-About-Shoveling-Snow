using System;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class SnowField : MonoBehaviour
{
    public static SnowField Instance;

    private SnowController snowController;
    public event Action OnInitialized;
    public event Action<List<int>> OnVertsEdited;

    [Header("Octree")]
    public int maxDepth = 10;
    public int maxPerLeaf = 32;

    [Header("Initial Snow Clear")]
    [SerializeField] private LayerMask clearInsideMask;
    [SerializeField, Min(0.0001f)] private float insideProbeRadius = 0.01f;
    [SerializeField] private QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Collide;

    private readonly Collider[] _insideHits = new Collider[32];
    private const float INSIDE_EPS = 1e-5f;

    private bool[] _blocked;        // verts inside clearInsideMask colliders (excluded from progress)
    private int _blockedCount;

    [Header("Snow Progress")]
    [SerializeField, Min(0f)] private float clearedEpsilon = 0.001f;

    [Header("Plow Slope (Angle of Repose)")]
    [SerializeField, Range(1f, 89f)] private float reposeAngleDeg = 35f;
    [SerializeField, Min(0)] private int reposeIterations = 4;
    [SerializeField, Range(0f, 1f)] private float reposeStrength = 0.5f;

    public VertexOctree Tree { get; private set; }
    public SnowHeightfieldCollider _hf;
    public Vector3[] GetVerts() => _verts;

    private int lastClearedCount;

    private MeshFilter _mf;
    private Mesh _mesh;
    private Vector3[] _verts;
    private float _clearedThreshold;

    private float[] _tmpFrontWeights;

    // repose support
    private List<int>[] _neighbors;
    private bool[] _reposeMark;
    private readonly List<int> _reposeList = new(1024);

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(Instance);
        }
        Instance = this;
    }

    void Start()
    {
        _mf = GetComponent<MeshFilter>();
        _mesh = _mf.mesh;
        _mesh.MarkDynamic();

        _verts = _mesh.vertices; // ONE initial copy only
        _blocked = new bool[_verts.Length];
        _blockedCount = 0;

        BuildAdjacency();
        _reposeMark = new bool[_verts.Length];

        Tree = new VertexOctree(_mesh, maxDepth, maxPerLeaf);
        _hf = new SnowHeightfieldCollider(transform, _mesh);

        snowController = FindFirstObjectByType<SnowController>();
        float minH = snowController.GetSnowSettings().minHeight;
        _clearedThreshold = minH + clearedEpsilon;

        ApplyInitialSnow();

        lastClearedCount = CountClearedVerts(_verts);
        snowController.RegisterField(_mesh.vertexCount - _blockedCount, lastClearedCount);

        OnInitialized?.Invoke();
    }

    private void ApplyInitialSnow()
    {
        float minH = snowController.GetSnowSettings().minHeight;

        for (int i = 0; i < _verts.Length; i++)
        {
            Vector3 wp = transform.TransformPoint(_verts[i]);

            if (IsInsideMaskedCollider(wp))
            {
                _blocked[i] = true;
                _blockedCount++;
                _verts[i].y = minH; // kept cleared physically, but excluded from progress
                continue;
            }

            _verts[i].y = snowController.SampleNoise(_verts[i]);
        }

        _mesh.vertices = _verts;
        ApplyMeshChanges(0);
    }

    private int CountClearedVerts(Vector3[] verts)
    {
        float minH = SnowController.Instance.GetSnowSettings().minHeight;
        float thresh = minH + clearedEpsilon;

        int count = 0;
        for (int i = 0; i < verts.Length; i++)
        {
            if (_blocked != null && _blocked[i]) continue;
            if (verts[i].y <= thresh) count++;
        }
        return count;
    }

    private void ApplyMeshChanges(int clearedDelta)
    {
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();

        _hf.Refresh(_verts);

        if (clearedDelta != 0)
        {
            SnowController.Instance.ApplyClearedDelta(clearedDelta);
            lastClearedCount += clearedDelta;
        }
    }

    private bool IsInsideMaskedCollider(Vector3 worldPoint)
    {
        int n = Physics.OverlapSphereNonAlloc(
            worldPoint,
            insideProbeRadius,
            _insideHits,
            clearInsideMask,
            triggerInteraction
        );

        for (int i = 0; i < n; i++)
        {
            Collider col = _insideHits[i];
            if (col == null) continue;

            Vector3 cp = col.ClosestPoint(worldPoint);
            if ((cp - worldPoint).sqrMagnitude <= INSIDE_EPS * INSIDE_EPS)
                return true;
        }
        return false;
    }

    public bool TryGetSnowHeightWorld(Vector3 worldPoint, out float heightWorld)
        => _hf.TryGetHeightWorld(worldPoint, out heightWorld);

    public bool RaycastSnow(Ray worldRay, out SnowHeightfieldCollider.Hit hit, float maxDistance = 500f)
        => _hf.Raycast(worldRay, out hit, maxDistance);

    public bool ContainsPoint(Vector3 worldPoint)
        => _hf.ContainsPoint(worldPoint);

    public void QuerySphere(Vector3 hitLocal, float radius, List<int> results)
    {
        hitLocal.y = 0f;
        Tree.QuerySphere(hitLocal, radius, results);
    }

    public void QueryBounds(Bounds worldBounds, List<int> results)
    {
        Tree.QueryBounds(transform, worldBounds.center,
            new Vector3(worldBounds.extents.x, worldBounds.extents.y, worldBounds.extents.z),
            results);
    }

    public int EditY(List<int> indices, Vector3 hitLocal, float radius, float value, YEditMode mode, float smoothness = 0f)
    {
        if (indices == null || indices.Count == 0) return 0;

        int clearedDelta = 0;
        int changedCount = 0;

        float minH = snowController.GetSnowSettings().minHeight;
        float thresh = _clearedThreshold;

        const float changeEps = 1e-6f;

        if (smoothness <= 0f)
        {
            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                if (_blocked != null && _blocked[i]) continue;

                float before = _verts[i].y;
                bool wasCleared = before <= thresh;

                switch (mode)
                {
                    case YEditMode.Set:      _verts[i].y = value; break;
                    case YEditMode.Add:      _verts[i].y += value; break;
                    case YEditMode.Subtract: _verts[i].y -= value; break;
                }

                _verts[i].y = Mathf.Max(minH, _verts[i].y);

                float after = _verts[i].y;
                if (Mathf.Abs(after - before) > changeEps) changedCount++;

                bool isCleared = after <= thresh;
                if (wasCleared != isCleared) clearedDelta += isCleared ? 1 : -1;
            }
        }
        else
        {
            hitLocal.y = 0f;

            float r = Mathf.Max(1e-6f, radius);
            float invR = 1f / r;

            float cx = hitLocal.x;
            float cz = hitLocal.z;

            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                if (_blocked != null && _blocked[i]) continue;

                float before = _verts[i].y;
                bool wasCleared = before <= thresh;

                float dx = _verts[i].x - cx;
                float dz = _verts[i].z - cz;

                float t = 1f - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz) * invR);
                t = Mathf.Pow(t, smoothness);

                switch (mode)
                {
                    case YEditMode.Set:      _verts[i].y = Mathf.Lerp(_verts[i].y, value, t); break;
                    case YEditMode.Add:      _verts[i].y += value * t; break;
                    case YEditMode.Subtract: _verts[i].y -= value * t; break;
                }

                _verts[i].y = Mathf.Max(minH, _verts[i].y);

                float after = _verts[i].y;
                if (Mathf.Abs(after - before) > changeEps) changedCount++;

                bool isCleared = after <= thresh;
                if (wasCleared != isCleared) clearedDelta += isCleared ? 1 : -1;
            }
        }

        if (changedCount == 0) return 0;

        OnVertsEdited?.Invoke(indices);

        _mesh.vertices = _verts;
        ApplyMeshChanges(clearedDelta);

        return changedCount;
    }

    public int Plow(
        List<int> removeIndices,
        List<int> depositIndices,
        Vector3 removeCenterLocal,
        Vector3 depositCenterLocal,
        Vector3 forwardLocal,
        float removeRadius,
        float depositRadius,
        float pushAmount,
        float smoothness = 1f)
    {
        if (removeIndices == null || removeIndices.Count == 0) return 0;
        if (depositIndices == null || depositIndices.Count == 0) return 0;

        float minH = snowController.GetSnowSettings().minHeight;
        float thresh = _clearedThreshold;
        const float changeEps = 1e-6f;

        forwardLocal.y = 0f;
        if (forwardLocal.sqrMagnitude < 1e-8f) return 0;
        forwardLocal.Normalize();

        removeCenterLocal.y = 0f;
        depositCenterLocal.y = 0f;

        float rRem = Mathf.Max(1e-6f, removeRadius);
        float invRem = 1f / rRem;

        float rDep = Mathf.Max(1e-6f, depositRadius);
        float invDep = 1f / rDep;

        float cx = removeCenterLocal.x;
        float cz = removeCenterLocal.z;

        float dcx = depositCenterLocal.x;
        float dcz = depositCenterLocal.z;

        float powSmooth = Mathf.Max(0.0001f, smoothness);

        // 1) PICKUP: remove only from behind
        float totalRemoved = 0f;

        int clearedDelta = 0;
        int changedCount = 0;

        for (int k = 0; k < removeIndices.Count; k++)
        {
            int i = removeIndices[k];
            if (_blocked != null && _blocked[i]) continue;

            float dx = _verts[i].x - cx;
            float dz = _verts[i].z - cz;

            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist > rRem) continue;

            float u = Mathf.Clamp01(1f - dist * invRem);
            u = u * u * (3f - 2f * u);                 // SmoothStep
            u = Mathf.Pow(u, 1f / powSmooth);          // higher smoothness = wider
            float t = u;

            float s = dx * forwardLocal.x + dz * forwardLocal.z; // behind < 0
            if (s >= 0f) continue;

            float before = _verts[i].y;
            bool wasCleared = before <= thresh;

            float behind01 = Mathf.Clamp01((-s) * invRem);
            float desiredRemove = pushAmount * t * behind01;

            float available = Mathf.Max(0f, before - minH);
            float actualRemove = Mathf.Min(desiredRemove, available);
            if (actualRemove <= 0f) continue;

            _verts[i].y = before - actualRemove;

            float after = _verts[i].y;
            if (Mathf.Abs(after - before) > changeEps) changedCount++;

            bool isCleared = after <= thresh;
            if (wasCleared != isCleared) clearedDelta += isCleared ? 1 : -1;

            totalRemoved += actualRemove;
        }

        if (totalRemoved <= 0f) return 0;

        // 2) DEPOSIT: distribute over depositIndices, biased forward
        _tmpFrontWeights ??= new float[512];
        if (_tmpFrontWeights.Length < depositIndices.Count) _tmpFrontWeights = new float[depositIndices.Count * 2];
        for (int k = 0; k < depositIndices.Count; k++) _tmpFrontWeights[k] = 0f;

        float totalW = 0f;

        for (int k = 0; k < depositIndices.Count; k++)
        {
            int i = depositIndices[k];
            if (_blocked != null && _blocked[i]) continue;

            float dx = _verts[i].x - dcx;
            float dz = _verts[i].z - dcz;

            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist > rDep) continue;

            float u = Mathf.Clamp01(1f - dist * invDep);
            u = u * u * (3f - 2f * u);                 // SmoothStep (softer edge)
            u = Mathf.Pow(u, 1f / powSmooth);          // higher smoothness = wider
            float t = u;

            float s = (_verts[i].x - cx) * forwardLocal.x + (_verts[i].z - cz) * forwardLocal.z;
            float ahead01 = Mathf.Clamp01(s * invRem);

            float w = t * (0.25f + 0.75f * ahead01);
            _tmpFrontWeights[k] = w;
            totalW += w;
        }

        if (totalW <= 1e-8f) return changedCount;

        for (int k = 0; k < depositIndices.Count; k++)
        {
            float w = _tmpFrontWeights[k];
            if (w <= 0f) continue;

            int i = depositIndices[k];
            if (_blocked != null && _blocked[i]) continue;

            float before = _verts[i].y;
            bool wasCleared = before <= thresh;

            float add = totalRemoved * (w / totalW);
            _verts[i].y = before + add;

            float after = _verts[i].y;
            if (Mathf.Abs(after - before) > changeEps) changedCount++;

            bool isCleared = after <= thresh;
            if (wasCleared != isCleared) clearedDelta += isCleared ? 1 : -1;
        }

        // 3) SLOPE RELAX: prevent vertical walls after repeated plows
        ApplyAngleOfRepose(depositIndices, minH, thresh, ref clearedDelta, ref changedCount);

        if (changedCount == 0) return 0;

        OnVertsEdited?.Invoke(removeIndices);
        OnVertsEdited?.Invoke(depositIndices);

        _mesh.vertices = _verts;
        ApplyMeshChanges(clearedDelta);

        return changedCount;
    }

    // -------------------- Angle of Repose --------------------

    private void BuildAdjacency()
    {
        int vc = _mesh.vertexCount;

        _neighbors = new List<int>[vc];
        for (int i = 0; i < vc; i++)
            _neighbors[i] = new List<int>(8);

        int[] tris = _mesh.triangles;
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t];
            int b = tris[t + 1];
            int c = tris[t + 2];

            AddNeighborUnique(a, b);
            AddNeighborUnique(b, a);

            AddNeighborUnique(b, c);
            AddNeighborUnique(c, b);

            AddNeighborUnique(c, a);
            AddNeighborUnique(a, c);
        }
    }

    private void AddNeighborUnique(int a, int b)
    {
        var list = _neighbors[a];
        for (int i = 0; i < list.Count; i++)
            if (list[i] == b) return;
        list.Add(b);
    }

    private void ApplyAngleOfRepose(List<int> seeds, float minH, float thresh, ref int clearedDelta, ref int changedCount)
    {
        if (reposeIterations <= 0 || reposeStrength <= 0f) return;
        if (seeds == null || seeds.Count == 0) return;
        if (_neighbors == null || _reposeMark == null) return;

        _reposeList.Clear();

        for (int k = 0; k < seeds.Count; k++)
        {
            int i = seeds[k];
            if ((uint)i >= (uint)_verts.Length) continue;
            if (_blocked != null && _blocked[i]) continue;
            if (_reposeMark[i]) continue;

            _reposeMark[i] = true;
            _reposeList.Add(i);
        }

        float maxSlope = Mathf.Tan(reposeAngleDeg * Mathf.Deg2Rad);
        float strength = Mathf.Clamp01(reposeStrength);

        for (int iter = 0; iter < reposeIterations; iter++)
        {
            for (int idx = 0; idx < _reposeList.Count; idx++)
            {
                int i = _reposeList[idx];
                Vector3 vi = _verts[i];

                var nb = _neighbors[i];
                for (int n = 0; n < nb.Count; n++)
                {
                    int j = nb[n];
                    if ((uint)j >= (uint)_verts.Length) continue;
                    if (_blocked != null && _blocked[j]) continue;

                    Vector3 vj = _verts[j];

                    float dx = vi.x - vj.x;
                    float dz = vi.z - vj.z;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (dist <= 1e-6f) continue;

                    float limit = maxSlope * dist;
                    float diff = vi.y - vj.y;

                    if (diff > limit)
                    {
                        float excess = diff - limit;
                        float move = excess * 0.5f * strength;

                        float canGive = Mathf.Max(0f, vi.y - minH);
                        move = Mathf.Min(move, canGive);
                        if (move <= 0f) continue;

                        bool iWas = vi.y <= thresh;
                        bool jWas = vj.y <= thresh;

                        vi.y -= move;
                        vj.y += move;

                        bool iIs = vi.y <= thresh;
                        bool jIs = vj.y <= thresh;

                        if (iWas != iIs) clearedDelta += iIs ? 1 : -1;
                        if (jWas != jIs) clearedDelta += jIs ? 1 : -1;

                        _verts[i] = vi;
                        _verts[j] = vj;
                        changedCount++;

                        if (!_reposeMark[j])
                        {
                            _reposeMark[j] = true;
                            _reposeList.Add(j);
                        }
                    }
                }
            }
        }

        for (int k = 0; k < _reposeList.Count; k++)
            _reposeMark[_reposeList[k]] = false;
    }
}
