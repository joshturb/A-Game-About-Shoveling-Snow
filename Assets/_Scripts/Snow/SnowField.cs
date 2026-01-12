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

    [Header("Ice Rendering (Submeshes)")]
    [SerializeField, Range(0f, 1f)] private float iceTriMajority = 0.5f; // avg mask >= this => triangle goes to ice submesh
    [SerializeField, Min(0)] private int iceVisualExpandRings = 1;        // VISUAL ONLY expansion (rings of edge-adjacent tris)
    [SerializeField, Min(0.001f)] private float iceProbeRadius = 0.35f;   // tune ~ your vertex spacing
    private readonly List<int> _iceProbe = new(64);

    public VertexOctree Tree { get; private set; }
    public SnowHeightfieldCollider _hf;
    public Vector3[] GetVerts() => _verts;

    private int lastClearedCount;

    private MeshFilter _mf;
    private Mesh _mesh;
    private Vector3[] _verts;

    private float[] _baseY;
    private float[] _tmpFrontWeights;
    private List<int>[] _neighbors;
    private bool[] _reposeMark;
    private readonly List<int> _reposeList = new(1024);

    // ice rendering support
    private float[] _iceMask;   // per-vertex 0..1
    private int[] _baseTris;    // full triangle list (combined), used for adjacency + classification
    private bool[] _isIceVert;
    private int[] _iceTrisGameplay;

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
        // If this object also has SplineToMesh, ensure the mesh is built before we cache baselines.
        var stm = GetComponent<SplineToMesh>();
        if (stm != null) stm.Rebuild();

        _mf = GetComponent<MeshFilter>();
        _mesh = _mf.mesh; // instance
        _mesh.MarkDynamic();

        // Cache full tri list BEFORE we author submeshes, so we always have the original connectivity.
        _baseTris = CaptureBaseTriangles(_mesh);

        _verts = _mesh.vertices; // ONE initial copy only

        // NEW: capture baseline Y from the generated (sloped) spline mesh
        _baseY = new float[_verts.Length];
        for (int i = 0; i < _verts.Length; i++)
            _baseY[i] = _verts[i].y;

        _blocked = new bool[_verts.Length];
        _blockedCount = 0;

        BuildAdjacency();
        _reposeMark = new bool[_verts.Length];

        Tree = new VertexOctree(_mesh, maxDepth, maxPerLeaf);
        _hf = new SnowHeightfieldCollider(transform, _mesh);

        snowController = FindFirstObjectByType<SnowController>();

        ApplyInitialSnow();

        lastClearedCount = CountClearedVerts(_verts);
        snowController.RegisterField(_mesh.vertexCount - _blockedCount, lastClearedCount);

        OnInitialized?.Invoke();
    }

    public float GetBaseY(int i)
    {
        if (_baseY == null) return 0f;
        if ((uint)i >= (uint)_baseY.Length) return 0f;
        return _baseY[i];
    }


    public bool IsIceAtWorld(Vector3 worldPos)
    {
        if (_isIceVert == null || Tree == null) return false;

        Vector3 local = transform.InverseTransformPoint(worldPos);
        local.y = 0f;

        _iceProbe.Clear();
        Tree.QuerySphere(local, iceProbeRadius, _iceProbe);
        if (_iceProbe.Count == 0) return false;

        // use closest sampled vertex as the surface type
        float best = float.PositiveInfinity;
        int bestIdx = -1;

        for (int k = 0; k < _iceProbe.Count; k++)
        {
            int i = _iceProbe[k];
            if ((uint)i >= (uint)_verts.Length) continue;
            if (_blocked != null && _blocked[i]) continue;

            float dx = _verts[i].x - local.x;
            float dz = _verts[i].z - local.z;
            float d2 = dx * dx + dz * dz;

            if (d2 < best)
            {
                best = d2;
                bestIdx = i;
            }
        }

        return bestIdx >= 0 && _isIceVert[bestIdx];
    }

    private static int[] CaptureBaseTriangles(Mesh m)
    {
        if (m == null) return Array.Empty<int>();

        int smc = Mathf.Max(1, m.subMeshCount);

        if (smc == 1)
            return m.triangles;

        int total = 0;
        for (int s = 0; s < smc; s++)
            total += m.GetTriangles(s).Length;

        var combined = new int[total];
        int at = 0;
        for (int s = 0; s < smc; s++)
        {
            var tris = m.GetTriangles(s);
            Array.Copy(tris, 0, combined, at, tris.Length);
            at += tris.Length;
        }
        return combined;
    }

    private void ApplyInitialSnow()
    {
        float minH = snowController.GetSnowSettings().minHeight;
        _iceMask = null;
        _iceMask ??= new float[_verts.Length];

        for (int i = 0; i < _verts.Length; i++)
        {
            Vector3 wp = transform.TransformPoint(_verts[i]);

            float baseY = (_baseY != null && _baseY.Length == _verts.Length) ? _baseY[i] : 0f;
            float minY = baseY + minH;

            if (IsInsideMaskedCollider(wp))
            {
                _blocked[i] = true;
                _blockedCount++;
                _verts[i].y = minY;     // cleared to baseline+minHeight
                _iceMask[i] = 0f;
                continue;
            }

            // Keep noise stable regardless of slope: sample in XZ only.
            Vector3 pXZ = new Vector3(_verts[i].x, 0f, _verts[i].z);

            // Treat SampleNoise as "height offset above baseline"
            _verts[i].y = baseY + snowController.SampleNoise(pXZ);

            if (_verts[i].y < minY) _verts[i].y = minY;

            _iceMask[i] = snowController.SampleIceMask(pXZ);
        }

        _mesh.vertices = _verts;

        RebuildIceSubmeshes();
        RebuildIceVertexFlags();

        ApplyMeshChanges(0);
        MeshEdgeDistanceBaker.BakeToUV2X(_mesh, submeshIndex: 1, planarXZ: true);
    }

    private static ulong EdgeKey(int a, int b)
    {
        if (a > b) (a, b) = (b, a);
        return ((ulong)(uint)a << 32) | (uint)b;
    }

    private static void AddTriEdges(HashSet<ulong> set, int a, int b, int c)
    {
        set.Add(EdgeKey(a, b));
        set.Add(EdgeKey(b, c));
        set.Add(EdgeKey(c, a));
    }

    private void RebuildIceSubmeshes()
    {
        var settings = snowController.GetSnowSettings();

        if (!settings.enableIce || _iceMask == null || _iceMask.Length == 0)
        {
            _iceTrisGameplay = null;
            _mesh.subMeshCount = 1;
            _mesh.SetTriangles(_baseTris, 0, true);
            return;
        }

        int triCount = _baseTris.Length / 3;
        if (triCount <= 0)
        {
            _iceTrisGameplay = null;
            _mesh.subMeshCount = 1;
            _mesh.SetTriangles(_baseTris, 0, true);
            return;
        }

        var baseIceTri = new bool[triCount];
        var blockedTri = new bool[triCount];

        var iceTrisGameplay = new List<int>(_baseTris.Length / 4);

        for (int t = 0; t < triCount; t++)
        {
            int i0 = t * 3;
            int a = _baseTris[i0 + 0];
            int b = _baseTris[i0 + 1];
            int c = _baseTris[i0 + 2];

            if ((_blocked != null) && (_blocked[a] || _blocked[b] || _blocked[c]))
            {
                blockedTri[t] = true;
                baseIceTri[t] = false;
                continue;
            }

            float m = (_iceMask[a] + _iceMask[b] + _iceMask[c]) / 3f;
            bool isIce = (m >= iceTriMajority);
            baseIceTri[t] = isIce;

            if (isIce)
            {
                iceTrisGameplay.Add(a);
                iceTrisGameplay.Add(b);
                iceTrisGameplay.Add(c);
            }
        }

        _iceTrisGameplay = iceTrisGameplay.Count > 0 ? iceTrisGameplay.ToArray() : Array.Empty<int>();

        var visualIceTri = new bool[triCount];
        Array.Copy(baseIceTri, visualIceTri, triCount);

        int rings = Mathf.Max(0, iceVisualExpandRings);
        if (rings > 0)
        {
            var edgeSet = new HashSet<ulong>(triCount * 2);

            for (int t = 0; t < triCount; t++)
            {
                if (!visualIceTri[t]) continue;
                int i0 = t * 3;
                AddTriEdges(edgeSet,
                    _baseTris[i0 + 0],
                    _baseTris[i0 + 1],
                    _baseTris[i0 + 2]);
            }

            var newlyAdded = new List<int>(triCount / 8);

            for (int r = 0; r < rings; r++)
            {
                newlyAdded.Clear();

                for (int t = 0; t < triCount; t++)
                {
                    if (visualIceTri[t]) continue;
                    if (blockedTri[t]) continue;

                    int i0 = t * 3;
                    int a = _baseTris[i0 + 0];
                    int b = _baseTris[i0 + 1];
                    int c = _baseTris[i0 + 2];

                    if (edgeSet.Contains(EdgeKey(a, b)) ||
                        edgeSet.Contains(EdgeKey(b, c)) ||
                        edgeSet.Contains(EdgeKey(c, a)))
                    {
                        newlyAdded.Add(t);
                    }
                }

                if (newlyAdded.Count == 0)
                    break;

                for (int n = 0; n < newlyAdded.Count; n++)
                {
                    int t = newlyAdded[n];
                    visualIceTri[t] = true;

                    int i0 = t * 3;
                    AddTriEdges(edgeSet,
                        _baseTris[i0 + 0],
                        _baseTris[i0 + 1],
                        _baseTris[i0 + 2]);
                }
            }
        }

        var snowTris = new List<int>(_baseTris.Length);
        var iceTris = new List<int>(_baseTris.Length / 4);

        for (int t = 0; t < triCount; t++)
        {
            int i0 = t * 3;
            int a = _baseTris[i0 + 0];
            int b = _baseTris[i0 + 1];
            int c = _baseTris[i0 + 2];

            if (blockedTri[t] || !visualIceTri[t])
            {
                snowTris.Add(a); snowTris.Add(b); snowTris.Add(c);
            }
            else
            {
                iceTris.Add(a); iceTris.Add(b); iceTris.Add(c);
            }
        }

        _mesh.subMeshCount = 2;
        _mesh.SetTriangles(snowTris, 0, true);
        _mesh.SetTriangles(iceTris, 1, true);
    }

    private void RebuildIceVertexFlags()
    {
        if (_isIceVert == null || _isIceVert.Length != _mesh.vertexCount)
            _isIceVert = new bool[_mesh.vertexCount];
        else
            Array.Clear(_isIceVert, 0, _isIceVert.Length);

        var settings = snowController.GetSnowSettings();
        if (!settings.enableIce) return;
        if (_iceTrisGameplay == null || _iceTrisGameplay.Length == 0) return;

        for (int t = 0; t < _iceTrisGameplay.Length; t++)
        {
            int i = _iceTrisGameplay[t];
            if ((uint)i >= (uint)_isIceVert.Length) continue;
            if (_blocked != null && _blocked[i]) continue;
            _isIceVert[i] = true;
        }
    }

    private bool PassesEditType(int vertIndex, EditType editType)
    {
        if (_blocked != null && _blocked[vertIndex]) return false;

        if (editType == EditType.Both) return true;

        bool isIce = (_isIceVert != null) && _isIceVert[vertIndex];
        return editType == EditType.Ice ? isIce : !isIce;
    }

    private int CountClearedVerts(Vector3[] verts)
    {
        float minH = SnowController.Instance.GetSnowSettings().minHeight;

        int count = 0;
        for (int i = 0; i < verts.Length; i++)
        {
            if (_blocked != null && _blocked[i]) continue;

            float baseY = (_baseY != null && _baseY.Length == verts.Length) ? _baseY[i] : 0f;
            float thresh = (baseY + minH) + clearedEpsilon;

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

    public int EditY(List<int> indices, Vector3 hitLocal, float radius, float value, YEditMode mode, float smoothness = 0f, EditType editType = EditType.Both)
    {
        if (indices == null || indices.Count == 0) return 0;

        int clearedDelta = 0;
        int changedCount = 0;

        float minH = snowController.GetSnowSettings().minHeight;

        const float changeEps = 1e-6f;

        if (smoothness <= 0f)
        {
            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                if (!PassesEditType(i, editType)) continue;

                float baseY = (_baseY != null && _baseY.Length == _verts.Length) ? _baseY[i] : 0f;
                float minY = baseY + minH;
                float thresh = minY + clearedEpsilon;

                float before = _verts[i].y;
                bool wasCleared = before <= thresh;

                switch (mode)
                {
                    case YEditMode.Set:      _verts[i].y = value; break;
                    case YEditMode.Add:      _verts[i].y += value; break;
                    case YEditMode.Subtract: _verts[i].y -= value; break;
                }

                _verts[i].y = Mathf.Max(minY, _verts[i].y);

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
                if (!PassesEditType(i, editType)) continue;

                float baseY = (_baseY != null && _baseY.Length == _verts.Length) ? _baseY[i] : 0f;
                float minY = baseY + minH;
                float thresh = minY + clearedEpsilon;

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

                _verts[i].y = Mathf.Max(minY, _verts[i].y);

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
        float smoothness = 1f,
        EditType editType = EditType.Both)
    {
        if (removeIndices == null || removeIndices.Count == 0) return 0;
        if (depositIndices == null || depositIndices.Count == 0) return 0;

        float minH = snowController.GetSnowSettings().minHeight;
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

        float totalRemoved = 0f;

        int clearedDelta = 0;
        int changedCount = 0;

        for (int k = 0; k < removeIndices.Count; k++)
        {
            int i = removeIndices[k];
            if (!PassesEditType(i, editType)) continue;

            float baseY = (_baseY != null && _baseY.Length == _verts.Length) ? _baseY[i] : 0f;
            float minY = baseY + minH;
            float thresh = minY + clearedEpsilon;

            float dx = _verts[i].x - cx;
            float dz = _verts[i].z - cz;

            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist > rRem) continue;

            float u = Mathf.Clamp01(1f - dist * invRem);
            u = u * u * (3f - 2f * u);                 // SmoothStep
            u = Mathf.Pow(u, 1f / powSmooth);
            float t = u;

            float s = dx * forwardLocal.x + dz * forwardLocal.z; // behind < 0
            if (s >= 0f) continue;

            float before = _verts[i].y;
            bool wasCleared = before <= thresh;

            float behind01 = Mathf.Clamp01((-s) * invRem);
            float desiredRemove = pushAmount * t * behind01;

            float available = Mathf.Max(0f, before - minY);
            float actualRemove = Mathf.Min(desiredRemove, available);
            if (actualRemove <= 0f) continue;

            float newY = before - actualRemove;
            if (newY < minY) newY = minY;
            _verts[i].y = newY;

            float after = _verts[i].y;
            if (Mathf.Abs(after - before) > changeEps) changedCount++;

            bool isCleared = after <= thresh;
            if (wasCleared != isCleared) clearedDelta += isCleared ? 1 : -1;

            totalRemoved += (before - after);
        }

        if (totalRemoved <= 0f) return 0;

        _tmpFrontWeights ??= new float[512];
        if (_tmpFrontWeights.Length < depositIndices.Count) _tmpFrontWeights = new float[depositIndices.Count * 2];
        for (int k = 0; k < depositIndices.Count; k++) _tmpFrontWeights[k] = 0f;

        float totalW = 0f;

        for (int k = 0; k < depositIndices.Count; k++)
        {
            int i = depositIndices[k];
            if (!PassesEditType(i, editType)) continue;

            float dx = _verts[i].x - dcx;
            float dz = _verts[i].z - dcz;

            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist > rDep) continue;

            float u = Mathf.Clamp01(1f - dist * invDep);
            u = u * u * (3f - 2f * u);
            u = Mathf.Pow(u, 1f / powSmooth);
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
            if (!PassesEditType(i, editType)) continue;

            float baseY = (_baseY != null && _baseY.Length == _verts.Length) ? _baseY[i] : 0f;
            float minY = baseY + minH;
            float thresh = minY + clearedEpsilon;

            float before = _verts[i].y;
            bool wasCleared = before <= thresh;

            float add = totalRemoved * (w / totalW);
            _verts[i].y = before + add;

            float after = _verts[i].y;
            if (Mathf.Abs(after - before) > changeEps) changedCount++;

            bool isCleared = after <= thresh;
            if (wasCleared != isCleared) clearedDelta += isCleared ? 1 : -1;
        }

        ApplyAngleOfRepose(depositIndices, minH, editType, ref clearedDelta, ref changedCount);

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

        int[] tris = _baseTris;
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

    private void ApplyAngleOfRepose(List<int> seeds, float minH, EditType editType, ref int clearedDelta, ref int changedCount)
    {
        if (reposeIterations <= 0 || reposeStrength <= 0f) return;
        if (seeds == null || seeds.Count == 0) return;
        if (_neighbors == null || _reposeMark == null) return;

        _reposeList.Clear();

        for (int k = 0; k < seeds.Count; k++)
        {
            int i = seeds[k];
            if ((uint)i >= (uint)_verts.Length) continue;
            if (!PassesEditType(i, editType)) continue;
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
                    if (!PassesEditType(j, editType)) continue;

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

                        float baseYi = (_baseY != null && _baseY.Length == _verts.Length) ? _baseY[i] : 0f;
                        float baseYj = (_baseY != null && _baseY.Length == _verts.Length) ? _baseY[j] : 0f;

                        float minYi = baseYi + minH;
                        float threshI = minYi + clearedEpsilon;
                        float threshJ = (baseYj + minH) + clearedEpsilon;

                        float canGive = Mathf.Max(0f, vi.y - minYi);
                        move = Mathf.Min(move, canGive);
                        if (move <= 0f) continue;

                        bool iWas = vi.y <= threshI;
                        bool jWas = vj.y <= threshJ;

                        vi.y -= move;
                        vj.y += move;

                        bool iIs = vi.y <= threshI;
                        bool jIs = vj.y <= threshJ;

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