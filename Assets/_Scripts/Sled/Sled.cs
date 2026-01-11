using UnityEngine;

public class Sled : MonoBehaviour, IInteractable
{
    public int snowAmount;
    public int snowCapacity = 500;
    public GameObject[] snowObjects;

    public void AddToSled(int amount)
    {
        snowAmount += amount;
        snowAmount = Mathf.Clamp(snowAmount, 0, snowCapacity);
        Evaluate();
    }

    private void Evaluate()
    {
        int index;
        int last = snowObjects.Length - 1;

        if (snowCapacity <= 0f || snowAmount <= 0f)
        {
            index = 0;
        }
        else
        {
            float t = snowAmount / snowCapacity; // not clamped yet

            if (t >= 1f)
            {
                index = last; // ONLY at 100% (or above)
            }
            else
            {
                // Map 0..(<1) into 0..(last-1)
                int nonFullLast = Mathf.Max(0, last - 1);
                float tc = Mathf.Clamp01(t);
                index = Mathf.Clamp(Mathf.FloorToInt(tc * (nonFullLast + 1)), 0, nonFullLast);
            }
        }

        for (int i = 0; i < snowObjects.Length; i++)
            snowObjects[i].SetActive(i == index);
    }

    public void ClearSled()
    {
        foreach (var item in snowObjects)
        {
            item.SetActive(false);
        }
        snowAmount = 0;
    }

    public void Interact(RaycastHit hit, Transform player, InteractKey interactKey)
    {
        if (interactKey == InteractKey.Click)
            return;

        if (Inventory.SnowQuantity == 0)
            return;

       int quantity = Inventory.SnowQuantity;
        Inventory.SetSnowQuantity(0);

        AddToSled(quantity);
        print($"Deposit {quantity}");
    }
}
