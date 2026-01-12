using System;
using UnityEngine;

public class ItemController : MonoBehaviour
{
    private SnowInteractor snowInteractor;
    private FPCModule fPCModule;
    private MovementModule movementModule;
    private Inventory inventory;
    private Animator animator;
    private bool isPlowing;
    private bool isShoveling;

    void Start()
    {
        animator = GetComponent<Animator>();
        fPCModule = FindFirstObjectByType<FPCModule>();
        inventory = FindFirstObjectByType<Inventory>();
        if (!fPCModule.TryGetModule(out movementModule))
        {
            Debug.Log("Cant find Movement Module");
            return;
        }
        inventory.OnItemEquip += OnEquip;
        inventory.OnItemDequip += OnDequip;
    }

    void OnDestroy()
    {
        inventory.OnItemEquip -= OnEquip;
        inventory.OnItemDequip -= OnDequip;
    }

    private void OnEquip(SnowInteractor si)
    {
        snowInteractor = si;
        animator.runtimeAnimatorController = snowInteractor.overrideController;
    }

    private void OnDequip()
    {
        snowInteractor = null;
        animator.runtimeAnimatorController = null;
    }

    void Update()
    {
        if (snowInteractor == null)
            return;

        if (Interaction.currentHeldInteractable != null || Interaction.raycastHitResults.collider != null)
            return;

        if (InputHandler.Instance.playerActions.Attack.IsPressed() && !isPlowing && !isShoveling)
        {
            isShoveling = true;
            animator.SetTrigger("Trigger");
        }
        else if (InputHandler.Instance.playerActions.Aim.IsPressed() && movementModule.IsMovingForward && !isShoveling)
        {
            isPlowing = true;
            snowInteractor.Plow();
            if (animator.GetBool("SecondaryAction") == false)
            {
                animator.SetBool("SecondaryAction", true);
            }
        }
        else if (InputHandler.Instance.playerActions.Aim.WasReleasedThisFrame() && isPlowing)
        {
            isPlowing = false;
            if (animator.GetBool("SecondaryAction") == true)
            {
                animator.SetBool("SecondaryAction", false);
            }
        }
    }

    public void Trigger()
    {        
        snowInteractor.Edit();
        isShoveling = false;
    }
}