using UnityEngine;

/// <summary>
/// Voxel-filter data containing a simple voxel-downsampling result.
/// Used for comparison with PGV.
/// </summary>
[CreateAssetMenu(fileName = "NewVoxelFilterData", menuName = "Gaussian Splats/Voxel Filter Data")]
public class GaussianSplatVoxelFilterData : ScriptableObject
{
    [HideInInspector]
    public Vector3[] positions;
    
    [HideInInspector]
    public Color[] colors;
    
    // Metadata
    public float voxelSize;
    public string bakedDate;
    public int sourcePointCount;
    
    public int PointCount => positions != null ? positions.Length : 0;
}
