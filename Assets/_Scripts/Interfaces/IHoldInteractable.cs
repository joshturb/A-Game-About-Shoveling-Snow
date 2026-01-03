using UnityEngine;

public interface IHoldInteractable
{
	void OnHoldStart(RaycastHit hit);
	void OnHoldUpdate();
	void OnHoldEnd();
}
