
//using Meta.XR.BuildingBlocks;
//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Threading.Tasks;
//using UnityEngine;

///// <summary>
///// All-in-one manager.
///// Responsibilities:
///// 1. Load and localize a shared spatial anchor when the scene starts.
///// 2. Read each managed object's alignment file after localization and move it to its final pose.
///// 3. Deactivate all managed objects after positioning.
///// 4. Listen for controller input to cycle through the prepared objects.
///// 5. Broadcast an event after each switch so other systems, such as VisualComparisonManager, know which object is active.
///// </summary>
//public class AnchorAndObjectManager : MonoBehaviour
//{
//    // Helper class for configuring objects and their corresponding alignment files in the Inspector.
//    [System.Serializable]
//    public class AlignableObject
//    {
//        [Tooltip("GameObject to position and switch, such as a GS renderer or mesh object.")]
//        public GameObject objectToManage;

//        [Tooltip("Unique alignment filename corresponding to the GameObject above.")]
//        public string alignmentFile;
//    }

//    [Header("Managed Objects")]
//    [Tooltip("Add every object to manage here and configure its alignment filename.")]
//    public List<AlignableObject> managedObjects;

//    [Header("Shared Anchor")]
//    [Tooltip("Filename that stores the shared anchor UUID.")]
//    [SerializeField]
//    private string anchorUuidFile = "anchor_uuid.txt";

//    [Tooltip("Simple prefab used to hold the loaded anchor component.")]
//    [SerializeField]
//    private GameObject anchorHolderPrefab;

//    [Header("OVR Controller Input")]
//    [Tooltip("Button used to switch objects.")]
//    public OVRInput.Button switchObjectButton = OVRInput.Button.One; // A button on the right Oculus controller
//    //public OVRInput.Button relocaliseObjectButton = OVRInput.Button.Two;
//    // Event broadcast that notifies other scripts when the active object changes.
//    public static event Action<GameObject> OnActiveObjectChanged;

//    public VisualComparisonManager comparisonManager;

//    // Public property that lets other scripts query the currently active object.
//    public GameObject CurrentActiveObject { get; private set; }

//    // Tracks the current index in the object cycle.
//    private int currentIndex = -1;

//    private Transform sharedAnchorTransform;

//    // Core flow 1: setup when the scene starts.
//    async void Start()
//    {
//        Debug.Log("--- AnchorAndObjectManager: setup started ---");

//        // Step 1: Load and localize the shared spatial anchor.
//        sharedAnchorTransform = await LoadSharedAnchorAsync();

//        if (sharedAnchorTransform == null)
//        {
//            Debug.LogError("Setup failed: the shared anchor could not be loaded and localized.");
//            return;
//        }

//        // Step 2: Position all managed objects.
//        PositionAllObjects(sharedAnchorTransform);

//        // Step 3: Broadcast an initial event indicating that no object is active.
//        OnActiveObjectChanged?.Invoke(null);

//        Debug.Log("--- AnchorAndObjectManager: setup complete --- All objects are positioned, hidden, and ready to switch.");
//    }

//    // Core flow 2: per-frame interaction.
//    void Update()
//    {
//        // Listen for the button press that switches objects.
//        if (OVRInput.GetDown(switchObjectButton))
//        {
//            //comparisonManager.ResetState();
//            CycleNextObject();
//        }

//        //if (OVRInput.GetDown(relocaliseObjectButton))
//        //{
//        //    PositionAllObjects(sharedAnchorTransform);
//        //}
//    }

//    /// <summary>
//    /// Load and localize the shared spatial anchor asynchronously.
//    /// </summary>
//    /// <returns>The localized anchor transform, or null on failure.</returns>
//    private async Task<Transform> LoadSharedAnchorAsync()
//    {
//        string uuidPath = Path.Combine(Application.persistentDataPath, anchorUuidFile);
//        if (!File.Exists(uuidPath))
//        {
//            Debug.LogError($"Anchor UUID file not found: {uuidPath}", this);
//            return null;
//        }

//        Guid anchorUuid;
//        try { anchorUuid = Guid.Parse(File.ReadAllText(uuidPath)); }
//        catch (Exception e)
//        {
//            Debug.LogError($"Failed to parse anchor UUID: {e.Message}", this);
//            return null;
//        }

//        var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
//        var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new List<Guid> { anchorUuid }, unboundAnchors, null);
//        if (result.Status != OVRAnchor.FetchResult.Success || unboundAnchors.Count == 0)
//        {
//            Debug.LogError($"Failed to load anchor data. UUID: {anchorUuid}, status: {result.Status}", this);
//            return null;
//        }

