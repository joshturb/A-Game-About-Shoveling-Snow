// InventoryUI.cs (change only)
using UnityEngine;
using UnityEngine.UI;

public class InventoryUI : MonoBehaviour
{
    [System.Serializable]
    public class SlotUI
    {
        public GameObject root;      // disable whole slot when empty
        public RectTransform scaleTarget; // optional: scale changes here (can be same object)
        public Image background;
        public Image icon;
    }

    [Header("Slots (size must be 5)")]
    [SerializeField] private SlotUI[] slots = new SlotUI[5];

    [Header("Selection Visuals")]
    [SerializeField] private Color bgNormal = Color.white;
    [SerializeField] private Color bgSelected = Color.white;
    [SerializeField] private Color iconNormal = Color.white;
    [SerializeField] private Color iconSelected = Color.white;

    [SerializeField, Min(0.1f)] private float scaleNormal = 1.0f;
    [SerializeField, Min(0.1f)] private float scaleSelected = 1.15f;

    public void Refresh(Inventory inv)
    {
        if (inv == null) return;
        if (slots == null || slots.Length != 5) return;

        int selected = inv.SelectedSlot;

        for (int i = 0; i < 5; i++)
        {
            var s = slots[i];
            if (s == null) continue;

            var item = inv.GetItem(i);
            bool hasItem = item != null;

            s.root?.SetActive(hasItem);

            if (!hasItem)
                continue;

            if (s.icon != null)
            {
                s.icon.enabled = true;
                s.icon.sprite = item.icon;
            }

            bool isSelected = i == selected;

            if (s.scaleTarget != null)
                s.scaleTarget.localScale = Vector3.one * (isSelected ? scaleSelected : scaleNormal);

            if (s.background != null)
                s.background.color = isSelected ? bgSelected : bgNormal;

            if (s.icon != null)
                s.icon.color = isSelected ? iconSelected : iconNormal;
        }
    }
}
