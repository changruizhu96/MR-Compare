using UnityEngine;

// Standalone container for baked positions and colors.
[CreateAssetMenu(fileName = "NewBakedData", menuName = "Gaussian Splats/Baked Data")]
public class GaussianSplatBakedData : ScriptableObject
{
    // Base data

    // Positions in object-local space
    [HideInInspector]
    public Vector3[] positions;

    // Color (RGB) and opacity (A)
    [HideInInspector]
    public Color[] colors;

    // Per-point opacity
    [HideInInspector]
    public float[] opacities;

    // PGV extension data

    // Normals derived from covariance-matrix eigendecomposition
    [HideInInspector]
    public Vector3[] normals;

    // 3x3 covariance matrices stored as 4x4 matrices for serialization
    [HideInInspector]
    public Matrix4x4[] covariances;

    // Combined weight (opacity x flatness)
    [HideInInspector]
    public float[] weights;

    // Properties

    public int PointCount => positions != null ? positions.Length : 0;

    public bool HasNormals => normals != null && normals.Length == PointCount;
    public bool HasCovariances => covariances != null && covariances.Length == PointCount;
    public bool HasWeights => weights != null && weights.Length == PointCount;

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