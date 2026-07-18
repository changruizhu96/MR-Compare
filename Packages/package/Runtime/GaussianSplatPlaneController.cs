using UnityEngine;
using GaussianSplatting.Runtime;

[ExecuteInEditMode]
[RequireComponent(typeof(GaussianSplatRenderer))]
public class GaussianSplatPlaneController : MonoBehaviour
{
    [SerializeField]
    [Tooltip("Point on the clipping plane")]
    public Vector3 planePoint = Vector3.zero;

    [SerializeField]
    [Tooltip("Normal of the clipping plane")]
    public Vector3 planeNormal = Vector3.up;

    private GaussianSplatRenderer gaussianRenderer;
    private Material splatMaterial;

    void Awake()
    {
        InitializeReferences();
    }

    void OnEnable()
    {
        InitializeReferences();
    }

    void InitializeReferences()
    {
        if (gaussianRenderer == null)
            gaussianRenderer = GetComponent<GaussianSplatRenderer>();

        if (gaussianRenderer != null && splatMaterial == null)
        {
            splatMaterial = gaussianRenderer.m_MatSplats;
            UpdatePlaneParams();
        }
    }

    void OnValidate()
    {
        InitializeReferences();
        UpdatePlaneParams();
    }

    void Update()
    {
        if (splatMaterial == null)
        {
            InitializeReferences();
        }
        UpdatePlaneParams();
    }

    void UpdatePlaneParams()
    {
        if (splatMaterial != null)
        {
            splatMaterial.SetVector("_PlanePoint", planePoint);
            splatMaterial.SetVector("_PlaneNormal", planeNormal);
        }
    }

    public void SetPlanePoint(Vector3 point)
    {
        planePoint = point;
        UpdatePlaneParams();
    }

    public void SetPlaneNormal(Vector3 normal)
    {
        planeNormal = normal;
        UpdatePlaneParams();
    }
}