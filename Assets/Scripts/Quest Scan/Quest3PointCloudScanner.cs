using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks; // Required for asynchronous operations
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.VFX;
using Meta.XR;
using Meta.XR.BuildingBlocks;

namespace Appletea.Dev.PointCloud
{
    /// <summary>
    /// Point-cloud scanner optimized for Meta Quest 3.
    /// Includes real-time edge rejection and asynchronous high-quality denoising before saving.
    /// </summary>
    public class Quest3PointCloudScanner : MonoBehaviour
    {
        public enum ScannerMode { Scan, Load }
        public enum Density { low = 32, medium = 64, high = 128, vHigh = 256, ultra = 512 }

        [SerializeField] private ScannerMode scannerMode = ScannerMode.Scan;

        [SerializeField] private VisualEffect pointCloudVFX;
        [SerializeField] private EnvironmentRaycastManager raycastManager;
        [SerializeField] private Camera mainCamera;
        [SerializeField] private GameObject anchorHolder;

        [SerializeField] private Density density = Density.medium;
        [SerializeField] private float maxScanDistance = 5f;
        [SerializeField] private float scanInterval = 0.2f;

        [Tooltip("VFX buffer upload interval in seconds. Higher values reduce frame hitches.")]
        [Range(0.05f, 1.0f)]
        [SerializeField] private float vfxRefreshInterval = 0.25f;
        [Tooltip("Hard cap for points uploaded to GPU each refresh.")]
        [SerializeField] private int hardUploadPointCap = 80000;
        [Tooltip("Maximum raycasts processed per scan tick. Density will be sampled with stride when exceeded.")]
        [SerializeField] private int maxRaycastsPerTick = 20000;

        [Tooltip("Real-time edge detection threshold in meters. Adjacent pixels whose depth difference exceeds this value are rejected as noise. Recommended: 0.05-0.1.")]
        [SerializeField] private float edgeThreshold = 0.05f; 

        [Tooltip("Run a second denoising pass before saving. This takes a few seconds but can significantly improve point-cloud quality.")]
        [SerializeField] private bool enablePostDenoising = true;
        [Tooltip("Skip post denoising when point count exceeds this limit to avoid long stalls.")]
        [SerializeField] private int maxPointsForPostDenoising = 250000;
        
        [Tooltip("Denoising radius in meters used when saving. Points are retained only when they have enough neighbors within this radius.")]
        [SerializeField] private float denoiseRadius = 0.05f; // 5cm
        
        [Tooltip("Minimum neighbor count used when saving. Points with fewer neighbors inside the radius are removed.")]
        [SerializeField] private int denoiseMinNeighbors = 4;

        [SerializeField] private float renderingRadius = 20f;
        [SerializeField] private int maxChunkCount = 15;

        [SerializeField] private int chunkSize = 1;
        [SerializeField] private int maxPointsPerChunk = 256;

        [Tooltip("When enabled, the first scan after each X press is forced into the keyframe buffer. Interval scans are sampled by motion/coverage gates.")]
        [SerializeField] private bool enableKeyframeCapture = true;
        [SerializeField] private int maxStoredKeyframes = 96;
        [SerializeField] private int minKeyframeValidHits = 512;
        [SerializeField] private float minKeyframeInterval = 0.35f;
        [SerializeField] private float maxKeyframeInterval = 1.5f;
        [SerializeField] private float minKeyframeTranslation = 0.10f;
        [SerializeField] private float minKeyframeRotationDegrees = 10f;
        [SerializeField] private float keyframeCoverageVoxelSize = 0.05f;
        [SerializeField] private float minNewCoverageRatio = 0.15f;

        [Tooltip("Optional GPU surfel fusion used only while saving. Disabled preserves the existing raw point-cloud save path.")]
        [SerializeField] private bool enableSurfelFusionOnSave = false;
        [SerializeField] private ComputeShader surfelFusionCompute;
        [SerializeField] private int maxSurfelCount = 300000;
        [SerializeField] private int surfelHashBucketCount = 262144;
        [SerializeField] private float surfelCellSize = 0.05f;
        [SerializeField] private float surfelRadius = 0.03f;
        [SerializeField] private float surfelMatchDistance = 0.045f;
        [SerializeField] private float surfelPointToPlaneDistance = 0.025f;
        [Range(0f, 89f)]
        [SerializeField] private float surfelNormalAngleThreshold = 35f;
        [Range(0f, 89f)]
        [SerializeField] private float surfelMaxGrazingAngle = 75f;
        [SerializeField] private float surfelNormalNeighborDistance = 0.08f;
        [SerializeField] private int surfelMaxBucketTraversal = 64;
        [SerializeField] private int newSurfelStride = 1;
        [SerializeField] private int minSurfelObservations = 2;
        [SerializeField] private float minSurfelWeight = 2f;
        [SerializeField] private float maxSurfelWeight = 20f;
        [SerializeField] private int minFusedPointCountForSave = 512;

        [SerializeField] private bool enableRenderPointBudget = true;
        [SerializeField] private int maxRenderPointCount = 50000;

        // Point storage and VFX buffers
        private ChunkManager pointsData;
        private GraphicsBuffer pointDataBuffer;
        private List<PointDataVFX> pointDataList = new();
        private int pointDataStride = System.Runtime.InteropServices.Marshal.SizeOf<PointDataVFX>();
        private readonly List<Vector3> visiblePointsBuffer = new();
        private readonly List<Vector3> allStoredPointsBuffer = new();
        private readonly List<Vector3> loadedPointCloud = new();
        private readonly List<Vector3> loadedRenderBuffer = new();

        // Runtime state
        private Coroutine scanCoroutine;
        private OVRSpatialAnchor currentAnchor;
        private float lastVfxRefreshTime;
        private bool anchorCreateListenerRegistered;
        private bool awaitingOwnAnchorCreate;
        private bool forceNextScanAsKeyframe;
        private int nextScanFrameId;
        private float lastAcceptedKeyframeTime = float.NegativeInfinity;
        private ScanFrame lastAcceptedKeyframe;
        
