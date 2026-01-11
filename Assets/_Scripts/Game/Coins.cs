using System;
using UnityEngine;

public class Coins : MonoBehaviour
{
    public static Coins Instance;

    public event Action<int> OnCoinAmountChanged;

    private int coinAmount;
    public int CoinAmount
    {
        get => coinAmount;
        set 
        { 
            if (coinAmount == value)
                return;

            coinAmount = value;
            OnCoinAmountChanged(coinAmount);
        }
    }

    void Awake()
    {
        if (Instance != null)
        {
            Destroy(Instance);
        }
        Instance = this;
    }

    public void AddCoins(int value)
    {
        CoinAmount += value;
    }

    public void SetCoins(int value)
    {
        CoinAmount = value;
    }

    public void RemoveCoins(int amount)
    {
        if (CoinAmount < amount)
            return;

        CoinAmount -= amount;
    }
}
