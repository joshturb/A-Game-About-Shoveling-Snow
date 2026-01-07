using System;
using UnityEngine;

public class ItemController : MonoBehaviour
{
    private SnowInteractor snowInteractor;
    private Animator animator;

    void Start()
    {
        animator = GetComponent<Animator>();
        var inv = FindFirstObjectByType<Inventory>();
        inv.OnItemEquip += OnEquip;
        inv.OnItemDequip += OnDequip;
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
        if (!InputHandler.Instance.playerActions.Attack.WasPressedThisFrame())
            return;

        animator.SetTrigger("Trigger");
    }

    public void Trigger()
    {        
        snowInteractor.Edit();
    }
}