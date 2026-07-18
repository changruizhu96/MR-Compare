using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using GaussianSplatting.Runtime;
using Unity.Mathematics;

/// <summary>
/// PGV point-cloud preprocessor.
/// Coordinates GPU voxelization with CPU eigendecomposition.
/// </summary>
public class PointCloudPreprocessor
{
    public int[] LastRawGaussianToSuperGaussianMap { get; private set; }

    // Configuration
    private const int DEFAULT_GRID_SIZE = 10000000;  // 10M entries for large user scenes

    // Compute-shader reference
    private ComputeShader computeShader;
    private int kernelClearVoxels;
    private int kernelPGVVoxelize;

    // === GPU Buffers ===
    private ComputeBuffer voxelBuffer;

    /// <summary>
    /// Initialize and locate the compute shader automatically.
    /// </summary>
    public PointCloudPreprocessor()
    {
        // SplatUtilitiesSurface.compute is extended by this package, so the shader reference
        // must be obtained from GaussianSplatRenderer in actual use.
    }

    /// <summary>
    /// Initialize with the specified compute shader.
    /// </summary>
    public PointCloudPreprocessor(ComputeShader shader)
    {
        computeShader = shader;
        if (computeShader != null)
        {
            kernelClearVoxels = computeShader.FindKernel("CSClearPGVVoxels");
            kernelPGVVoxelize = computeShader.FindKernel("CSPGVVoxelize");
        }
    }

    /// <summary>
    /// Execute PGV.
    /// </summary>
    public SuperGaussian[] ComputePGV(
        GaussianSplatRenderer gsRenderer,
        float voxelSize,
        float minOpacity = 0.1f,
        float scaleRatio = 0.5f,
        bool computeCovariances = true)
    {
        // 1. GPU: voxelization and covariance aggregation
        var voxelData = GPUVoxelize(gsRenderer, voxelSize, minOpacity, scaleRatio);

        // 2. CPU: read voxel data
        var voxels = ReadVoxelsFromGPU(voxelData);

        // 3. CPU: extract normals through eigendecomposition
        var superGaussians = ExtractSuperGaussians(voxels, computeCovariances);

        // 4. Clean up.
        CleanupBuffers();

        return superGaussians;
    }

    /// <summary>
    /// Simple voxel filter used for comparison.
    /// Computes voxel centers without covariance matrices or normals.
    /// </summary>
    public (Vector3[] positions, Color[] colors) ComputeVoxelFilter(
        GaussianSplatRenderer gsRenderer,
        float voxelSize,
        float minOpacity = 0.1f,
        float scaleRatio = 0.5f)
    {
        // 1. GPU: reuse the PGV voxelization path.
        var voxelData = GPUVoxelize(gsRenderer, voxelSize, minOpacity, scaleRatio);

        // 2. CPU: read voxel data.
        var voxels = ReadVoxelsFromGPU(voxelData);

        // 3. CPU: extract center points without eigendecomposition.
        var (positions, colors) = ExtractVoxelCenters(voxels);

        // 4. Clean up.
        CleanupBuffers();

        return (positions, colors);
    }

    /// <summary>
    /// Extract voxel centers without calculating normals.
    /// </summary>
    private (Vector3[] positions, Color[] colors) ExtractVoxelCenters(PGVVoxelData[] voxels)
    {
        // Retain voxels with at least five points, matching the PGV path.
        var validVoxels = voxels.Where(v => v.count >= 5).ToArray();

        Debug.Log($"[Voxel Filter] Valid voxels: {validVoxels.Length}");

        Vector3[] positions = new Vector3[validVoxels.Length];
        Color[] colors = new Color[validVoxels.Length];

        for (int i = 0; i < validVoxels.Length; i++)
        {
            var voxel = validVoxels[i];

            // Weighted-average position
            positions[i] = voxel.pos_sum / voxel.weight_sum;

            // Default to white; color can be derived from splats later.
            colors[i] = Color.white;
        }

        return (positions, colors);
    }



    public (Vector3[] positions, Color[] colors) ComputeVoxelFilterCPU(GaussianSplatRenderer gsRenderer, float voxelSize, int minPoints = 1)
    {
        if (gsRenderer.m_BakedData == null || gsRenderer.m_BakedData.PointCount == 0)
            return (new Vector3[0], new Color[0]);

        var inputPos = gsRenderer.m_BakedData.positions;
        var inputCol = gsRenderer.m_BakedData.colors;
        var voxelDict = new Dictionary<Vector3Int, VoxelAccumulator>();

        for (int i = 0; i < inputPos.Length; i++)
        {
            Vector3Int key = new Vector3Int(
                Mathf.FloorToInt(inputPos[i].x / voxelSize),
                Mathf.FloorToInt(inputPos[i].y / voxelSize),
                Mathf.FloorToInt(inputPos[i].z / voxelSize));
            if (!voxelDict.ContainsKey(key))
                voxelDict[key] = new VoxelAccumulator();
            var acc = voxelDict[key];
            acc.posSum += inputPos[i];
            acc.colorSum += new Vector3(inputCol[i].r, inputCol[i].g, inputCol[i].b);
            acc.count++;
            voxelDict[key] = acc;
        }

        var resultPos = new List<Vector3>();
        var resultCol = new List<Color>();
        foreach (var kvp in voxelDict)
        {
            if (kvp.Value.count >= minPoints)
            {
                resultPos.Add(kvp.Value.posSum / kvp.Value.count);
                Vector3 avgCol = kvp.Value.colorSum / kvp.Value.count;
                resultCol.Add(new Color(avgCol.x, avgCol.y, avgCol.z, 1f));
            }
        }
        Debug.Log($"[CPU Voxel Filter] {resultPos.Count} voxels (minPoints={minPoints})");
        return (resultPos.ToArray(), resultCol.ToArray());
    }