        [SerializeField] private SpatialAnchorCoreBuildingBlock anchorCoreBlock;

        [SerializeField] private string AnchorUuidFile = "anchor_uuid_part.txt";
        [SerializeField] private string PointCloudFile = "pointcloud_part.bytes";
        [SerializeField] private bool loadPointCloudOnStart = true;
        [SerializeField] private bool playVfxAfterLoad = true;

        private struct PointDataVFX { public Vector3 position; }

        private sealed class ScanFrame
        {
            public int frameId;
            public float timestamp;
            public Vector3 cameraPosition;
            public Quaternion cameraRotation;
            public int sampleSize;
            public int stride;
            public int width;
            public int height;
            public Vector3[] points;
            public Vector3[] viewDirections;
            public byte[] validMask;
            public int validCount;
            public Bounds bounds;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuScanSample
        {
            public Vector4 positionDepth;
            public Vector4 viewDirectionValid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuSurfel
        {
            public Vector4 positionRadius;
            public Vector4 normalWeight;
            public uint observationCount;
            public uint lastSeenFrame;
            public int next;
            public uint state;
            public int cellX;
            public int cellY;
            public int cellZ;
            public int padding;
        }

        private readonly List<ScanFrame> keyframes = new();
        private readonly HashSet<Vector3Int> keyframeCoverage = new();
        
        private static readonly int PointBufferID = Shader.PropertyToID("PointBuffer");
        private static readonly int PointCountID = Shader.PropertyToID("PointCount");

        private void Start()
        {
            if (pointCloudVFX == null)
            {
                Debug.LogError("[Quest3PointCloudScanner] Point cloud VFX is missing.");
                enabled = false;
                return;
            }

            if (scannerMode == ScannerMode.Scan && (raycastManager == null || mainCamera == null))
            {
                Debug.LogError("[Quest3PointCloudScanner] Scan mode requires raycastManager and Camera.");
                enabled = false;
                return;
            }

            pointsData = new ChunkManager(chunkSize, maxPointsPerChunk);
            InitializeBuffer(1024);
            pointCloudVFX.SetInt(PointCountID, 0);

            if (scannerMode == ScannerMode.Scan)
            {
                if (anchorCoreBlock == null)
                {
                    Debug.LogError("[Quest3PointCloudScanner] Scan mode requires SpatialAnchorCoreBuildingBlock.");
                    enabled = false;
                    return;
                }

                Debug.Log("[Quest3PointCloudScanner] Ready. Hold 'X' to scan, press 'B' to create anchor and save point cloud.");
            }
            else
            {
                Debug.Log("[Quest3PointCloudScanner] Load mode ready. Press 'X' to reload saved point cloud.");
                if (loadPointCloudOnStart)
                {
                    LoadSavedPointCloud();
                }
            }
        }

        private void Update()
        {
            if (scannerMode == ScannerMode.Load)
            {
                if (OVRInput.GetDown(OVRInput.RawButton.X))
                {
                    LoadSavedPointCloud();
                }

                return;
            }

            if (OVRInput.GetDown(OVRInput.RawButton.X))
            {
                BeginScanning();
            }

            if (OVRInput.GetDown(OVRInput.RawButton.B))
            {
                CreateAnchorAndSavePointCloud();
            }
        }

        public void BeginScanning()
        {
            forceNextScanAsKeyframe = true;

            if (!pointCloudVFX.gameObject.activeSelf)
            {
                pointCloudVFX.gameObject.SetActive(true);
            }

            if (scanCoroutine == null)
            {
                scanCoroutine = StartCoroutine(ScanLoopCoroutine());
            }

            lastVfxRefreshTime = float.NegativeInfinity;
            pointCloudVFX.Play();
        }

        private IEnumerator ScanLoopCoroutine()
        {
            while (OVRInput.Get(OVRInput.RawButton.X))
            {
                PerformScanAndUpdateVFX();
                yield return new WaitForSeconds(scanInterval);
            }
            // Push one final refresh when scanning stops.
            UpdateVFXFromStoredPoints(forceUpload: true);
            scanCoroutine = null;
        }

        public List<Vector3> GetStoredPointCloud()
        {
            if (scannerMode == ScannerMode.Load)
            {
                return new List<Vector3>(loadedPointCloud);
            }

            return new List<Vector3>(GetAllStoredPoints());
        }

        public async void LoadSavedPointCloud()
        {
            try
            {
                await LoadSavedPointCloudAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Quest3PointCloudScanner] Failed to load saved point cloud.\n{ex}");
            }
        }

        private async Task LoadSavedPointCloudAsync()
        {
            string uuidPath = Path.Combine(Application.persistentDataPath, AnchorUuidFile);
            string pointCloudPath = Path.Combine(Application.persistentDataPath, PointCloudFile);

            if (!File.Exists(uuidPath))
            {
                throw new FileNotFoundException("Anchor UUID file not found.", uuidPath);
            }

            if (!File.Exists(pointCloudPath))
            {
                throw new FileNotFoundException("Point cloud file not found.", pointCloudPath);
            }

            Guid uuid = Guid.Parse(File.ReadAllText(uuidPath));
            List<OVRSpatialAnchor.UnboundAnchor> unboundAnchors = new();
            var loadResult = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new List<Guid> { uuid }, unboundAnchors, null);
            if (loadResult.Status != OVRAnchor.FetchResult.Success || unboundAnchors.Count == 0)
            {
                throw new InvalidOperationException($"Failed to load spatial anchor {uuid}. Status={loadResult.Status}");
            }

