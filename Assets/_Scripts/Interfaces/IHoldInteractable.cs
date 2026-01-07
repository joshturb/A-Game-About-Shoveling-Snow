using UnityEngine;

public interface IHoldInteractable
{
	void OnHoldStart(RaycastHit hit);
	void OnHoldUpdate(Interaction interaction);
	void OnHoldEnd();
}
