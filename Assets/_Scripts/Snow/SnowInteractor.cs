// SnowInteractor.cs
using UnityEngine;
using UnityEngine.InputSystem;

public class SnowInteractor : MonoBehaviour
{
    [Header("Aim")]
    [SerializeField] private Camera aimCamera;
    [SerializeField] private LayerMask aimLayerMask = ~0;
    [SerializeField, Min(0.5f)] private float maxDistance = 20f;

    [Header("Dig (LMB)")]
    [SerializeField] private bool holdToDig = true;
    [SerializeField, Min(0.01f)] private float digRadius = 0.35f;
    [SerializeField, Range(0f, 0.99f)] private float digInnerFullClear01 = 0.75f;
    [SerializeField, Min(0.01f)] private float digClearRate = 3.0f; // depth/sec

    [Header("Plow (Shift + Move)")]
    [SerializeField] private float plowHalfWidth = 0.35f;
    [SerializeField] private float plowLength = 0.9f;
    [SerializeField] private float plowMovePerSecond = 0.35f;
    [SerializeField] private float depositForwardDistance = 0.6f;
    [SerializeField, Range(0f, 0.5f)] private float sidewaysSpill = 0.15f;

    private SnowField[] fields;
    private Vector3 lastPos;
    private bool hasLast;

    private void Reset()
    {
        aimCamera = Camera.main;
    }

    private void Start()
    {
        fields = FindObjectsByType<SnowField>(FindObjectsSortMode.None);
    }

    private void Update()
    {
        var mouse = Mouse.current;
        var kb = Keyboard.current;
        if (mouse == null || kb == null || aimCamera == null) return;

        // --- Dig ---
        bool digActive = holdToDig ? mouse.leftButton.isPressed : mouse.leftButton.wasPressedThisFrame;
        if (digActive && TryGetAimPoint(out Vector3 hit))
        {
            if (TryGetFieldAt(hit, out SnowField field))
            {
                field.RemoveToZeroStamp(hit, digRadius, digInnerFullClear01, digClearRate * Time.deltaTime);
            }
        }

        // --- Plow ---
        bool plowing = kb.leftShiftKey.isPressed;
        if (plowing)
        {
            Vector3 pos = transform.position;
            if (hasLast)
            {
                Vector3 delta = pos - lastPos;
                delta.y = 0f;

                if (delta.sqrMagnitude > 0.0004f)
                {
                    Vector3 dir = delta.normalized;

                    // Use player's current position to pick the field
                    if (TryGetFieldAt(pos, out SnowField field))
                    {
                        field.Plow(pos, dir, plowHalfWidth, plowLength, plowMovePerSecond * Time.deltaTime, depositForwardDistance, sidewaysSpill);
                    }
                }
            }

            lastPos = pos;
            hasLast = true;
        }
        else
        {
            hasLast = false;
        }
    }

    private bool TryGetAimPoint(out Vector3 worldPoint)
    {
        worldPoint = default;
        Ray ray = aimCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (Physics.Raycast(ray, out var hit, maxDistance, aimLayerMask, QueryTriggerInteraction.Ignore))
        {
            worldPoint = hit.point;
            return true;
        }
        return false;
    }

    private bool TryGetFieldAt(Vector3 worldPos, out SnowField field)
    {
        field = null;
        if (fields == null || fields.Length == 0) return false;

        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i] != null && fields[i].ContainsWorld(worldPos))
            {
                field = fields[i];
                return true;
            }
        }
        return false;
    }
}
