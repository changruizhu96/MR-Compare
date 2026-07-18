using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using UnityEngine.EventSystems;

[RequireComponent(typeof(Toggle))]
public class ToggleClickEvent : MonoBehaviour, IPointerClickHandler
{
    [System.Serializable]
    public class ToggleClickedEvent : UnityEvent<Toggle> { }

    public ToggleClickedEvent onClick;

    private Toggle toggle;

    void Awake()
    {
        toggle = GetComponent<Toggle>();
    }

    // IPointerClickHandler callback invoked when the user clicks.
    public void OnPointerClick(PointerEventData eventData)
    {
        // Invoke the onClick event configured in the Inspector.
        onClick.Invoke(toggle);
    }
}
