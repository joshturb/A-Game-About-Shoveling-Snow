using System;
using TMPro;
using UnityEngine;

// controls area completion. aswell as global
public class StatUI : MonoBehaviour
{
    public TMP_Text globalProgress;
    void Start()
    {
        SnowController.Instance.OnGlobalProgressUpdated += UpdateProgress;
    }

    private void UpdateProgress(float percent)
    {
        var value = percent == 100 ? percent.ToString("F0") : percent.ToString("F1");
        globalProgress.text = $"{value} / 100";
    }
}
