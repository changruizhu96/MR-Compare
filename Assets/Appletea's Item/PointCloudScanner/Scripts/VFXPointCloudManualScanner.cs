// VFXPointCloudManualScanner.cs (using Coroutine)
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.VFX;
using UnityEngine.Rendering;
using Meta.XR; // Keep if still needed

namespace Appletea.Dev.PointCloud
{
    public class VFXPointCloudManualScanner : MonoBehaviour
    {
        // --- Enums and Settings (Unchanged) ---
        public enum Density : int { low = 32, medium = 64, high = 128, vHigh = 256, ultra = 512 }

        [Header("Reference Scripts")]
        [SerializeField] private VisualEffect pointCloudVFX;
        [SerializeField] private EnvironmentRaycastManager depthManager;
        [SerializeField] private float renderingRadius = 20.0f;
        [Space(10)]
        [Header("Chunk Settings")]
        [SerializeField] private int chunkSize = 1;
        [SerializeField] private int maxPointsPerChunk = 256;

        [Space(10)]
        [Header("Scan Settings")]
        [SerializeField] private Density density = Density.medium;
        [SerializeField][Tooltip("The limit is about 5m")] private float maxScanDistance = 5;
        [SerializeField]
        [Tooltip("Interval between scans when button is held (in seconds)")]
        private float scanInterval = 0.2f;

        [Space(10)]
        [Header("Rendering Settings")]
        [SerializeField] int maxChunkCount = 15;

        [Space(10)]
        [Header("Camera Settings")]
        [SerializeField] private Camera mainCamera;
        [SerializeField][Tooltip("Percentage of the field of view")] private float fovMargin = 0.9f;

        // --- VFX Buffer Related (Unchanged) ---
        private GraphicsBuffer pointDataBuffer;
        private struct PointDataVFX { public Vector3 position; }
        private int pointDataStride = System.Runtime.InteropServices.Marshal.SizeOf<PointDataVFX>();
        private List<PointDataVFX> pointDataList = new List<PointDataVFX>();

        private static readonly int PointBufferID = Shader.PropertyToID("PointBuffer");
        private static readonly int PointCountID = Shader.PropertyToID("PointCount");
        // --- End VFX Buffer Related ---

        private ChunkManager pointsData;
        public List<Vector3> GetStoredPointCloud()
        {
            if (pointsData != null)
            {
                return pointsData.GetAllPoints();
            }
            else
            {
                Debug.LogError("pointsData (ChunkManager) is null in VFXPointCloudManualScanner. Cannot get points.", this);
                return new List<Vector3>(); // Return an empty list to avoid null reference errors
            }
        }

        public void DisableVisualisedPoint()
        {
            pointCloudVFX.enabled = !pointCloudVFX.isActiveAndEnabled;
            
        }

        // --- Coroutine Management ---
        private Coroutine scanCoroutine = null; // Reference to the running scan coroutine

        void Start()
        {
            // --- Check References ---
            if (pointCloudVFX == null) { Debug.LogError("PointCloud VFX not assigned!", this); enabled = false; return; }
            if (depthManager == null) { Debug.LogError("Depth Manager not assigned!", this); enabled = false; return; }
            if (mainCamera == null) { Debug.LogError("Main Camera not assigned!", this); enabled = false; return; }

            // --- Initialization ---
            pointsData = new ChunkManager(chunkSize, maxPointsPerChunk);
            InitializeBuffer(1024);

            Debug.Log($"VFXPointCloudManualScanner Initialized. Hold 'X' button ({OVRInput.RawButton.X}) to scan continuously.");
        }

        void InitializeBuffer(int count)
        {
            pointDataBuffer?.Release();
            if (count <= 0) count = 1;
            pointDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, pointDataStride);
            Debug.Log($"[VFX Scanner] Initialized/Resized Point Buffer with count: {count}");
            pointCloudVFX.SetGraphicsBuffer(PointBufferID, pointDataBuffer);
        }

        // Performs a single scan and updates the VFX buffer (Function remains the same)
        void PerformScanAndUpdateVFX()
        {
            ScanAndStorePointCloud(((int)density), pointsData);
            List<Vector3> pointsToRender = pointsData.GetPointsInRadius(mainCamera.transform.position, renderingRadius, maxChunkCount);
            int currentRenderPointCount = pointsToRender.Count;

            if (pointDataBuffer == null || currentRenderPointCount > pointDataBuffer.count)
            {
                int newSize = Mathf.Max(currentRenderPointCount, pointDataBuffer?.count ?? 0) + 2048;
                InitializeBuffer(newSize);
            }

            pointDataList.Clear();
            for (int i = 0; i < currentRenderPointCount; i++)
            {
                pointDataList.Add(new PointDataVFX { position = pointsToRender[i] });
            }

            if (currentRenderPointCount > 0)
            {
                pointDataBuffer.SetData(pointDataList, 0, 0, currentRenderPointCount);
            }

            pointCloudVFX.SetInt(PointCountID, currentRenderPointCount);

            
        }

        // The Coroutine that handles the scanning loop
        private IEnumerator ScanLoopCoroutine()
        {
            Debug.Log("Starting continuous scan coroutine...");
            // Optional: Perform an immediate first scan uncommenting the line below
            // PerformScanAndUpdateVFX();

            // Loop while the button is held down
            while (OVRInput.Get(OVRInput.RawButton.X))
            {
                // Perform the scan and update VFX
                Debug.Log("Scan triggered (Coroutine)...");
                PerformScanAndUpdateVFX();

                // Wait for the specified interval before the next iteration
                yield return new WaitForSeconds(scanInterval);

            }

            // This part executes when the while loop condition (OVRInput.Get) becomes false (button released)
            Debug.Log("Scan button released, stopping coroutine.");
            scanCoroutine = null; // Clear the reference as the coroutine is ending
        }

