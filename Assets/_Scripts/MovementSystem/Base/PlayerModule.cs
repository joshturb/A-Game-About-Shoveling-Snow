
using UnityEngine;

public abstract class PlayerModule : ScriptableObject
{
    public abstract bool IsLocked {get; set;}
    public abstract bool IsInitialized {get; set;}
    public abstract void InitializeModule(FPCModule fPCModule);
    public abstract void HandleInput(FPCModule fPCModule);
    public abstract void UpdateModule(FPCModule fPCModule);
    public abstract void OnModuleRemoved(FPCModule fPCModule);
}