//        GameObject anchorGO = Instantiate(anchorHolderPrefab);
//        anchorGO.name = "SharedWorldAnchor";
//        var currentAnchor = anchorGO.AddComponent<OVRSpatialAnchor>();
//        unboundAnchors[0].BindTo(currentAnchor);

//        bool localized = await currentAnchor.WhenLocalizedAsync();
//        if (!localized)
//        {
//            Debug.LogError("Failed to localize the shared anchor.", this);
//            Destroy(anchorGO);
//            return null;
//        }

//        Debug.Log("Shared anchor localized successfully.");
//        return currentAnchor.transform;
//    }

//    /// <summary>
//    /// Position every managed object from its alignment file, then hide it.
//    /// </summary>
//    private void PositionAllObjects(Transform anchorTransform)
//    {
//        if (managedObjects == null || managedObjects.Count == 0) return;

//        foreach (var objInfo in managedObjects)
//        {
//            if (objInfo.objectToManage == null) continue;

//            // Read the alignment file assigned to this object.
//            string alignmentPath = Path.Combine(Application.persistentDataPath, objInfo.alignmentFile);
//            if (!File.Exists(alignmentPath))
//            {
//                Debug.LogError($"Alignment file '{objInfo.alignmentFile}' was not found; object '{objInfo.objectToManage.name}' will not be positioned.", objInfo.objectToManage);
//                continue;
//            }

//            string json = File.ReadAllText(alignmentPath);
//            PoseData savedPose = JsonUtility.FromJson<PoseData>(json);

//            // Calculate and apply the final world pose from the shared anchor and alignment data.
//            objInfo.objectToManage.transform.position = anchorTransform.TransformPoint(savedPose.position);
//            objInfo.objectToManage.transform.rotation = anchorTransform.rotation * savedPose.rotation;

//            // Deactivate the object after positioning until it is selected.
//            objInfo.objectToManage.SetActive(false);

//            Debug.Log($"Object '{objInfo.objectToManage.name}' was positioned and hidden.");
//        }
//    }

//    /// <summary>
//    /// Hide the current object, activate the next object, and broadcast the change.
//    /// </summary>
//    private void CycleNextObject()
//    {
//        if (managedObjects == null || managedObjects.Count == 0) return;

//        // Hide the currently active object, if any.
//        if (currentIndex != -1)
//        {
//            managedObjects[currentIndex].objectToManage.SetActive(false);
//        }

//        // Calculate the next object index.
//        currentIndex++;
//        if (currentIndex >= managedObjects.Count)
//        {
//            currentIndex = 0; // Wrap to the beginning of the list.
//        }

//        // Activate the next object.
//        var newActiveObject = managedObjects[currentIndex].objectToManage;
//        newActiveObject.SetActive(true);

//        // Update the public property and broadcast the event.
//        CurrentActiveObject = newActiveObject;
//        OnActiveObjectChanged?.Invoke(CurrentActiveObject);

//        Debug.Log($"Switched to: {newActiveObject.name}");
//    }

//    // Helper type for JSON deserialization.
//    [System.Serializable]
//    private class PoseData
//    {
//        public Vector3 position;
//        public Quaternion rotation;
//    }
//}



using Meta.XR.BuildingBlocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// All-in-one manager.
/// Responsibilities:
/// 1. Load and localize a shared spatial anchor when the scene starts.
/// 2. Read each managed object's alignment file after localization and move it to its final pose.
/// 3. Deactivate all managed objects after positioning.
/// 4. Listen for controller input to cycle through the prepared objects.
/// 5. Broadcast an event after each switch so other systems know which object is active.
/// 6. Optionally use the 1-5 order supplied by SceneOrderLoader; otherwise use the default order.
/// </summary>
public class AnchorAndObjectManager : MonoBehaviour
{
    // Helper class for configuring objects and their corresponding alignment files in the Inspector.
    [Serializable]
    public class AlignableObject
    {
        [Tooltip("GameObject to position and switch, such as a Gaussian Splat renderer or mesh object.")]
        public GameObject objectToManage;

        [Tooltip("Unique alignment filename corresponding to the GameObject above.")]
        public string alignmentFile;
    }

    [Header("受管物体列表")]
    [Tooltip("Add every object to manage here and configure its alignment filename.")]
    public List<AlignableObject> managedObjects;

    [Header("共享锚点配置")]
    [Tooltip("Filename that stores the shared anchor UUID.")]
    [SerializeField] private string anchorUuidFile = "anchor_uuid.txt";

    [Tooltip("Simple prefab used to hold the loaded anchor component.")]
    [SerializeField] private GameObject anchorHolderPrefab;

    [Header("OVR控制器输入")]
    [Tooltip("Button used to switch objects.")]
    public OVRInput.Button switchObjectButton = OVRInput.Button.One; // A button on the right Oculus controller
    public OVRInput.Button relocaliseObjectButton = OVRInput.Button.Two;

