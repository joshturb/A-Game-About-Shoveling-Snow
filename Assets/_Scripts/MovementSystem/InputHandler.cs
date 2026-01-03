using UnityEngine;

public class InputHandler : MonoBehaviour
{
	public static InputHandler Instance;

	public InputActions inputActions;
	public InputActions.PlayerActions playerActions;
	public InputActions.UIActions uiActions;

	void Awake()
	{
		Instance = this;

		inputActions = new();
		inputActions.Enable();

		playerActions = inputActions.Player;
		uiActions = inputActions.UI;
	}

	public void OnDestroy()
	{
		inputActions.Disable();
		inputActions.Dispose();
	}
}
