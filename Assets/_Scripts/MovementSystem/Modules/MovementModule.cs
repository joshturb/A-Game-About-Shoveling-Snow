using UnityEngine;

[CreateAssetMenu(menuName = "Player Movement/Movement Module")]
public class MovementModule : PlayerModule
{
	public float walkingSpeed = 2f;
	public float sprintingSpeed = 5f;
	public float walkingStaminaMultiplier = 0f;
	public float sprintingStaminaMultiplier = 2f;
	[Header("Snow Movement")]
	public float feetYOffset = 0f;                 // adjust if your transform is not at feet
	public float slowDepth = 0.8f;                 // depth where you reach min multiplier
	public float minSnowSpeedMultiplier = 0.1f;    // deep snow speed (10%)
	public float blockDepth = 1.2f;                // cannot move if depth >= this
	public float blockProbeForward = 1;
	public float blockProbeSide = 0.35f; // set to your controller radius (or slightly larger)

	public bool blockInDeepSnow = true;

	// new: higher = faster interpolation (instant when very large)
	public float speedLerp = 10f;

	private float currentSpeed = 0f;

	private Vector2 input;
	private bool isSprinting;
	private bool sprintToggleState = false;
	public bool toggleSprint = false;

	public bool isLocked;
	public override bool IsLocked { get => isLocked; set => isLocked = value; }

	private bool isInitialized;
	public override bool IsInitialized { get => isInitialized; set => isInitialized = value; }

	public override void InitializeModule(FPCModule fPCModule)
	{
		IsLocked = false;
	}

	public override void OnModuleRemoved(FPCModule fPCModule)
	{
		// nothing to restore (crouch logic removed)
	}

	public override void HandleInput(FPCModule fPCModule)
	{
		input = InputHandler.Instance.playerActions.Move.ReadValue<Vector2>().normalized;

		bool sprintInput = InputHandler.Instance.playerActions.Sprint.IsPressed();

		if (toggleSprint)
		{
			if (sprintInput && !sprintToggleState)
			{
				isSprinting = !isSprinting;
				sprintToggleState = true;
			}
			else if (!sprintInput)
			{
				sprintToggleState = false;
			}
		}
		else isSprinting = sprintInput && fPCModule.currentStamina > 5;

		if (input == Vector2.zero)
		{
			fPCModule.staminaReductionRate = 0;
			return;
		}

		if (isSprinting)
		{
			fPCModule.staminaReductionRate = sprintingStaminaMultiplier;
		}
		else
		{
			fPCModule.staminaReductionRate = walkingStaminaMultiplier;
		}
	}

	public override void UpdateModule(FPCModule fPCModule)
	{
		Vector3 desired = input.y * fPCModule.transform.forward + input.x * fPCModule.transform.right;
		desired.y = 0f;

		float baseSpeed =
			(input != Vector2.zero && ((isSprinting && fPCModule.currentStamina > 5f) || !isSprinting))
				? ((isSprinting && fPCModule.currentStamina > 5f) ? sprintingSpeed : walkingSpeed)
				: 0f;

		float targetSpeed = baseSpeed;

		if (baseSpeed > 0f && SnowField.Instance != null && desired.sqrMagnitude > 1e-8f)
		{
			Vector3 moveDir = desired.normalized;

			// ----- block check: probe ahead with width (center + left/right) -----
			if (blockInDeepSnow)
			{
				Vector3 right = Vector3.Cross(Vector3.up, moveDir);
				if (right.sqrMagnitude > 1e-8f) right.Normalize();

				Vector3 basePos = fPCModule.transform.position + moveDir * blockProbeForward;

				bool blocked =
					IsBlockedAt(basePos) ||
					IsBlockedAt(basePos + right * blockProbeSide) ||
					IsBlockedAt(basePos - right * blockProbeSide);

				if (blocked)
					targetSpeed = 0f; // blocks ONLY this attempted direction; backing out works (different moveDir)
			}

			// ----- slowdown by depth at current position (only if not blocked) -----
			if (targetSpeed > 0f && SnowField.Instance.TryGetSnowHeightWorld(fPCModule.transform.position, out float snowYHere))
			{
				float feetYHere = fPCModule.transform.position.y + feetYOffset;
				float depthHere = Mathf.Max(0f, snowYHere - feetYHere);

				float t = Mathf.Clamp01(depthHere / Mathf.Max(1e-6f, slowDepth));
				float snowMult = Mathf.Lerp(1f, minSnowSpeedMultiplier, t);
				targetSpeed = baseSpeed * snowMult;
			}
		}

		currentSpeed = Mathf.Lerp(currentSpeed, targetSpeed, speedLerp * Time.deltaTime);

		Vector3 movement = desired.sqrMagnitude > 1e-8f ? desired.normalized * currentSpeed : Vector3.zero;
		fPCModule.movement = new Vector3(movement.x, fPCModule.movement.y, movement.z);

		// local helper
		bool IsBlockedAt(Vector3 probeWorldPos)
		{
			if (!SnowField.Instance.TryGetSnowHeightWorld(probeWorldPos, out float snowY))
				return false;

			float feetY = probeWorldPos.y + feetYOffset;
			float depth = Mathf.Max(0f, snowY - feetY);
			return depth >= blockDepth;
		}
	}

	public bool IsMovingForward
	{
		get { return input.y > 0; }
	}

	public bool IsMoving
	{
		get { return input != Vector2.zero; }
	}

	public bool IsWalking
	{
		get { return input != Vector2.zero && !isSprinting; }
	}

	public bool IsSprinting
	{
		get { return isSprinting; }
	}
}