    // public SuperGaussian[] ComputePGVCPU(GaussianSplatRenderer gsRenderer, float voxelSize, bool computeCovariances = true, bool useCountWeight = false)
    // {
    //     if (gsRenderer.m_BakedData == null || gsRenderer.m_BakedData.PointCount == 0)
    //         return new SuperGaussian[0];

    //     var inputPos = gsRenderer.m_BakedData.positions;
    //     var inputCol = gsRenderer.m_BakedData.colors;

    //     // Prefer the dedicated opacity array; otherwise use Color.a.
    //     float[] inputOpacity;
    //     if (gsRenderer.m_BakedData.opacities != null && gsRenderer.m_BakedData.opacities.Length == inputPos.Length)
    //     {
    //         inputOpacity = gsRenderer.m_BakedData.opacities;
    //     }
    //     else
    //     {
    //         // Fall back to Color.a as opacity.
    //         inputOpacity = new float[inputCol.Length];
    //         for (int i = 0; i < inputCol.Length; i++)
    //             inputOpacity[i] = inputCol[i].a;
    //     }

    //     // Summarize the opacity distribution.
    //     float minOp = float.MaxValue, maxOp = float.MinValue, sumOp = 0f;
    //     for (int i = 0; i < inputOpacity.Length; i++)
    //     {
    //         minOp = Mathf.Min(minOp, inputOpacity[i]);
    //         maxOp = Mathf.Max(maxOp, inputOpacity[i]);
    //         sumOp += inputOpacity[i];
    //     }
    //     float avgOp = sumOp / inputOpacity.Length;

    //     Debug.Log($"[CPU PGV] Input: {inputPos.Length} points, useCountWeight: {useCountWeight}\n" +
    //         $"  Opacity range: [{minOp:F3}, {maxOp:F3}], avg: {avgOp:F3}\n" +
    //         $"  → Expected weight range: [{minOp:F3} × flatness, {maxOp:F3} × flatness]");

    //     var voxelDict = new Dictionary<Vector3Int, PGVVoxelAccumulatorCPU>();

    //     for (int i = 0; i < inputPos.Length; i++)
    //     {
    //         Vector3 pos = inputPos[i];
    //         Vector3Int key = new Vector3Int(
    //             Mathf.FloorToInt(pos.x / voxelSize),
    //             Mathf.FloorToInt(pos.y / voxelSize),
    //             Mathf.FloorToInt(pos.z / voxelSize));

    //         if (!voxelDict.ContainsKey(key))
    //         {
    //             voxelDict[key] = new PGVVoxelAccumulatorCPU
    //             {
    //                 posSum = Vector3.zero,
    //                 colorSum = Vector3.zero,
    //                 covSum = new float[6],
    //                 opacitySum = 0f,
    //                 count = 0
    //             };
    //         }

    //         var acc = voxelDict[key];
    //         acc.posSum += pos;
    //         acc.colorSum += new Vector3(inputCol[i].r, inputCol[i].g, inputCol[i].b);
    //         acc.opacitySum += inputOpacity[i];  // Accumulate opacity.
    //         acc.count++;
    //         acc.covSum[0] += pos.x * pos.x;
    //         acc.covSum[1] += pos.x * pos.y;
    //         acc.covSum[2] += pos.x * pos.z;
    //         acc.covSum[3] += pos.y * pos.y;
    //         acc.covSum[4] += pos.y * pos.z;
    //         acc.covSum[5] += pos.z * pos.z;
    //         voxelDict[key] = acc;
    //     }

    //     var result = new List<SuperGaussian>();
    //     foreach (var kvp in voxelDict)
    //     {
    //         if (kvp.Value.count < 5) continue;

    //         var acc = kvp.Value;
    //         Vector3 avgPos = acc.posSum / acc.count;

    //         float[] avgCov = new float[6];
    //         for (int i = 0; i < 6; i++)
    //             avgCov[i] = acc.covSum[i] / acc.count;

    //         Matrix3x3 cov = new Matrix3x3();
    //         cov[0, 0] = avgCov[0] - avgPos.x * avgPos.x;
    //         cov[0, 1] = avgCov[1] - avgPos.x * avgPos.y;
    //         cov[0, 2] = avgCov[2] - avgPos.x * avgPos.z;
    //         cov[1, 0] = cov[0, 1];
    //         cov[1, 1] = avgCov[3] - avgPos.y * avgPos.y;
    //         cov[1, 2] = avgCov[4] - avgPos.y * avgPos.z;
    //         cov[2, 0] = cov[0, 2];
    //         cov[2, 1] = cov[1, 2];
    //         cov[2, 2] = avgCov[5] - avgPos.z * avgPos.z;

    //         var (eigenvalues, eigenvectors) = EigenDecomposition3x3(cov);
    //         Vector3 normal = eigenvectors[2].normalized;
    //         float flatness = 1f - (eigenvalues[2] / Mathf.Max(eigenvalues[0], 1e-6f));

    //         // Optionally weight the result by opacity.
    //         float weight;
    //         if (useCountWeight)
    //         {
    //             // Use average opacity as the quality term.
    //             float avgOpacity = acc.opacitySum / acc.count;
    //             weight = flatness * avgOpacity;
    //         }
    //         else
    //         {
    //             // Geometry-only quality
    //             weight = flatness;
    //         }

    //         var sg = new SuperGaussian
    //         {
    //             position = avgPos,
    //             normal = normal,
    //             weight = weight,
    //             sourceCount = acc.count,
    //             color = new Color(acc.colorSum.x / acc.count, acc.colorSum.y / acc.count, acc.colorSum.z / acc.count, 1f)
    //         };

