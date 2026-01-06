using UnityEngine;

[CreateAssetMenu(menuName = "SnowGame/Inventory Item", fileName = "NewInventoryItem")]
public class InventoryItem : ScriptableObject
{
    public string itemId;
    public Sprite icon;
    public GameObject prefab;
}
