// SnowInteractor.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class SnowInteractor : MonoBehaviour
{
    [Header("Ray")]
    [SerializeField] private Camera cam;
    [SerializeField] private float maxDistance = 500f;

    [Header("Brush")]
    [SerializeField] private float radius = 0.5f;
    [SerializeField] private YEditMode mode = YEditMode.Add;
    [SerializeField] private float value = 0.05f;
    [SerializeField] private float smoothness = 0f;

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

            if (f.RaycastSnow(ray, out var h, maxDistance) && h.distanceWorld < bestDist)
            {
                bestDist = h.distanceWorld;
                best = f;
                bestHit = h;
            }
        }

        if (best == null) return;

        Vector3 localPoint = best.transform.InverseTransformPoint(bestHit.pointWorld);

        _hitVerts.Clear();
        best.QueryBrush(localPoint, radius, _hitVerts);
        best.EditY(_hitVerts, localPoint, radius, value, mode, smoothness);
    }
}
