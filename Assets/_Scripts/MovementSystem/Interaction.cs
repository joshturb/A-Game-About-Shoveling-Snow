using UnityEngine;
using UnityEngine.UI;
using System;

public class Interaction : MonoBehaviour
{
    [SerializeField] private float interactDelay = 0.5f;
    [SerializeField] private float interactDistance = 5f;
    [SerializeField] private int delayedInteractionLayer = 7;
    [SerializeField] private float delayedInteractionTime = 0.5f;
    [SerializeField] private LayerMask layerMask;
    public Slider interactionSlider;
    public bool canInteract = true;
	public RaycastHit raycastHitResults;
    public event Action<RaycastHit> OnInteract;
	public RectTransform crosshair;
	public bool IsHovering;
    [HideInInspector] public Camera playerCamera;
    private float interactTimer = 0f;
    private bool isInteractingWithDelayedObject = false;
    private bool hasInteractedDelayed = false;
    private IHoldInteractable currentHeldInteractable; // NEW

    public void Awake()
    {
        playerCamera = Camera.main;

		if (interactionSlider != null)
        	interactionSlider.maxValue = delayedInteractionTime * 0.95f;
    }
	
	private void Update()
	{
		if (playerCamera.enabled == false)
			return;
			
		// Always check for release first
		if (InputHandler.Instance.playerActions.Interact.WasReleasedThisFrame())
		{
			if (currentHeldInteractable != null)
			{
				currentHeldInteractable.OnHoldEnd();
				currentHeldInteractable = null;
			}
			ResetInteractionState();
			return;
		}
		
		// If a hold interactable is already active, update it without checking for a new one
		if (currentHeldInteractable != null)
		{
			currentHeldInteractable.OnHoldUpdate(this);
		}
		
		// Otherwise, perform raycast for a new hold interactable or delayed interaction
		Ray cameraToMouseRay = playerCamera.ScreenPointToRay(Input.mousePosition);
		if (!Physics.Raycast(cameraToMouseRay, out raycastHitResults, interactDistance, layerMask))
		{
			ResetDelayedInteractionState();
			IsHovering = false;
			return;
		}

		UpdateHover();

		if (InputHandler.Instance.playerActions.Interact.WasPressedThisFrame())
		{
			IHoldInteractable holdInteractable = null;
			if (raycastHitResults.collider != null)
			{
				raycastHitResults.collider.TryGetComponent(out holdInteractable);
				holdInteractable ??= raycastHitResults.collider.GetComponentInParent<IHoldInteractable>();
				holdInteractable ??= raycastHitResults.collider.GetComponentInChildren<IHoldInteractable>();
			}
			if (holdInteractable != null && currentHeldInteractable == null)
			{
				currentHeldInteractable = holdInteractable;
				currentHeldInteractable.OnHoldStart(raycastHitResults);
			}
			else
			{
				PerformRaycast(true);
			}
		}
		else if (InputHandler.Instance.playerActions.Interact.IsPressed())
		{
			if (raycastHitResults.collider.gameObject.layer == delayedInteractionLayer)
				HandleDelayedInteraction(raycastHitResults);
		}
	}

	private void UpdateHover()
	{
		if (raycastHitResults.collider.TryGetComponent(out IInteractable _) ||
			raycastHitResults.collider.GetComponentInParent<IInteractable>() != null ||
			raycastHitResults.collider.GetComponentInChildren<IInteractable>() != null ||
			// added check for IHoldInteractable
			raycastHitResults.collider.TryGetComponent(out IHoverText _) ||
			raycastHitResults.collider.GetComponentInParent<IHoverText>() != null ||
			raycastHitResults.collider.GetComponentInChildren<IHoverText>() != null)
		{
			IsHovering = true;
		}
		else
		{
			IsHovering = false;
		}
	}

	private void PerformRaycast(bool instantInteraction)
    {
        if (!canInteract) 
            return;
            
		int objectLayer = raycastHitResults.collider.gameObject.layer;

		if (instantInteraction && objectLayer != delayedInteractionLayer && Time.time - interactTimer >= interactDelay)
		{
			DetermineInteract(raycastHitResults);
		}
		else if (!instantInteraction && objectLayer == delayedInteractionLayer)
		{
			HandleDelayedInteraction(raycastHitResults);
		}
		else
		{
			ResetDelayedInteractionState();
		}
    }

	private void DetermineInteract(RaycastHit raycastHitResults)
	{
		// Check if not typing in an input field
		bool inputFieldHasFocus = false;
		var eventSystem = UnityEngine.EventSystems.EventSystem.current;
		var selectedObject = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
		
		if (selectedObject != null)
		{
			inputFieldHasFocus = selectedObject.GetComponent<InputField>() != null;
			inputFieldHasFocus |= selectedObject.GetComponent<TMPro.TMP_InputField>() != null;
		}
		
		// Only proceed with interaction if no input field is active
		if (inputFieldHasFocus)
			return;

		if (raycastHitResults.collider.TryGetComponent(out IInteractable interactable) ||
			raycastHitResults.collider.GetComponentInParent<IInteractable>() != null ||
			raycastHitResults.collider.GetComponentInChildren<IInteractable>() != null)
		{
			// If not found directly on the collider, check parent and children
			interactable ??= raycastHitResults.collider.GetComponentInParent<IInteractable>();
			interactable ??= raycastHitResults.collider.GetComponentInChildren<IInteractable>();

			interactable?.Interact(raycastHitResults, transform);
		}

		OnInteract?.Invoke(raycastHitResults);
    }

    private void HandleDelayedInteraction(RaycastHit raycastHitResults)
    {
        if (!isInteractingWithDelayedObject)
        {
            interactTimer = Time.time;
            isInteractingWithDelayedObject = true;
			if (interactionSlider != null)
            	interactionSlider.value = 0;
        }

        float progress = (Time.time - interactTimer) / interactDelay;

        if (interactionSlider != null)
			interactionSlider.value = progress;

        // Interaction completed
        if (progress >= delayedInteractionTime && !hasInteractedDelayed)
        {
            DetermineInteract(raycastHitResults);
            hasInteractedDelayed = true;
            canInteract = false;
            ResetDelayedInteractionState();
        }
    }

    private void ResetDelayedInteractionState()
    {
        isInteractingWithDelayedObject = false;
		if (interactionSlider != null)
        	interactionSlider.value = 0;
    }

    private void ResetInteractionState()
    {
        isInteractingWithDelayedObject = false;
        hasInteractedDelayed = false;
        canInteract = true;
		if (interactionSlider != null)
        	interactionSlider.value = 0;
    }

	public Vector3 GetLookDirection()
	{
		// Return direction to mouse cursor instead of camera forward
		Ray cameraToMouseRay = playerCamera.ScreenPointToRay(Input.mousePosition);
		return cameraToMouseRay.direction;
	}
}
