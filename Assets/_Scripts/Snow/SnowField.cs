// SnowField.cs
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class SnowField : MonoBehaviour
{
    private SnowController snowController;
    
    [Header("Octree")]
    public int maxDepth = 10;
    public int maxPerLeaf = 32;

    [Header("Snow Progress")]
    [SerializeField, Min(0f)] private float clearedEpsilon = 0.001f;

    public VertexOctree Tree { get; private set; }
    public SnowHeightfieldCollider _hf;
    public Vector3[] GetVerts() => _verts;
    private int lastClearedCount;
    private MeshFilter _mf;
    private Mesh _mesh;
    private Vector3[] _verts;
    private float _clearedThreshold;
    
    void Awake()
    {
        _mf = GetComponent<MeshFilter>();
        _mesh = _mf.mesh;
        _mesh.MarkDynamic();

        _verts = _mesh.vertices; // ONE initial copy only

        Tree = new VertexOctree(_mesh, maxDepth, maxPerLeaf);
        _hf = new SnowHeightfieldCollider(transform, _mesh);

        snowController = SnowController.Instance;

        float minH = snowController.GetSnowSettings().minHeight;
        _clearedThreshold = minH + clearedEpsilon;

        ApplyInitialSnow();

        lastClearedCount = CountClearedVerts(_verts);
        snowController.RegisterField(_mesh.vertexCount, lastClearedCount);
    }

    private void ApplyInitialSnow()
    {
        for (int i = 0; i < _verts.Length; i++)
            _verts[i].y = SnowController.Instance.SampleNoise(_verts[i]);

        _mesh.vertices = _verts;        // upload once
        ApplyMeshChanges(0);            // no rescan
    }

    private int CountClearedVerts(Vector3[] verts)
    {
        float minH = SnowController.Instance.GetSnowSettings().minHeight;
        float thresh = minH + clearedEpsilon;

        int count = 0;
        for (int i = 0; i < verts.Length; i++)
            if (verts[i].y <= thresh) count++;

        return count;
    }

    private void ApplyMeshChanges(int clearedDelta)
    {
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();

        _hf.Refresh(_verts);            // no mesh read

        if (clearedDelta != 0)
        {
            SnowController.Instance.ApplyClearedDelta(clearedDelta);
            lastClearedCount += clearedDelta;
        }
    }


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
        Tree.QueryBounds(transform, worldBounds.center, new Vector2(worldBounds.extents.x, worldBounds.extents.z), results);
    }

    public void EditY(List<int> indices, Vector3 hitLocal, float radius, float value, YEditMode mode, float smoothness = 0f)
    {
        if (indices == null || indices.Count == 0) return;

        int clearedDelta = 0;
        float minH = snowController.GetSnowSettings().minHeight;
        float thresh = _clearedThreshold;

        if (smoothness <= 0f)
        {
            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];

                bool wasCleared = _verts[i].y <= thresh;

                switch (mode)
                {
                    case YEditMode.Set:      _verts[i].y = value; break;
                    case YEditMode.Add:      _verts[i].y += value; break;
                    case YEditMode.Subtract: _verts[i].y -= value; break;
                }

                _verts[i].y = Mathf.Max(minH, _verts[i].y);

                bool isCleared = _verts[i].y <= thresh;
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

                bool wasCleared = _verts[i].y <= thresh;

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

                bool isCleared = _verts[i].y <= thresh;
                if (wasCleared != isCleared) clearedDelta += isCleared ? 1 : -1;
            }
        }

        _mesh.vertices = _verts;         // upload only (no readback)
        ApplyMeshChanges(clearedDelta);  // no rescan + no mesh.vertices calls
    }

}
