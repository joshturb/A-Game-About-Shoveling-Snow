using System;
using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;

public class FPCModule : MonoBehaviour
{
	public CharacterController characterController;
	public CinemachineCamera playerCamera;

    [Tooltip("List of player modules attached to this character. | Movement should exist first, as some modules such as: 'Jump' rely on the movement the be updated first!")]
    public Vector3 movement;
    public List<PlayerModule> modules = new();
    public event Action<FPCModule, PlayerModule> OnModuleAdded;
    public event Action<FPCModule, PlayerModule> OnModuleRemoved;
	public event Action<float> OnStaminaChanged;
    public float staminaReductionRate;
    public float staminaIncreaseRate = 5;
    public float staminaIncreaseDelay = 5f;
    public float currentStamina = 100f;
    public bool isGrounded;
    private float _timeSinceLastStaminaChange = 0f;

    public void Awake()
    {
        foreach (PlayerModule module in modules)
        {
            module.IsInitialized = true;
            module.InitializeModule(this); 
        }   
    }
    
	public void Update()
	{
		movement = new Vector3(0, movement.y, 0);

		for (int i = 0; i < modules.Count; i++)
		{
			if (modules[i].IsLocked)
				continue;

			modules[i].HandleInput(this);
			modules[i].UpdateModule(this);
		}
		HandleStamina();

		if (characterController.enabled)
			characterController.Move(movement * Time.deltaTime);
	}


    private void HandleStamina()
    {
        if (staminaReductionRate > 0)
        {
            currentStamina -= staminaReductionRate * Time.deltaTime;
            _timeSinceLastStaminaChange = 0f;
        }
        else
        {
            _timeSinceLastStaminaChange += Time.deltaTime;
            if (_timeSinceLastStaminaChange >= staminaIncreaseDelay)
            {
                currentStamina += staminaIncreaseRate * Time.deltaTime;
            }
        }
        
        currentStamina = Mathf.Clamp(currentStamina, 0, 100);
		OnStaminaChanged?.Invoke(currentStamina);
    }

    public void SetStamina(float stamina)
    {
        currentStamina = stamina;
		OnStaminaChanged?.Invoke(currentStamina);
    }

    public void AddModule(PlayerModule module)
    {
        if (!modules.Contains(module))
        {
            module.InitializeModule(this);
            OnModuleAdded?.Invoke(this, module);
            modules.Add(module);
        }
    }

    public void RemoveModule(PlayerModule module)
    {
        if (modules.Contains(module))
        {
            module.OnModuleRemoved(this);
            OnModuleRemoved?.Invoke(this, module);
            modules.Remove(module);
        }
    }

    public bool ContainsModule<T>() where T : PlayerModule
    {
        foreach (var module in modules)
        {
            if (module is T)
            {
                return true;
            }
        }
        return false;
    }

	public bool TryGetModule<T>(out T moduleOut) where T : PlayerModule
	{
		// Search existing modules list first
		foreach (var module in modules)
		{
			if (module is T typedModule)
			{
				moduleOut = typedModule;
				return true;
			}
		}

        // Search children for any Component that matches the requested generic type T
        Component[] childComponents = GetComponentsInChildren<Component>(true);
        for (int i = 0; i < childComponents.Length; i++)
        {
            if (childComponents[i] is T typed)
            {
                moduleOut = typed;
                return true;
            }
        }

        moduleOut = null;
        return false;
	}
}