    //         if (computeCovariances)
    //             sg.SetCovariance3x3(cov[0, 0], cov[0, 1], cov[0, 2], cov[1, 0], cov[1, 1], cov[1, 2], cov[2, 0], cov[2, 1], cov[2, 2]);

    //         result.Add(sg);
    //     }

    //     Debug.Log($"[CPU PGV] Output: {result.Count} Super-Gaussians");
    //     return result.ToArray();
    // }
    public SuperGaussian[] ComputePGVCPU(GaussianSplatRenderer gsRenderer, float voxelSize, bool computeCovariances = true, bool useCountWeight = false, int minPoints = 1)
    {
        if (gsRenderer.m_BakedData == null || gsRenderer.m_BakedData.PointCount == 0)
            return new SuperGaussian[0];

        var inputPos = gsRenderer.m_BakedData.positions;
        var inputCol = gsRenderer.m_BakedData.colors;

        // Prefer the dedicated opacity array; otherwise use Color.a.
        float[] inputOpacity;
        if (gsRenderer.m_BakedData.opacities != null && gsRenderer.m_BakedData.opacities.Length == inputPos.Length)
        {
            inputOpacity = gsRenderer.m_BakedData.opacities;
        }
        else
        {
            inputOpacity = new float[inputCol.Length];
            for (int i = 0; i < inputCol.Length; i++)
                inputOpacity[i] = inputCol[i].a;
        }

        // Summarize the opacity distribution.
        float minOp = float.MaxValue, maxOp = float.MinValue, sumOp = 0f;
        for (int i = 0; i < inputOpacity.Length; i++)
        {
            minOp = Mathf.Min(minOp, inputOpacity[i]);
            maxOp = Mathf.Max(maxOp, inputOpacity[i]);
            sumOp += inputOpacity[i];
        }
        float avgOp = sumOp / inputOpacity.Length;

        Debug.Log($"[CPU PGV] Input: {inputPos.Length} points, useCountWeight: {useCountWeight}\n" +
            $"  Opacity range: [{minOp:F3}, {maxOp:F3}], avg: {avgOp:F3}\n" +
            $"  → Expected weight range: [{minOp:F3} × flatness, {maxOp:F3} × flatness]");

        var voxelDict = new Dictionary<Vector3Int, PGVVoxelAccumulatorCPU>();

        // 1. Accumulate the global centroid.
        Vector3 totalPosSum = Vector3.zero;

        for (int i = 0; i < inputPos.Length; i++)
        {
            Vector3 pos = inputPos[i];

            // Accumulate every point position.
            totalPosSum += pos;

            Vector3Int key = new Vector3Int(
                Mathf.FloorToInt(pos.x / voxelSize),
                Mathf.FloorToInt(pos.y / voxelSize),
                Mathf.FloorToInt(pos.z / voxelSize));

            if (!voxelDict.ContainsKey(key))
            {
                voxelDict[key] = new PGVVoxelAccumulatorCPU
                {
                    posSum = Vector3.zero,
                    colorSum = Vector3.zero,
                    covSum = new float[6],
                    opacitySum = 0f,
                    count = 0
                };
            }

            var acc = voxelDict[key];
            acc.posSum += pos;
            acc.colorSum += new Vector3(inputCol[i].r, inputCol[i].g, inputCol[i].b);
            acc.opacitySum += inputOpacity[i];
            acc.count++;
            acc.covSum[0] += pos.x * pos.x;
            acc.covSum[1] += pos.x * pos.y;
            acc.covSum[2] += pos.x * pos.z;
            acc.covSum[3] += pos.y * pos.y;
            acc.covSum[4] += pos.y * pos.z;
            acc.covSum[5] += pos.z * pos.z;
            voxelDict[key] = acc;
        }

        // 2. Calculate the global centroid.
        Vector3 globalCentroid = totalPosSum / inputPos.Length;
        Debug.Log($"[CPU PGV] Global Centroid Calculated at: {globalCentroid}");

        var result = new List<SuperGaussian>();

        // Filter valid voxels and convert them to a list.
        var voxelList = voxelDict.Where(kvp => kvp.Value.count >= minPoints).Select(kvp => kvp.Value).ToList();

        Debug.Log($"[PGV Stats] Valid voxels (>={minPoints}): {voxelList.Count:N0}");

        // Process the voxels in parallel.
        System.Threading.Tasks.Parallel.For(0, voxelList.Count, i =>
        {
            var voxel = voxelList[i]; // PGVVoxelAccumulatorCPU

            // Use posSum rather than pos_sum.
            Vector3 avgPos = voxel.posSum / voxel.count;

            float[] avgCov = new float[6];
            for (int k = 0; k < 6; k++)
                avgCov[k] = voxel.covSum[k] / voxel.count;

            Matrix3x3 cov = new Matrix3x3();
            cov[0, 0] = avgCov[0] - avgPos.x * avgPos.x;
            cov[0, 1] = avgCov[1] - avgPos.x * avgPos.y;
            cov[0, 2] = avgCov[2] - avgPos.x * avgPos.z;
            cov[1, 0] = cov[0, 1];
            cov[1, 1] = avgCov[3] - avgPos.y * avgPos.y;
            cov[1, 2] = avgCov[4] - avgPos.y * avgPos.z;
            cov[2, 0] = cov[0, 2];
            cov[2, 1] = cov[1, 2];
            cov[2, 2] = avgCov[5] - avgPos.z * avgPos.z;

            // Eigendecomposition
            var (eigenvalues, eigenvectors) = EigenDecomposition3x3(cov);

            // Initial normal
            Vector3 normal = eigenvectors[2].normalized;

            // 3. Orient the normal toward the global centroid.
            // Calculate the direction from this voxel to the global centroid.
            Vector3 dirToCentroid = globalCentroid - avgPos;

            // Force every normal to point inward toward the centroid.
            if (Vector3.Dot(normal, dirToCentroid) < 0)
            {
                normal = -normal;
            }

            // Calculate flatness.
            float flatness = 1f - (eigenvalues[2] / Mathf.Max(eigenvalues[0], 1e-6f));

            // Calculate weight.
            float weight;
            if (useCountWeight)
            {
                // Use opacitySum rather than opacity_sum.
                float avgOpacity = voxel.opacitySum / voxel.count;
                weight = flatness * avgOpacity;
            }
            else
            {
                weight = flatness;
            }

            var sg = new SuperGaussian
            {
                position = avgPos,
                normal = normal, // Oriented normal
                weight = weight,
                avgOpacity = voxel.opacitySum / voxel.count,
                sourceCount = voxel.count,
                // Use colorSum rather than color_sum.
                color = new Color(voxel.colorSum.x / voxel.count, voxel.colorSum.y / voxel.count, voxel.colorSum.z / voxel.count, 1f)
            };

            if (computeCovariances)
            {
                sg.SetCovariance3x3(
                    cov[0, 0], cov[0, 1], cov[0, 2],
                    cov[1, 0], cov[1, 1], cov[1, 2],
                    cov[2, 0], cov[2, 1], cov[2, 2]
                );
            }

            lock (result)
            {
                result.Add(sg);
            }
        });

        Debug.Log($"[CPU PGV] Output: {result.Count} Super-Gaussians (Normals Oriented towards Centroid)");
        return result.ToArray();
    }

