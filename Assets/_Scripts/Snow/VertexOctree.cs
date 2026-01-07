using System;
using System.Collections.Generic;
using UnityEngine;

/// XZ-only spatial index (quadtree) but kept as VertexOctree for compatibility.
/// Key fix: NO bounds.Contains insertion (avoids “spill indices” being lost in internal nodes).
public sealed class VertexOctree
{
    public sealed class Node
    {
        public Bounds bounds;        // huge Y slab so queries never miss due to height changes
        public Node[] children;      // null if leaf, length 4 if split
        public List<int> indices;    // only used on leaves

        public bool IsLeaf => children == null;

        public Node(Bounds b, int capacityHint)
        {
            bounds = b;
            indices = new List<int>(capacityHint);
        }
    }

    private Vector3[] _verts;               // local-space
    private readonly int _maxDepth;
    private readonly int _maxPerLeaf;
    private readonly float _minHalfExtentXZ;
    private const float HUGE_Y = 100000f;

    public Node Root { get; private set; }

    public VertexOctree(Mesh mesh, int maxDepth = 10, int maxPerLeaf = 32, float minHalfExtent = 0.0001f)
    {
        if (mesh == null) throw new ArgumentNullException(nameof(mesh));
        _maxDepth = Mathf.Max(0, maxDepth);
        _maxPerLeaf = Mathf.Max(1, maxPerLeaf);
        _minHalfExtentXZ = Mathf.Max(0f, minHalfExtent);

        RebuildFromMesh(mesh);
    }

   // public void RefreshVertices(Vector3[] verts) { _verts = verts ?? throw new ArgumentNullException(nameof(verts)); }

    public void RebuildFromMesh(Mesh mesh)
    {
        if (mesh == null) throw new ArgumentNullException(nameof(mesh));
        _verts = mesh.vertices ?? throw new InvalidOperationException("Mesh has no vertices.");

        // XZ-only bounds, huge Y to be height-edit safe
        var b = mesh.bounds;
        var size = b.size;

        size.x = Mathf.Max(size.x, _minHalfExtentXZ * 2f);
        size.z = Mathf.Max(size.z, _minHalfExtentXZ * 2f);
        size.y = HUGE_Y * 2f;

        b.center = new Vector3(b.center.x, 0f, b.center.z);
        b.size = size;

        Root = new Node(b, _maxPerLeaf);
        Build();
    }

    private void Build()
    {
        for (int i = 0; i < _verts.Length; i++)
            Insert(Root, i, 0);
    }

    private void Insert(Node node, int index, int depth)
    {
        if (node.IsLeaf)
        {
            node.indices.Add(index);

            if (depth < _maxDepth &&
                node.indices.Count > _maxPerLeaf &&
                CanSubdivide(node.bounds))
            {
                Subdivide(node);

                // redistribute all indices into children (no spill)
                var old = node.indices;
                node.indices = new List<int>(_maxPerLeaf);

                for (int j = 0; j < old.Count; j++)
                    Insert(node, old[j], depth); // will go into children
            }
            return;
        }

        int child = ChildIndexXZ(node.bounds.center, _verts[index]);
        Insert(node.children[child], index, depth + 1);
    }

    private bool CanSubdivide(Bounds b)
    {
        var e = b.extents;
        return (e.x > _minHalfExtentXZ) && (e.z > _minHalfExtentXZ);
    }

    private static int ChildIndexXZ(Vector3 c, Vector3 p)
    {
        int ix = (p.x >= c.x) ? 1 : 0;
        int iz = (p.z >= c.z) ? 1 : 0;
        return ix | (iz << 1); // 0..3
    }

