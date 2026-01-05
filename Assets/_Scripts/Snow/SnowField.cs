using System.Collections.Generic;
using UnityEngine;

public enum YEditMode { Set, Add, Subtract }

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class SnowField : MonoBehaviour
{
    private MeshRenderer meshRenderer;
    private MeshFilter meshFilter;

    private Mesh _mesh;                 // single instanced mesh we edit
    public VertexOctree _tree;

    private SnowHeightfieldCollider _hf; // custom “collider” (raycast + contains)

    void Awake()
    {
        meshRenderer = GetComponent<MeshRenderer>();
        meshFilter = GetComponent<MeshFilter>();

        _mesh = meshFilter.mesh; // instance once
        _mesh.MarkDynamic();
    }

    void Start()
    {
        // Build once (XZ-based version)
        _tree = new VertexOctree(_mesh, maxDepth: 10, maxPerLeaf: 32);
        SnowUtils.InferGridDims(_mesh, out int w, out int h);
        _hf = new SnowHeightfieldCollider(transform, _mesh);

        Snow();
    }

    private void Snow()
    {
        var verts = _mesh.vertices;
        for (int i = 0; i < verts.Length; i++)
            verts[i].y = SnowController.Instance.SampleNoise(verts[i]);

        _mesh.vertices = verts;
        ApplyMeshChanges();
    }

    private void ApplyMeshChanges()
    {
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();

        // No rebuild needed (XZ tree). Just refresh vertex reference for query filtering.
        _tree.RefreshVertices(_mesh);

        // Custom collider uses current verts
        _hf.Refresh();
    }

    public bool RaycastSnow(Ray worldRay, out SnowHeightfieldCollider.Hit hit, float maxDistance = 1000f)
        => _hf.Raycast(worldRay, out hit, maxDistance);

    public bool ContainsPoint(Vector3 worldPoint)
        => _hf.ContainsPoint(worldPoint);

    public void EditY(List<int> indexes, float value, YEditMode mode, float smoothness = 0f)
    {
        if (indexes == null || indexes.Count == 0) return;

        var verts = _mesh.vertices;

        Vector3 center = Vector3.zero;
        for (int k = 0; k < indexes.Count; k++) center += verts[indexes[k]];
        center /= Mathf.Max(1, indexes.Count);

        float maxDist = 0f;
        for (int k = 0; k < indexes.Count; k++)
            maxDist = Mathf.Max(maxDist, Vector3.Distance(center, verts[indexes[k]]));
        if (maxDist <= 1e-6f) maxDist = 1f;

        // Exact old behavior: smoothness <= 0 => uniform edit (like original separate funcs)
        if (smoothness <= 0f)
        {
            for (int k = 0; k < indexes.Count; k++)
            {
                int i = indexes[k];
                switch (mode)
                {
                    case YEditMode.Set:      verts[i].y = value; break;
                    case YEditMode.Add:      verts[i].y += value; break;
                    case YEditMode.Subtract: verts[i].y -= value; break;
                }
            }

            _mesh.vertices = verts;
            ApplyMeshChanges();
            return;
        }

        for (int k = 0; k < indexes.Count; k++)
        {
            int i = indexes[k];

            float t = 1f - (Vector3.Distance(center, verts[i]) / maxDist);
            t = Mathf.Clamp01(t);
            t = Mathf.Pow(t, smoothness);

            switch (mode)
            {
                case YEditMode.Set:
                    verts[i].y = Mathf.Lerp(verts[i].y, value, t);
                    break;

                case YEditMode.Add:
                    verts[i].y += value * t;
                    break;

                case YEditMode.Subtract:
                    verts[i].y -= value * t;
                    break;
            }
        }

        _mesh.vertices = verts;
        ApplyMeshChanges();
    }
}
