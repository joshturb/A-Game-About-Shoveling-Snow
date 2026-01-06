// SnowField.cs
using System.Collections.Generic;
using UnityEngine;

public enum YEditMode { Set, Add, Subtract }

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class SnowField : MonoBehaviour
{
    [Header("Octree")]
    public int maxDepth = 10;
    public int maxPerLeaf = 32;

    private MeshFilter _mf;
    private Mesh _mesh;

    public VertexOctree Tree { get; private set; }
    private SnowHeightfieldCollider _hf;

    void Awake()
    {
        _mf = GetComponent<MeshFilter>();

        _mesh = _mf.mesh;          // instantiate once
        _mesh.MarkDynamic();
    }

    void Start()
    {
        Tree = new VertexOctree(_mesh, maxDepth, maxPerLeaf);
        _hf = new SnowHeightfieldCollider(transform, _mesh);

        ApplyInitialSnow();
    }

    private void ApplyInitialSnow()
    {
        var v = _mesh.vertices;
        for (int i = 0; i < v.Length; i++)
            v[i].y = SnowController.Instance.SampleNoise(v[i]);

        _mesh.vertices = v;
        ApplyMeshChanges();
    }

    private void ApplyMeshChanges()
    {
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();

        Tree.RefreshVertices(_mesh);
        _hf.Refresh();
    }

    public bool RaycastSnow(Ray worldRay, out SnowHeightfieldCollider.Hit hit, float maxDistance = 500f)
        => _hf.Raycast(worldRay, out hit, maxDistance);

    public bool ContainsPoint(Vector3 worldPoint)
        => _hf.ContainsPoint(worldPoint);

    public void QueryBrush(Vector3 hitLocal, float radius, List<int> results)
    {
        hitLocal.y = 0f;
        Tree.QuerySphere(hitLocal, radius, results);
    }

    public void EditY(List<int> indices, Vector3 hitLocal, float radius, float value, YEditMode mode, float smoothness = 0f)
    {
        if (indices == null || indices.Count == 0) return;

        var verts = _mesh.vertices;

        if (smoothness <= 0f)
        {
            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                switch (mode)
                {
                    case YEditMode.Set:      verts[i].y = value; break;
                    case YEditMode.Add:      verts[i].y += value; break;
                    case YEditMode.Subtract: verts[i].y -= value; break;
                }
                verts[i].y = Mathf.Max(0, verts[i].y);
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

                float dx = verts[i].x - cx;
                float dz = verts[i].z - cz;

                float t = 1f - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz) * invR);
                t = Mathf.Pow(t, smoothness);

                switch (mode)
                {
                    case YEditMode.Set:      verts[i].y = Mathf.Lerp(verts[i].y, value, t); break;
                    case YEditMode.Add:      verts[i].y += value * t; break;
                    case YEditMode.Subtract: verts[i].y -= value * t; break;
                }
                verts[i].y = Mathf.Max(0f, verts[i].y);
            }
        }

        _mesh.vertices = verts;
        ApplyMeshChanges();
    }
}