    /// <summary>
    /// Perform GPU voxelization.
    /// </summary>
    private ComputeBuffer GPUVoxelize(
        GaussianSplatRenderer gsRenderer,
        float voxelSize,
        float minOpacity,
        float scaleRatio)
    {
        // Obtain the compute shader from GaussianSplatRenderer.
        if (computeShader == null)
        {
            computeShader = gsRenderer.m_CSSplatUtilities;
            kernelClearVoxels = computeShader.FindKernel("CSClearPGVVoxels");
            kernelPGVVoxelize = computeShader.FindKernel("CSPGVVoxelize");
        }

        // Create the voxel buffer.
        int gridSize = DEFAULT_GRID_SIZE;
        int voxelStride = sizeof(float) * (3 + 1 + 9) + sizeof(uint);  // pos_sum(3) + weight_sum(1) + cov_sum(9) + count(1)
        voxelBuffer = new ComputeBuffer(gridSize, voxelStride);

        // Clear the voxel buffer.
        computeShader.SetInt("_PGVGridSize", gridSize);
        computeShader.SetFloat("_PGVVoxelSize", voxelSize);
        computeShader.SetBuffer(kernelClearVoxels, "_PGVVoxelBuffer", voxelBuffer);

        int clearThreadGroups = Mathf.CeilToInt(gridSize / 1024f);
        computeShader.Dispatch(kernelClearVoxels, clearThreadGroups, 1, 1);

        // Set the existing filtering parameters.
        computeShader.SetFloat("_FilterMinOpacity", minOpacity);
        computeShader.SetFloat("_FilterScaleRatio", scaleRatio);

        // Bind every buffer required for voxelization.
        computeShader.SetBuffer(kernelPGVVoxelize, "_PGVVoxelBuffer", voxelBuffer);

        // Bind the splat-data buffers.
        gsRenderer.SetPreprocessorComputeBuffers(kernelPGVVoxelize);

        // Use the correct point count for BakedData or source data.
        int splatCount = (gsRenderer.m_BakedData != null && gsRenderer.m_BakedData.PointCount > 0)
            ? gsRenderer.m_BakedData.PointCount
            : gsRenderer.splatCount;
        computeShader.SetInt("_SplatCount", splatCount);

        int voxelizeThreadGroups = Mathf.CeilToInt(splatCount / 1024f);
        computeShader.Dispatch(kernelPGVVoxelize, voxelizeThreadGroups, 1, 1);

        return voxelBuffer;
    }

    /// <summary>
    /// Read voxel data from the GPU.
    /// </summary>
    private PGVVoxelData[] ReadVoxelsFromGPU(ComputeBuffer buffer)
    {
        int count = buffer.count;
        PGVVoxelData[] voxels = new PGVVoxelData[count];
        buffer.GetData(voxels);
        return voxels;
    }

