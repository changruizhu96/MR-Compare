using System.Collections.Generic;
using UnityEngine;

namespace Appletea.Dev.PointCloud // 保持原始命名空间
{
    /// <summary>
    /// Manages rendering a point cloud using an object pool of prefabs.
    /// </summary>
    public class PointCloudRenderer : MonoBehaviour
    {
        private GameObject pointPrefab;             // Prefab used for each point.
        private List<GameObject> pointPool;         // List holding the pooled GameObjects.
        private int activePointCount = 0;           // Number of points active in the previous frame.
        private Transform poolParentTransform;      // Parent transform for all pooled objects.

        /// <summary>
        /// Initializes the renderer with a prefab and initial pool size.
        /// Creates a parent object to hold the pooled points.
        /// </summary>
        /// <param name="pointPrefab">The GameObject prefab to use for representing each point.</param>
        /// <param name="initialPoolSize">The initial number of point GameObjects to create.</param>
        public void Initialize(GameObject pointPrefab, int initialPoolSize)
        {
            // Check if prefab is assigned
            if (pointPrefab == null)
            {
                Debug.LogError("Point Prefab is not assigned in the Inspector!", this);
                enabled = false; // Disable component if prefab is missing
                return;
            }
            this.pointPrefab = pointPrefab;

            // Create a parent GameObject to keep the hierarchy clean
            GameObject poolParentObject = new GameObject("[PointCloudRenderer Pool]");
            poolParentTransform = poolParentObject.transform;
            // Optional: Make the pool parent a child of this renderer's GameObject
            // poolParentTransform.SetParent(this.transform);

            // Initialize the object pool
            Debug.Log($"Initializing PointCloudRenderer pool with size: {initialPoolSize}");
            pointPool = new List<GameObject>(initialPoolSize);
            for (int i = 0; i < initialPoolSize; i++)
            {
                // Instantiate the point prefab under the designated parent transform
                GameObject point = Instantiate(pointPrefab, poolParentTransform);
                point.name = $"{pointPrefab.name}_{i}"; // Give a slightly more informative name
                point.SetActive(false); // Start inactive
                pointPool.Add(point);
            }
            activePointCount = 0; // Ensure count starts at 0
        }

        /// <summary>
        /// Updates the displayed points based on the provided list of positions.
        /// Activates/deactivates pooled objects as needed.
        /// Expands the pool if necessary.
        /// </summary>
        /// <param name="points">A list of world space positions for the points to display.</param>
        public void UpdatePointCloud(List<Vector3> points)
        {
            // Check if initialized
            if (pointPool == null || pointPrefab == null || poolParentTransform == null)
            {
                Debug.LogError("PointCloudRenderer is not initialized properly! Call Initialize() first.", this);
                return;
            }

            int requiredPoints = points.Count;

            // Expand the pool if more points are needed than currently exist
            if (requiredPoints > pointPool.Count)
            {
                int currentPoolSize = pointPool.Count;
                int additionalPoints = requiredPoints - currentPoolSize;
                Debug.Log($"Expanding point pool from {currentPoolSize} to {requiredPoints} (adding {additionalPoints}).");
                for (int i = 0; i < additionalPoints; i++)
                {
                    // Instantiate new points under the same parent transform
                    GameObject point = Instantiate(pointPrefab, poolParentTransform);
                    point.name = $"{pointPrefab.name}_{currentPoolSize + i}";
                    point.SetActive(false);
                    pointPool.Add(point);
                }
            }

            // Activate and position the necessary points from the pool
            for (int i = 0; i < requiredPoints; i++)
            {
                GameObject point = pointPool[i];
                point.transform.position = points[i];
                // Only call SetActive(true) if it's currently inactive to potentially save minor overhead
                if (!point.activeSelf)
                {
                    point.SetActive(true);
                }
            }

            // Deactivate any points that were active last frame but are no longer needed
            // Iterate from the current required count up to the count active last frame
            for (int i = requiredPoints; i < activePointCount; i++)
            {
                // Check index validity and if the object is actually active before deactivating
                if (i < pointPool.Count && pointPool[i].activeSelf)
                {
                    pointPool[i].SetActive(false);
                }
            }

            // Update the count of active points for the next frame's comparison
            activePointCount = requiredPoints;
        }

        /// <summary>
        /// Cleans up the created parent object and its children when the script is destroyed.
        /// </summary>
        void OnDestroy()
        {
            // If the parent transform exists, destroy its GameObject, which also destroys all children (pooled points)
            if (poolParentTransform != null)
            {
                Debug.Log($"Destroying PointCloudRenderer pool parent: {poolParentTransform.name}");
                Destroy(poolParentTransform.gameObject);
            }
            // Clear the list reference (the GameObjects themselves are destroyed above)
            pointPool?.Clear();
        }
    }
}