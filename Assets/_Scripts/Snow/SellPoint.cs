using UnityEngine;

public class SellPoint : MonoBehaviour
{
    public BoxCollider sellCollider;
    public float sellMultiplier = 1;
    public float snowPrice;

    void OnTriggerEnter(Collider other)
    {
        if (other.TryGetComponent(out FPCModule _))
        {
            var sq = Inventory.SnowQuantity;

            if (sq == 0)
                return;

            Inventory.SetSnowQuantity(0);
            Sell(sq);
        }

        if (other.TryGetComponent(out Sled sled))
        {
            if (sled.snowAmount == 0)
                return;

            Sell(sled.snowAmount);
            sled.ClearSled();
        }

    }

    private void Sell(int snowAmount)
    {
        int amount = (int)(snowAmount * snowPrice);
        Coins.Instance.AddCoins((int)(amount * sellMultiplier));
    }
}
