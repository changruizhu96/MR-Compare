using UnityEngine;

/// <summary>
/// Super-Gaussian produced by PGV voxelization.
/// Represents the weighted aggregate of all original Gaussians in one voxel.
/// </summary>
[System.Serializable]
public struct SuperGaussian
{
    /// <summary>
    /// Voxel-center position.
    /// </summary>
    public Vector3 position;
    
    /// <summary>
    /// Normal derived from covariance-matrix eigendecomposition.
    /// </summary>
    public Vector3 normal;
    
    /// <summary>
    /// 3x3 covariance matrix stored as a 4x4 matrix for serialization.
    /// Only the upper-left 3x3 portion is used.
    /// </summary>
    public Matrix4x4 covariance;
    
    /// <summary>
    /// Combined weight (opacity x flatness).
    /// </summary>
    public float weight;

    /// <summary>
    /// Average opacity of the original Gaussians in this aggregate.
    /// </summary>
    public float avgOpacity;
    
    /// <summary>
    /// Optional color used for visualization.
    /// </summary>
    public Color color;
    
    /// <summary>
    /// Number of original Gaussians contained in this voxel.
    /// </summary>
    public int sourceCount;
    
    // Helper methods
    
    /// <summary>
    /// Store a 3x3 covariance matrix in 4x4 storage.
    /// </summary>
    public void SetCovariance3x3(
        float m00, float m01, float m02,
        float m10, float m11, float m12,
        float m20, float m21, float m22)
    {
        covariance = new Matrix4x4();
        covariance.m00 = m00; covariance.m01 = m01; covariance.m02 = m02; covariance.m03 = 0;
        covariance.m10 = m10; covariance.m11 = m11; covariance.m12 = m12; covariance.m13 = 0;
        covariance.m20 = m20; covariance.m21 = m21; covariance.m22 = m22; covariance.m23 = 0;
        covariance.m30 = 0;   covariance.m31 = 0;   covariance.m32 = 0;   covariance.m33 = 1;
    }
    
    /// <summary>
    /// Extract a 3x3 covariance matrix from a Matrix4x4.
    /// </summary>
    public void GetCovariance3x3(out float m00, out float m01, out float m02,
                                  out float m10, out float m11, out float m12,
                                  out float m20, out float m21, out float m22)
    {
        m00 = covariance.m00; m01 = covariance.m01; m02 = covariance.m02;
        m10 = covariance.m10; m11 = covariance.m11; m12 = covariance.m12;
        m20 = covariance.m20; m21 = covariance.m21; m22 = covariance.m22;
    }
    
    /// <summary>
    /// Create a default Super-Gaussian with identity covariance.
    /// </summary>
    public static SuperGaussian CreateDefault(Vector3 position)
    {
        var sg = new SuperGaussian
        {
            position = position,
            normal = Vector3.up,
            weight = 1f,
            avgOpacity = 1f,
            color = Color.white,
            sourceCount = 0
        };
        sg.SetCovariance3x3(
            1, 0, 0,
            0, 1, 0,
            0, 0, 1
        );
        return sg;
    }
}
