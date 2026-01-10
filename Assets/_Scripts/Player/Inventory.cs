using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class Inventory : MonoBehaviour
{
    public static float SnowQuantity { get; private set; }

    public static void SetSnowQuantity(float value)
    {
        SnowQuantity = Mathf.Clamp(value, 0f, MaxSnowQuantity);
        OnSnowQuantityChanged?.Invoke(SnowQuantity);
    }

    public static float MaxSnowQuantity = 100;
    public static event Action<float> OnSnowQuantityChanged;

    [Header("Slots")]
    [SerializeField] private int maxSlots = 5;
    [SerializeField] private InventoryItem[] slots = new InventoryItem[5];

    [Header("Spawn")]
    [SerializeField] private Transform spawnPoint;         // where the item spawns
    [SerializeField] private Transform spawnParent;        // optional parent (ex: hand)
    [SerializeField] private bool keepWorldPosition = true;

    [Header("UI")]
    [SerializeField] private InventoryUI inventoryUI;

    [Header("Selection")]
    [SerializeField] private int selectedSlot = -1;

    private GameObject spawnedInstance;

    public event Action<SnowInteractor> OnItemEquip;
    public event Action OnItemDequip;
    public int MaxSlots => Mathf.Clamp(maxSlots, 1, 5);
    public int SelectedSlot => selectedSlot;

    private void Awake()
    {
        if (slots == null || slots.Length != 5)
            slots = new InventoryItem[5];

        if (inventoryUI != null)
            inventoryUI.Refresh(this);

        SetSnowQuantity(0);
        MaxSnowQuantity = 100;
    }

    private void Update()
    {
        HandleNumberKeys();
        HandleScrollWheel();
    }

    // ---- Public API ----

    public InventoryItem GetItem(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= 5) return null;
        return slots[slotIndex];
    }

    public bool AddToInventory(InventoryItem item)
    {
        if (item == null) return false;

        print("added ");
        // Find first empty slot among 0..MaxSlots-1
        for (int i = 0; i < MaxSlots; i++)
        {
            if (slots[i] == null)
            {
                slots[i] = item;

                // Auto-select first item if nothing selected yet
                if (selectedSlot < 0)
                    SelectSlot(i);

                inventoryUI?.Refresh(this);
                return true;
            }
        }

        return false; // full
    }

    public void SelectSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= MaxSlots) return;
        if (slots[slotIndex] == null) return;             // only select slots with items
        if (selectedSlot == slotIndex) 
        {
            if (spawnedInstance != null)
            {
                DespawnCurrent();
                selectedSlot = -1;
                return;
            }
        }

        selectedSlot = slotIndex;
        SpawnSelected();
        inventoryUI?.Refresh(this);
    }

    // ---- Input ----

    private void HandleNumberKeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.digit1Key.wasPressedThisFrame) SelectSlot(0);
        if (kb.digit2Key.wasPressedThisFrame) SelectSlot(1);
        if (kb.digit3Key.wasPressedThisFrame) SelectSlot(2);
        if (kb.digit4Key.wasPressedThisFrame) SelectSlot(3);
        if (kb.digit5Key.wasPressedThisFrame) SelectSlot(4);
    }

    private void HandleScrollWheel()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;

        float scrollY = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scrollY) < 0.01f) return;

        var filled = GetFilledSlots();
        if (filled.Count == 0) return;

        // If nothing selected yet, select first filled
        if (selectedSlot < 0 || slots[selectedSlot] == null)
        {
            SelectSlot(filled[0]);
            return;
        }

        int currentPos = filled.IndexOf(selectedSlot);
        if (currentPos < 0) currentPos = 0;

        bool next = scrollY > 0f;
        int newPos = next
            ? (currentPos + 1) % filled.Count
            : (currentPos - 1 + filled.Count) % filled.Count;

        SelectSlot(filled[newPos]);
    }

    private List<int> GetFilledSlots()
    {
        var list = new List<int>(MaxSlots);
        for (int i = 0; i < MaxSlots; i++)
        {
            if (slots[i] != null)
                list.Add(i);
        }
        return list;
    }

    public static void AddSnow(int amount)
    {
        SetSnowQuantity(SnowQuantity + amount);
    }

    public static void RemoveSnow(int amount)
    {
        SetSnowQuantity(SnowQuantity - amount);
    }

    // ---- Spawning ----

    private void SpawnSelected()
    {
        DespawnCurrent();

        if (selectedSlot < 0 || selectedSlot >= MaxSlots) return;

        var item = slots[selectedSlot];
        if (item == null || item.prefab == null) return;

        Vector3 pos = spawnPoint ? spawnPoint.position : transform.position;
        Quaternion rot = spawnPoint ? spawnPoint.rotation : transform.rotation;

        spawnedInstance = Instantiate(item.prefab, pos, rot);
        spawnedInstance.name = item.prefab.name;

        if (spawnParent != null)
            spawnedInstance.transform.SetParent(spawnParent, keepWorldPosition);

        OnItemEquip?.Invoke(spawnedInstance.GetComponent<SnowInteractor>());
    }

    private void DespawnCurrent()
    {
        if (spawnedInstance != null)
        {
            Destroy(spawnedInstance);
            spawnedInstance = null;
            OnItemDequip?.Invoke();
        }
    }
}
