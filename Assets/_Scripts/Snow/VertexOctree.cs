using System;
using System.Collections.Generic;
using UnityEngine;

/// Heightfield-friendly vertex lookup:
/// - Tree is built and queried in XZ only (Y ignored), so changing vertex Y / normals does NOT require rebuilds.
/// - QuerySphere uses XZ distance.
/// - Root bounds use a huge Y slab so contains/intersects never fail due to height changes.
public sealed class VertexOctree
{
    public sealed class Node
    {
        public Bounds bounds;
        public Node[] children;          // null if leaf
        public List<int> indices;        // vertex indices stored at leaf

        public bool IsLeaf => children == null;

        public Node(Bounds b, int capacityHint)
        {
            bounds = b;
            indices = new List<int>(capacityHint);
        }
    }

    private Vector3[] _verts;                 // local-space vertices (we read XZ from this)
    private readonly int _maxDepth;
    private readonly int _maxPerLeaf;
    private readonly float _minHalfExtent;

    public Node Root { get; private set; }

    public VertexOctree(Mesh mesh, int maxDepth = 10, int maxPerLeaf = 32, float minHalfExtent = 0.0001f)
    {
        if (mesh == null) throw new ArgumentNullException(nameof(mesh));

        _maxDepth = Mathf.Max(0, maxDepth);
        _maxPerLeaf = Mathf.Max(1, maxPerLeaf);
        _minHalfExtent = Mathf.Max(0f, minHalfExtent);

        BuildFromMesh(mesh);
    }

    /// Call this ONLY if your vertex X/Z positions change or the mesh topology changes.
    /// If you're only editing Y (height), you do NOT need to rebuild; just call RefreshVertices(mesh).
    public void BuildFromMesh(Mesh mesh)
    {
        if (mesh == null) throw new ArgumentNullException(nameof(mesh));

        _verts = mesh.vertices ?? throw new InvalidOperationException("Mesh has no vertices.");

        var b = mesh.bounds;

        // Work in XZ only: lock center.y to 0, make Y extremely tall so bounds tests always pass.
        b.center = new Vector3(b.center.x, 0f, b.center.z);

        var size = b.size;
        size.x = Mathf.Max(size.x, _minHalfExtent * 2f);
        size.z = Mathf.Max(size.z, _minHalfExtent * 2f);
        size.y = Mathf.Max(size.y, _minHalfExtent * 2f);

        // huge Y slab to survive any height changes without rebuild
        size.y = Mathf.Max(size.y, 100000f);

        b.size = size;

        Root = new Node(b, _maxPerLeaf);
        Build();
    }

    /// Updates the vertex array reference so queries use current positions.
    /// Cheap. Use this after you modify mesh.vertices if only Y changed.
    public void RefreshVertices(Mesh mesh)
    {
        if (mesh == null) throw new ArgumentNullException(nameof(mesh));
        _verts = mesh.vertices ?? throw new InvalidOperationException("Mesh has no vertices.");
    }

    private void Build()
    {
        for (int i = 0; i < _verts.Length; i++)
            Insert(Root, i, 0);
    }

    private static Vector3 KeyXZ(Vector3 p)
    {
        p.y = 0f;
        return p;
    }

    private Vector3 KeyXZ(int vi)
    {
        Vector3 p = _verts[vi];
        p.y = 0f;
        return p;
    }

    private void Insert(Node node, int index, int depth)
    {
        Vector3 p = KeyXZ(index);

        if (!node.bounds.Contains(p))
        {
            // Precision edge-case: store at this node (root catch-all).
            node.indices.Add(index);
            return;
        }

        if (node.IsLeaf)
        {
            node.indices.Add(index);

            if (depth < _maxDepth &&
                node.indices.Count > _maxPerLeaf &&
                CanSubdivide(node.bounds))
            {
                Subdivide(node);

                var old = node.indices;
                node.indices = new List<int>(_maxPerLeaf);
                for (int j = 0; j < old.Count; j++)
                    Insert(node, old[j], depth);
            }
            return;
        }

        int child = ChildIndex(KeyXZ(node.bounds.center), p);
        Insert(node.children[child], index, depth + 1);
    }