    [Header("外部顺序控制（1-5 索引，可选）")]
    [Tooltip("When assigned, use the sceneOrder values (1-5) supplied by this loader as the activation order.")]
    public SceneOrderLoader sceneOrderLoader;

    // Event broadcast that notifies other scripts when the active object changes.
    public static event Action<GameObject> OnActiveObjectChanged;

    // Public property that lets other scripts query the currently active object.
    public GameObject CurrentActiveObject { get; private set; }

    // Index of the current entry in managedObjects.
    private int currentIndex = -1;

    // Zero-based activation order converted from sceneOrderLoader; for example, [3,1,5,2,4] becomes [2,0,4,1,3].
    private List<int> activationOrder = null;
    private int orderPtr = -1; // Current position in activationOrder

    private Transform sharedAnchorTransform;

    // Core flow 1: setup when the scene starts.
    async void Start()
    {
        Debug.Log("--- AnchorAndObjectManager: 设置阶段开始 ---");

        // Step 1: Load and localize the shared spatial anchor.
        sharedAnchorTransform = await LoadSharedAnchorAsync();
        if (sharedAnchorTransform == null)
        {
            Debug.LogError("设置失败：共享锚点未能成功加载和定位。流程终止。");
            return;
        }

        // Step 2: Position all managed objects.
        PositionAllObjects(sharedAnchorTransform);

        // Step 3: Convert SceneOrderLoader's one-based order to zero-based indices.
        PrepareActivationOrder();

        // Step 4: Broadcast an initial event indicating that no object is active.
        OnActiveObjectChanged?.Invoke(null);

        Debug.Log("--- AnchorAndObjectManager: 设置阶段完成 --- 所有物体均已定位并隐藏，准备就绪可供切换。");
    }

    // Core flow 2: per-frame interaction.
    void Update()
    {
        if (OVRInput.GetDown(switchObjectButton))
        {
            CycleNextObject();
        }

        if (OVRInput.GetDown(relocaliseObjectButton))
        {
            ReloclisedAllObjects(sharedAnchorTransform);
        }
    }

    /// <summary>
    /// Load and localize the shared spatial anchor asynchronously.
    /// </summary>
    private async Task<Transform> LoadSharedAnchorAsync()
    {
        string uuidPath = Path.Combine(Application.persistentDataPath, anchorUuidFile);
        if (!File.Exists(uuidPath))
        {
            Debug.LogError($"锚点UUID文件未找到: {uuidPath}", this);
            return null;
        }

        Guid anchorUuid;
        try { anchorUuid = Guid.Parse(File.ReadAllText(uuidPath)); }
        catch (Exception e)
        {
            Debug.LogError($"解析锚点UUID失败: {e.Message}", this);
            return null;
        }

        var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
        var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new List<Guid> { anchorUuid }, unboundAnchors, null);
        if (result.Status != OVRAnchor.FetchResult.Success || unboundAnchors.Count == 0)
        {
            Debug.LogError($"加载锚点数据失败，UUID: {anchorUuid}, 状态: {result.Status}", this);
            return null;
        }

        GameObject anchorGO = Instantiate(anchorHolderPrefab);
        anchorGO.name = "SharedWorldAnchor";
        var currentAnchor = anchorGO.AddComponent<OVRSpatialAnchor>();
        unboundAnchors[0].BindTo(currentAnchor);

        bool localized = await currentAnchor.WhenLocalizedAsync();
        if (!localized)
        {
            Debug.LogError("共享锚点定位失败！", this);
            Destroy(anchorGO);
            return null;
        }

