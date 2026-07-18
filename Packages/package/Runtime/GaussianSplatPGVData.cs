using UnityEngine;

/// <summary>
/// Baked PGV (Probabilistic Gaussian Voxelization) data.
/// Stores Super-Gaussians with their normals, covariance matrices, and related data.
/// </summary>
[CreateAssetMenu(fileName = "NewPGVData", menuName = "Gaussian Splats/PGV Data")]
public class GaussianSplatPGVData : ScriptableObject
{
    // Super-Gaussian data
    
    [HideInInspector]
    public Vector3[] positions;
    
    [HideInInspector]
    public Vector3[] normals;
    
    [HideInInspector]
    public Matrix4x4[] covariances;
    
    [HideInInspector]
    public float[] weights;

    [HideInInspector]
    public float[] avgOpacities;

    [HideInInspector]
    public int[] sourceCounts;
    
    [HideInInspector]
    public Color[] colors;

    [HideInInspector]
    public int[] rawToSuperGaussian;
    
    // Metadata
    
    public float voxelSize;
    public string bakedDate;
    public int sourcePointCount;  // Input point count from BakedData
    public int rawGaussianCount;
    
    // Properties
    
    public int SuperGaussianCount => positions != null ? positions.Length : 0;
    
    public bool HasNormals => normals != null && normals.Length == SuperGaussianCount;
    public bool HasCovariances => covariances != null && covariances.Length == SuperGaussianCount;
    public bool HasWeights => weights != null && weights.Length == SuperGaussianCount;
    public bool HasAvgOpacities => avgOpacities != null && avgOpacities.Length == SuperGaussianCount;
    public bool HasSourceCounts => sourceCounts != null && sourceCounts.Length == SuperGaussianCount;
    public bool HasRawGaussianMapping => rawToSuperGaussian != null && rawToSuperGaussian.Length == rawGaussianCount;
    
    // Helper methods
    
    /// <summary>
    /// Extract a 3x3 covariance matrix from its 4x4 storage.
    /// </summary>
    public void GetCovariance3x3(int index, out float m00, out float m01, out float m02,
                                               out float m10, out float m11, out float m12,
                                               out float m20, out float m21, out float m22)
    {
        if (!HasCovariances || index < 0 || index >= covariances.Length)
        {
            // Return the identity matrix.
            m00 = m11 = m22 = 1f;
            m01 = m02 = m10 = m12 = m20 = m21 = 0f;
            return;
        }
        
        Matrix4x4 m = covariances[index];
        m00 = m.m00; m01 = m.m01; m02 = m.m02;
        m10 = m.m10; m11 = m.m11; m12 = m.m12;
        m20 = m.m20; m21 = m.m21; m22 = m.m22;
    }
}