    /// <summary>
    /// Extract Super-Gaussians on the CPU.
    /// </summary>
    private SuperGaussian[] ExtractSuperGaussians(PGVVoxelData[] voxels, bool computeCovariances)
    {
        List<SuperGaussian> result = new List<SuperGaussian>();

        // Process all voxels in parallel.
        // Use an aggressive minimum point-count threshold.
        var validVoxels = voxels.Where(v => v.count >= 5).ToArray();  // At least five points to reject isolated splats

        // Diagnostic statistics
        int totalNonEmpty = voxels.Count(v => v.count > 0);
        int singlePoint = voxels.Count(v => v.count == 1);
        int twoPoints = voxels.Count(v => v.count == 2);
        float occupancy = (float)totalNonEmpty / voxels.Length * 100f;

        Debug.Log($"[PGV Stats]\n" +
            $"  Grid Size: {voxels.Length:N0}\n" +
            $"  Non-empty voxels: {totalNonEmpty:N0} ({occupancy:F2}% occupancy)\n" +
            $"  Single-point voxels: {singlePoint:N0} (potential noise/collision)\n" +
            $"  Two-point voxels: {twoPoints:N0}\n" +
            $"  Valid voxels (>=3): {validVoxels.Length:N0}\n" +
            $"  ⚠️ High single-point ratio indicates hash collisions!");

        System.Threading.Tasks.Parallel.For(0, validVoxels.Length, i =>
        {
            var voxel = validVoxels[i];

            // Calculate the average position.
            Vector3 position = voxel.pos_sum / voxel.weight_sum;

            // Calculate the average covariance.
            Matrix3x3 covariance = new Matrix3x3();
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    float value = voxel[r * 3 + c] / voxel.weight_sum;
                    covariance[r, c] = value;
                }
            }

            // Extract the normal through eigendecomposition.
            var (eigenvalues, eigenvectors) = EigenDecomposition3x3(covariance);

            // The normal is the eigenvector associated with the smallest eigenvalue.
            Vector3 normal = eigenvectors[2].normalized;

            // Flatness = 1 - (λ_min / λ_max)
            float flatness = 1f - (eigenvalues[2] / Mathf.Max(eigenvalues[0], 1e-6f));

            // Combined weight
            float weight = (voxel.weight_sum / voxel.count) * flatness;

            // Create the SuperGaussian.
            SuperGaussian sg = new SuperGaussian
            {
                position = position,
                normal = normal,
                weight = weight,
                avgOpacity = voxel.weight_sum / Mathf.Max(1u, voxel.count),
                sourceCount = (int)voxel.count,
                color = Color.white  // Color can be derived from splats later.
            };

            if (computeCovariances)
            {
                sg.SetCovariance3x3(
                    covariance[0, 0], covariance[0, 1], covariance[0, 2],
                    covariance[1, 0], covariance[1, 1], covariance[1, 2],
                    covariance[2, 0], covariance[2, 1], covariance[2, 2]
                );
            }

            lock (result)
            {
                result.Add(sg);
            }
        });

        return result.ToArray();
    }

    /// <summary>
    /// Perform 3x3 matrix eigendecomposition with Jacobi iteration.
    /// Returns eigenvalues in descending order and their eigenvectors.
    /// </summary>
    private (Vector3 eigenvalues, Vector3[] eigenvectors) EigenDecomposition3x3(Matrix3x3 A)
    {
        // Jacobi iteration is suitable for symmetric matrices.
        const int MAX_ITERATIONS = 50;
        const float EPSILON = 1e-6f;

        // Initialize to the identity matrix.
        Matrix3x3 V = Matrix3x3.identity;
        Matrix3x3 D = A;

        for (int iter = 0; iter < MAX_ITERATIONS; iter++)
        {
            // Find the largest off-diagonal element.
            int p = 0, q = 1;
            float maxVal = Mathf.Abs(D[0, 1]);

            if (Mathf.Abs(D[0, 2]) > maxVal) { p = 0; q = 2; maxVal = Mathf.Abs(D[0, 2]); }
            if (Mathf.Abs(D[1, 2]) > maxVal) { p = 1; q = 2; maxVal = Mathf.Abs(D[1, 2]); }

            // Check convergence.
            if (maxVal < EPSILON) break;

            // Calculate the rotation angle.
            float theta = 0.5f * Mathf.Atan2(2f * D[p, q], D[q, q] - D[p, p]);
            float c = Mathf.Cos(theta);
            float s = Mathf.Sin(theta);

            // Givens rotation
            Matrix3x3 G = Matrix3x3.identity;
            G[p, p] = c; G[p, q] = -s;
            G[q, p] = s; G[q, q] = c;

            D = G.Transpose() * D * G;
            V = V * G;
        }

        // Extract eigenvalues.
        Vector3 eigenvalues = new Vector3(D[0, 0], D[1, 1], D[2, 2]);

        // Extract eigenvectors as column vectors.
        Vector3[] eigenvectors = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            eigenvectors[i] = new Vector3(V[0, i], V[1, i], V[2, i]);
        }

        // Sort by eigenvalue in descending order.
        SortEigenPairs(ref eigenvalues, ref eigenvectors);

        return (eigenvalues, eigenvectors);
    }

    /// <summary>
    /// Sort eigenpairs by eigenvalue in descending order.
    /// </summary>
    private void SortEigenPairs(ref Vector3 eigenvalues, ref Vector3[] eigenvectors)
    {
        // Bubble sort is sufficient for three elements.
        for (int i = 0; i < 2; i++)
        {
            for (int j = i + 1; j < 3; j++)
            {
                if (eigenvalues[j] > eigenvalues[i])
                {
                    // Swap eigenvalues.
                    float tempVal = eigenvalues[i];
                    eigenvalues[i] = eigenvalues[j];
                    eigenvalues[j] = tempVal;

                    // Swap eigenvectors.
                    Vector3 tempVec = eigenvectors[i];
                    eigenvectors[i] = eigenvectors[j];
                    eigenvectors[j] = tempVec;
                }
            }
        }
    }

    /// <summary>
    /// Release GPU buffers.
    /// </summary>
    private void CleanupBuffers()
    {
        voxelBuffer?.Release();
        voxelBuffer = null;
    }

    /// <summary>
    /// Calculate PGV from source Gaussian data using the full law of total covariance.
    /// Includes Term I (individual covariance) and Term II (center distribution).
    /// </summary>
    /// <param name="gsRenderer">GaussianSplatRenderer instance.</param>
    /// <param name="voxelSize">Voxel size in meters.</param>
    /// <param name="includeTermI">Whether to include Term I, the individual Gaussian covariance.</param>
    /// <param name="minOpacity">Minimum opacity used for filtering.</param>
    /// <param name="maxScaleRatio">Maximum scale ratio used to reject spherical points.</param>
    /// <param name="computeCovariances">Whether to output complete covariance matrices.</param>
    /// <param name="useCountWeight">Whether to use opacity weighting.</param>
    /// <returns>An array of SuperGaussians.</returns>
    public SuperGaussian[] ComputePGVFromRaw(
        GaussianSplatRenderer gsRenderer,
        float voxelSize,
        bool includeTermI,
        float minOpacity = 0.1f,
        float maxScaleRatio = 0.5f,
        bool computeCovariances = true,
        bool useCountWeight = false,
        int minPoints = 1)
    {
        LastRawGaussianToSuperGaussianMap = null;

        if (gsRenderer == null || gsRenderer.splatCount == 0)
            return new SuperGaussian[0];

        // 1. Read source Gaussian data, including scale and rotation.
        var rawData = ReadRawGaussianData(gsRenderer);
        if (rawData == null || rawData.Length == 0)
        {
            Debug.LogWarning("[PGV FromRaw] Failed to read raw Gaussian data, falling back to CPU version");
            return ComputePGVCPU(gsRenderer, voxelSize, computeCovariances, useCountWeight);
        }

        Debug.Log($"[PGV FromRaw] Input: {rawData.Length} raw Gaussians, includeTermI: {includeTermI}");

        // 2. Voxelize and accumulate.
        var voxelDict = new Dictionary<Vector3Int, PGVVoxelAccumulatorCPU>();
        var rawVoxelKeys = new Vector3Int[rawData.Length];
        var rawVoxelKeyValid = new bool[rawData.Length];
        Vector3 totalPosSum = Vector3.zero;
        int filteredCount = 0;

        for (int i = 0; i < rawData.Length; i++)
        {
            var splat = rawData[i];

            // Opacity filtering
            if (splat.opacity < minOpacity)
                continue;

            // Scale-ratio filtering to remove spherical points
            Vector3 scale = splat.scale;
            float minS = Mathf.Min(scale.x, Mathf.Min(scale.y, scale.z));
            float maxS = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
            float scaleRatio = maxS > 1e-6f ? (minS / maxS) : 1f;
            if (scaleRatio > maxScaleRatio)
                continue;

            Vector3 pos = splat.pos;
            totalPosSum += pos;
            filteredCount++;

            // Calculate the voxel key.
            Vector3Int key = new Vector3Int(
                Mathf.FloorToInt(pos.x / voxelSize),
                Mathf.FloorToInt(pos.y / voxelSize),
                Mathf.FloorToInt(pos.z / voxelSize)
            );

            // Get or create the accumulator.
            if (!voxelDict.TryGetValue(key, out var acc))
            {
                acc = new PGVVoxelAccumulatorCPU
                {
                    posSum = Vector3.zero,
                    colorSum = Vector3.zero,
                    covSum = new float[6],
                    splatCovSum = includeTermI ? new float[9] : null,
                    opacitySum = 0f,
                    count = 0
                };
            }

            // Term II: accumulate positions and their second moments.
            acc.posSum += pos;
            acc.covSum[0] += pos.x * pos.x;
            acc.covSum[1] += pos.x * pos.y;
            acc.covSum[2] += pos.x * pos.z;
            acc.covSum[3] += pos.y * pos.y;
            acc.covSum[4] += pos.y * pos.z;
            acc.covSum[5] += pos.z * pos.z;

            // Term I: accumulate individual Gaussian covariance matrices.
            if (includeTermI && acc.splatCovSum != null)
            {
                // Construct R * S² * R^T from scale and rotation.
                Matrix3x3 splatCov = ComputeGaussianCovariance(splat.scale, splat.rot);
                acc.splatCovSum[0] += splatCov[0, 0];
                acc.splatCovSum[1] += splatCov[0, 1];
                acc.splatCovSum[2] += splatCov[0, 2];
                acc.splatCovSum[3] += splatCov[1, 0];
                acc.splatCovSum[4] += splatCov[1, 1];
                acc.splatCovSum[5] += splatCov[1, 2];
                acc.splatCovSum[6] += splatCov[2, 0];
                acc.splatCovSum[7] += splatCov[2, 1];
                acc.splatCovSum[8] += splatCov[2, 2];
            }

            // Accumulate color and opacity.
            acc.colorSum += new Vector3(splat.dc0.x, splat.dc0.y, splat.dc0.z);
            acc.opacitySum += splat.opacity;
            acc.count++;

            voxelDict[key] = acc;
            rawVoxelKeys[i] = key;
            rawVoxelKeyValid[i] = true;
        }

        Debug.Log($"[PGV FromRaw] After filtering: {filteredCount} points, {voxelDict.Count} voxels");

        // 3. Calculate the global centroid.
        Vector3 globalCentroid = totalPosSum / Mathf.Max(1, filteredCount);

        // 4. Extract Super-Gaussians.
        var voxelEntries = voxelDict.Where(kvp => kvp.Value.count >= minPoints).ToList();
        var result = new SuperGaussian[voxelEntries.Count];

        Debug.Log($"[PGV FromRaw] Valid voxels (>={minPoints}): {voxelEntries.Count:N0}");

        System.Threading.Tasks.Parallel.For(0, voxelEntries.Count, i =>
        {
            var voxel = voxelEntries[i].Value;
            Vector3 avgPos = voxel.posSum / voxel.count;

            // Term II: center-distribution covariance = E[μμ^T] - μ̄μ̄^T
            float[] avgCov = new float[6];
            for (int k = 0; k < 6; k++)
                avgCov[k] = voxel.covSum[k] / voxel.count;

            Matrix3x3 termII = new Matrix3x3();
            termII[0, 0] = avgCov[0] - avgPos.x * avgPos.x;
            termII[0, 1] = avgCov[1] - avgPos.x * avgPos.y;
            termII[0, 2] = avgCov[2] - avgPos.x * avgPos.z;
            termII[1, 0] = termII[0, 1];
            termII[1, 1] = avgCov[3] - avgPos.y * avgPos.y;
            termII[1, 2] = avgCov[4] - avgPos.y * avgPos.z;
            termII[2, 0] = termII[0, 2];
            termII[2, 1] = termII[1, 2];
            termII[2, 2] = avgCov[5] - avgPos.z * avgPos.z;

            // Term I: average individual covariance = E[Σ_k]
            Matrix3x3 termI = new Matrix3x3();
            if (includeTermI && voxel.splatCovSum != null)
            {
                termI[0, 0] = voxel.splatCovSum[0] / voxel.count;
                termI[0, 1] = voxel.splatCovSum[1] / voxel.count;
                termI[0, 2] = voxel.splatCovSum[2] / voxel.count;
                termI[1, 0] = voxel.splatCovSum[3] / voxel.count;
                termI[1, 1] = voxel.splatCovSum[4] / voxel.count;
                termI[1, 2] = voxel.splatCovSum[5] / voxel.count;
                termI[2, 0] = voxel.splatCovSum[6] / voxel.count;
                termI[2, 1] = voxel.splatCovSum[7] / voxel.count;
                termI[2, 2] = voxel.splatCovSum[8] / voxel.count;
            }

            // Total covariance = Term I + Term II
            Matrix3x3 totalCov = new Matrix3x3();
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    totalCov[r, c] = termI[r, c] + termII[r, c];

            // Eigendecomposition
            var (eigenvalues, eigenvectors) = EigenDecomposition3x3(totalCov);

            // The normal is the eigenvector associated with the smallest eigenvalue.
            Vector3 normal = eigenvectors[2].normalized;

            // Orient the normal.
            Vector3 dirToCentroid = globalCentroid - avgPos;
            if (Vector3.Dot(normal, dirToCentroid) < 0)
                normal = -normal;

            // Calculate flatness.
            float flatness = 1f - (eigenvalues[2] / Mathf.Max(eigenvalues[0], 1e-6f));

            // Calculate weight.
            float weight;
            if (useCountWeight)
            {
                float avgOpacity = voxel.opacitySum / voxel.count;
                weight = flatness * avgOpacity;
            }
            else
            {
                weight = flatness;
            }

            var sg = new SuperGaussian
            {
                position = avgPos,
                normal = normal,
                weight = weight,
                avgOpacity = voxel.opacitySum / voxel.count,
                sourceCount = voxel.count,
                color = new Color(voxel.colorSum.x / voxel.count, voxel.colorSum.y / voxel.count, voxel.colorSum.z / voxel.count, 1f)
            };

            if (computeCovariances)
            {
                sg.SetCovariance3x3(
                    totalCov[0, 0], totalCov[0, 1], totalCov[0, 2],
                    totalCov[1, 0], totalCov[1, 1], totalCov[1, 2],
                    totalCov[2, 0], totalCov[2, 1], totalCov[2, 2]
                );
            }

            result[i] = sg;
        });

        var voxelKeyToSuperIndex = new Dictionary<Vector3Int, int>(voxelEntries.Count);
        for (int i = 0; i < voxelEntries.Count; i++)
        {
            voxelKeyToSuperIndex[voxelEntries[i].Key] = i;
        }

        var rawToSuper = new int[rawData.Length];
        for (int i = 0; i < rawToSuper.Length; i++)
            rawToSuper[i] = -1;

        int mappedCount = 0;
        for (int i = 0; i < rawData.Length; i++)
        {
            if (!rawVoxelKeyValid[i])
                continue;

            if (!voxelKeyToSuperIndex.TryGetValue(rawVoxelKeys[i], out int superIndex))
                continue;

            rawToSuper[i] = superIndex;
            mappedCount++;
        }

        LastRawGaussianToSuperGaussianMap = rawToSuper;

        Debug.Log($"[PGV FromRaw] Output: {result.Length} Super-Gaussians (includeTermI: {includeTermI}, mappedRawGaussians: {mappedCount})");
        return result;
    }

    /// <summary>
    /// Calculate the Gaussian covariance matrix from scale and rotation: Σ = R * diag(s²) * R^T.
    /// </summary>
    private Matrix3x3 ComputeGaussianCovariance(Vector3 scale, Quaternion rotation)
    {
        // S² = diag(σx², σy², σz²)
        float sx2 = scale.x * scale.x;
        float sy2 = scale.y * scale.y;
        float sz2 = scale.z * scale.z;

        // R = rotation matrix from quaternion
        Matrix4x4 rotMat = Matrix4x4.Rotate(rotation);
        
        // Σ = R * S² * R^T
        // Calculate directly to avoid allocating intermediate matrices.
        Matrix3x3 result = new Matrix3x3();
        
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                result[i, j] = rotMat[i, 0] * sx2 * rotMat[j, 0] +
                               rotMat[i, 1] * sy2 * rotMat[j, 1] +
                               rotMat[i, 2] * sz2 * rotMat[j, 2];
            }
        }
        
        return result;
    }

    /// <summary>
    /// Read source Gaussian data from the GPU, including scale and rotation.
    /// </summary>
    private RawGaussianData[] ReadRawGaussianData(GaussianSplatRenderer gsRenderer)
    {
        try
        {
            // Export complete data through EditExportData.
            // InputSplatData struct size: 62 floats * 4 bytes = 248 bytes
            // Layout: pos(3) + nor(3) + dc0(3) + sh(45) + opacity(1) + scale(3) + rot(4) = 62
            int kSplatSize = 248; 
            using var gpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gsRenderer.splatCount, kSplatSize);
            
            if (!gsRenderer.EditExportData(gpuData, false))
            {
                Debug.LogWarning("[PGV] EditExportData failed");
                return null;
            }

            // Read the raw bytes.
            float[] rawFloats = new float[gsRenderer.splatCount * 62];
            gpuData.GetData(rawFloats);

            // Parse the data into RawGaussianData.
            var result = new RawGaussianData[gsRenderer.splatCount];
            for (int i = 0; i < gsRenderer.splatCount; i++)
            {
                int offset = i * 62;
                result[i] = new RawGaussianData
                {
                    pos = new Vector3(rawFloats[offset], rawFloats[offset + 1], rawFloats[offset + 2]),
                    
                    // Raw SH0 to Color
                    dc0 = (Vector3)GaussianUtils.SH0ToColor(new float3(rawFloats[offset + 6], rawFloats[offset + 7], rawFloats[offset + 8])),
                    
                    // Opacity: valid range 0..1 (from logit)
                    opacity = GaussianUtils.Sigmoid(rawFloats[offset + 54]),
                    
                    // Scale: valid range (from log-scale)
                    scale = (Vector3)GaussianUtils.LinearScale(new float3(rawFloats[offset + 55], rawFloats[offset + 56], rawFloats[offset + 57])),
                    
                    // Rot: format (w,x,y,z) -> normalize & swizzle to (x,y,z,w)
                    rot = LinkRotation(rawFloats[offset + 58], rawFloats[offset + 59], rawFloats[offset + 60], rawFloats[offset + 61])
                };
            }

            return result;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PGV] Failed to read raw Gaussian data: {e.Message}");
            return null;
        }
    }

    private Quaternion LinkRotation(float r0, float r1, float r2, float r3)
    {
        // 3DGS PLY stores rotation as (r, x, y, z) -> w, x, y, z
        // Unity needs (x, y, z, w)
        // NormalizeSwizzleRotation does math.normalize(wxyz).yzwx
        float4 q = new float4(r0, r1, r2, r3); // w, x, y, z
        q = GaussianUtils.NormalizeSwizzleRotation(q); // x, y, z, w
        return new Quaternion(q.x, q.y, q.z, q.w);
    }

    ~PointCloudPreprocessor()
    {
        CleanupBuffers();
    }
}

