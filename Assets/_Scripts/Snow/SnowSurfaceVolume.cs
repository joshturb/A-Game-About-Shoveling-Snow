// SnowSurfaceVolumeHeight.cs
// Attach to a GameObject with a BoxCollider.
// Height-only, fast. Uses SnowField.QueryBounds (octree).
// - makeFlat = true  => forces uniform thickness (removes noise texture).
// - makeFlat = false => caps thickness to height (keeps noise texture below).
// - lerp 0..1        => smooth blend (1 = instant).

using System;
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(BoxCollider))]
public sealed class SnowSurfaceVolumeHeight : MonoBehaviour
{
    [Header("Height")]
    [Tooltip("Target snow thickness above spline baseline.")]
    [Min(0f)] public float height = 0.5f;

    [Tooltip("If true: force uniform thickness (flat). If false: cap thickness (cut off).")]
    public bool makeFlat = true;

    [Tooltip("0..1. 1 = instant set, <1 = smooth blend.")]
    [Range(0f, 1f)] public float lerp = 1f;

    [Header("Apply")]
    public bool applyOnEnable = true;
    public bool applyInPlayMode = true;
    public bool applyInEditMode = true;

    private BoxCollider _box;

    // Reused buffers (no GC)
    private readonly List<int> _hits = new(2048);
    private readonly List<int> _unique = new(2048);

    private float _cachedMinH = float.NaN;

    private void OnEnable()
    {
        _box = GetComponent<BoxCollider>();

        if (!applyOnEnable) return;

        var field = SnowField.Instance;
        if (field != null) field.OnInitialized += Apply;
    }

    private void OnDisable()
    {
        var field = SnowField.Instance;
        if (field != null) field.OnInitialized -= Apply;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        _box ??= GetComponent<BoxCollider>();
        Apply();
    }
#endif

    [ContextMenu("Apply")]
    public void Apply()
    {
        _box ??= GetComponent<BoxCollider>();

        bool playing = Application.isPlaying;
        if (playing && !applyInPlayMode) return;
        if (!playing && !applyInEditMode) return;

        var field = SnowField.Instance;
        if (field == null) return;

        var mf = field.GetComponent<MeshFilter>();
        if (mf == null) return;

        Mesh mesh = playing ? mf.mesh : mf.sharedMesh;
        if (mesh == null) return;

        Vector3[] verts = field.GetVerts();
        if (verts == null || verts.Length == 0) return;

        float minH = GetMinH();

        Bounds wb = _box.bounds;

        // Broadphase candidates via octree (fast)
        _hits.Clear();
        field.QueryBounds(wb, _hits);
        if (_hits.Count == 0) return;

        // Dedup by sort (fast, low overhead)
        _hits.Sort();
        _unique.Clear();
        int last = int.MinValue;
        for (int k = 0; k < _hits.Count; k++)
        {
            int i = _hits[k];
            if (i == last) continue;
            last = i;
            _unique.Add(i);
        }

        Transform fieldTr = field.transform;
        Bounds localBounds = WorldBoundsToLocalAABB(fieldTr, wb);

        bool changed = false;
        float tLerp = lerp;

        for (int idx = 0; idx < _unique.Count; idx++)
        {
            int i = _unique[idx];
            if ((uint)i >= (uint)verts.Length) continue;

            // Local AABB test (cheaper than world Bounds.Contains per vertex)
            Vector3 v = verts[i];
            if (!localBounds.Contains(v)) continue;

            float baseY = field.GetBaseY(i);
            float minY = baseY + minH;

            float targetY;
            if (makeFlat)
            {
                // flatten: set thickness exactly to 'height'
                targetY = baseY + height;
                if (targetY < minY) targetY = minY;
            }
            else
            {
                // cut off: keep current thickness, but cap at 'height'
                float currentThickness = v.y - baseY;
                float cappedThickness = Mathf.Min(currentThickness, height);
                if (cappedThickness < minH) cappedThickness = minH;
                targetY = baseY + cappedThickness;
            }

            float newY = (tLerp >= 0.999999f) ? targetY : Mathf.Lerp(v.y, targetY, tLerp);

            if (Mathf.Abs(newY - v.y) > 1e-6f)
            {
                v.y = newY;
                verts[i] = v;
                changed = true;
            }
        }

        if (!changed) return;

        mesh.vertices = verts;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        if (field._hf != null)
            field._hf.Refresh(verts);
    }

    private float GetMinH()
    {
        if (!float.IsNaN(_cachedMinH)) return _cachedMinH;

        try { _cachedMinH = SnowController.Instance.GetSnowSettings().minHeight; }
        catch { _cachedMinH = 0f; }

        return _cachedMinH;
    }

    private static Bounds WorldBoundsToLocalAABB(Transform fieldTr, Bounds worldBounds)
    {
        Vector3 c = worldBounds.center;
        Vector3 e = worldBounds.extents;

        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (int xi = -1; xi <= 1; xi += 2)
        for (int yi = -1; yi <= 1; yi += 2)
        for (int zi = -1; zi <= 1; zi += 2)
        {
            Vector3 wc = new Vector3(c.x + e.x * xi, c.y + e.y * yi, c.z + e.z * zi);
            Vector3 lc = fieldTr.InverseTransformPoint(wc);

            min = Vector3.Min(min, lc);
            max = Vector3.Max(max, lc);
        }

        return new Bounds((min + max) * 0.5f, max - min);
    }
}
