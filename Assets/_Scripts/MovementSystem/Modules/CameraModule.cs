
using System;
using UnityEngine;

[CreateAssetMenu(menuName = "Player Movement/Camera Module")]
public class CameraModule : PlayerModule
{
	[SerializeField] private float sensitivity = 1f;
    public float smoothTime = 20f;
    public float clampLookY = 85f;

    private float xRotation = 0f;
    private float yRotation = 0f;
    private float accMouseX = 0f;
    private float accMouseY = 0f;
    private Vector2 currentMouseDelta;
	private Transform cameraHolder;

	public bool isLocked;
    public override bool IsLocked { get => isLocked; set => isLocked = value; }

    private bool isInitialized;
    public override bool IsInitialized { get => isInitialized; set => isInitialized = value; }

	public override void InitializeModule(FPCModule fPCModule)
	{
		IsLocked = false;
		cameraHolder = fPCModule.playerCamera.transform.parent;

		Cursor.lockState = CursorLockMode.Locked;
		Cursor.visible = false;
	}

	public override void OnModuleRemoved(FPCModule fPCModule) { }

    public override void HandleInput(FPCModule fPCModule)
    {
        currentMouseDelta = InputHandler.Instance.playerActions.Look.ReadValue<Vector2>();
    }

    public override void UpdateModule(FPCModule fPCModule)
    {
        // if (Cursor.visible)
        //     return;

        accMouseX = Mathf.Lerp(accMouseX, currentMouseDelta.x, smoothTime * Time.deltaTime);
        accMouseY = Mathf.Lerp(accMouseY, currentMouseDelta.y, smoothTime * Time.deltaTime);

        float mouseX = accMouseX * sensitivity;
        float mouseY = accMouseY * sensitivity;

        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -clampLookY, clampLookY);

        yRotation += mouseX;

        // Apply rotations
        cameraHolder.localRotation = Quaternion.Euler(xRotation, 0, 0);
		fPCModule.transform.localRotation = Quaternion.Euler(0, yRotation, 0);
    }
}