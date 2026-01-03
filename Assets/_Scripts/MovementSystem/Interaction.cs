using UnityEngine;
using System;
using UnityEngine.UIElements;

public class Interaction : MonoBehaviour
{
    [SerializeField] private float interactDelay = 0.5f;
    [SerializeField] private float interactDistance = 5f;
    [SerializeField] private int delayedInteractionLayer = 7;
    [SerializeField] private float delayedInteractionTime = 0.5f;
    [SerializeField] private LayerMask layerMask;
    public event Action<RaycastHit> OnInteract;
	public RaycastHit raycastHitResults;
    public bool canInteract = true;
	public bool IsHovering;
	private Slider interactionSlider;
	private VisualElement crosshair;
	private Label hoverTextLabel;
	private bool isHoverTextLocked;
    [HideInInspector] public Camera playerCamera;
    private float interactTimer = 0f;
    private bool isInteractingWithDelayedObject = false;
    private bool hasInteractedDelayed = false;
    private IHoldInteractable currentHeldInteractable; // NEW

    public void Awake()
    {
		var uiDocument = FindFirstObjectByType<UIDocument>();

		crosshair = uiDocument.rootVisualElement.Q("Crosshair");
		interactionSlider = uiDocument.rootVisualElement.Q<Slider>("InteractSlider");
		hoverTextLabel = uiDocument.rootVisualElement.Q<Label>("HoverText");

		if (interactionSlider != null)
		{
			interactionSlider.highValue = delayedInteractionTime * 0.95f;
			interactionSlider.visible = false;
		}
        playerCamera = Camera.main;
	}
	
	private void Update()
	{
		if (playerCamera.enabled == false)
			return;
			
		// Always check for release first
		if (InputHandler.Instance.playerActions.Interact.WasReleasedThisFrame())
		{
			currentHeldInteractable?.OnHoldEnd();
			currentHeldInteractable = null;
			ResetInteractionState();
			return;
		}
		
		// If a hold interactable is already active, update it without checking for a new one
		if (currentHeldInteractable != null)
		{
			currentHeldInteractable.OnHoldUpdate();
			return;
		}
		
		// Otherwise, perform raycast for a new hold interactable or delayed interaction
		Ray cameraToMouseRay = playerCamera.ScreenPointToRay(Input.mousePosition);
		if (!Physics.Raycast(cameraToMouseRay, out raycastHitResults, interactDistance, layerMask))
		{
			crosshair.style.scale = new Vector3(1f, 1f, 1f);
			ResetDelayedInteractionState();
			IsHovering = false;
			if (!isHoverTextLocked)
			{
				hoverTextLabel.text = "";
			}

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
			if (holdInteractable != null)
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
			raycastHitResults.collider.GetComponentInChildren<IInteractable>() != null)
		{
			if (raycastHitResults.collider.TryGetComponent(out IHoverText text) ||
				raycastHitResults.collider.GetComponentInParent<IHoverText>() != null ||
				raycastHitResults.collider.GetComponentInChildren<IHoverText>() != null)
			{
				text ??= raycastHitResults.collider.GetComponentInParent<IHoverText>();
				text ??= raycastHitResults.collider.GetComponentInChildren<IHoverText>();
				if (!isHoverTextLocked)
					hoverTextLabel.text = text.HoverText;

				return;
			}

			IsHovering = true;
			crosshair.style.scale = new Vector3(1.5f, 1.5f, 1f);
		}
		else
		{
			IsHovering = false;
			if (!isHoverTextLocked)
			{
				hoverTextLabel.text = "";
			}
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
		var eventSystem = UnityEngine.EventSystems.EventSystem.current;
		var selectedObject = eventSystem != null ? eventSystem.currentSelectedGameObject : null;

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
			interactionSlider.visible = true;

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
			interactionSlider.visible = false;
    }

	private void ResetInteractionState()
	{
		isInteractingWithDelayedObject = false;
		hasInteractedDelayed = false;
		canInteract = true;
		if (interactionSlider != null)
			interactionSlider.value = 0;
		interactionSlider.visible = false;
	}

	public void LockHoverText()
	{
		isHoverTextLocked = true;
	}

	public void UnlockHoverText()
	{
		isHoverTextLocked = false;
	}

	public void SetHoverText(string text)
	{
		hoverTextLabel.text = text;
	}
}
