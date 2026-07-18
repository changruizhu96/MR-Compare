using GaussianSplatting.Runtime; // Required for GaussianSplatRenderer
using Meta.XR;
using Meta.XR.BuildingBlocks; // Required for RoomMeshEvent
using Meta.XR.MRUtilityKit;
using Meta.XR.MRUtilityKit.BuildingBlocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using UnityEngine.VFX;


using Appletea.Dev.PointCloud;



[DefaultExecutionOrder(-1000)]
public class AllInOneRegistration : MonoBehaviour
{
    public enum WorkflowMode
    {
        Align,
        Load
    }

    public enum SelectionGizmoShape
    {
        Box,
        Sphere
    }

    public enum TargetFormat
    {
        [InspectorName("Room Mesh (Legacy)")]
        roomMesh,
        preScan,
        realTimeScan,
        effectMesh

    }

    public enum RoughAlignMode
    {
        Teaser,
        None
    }

    public enum SourceFormat
    {
        GaussianSplating,
        Mesh
    }

    public enum AlignmentReferenceMode
    {
        SpatialAnchor,
        [InspectorName("Room Mesh (Legacy)")]
        RoomMesh,
        EffectMesh
    }

    public WorkflowMode workflowMode = WorkflowMode.Align;
    public TargetFormat targetFormat = TargetFormat.effectMesh;
    public SourceFormat sourceType = SourceFormat.GaussianSplating;
    public RoughAlignMode roughAligner = RoughAlignMode.Teaser;


    [SerializeField]
    private GaussianSplatRenderer gaussianSourceRenderer;

    [SerializeField] private MeshFilter sourceMesh;

    [SerializeField]
    private string pointCloudTargetFile = "pointcloud.bytes";

    [SerializeField]
    private string AnchorUuidFile = "anchor_uuid.txt";
    [SerializeField]
    private GameObject anchorHolder;

    [SerializeField] private RoomMeshEvent roomMeshEventTarget;
    [SerializeField] private EffectMeshEvent effectMeshEventTarget;

    [SerializeField] private VisualEffect pointCloudVFX;

    [SerializeField] private MonoBehaviour scannerTarget;
    [SerializeField, Tooltip("Allow Y to run additional alignments with the currently available target data.")]
    private bool allowRepeatedAlignment = true;



    [SerializeField] private bool estimateScaling = false; // Added scaling parameter
    [SerializeField] private float voxelSizeTeaser = 0.1f; // For TEASER++ input point cloud
    [SerializeField] private int maxIterationsTeaser = 150;
    [SerializeField] private float noiseBoundTeaser = 0.1f;
    [SerializeField] private float normalRadiusTeaser = 0.4f;
    [SerializeField] private float fpfhRadiusTeaser = 0.8f;
    [SerializeField] private float matcherRatioTeaser = 0.8f;
    [SerializeField] private float rotationCostThresholdTeaser = 1e-6f;


    public bool useGICP = true;
    [SerializeField] private float voxelSizeGICP = 0.1f;
    [SerializeField] private int modelName = 0;
    [SerializeField] private int maxIterGICP = 100;
    [SerializeField] private float voxelResolutionGICP = 0.1f;
    [SerializeField] private float maxCorrespondenceDistanceGICP = 0.2f;
    [SerializeField] private int correspondenceRandomnessGICP = 20;
    [SerializeField] private float epsilonGICP = 1e-8f;

    [SerializeField] private Vector3 boxSize = new Vector3(10f, 10f, 10f); //make sure it's big enough!
    [SerializeField] private bool showSelectionGizmo = true;
    [SerializeField] private SelectionGizmoShape selectionGizmoShape = SelectionGizmoShape.Box;
    [SerializeField] private Color selectionGizmoColor = new Color(1f, 0.65f, 0.1f, 1f);
    [SerializeField] private bool usePointCloudCentroidAsSelectionCenter = false;
    [SerializeField] private Transform selectionCenterOverride;
    [SerializeField] private Vector3 selectionCenterOffset = Vector3.zero;
    [SerializeField] private bool useBakedData = true;
    [FormerlySerializedAs("isPointCloudVisulazation")]
    public bool isTargetPointVisualization = false;
    public bool isBoxSelection = false;


    public bool isSaving = false;
    [SerializeField] private AlignmentReferenceMode alignmentReferenceMode = AlignmentReferenceMode.EffectMesh;
    [SerializeField] private string alignmentFile = "alignment_demo.json";

    

    private List<Vector3> cloudSource = new List<Vector3>(); // Moving points (3DGS)
    private List<Vector3> cloudTarget = new List<Vector3>();
    private bool isRegistrationRunning = false; // Prevent concurrent registrations
    private bool hasCompletedRegistration = false;
    private int splatCount;
    private List<int> selectedGSIndices = new List<int>();
    private float3[] positions;
    private GameObject anchorHolderCopy;
    private OVRSpatialAnchor currentAnchor;
    private GraphicsBuffer pointDataBuffer;
    private int readyCount = 0;
    private Action onBothReady;
    private Transform currentRoomMeshReference;
    private Transform currentEffectMeshReference;

    private struct PointDataVFX { public Vector3 position; }
    private List<PointDataVFX> pointDataList = new();
    private int pointDataStride = System.Runtime.InteropServices.Marshal.SizeOf<PointDataVFX>();
    private static readonly int PointBufferID = Shader.PropertyToID("PointBuffer");
    private static readonly int PointCountID = Shader.PropertyToID("PointCount");
    private bool useVoxelGICP;
    private bool useTeaser;
    //private bool useTurboReg;

    private Transform saveTransform;

    Quaternion R = Quaternion.identity;
    Vector3 t = Vector3.zero;
    float s = 1f;
    private Vector3 latestInitialPosition = Vector3.zero;
    private Quaternion latestInitialRotation = Quaternion.identity;
    private Vector3 latestInitialLocalScale = Vector3.one;
    private Matrix4x4 latestDeltaMatrix = Matrix4x4.identity;
    private bool hasLatestTransformData = false;



    [DllImport("TeaserDll_final", CallingConvention = CallingConvention.Cdecl)]
    private static extern GICPResult RunTeaserGICP(
    // --- general input ---
    [In] float[] referencePoints,
    int refTotalFloats,
    [In] float[] targetPoints,
    int tgtTotalFloats,

    // --- TEASER++ parameter ---
    [MarshalAs(UnmanagedType.I1)] bool useTeaser,
    [MarshalAs(UnmanagedType.I1)] bool useVICP, // for controlling the user of GICP or VGCIP
    [MarshalAs(UnmanagedType.I1)] bool scaling,
    float voxelSizeTeaser,
    int maxIterationsTeaser,
    float teaser_noise_bound,
    float teaser_normalRadius,
    float teaser_fpfhRadius,
    float teaser_matcher_ratio,
    float rotation_cost_threshold,

    // --- VGICP parameter ---
    [MarshalAs(UnmanagedType.I1)] bool doDownsampleGICP,
    float voxelSizeGICP,
    int GICPmethod,
    int gicp_max_iter,
    float gicp_voxel_resolution,
    float gicp_max_correspondence_distance,
    int gicp_correspondence_randomness,
    float gicp_epsilon
);




    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct GICPResult
    {
        public bool converged;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public float[] matrix;

        public float scale;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string message;
    }


