using System.Collections;
using UnityEngine;

public class Shovel : SnowInteractor
{
    public GameObject snowVisual;
    public float visualDisplayTime = 0.25f;
    private Coroutine displayCoroutine;

    public override void Awake()
    {
        base.Awake();
        snowVisual.SetActive(false);
    }

    public override void Edit()
    {
        base.Edit();
        
        if (changedCount == 0)
            return;

        if (displayCoroutine != null)
        {
            StopCoroutine(displayCoroutine);
            displayCoroutine = null;
        }
        displayCoroutine = StartCoroutine(ShowVisual(visualDisplayTime));
    }

    public override void Plow()
    {
        base.Plow();

        if (displayCoroutine != null)
        {
            StopCoroutine(displayCoroutine);
            displayCoroutine = null;
        }
        displayCoroutine = StartCoroutine(ShowVisual(.25f));
    }

    private IEnumerator ShowVisual(float disableTime)
    {
        snowVisual.SetActive(true);
        yield return new WaitForSeconds(disableTime);
        snowVisual.SetActive(false);
    }
}
