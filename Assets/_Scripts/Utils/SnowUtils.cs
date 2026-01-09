using System.Collections.Generic;
using UnityEngine;

public static class SnowUtils
{
    public static HashSet<int> FindEdgeVertices(int[] triangles)
    {
        var edgeCount = new Dictionary<(int, int), int>();

        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i];
            int b = triangles[i + 1];
            int c = triangles[i + 2];

            AddEdge(edgeCount, a, b);
            AddEdge(edgeCount, b, c);
            AddEdge(edgeCount, c, a);
        }

        var edgeVertices = new HashSet<int>();
        foreach (var edge in edgeCount)
        {
            if (edge.Value == 1)
            {
                edgeVertices.Add(edge.Key.Item1);
                edgeVertices.Add(edge.Key.Item2);
            }
        }

        return edgeVertices;
    }

    private static void AddEdge(Dictionary<(int, int), int> edges, int a, int b)
    {
        if (a > b) (a, b) = (b, a);
        edges.TryGetValue((a, b), out int count);
        edges[(a, b)] = count + 1;
    }

    public static void LaplacianSmooth(Mesh mesh, int iterations)
    {
        if (!mesh || iterations <= 0) return;

        var vertices = mesh.vertices;
        var triangles = mesh.triangles;
        var edgeVertices = FindEdgeVertices(triangles);

        List<int>[] neighbors = new List<int>[vertices.Length];
        for (int i = 0; i < neighbors.Length; i++)
            neighbors[i] = new List<int>();

        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            neighbors[a].Add(b); neighbors[a].Add(c);
            neighbors[b].Add(a); neighbors[b].Add(c);
            neighbors[c].Add(a); neighbors[c].Add(b);
        }

        var tempVertices = new Vector3[vertices.Length];

        for (int iter = 0; iter < iterations; iter++)
        {
            vertices.CopyTo(tempVertices, 0);

            for (int i = 0; i < vertices.Length; i++)
            {
                if (edgeVertices.Contains(i))
                    continue;

                var list = neighbors[i];
                if (list.Count == 0)
                    continue;

                Vector3 avg = Vector3.zero;
                for (int k = 0; k < list.Count; k++)
                    avg += tempVertices[list[k]];
                avg /= list.Count;

                vertices[i] = Vector3.Lerp(vertices[i], avg, 0.5f);
            }
        }

        mesh.vertices = vertices;
    }
    
    public static void InferGridDims(Mesh m, out int w, out int h)
    {
        var v = m.vertices;
        int count = v.Length;

        float z0 = v[0].z;
        int width = 1;
        const float eps = 1e-5f;

        for (int i = 1; i < count; i++)
        {
            if (Mathf.Abs(v[i].z - z0) > eps) break;
            width++;
        }

        if (width <= 1 || (count % width) != 0)
        {
            width = Mathf.RoundToInt(Mathf.Sqrt(count));
            width = Mathf.Max(2, width);
            while (width > 2 && (count % width) != 0) width--;
        }

        w = width;
        h = Mathf.Max(2, count / w);
    }
    
    public static int MergeVertices(ref List<Vector3> vertices, ref List<int> triangles, float threshold)
    {
        float inv = 1f / threshold;
        int oldCount = vertices.Count;

        int[] map = new int[oldCount];
        var list = new List<Vector3>(oldCount);
        var dict = new Dictionary<int, int>(oldCount);

        for (int i = 0; i < oldCount; i++)
        {
            Vector3 v = vertices[i];
            int key = HashVertex(v, inv);

            if (!dict.TryGetValue(key, out int idx))
            {
                idx = list.Count;
                list.Add(v);
                dict[key] = idx;
            }

            map[i] = idx;
        }

        for (int i = 0; i < triangles.Count; i++)
            triangles[i] = map[triangles[i]];

        vertices = list;

        int newCount = vertices.Count;
        int merged = oldCount - newCount;

        Debug.Log($"[MergeVertices] threshold={threshold} old={oldCount} new={newCount} merged={merged}");

        return merged;
    }

    private static int HashVertex(Vector3 v, float inv)
    {
        int x = Mathf.FloorToInt(v.x * inv);
        int y = Mathf.FloorToInt(v.y * inv);
        int z = Mathf.FloorToInt(v.z * inv);
        return (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);
    }
}
