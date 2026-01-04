using UnityEngine;
using UnityEngine.InputSystem;

public class SnowInteractor : MonoBehaviour
{
    [SerializeField] private Camera cam;
    [SerializeField] private LayerMask snowMask = ~0;

    [Header("Paint")]
    [SerializeField] private float paintRadius = 0.75f;
    [SerializeField] private float paintStrength = 1f;

    [Header("Plow")]
    [SerializeField] private float plowRadius = 1.0f;
    [SerializeField] private float plowMoveAmountPerTick = 0.25f;

    private void Awake()
    {
        if (cam == null) cam = Camera.main;
    }

    private void Update()
    {
        if (cam == null || Mouse.current == null)
            return;

        Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, 500f, snowMask, QueryTriggerInteraction.Ignore))
            return;

        if (!hit.collider.transform.root.TryGetComponent(out SnowField snowField))
            return;

        // LMB: add snow
        if (Mouse.current.leftButton.isPressed && !Keyboard.current.leftShiftKey.isPressed)
            SnowController.Instance.PaintDensity(snowField, hit.point, paintRadius, targetDensity: 1f, strength: paintStrength);

        // RMB: remove snow
        if (Mouse.current.rightButton.isPressed)
            SnowController.Instance.PaintDensity(snowField, hit.point, paintRadius, targetDensity: 0f, strength: paintStrength);
    }
}
