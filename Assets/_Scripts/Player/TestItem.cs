using UnityEngine;

public class TestItem : MonoBehaviour, IInteractable
{
    public InventoryItem inventoryItem;
    public void Interact(RaycastHit hit, Transform player)
    {
        FindFirstObjectByType<Inventory>().AddToInventory(inventoryItem);
    }

}