    private bool CanSubdivide(Bounds b)
    {
        var e = b.extents;
        // Only care about X/Z extent; Y is intentionally huge.
        return (e.x > _minHalfExtent) && (e.z > _minHalfExtent);
    }

    // Still 8 children for minimal churn, but we force iy=0 so only the lower 4 are used.
    private static int ChildIndex(Vector3 c, Vector3 p)
    {
        c.y = 0f; p.y = 0f;

        int ix = (p.x >= c.x) ? 1 : 0;
        int iz = (p.z >= c.z) ? 1 : 0;
        int iy = 0;

        return ix | (iy << 1) | (iz << 2); // yields {0,1,4,5}
    }

    private void Subdivide(Node node)
    {
        node.children = new Node[8];

        Vector3 c = node.bounds.center;
        Vector3 e = node.bounds.extents * 0.5f;

        for (int i = 0; i < 8; i++)
        {
            // Only X/Z splitting matters; Y stays the same (huge slab).
            float sx = ((i & 1) != 0) ? 1f : -1f;
            float sy = 0f;
            float sz = ((i & 4) != 0) ? 1f : -1f;

            Vector3 childCenter = c + new Vector3(sx * e.x, sy * e.y, sz * e.z);

            // Keep Y huge so queries never miss due to height changes.
            var childBounds = new Bounds(childCenter, e * 2f);
            childBounds.center = new Vector3(childBounds.center.x, 0f, childBounds.center.z);
            childBounds.size = new Vector3(childBounds.size.x, node.bounds.size.y, childBounds.size.z);

            node.children[i] = new Node(childBounds, _maxPerLeaf);
        }
    }

    /// Query vertices within radius in XZ (local-space). Returns vertex indices.
    public void QuerySphere(Vector3 center, float radius, List<int> results)
    {
        if (results == null) throw new ArgumentNullException(nameof(results));
        results.Clear();

        float r = Mathf.Max(0f, radius);
        float r2 = r * r;

        // AABB in XZ, huge Y (matches the slab)
        Vector3 c = center;
        c.y = 0f;

        var aabb = new Bounds(c, new Vector3(r * 2f, Root.bounds.size.y, r * 2f));
        QueryAabbInternal(Root, aabb, c, r2, results, exactSphereXZ: true);
    }

    /// Query nodes intersecting an AABB (local-space).
    /// Note: for heightfield usage, pass bounds with center.y=0 and a tall Y size.
    public void QueryAabb(Bounds aabb, List<int> results)
    {
        if (results == null) throw new ArgumentNullException(nameof(results));
        results.Clear();

        // normalize to slab space
        aabb.center = new Vector3(aabb.center.x, 0f, aabb.center.z);
        aabb.size = new Vector3(aabb.size.x, Root.bounds.size.y, aabb.size.z);

        QueryAabbInternal(Root, aabb, Vector3.zero, 0f, results, exactSphereXZ: false);
    }

    private void QueryAabbInternal(Node node, Bounds aabb, Vector3 sphereCenter, float r2, List<int> results, bool exactSphereXZ)
    {
        if (node == null) return;
        if (!node.bounds.Intersects(aabb)) return;

        if (node.IsLeaf)
        {
            if (!exactSphereXZ)
            {
                results.AddRange(node.indices);
                return;
            }

            for (int i = 0; i < node.indices.Count; i++)
            {
                int vi = node.indices[i];
                Vector3 p = _verts[vi];

                float dx = p.x - sphereCenter.x;
                float dz = p.z - sphereCenter.z;

                if ((dx * dx + dz * dz) <= r2)
                    results.Add(vi);
            }
            return;
        }

        for (int i = 0; i < 8; i++)
            QueryAabbInternal(node.children[i], aabb, sphereCenter, r2, results, exactSphereXZ);
    }
}
