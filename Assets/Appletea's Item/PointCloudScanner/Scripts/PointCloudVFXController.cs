// PointCloudVFXController.cs (新脚本)
using System.Collections;
using System.Collections.Generic;
using System.IO; // 仍然需要用于路径
using UnityEngine;
using UnityEngine.VFX; // 需要引用 VFX 包
using UnityEngine.Rendering; // 需要引用 Rendering 包
using Meta.XR; // 保留，因为需要 EnvironmentRaycastManager

// 确保命名空间不冲突，如果需要可以修改
namespace Appletea.Dev.PointCloud
{
    public class PointCloudVFXController : MonoBehaviour
    {
        // --- 复制 PointCloudController 中的枚举和设置 ---
        public enum Density : int { low = 32, medium = 64, high = 128, vHigh = 256, ultra = 512 }

        [Header("Reference Scripts")]
        [SerializeField] private VisualEffect pointCloudVFX; // 引用 Visual Effect 组件

        [SerializeField] private EnvironmentRaycastManager depthManager; // 引用深度管理器

        [Space(10)]
        [Header("Chunk Settings")]
        [SerializeField] private int chunkSize = 1;
        [SerializeField] private int maxPointsPerChunk = 256;

        [Space(10)]
        [Header("Scan Settings")]
        [SerializeField] private float scanInterval = 1.0f;
        [SerializeField] private Density density = Density.medium;
        [SerializeField][Tooltip("The limit is about 5m")] private float maxScanDistance = 5;

        [Space(10)]
        [Header("Rendering Settings")]
        [SerializeField] private float renderingRadius = 10.0f;
        [SerializeField] int maxChunkCount = 15;

        [Space(10)]
        [Header("Camera Settings")]
        [SerializeField] private Camera mainCamera;
        [SerializeField][Tooltip("Percentage of the field of view")] private float fovMargin = 0.9f;

        // --- VFX Buffer 相关 ---
        private GraphicsBuffer pointDataBuffer;
        private struct PointDataVFX { public Vector3 position; } // 数据结构
        private int pointDataStride = System.Runtime.InteropServices.Marshal.SizeOf<PointDataVFX>();
        private List<PointDataVFX> pointDataList = new List<PointDataVFX>(); // 临时列表

        // Shader 属性 ID (需要与 VFX Graph 中的参数名一致)
        private static readonly int PointBufferID = Shader.PropertyToID("PointBuffer");
        private static readonly int PointCountID = Shader.PropertyToID("PointCount");
        // --- End VFX Buffer 相关 ---

        private ChunkManager pointsData; // 沿用原来的数据管理逻辑
        private string directoryPath; // 保留用于可能的本地保存路径

        void Start()
        {
            if (pointCloudVFX == null)
            {
                Debug.LogError("PointCloud VFX (VisualEffect Component) is not assigned!", this);
                enabled = false;
                return;
            }
            if (depthManager == null)
            {
                Debug.LogError("Depth Manager is not assigned!", this);
                enabled = false;
                return;
            }
            if (mainCamera == null)
            {
                Debug.LogError("Main Camera is not assigned!", this);
                enabled = false;
                return;
            }

            
            directoryPath = Application.persistentDataPath; // 获取本地路径
            pointsData = new ChunkManager(chunkSize, maxPointsPerChunk);

            // 基于预估初始化 Buffer
            int estimatedMaxPoints = (int)density * (int)density * maxChunkCount;
            InitializeBuffer(estimatedMaxPoints > 0 ? estimatedMaxPoints : 1024);

            Invoke("StartScanRoutine", 1.0f);
            
        }

        void InitializeBuffer(int count)
        {
            pointDataBuffer?.Release(); // 释放旧的（如果存在）
            if (count <= 0) count = 1; // 确保数量大于0

            pointDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, pointDataStride);
            Debug.Log($"[VFX Controller] Initialized Point Buffer with count: {count}");

            // 设置 Buffer 到 VFX Graph
            pointCloudVFX.SetGraphicsBuffer(PointBufferID, pointDataBuffer);
            pointCloudVFX.SetInt(PointCountID, 0); // 初始激活点数为0
        }

        void StartScanRoutine()
        {
            StartCoroutine(ScanRoutine());
            
        }