            currentAnchor = ResolveLoadAnchorComponent();
            unboundAnchors[0].BindTo(currentAnchor);
            bool localized = await currentAnchor.WhenLocalizedAsync();
            if (!localized)
            {
                throw new InvalidOperationException($"Spatial anchor {uuid} failed to localize.");
            }

            LoadPointCloudFileIntoVFX(pointCloudPath);
            Debug.Log($"[Quest3PointCloudScanner] Loaded {loadedPointCloud.Count} saved points from {pointCloudPath}");
        }

        private OVRSpatialAnchor ResolveLoadAnchorComponent()
        {
            GameObject anchorObject = anchorHolder != null ? anchorHolder : new GameObject("LoadedPointCloudAnchor");
            OVRSpatialAnchor anchor = anchorObject.GetComponent<OVRSpatialAnchor>();
            if (anchor == null)
            {
                anchor = anchorObject.AddComponent<OVRSpatialAnchor>();
            }

            return anchor;
        }

        private void LoadPointCloudFileIntoVFX(string path)
        {
            loadedPointCloud.Clear();
            using (BinaryReader reader = new BinaryReader(File.Open(path, FileMode.Open)))
            {
                int count = reader.ReadInt32();
                loadedPointCloud.Capacity = Mathf.Max(loadedPointCloud.Capacity, count);
                for (int i = 0; i < count; i++)
                {
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    float z = reader.ReadSingle();
                    Vector3 localPoint = new Vector3(x, y, z);
                    loadedPointCloud.Add(currentAnchor != null ? currentAnchor.transform.TransformPoint(localPoint) : localPoint);
                }
            }

            loadedRenderBuffer.Clear();
            loadedRenderBuffer.AddRange(loadedPointCloud);
            if (enableRenderPointBudget && maxRenderPointCount > 0 && loadedRenderBuffer.Count > maxRenderPointCount)
            {
                DownsampleVisiblePoints(loadedRenderBuffer, maxRenderPointCount);
            }

            if (hardUploadPointCap > 0 && loadedRenderBuffer.Count > hardUploadPointCap)
            {
                DownsampleVisiblePoints(loadedRenderBuffer, hardUploadPointCap);
            }

            if (!pointCloudVFX.gameObject.activeSelf)
            {
                pointCloudVFX.gameObject.SetActive(true);
            }

            UploadPointsToVFX(loadedRenderBuffer);
            if (playVfxAfterLoad)
            {
                pointCloudVFX.Play();
            }
        }

        private void PerformScanAndUpdateVFX()
        {
            ScanFrame frame = CaptureScanFrame((int)density);
            StoreFramePoints(frame);
            TryAcceptKeyframe(frame, forceNextScanAsKeyframe);
            forceNextScanAsKeyframe = false;
            UpdateVFXFromStoredPoints(forceUpload: false);
        }

        private void UpdateVFXFromStoredPoints(bool forceUpload)
        {
            if (!forceUpload && Time.unscaledTime - lastVfxRefreshTime < vfxRefreshInterval)
            {
                return;
            }

            List<Vector3> pointsToRender = GetPointsToRender();
            UploadPointsToVFX(pointsToRender);
            lastVfxRefreshTime = Time.unscaledTime;
        }

        private void UploadPointsToVFX(List<Vector3> pointsToRender)
        {
            if (pointDataBuffer == null || pointsToRender.Count > pointDataBuffer.count)
            {
                InitializeBuffer(pointsToRender.Count + 1024);
            }

            pointDataList.Clear();
            if (pointDataList.Capacity < pointsToRender.Count)
            {
                pointDataList.Capacity = pointsToRender.Count;
            }

            foreach (var pt in pointsToRender)
            {
                pointDataList.Add(new PointDataVFX { position = pt });
            }

            pointDataBuffer.SetData(pointDataList);
            pointCloudVFX.SetGraphicsBuffer(PointBufferID, pointDataBuffer);
            pointCloudVFX.SetInt(PointCountID, pointDataList.Count);
        }

        private void InitializeBuffer(int count)
        {
            pointDataBuffer?.Release();
            pointDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, pointDataStride);
            pointCloudVFX.SetGraphicsBuffer(PointBufferID, pointDataBuffer);
        }

        private void ScanAndStorePointCloud(int sampleSize, ChunkManager pointsData)
        {
            StoreFramePoints(CaptureScanFrame(sampleSize));
        }

        private ScanFrame CaptureScanFrame(int sampleSize)
        {
            int stride = GetSamplingStride(sampleSize);
            float fovY = mainCamera.fieldOfView;
            float fovX = Camera.VerticalToHorizontalFieldOfView(fovY, mainCamera.aspect);
            float halfHeight = Mathf.Tan(fovY * 0.5f * Mathf.Deg2Rad);
            float halfWidth = Mathf.Tan(fovX * 0.5f * Mathf.Deg2Rad);
             
            float stepX = (halfWidth * 2) / (sampleSize > 1 ? sampleSize - 1 : 1);
            float stepY = (halfHeight * 2) / (sampleSize > 1 ? sampleSize - 1 : 1);
            int width = GetSampleGridCount(sampleSize, stride);
            int height = width;
            int sampleCount = width * height;
            ScanFrame frame = new ScanFrame
            {
                frameId = nextScanFrameId++,
                timestamp = Time.unscaledTime,
                cameraPosition = mainCamera.transform.position,
                cameraRotation = mainCamera.transform.rotation,
                sampleSize = sampleSize,
                stride = stride,
                width = width,
                height = height,
                points = new Vector3[sampleCount],
                viewDirections = new Vector3[sampleCount],
                validMask = new byte[sampleCount],
                bounds = new Bounds()
            };

            bool hasBounds = false;

            for (int z = 0, gy = 0; z < sampleSize; z += stride, gy++)
            {
                float lastValidDepth = -1f;

                for (int x = 0, gx = 0; x < sampleSize; x += stride, gx++)
                {
                    int index = gy * width + gx;
                    float viewX = -halfWidth + x * stepX;
                    float viewY = -halfHeight + z * stepY;
                    
                    float u = (viewX / halfWidth + 1.0f) * 0.5f;
                    float v = (viewY / halfHeight + 1.0f) * 0.5f;

                    Ray ray = mainCamera.ViewportPointToRay(new Vector3(u, v, 0));
                    frame.viewDirections[index] = ray.direction.normalized;

                    if (raycastManager.Raycast(ray, out EnvironmentRaycastHit hit, maxScanDistance))
                    {
                        float dist = Vector3.Distance(hit.point, mainCamera.transform.position);

                        if (hit.point != Vector3.zero && dist <= maxScanDistance)
                        {
                            bool isEdgeArtifact = false;
                            if (lastValidDepth > 0)
                            {
                                float depthDelta = Mathf.Abs(dist - lastValidDepth);
                                if (depthDelta > edgeThreshold) isEdgeArtifact = true;
                            }

                            if (!isEdgeArtifact)
                            {
                                frame.points[index] = hit.point;
                                frame.validMask[index] = 1;
                                frame.validCount++;
                                if (hasBounds)
                                {
                                    frame.bounds.Encapsulate(hit.point);
                                }
                                else
                                {
                                    frame.bounds = new Bounds(hit.point, Vector3.zero);
                                    hasBounds = true;
                                }
                                lastValidDepth = dist;
                            }
                            else
                            {
                                lastValidDepth = -1f; 
                            }
                        }
                        else
                        {
                            lastValidDepth = -1f;
                        }
                    }
                    else
                    {
                        lastValidDepth = -1f;
                    }
                }
            }

            return frame;
        }