    private void Subdivide(Node node)
    {
        node.children = new Node[4];

        Vector3 c = node.bounds.center;
        Vector3 e = node.bounds.extents;

        // Half in XZ, keep huge Y
        Vector3 childExt = new Vector3(e.x * 0.5f, e.y, e.z * 0.5f);

        for (int i = 0; i < 4; i++)
        {
            float sx = ((i & 1) != 0) ? 1f : -1f;
            float sz = ((i & 2) != 0) ? 1f : -1f;

            Vector3 childCenter = c + new Vector3(sx * childExt.x, 0f, sz * childExt.z);

            var b = new Bounds(
                childCenter,
                new Vector3(childExt.x * 2f, node.bounds.size.y, childExt.z * 2f)
            );

            node.children[i] = new Node(b, _maxPerLeaf);
        }
    }

    /// Query in XZ (circle), ignores Y. Kept name QuerySphere for compatibility.
    public void QuerySphere(Vector3 center, float radius, List<int> results)
    {
        if (results == null) throw new ArgumentNullException(nameof(results));
        results.Clear();

        float r = Mathf.Max(0f, radius);
        float r2 = r * r;

        // AABB in XZ, huge Y
        var aabb = new Bounds(
            new Vector3(center.x, 0f, center.z),
            new Vector3(r * 2f, HUGE_Y * 2f, r * 2f)
        );

        QueryInternal(Root, aabb, center.x, center.z, r2, results);
    }

    private void QueryInternal(Node node, Bounds aabb, float cx, float cz, float r2, List<int> results)
    {
        if (node == null) return;
        if (!node.bounds.Intersects(aabb)) return;

        if (node.IsLeaf)
        {
            for (int i = 0; i < node.indices.Count; i++)
            {
                int vi = node.indices[i];
                Vector3 p = _verts[vi];

                float dx = p.x - cx;
                float dz = p.z - cz;
                if ((dx * dx + dz * dz) <= r2)
                    results.Add(vi);
            }
            return;
        }

        for (int i = 0; i < 4; i++)
            QueryInternal(node.children[i], aabb, cx, cz, r2, results);
    }

    public void QueryBounds(Transform meshTransform, Vector3 worldCenter, Vector2 halfExtentsXZ, List<int> results)
    {
        if (meshTransform == null) throw new ArgumentNullException(nameof(meshTransform));
        if (results == null) throw new ArgumentNullException(nameof(results));
        results.Clear();

        // 4 world corners in XZ
        Vector3 c = worldCenter;
        Vector3[] wc =
        {
            new Vector3(c.x - halfExtentsXZ.x, c.y, c.z - halfExtentsXZ.y),
            new Vector3(c.x - halfExtentsXZ.x, c.y, c.z + halfExtentsXZ.y),
            new Vector3(c.x + halfExtentsXZ.x, c.y, c.z - halfExtentsXZ.y),
            new Vector3(c.x + halfExtentsXZ.x, c.y, c.z + halfExtentsXZ.y),
        };

        // Convert to local and encapsulate
        Bounds local = new Bounds(meshTransform.InverseTransformPoint(wc[0]), Vector3.zero);
        for (int i = 1; i < 4; i++)
            local.Encapsulate(meshTransform.InverseTransformPoint(wc[i]));

        // XZ-only query slab
        local.center = new Vector3(local.center.x, 0f, local.center.z);
        local.size   = new Vector3(local.size.x, HUGE_Y * 2f, local.size.z);

        QueryBoundsInternal(Root, local, results);
    }

    private void QueryBoundsInternal(Node node, Bounds query, List<int> results)
    {
        if (node == null) return;
        if (!node.bounds.Intersects(query)) return;

        if (node.IsLeaf)
        {
            float minX = query.min.x, maxX = query.max.x;
            float minZ = query.min.z, maxZ = query.max.z;

            for (int i = 0; i < node.indices.Count; i++)
            {
                int vi = node.indices[i];
                Vector3 p = _verts[vi];

                if (p.x >= minX && p.x <= maxX && p.z >= minZ && p.z <= maxZ)
                    results.Add(vi);
            }
            return;
        }
        Debug.Log($"{results.Count}");
        for (int i = 0; i < 4; i++)
            QueryBoundsInternal(node.children[i], query, results);
    }
}