        Debug.Log("共享锚点成功定位。");
        return currentAnchor.transform;
    }

    /// <summary>
    /// Position every managed object from its alignment file, then hide it.
    /// </summary>
    private void PositionAllObjects(Transform anchorTransform)
    {
        if (managedObjects == null || managedObjects.Count == 0) return;

        foreach (var objInfo in managedObjects)
        {
            if (objInfo.objectToManage == null) continue;

            string alignmentPath = Path.Combine(Application.persistentDataPath, objInfo.alignmentFile);
            if (!File.Exists(alignmentPath))
            {
                Debug.LogError($"对齐文件 '{objInfo.alignmentFile}' 未找到，物体 '{objInfo.objectToManage.name}' 将不会被定位。", objInfo.objectToManage);
                continue;
            }

            string json = File.ReadAllText(alignmentPath);
            PoseData savedPose = JsonUtility.FromJson<PoseData>(json);

            ApplyPoseRelativeToAnchor(objInfo.objectToManage, anchorTransform, savedPose);

            objInfo.objectToManage.SetActive(false);
            Debug.Log($"物体 '{objInfo.objectToManage.name}' 已被预先定位并隐藏。");
        }
    }


    private void ReloclisedAllObjects(Transform anchorTransform)
    {
        if (managedObjects == null || managedObjects.Count == 0) return;

        foreach (var objInfo in managedObjects)
        {
            if (objInfo.objectToManage == null) continue;

            string alignmentPath = Path.Combine(Application.persistentDataPath, objInfo.alignmentFile);
            if (!File.Exists(alignmentPath))
            {
                Debug.LogError($"对齐文件 '{objInfo.alignmentFile}' 未找到，物体 '{objInfo.objectToManage.name}' 将不会被定位。", objInfo.objectToManage);
                continue;
            }

            string json = File.ReadAllText(alignmentPath);
            PoseData savedPose = JsonUtility.FromJson<PoseData>(json);

            ApplyPoseRelativeToAnchor(objInfo.objectToManage, anchorTransform, savedPose);

            //objInfo.objectToManage.SetActive(false);
            Debug.Log($"物体 '{objInfo.objectToManage.name}' 已被预先定位并隐藏。");
        }
    }

    private static void ApplyPoseRelativeToAnchor(GameObject targetObject, Transform anchorTransform, PoseData savedPose)
    {
        if (targetObject == null || anchorTransform == null || savedPose == null)
        {
            return;
        }

        Transform target = targetObject.transform;
        target.SetParent(anchorTransform, false);
        target.localPosition = savedPose.position;
        target.localRotation = savedPose.rotation;
        if (savedPose.localScale.sqrMagnitude > 1e-8f)
        {
            target.localScale = savedPose.localScale;
        }
    }

    /// <summary>
    /// Convert SceneOrderLoader's 1-5 list to zero-based indices and validate the bounds.
    /// Keep activationOrder null and use the default order when no valid list is available.
    /// </summary>
    private void PrepareActivationOrder()
    {
        activationOrder = null;
        orderPtr = -1;

        if (sceneOrderLoader == null || sceneOrderLoader.sceneOrder == null || sceneOrderLoader.sceneOrder.Count == 0)
        {
            Debug.Log("[AnchorAndObjectManager] 未提供 sceneOrder（或为空），将使用默认顺序。");
            return;
        }

        var list = new List<int>(sceneOrderLoader.sceneOrder.Count);
        foreach (var raw in sceneOrderLoader.sceneOrder)
        {
            // Convert the one-based 1-5 value to a zero-based 0-4 index.
            int idx = raw - 1;

            if (idx < 0 || idx >= managedObjects.Count)
            {
                Debug.LogWarning($"[AnchorAndObjectManager] sceneOrder 中的值 {raw} 超出受管物体范围（0..{managedObjects.Count - 1}），已跳过。");
                continue;
            }

            if (managedObjects[idx] == null || managedObjects[idx].objectToManage == null)
            {
                Debug.LogWarning($"[AnchorAndObjectManager] sceneOrder 值 {raw} 对应物体为空，已跳过。");
                continue;
            }

            list.Add(idx);
        }

        if (list.Count == 0)
        {
            Debug.LogWarning("[AnchorAndObjectManager] 有效顺序为 0，回退默认顺序。");
            return;
        }

        activationOrder = list;
        orderPtr = -1;
        Debug.Log($"[AnchorAndObjectManager] 激活顺序（0基）= [{string.Join(",", activationOrder)}]");
    }

    /// <summary>
    /// Hide the current object, activate the next object using activationOrder or the default order, and broadcast the change.
    /// </summary>
    private void CycleNextObject()
    {
        if (managedObjects == null || managedObjects.Count == 0) return;

        // Hide the currently active object, if any.
        if (currentIndex != -1)
            managedObjects[currentIndex].objectToManage.SetActive(false);

        // Select the next index.
        if (activationOrder != null && activationOrder.Count > 0)
        {
            orderPtr++;
            if (orderPtr >= activationOrder.Count) orderPtr = 0;
            currentIndex = activationOrder[orderPtr];
        }
        else
        {
            currentIndex++;
            if (currentIndex >= managedObjects.Count) currentIndex = 0;
        }

        // Activate the new object.
        var newActiveObject = managedObjects[currentIndex].objectToManage;
        newActiveObject.SetActive(true);

        // Update the property and broadcast the change.
        CurrentActiveObject = newActiveObject;
        OnActiveObjectChanged?.Invoke(CurrentActiveObject);

        Debug.Log($"切换到: {newActiveObject.name} (index={currentIndex})");
    }

    // Helper type for JSON deserialization.
    [Serializable]
    private class PoseData
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 localScale;
    }
}
