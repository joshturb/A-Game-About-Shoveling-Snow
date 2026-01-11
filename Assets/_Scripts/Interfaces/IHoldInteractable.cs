using UnityEngine;

public interface IHoldInteractable
{
	void OnHoldStart(RaycastHit hit, InteractKey interactKey);
	void OnHoldUpdate(Interaction interaction);
	void OnHoldEnd();
}