        void Update()
        {
            // --- Manage Scan Coroutine ---

            // When the button is pressed DOWN
            if (OVRInput.GetDown(OVRInput.RawButton.X))
            {
                // Start the coroutine ONLY if it's not already running
                if (scanCoroutine == null)
                {
                    scanCoroutine = StartCoroutine(ScanLoopCoroutine());
                }
                pointCloudVFX.Play();
            }

            if (OVRInput.GetDown(OVRInput.RawButton.Y))
            {
                // Start the coroutine ONLY if it's not already running
                if (pointCloudVFX != null)
                {
                    DisableVisualisedPoint();
                }
            }

            // OPTIONAL: Explicit Stop on Button Up (Though the coroutine stops itself)
            // You generally don't need this because the 'while' loop inside the
            // coroutine checks OVRInput.Get() and exits automatically.
            // if (OVRInput.GetUp(OVRInput.RawButton.X))
            // {
            //     if (scanCoroutine != null)
            //     {
            //         StopCoroutine(scanCoroutine);
            //         scanCoroutine = null;
            //         Debug.Log("Explicitly stopping scan coroutine on button up.");
            //     }
            // }

            // --- Separate logic for saving PLY (remains the same) ---
            if (OVRInput.GetDown(OVRInput.RawButton.Y))
            {
                Debug.Log("Triggering PLY Save...");
                List<Vector3> allPoints = pointsData.GetAllPoints();
                Debug.Log("Total points to save: " + allPoints.Count);
                // --- Add your PlyLib saving function call here ---
                // Example: PlyLib.SavePointCloud(allPoints, "Assets/SavedPointCloud.ply");
                Debug.Log("Local PLY Output Done! (Actual saving requires PlyLib implementation)");
            }
        }

        // --- GenerateViewSpaceCoords and ScanAndStorePointCloud functions (Unchanged) ---
        List<Vector2> GenerateViewSpaceCoords(int xSize, int zSize)
        { /* ... Unchanged ... */
            List<Vector2> coords = new List<Vector2>();
            float fovY = mainCamera.fieldOfView * fovMargin;
            float fovX = Camera.VerticalToHorizontalFieldOfView(fovY, mainCamera.aspect) * fovMargin;
            float halfFrustumHeight = Mathf.Tan(fovY * 0.5f * Mathf.Deg2Rad);
            float halfFrustumWidth = Mathf.Tan(fovX * 0.5f * Mathf.Deg2Rad);
            float stepY = (halfFrustumHeight * 2) / (zSize > 1 ? zSize - 1 : 1);
            float stepX = (halfFrustumWidth * 2) / (xSize > 1 ? xSize - 1 : 1);
            for (int z = 0; z < zSize; z++)
            {
                for (int x = 0; x < xSize; x++)
                {
                    float viewX = -halfFrustumWidth + x * stepX; float viewY = -halfFrustumHeight + z * stepY;
                    float normX = (viewX / halfFrustumWidth + 1.0f) * 0.5f; float normY = (viewY / halfFrustumHeight + 1.0f) * 0.5f;
                    coords.Add(new Vector2(normX, normY));
                }
            }
            return coords;
        }

        void ScanAndStorePointCloud(int sampleSize, ChunkManager pointsData)
        { /* ... Unchanged ... */
            List<Vector2> viewSpaceCoords = GenerateViewSpaceCoords(sampleSize, sampleSize); List<Ray> rays = new List<Ray>();
            foreach (Vector2 i in viewSpaceCoords) { rays.Add(mainCamera.ViewportPointToRay(new Vector3(i.x, i.y, 0))); }
            List<EnvironmentRaycastHit> results = new List<EnvironmentRaycastHit>(); int hitsFound = 0;
            foreach (Ray ray in rays)
            {
                EnvironmentRaycastHit result;
                if (depthManager != null && depthManager.Raycast(ray, out result, maxScanDistance))
                {
                    if (result.point != Vector3.zero && Vector3.Distance(result.point, mainCamera.transform.position) <= maxScanDistance)
                    {
                        results.Add(result); hitsFound++;
                    }
                }
            }
            foreach (var result in results) { pointsData.AddPoint(result.point); }
        }

        // --- OnDestroy, OnDisable, OnEnable ---
        void OnDestroy()
        {
            // Stop coroutine if running
            if (scanCoroutine != null) StopCoroutine(scanCoroutine);
            pointDataBuffer?.Release(); pointDataBuffer = null;
        }

        void OnDisable()
        {
            // Stop coroutine if running when the object is disabled
            if (scanCoroutine != null)
            {
                StopCoroutine(scanCoroutine);
                scanCoroutine = null; // Clear reference
                Debug.Log("Scan coroutine stopped due to object disable.");
            }
            pointDataBuffer?.Release(); pointDataBuffer = null;
        }

        void OnEnable()
        { // (Logic remains the same as previous version)
            if (pointDataBuffer == null && Application.isPlaying)
            {
                InitializeBuffer(1024);
                if (pointCloudVFX != null) pointCloudVFX.Play();
            }
            else if (pointCloudVFX != null && Application.isPlaying)
            {
                pointCloudVFX.Play();
            }
            // Ensure coroutine reference is null on enable, it will be started by input if needed
            scanCoroutine = null;
        }
    }
}