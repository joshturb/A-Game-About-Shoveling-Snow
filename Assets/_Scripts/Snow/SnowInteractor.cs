// SnowInteractor.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public enum YEditMode { Set, Add, Subtract }
public enum EditShape { Bounds, Sphere}

public abstract class SnowInteractor : MonoBehaviour
{
    public RuntimeAnimatorController overrideController;
    private SnowField snowField;

    [Header("Ray")]
    [SerializeField] private float distance = 5f;
    [SerializeField] private EditShape shape = EditShape.Bounds;
    [SerializeField] private YEditMode mode = YEditMode.Add;

    [Header("Sphere")]
    [SerializeField] private float sphereRadius = 0.5f;
    [Header("Bounds")]
    [SerializeField] private Collider boundsCollider;
    [SerializeField] private float value = 0.05f;
    [SerializeField] private float smoothness = 1f;

    private readonly List<int> _hitVerts = new(256);
    private readonly List<SnowField> _fields = new(64);

    public virtual void Awake()
    {
        snowField = FindFirstObjectByType<SnowField>();
    }

    public virtual void Edit()
    {
        if (Camera.main == null) return;
        if (Mouse.current == null) return;

        if (snowField == null)
            snowField = FindFirstObjectByType<SnowField>();

        if (snowField == null) return;

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());

        if (shape == EditShape.Sphere)
        {
            if (!snowField.RaycastSnow(ray, out var h, distance))
                return;

            Vector3 localPoint = snowField.transform.InverseTransformPoint(h.pointWorld);

            _hitVerts.Clear();
            snowField.QuerySphere(localPoint, sphereRadius, _hitVerts);
            snowField.EditY(_hitVerts, localPoint, sphereRadius, value, mode, smoothness);
            return;
        }

        if (boundsCollider == null) return;

        Vector3 worldCenter = boundsCollider.bounds.center;
        Vector3 localCenter = snowField.transform.InverseTransformPoint(worldCenter);

        _hitVerts.Clear();
        var b = boundsCollider.bounds;
        snowField.QueryBounds(b, _hitVerts);
        float boundsRadius = Mathf.Max(b.extents.x, b.extents.z);
        snowField.EditY(_hitVerts, localCenter, boundsRadius, value, mode, smoothness);
    }
}
