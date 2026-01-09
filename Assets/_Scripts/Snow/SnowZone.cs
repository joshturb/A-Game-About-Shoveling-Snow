using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class SnowZone : MonoBehaviour
{
    public string ZoneName;

    [Header("Zone Volumes (ordered)")]
    [Tooltip("Multiple BoxColliders make up this zone. Order matters only if you use multiple zones and want priority.")]
    [SerializeField] private BoxCollider[] zoneColliders;

    [Header("Snow Field (optional)")]
    [SerializeField] private SnowField snowField;

    [Header("Progress")]
    [SerializeField, Min(0f)] private float clearedEpsilon = 0.001f;

    [Header("Gizmos")]
    [SerializeField] private bool drawZoneVertsGizmos = false;
    [SerializeField, Min(0.0001f)] private float gizmoSize = 0.03f;

    public float Percent { get; private set; } // 0..100
    public int TotalVertsInZone => _totalInZone;
    public int ClearedVertsInZone => _clearedInZone;

    private readonly List<int> _zoneVerts = new();
    private HashSet<int> _zoneSet;
    private bool[] _isCleared;

    private int _totalInZone;
    private int _clearedInZone;
    private float _clearedThreshold;
    private float _minSnowHeight;

    private void Start()
    {
        if (zoneColliders == null || zoneColliders.Length == 0)
        {
            Debug.LogError("[SnowZone] No zone colliders assigned.", this);
            enabled = false;
            return;
        }

        if (snowField == null)
            snowField = FindFirstObjectByType<SnowField>();

        if (snowField == null)
        {
            Debug.LogError("[SnowZone] No SnowField found in scene.", this);
            enabled = false;
            return;
        }

        snowField.OnInitialized += Initialize;
    }

    private void OnDestroy()
    {
        if (snowField != null)
        {
            snowField.OnInitialized -= Initialize;
            snowField.OnVertsEdited -= OnVertsEdited;
        }
    }

    private void Initialize()
    {
        var settings = SnowController.Instance.GetSnowSettings();
        _minSnowHeight = settings.minHeight;

        _clearedThreshold = _minSnowHeight + clearedEpsilon;

        BuildZoneMembership();
        BuildInitialClearedState();

        snowField.OnVertsEdited += OnVertsEdited;
        UpdatePercent();
    }

    [ContextMenu("Rebuild Zone")]
    public void RebuildZone()
    {
        if (snowField == null) return;
        BuildZoneMembership();
        BuildInitialClearedState();
        UpdatePercent();
    }

    private void BuildZoneMembership()
    {
        _zoneVerts.Clear();

        var verts = snowField.GetVerts();
        var snowTf = snowField.transform;

        // union of all colliders (duplicates avoided)
        var set = new HashSet<int>();

        for (int cIdx = 0; cIdx < zoneColliders.Length; cIdx++)
        {
            BoxCollider box = zoneColliders[cIdx];
            if (box == null) continue;

            Vector3 c = box.center;
            Vector3 he = box.size * 0.5f;
            Transform boxTf = box.transform;

            for (int i = 0; i < verts.Length; i++)
            {
                if (set.Contains(i)) continue;

                Vector3 wp = snowTf.TransformPoint(verts[i]);
                Vector3 lp = boxTf.InverseTransformPoint(wp);

                Vector3 d = lp - c;
                if (Mathf.Abs(d.x) <= he.x && Mathf.Abs(d.y) <= he.y && Mathf.Abs(d.z) <= he.z)
                    set.Add(i);
            }
        }

        _zoneVerts.AddRange(set);
        _zoneSet = set;
        _totalInZone = _zoneVerts.Count;
    }

    private void BuildInitialClearedState()
    {
        Vector3[] verts = snowField.GetVerts();

        if (_isCleared == null || _isCleared.Length != verts.Length)
            _isCleared = new bool[verts.Length];
        else
            Array.Clear(_isCleared, 0, _isCleared.Length);

        _clearedInZone = 0;

        for (int k = 0; k < _zoneVerts.Count; k++)
        {
            int vi = _zoneVerts[k];
            bool cleared = verts[vi].y <= _clearedThreshold;
            _isCleared[vi] = cleared;
            if (cleared) _clearedInZone++;
        }
    }

    private void OnVertsEdited(List<int> edited)
    {
        if (edited == null || edited.Count == 0) return;

        Vector3[] verts = snowField.GetVerts();

        for (int k = 0; k < edited.Count; k++)
        {
            int vi = edited[k];
            if (!_zoneSet.Contains(vi)) continue;

            bool was = _isCleared[vi];
            bool now = verts[vi].y <= _clearedThreshold;
            if (was == now) continue;

            _isCleared[vi] = now;
            _clearedInZone += now ? 1 : -1;
        }

        UpdatePercent();
    }

    private void UpdatePercent()
    {
        if (_totalInZone <= 0) { Percent = 0f; return; }
        Percent = Mathf.Clamp01(_clearedInZone / (float)_totalInZone) * 100f;
    }

    [ContextMenu("GetPercent")]
    public void GetPercent()
    {
        Debug.Log($"{ZoneName} = {Percent}");
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!drawZoneVertsGizmos) return;
        if (!Application.isPlaying) return;
        if (snowField == null) return;

        var verts = snowField.GetVerts();
        var snowTf = snowField.transform;

        for (int k = 0; k < _zoneVerts.Count; k++)
        {
            int vi = _zoneVerts[k];
            Vector3 wp = snowTf.TransformPoint(verts[vi]);

            // green if at minSnowHeight (or below), red otherwise
            Gizmos.color = (verts[vi].y <= _minSnowHeight + 1e-6f) ? Color.green : Color.red;
            Gizmos.DrawSphere(wp, gizmoSize);
        }
    }
#endif
}
