// CHANGED: DetermineInteract now collects ALL IInteractable components found on
// collider + parent chain + children and calls Interact on each (deduped).

using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections.Generic;

public class Interaction : MonoBehaviour
{
    [SerializeField] private float interactDelay = 0.5f;
    [SerializeField] private float interactDistance = 5f;
    [SerializeField] private int delayedInteractionLayer = 7;
    [SerializeField] private float delayedInteractionTime = 0.5f;
    [SerializeField] private LayerMask layerMask;
    public Slider interactionSlider;
    public bool canInteract = true;
    public event Action<RaycastHit> OnInteract;
    public RectTransform crosshair;
    public bool IsHovering;
    [HideInInspector] public Camera playerCamera;
    private float interactTimer = 0f;
    private bool isInteractingWithDelayedObject = false;
    private bool hasInteractedDelayed = false;
    public static RaycastHit raycastHitResults;
    public static IHoldInteractable currentHeldInteractable; // NEW

    public void Awake()
    {
        playerCamera = Camera.main;

        if (interactionSlider != null)
            interactionSlider.maxValue = delayedInteractionTime * 0.95f;
    }

    private void Update()
    {
        InteractKey key;
        if (playerCamera.enabled == false)
            return;

        key = InputHandler.Instance.playerActions.Attack.WasReleasedThisFrame() ? InteractKey.Click : InputHandler.Instance.playerActions.Interact.WasReleasedThisFrame() ? InteractKey.Key : InteractKey.None;
        if (key != InteractKey.None)
        {
            currentHeldInteractable?.OnHoldEnd();
            currentHeldInteractable = null;
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
        var pressedKey = InputHandler.Instance.playerActions.Attack.IsPressed() ? InteractKey.Click : InputHandler.Instance.playerActions.Interact.IsPressed() ? InteractKey.Key : InteractKey.None;
        key = InputHandler.Instance.playerActions.Attack.WasPressedThisFrame() ? InteractKey.Click : InputHandler.Instance.playerActions.Interact.WasPressedThisFrame() ? InteractKey.Key : InteractKey.None;
        if (key != InteractKey.None)
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
                currentHeldInteractable.OnHoldStart(raycastHitResults, key);
            }
            PerformRaycast(true, key);
            
        }
        else if (pressedKey != InteractKey.None)
        {
            if (raycastHitResults.collider.gameObject.layer == delayedInteractionLayer)
                HandleDelayedInteraction(raycastHitResults, pressedKey);
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

    private void PerformRaycast(bool instantInteraction, InteractKey interactKey)
    {
        if (!canInteract)
            return;

        int objectLayer = raycastHitResults.collider.gameObject.layer;

        if (instantInteraction && objectLayer != delayedInteractionLayer && Time.time - interactTimer >= interactDelay)
        {
            DetermineInteract(raycastHitResults, interactKey);
        }
        else if (!instantInteraction && objectLayer == delayedInteractionLayer)
        {
            HandleDelayedInteraction(raycastHitResults, interactKey);
        }
        else
        {
            ResetDelayedInteractionState();
        }
    }

    private void DetermineInteract(RaycastHit raycastHitResults, InteractKey key)
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

        // NEW: call Interact on ALL IInteractables found (collider, parents, children), deduped.
        if (raycastHitResults.collider != null)
        {
            var seen = new HashSet<IInteractable>();

            // On collider object
            var go = raycastHitResults.collider.gameObject;
            var direct = go.GetComponents<IInteractable>();
            for (int i = 0; i < direct.Length; i++)
                if (direct[i] != null) seen.Add(direct[i]);

            // In parents (includes self again sometimes depending on hierarchy; HashSet handles it)
            var parents = go.GetComponentsInParent<IInteractable>(true);
            for (int i = 0; i < parents.Length; i++)
                if (parents[i] != null) seen.Add(parents[i]);

            // In children
            var children = go.GetComponentsInChildren<IInteractable>(true);
            for (int i = 0; i < children.Length; i++)
                if (children[i] != null) seen.Add(children[i]);

            foreach (var interactable in seen)
                interactable.Interact(raycastHitResults, transform, key);
        }

        OnInteract?.Invoke(raycastHitResults);
    }

    private void HandleDelayedInteraction(RaycastHit raycastHitResults, InteractKey key)
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
            DetermineInteract(raycastHitResults, key);
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