        IEnumerator ScanRoutine()
        {
            while (true)
            {
                // 扫描并存储点云数据 (使用原来的逻辑)
                ScanAndStorePointCloud(((int)density), pointsData);

                // 获取当前渲染半径内的点
                List<Vector3> allAccumulatedPoints = pointsData.GetAllPoints(); // 获取所有累积的点
                List<Vector3> currentPoints = pointsData.GetPointsInRadius(mainCamera.transform.position, renderingRadius, maxChunkCount);
                //List<Vector3> currentPoints = pointsData.GetAllPoints();

                // --- 更新 VFX Buffer ---
                int currentPointCount = currentPoints.Count;

                // 如果需要，调整 Buffer 大小
                if (pointDataBuffer == null || currentPointCount > pointDataBuffer.count)
                {
                    InitializeBuffer(Mathf.Max(currentPointCount, pointDataBuffer?.count ?? 0) + 1024); // 增加一些余量
                }

                // 准备数据
                pointDataList.Clear();
                for (int i = 0; i < currentPointCount; i++)
                {
                    pointDataList.Add(new PointDataVFX { position = currentPoints[i] });
                }

                // 上传数据到 GPU Buffer
                if (currentPointCount > 0)
                {
                    pointDataBuffer.SetData(pointDataList, 0, 0, currentPointCount);
                }

                // 告知 VFX Graph 当前有效的点数
                pointCloudVFX.SetInt(PointCountID, currentPointCount);

                yield return new WaitForSeconds(scanInterval);
            }
        }


        void Update()
        {
            // 如果需要其他按键触发的功能（例如本地保存 PLY），可以在这里添加
            // 例如：
            if (OVRInput.GetDown(OVRInput.RawButton.X)) // 使用不同按键
            {
                pointCloudVFX.Play();
                //Debug.Log("Local PLY Output Sequence...");
                //ScanAndStorePointCloud(((int)density), pointsData);
                //List<Vector3> allPoints = pointsData.GetAllPoints();
            }
        }

        // --- 保留 GenerateViewSpaceCoords 和 ScanAndStorePointCloud 函数 ---
        // 这些函数与数据采集相关，与渲染方式无关，所以保持不变
        List<Vector2> GenerateViewSpaceCoords(int xSize, int zSize)
        {
            // ... (复制 PointCloudController 中的实现)
            List<Vector2> coords = new List<Vector2>();
            float fovY = mainCamera.fieldOfView * fovMargin;
            float fovX = Camera.VerticalToHorizontalFieldOfView(fovY, mainCamera.aspect) * fovMargin;
            float frustumHeight = 2.0f * Mathf.Tan(fovY * 0.5f * Mathf.Deg2Rad);
            float frustumWidth = 2.0f * Mathf.Tan(fovX * 0.5f * Mathf.Deg2Rad);
            float stepX = frustumWidth / (xSize - 1);
            float stepY = frustumHeight / (zSize - 1);
            for (int z = 0; z < zSize; z++)
            {
                for (int x = 0; x < xSize; x++)
                {
                    float ndcX = (x * stepX - frustumWidth * 0.5f) / (frustumWidth * 0.5f);
                    float ndcY = (z * stepY - frustumHeight * 0.5f) / (frustumHeight * 0.5f);
                    float xCoord = (ndcX + 1) * 0.5f;
                    float yCoord = (ndcY + 1) * 0.5f;
                    coords.Add(new Vector2(xCoord, yCoord));
                }
            }
            return coords;
        }

        void ScanAndStorePointCloud(int sampleSize, ChunkManager pointsData)
        {
            // ... (复制 PointCloudController 中的实现)
            List<Vector2> viewSpaceCoords = GenerateViewSpaceCoords(sampleSize, sampleSize);
            List<Ray> rays = new List<Ray>();
            foreach (Vector2 i in viewSpaceCoords)
            {
                rays.Add(mainCamera.ViewportPointToRay(new Vector3(i.x, i.y, 0)));
            }

            List<EnvironmentRaycastHit> results = new List<EnvironmentRaycastHit>();
            foreach (Ray ray in rays)
            {
                EnvironmentRaycastHit result;
                depthManager.Raycast(ray, out result, maxScanDistance);

                if (Vector3.Distance(result.point, mainCamera.transform.position) < maxScanDistance)
                    results.Add(result);
            }

            // Randomize (确保 ListExtensions.Shuffle 存在)
            // ListExtensions.Shuffle(results);

            foreach (var result in results)
            {
                pointsData.AddPoint(result.point);
            }
        }

        // --- 释放 Buffer ---
        void OnDestroy()
        {
            pointDataBuffer?.Release();
            pointDataBuffer = null;
        }
        void OnDisable()
        {
            pointDataBuffer?.Release();
            pointDataBuffer = null;
        }

        void OnEnable()
        {
            // 如果在运行时重新启用，需要重新创建 Buffer
            if (pointDataBuffer == null && Application.isPlaying)
            {
                int estimatedMaxPoints = (int)density * (int)density * maxChunkCount;
                InitializeBuffer(estimatedMaxPoints > 0 ? estimatedMaxPoints : 1024);
            }
        }
    }
}

