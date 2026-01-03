using UnityEngine;

[CreateAssetMenu(menuName = "Player Movement/Crouch Module")]
public class CrouchModule : PlayerModule
{
	public float crouchingSpeed = 1f;
	public float crouchingStaminaMultiplier = 1f;
	public float crouchingHeightMultiplier = 0.5f;
	public float crouchingCameraLerpSpeed = 10f;
	public Vector3 originalCameraPosition;
	public Vector3 crouchingCameraPosition;
	public LayerMask standingBlockLayers = ~0; // By default, all layers block standing

	public bool isCrouching;
	private Vector2 input;
	private bool crouchToggleState = false;
	public bool toggleCrouch = false;
	private bool wantsToUncrouch = false;

	public bool isLocked;
	public override bool IsLocked { get => isLocked; set => isLocked = value; }

	private bool isInitialized;
	public override bool IsInitialized { get => isInitialized; set => isInitialized = value; }

	private float originalHeight;
	private Vector3 originalCenter;
	private CapsuleCollider capsuleCollider;
	private CharacterController characterController;
	private FPCModule fPCModule;

	public override void InitializeModule(FPCModule fPCModule)
	{
		this.fPCModule = fPCModule;
		IsLocked = false;
		capsuleCollider = fPCModule.GetComponent<CapsuleCollider>();
		characterController = fPCModule.GetComponent<CharacterController>();

		if (capsuleCollider != null)
		{
			originalHeight = capsuleCollider.height;
			originalCenter = capsuleCollider.center;
		}
		else if (characterController != null)
		{
			originalHeight = characterController.height;
			originalCenter = characterController.center;
		}
	}

	public override void OnModuleRemoved(FPCModule fPCModule)
	{
		if (capsuleCollider != null)
		{
			capsuleCollider.height = originalHeight;
			capsuleCollider.center = originalCenter;
		}
		else if (characterController != null)
		{
			characterController.height = originalHeight;
			characterController.center = originalCenter;
		}
	}

	public override void HandleInput(FPCModule fPCModule)
	{
		bool crouchInput = InputHandler.Instance.playerActions.Crouch.IsPressed();

		if (toggleCrouch)
		{
			if (crouchInput && !crouchToggleState)
			{
				if (isCrouching)
				{
					wantsToUncrouch = true;
				}
				else
				{
					isCrouching = true;
				}
				crouchToggleState = true;
			}
			else if (!crouchInput)
			{
				crouchToggleState = false;
			}
		}
		else
		{
			if (isCrouching && !crouchInput)
			{
				wantsToUncrouch = true;
			}
			else
			{
				isCrouching = crouchInput;
			}
		}

		if (input == Vector2.zero)
		{
			fPCModule.staminaReductionRate = 0;
			return;
		}

		if (isCrouching)
		{
			fPCModule.staminaReductionRate = crouchingStaminaMultiplier;
		}
	}

	public override void UpdateModule(FPCModule fPCModule)
	{
		if (isCrouching)
		{
			float y = fPCModule.movement.y;
			fPCModule.movement = new(fPCModule.movement.x * crouchingSpeed, y, fPCModule.movement.z * crouchingSpeed);
		}

		// Check if player wants to uncrouch and can stand
		if (wantsToUncrouch && CanStand())
		{
			isCrouching = false;
			wantsToUncrouch = false;
		}

		if (capsuleCollider != null)
		{
			if (isCrouching)
			{
				capsuleCollider.height = originalHeight * crouchingHeightMultiplier;
				capsuleCollider.center = new Vector3(originalCenter.x, originalCenter.y * crouchingHeightMultiplier, originalCenter.z);
			}
			else
			{
				capsuleCollider.height = originalHeight;
				capsuleCollider.center = originalCenter;
			}
		}
		if (characterController != null)
		{
			if (isCrouching)
			{
				characterController.height = originalHeight * crouchingHeightMultiplier;
				characterController.center = new Vector3(originalCenter.x, originalCenter.y * crouchingHeightMultiplier, originalCenter.z);
			}
			else
			{
				characterController.height = originalHeight;
				characterController.center = originalCenter;
			}
		}
	}

	private bool CanStand()
	{
		Vector3 start = fPCModule.transform.position;
		float rayDistance = originalHeight - (originalHeight * crouchingHeightMultiplier);

		// Offset the start position to be at the top of the crouched collider
		if (capsuleCollider != null)
		{
			start.y += capsuleCollider.height / 2;
		}
		else if (characterController != null)
		{
			start.y += characterController.height / 2;
		}

		// Visualize the ray for debugging
		Debug.DrawRay(start, Vector3.up * rayDistance, Color.red, 0.1f);

		// Check if there's anything above the player's head
		if (Physics.Raycast(start, Vector3.up, rayDistance, standingBlockLayers))
		{
			return false;
		}

		return true;
	}

	public void SetCrouchState(bool state)
	{
		if (state)
		{
			// Force crouch
			isCrouching = true;
		}
		else
		{
			// Only uncrouch if we have clearance
			if (CanStand())
			{
				isCrouching = false;
			}
			else
			{
				// Can't uncrouch, maintain crouched state
				isCrouching = true;
			}
		}

		wantsToUncrouch = false;
		crouchToggleState = false;
	}
}