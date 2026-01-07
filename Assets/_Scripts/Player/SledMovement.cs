using UnityEngine;
using static SnowHeightfieldCollider;

// Raycast to snow; if no snow hit, fall back to Physics raycast hit.
// Move sled toward the look point on XZ only, with a stop radius + heavy (PD) feel.
public class SledMovement : MonoBehaviour, IHoldInteractable
{
    [Header("Targeting")]
    [SerializeField, Min(0.01f)] private float distance = 3f;              // ray length
    [SerializeField, Min(0f)]    private float stopRadius = 0.5f;           // within this, stop
    [SerializeField, Min(0f)]    private float stopHysteresis = 0.15f;      // prevents edge jitter
    [SerializeField, Min(0f)]    private float targetSmoothing = 12f;       // filters soft-body jitter

    [Header("Heavy Movement (XZ only)")]
    [SerializeField, Min(0.01f)] private float maxSpeed = 3f;              // top speed on XZ
    [SerializeField, Min(0f)]    private float drive = 12f;                // velocity gain
    [SerializeField, Min(0f)]    private float damping = 8f;               // braking / damping

    private Rigidbody rb;
    private bool isHolding;

    private SnowHeightfieldCollider _collider;

    private Vector3 targetXZ;      // smoothed target at rb.position.y
    private bool inStopZone;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        _collider = FindFirstObjectByType<SnowField>(FindObjectsInactive.Exclude)._hf;

        targetXZ = rb.position;
        inStopZone = true;
    }

    public void OnHoldStart(RaycastHit hit)
    {
        isHolding = true;
        targetXZ = rb.position;
        inStopZone = true;
    }

    public void OnHoldEnd()
    {
        isHolding = false;
        inStopZone = true;
    }

    public void OnHoldUpdate(Interaction interaction)
    {
        // Look-ray from camera forward (screen center style).
        Ray ray = new(interaction.playerCamera.transform.position, interaction.playerCamera.transform.forward);

        Vector3 targetWorld;

        // Prefer snow hit if available.
        if (_collider != null && _collider.Raycast(ray, out Hit snowHit, distance))
        {
            targetWorld = snowHit.pointWorld;
        }
        else
        {
            // Fallback to whatever Interaction is currently pointing at (ensure Interaction keeps this updated while holding).
            targetWorld = interaction.raycastHitResults.collider != null && interaction.raycastHitResults.transform != transform
                ? interaction.raycastHitResults.point
                : (interaction.playerCamera.transform.position + interaction.playerCamera.transform.forward * distance);
        }

        // Lock to XZ at sled height (prevents vertical wobble from deforming snow).
        Vector3 desiredTargetXZ = new(targetWorld.x, rb.position.y, targetWorld.z);

        // Smooth the target to reduce jitter from soft/deforming surfaces.
        float a = 1f - Mathf.Exp(-targetSmoothing * Time.deltaTime);
        targetXZ = Vector3.Lerp(targetXZ, desiredTargetXZ, a);
    }

    void FixedUpdate()
    {
        if (!isHolding)
        {
            return;
        }

        Vector3 toTarget = targetXZ - rb.position;
        toTarget.y = 0f;

        float dist = toTarget.magnitude;

        // Dead-zone with hysteresis to prevent oscillation at the boundary.
        float resumeRadius = stopRadius + stopHysteresis;
        if (inStopZone)
            inStopZone = dist <= resumeRadius;
        else
            inStopZone = dist <= stopRadius;

        Vector3 vel = rb.linearVelocity;
        Vector3 velXZ = new(vel.x, 0f, vel.z);

        // Desired XZ velocity: slows as you get close, so it doesn't overshoot.
        Vector3 desiredVelXZ = inStopZone
            ? Vector3.zero
            : toTarget.normalized * Mathf.Min(maxSpeed, dist * maxSpeed);

        // PD-ish controller on velocity for a "heavy" feel.
        Vector3 velError = desiredVelXZ - velXZ;
        Vector3 forceXZ = (velError * drive - velXZ * damping) * rb.mass;

        rb.AddForce(forceXZ, ForceMode.Force);
    }
}
