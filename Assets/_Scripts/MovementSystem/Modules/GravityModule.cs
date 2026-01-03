using UnityEngine;

[CreateAssetMenu(menuName = "Player Movement/Gravity Module")]
public class GravityModule : PlayerModule
{
    public LayerMask groundMask;
    public float detectionLength = 0.5f;
    public float gravityMultiplier = 1f;
    private float gravity;

    public bool isLocked;
    public override bool IsLocked { get => isLocked; set => isLocked = value; }

    private bool _isInitialized;

    public override bool IsInitialized { get => _isInitialized; set => _isInitialized = value; }

    public override void InitializeModule(FPCModule fPCModule)
    {
        IsLocked = false;
        gravity = Physics.gravity.y * gravityMultiplier;
    }

    public override void OnModuleRemoved(FPCModule fPCModule) {	}

    public override void HandleInput(FPCModule fPCModule)
    {
        fPCModule.isGrounded = GroundedCheck(fPCModule);
    }

    public bool GroundedCheck(FPCModule fPCModule)
    {
        // offset the sphere slightly downward so it checks at the feet instead of overlapping the ground
        Vector3 origin = fPCModule.transform.position + Vector3.down * 0.1f;
        return Physics.CheckSphere(origin, detectionLength, groundMask, QueryTriggerInteraction.Ignore);
    }

    public override void UpdateModule(FPCModule fPCModule)
    {
        if (fPCModule.isGrounded)
        {
            // If we're grounded and moving downward, keep a small downward snap to stay pinned to the ground
            // but don't cancel upward velocity (so jumps aren't immediately wiped out if ground detection lags).
            if (fPCModule.movement.y < 0f)
            {
                fPCModule.movement.y = -2f;
            }
        }
        else
        {
            // Apply gravity while airborne and clamp to terminal velocity (gravity value).
            fPCModule.movement.y += gravity * Time.deltaTime;
            fPCModule.movement.y = Mathf.Max(fPCModule.movement.y, gravity);
        }
    }
}