        private void StoreFramePoints(ScanFrame frame)
        {
            if (frame == null || frame.validCount == 0)
            {
                return;
            }

            for (int i = 0; i < frame.points.Length; i++)
            {
                if (frame.validMask[i] != 0)
                {
                    AddScannedPoint(frame.points[i]);
                }
            }
        }

        private int GetSampleGridCount(int sampleSize, int stride)
        {
            if (sampleSize <= 0)
            {
                return 0;
            }

            return ((sampleSize - 1) / Mathf.Max(1, stride)) + 1;
        }

        private int GetSamplingStride(int sampleSize)
        {
            if (maxRaycastsPerTick <= 0 || sampleSize <= 1)
            {
                return 1;
            }

            long totalSamples = (long)sampleSize * sampleSize;
            if (totalSamples <= maxRaycastsPerTick)
            {
                return 1;
            }

            float stride = Mathf.Sqrt((float)totalSamples / maxRaycastsPerTick);
            return Mathf.Clamp(Mathf.CeilToInt(stride), 1, sampleSize);
        }

        private void TryAcceptKeyframe(ScanFrame frame, bool force)
        {
            if (!enableSurfelFusionOnSave || !enableKeyframeCapture || frame == null || frame.validCount < minKeyframeValidHits)
            {
                return;
            }

            bool accept = force || keyframes.Count == 0;
            if (!accept)
            {
                float elapsed = frame.timestamp - lastAcceptedKeyframeTime;
                if (elapsed < minKeyframeInterval)
                {
                    return;
                }

                float translation = Vector3.Distance(frame.cameraPosition, lastAcceptedKeyframe.cameraPosition);
                float rotation = Quaternion.Angle(frame.cameraRotation, lastAcceptedKeyframe.cameraRotation);
                float newCoverageRatio = EstimateNewCoverageRatio(frame);

                accept = elapsed >= maxKeyframeInterval ||
                         translation >= minKeyframeTranslation ||
                         rotation >= minKeyframeRotationDegrees ||
                         newCoverageRatio >= minNewCoverageRatio;
            }

            if (!accept)
            {
                return;
            }

            keyframes.Add(frame);
            lastAcceptedKeyframe = frame;
            lastAcceptedKeyframeTime = frame.timestamp;
            AddFrameToCoverage(frame);

            if (maxStoredKeyframes > 0 && keyframes.Count > maxStoredKeyframes)
            {
                keyframes.RemoveAt(0);
                RebuildKeyframeCoverage();
            }
        }

        private float EstimateNewCoverageRatio(ScanFrame frame)
        {
            if (frame.validCount == 0 || keyframeCoverageVoxelSize <= 0f)
            {
                return 0f;
            }

            int newCells = 0;
            HashSet<Vector3Int> frameCells = new HashSet<Vector3Int>();
            for (int i = 0; i < frame.points.Length; i++)
            {
                if (frame.validMask[i] == 0)
                {
                    continue;
                }

                Vector3Int cell = WorldToCoverageKey(frame.points[i]);
                if (frameCells.Add(cell) && !keyframeCoverage.Contains(cell))
                {
                    newCells++;
                }
            }

            return frameCells.Count > 0 ? (float)newCells / frameCells.Count : 0f;
        }

        private void AddFrameToCoverage(ScanFrame frame)
        {
            for (int i = 0; i < frame.points.Length; i++)
            {
                if (frame.validMask[i] != 0)
                {
                    keyframeCoverage.Add(WorldToCoverageKey(frame.points[i]));
                }
            }
        }

        private void RebuildKeyframeCoverage()
        {
            keyframeCoverage.Clear();
            foreach (ScanFrame frame in keyframes)
            {
                AddFrameToCoverage(frame);
            }

            lastAcceptedKeyframe = keyframes.Count > 0 ? keyframes[keyframes.Count - 1] : null;
            lastAcceptedKeyframeTime = lastAcceptedKeyframe != null ? lastAcceptedKeyframe.timestamp : float.NegativeInfinity;
        }

        private Vector3Int WorldToCoverageKey(Vector3 point)
        {
            float invVoxelSize = 1.0f / keyframeCoverageVoxelSize;
            return new Vector3Int(
                Mathf.FloorToInt(point.x * invVoxelSize),
                Mathf.FloorToInt(point.y * invVoxelSize),
                Mathf.FloorToInt(point.z * invVoxelSize));
        }

