
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// controls area completion. aswell as global
public class StatUI : MonoBehaviour
{
    public TMP_Text globalProgress;
    public Slider globalProgressSlider;
    public Sprite[] snowAmountSprites;
    public Image snowAmountImage;
    public TMP_Text snowAmount;
    public TMP_Text coinAmount;


    void Start()
    {
        SnowController.Instance.OnGlobalProgressUpdated += UpdateProgress;
        Coins.Instance.OnCoinAmountChanged += OnCoinsChanged;
        Inventory.OnSnowQuantityChanged += OnHeldSnowQuantityChanged;
    }

    void OnDestroy()
    {
        SnowController.Instance.OnGlobalProgressUpdated -= UpdateProgress;
        Inventory.OnSnowQuantityChanged -= OnHeldSnowQuantityChanged;
        Coins.Instance.OnCoinAmountChanged -= OnCoinsChanged;
    }

    private void OnCoinsChanged(int value)
    {
        coinAmount.text = value.ToString();
    }

    private void OnHeldSnowQuantityChanged(float amount)
    {
        snowAmount.text = amount.ToString("F0");

        if (snowAmountSprites == null || snowAmountSprites.Length == 0 || snowAmountImage == null)
            return;

        float max = Inventory.MaxSnowQuantity;
        int last = snowAmountSprites.Length - 1;

        int index;
        if (max <= 0f) index = 0;
        else
        {
            float t = Mathf.Clamp01(amount / max);                  // 0..1
            index = Mathf.Clamp(Mathf.FloorToInt(t * (last + 1)),   // 0..last, with t==1 => last
                                0, last);
        }

        snowAmountImage.sprite = snowAmountSprites[index];
    }

    private void UpdateProgress(float percent)
    {
        var value = percent == 100 ? percent.ToString("F0") : percent.ToString("F1");
        globalProgress.text = $"{value} / 100";
        globalProgressSlider.value = percent;
    }
}
