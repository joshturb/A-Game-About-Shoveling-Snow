using System;
using UnityEngine;

public class Interaction : MonoBehaviour
{
    [SerializeField] private float interactDelay = 1.0f;
    [SerializeField] private float interactDistance = 2f;
    [SerializeField] private LayerMask layerMask;
    public bool canInteract = true;

    private float interactTimer = 0f;

    public event Action<RaycastHit> OnInteract;

    private void Update()
    {
        if (InputHandler.Instance.playerActions.Interact.WasPressedThisFrame())
            PerformRaycast();
    }

    private void PerformRaycast()
    {
        if (!canInteract)
            return;

        if (Time.time - interactTimer < interactDelay)
            return;

        if (!Physics.Raycast(Camera.main.transform.position, Camera.main.transform.forward, out RaycastHit hitInfo, interactDistance, layerMask))
            return;

        interactTimer = Time.time;
        DetermineInteract(hitInfo);
    }

    private void DetermineInteract(RaycastHit hitInfo)
    {
        IInteractable interactable =
            hitInfo.collider.GetComponent<IInteractable>() ??
            hitInfo.collider.GetComponentInParent<IInteractable>() ??
            hitInfo.collider.GetComponentInChildren<IInteractable>();

        interactable?.Interact(hitInfo, transform);

        OnInteract?.Invoke(hitInfo);
        print("Interacted with " + hitInfo.transform.name);
    }
}