// Helper data structures

/// <summary>
/// GPU voxel structure that must match the compute shader.
/// This must be a blittable type so it can be read from a ComputeBuffer.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct PGVVoxelData
{
    public Vector3 pos_sum;
    public float weight_sum;

    // 3x3 covariance matrix stored as nine individual fields rather than an array
    public float cov_sum_0;
    public float cov_sum_1;
    public float cov_sum_2;
    public float cov_sum_3;
    public float cov_sum_4;
    public float cov_sum_5;
    public float cov_sum_6;
    public float cov_sum_7;
    public float cov_sum_8;

    public uint count;

    // Convenience indexer
    public float this[int index]
    {
        get
        {
            switch (index)
            {
                case 0: return cov_sum_0;
                case 1: return cov_sum_1;
                case 2: return cov_sum_2;
                case 3: return cov_sum_3;
                case 4: return cov_sum_4;
                case 5: return cov_sum_5;
                case 6: return cov_sum_6;
                case 7: return cov_sum_7;
                case 8: return cov_sum_8;
                default: throw new System.IndexOutOfRangeException();
            }
        }
    }
}


/// <summary>
/// Simple 3x3 matrix type.
/// </summary>
public struct Matrix3x3
{
    private float m00, m01, m02;
    private float m10, m11, m12;
    private float m20, m21, m22;

