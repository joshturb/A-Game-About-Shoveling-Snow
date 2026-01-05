using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class SnowInteractor : MonoBehaviour
{
    [Header("Raycast (Custom Snow Collider)")]
    [SerializeField] private Camera cam;
    [SerializeField] private float maxDistance = 500f;

    [Header("Edit")]
    [SerializeField] private YEditMode mode;
    [SerializeField] private float value;
    [SerializeField] private float smoothness;

    [Header("Query")]
    [SerializeField] private float radius = 0.5f;

    private readonly List<int> _hitVerts = new(256);
    private readonly List<SnowField> _fields = new(64);

    void Awake()
    {
        if (cam == null) cam = Camera.main;
        RefreshFields();
    }

    void OnEnable() => RefreshFields();

    private void RefreshFields()
    {
        _fields.Clear();
        var found = Object.FindObjectsByType<SnowField>(FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
            if (found[i] != null) _fields.Add(found[i]);
    }

    void Update()
    {
        if (cam == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;

        Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());

        SnowField best = null;
        SnowHeightfieldCollider.Hit bestHit = default;
        float bestDist = float.PositiveInfinity;

        for (int i = 0; i < _fields.Count; i++)
        {
            var f = _fields[i];
            if (f == null) continue;

            if (f.RaycastSnow(ray, out var h, maxDistance))
            {
                if (h.distanceWorld < bestDist)
                {
                    bestDist = h.distanceWorld;
                    best = f;
                    bestHit = h;
                }
            }
        }

        if (best == null || best._tree == null)
            return;

        Vector3 localPoint = best.transform.InverseTransformPoint(bestHit.pointWorld);

        _hitVerts.Clear();
        best._tree.QuerySphere(localPoint, radius, _hitVerts);
        best.EditY(_hitVerts, value, mode, smoothness);
    }
}
