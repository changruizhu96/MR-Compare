using System.Collections;
using GaussianSplatting.Runtime;
using UnityEngine;

public class VisualizationModeManager : MonoBehaviour
{
    [Header("UI & Visualization Objects")]
    [SerializeField] private GameObject VCUI;
    [SerializeField] private GameObject DS3;
    [SerializeField] private GameObject MW;
    [SerializeField] private GameObject SB;

    [Header("Passthrough & Camera")]
    [SerializeField] private OVRPassthroughLayer OVRpassThrough;

    private GaussianSplatRenderer _currentGsRenderer;
    private MeshRenderer _currentMeshRenderer;
    private bool _isMenuOpen;
    private bool _isSbModeActive;
    private bool _isSbVisible;
    private float _currentOpacity = 1.0f;

    private void OnEnable()
    {
        AnchorAndObjectManager.OnActiveObjectChanged += HandleActiveObjectChanged;
    }

    private void OnDisable()
    {
        AnchorAndObjectManager.OnActiveObjectChanged -= HandleActiveObjectChanged;
    }

    private void Start()
    {
        VCUI.SetActive(false);
        if (OVRpassThrough) OVRpassThrough.textureOpacity = 1.0f;
        ResetState();

        StartCoroutine(WatchMenuButton());
        StartCoroutine(WatchInteractionButton());
        StartCoroutine(TrackThumbstickOnChange());
    }

    private void HandleActiveObjectChanged(GameObject newActiveObject)
    {
        _currentGsRenderer = null;
        _currentMeshRenderer = null;
        ResetState();

        if (newActiveObject == null)
        {
            return;
        }

        if (newActiveObject.TryGetComponent<GaussianSplatRenderer>(out var gsRenderer))
        {
            _currentGsRenderer = gsRenderer;
            Debug.Log($"VisualizationModeManager is now targeting [Gaussian Splat]: {newActiveObject.name}");
            return;
        }

        if (newActiveObject.TryGetComponent<MeshRenderer>(out var meshRenderer))
        {
            _currentMeshRenderer = meshRenderer;
            Debug.Log($"VisualizationModeManager is now targeting [Mesh]: {newActiveObject.name}");
        }
    }

    public void ResetState()
    {
        _isSbModeActive = false;
        _isSbVisible = false;
        DS3.SetActive(false);
        MW.SetActive(false);
        SB.SetActive(false);

        if (OVRpassThrough)
        {
            OVRpassThrough.overlayType = OVROverlay.OverlayType.Underlay;
            OVRpassThrough.textureOpacity = 1.0f;
        }
    }

    public void ActivateSB()
    {
        _isSbModeActive = true;
        _isSbVisible = true;
        VCUI.SetActive(false);
        _isMenuOpen = false;
        SB.SetActive(true);
        Debug.Log("[VisualizationModeManager] Switch Back mode activated.");
    }

    public void ActivateDS3()
    {
        VCUI.SetActive(false);
        _isMenuOpen = false;
        DS3.SetActive(true);

        if (_currentGsRenderer != null) _currentGsRenderer.m_SplatScale = 1;
        if (_currentMeshRenderer != null) _currentMeshRenderer.enabled = true;
    }

    public void ActivateMW()
    {
        VCUI.SetActive(false);
        _isMenuOpen = false;
        MW.SetActive(true);

        if (_currentGsRenderer != null) _currentGsRenderer.m_SplatScale = 1;
        if (_currentMeshRenderer != null) _currentMeshRenderer.enabled = true;
    }

    private IEnumerator WatchMenuButton()
    {
        while (true)
        {
            if (OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
            {
                _isMenuOpen = !_isMenuOpen;
                if (_isMenuOpen)
                {
                    ResetState();
                }
                VCUI.SetActive(_isMenuOpen);
            }
            yield return null;
        }
    }

    private IEnumerator WatchInteractionButton()
    {
        while (true)
        {
            if (OVRInput.GetDown(OVRInput.RawButton.B, OVRInput.Controller.RTouch))
            {
                if (!_isSbModeActive)
                {
                    Debug.Log("[VisualizationModeManager] B pressed, but Switch Back mode is not active.");
                }
                else
                {
                    _isSbVisible = !_isSbVisible;
                    SB.SetActive(_isSbVisible);
                    Debug.Log($"[VisualizationModeManager] Switch Back visibility toggled: {_isSbVisible}");
                }
            }
            yield return null;
        }
    }

    private IEnumerator TrackThumbstickOnChange()
    {
        while (true)
        {
            if (_currentGsRenderer != null)
            {
                float axisX = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick).x;
                if (Mathf.Abs(axisX) > 0.1f)
                {
                    _currentOpacity += axisX * 0.5f * Time.deltaTime;
                    _currentOpacity = Mathf.Clamp01(_currentOpacity);
                    _currentGsRenderer.m_OpacityScale = _currentOpacity;
                }
            }
            yield return null;
        }
    }
}
