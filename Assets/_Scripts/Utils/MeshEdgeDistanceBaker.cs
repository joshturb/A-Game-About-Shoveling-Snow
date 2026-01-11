using System;
using System.Collections.Generic;
using UnityEngine;

public static class MeshEdgeDistanceBaker
{
    // Undirected edge key (sorted indices)
    private readonly struct EdgeKey : IEquatable<EdgeKey>
    {
        public readonly int A, B;
        public EdgeKey(int i0, int i1)
        {
            if (i0 < i1) { A = i0; B = i1; }
            else { A = i1; B = i0; }
        }
        public bool Equals(EdgeKey other) => A == other.A && B == other.B;
        public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
        public override int GetHashCode() => (A * 73856093) ^ (B * 19349663);
    }

    // Simple min-heap for (vertex, dist)
    private sealed class MinHeap
    {
        private readonly List<(int v, float d)> _data = new();
        public int Count => _data.Count;

        public void Push(int v, float d)
        {
            _data.Add((v, d));
            SiftUp(_data.Count - 1);
        }

        public (int v, float d) Pop()
        {
            var root = _data[0];
            int last = _data.Count - 1;
            _data[0] = _data[last];
            _data.RemoveAt(last);
            if (_data.Count > 0) SiftDown(0);
            return root;
        }

        private void SiftUp(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_data[p].d <= _data[i].d) break;
                (_data[p], _data[i]) = (_data[i], _data[p]);
                i = p;
            }
        }

        private void SiftDown(int i)
        {
            int n = _data.Count;
            while (true)
            {
                int l = (i << 1) + 1;
                int r = l + 1;
                if (l >= n) break;

                int m = (r < n && _data[r].d < _data[l].d) ? r : l;
                if (_data[i].d <= _data[m].d) break;

                (_data[i], _data[m]) = (_data[m], _data[i]);
                i = m;
            }
        }
    }

    /// Bakes distance-to-boundary into UV2.x (UV channel 1).
    /// If planarXZ = true, distances use XZ only (typical for ground meshes).
    public static void BakeToUV2X(Mesh mesh, int submeshIndex, bool planarXZ = true)
    {
        if (mesh == null) throw new ArgumentNullException(nameof(mesh));
        if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount)
            throw new ArgumentOutOfRangeException(nameof(submeshIndex));

        var verts = mesh.vertices;
        int vCount = verts.Length;

        var tris = mesh.GetTriangles(submeshIndex);
        if (vCount == 0 || tris == null || tris.Length < 3)
            return;

        // boundary edges computed ONLY inside this submesh => perimeter of each ice island
        var edgeUse = new Dictionary<EdgeKey, int>(tris.Length);
        void AddEdge(int a, int b)
        {
            var k = new EdgeKey(a, b);
            edgeUse.TryGetValue(k, out int c);
            edgeUse[k] = c + 1;
        }

        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            AddEdge(a, b);
            AddEdge(b, c);
            AddEdge(c, a);
        }

        var inSub = new bool[vCount];
        for (int t = 0; t < tris.Length; t++) inSub[tris[t]] = true;

        var isBoundaryVert = new bool[vCount];
        foreach (var kv in edgeUse)
        {
            if (kv.Value == 1)
            {
                isBoundaryVert[kv.Key.A] = true;
                isBoundaryVert[kv.Key.B] = true;
            }
        }

        // adjacency ONLY within submesh connectivity
        var adj = new List<int>[vCount];
        for (int i = 0; i < vCount; i++) adj[i] = new List<int>(8);

        void Link(int a, int b)
        {
            if (a == b) return;
            if (!inSub[a] || !inSub[b]) return;
            adj[a].Add(b);
            adj[b].Add(a);
        }

        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            Link(a, b);
            Link(b, c);
            Link(c, a);
        }

        var dist = new float[vCount];
        for (int i = 0; i < vCount; i++) dist[i] = float.PositiveInfinity;

        var heap = new MinHeap();
        for (int i = 0; i < vCount; i++)
        {
            if (!inSub[i] || !isBoundaryVert[i]) continue;
            dist[i] = 0f;
            heap.Push(i, 0f);
        }

        float EdgeLen(int i, int j)
        {
            Vector3 a = verts[i], b = verts[j];
            if (!planarXZ) return Vector3.Distance(a, b);
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        while (heap.Count > 0)
        {
            var (v, d) = heap.Pop();
            if (d > dist[v]) continue;

            var nbs = adj[v];
            for (int k = 0; k < nbs.Count; k++)
            {
                int u = nbs[k];
                float nd = d + EdgeLen(v, u);
                if (nd < dist[u])
                {
                    dist[u] = nd;
                    heap.Push(u, nd);
                }
            }
        }

        // write UV2.x for submesh verts; non-ice verts get 0
        var existing = new List<Vector2>();
        mesh.GetUVs(1, existing);
        bool hasExisting = existing != null && existing.Count == vCount;

        var uv2 = new List<Vector2>(vCount);
        for (int i = 0; i < vCount; i++)
        {
            float y = hasExisting ? existing[i].y : 0f;
            float x = (inSub[i] && float.IsFinite(dist[i])) ? dist[i] : 0f;
            uv2.Add(new Vector2(x, y));
        }

        mesh.SetUVs(1, uv2);
        mesh.UploadMeshData(false);
    }
}