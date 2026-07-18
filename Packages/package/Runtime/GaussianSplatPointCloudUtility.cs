using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    public static class GaussianSplatPointCloudUtility
    {
        /// <summary>
        /// Extract point-cloud data from the GPU-side m_GpuPosData buffer.
        /// Assumes Float32 data with three floats per point.
        /// </summary>
        /// <param name="gpuPosData">GraphicsBuffer containing GPU-side position data.</param>
        /// <returns>The extracted point-cloud positions.</returns>
        public static List<Vector3> GetPointCloudFromGpuPos(GraphicsBuffer gpuPosData)
        {
            List<Vector3> points = new List<Vector3>();
            if (gpuPosData == null)
            {
                Debug.LogError("gpuPosData is null.");
                return points;
            }

            // gpuPosData.count is the total number of floats, with four bytes per element.
            int totalFloats = gpuPosData.count;
            if (totalFloats % 3 != 0)
            {
                Debug.LogWarning("The float count in gpuPosData is not divisible by three; the data format may be invalid.");
            }
            int numPoints = totalFloats / 3;

            // Allocate an array for reading the data.
            float[] posData = new float[totalFloats];
            gpuPosData.GetData(posData);

            for (int i = 0; i < numPoints; i++)
            {
                int idx = i * 3;
                Vector3 p = new Vector3(posData[idx], posData[idx + 1], posData[idx + 2]);
                points.Add(p);
            }

            Debug.Log($"Extracted {points.Count} points from the GPU buffer.");
            return points;
        }

        /// <summary>
        /// Calculate the extracted point-cloud bounds and compare them with the asset metadata.
        /// </summary>
        /// <param name="points">Extracted point-cloud positions.</param>
        /// <param name="asset">GaussianSplatAsset to validate against.</param>
        public static void ValidatePointCloud(List<Vector3> points, GaussianSplatAsset asset)
        {
            if (points == null || points.Count == 0)
            {
                Debug.LogError("The point cloud is empty and cannot be validated.");
                return;
            }

            Vector3 computedMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 computedMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            foreach (var p in points)
            {
                computedMin = Vector3.Min(computedMin, p);
                computedMax = Vector3.Max(computedMax, p);
            }

            Debug.Log($"Computed bounds: min {computedMin}, max {computedMax}");
            Debug.Log($"Bounds recorded in the asset: min {asset.boundsMin}, max {asset.boundsMax}");
        }

        /// <summary>
        /// Check whether the asset position format is Float32 and report the result.
        /// </summary>
        /// <param name="asset">GaussianSplatAsset to inspect.</param>
        public static void CheckDataFormat(GaussianSplatAsset asset)
        {
            if (asset.posFormat == GaussianSplatAsset.VectorFormat.Float32)
            {
                Debug.Log("The asset uses Float32 position data.");
            }
            else
            {
                Debug.LogWarning($"The asset position format is {asset.posFormat}, not Float32. Implement the corresponding decoding logic if required.");
            }
        }
    }
}