        private void AddScannedPoint(Vector3 point)
        {
            pointsData.AddPoint(point);
        }

        private List<Vector3> GetPointsToRender()
        {
            visiblePointsBuffer.Clear();

            visiblePointsBuffer.AddRange(pointsData.GetPointsInRadius(mainCamera.transform.position, renderingRadius, maxChunkCount));

            if (enableRenderPointBudget && maxRenderPointCount > 0 && visiblePointsBuffer.Count > maxRenderPointCount)
            {
                DownsampleVisiblePoints(visiblePointsBuffer, maxRenderPointCount);
            }

            if (hardUploadPointCap > 0 && visiblePointsBuffer.Count > hardUploadPointCap)
            {
                DownsampleVisiblePoints(visiblePointsBuffer, hardUploadPointCap);
            }

            return visiblePointsBuffer;
        }

        private void DownsampleVisiblePoints(List<Vector3> points, int targetCount)
        {
            int sourceCount = points.Count;
            if (sourceCount <= targetCount)
            {
                return;
            }

            float step = (float)sourceCount / targetCount;
            int writeIndex = 0;

            for (int i = 0; i < targetCount; i++)
            {
                int sampleIndex = Mathf.Min(Mathf.FloorToInt(i * step), sourceCount - 1);
                points[writeIndex++] = points[sampleIndex];
            }

            points.RemoveRange(writeIndex, sourceCount - writeIndex);
        }

        private List<Vector3> GetAllStoredPoints()
        {
            allStoredPointsBuffer.Clear();
            allStoredPointsBuffer.AddRange(pointsData.GetAllPoints());
            return allStoredPointsBuffer;
        }

        // -----------------------------------------------------------------------
        // Save asynchronously and run the optional denoising pass.
        // -----------------------------------------------------------------------

        private async void CreateAnchorAndSavePointCloud()
        {
            if (currentAnchor != null)
            {
                // Reuse the existing anchor and save immediately.
                if (await SavePointCloudToFileAsync())
                {
                    WriteAnchorUuidFile();
                }
                return;
            }

            EnsureAnchorCreateListenerRegistered();
            Vector3 spawnPos = anchorHolder != null ? anchorHolder.transform.position : mainCamera.transform.position + mainCamera.transform.forward * 0.2f;
            Quaternion spawnRot = anchorHolder != null ? anchorHolder.transform.rotation : Quaternion.identity;

            GameObject anchorObject = anchorHolder != null ? anchorHolder : new GameObject("AnchorHolderRuntime");
            awaitingOwnAnchorCreate = true;
            anchorCoreBlock.InstantiateSpatialAnchor(anchorObject, spawnPos, spawnRot);
        }

        // The anchor creation callback is async so it can await the save operation.
        private async void OnAnchorCreated(OVRSpatialAnchor anchor, OVRSpatialAnchor.OperationResult result)
        {
            if (!awaitingOwnAnchorCreate)
            {
                return;
            }
            awaitingOwnAnchorCreate = false;

            if (result == OVRSpatialAnchor.OperationResult.Success)
            {
                currentAnchor = anchor;
                Debug.Log($"[Quest3PointCloudScanner] Anchor created with UUID: {anchor.Uuid}");
                
                // Save asynchronously.
                if (await SavePointCloudToFileAsync())
                {
                    WriteAnchorUuidFile();
                }
            }
            else
            {
                Debug.LogError($"[Quest3PointCloudScanner] Anchor creation failed: {result}");
            }
        }

        private void WriteAnchorUuidFile()
        {
            if (currentAnchor == null)
            {
                return;
            }

            string path = Path.Combine(Application.persistentDataPath, AnchorUuidFile);
            File.WriteAllText(path, currentAnchor.Uuid.ToString());
            Debug.Log($"[Quest3PointCloudScanner] Anchor UUID saved to {path}");
        }

        private async Task<bool> SavePointCloudToFileAsync()
        {
            string path = Path.Combine(Application.persistentDataPath, PointCloudFile);
            
            // 1. Read all points on the main thread because ChunkManager may not be thread-safe.
            List<Vector3> pointsToSave;
            if (enableSurfelFusionOnSave)
            {
                try
                {
                    pointsToSave = await BuildSurfelFusedPointCloudAsync();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Quest3PointCloudScanner] Surfel fusion failed. Point cloud was not saved.\n{ex}");
                    return false;
                }
            }
            else
            {
                pointsToSave = new List<Vector3>(GetAllStoredPoints());
            }

            int originalCount = pointsToSave.Count;

            Debug.Log($"[System] Preparing to save {originalCount} points...");

            // 2. Run post-process denoising on a background thread when enabled.
            if (enablePostDenoising && originalCount > 0)
            {
                if (maxPointsForPostDenoising > 0 && originalCount > maxPointsForPostDenoising)
                {
                    Debug.LogWarning($"[System] Skip post denoising because point count ({originalCount}) exceeds limit ({maxPointsForPostDenoising}).");
                }
                else
                {
                    // Keep the denoising work off the main thread to avoid blocking the UI.
                    pointsToSave = await CleanPointsRadiusFilterAsync(pointsToSave, denoiseRadius, denoiseMinNeighbors);
                    Debug.Log($"[System] Denoising complete. Reduced from {originalCount} to {pointsToSave.Count} points.");
                }
            }

            // Return to the main thread for coordinate conversion and file output.
            // Writing the float data is fast; denoising is the dominant cost.
            using (BinaryWriter writer = new(File.Open(path, FileMode.Create)))
            {
                writer.Write(pointsToSave.Count);
                foreach (var p in pointsToSave)
                {
                    Vector3 relativePoint = currentAnchor != null ? currentAnchor.transform.InverseTransformPoint(p) : p;
                    writer.Write(relativePoint.x);
                    writer.Write(relativePoint.y);
                    writer.Write(relativePoint.z);
                }
            }

