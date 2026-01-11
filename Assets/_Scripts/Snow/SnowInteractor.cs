// SnowInteractor.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public enum YEditMode { Set, Add, Subtract }
public enum EditShape { Bounds, Sphere }
public enum EditType { Snow, Ice, Both }

public abstract class SnowInteractor : MonoBehaviour
{
    public RuntimeAnimatorController overrideController;
    private SnowField snowField;

    [Header("Ray")]
    [SerializeField] private float distance = 5f;
    [SerializeField] private EditShape shape = EditShape.Bounds;
    [SerializeField] private YEditMode mode = YEditMode.Add;
    [SerializeField] private EditType type = EditType.Snow;

    [Header("Sphere")]
    [SerializeField] private float sphereRadius = 0.5f;

    [Header("Bounds")]
    [SerializeField] private Collider boundsCollider;

    [SerializeField] private float value = 0.05f;
    [SerializeField] private float plowValueMultiplier = 0.3f;
    [SerializeField] private float smoothness = 1f;
    [SerializeField, Min(1f)] private float depositAreaMultiplier = 3f;
    [SerializeField, Min(0f)] private float depositForwardOffset = 0.75f;

    [SerializeField] private int _changedCount;

    public int changedCount
    {
        get => _changedCount;
        set
        {
            _changedCount = value;
            Inventory.AddSnow(value);
        }
    }

    private readonly List<int> _depositVerts = new(512);
    public readonly List<int> _hitVerts = new(256);

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
            changedCount = snowField.EditY(_hitVerts, localPoint, sphereRadius, value, mode, smoothness, type);
            return;
        }

        if (boundsCollider == null) return;

        Vector3 worldCenter = boundsCollider.bounds.center;
        Vector3 localCenter = snowField.transform.InverseTransformPoint(worldCenter);

        _hitVerts.Clear(); 
        var b = boundsCollider.bounds;
        snowField.QueryBounds(b, _hitVerts);

        float boundsRadius = Mathf.Max(b.extents.x, b.extents.z);
        changedCount = snowField.EditY(_hitVerts, localCenter, boundsRadius, value, mode, smoothness, type);
    }

    public virtual void Plow()
    {
        if (snowField == null) snowField = FindFirstObjectByType<SnowField>();
        if (snowField == null) return;
        if (boundsCollider == null) return;

        Bounds b = boundsCollider.bounds;

        // REMOVE set (tight)
        Vector3 removeWorldCenter = b.center;
        Vector3 removeLocalCenter = snowField.transform.InverseTransformPoint(removeWorldCenter);

        _hitVerts.Clear();
        snowField.QueryBounds(b, _hitVerts);

        float removeRadius = Mathf.Max(b.extents.x, b.extents.z);

        // DEPOSIT set (bigger + shifted forward)
        Vector3 fwdW = boundsCollider.transform.forward;
        Vector3 depositWorldCenter = removeWorldCenter + fwdW * (removeRadius * depositForwardOffset);

        Vector3 depExt = new Vector3(b.extents.x * depositAreaMultiplier, b.extents.y, b.extents.z * depositAreaMultiplier);
        Bounds depositBounds = new Bounds(depositWorldCenter, depExt * 2f);

        _depositVerts.Clear();
        snowField.QueryBounds(depositBounds, _depositVerts);

        Vector3 depositLocalCenter = snowField.transform.InverseTransformPoint(depositWorldCenter);
        Vector3 localForward = snowField.transform.InverseTransformDirection(fwdW);
        localForward = -localForward; // keep your current direction fix

        float depositRadius = Mathf.Max(depExt.x, depExt.z);

        snowField.Plow(
            _hitVerts,
            _depositVerts,
            removeLocalCenter,
            depositLocalCenter,
            localForward,
            removeRadius,
            depositRadius,
            value * plowValueMultiplier,
            smoothness,
            type);
    }
}