    // **NEW**: A simple class/struct to hold the pose data for serialization.
    [System.Serializable]
    private class PoseData
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 localScale;
        public Vector3 initialPosition;
        public Quaternion initialRotation;
        public Vector3 initialLocalScale;
        public Vector3 deltaTranslation;
        public Quaternion deltaRotation;
        public float deltaScale;
        public float[] deltaMatrix;
        public string alignmentReferenceMode;
        public string alignmentReferenceName;
    }

    private void Awake()
    {
        ConfigureScannerTargetActivation();
        ConfigureEffectMeshRendering();

        string path = Environment.GetEnvironmentVariable("PATH");
        string pluginPath = Application.dataPath + "/Plugins/x86_64";
        Environment.SetEnvironmentVariable("PATH", pluginPath + ";" + path);
        if (voxelSizeGICP > 0)
            useVoxelGICP = true;
        else useVoxelGICP = false;


        if (roughAligner == RoughAlignMode.Teaser)
        {
            //useTurboReg = false;
            useTeaser = true;
        }

        else
        {
            //useTurboReg = false;
            useTeaser = false;
        }


    }

    private void ConfigureScannerTargetActivation()
    {
        if (scannerTarget == null)
        {
            return;
        }

        bool shouldEnableScanner = workflowMode == WorkflowMode.Align &&
                                   targetFormat == TargetFormat.realTimeScan;
        scannerTarget.gameObject.SetActive(shouldEnableScanner);
    }

    private void ConfigureEffectMeshRendering()
    {
        if (effectMeshEventTarget == null)
        {
            effectMeshEventTarget = UnityEngine.Object.FindFirstObjectByType<EffectMeshEvent>();
        }

        if (effectMeshEventTarget == null)
        {
            return;
        }

        effectMeshEventTarget.OnGlobalMeshLoadComplete.AddListener(HandleEffectMeshRendererLoaded);
        HideExistingEffectMeshRenderers(effectMeshEventTarget.GetComponent<EffectMesh>());
    }



    // Start is called once before the first execution of Update after the MonoBehaviour is created
    async void Start()
    {
        if (workflowMode == WorkflowMode.Load)
        {
            await LoadSavedAlignmentAsync();
            return;
        }

        onBothReady += RegistrationProcess;

        if (!InitializeSelectedSource() || !InitializeSelectedTarget())
        {
            enabled = false;
            return;
        }

    }

    private async Task LoadSavedAlignmentAsync()
    {
        Transform targetTransform = GetSourceTransform();
        if (targetTransform == null)
        {
            Debug.LogError("Cannot load saved alignment: selected source object is not assigned.", this);
            enabled = false;
            return;
        }

        string alignmentPath = Path.Combine(Application.persistentDataPath, GetAlignmentFileName());
        if (!File.Exists(alignmentPath))
        {
            Debug.LogError($"Cannot load saved alignment: alignment file not found at {alignmentPath}", this);
            enabled = false;
            return;
        }

        PoseData savedPose;
        try
        {
            savedPose = JsonUtility.FromJson<PoseData>(File.ReadAllText(alignmentPath));
        }
        catch (Exception e)
        {
            Debug.LogError($"Cannot load saved alignment: failed to read alignment file. {e.Message}", this);
            enabled = false;
            return;
        }

        if (savedPose == null)
        {
            Debug.LogError("Cannot load saved alignment: alignment file is empty or invalid.", this);
            enabled = false;
            return;
        }

        WarnIfAlignmentReferenceMismatch(savedPose);

        Transform referenceTransform = await ResolveAlignmentReferenceForLoadAsync();
        if (referenceTransform == null)
        {
            enabled = false;
            return;
        }

        ApplySavedAlignment(referenceTransform, targetTransform, savedPose);
    }

    private bool InitializeSelectedSource()
    {
        switch (sourceType)
        {
            case SourceFormat.GaussianSplating:
                if (gaussianSourceRenderer == null)
                {
                    Debug.LogError("Gaussian source renderer is not assigned for GaussianSplating source mode.", this);
                    return false;
                }

                splatCount = useBakedData
                    ? (gaussianSourceRenderer.m_BakedData?.PointCount ?? 0)
                    : gaussianSourceRenderer.splatCount;
                return ExtractSourcePointCloudFromGS();

            case SourceFormat.Mesh:
                if (sourceMesh == null || sourceMesh.sharedMesh == null)
                {
                    Debug.LogError("Source mesh is not assigned or has no mesh for Mesh source mode.", this);
                    return false;
                }

                return ExtractSourcePointCloudFromMesh();

            default:
                Debug.LogError($"Unsupported source type: {sourceType}", this);
                return false;
        }
    }

    private bool InitializeSelectedTarget()
    {
        switch (targetFormat)
        {
            case TargetFormat.roomMesh:
                Debug.Log("RoomMesh target enabled. Subscribing to load event.");
                if (roomMeshEventTarget == null)
                {
                    Debug.LogError("RoomMesh target is not assigned!", this);
                    return false;
                }

                roomMeshEventTarget.OnRoomMeshLoadCompleted.AddListener(HandleRoomMeshLoaded);
                SetPointCloudVfxActive(false);
                return true;

            case TargetFormat.effectMesh:
                Debug.Log("EffectMesh target enabled. Subscribing to global mesh load event.");
                if (effectMeshEventTarget == null)
                {
                    Debug.LogError("EffectMesh target is not assigned!", this);
                    return false;
                }

                WarnIfEffectMeshDoesNotIncludeGlobalMesh(effectMeshEventTarget);
                effectMeshEventTarget.OnGlobalMeshLoadComplete.AddListener(HandleEffectMeshLoaded);
                SetPointCloudVfxActive(false);
                return true;

            case TargetFormat.preScan:
                Debug.Log("Scan file target enabled. Loading anchor and point cloud.");
                if (pointCloudVFX == null)
                {
                    Debug.LogError("PointCloud VFX is required for preScan target mode.", this);
                    return false;
                }

                StartAsyncPointFromScan();
                SetPointCloudVfxActive(false);
                return true;

            case TargetFormat.realTimeScan:
                if (sourceType == SourceFormat.GaussianSplating)
                {
                    gaussianSourceRenderer.m_SplatScale = 0;
                }

                Debug.Log("Real-time Scan target enabled. Waiting for user input to trigger registration.");
                if (!IsCompatibleScannerTarget(scannerTarget))
                {
                    Debug.LogError("Scanner target must expose GetStoredPointCloud() or a public pointsData field.", this);
                    return false;
                }

                return true;

            default:
                Debug.LogError($"Unsupported target format: {targetFormat}", this);
                return false;
        }
    }

    private void SetPointCloudVfxActive(bool active)
    {
        if (pointCloudVFX != null)
        {
            pointCloudVFX.gameObject.SetActive(active);
        }
    }

    void Update()
    {
        if (workflowMode != WorkflowMode.Align)
        {
            return;
        }

        if (targetFormat == TargetFormat.realTimeScan && OVRInput.GetDown(OVRInput.RawButton.X))
        {
            if (hasCompletedRegistration && !allowRepeatedAlignment)
            {
                Debug.LogWarning("Repeated alignment is disabled. Enable Allow Repeated Alignment to resume scanning.", this);
                return;
            }

            HandleRealTimeScanInput();
            return;
        }

        if (OVRInput.GetDown(OVRInput.RawButton.Y))
        {
            if (targetFormat == TargetFormat.realTimeScan)
            {
                if (hasCompletedRegistration && !allowRepeatedAlignment)
                {
                    Debug.LogWarning("Repeated alignment is disabled.", this);
                    return;
                }

                Debug.Log("Registration triggered for real-time scan.");
                if (TriggerRegistrationWithScannerData() &&
                    sourceType == SourceFormat.GaussianSplating &&
                    gaussianSourceRenderer != null)
                {
                    gaussianSourceRenderer.m_SplatScale = 1f;
                }
                return;
            }

            if (!allowRepeatedAlignment)
            {
                Debug.LogWarning("Manual repeated alignment is disabled.", this);
                return;
            }

            TriggerRegistrationWithRetainedTarget();
        }
    }

    private bool TriggerRegistrationWithRetainedTarget()
    {
        if (isRegistrationRunning)
        {
            Debug.LogWarning("Cannot trigger repeated alignment while registration is running.", this);
            return false;
        }

        if (cloudTarget.Count == 0)
        {
            Debug.LogWarning("Cannot trigger repeated alignment: target data is not ready.", this);
            return false;
        }

        if (!RefreshSourcePointCloudInCurrentPose())
        {
            return false;
        }

        Debug.Log($"Repeated alignment triggered with {cloudTarget.Count} retained target points.", this);
        RegistrationProcess();
        return true;
    }

    private bool TriggerRegistrationWithScannerData()
    {
        if (isRegistrationRunning)
        {
            Debug.LogWarning("Cannot trigger scanner registration, another registration is already running.");
            return false;
        }

        if (!RefreshSourcePointCloudInCurrentPose())
        {
            return false;
        }

        cloudTarget = ExtractScannerPointCloud();
        if (cloudTarget.Count == 0)
        {
            Debug.LogError("Scanner point cloud is empty! Make sure you have scanned points.");
            return false;
        }

        Debug.Log($"Extracted {cloudTarget.Count} points from the scanner.");
        if (scannerTarget != null)
        {
            scannerTarget.gameObject.SetActive(false);
            AsyncGPUReadback.WaitAllRequests();
        }

        MarkReady();
        return true;
    }

    private bool RefreshSourcePointCloudInCurrentPose()
    {
        List<Vector3> refreshedSource;
        switch (sourceType)
        {
            case SourceFormat.GaussianSplating:
                if (gaussianSourceRenderer == null)
                {
                    Debug.LogError("Cannot refresh registration source: Gaussian renderer is not assigned.", this);
                    return false;
                }

                if (useBakedData)
                {
                    refreshedSource = ExtractPointCloudFromBakedData();
                }
                else
                {
                    GraphicsBuffer gpuPosBuffer = gaussianSourceRenderer.GetGpuPosData();
                    if (gpuPosBuffer == null)
                    {
                        Debug.LogError("Cannot refresh registration source: Gaussian GPU position buffer is unavailable.", this);
                        return false;
                    }

                    refreshedSource = ExtractPointCloudFromGraphicsBuffer(gpuPosBuffer, gaussianSourceRenderer.transform);
                }
                break;

            case SourceFormat.Mesh:
                refreshedSource = ExtractMeshPointCloud(sourceMesh, applySelection: true);
                break;

            default:
                Debug.LogError($"Cannot refresh unsupported source type: {sourceType}", this);
                return false;
        }

        if (refreshedSource.Count == 0)
        {
            Debug.LogError("Cannot refresh registration source: current source point cloud is empty.", this);
            return false;
        }

        cloudSource = refreshedSource;
        return true;
    }

    private void HandleRealTimeScanInput()
    {
        if (isRegistrationRunning)
        {
            Debug.LogWarning("Cannot resume scanning while registration is running.", this);
            return;
        }

        if (!IsCompatibleScannerTarget(scannerTarget))
        {
            Debug.LogError("Cannot resume scanning: scanner target is unavailable or incompatible.", this);
            return;
        }

        if (sourceType == SourceFormat.GaussianSplating && gaussianSourceRenderer != null)
        {
            gaussianSourceRenderer.m_SplatScale = 0f;
        }

        BeginScannerScan();
    }

    private bool IsCompatibleScannerTarget(MonoBehaviour target)
    {
        if (target == null)
        {
            return false;
        }

        return target.GetType().GetMethod("GetStoredPointCloud") != null ||
               (target.GetType().GetField("pointsData") != null && target.GetType().GetField("pointsData").IsPublic);
    }

    private void BeginScannerScan()
    {
        if (scannerTarget == null)
        {
            return;
        }

        if (!scannerTarget.gameObject.activeInHierarchy)
        {
            scannerTarget.gameObject.SetActive(true);
        }

        if (!scannerTarget.enabled)
        {
            scannerTarget.enabled = true;
        }

        var beginMethod = scannerTarget.GetType().GetMethod("BeginScanning");
        if (beginMethod != null)
        {
            beginMethod.Invoke(scannerTarget, null);
        }
    }

    private List<Vector3> ExtractScannerPointCloud()
    {
        if (scannerTarget == null) return new List<Vector3>();

        // Use the public accessor method if available (Recommended)
        var getMethod = scannerTarget.GetType().GetMethod("GetStoredPointCloud");
        if (getMethod != null)
        {
            return (List<Vector3>)getMethod.Invoke(scannerTarget, null);
        }

        // Fallback to direct access if the field is public (Less Recommended)
        var fieldInfo = scannerTarget.GetType().GetField("pointsData");
        if (fieldInfo != null && fieldInfo.IsPublic)
        {
            var pointsDataObj = fieldInfo.GetValue(scannerTarget);
            if (pointsDataObj != null)
            {
                var getAllPointsMethod = pointsDataObj.GetType().GetMethod("GetAllPoints");
                if (getAllPointsMethod != null)
                {
                    return (List<Vector3>)getAllPointsMethod.Invoke(pointsDataObj, null);
                }
            }
        }

        Debug.LogError("Could not extract points from scannerTarget. Ensure it exposes 'GetStoredPointCloud()' or a public 'pointsData' field.", this);
        return new List<Vector3>();
    }



    // **NEW**: Method to handle the saving logic.
    private void SaveAlignmentResult(Transform referenceTransform, string referenceLabel)
    {
        if (referenceTransform == null)
        {
            Debug.LogWarning($"Cannot save alignment result: {referenceLabel} reference transform is null.");
            return;
        }

        if (sourceType == SourceFormat.GaussianSplating)
        {
            if (gaussianSourceRenderer == null)
            {
                Debug.LogWarning("Cannot save alignment result: Gaussian Renderer is null.");
                return;
            }
            saveTransform = gaussianSourceRenderer.transform;
        }
        else
        {
            if (sourceMesh == null)
            {
                Debug.LogWarning("Cannot save alignment result: Source Mesh is null.");
                return;
            }
            saveTransform = sourceMesh.transform;
        }


        Vector3 relativePosition = referenceTransform.InverseTransformPoint(saveTransform.position);
        Quaternion relativeRotation = Quaternion.Inverse(referenceTransform.rotation) * saveTransform.rotation;
        Matrix4x4 deltaToSave = hasLatestTransformData
            ? latestDeltaMatrix
            : Matrix4x4.TRS(t, R, Vector3.one * s);
        PoseData poseToSave = new PoseData
        {
            position = relativePosition,
            rotation = relativeRotation,
            localScale = saveTransform.localScale,
            initialPosition = hasLatestTransformData ? latestInitialPosition : saveTransform.position,
            initialRotation = hasLatestTransformData ? latestInitialRotation : saveTransform.rotation,
            initialLocalScale = hasLatestTransformData ? latestInitialLocalScale : saveTransform.localScale,
            deltaTranslation = t,
            deltaRotation = R,
            deltaScale = s,
            deltaMatrix = MatrixToArray(deltaToSave),
            alignmentReferenceMode = referenceLabel,
            alignmentReferenceName = referenceTransform.name
        };

        string json = JsonUtility.ToJson(poseToSave, true);

        // Define the path to save the file.
        string path = Path.Combine(Application.persistentDataPath, GetAlignmentFileName());

        try
        {
            // Write the JSON string to the file.
            File.WriteAllText(path, json);
            Debug.Log($"Successfully saved alignment result to: {path} using {referenceLabel} reference.");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to save alignment file: {e.Message}");
        }

    }

    private static float[] MatrixToArray(Matrix4x4 matrix)
    {
        float[] values = new float[16];
        for (int i = 0; i < 16; i++)
        {
            values[i] = matrix[i];
        }
        return values;
    }

    private string GetAlignmentFileName()
    {
        return string.IsNullOrWhiteSpace(alignmentFile) ? "alignment_result.json" : alignmentFile;
    }

    private void ApplySavedAlignment(Transform referenceTransform, Transform targetTransform, PoseData savedPose)
    {
        targetTransform.SetParent(referenceTransform, false);
        targetTransform.localPosition = savedPose.position;
        targetTransform.localRotation = savedPose.rotation;
        if (savedPose.localScale.sqrMagnitude > 1e-8f)
        {
            targetTransform.localScale = savedPose.localScale;
        }

        Debug.Log($"Loaded saved alignment from {GetAlignmentFileName()} using {GetAlignmentReferenceLabel()} reference.", this);
    }

    private void WarnIfAlignmentReferenceMismatch(PoseData savedPose)
    {
        if (string.IsNullOrWhiteSpace(savedPose.alignmentReferenceMode))
        {
            return;
        }

        string expected = GetAlignmentReferenceLabel();
        string savedReference = NormalizeAlignmentReferenceLabel(savedPose.alignmentReferenceMode);
        if (!string.Equals(savedReference, expected, StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogWarning($"Alignment file was saved with reference '{savedPose.alignmentReferenceMode}' ({savedPose.alignmentReferenceName}), but this component is configured for '{expected}'.", this);
        }
    }

    private async Task<Transform> ResolveAlignmentReferenceForLoadAsync()
    {
        switch (alignmentReferenceMode)
        {
            case AlignmentReferenceMode.RoomMesh:
                return await ResolveCurrentRoomMeshReferenceForLoadAsync();
            case AlignmentReferenceMode.EffectMesh:
                return await ResolveCurrentEffectMeshReferenceAsync();
            case AlignmentReferenceMode.SpatialAnchor:
            default:
                return await EnsureCurrentAnchorLoadedAsync() ? currentAnchor.transform : null;
        }
    }

    private async Task<Transform> ResolveCurrentRoomMeshReferenceForLoadAsync()
    {
        Transform existingReference = TryFindCompletedRoomMeshReference();
        if (existingReference != null)
        {
            currentRoomMeshReference = existingReference;
            return currentRoomMeshReference;
        }

        if (roomMeshEventTarget == null)
        {
            roomMeshEventTarget = UnityEngine.Object.FindFirstObjectByType<RoomMeshEvent>();
        }

        if (roomMeshEventTarget == null)
        {
            Debug.LogError("Cannot load saved alignment: RoomMeshEvent target is not assigned and could not be found in the scene.", this);
            return null;
        }

        void HandleLoaded(MeshFilter meshFilter)
        {
            if (meshFilter != null)
            {
                currentRoomMeshReference = meshFilter.transform;
            }
        }

        roomMeshEventTarget.OnRoomMeshLoadCompleted.AddListener(HandleLoaded);
        try
        {
            return await WaitForMeshReferenceAsync(
                () => currentRoomMeshReference != null ? currentRoomMeshReference : TryFindCompletedRoomMeshReference(),
                "RoomMesh");
        }
        finally
        {
            roomMeshEventTarget.OnRoomMeshLoadCompleted.RemoveListener(HandleLoaded);
        }
    }

    private async Task<Transform> ResolveCurrentEffectMeshReferenceAsync()
    {
        Transform existingReference = currentEffectMeshReference != null
            ? currentEffectMeshReference
            : TryFindCompletedEffectMeshReference();
        if (existingReference != null)
        {
            currentEffectMeshReference = existingReference;
            HideMeshRenderers(currentEffectMeshReference);
            return currentEffectMeshReference;
        }

        if (effectMeshEventTarget == null)
        {
            effectMeshEventTarget = UnityEngine.Object.FindFirstObjectByType<EffectMeshEvent>();
        }

        if (effectMeshEventTarget == null)
        {
            Debug.LogError("Cannot resolve EffectMesh alignment reference: EffectMeshEvent is not assigned and could not be found in the scene.", this);
            return null;
        }

        WarnIfEffectMeshDoesNotIncludeGlobalMesh(effectMeshEventTarget);

        void HandleLoaded(MeshFilter meshFilter)
        {
            if (meshFilter != null)
            {
                currentEffectMeshReference = meshFilter.transform;
                HideMeshRenderers(currentEffectMeshReference);
            }
        }

        effectMeshEventTarget.OnGlobalMeshLoadComplete.AddListener(HandleLoaded);
        try
        {
            Transform referenceTransform = await WaitForMeshReferenceAsync(
                () => currentEffectMeshReference != null ? currentEffectMeshReference : TryFindCompletedEffectMeshReference(),
                "EffectMesh Global Mesh");
            HideMeshRenderers(referenceTransform);
            return referenceTransform;
        }
        finally
        {
            effectMeshEventTarget.OnGlobalMeshLoadComplete.RemoveListener(HandleLoaded);
        }
    }

    private async Task<Transform> WaitForMeshReferenceAsync(Func<Transform> tryFindReference, string referenceLabel)
    {
        const float timeoutSeconds = 10f;
        float endTime = Time.realtimeSinceStartup + timeoutSeconds;
        while (true)
        {
            Transform referenceTransform = tryFindReference();
            if (referenceTransform != null)
            {
                return referenceTransform;
            }

            if (Time.realtimeSinceStartup >= endTime)
            {
                Debug.LogError($"Cannot resolve alignment reference: timed out waiting for {referenceLabel}.", this);
                return null;
            }

            await Task.Yield();
        }
    }

    private Transform TryFindCompletedRoomMeshReference()
    {
        foreach (RoomMeshAnchor roomMeshAnchor in UnityEngine.Object.FindObjectsByType<RoomMeshAnchor>(FindObjectsSortMode.None))
        {
            if (roomMeshAnchor == null || !roomMeshAnchor.IsCompleted)
            {
                continue;
            }

            MeshFilter meshFilter = roomMeshAnchor.GetComponent<MeshFilter>();
            return meshFilter != null ? meshFilter.transform : roomMeshAnchor.transform;
        }

        return null;
    }

    private Transform TryFindCompletedEffectMeshReference()
    {
        foreach (EffectMesh effectMesh in UnityEngine.Object.FindObjectsByType<EffectMesh>(FindObjectsSortMode.None))
        {
            foreach (var effectMeshObject in effectMesh.EffectMeshObjects)
            {
                if (effectMeshObject.Key == null ||
                    effectMeshObject.Value == null ||
                    effectMeshObject.Key.Label != MRUKAnchor.SceneLabels.GLOBAL_MESH ||
                    effectMeshObject.Value.effectMeshGO == null)
                {
                    continue;
                }

                MeshFilter meshFilter = effectMeshObject.Value.effectMeshGO.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    return meshFilter.transform;
                }
            }
        }

        return null;
    }

    private void HandleRoomMeshLoaded(MeshFilter mf)
    {
        // Only if enabled and not busy

        currentRoomMeshReference = mf != null ? mf.transform : null;
        Debug.Log("RoomMesh loaded event received.");
        HandleTargetMeshLoaded(mf, "Room Mesh");
    }

    private void HandleEffectMeshLoaded(MeshFilter mf)
    {
        if (effectMeshEventTarget != null)
        {
            effectMeshEventTarget.OnGlobalMeshLoadComplete.RemoveListener(HandleEffectMeshLoaded);
        }

        currentEffectMeshReference = mf != null ? mf.transform : null;
        Debug.Log("EffectMesh global mesh loaded event received.");
        HandleTargetMeshLoaded(mf, "Effect Mesh Global Mesh");
    }

    private void HandleEffectMeshRendererLoaded(MeshFilter mf)
    {
        currentEffectMeshReference = mf != null ? mf.transform : null;
        HideMeshRenderers(currentEffectMeshReference);
    }

    private void HandleTargetMeshLoaded(MeshFilter mf, string targetName)
    {
        cloudTarget = ExtractMeshPointCloud(mf, applySelection: false);
        if (cloudTarget.Count == 0)
        {
            Debug.LogError($"{targetName} is empty!", this);
            return;
        }


        HideMeshRenderers(mf != null ? mf.transform : null);

        MarkReady();
    }

    private void HideExistingEffectMeshRenderers(EffectMesh effectMesh)
    {
        if (effectMesh == null)
        {
            return;
        }

        foreach (var effectMeshObject in effectMesh.EffectMeshObjects)
        {
            if (effectMeshObject.Value?.effectMeshGO != null)
            {
                HideMeshRenderers(effectMeshObject.Value.effectMeshGO.transform);
            }
        }
    }

    private void HideMeshRenderers(Transform meshReference)
    {
        if (meshReference == null)
        {
            return;
        }

        MeshRenderer[] meshRenderers = meshReference.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < meshRenderers.Length; i++)
        {
            meshRenderers[i].enabled = false;
        }
    }

    private void WarnIfEffectMeshDoesNotIncludeGlobalMesh(EffectMeshEvent effectMeshEvent)
    {
        EffectMesh effectMesh = effectMeshEvent.GetComponent<EffectMesh>();
        if (effectMesh != null && !effectMesh.Labels.HasFlag(MRUKAnchor.SceneLabels.GLOBAL_MESH))
        {
            Debug.LogWarning("[AllInOneRegistration] Effect Mesh target should include the Global Mesh label.", this);
        }
    }

    private List<Vector3> ExtractMeshPointCloud(MeshFilter mf, bool applySelection)
    {
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogWarning("Cannot extract RoomMesh points: MeshFilter or sharedMesh is null.");
            return new List<Vector3>();
        }
        Mesh mesh = mf.sharedMesh;
        Transform t = mf.transform;
        Vector3[] vertices = mesh.vertices;
        Matrix4x4 localToWorld = t.localToWorldMatrix;

        if (!applySelection || !isBoxSelection)
        {
            List<Vector3> points = new List<Vector3>(vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                points.Add(localToWorld.MultiplyPoint3x4(vertices[i]));
            }
            Debug.Log($"Extracted {points.Count} points from {mf.name}.");
            return points;
        }

        Vector3 boxHalfExtents = boxSize * 0.5f;
        Vector3 boxCenter = ResolveSelectionCenter(t, localToWorld, vertices);
        List<Vector3> selectedPoints = new List<Vector3>(vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 worldPoint = localToWorld.MultiplyPoint3x4(vertices[i]);
            if (IsPointInsideSelection(worldPoint, boxCenter, boxHalfExtents))
            {
                selectedPoints.Add(worldPoint);
            }
        }

        int filteredOut = vertices.Length - selectedPoints.Count;
        Debug.Log($"[AllInOneRegistration] Mesh selection kept {selectedPoints.Count}/{vertices.Length} points, filtered out {filteredOut}. Shape={selectionGizmoShape}, CenterMode={GetSelectionCenterModeLabel()}, Center={boxCenter}, Size={boxSize}.");
        return selectedPoints;
    }

    private async void StartAsyncPointFromScan()
    {
        if (pointCloudTargetFile == null)
        {
            Debug.LogError("pointCloudTargetPath is not assigned!", this);
            enabled = false; return;
        }
        else
        {
            await LoadAnchorAndPointCloudAsync();
        }

    }

    private async Task LoadAnchorAndPointCloudAsync()
    {
        string uuidPath = Path.Combine(Application.persistentDataPath, AnchorUuidFile);
        if (!File.Exists(uuidPath))
        {
            Debug.LogError("[PointCloudViewer] Anchor UUID file not found, cannot load point cloud.");
            return;
        }

        Guid uuid = Guid.Parse(File.ReadAllText(uuidPath));
        List<OVRSpatialAnchor.UnboundAnchor> unboundAnchors = new();
        var loadResult = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new List<Guid> { uuid }, unboundAnchors, null);

        if (loadResult.Status != OVRAnchor.FetchResult.Success || unboundAnchors.Count == 0)
        {
            Debug.LogError("[PointCloudViewer] Failed to load unbound anchor.");
            return;
        }

        var unboundAnchor = unboundAnchors[0];
        anchorHolderCopy = Instantiate(anchorHolder);
        currentAnchor = anchorHolderCopy != null ? anchorHolderCopy.AddComponent<OVRSpatialAnchor>() : new GameObject("PointCloudAnchor").AddComponent<OVRSpatialAnchor>();
        unboundAnchor.BindTo(currentAnchor);

        bool localized = await currentAnchor.WhenLocalizedAsync();
        if (!localized)
        {
            Debug.LogError("[PointCloudViewer] Anchor failed to localize, cannot align point cloud.");

            return;
        }

        Debug.Log($"[PointCloudViewer] Anchor localized at: {currentAnchor.transform.position}");

        // currentAnchor.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 0.2f;

        LoadPointCloud();
        MarkReady();
    }

    private async Task<bool> EnsureCurrentAnchorLoadedAsync()
    {
        if (currentAnchor != null)
        {
            return true;
        }

        string uuidPath = Path.Combine(Application.persistentDataPath, AnchorUuidFile);
        if (!File.Exists(uuidPath))
        {
            Debug.LogWarning($"[AllInOneRegistration] Anchor UUID file not found: {uuidPath}");
            return false;
        }

        if (!Guid.TryParse(File.ReadAllText(uuidPath), out Guid uuid))
        {
            Debug.LogWarning($"[AllInOneRegistration] Failed to parse anchor UUID from: {uuidPath}");
            return false;
        }

        List<OVRSpatialAnchor.UnboundAnchor> unboundAnchors = new();
        var loadResult = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new List<Guid> { uuid }, unboundAnchors, null);
        if (loadResult.Status != OVRAnchor.FetchResult.Success || unboundAnchors.Count == 0)
        {
            Debug.LogWarning($"[AllInOneRegistration] Failed to load unbound anchor from UUID file. Status={loadResult.Status}");
            return false;
        }

        if (anchorHolderCopy == null && anchorHolder != null)
        {
            anchorHolderCopy = Instantiate(anchorHolder);
        }

        GameObject anchorObject = anchorHolderCopy != null ? anchorHolderCopy : new GameObject("PointCloudAnchor");
        currentAnchor = anchorObject.GetComponent<OVRSpatialAnchor>();
        if (currentAnchor == null)
        {
            currentAnchor = anchorObject.AddComponent<OVRSpatialAnchor>();
        }

        unboundAnchors[0].BindTo(currentAnchor);
        bool localized = await currentAnchor.WhenLocalizedAsync();
        if (!localized)
        {
            Debug.LogWarning("[AllInOneRegistration] Anchor loaded but failed to localize.");
            currentAnchor = null;
            return false;
        }

        Debug.Log($"[AllInOneRegistration] Anchor localized for saving at: {currentAnchor.transform.position}");
        return true;
    }

    private async Task<Transform> ResolveAlignmentReferenceAsync()
    {
        switch (alignmentReferenceMode)
        {
            case AlignmentReferenceMode.RoomMesh:
                if (targetFormat != TargetFormat.roomMesh)
                {
                    Debug.LogWarning("[AllInOneRegistration] RoomMesh alignment reference requires Target Format = Room Mesh (Legacy).");
                    return null;
                }

                if (currentRoomMeshReference == null)
                {
                    Debug.LogWarning("[AllInOneRegistration] RoomMesh alignment reference requested, but no room mesh has been loaded yet.");
                    return null;
                }

                return currentRoomMeshReference;

            case AlignmentReferenceMode.EffectMesh:
                return await ResolveCurrentEffectMeshReferenceAsync();

            case AlignmentReferenceMode.SpatialAnchor:
            default:
                return await EnsureCurrentAnchorLoadedAsync() ? currentAnchor.transform : null;
        }
    }

    private string GetAlignmentReferenceLabel()
    {
        return alignmentReferenceMode switch
        {
            AlignmentReferenceMode.RoomMesh => "RoomMesh",
            AlignmentReferenceMode.EffectMesh => "EffectMesh",
            _ => "SpatialAnchor"
        };
    }

    private static string NormalizeAlignmentReferenceLabel(string referenceLabel)
    {
        if (string.Equals(referenceLabel, "CurrentRoomMesh", StringComparison.OrdinalIgnoreCase))
        {
            return "RoomMesh";
        }

        if (string.Equals(referenceLabel, "CurrentEffectMesh", StringComparison.OrdinalIgnoreCase))
        {
            return "EffectMesh";
        }

        return referenceLabel;
    }

    private void OnDestroy()
    {
        if (roomMeshEventTarget != null)
        {
            roomMeshEventTarget.OnRoomMeshLoadCompleted.RemoveListener(HandleRoomMeshLoaded);
        }

        if (effectMeshEventTarget != null)
        {
            effectMeshEventTarget.OnGlobalMeshLoadComplete.RemoveListener(HandleEffectMeshRendererLoaded);
            effectMeshEventTarget.OnGlobalMeshLoadComplete.RemoveListener(HandleEffectMeshLoaded);
        }
    }

    private bool ExtractSourcePointCloudFromGS()
    {
        if (useBakedData)
        {
            cloudSource = ExtractPointCloudFromBakedData();
            MarkReady();

            if (cloudSource.Count == 0)
            {
                Debug.LogError("Extracted baked point cloud is empty! Check if BakedData exists.", this);
                return false;
            }

            Debug.Log($"Extracted {cloudSource.Count} points from BakedData as the source point cloud.");
            return true;
        }

        GraphicsBuffer gpuPosBuffer = gaussianSourceRenderer.GetGpuPosData();
        if (gpuPosBuffer == null)
        {
            Debug.LogError("Could not get GPU Position Buffer from Gaussian Source Renderer.", this);
            return false;
        }
        cloudSource = ExtractPointCloudFromGraphicsBuffer(gpuPosBuffer, gaussianSourceRenderer.transform);
        MarkReady();

        if (cloudSource.Count == 0)
        {
            Debug.LogError("Extracted source (3DGS) point cloud is empty!", this);
            return false;
        }

        Debug.Log($"Extracted {cloudSource.Count} points from 3DGS GPU data as the source point cloud.");
        return true;
    }

    public void SetUseBakedData(bool value)
    {
        useBakedData = value;
    }


    private bool ExtractSourcePointCloudFromMesh()
    {
        cloudSource = ExtractMeshPointCloud(sourceMesh, applySelection: true);
        MarkReady();
        if (cloudSource.Count == 0)
        {
            Debug.LogError("Extracted source (mesh) point cloud is empty!", this);
            return false;
        }
        Debug.Log($"Extracted {cloudSource.Count} points from mesh as the source point cloud.");
        return true;
    }

    private List<Vector3> ExtractPointCloudFromBakedData()
    {
        if (gaussianSourceRenderer.m_BakedData == null ||
            gaussianSourceRenderer.m_BakedData.PointCount == 0)
        {
            Debug.LogError("BakedData is null or empty! Please bake the data first.", this);
            return new List<Vector3>();
        }

        var bakedData = gaussianSourceRenderer.m_BakedData;
        Vector3[] bakedPositions = bakedData.positions;
        Transform objTransform = gaussianSourceRenderer.transform;

        positions = null;
        selectedGSIndices.Clear();

        Debug.Log($"Using BakedData with {bakedPositions.Length} filtered points.");

        Matrix4x4 localToWorld = objTransform.localToWorldMatrix;
        if (!isBoxSelection)
        {
            List<Vector3> allPoints = new List<Vector3>(bakedPositions.Length);
            foreach (Vector3 localPos in bakedPositions)
            {
                allPoints.Add(localToWorld.MultiplyPoint3x4(localPos));
            }

            Debug.Log($"[AllInOneRegistration] Source selection disabled for BakedData. Kept {bakedPositions.Length}/{bakedPositions.Length} points, filtered out 0.");
            return allPoints;
        }

        Vector3 boxHalfExtents = boxSize * 0.5f;
        Vector3 boxCenter = ResolveSelectionCenter(objTransform, localToWorld, bakedPositions);
        List<Vector3> selectedPoints = new List<Vector3>(bakedPositions.Length);

        for (int i = 0; i < bakedPositions.Length; i++)
        {
            Vector3 worldPoint = localToWorld.MultiplyPoint3x4(bakedPositions[i]);
            if (IsPointInsideSelection(worldPoint, boxCenter, boxHalfExtents))
            {
                selectedPoints.Add(worldPoint);
            }
        }

        int filteredOut = bakedPositions.Length - selectedPoints.Count;
        Debug.Log($"[AllInOneRegistration] BakedData selection kept {selectedPoints.Count}/{bakedPositions.Length} points, filtered out {filteredOut}. Shape={selectionGizmoShape}, CenterMode={GetSelectionCenterModeLabel()}, Center={boxCenter}, Size={boxSize}.");
        return selectedPoints;
    }

    private List<Vector3> ExtractPointCloudFromGraphicsBuffer(GraphicsBuffer buffer, Transform objTransform)
    {
        positions = new float3[splatCount];
        buffer.GetData(positions);
        Matrix4x4 localToWorld = objTransform.localToWorldMatrix;

        if (!isBoxSelection)
        {
            List<Vector3> pointsAll = new List<Vector3>(splatCount);
            for (int i = 0; i < splatCount; i++)
            {
                Vector3 worldPoint = localToWorld.MultiplyPoint3x4((Vector3)positions[i]);
                pointsAll.Add(worldPoint);
            }
            Debug.Log($"[AllInOneRegistration] Source selection disabled for GPU points. Kept {splatCount}/{splatCount} points, filtered out 0.");
            return pointsAll;
        }

        Vector3 boxHalfExtents = boxSize * 0.5f;
        Vector3 boxCenter = ResolveSelectionCenter(objTransform, localToWorld, positions, splatCount);

        List<Vector3> pointsSelected = new List<Vector3>(splatCount);
        selectedGSIndices.Clear();  

        for (int i = 0; i < splatCount; i++)
        {
            Vector3 worldPoint = localToWorld.MultiplyPoint3x4((Vector3)positions[i]);
            if (IsPointInsideSelection(worldPoint, boxCenter, boxHalfExtents))
            {
                pointsSelected.Add(worldPoint);
                selectedGSIndices.Add(i);
            }
        }

        int filteredOut = splatCount - pointsSelected.Count;
        Debug.Log($"[AllInOneRegistration] GPU selection kept {pointsSelected.Count}/{splatCount} points, filtered out {filteredOut}. Shape={selectionGizmoShape}, CenterMode={GetSelectionCenterModeLabel()}, Center={boxCenter}, Size={boxSize}.");
        return pointsSelected;
    }

    private bool IsPointInsideSelection(Vector3 worldPoint, Vector3 center, Vector3 halfExtents)
    {
        Vector3 delta = worldPoint - center;
        if (selectionGizmoShape == SelectionGizmoShape.Box)
        {
            return Mathf.Abs(delta.x) <= halfExtents.x &&
                   Mathf.Abs(delta.y) <= halfExtents.y &&
                   Mathf.Abs(delta.z) <= halfExtents.z;
        }

        float hx = Mathf.Max(halfExtents.x, 1e-6f);
        float hy = Mathf.Max(halfExtents.y, 1e-6f);
        float hz = Mathf.Max(halfExtents.z, 1e-6f);
        float nx = delta.x / hx;
        float ny = delta.y / hy;
        float nz = delta.z / hz;
        return nx * nx + ny * ny + nz * nz <= 1f;
    }

    private string GetSelectionCenterModeLabel()
    {
        if (selectionCenterOverride != null)
        {
            return "OverrideTransform";
        }

        return usePointCloudCentroidAsSelectionCenter ? "PointCloudCentroid" : "SourceTransform";
    }

    private Vector3 ResolveSelectionCenter(Transform objTransform, Matrix4x4 localToWorld, Vector3[] localPositions)
    {
        if (selectionCenterOverride != null)
        {
            return selectionCenterOverride.position + selectionCenterOffset;
        }

        if (usePointCloudCentroidAsSelectionCenter && localPositions != null && localPositions.Length > 0)
        {
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < localPositions.Length; i++)
            {
                sum += localToWorld.MultiplyPoint3x4(localPositions[i]);
            }
            return (sum / localPositions.Length) + selectionCenterOffset;
        }

        return objTransform.position + selectionCenterOffset;
    }

    private Vector3 ResolveSelectionCenter(Transform objTransform, Matrix4x4 localToWorld, float3[] localPositions, int count)
    {
        if (selectionCenterOverride != null)
        {
            return selectionCenterOverride.position + selectionCenterOffset;
        }

        if (usePointCloudCentroidAsSelectionCenter && localPositions != null && count > 0)
        {
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                sum += localToWorld.MultiplyPoint3x4((Vector3)localPositions[i]);
            }
            return (sum / count) + selectionCenterOffset;
        }

        return objTransform.position + selectionCenterOffset;
    }

    private Transform GetSourceTransform()
    {
        return sourceType == SourceFormat.GaussianSplating ? gaussianSourceRenderer?.transform : sourceMesh?.transform;
    }

    private Vector3 ResolveSelectionCenterForGizmo()
    {
        if (selectionCenterOverride != null)
        {
            return selectionCenterOverride.position + selectionCenterOffset;
        }

        Transform sourceTransform = GetSourceTransform();
        if (sourceTransform == null)
        {
            return transform.position + selectionCenterOffset;
        }

        if (usePointCloudCentroidAsSelectionCenter)
        {
            if (sourceType == SourceFormat.Mesh && sourceMesh != null && sourceMesh.sharedMesh != null)
            {
                return ResolveSelectionCenter(sourceTransform, sourceTransform.localToWorldMatrix, sourceMesh.sharedMesh.vertices);
            }

            if (sourceType == SourceFormat.GaussianSplating && useBakedData)
            {
                var bakedData = gaussianSourceRenderer.m_BakedData;
                if (bakedData != null && bakedData.positions != null && bakedData.positions.Length > 0)
                {
                    return ResolveSelectionCenter(sourceTransform, sourceTransform.localToWorldMatrix, bakedData.positions);
                }
            }

            if (positions != null && positions.Length > 0)
            {
                return ResolveSelectionCenter(sourceTransform, sourceTransform.localToWorldMatrix, positions, positions.Length);
            }
        }

        return sourceTransform.position + selectionCenterOffset;
    }




    private void LoadPointCloud()
    {
        string path = Path.Combine(Application.persistentDataPath, pointCloudTargetFile);
        if (!File.Exists(path))
        {
            Debug.LogError("[PointCloudViewer] Point cloud file not found: " + path);
            return;
        }

        //List<Vector3> points = new();
        using (BinaryReader reader = new(File.Open(path, FileMode.Open)))
        {
            int count = reader.ReadInt32();
            Debug.Log($"[PointCloudViewer] Point count in file: {count}");

            for (int i = 0; i < count; i++)
            {
                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                float z = reader.ReadSingle();
                Vector3 localPoint = new(x, y, z);
                Vector3 worldPoint = currentAnchor.transform.TransformPoint(localPoint);
                cloudTarget.Add(worldPoint);
            }
        }

        InitializeBuffer(cloudTarget.Count);

        pointDataList.Clear();
        foreach (var p in cloudTarget)
        {
            pointDataList.Add(new PointDataVFX { position = p });
        }

        pointDataBuffer.SetData(pointDataList);
        pointCloudVFX.SetGraphicsBuffer(PointBufferID, pointDataBuffer);
        pointCloudVFX.SetInt(PointCountID, pointDataList.Count);

        Debug.Log($"[PointCloudViewer] Loaded and displayed {pointDataList.Count} points aligned with Anchor.");

        if (isTargetPointVisualization)
            pointCloudVFX.Play(); 
    }

    private void InitializeBuffer(int count)
    {
        pointDataBuffer?.Release();
        pointDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, pointDataStride);
    }

    private async void RegistrationProcess()
    {
        if (isRegistrationRunning)
        {
            Debug.LogWarning("Registration is already in progress.");
            return;
        }
        if (cloudSource.Count == 0)
        {
            Debug.LogError("Cannot start registration: Source (3DGS) point cloud is empty.", this);
            return;
        }

        if (cloudTarget.Count == 0)
        {
            Debug.LogError("Cannot start registration: scan point cloud is empty.", this);
            return;
        }

        isRegistrationRunning = true;

        // Convert lists to flat float arrays
        float[] targetArray = ConvertToFloatArray(cloudTarget); // Target = refPoints
        float[] sourceArray = ConvertToFloatArray(cloudSource); // Source = tgtPoints
        Matrix4x4 deltaMatrix = Matrix4x4.identity;
        R = Quaternion.identity;
        t = Vector3.zero;
        s = 1f;
        hasLatestTransformData = false;


        if (roughAligner == RoughAlignMode.Teaser)
        {
            GICPResult result = RunTeaserGICP(
            sourceArray, sourceArray.Length,
            targetArray, targetArray.Length,
            // General Params
            useTeaser,
            useGICP,
            // TEASER++ Params
            estimateScaling,
            voxelSizeTeaser,
            maxIterationsTeaser,
            noiseBoundTeaser,
            normalRadiusTeaser,
            fpfhRadiusTeaser,
            matcherRatioTeaser,
            rotationCostThresholdTeaser,
            // VGICP Params
            useVoxelGICP,
            voxelSizeGICP,
            modelName,
            maxIterGICP,
            voxelResolutionGICP,
            maxCorrespondenceDistanceGICP,
            correspondenceRandomnessGICP,
            epsilonGICP);
            Debug.Log(result.message);
            Matrix4x4 T_result = new Matrix4x4();
            for (int i = 0; i < 16; ++i)
            {
                T_result[i] = result.matrix[i];
            }

            Debug.Log("Transformation matrix from DLL:\n" + T_result);
            s = (float.IsFinite(result.scale) && result.scale > 0f) ? result.scale : 1f;
            deltaMatrix = Matrix4x4.TRS(T_result.GetColumn(3), T_result.rotation, Vector3.one * s);
            R = T_result.rotation;
            t = T_result.GetColumn(3);
            Debug.Log($"[AllInOneRegistration] Estimated scale from DLL: {s}");

        }

        Transform sourceTransform = GetSourceTransform();
        if (sourceTransform == null)
        {
            Debug.LogError("Cannot apply registration result: source transform is missing.", this);
            isRegistrationRunning = false;
            return;
        }

        Vector3 initialPosition = sourceTransform.position;
        Quaternion initialRotation = sourceTransform.rotation;
        Vector3 initialLocalScale = sourceTransform.localScale;
        latestInitialPosition = initialPosition;
        latestInitialRotation = initialRotation;
        latestInitialLocalScale = initialLocalScale;
        latestDeltaMatrix = roughAligner == RoughAlignMode.Teaser ? deltaMatrix : Matrix4x4.TRS(t, R, Vector3.one * s);
        hasLatestTransformData = true;

        sourceTransform.localScale = initialLocalScale * s;
        sourceTransform.rotation = R * initialRotation;
        sourceTransform.position = latestDeltaMatrix.MultiplyPoint3x4(initialPosition);
        hasCompletedRegistration = true;

        try
        {
            if (isSaving)
            {
                Transform referenceTransform = await ResolveAlignmentReferenceAsync();
                if (referenceTransform != null)
                {
                    SaveAlignmentResult(referenceTransform, GetAlignmentReferenceLabel());
                }
                else
                {
                    Debug.LogWarning($"[AllInOneRegistration] Skipped saving alignment because the {GetAlignmentReferenceLabel()} reference could not be resolved.");
                }
            }
        }
        finally
        {
            isRegistrationRunning = false;
            if (pointCloudVFX != null)
            {
                pointCloudVFX.Stop();
                pointCloudVFX.gameObject.SetActive(false);
            }

        }

    }

    float[] ConvertToFloatArray(List<Vector3> points)
    {
        float[] arr = new float[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            arr[i * 3] = points[i].x;
            arr[i * 3 + 1] = points[i].y;
            arr[i * 3 + 2] = points[i].z;
        }
        return arr;
    }

    private void MarkReady()
    {
        readyCount++;
        if (readyCount >= 2)
        {
            onBothReady?.Invoke();
        }
    }

    private void OnDrawGizmos()
    {
        if (!showSelectionGizmo || !isBoxSelection)
        {
            return;
        }

        Vector3 center = ResolveSelectionCenterForGizmo();
        Color previousColor = Gizmos.color;
        Matrix4x4 previousMatrix = Gizmos.matrix;

        Gizmos.color = selectionGizmoColor;
        if (selectionGizmoShape == SelectionGizmoShape.Box)
        {
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.DrawWireCube(center, boxSize);
        }
        else
        {
            Gizmos.matrix = Matrix4x4.TRS(center, Quaternion.identity, boxSize);
            Gizmos.DrawWireSphere(Vector3.zero, 0.5f);
        }

        Gizmos.matrix = previousMatrix;
        Gizmos.color = previousColor;
    }

}