            Debug.Log($"[Quest3PointCloudScanner] Successfully saved {pointsToSave.Count} points to {path}");
            return true;
        }

        private async Task<List<Vector3>> BuildSurfelFusedPointCloudAsync()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                throw new InvalidOperationException("Compute shaders are not supported on this device.");
            }

            if (surfelFusionCompute == null)
            {
                surfelFusionCompute = Resources.Load<ComputeShader>("QuestSurfelFusion");
            }

            if (surfelFusionCompute == null)
            {
                throw new InvalidOperationException("QuestSurfelFusion.compute is missing. Assign surfelFusionCompute or keep Assets/Resources/QuestSurfelFusion.compute in the project.");
            }

            List<ScanFrame> frames = new List<ScanFrame>(keyframes);
            if (frames.Count == 0)
            {
                throw new InvalidOperationException("Surfel fusion is enabled, but no keyframes were captured. Press/hold X to scan before saving.");
            }

            int maxSamplesInFrame = 1;
            foreach (ScanFrame frame in frames)
            {
                maxSamplesInFrame = Mathf.Max(maxSamplesInFrame, frame.points.Length);
            }

            int surfelCapacity = Mathf.Max(1, maxSurfelCount);
            int hashCapacity = Mathf.Max(1024, surfelHashBucketCount);
            ComputeShader cs = surfelFusionCompute;

            int clearSurfelsKernel = FindRequiredKernel(cs, "ClearSurfels");
            int estimateNormalsKernel = FindRequiredKernel(cs, "EstimateNormals");
            int clearHashKernel = FindRequiredKernel(cs, "ClearHash");
            int buildHashKernel = FindRequiredKernel(cs, "BuildHash");
            int clearAccumKernel = FindRequiredKernel(cs, "ClearAccumulation");
            int matchKernel = FindRequiredKernel(cs, "MatchAndAccumulate");
            int applyKernel = FindRequiredKernel(cs, "ApplyAccumulation");
            int createKernel = FindRequiredKernel(cs, "CreateNewSurfels");
            int clearOutputKernel = FindRequiredKernel(cs, "ClearOutputCounter");
            int compactKernel = FindRequiredKernel(cs, "CompactStableSurfels");

            GraphicsBuffer sampleBuffer = null;
            GraphicsBuffer sampleStateBuffer = null;
            GraphicsBuffer surfelBuffer = null;
            GraphicsBuffer hashBuffer = null;
            GraphicsBuffer accumPositionBuffer = null;
            GraphicsBuffer accumNormalBuffer = null;
            GraphicsBuffer matchBuffer = null;
            GraphicsBuffer countersBuffer = null;
            GraphicsBuffer outputBuffer = null;

            try
            {
                sampleBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxSamplesInFrame, Marshal.SizeOf<GpuScanSample>());
                sampleStateBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxSamplesInFrame, 16);
                surfelBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, surfelCapacity, Marshal.SizeOf<GpuSurfel>());
                hashBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, hashCapacity, 4);
                accumPositionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, surfelCapacity, 16);
                accumNormalBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, surfelCapacity, 16);
                matchBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxSamplesInFrame, 4);
                countersBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4);
                outputBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, surfelCapacity, 12);

                BindSurfelFusionBuffers(cs, clearSurfelsKernel, sampleBuffer, sampleStateBuffer, surfelBuffer, hashBuffer, accumPositionBuffer, accumNormalBuffer, matchBuffer, countersBuffer, outputBuffer);
                BindSurfelFusionBuffers(cs, estimateNormalsKernel, sampleBuffer, sampleStateBuffer, surfelBuffer, hashBuffer, accumPositionBuffer, accumNormalBuffer, matchBuffer, countersBuffer, outputBuffer);
                BindSurfelFusionBuffers(cs, clearHashKernel, sampleBuffer, sampleStateBuffer, surfelBuffer, hashBuffer, accumPositionBuffer, accumNormalBuffer, matchBuffer, countersBuffer, outputBuffer);
                BindSurfelFusionBuffers(cs, buildHashKernel, sampleBuffer, sampleStateBuffer, surfelBuffer, hashBuffer, accumPositionBuffer, accumNormalBuffer, matchBuffer, countersBuffer, outputBuffer);
                BindSurfelFusionBuffers(cs, clearAccumKernel, sampleBuffer, sampleStateBuffer, surfelBuffer, hashBuffer, accumPositionBuffer, accumNormalBuffer, matchBuffer, countersBuffer, outputBuffer);
                BindSurfelFusionBuffers(cs, matchKernel, sampleBuffer, sampleStateBuffer, surfelBuffer, hashBuffer, accumPositionBuffer, accumNormalBuffer, matchBuffer, countersBuffer, outputBuffer);
                BindSurfelFusionBuffers(cs, applyKernel, sampleBuffer, sampleStateBuffer, surfelBuffer, hashBuffer, accumPositionBuffer, accumNormalBuffer, matchBuffer, countersBuffer, outputBuffer);
                BindSurfelFusionBuffers(cs, createKernel, sampleBuffer, sampleStateBuffer, surfelBuffer, hashBuffer, accumPositionBuffer, accumNormalBuffer, matchBuffer, countersBuffer, outputBuffer);
                BindSurfelFusionBuffers(cs, clearOutputKernel, sampleBuffer, sampleStateBuffer, surfelBuffer, hashBuffer, accumPositionBuffer, accumNormalBuffer, matchBuffer, countersBuffer, outputBuffer);
                BindSurfelFusionBuffers(cs, compactKernel, sampleBuffer, sampleStateBuffer, surfelBuffer, hashBuffer, accumPositionBuffer, accumNormalBuffer, matchBuffer, countersBuffer, outputBuffer);

                cs.SetInt("_MaxSurfels", surfelCapacity);
                cs.SetInt("_HashBucketCount", hashCapacity);
                cs.SetFloat("_CellSize", Mathf.Max(0.001f, surfelCellSize));
                cs.SetFloat("_SurfelRadius", Mathf.Max(0.001f, surfelRadius));
                cs.SetFloat("_MatchDistance", Mathf.Max(0.001f, surfelMatchDistance));
                cs.SetFloat("_PointToPlaneDistance", Mathf.Max(0.001f, surfelPointToPlaneDistance));
                cs.SetFloat("_NormalDotThreshold", Mathf.Cos(surfelNormalAngleThreshold * Mathf.Deg2Rad));
                cs.SetFloat("_MinViewNormalDot", Mathf.Cos(surfelMaxGrazingAngle * Mathf.Deg2Rad));
                cs.SetFloat("_NormalNeighborDistance", Mathf.Max(0.001f, surfelNormalNeighborDistance));
                cs.SetInt("_MaxBucketTraversal", Mathf.Max(1, surfelMaxBucketTraversal));
                cs.SetInt("_NewSurfelStride", Mathf.Max(1, newSurfelStride));
                cs.SetInt("_MinSurfelObservations", Mathf.Max(1, minSurfelObservations));
                cs.SetFloat("_MinSurfelWeight", Mathf.Max(0f, minSurfelWeight));
                cs.SetFloat("_MaxSurfelWeight", Mathf.Max(1f, maxSurfelWeight));
                cs.SetFloat("_PositionScale", 10000f);
                cs.SetFloat("_NormalScale", 10000f);

                countersBuffer.SetData(new uint[] { 0, 0, 0, 0 });
                Dispatch1D(cs, clearSurfelsKernel, surfelCapacity);

                GpuScanSample[] packedSamples = new GpuScanSample[maxSamplesInFrame];
                foreach (ScanFrame frame in frames)
                {
                    int sampleCount = frame.points.Length;
                    PackScanFrameForGpu(frame, packedSamples);
                    sampleBuffer.SetData(packedSamples, 0, 0, sampleCount);

                    cs.SetInt("_SampleCount", sampleCount);
                    cs.SetInt("_FrameWidth", frame.width);
                    cs.SetInt("_FrameHeight", frame.height);
                    cs.SetInt("_FrameIndex", frame.frameId);

                    Dispatch1D(cs, estimateNormalsKernel, sampleCount);
                    Dispatch1D(cs, clearHashKernel, hashCapacity);
                    Dispatch1D(cs, buildHashKernel, surfelCapacity);
                    Dispatch1D(cs, clearAccumKernel, surfelCapacity);
                    Dispatch1D(cs, matchKernel, sampleCount);
                    Dispatch1D(cs, applyKernel, surfelCapacity);
                    Dispatch1D(cs, createKernel, sampleCount);
                }

                Dispatch1D(cs, clearOutputKernel, 1);
                Dispatch1D(cs, compactKernel, surfelCapacity);

                uint[] counters = await RequestGpuReadbackAsync<uint>(countersBuffer);
                int outputCount = counters.Length > 1 ? Mathf.Min((int)counters[1], surfelCapacity) : 0;
                if (outputCount < minFusedPointCountForSave)
                {
                    throw new InvalidOperationException($"Surfel fusion produced too few stable points ({outputCount}). Required at least {minFusedPointCountForSave}.");
                }

                Vector3[] output = await RequestGpuReadbackAsync<Vector3>(outputBuffer);
                List<Vector3> fusedPoints = new List<Vector3>(outputCount);
                for (int i = 0; i < outputCount; i++)
                {
                    fusedPoints.Add(output[i]);
                }

                Debug.Log($"[Quest3PointCloudScanner] GPU surfel fusion complete. Keyframes={frames.Count}, stable surfels={fusedPoints.Count}, allocated surfels={counters[0]}.");
                return fusedPoints;
            }
            finally
            {
                ReleaseBuffer(ref sampleBuffer);
                ReleaseBuffer(ref sampleStateBuffer);
                ReleaseBuffer(ref surfelBuffer);
                ReleaseBuffer(ref hashBuffer);
                ReleaseBuffer(ref accumPositionBuffer);
                ReleaseBuffer(ref accumNormalBuffer);
                ReleaseBuffer(ref matchBuffer);
                ReleaseBuffer(ref countersBuffer);
                ReleaseBuffer(ref outputBuffer);
            }
        }

        private void BindSurfelFusionBuffers(
            ComputeShader cs,
            int kernel,
            GraphicsBuffer sampleBuffer,
            GraphicsBuffer sampleStateBuffer,
            GraphicsBuffer surfelBuffer,
            GraphicsBuffer hashBuffer,
            GraphicsBuffer accumPositionBuffer,
            GraphicsBuffer accumNormalBuffer,
            GraphicsBuffer matchBuffer,
            GraphicsBuffer countersBuffer,
            GraphicsBuffer outputBuffer)
        {
            cs.SetBuffer(kernel, "_Samples", sampleBuffer);
            cs.SetBuffer(kernel, "_SampleStates", sampleStateBuffer);
            cs.SetBuffer(kernel, "_Surfels", surfelBuffer);
            cs.SetBuffer(kernel, "_HashHeads", hashBuffer);
            cs.SetBuffer(kernel, "_AccumPosition", accumPositionBuffer);
            cs.SetBuffer(kernel, "_AccumNormal", accumNormalBuffer);
            cs.SetBuffer(kernel, "_MatchIndices", matchBuffer);
            cs.SetBuffer(kernel, "_Counters", countersBuffer);
            cs.SetBuffer(kernel, "_OutputPoints", outputBuffer);
        }

        private int FindRequiredKernel(ComputeShader cs, string kernelName)
        {
            if (!cs.HasKernel(kernelName))
            {
                throw new InvalidOperationException($"QuestSurfelFusion.compute did not compile kernel '{kernelName}'. Check the Unity shader import errors before saving.");
            }

            return cs.FindKernel(kernelName);
        }

        private void PackScanFrameForGpu(ScanFrame frame, GpuScanSample[] packedSamples)
        {
            for (int i = 0; i < frame.points.Length; i++)
            {
                Vector3 point = frame.points[i];
                Vector3 viewDirection = frame.viewDirections[i];
                float valid = frame.validMask[i] != 0 ? 1f : 0f;
                float depth = valid > 0f ? Vector3.Distance(point, frame.cameraPosition) : 0f;
                packedSamples[i] = new GpuScanSample
                {
                    positionDepth = new Vector4(point.x, point.y, point.z, depth),
                    viewDirectionValid = new Vector4(viewDirection.x, viewDirection.y, viewDirection.z, valid)
                };
            }
        }

        private void Dispatch1D(ComputeShader cs, int kernel, int count)
        {
            if (count <= 0)
            {
                return;
            }

            cs.Dispatch(kernel, Mathf.CeilToInt(count / 64f), 1, 1);
        }

        private static Task<T[]> RequestGpuReadbackAsync<T>(GraphicsBuffer buffer) where T : struct
        {
            TaskCompletionSource<T[]> completion = new TaskCompletionSource<T[]>();
            AsyncGPUReadback.Request(buffer, request =>
            {
                if (request.hasError)
                {
                    completion.TrySetException(new InvalidOperationException("AsyncGPUReadback failed."));
                    return;
                }

                var data = request.GetData<T>();
                T[] result = new T[data.Length];
                data.CopyTo(result);
                completion.TrySetResult(result);
            });

            return completion.Task;
        }

        private static void ReleaseBuffer(ref GraphicsBuffer buffer)
        {
            buffer?.Release();
            buffer = null;
        }

        /// <summary>
        /// Grid-accelerated radius outlier removal for background-thread execution.
        /// Suitable for point clouds around one million points and faster than a KD-tree for this workload.
        /// </summary>
        private async Task<List<Vector3>> CleanPointsRadiusFilterAsync(List<Vector3> rawPoints, float radius, int minNeighbors)
        {
            if (rawPoints.Count < 100) return rawPoints;

            Debug.Log($"[Cleaner] Starting background denoise (Radius: {radius}, MinNeighbors: {minNeighbors})...");
            
            return await Task.Run(() =>
            {
                // 1. Build the spatial grid index.
                float cellSize = radius;
                float invCellSize = 1.0f / cellSize;
                
                // Key: grid coordinate; value: point indices in that cell.
                Dictionary<Vector3Int, List<int>> grid = new Dictionary<Vector3Int, List<int>>();

                for (int i = 0; i < rawPoints.Count; i++)
                {
                    Vector3 p = rawPoints[i];
                    Vector3Int key = new Vector3Int(
                        Mathf.FloorToInt(p.x * invCellSize),
                        Mathf.FloorToInt(p.y * invCellSize),
                        Mathf.FloorToInt(p.z * invCellSize)
                    );

                    if (!grid.TryGetValue(key, out List<int> indices))
                    {
                        indices = new List<int>();
                        grid[key] = indices;
                    }
                    indices.Add(i);
                }

                List<Vector3> keptPoints = new List<Vector3>(rawPoints.Count);
                float sqrRadius = radius * radius;

                // Precompute the 27 neighboring cell offsets.
                List<Vector3Int> neighborOffsets = new List<Vector3Int>();
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                    neighborOffsets.Add(new Vector3Int(x, y, z));

                // 2. Filter outliers.
                for (int i = 0; i < rawPoints.Count; i++)
                {
                    Vector3 currentPt = rawPoints[i];
                    Vector3Int centerKey = new Vector3Int(
                        Mathf.FloorToInt(currentPt.x * invCellSize),
                        Mathf.FloorToInt(currentPt.y * invCellSize),
                        Mathf.FloorToInt(currentPt.z * invCellSize)
                    );

                    int neighborCount = 0;
                    bool keep = false;

                    foreach (var offset in neighborOffsets)
                    {
                        if (grid.TryGetValue(centerKey + offset, out List<int> candidates))
                        {
                            foreach (int neighborIdx in candidates)
                            {
                                // Check distance, including the point itself.
                                if ((rawPoints[neighborIdx] - currentPt).sqrMagnitude <= sqrRadius)
                                {
                                    neighborCount++;
                                    if (neighborCount > minNeighbors) 
                                    {
                                        keep = true; 
                                        break; 
                                    }
                                }
                            }
                        }
                        if (keep) break;
                    }

                    if (keep) keptPoints.Add(currentPt);
                }

                return keptPoints;
            });
        }

        private void EnsureAnchorCreateListenerRegistered()
        {
            if (anchorCoreBlock == null || anchorCreateListenerRegistered)
            {
                return;
            }

            anchorCoreBlock.OnAnchorCreateCompleted.AddListener(OnAnchorCreated);
            anchorCreateListenerRegistered = true;
        }

        private void OnDisable()
        {
            if (scanCoroutine != null)
            {
                StopCoroutine(scanCoroutine);
                scanCoroutine = null;
            }

            if (pointCloudVFX != null)
            {
                pointCloudVFX.SetInt(PointCountID, 0);
            }

            pointDataBuffer?.Release();
            pointDataBuffer = null;
        }

        private void OnDestroy()
        {
            if (scanCoroutine != null) StopCoroutine(scanCoroutine);
            if (anchorCoreBlock != null && anchorCreateListenerRegistered)
            {
                anchorCoreBlock.OnAnchorCreateCompleted.RemoveListener(OnAnchorCreated);
                anchorCreateListenerRegistered = false;
            }
            pointDataBuffer?.Release();
            pointDataBuffer = null;
        }
    }
}
