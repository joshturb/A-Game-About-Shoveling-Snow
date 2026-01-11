using UnityEngine;

public class TestItem : MonoBehaviour, IInteractable
{
    public InventoryItem inventoryItem;
    public void Interact(RaycastHit hit, Transform player, InteractKey interactKey)
    {
        if (interactKey == InteractKey.Click)
            return;
            
        FindFirstObjectByType<Inventory>().AddToInventory(inventoryItem);
    }

}
