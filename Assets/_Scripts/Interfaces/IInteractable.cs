using UnityEngine;

public enum InteractKey { None, Key, Click }
public interface IInteractable
{
	public void Interact(RaycastHit hit, Transform player, InteractKey interactKey); 
}   