    public float this[int row, int col]
    {
        get
        {
            if (row == 0 && col == 0) return m00;
            if (row == 0 && col == 1) return m01;
            if (row == 0 && col == 2) return m02;
            if (row == 1 && col == 0) return m10;
            if (row == 1 && col == 1) return m11;
            if (row == 1 && col == 2) return m12;
            if (row == 2 && col == 0) return m20;
            if (row == 2 && col == 1) return m21;
            if (row == 2 && col == 2) return m22;
            throw new System.IndexOutOfRangeException();
        }
        set
        {
            if (row == 0 && col == 0) m00 = value;
            else if (row == 0 && col == 1) m01 = value;
            else if (row == 0 && col == 2) m02 = value;
            else if (row == 1 && col == 0) m10 = value;
            else if (row == 1 && col == 1) m11 = value;
            else if (row == 1 && col == 2) m12 = value;
            else if (row == 2 && col == 0) m20 = value;
            else if (row == 2 && col == 1) m21 = value;
            else if (row == 2 && col == 2) m22 = value;
            else throw new System.IndexOutOfRangeException();
        }
    }

    public static Matrix3x3 identity => new Matrix3x3
    {
        m00 = 1,
        m11 = 1,
        m22 = 1
    };

    public Matrix3x3 Transpose()
    {
        return new Matrix3x3
        {
            m00 = m00,
            m01 = m10,
            m02 = m20,
            m10 = m01,
            m11 = m11,
            m12 = m21,
            m20 = m02,
            m21 = m12,
            m22 = m22
        };
    }

    public static Matrix3x3 operator *(Matrix3x3 a, Matrix3x3 b)
    {
        Matrix3x3 result = new Matrix3x3();
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                result[r, c] = a[r, 0] * b[0, c] + a[r, 1] * b[1, c] + a[r, 2] * b[2, c];
            }
        }
        return result;
    }
}

// CPU voxel-filter helper structures

/// <summary>
/// CPU voxel accumulator used in a Dictionary.
/// </summary>
struct VoxelAccumulator
{
    public Vector3 posSum;
    public Vector3 colorSum;
    public int count;
}

struct PGVVoxelAccumulatorCPU
{
    public Vector3 posSum;
    public Vector3 colorSum;
    public float[] covSum;       // Term II: Σ μ_k μ_k^T (second moment of position)
    public float[] splatCovSum;  // Term I: Σ Σ_k (sum of individual Gaussian covariance matrices)
    public float opacitySum;     // Accumulated opacity
    public int count;
}

/// <summary>
/// Source Gaussian data read from the GPU.
/// </summary>
struct RawGaussianData
{
    public Vector3 pos;
    public Vector3 dc0;      // Color
    public float opacity;
    public Vector3 scale;
    public Quaternion rot;
}
