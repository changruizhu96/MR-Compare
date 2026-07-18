using Appletea.Dev.PointCloud;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(Quest3PointCloudScanner))]
public class Quest3PointCloudScannerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        SerializedProperty scannerMode = serializedObject.FindProperty("scannerMode");
        EditorGUILayout.PropertyField(scannerMode);
        bool isLoadMode = scannerMode.enumValueIndex == (int)Quest3PointCloudScanner.ScannerMode.Load;

        DrawReferences(isLoadMode);
        DrawStorage(isLoadMode);

        if (isLoadMode)
        {
            DrawLoadMode();
        }
        else
        {
            DrawScanMode();
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawReferences(bool isLoadMode)
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("References", EditorStyles.boldLabel);
        DrawProperty("pointCloudVFX");
        if (!isLoadMode)
        {
            DrawProperty("raycastManager");
            DrawProperty("mainCamera");
            DrawProperty("anchorCoreBlock");
        }

        DrawProperty("anchorHolder");
    }

    private void DrawStorage(bool isLoadMode)
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField(isLoadMode ? "Load Files" : "Save Files", EditorStyles.boldLabel);
        DrawProperty("AnchorUuidFile");
        DrawProperty("PointCloudFile");
        if (isLoadMode)
        {
            DrawProperty("loadPointCloudOnStart");
            DrawProperty("playVfxAfterLoad");
        }
    }

    private void DrawLoadMode()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Visualization", EditorStyles.boldLabel);
        DrawProperty("enableRenderPointBudget");
        if (serializedObject.FindProperty("enableRenderPointBudget").boolValue)
        {
            EditorGUI.indentLevel++;
            DrawProperty("maxRenderPointCount");
            EditorGUI.indentLevel--;
        }

        DrawProperty("hardUploadPointCap");
        EditorGUILayout.HelpBox("Load mode reads the saved anchor and point cloud, transforms points back to world space, and displays them in the scanner VFX. Press X at runtime to reload.", MessageType.Info);
    }

    private void DrawScanMode()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Scan Settings", EditorStyles.boldLabel);
        DrawProperty("density");
        DrawProperty("maxScanDistance");
        DrawProperty("scanInterval");
        DrawProperty("edgeThreshold");

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Performance Guard", EditorStyles.boldLabel);
        DrawProperty("vfxRefreshInterval");
        DrawProperty("hardUploadPointCap");
        DrawProperty("maxRaycastsPerTick");

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Post-Processing", EditorStyles.boldLabel);
        SerializedProperty enablePostDenoising = serializedObject.FindProperty("enablePostDenoising");
        EditorGUILayout.PropertyField(enablePostDenoising);
        if (enablePostDenoising.boolValue)
        {
            EditorGUI.indentLevel++;
            DrawProperty("maxPointsForPostDenoising");
            DrawProperty("denoiseRadius");
            DrawProperty("denoiseMinNeighbors");
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Rendering", EditorStyles.boldLabel);
        DrawProperty("renderingRadius");
        DrawProperty("maxChunkCount");
        DrawProperty("enableRenderPointBudget");
        if (serializedObject.FindProperty("enableRenderPointBudget").boolValue)
        {
            EditorGUI.indentLevel++;
            DrawProperty("maxRenderPointCount");
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Chunk Storage", EditorStyles.boldLabel);
        DrawProperty("chunkSize");
        DrawProperty("maxPointsPerChunk");

        DrawSurfelFusion();
    }

    private void DrawSurfelFusion()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Save-Time Surfel Fusion", EditorStyles.boldLabel);
        SerializedProperty enableSurfelFusionOnSave = serializedObject.FindProperty("enableSurfelFusionOnSave");
        EditorGUILayout.PropertyField(enableSurfelFusionOnSave);
        if (!enableSurfelFusionOnSave.boolValue)
        {
            EditorGUILayout.HelpBox("Disabled: saving uses the existing raw point cloud path.", MessageType.None);
            return;
        }

        EditorGUI.indentLevel++;
        DrawProperty("surfelFusionCompute");
        DrawProperty("maxSurfelCount");
        DrawProperty("surfelHashBucketCount");
        DrawProperty("surfelCellSize");
        DrawProperty("surfelRadius");
        DrawProperty("surfelMatchDistance");
        DrawProperty("surfelPointToPlaneDistance");
        DrawProperty("surfelNormalAngleThreshold");
        DrawProperty("surfelMaxGrazingAngle");
        DrawProperty("surfelNormalNeighborDistance");
        DrawProperty("surfelMaxBucketTraversal");
        DrawProperty("newSurfelStride");
        DrawProperty("minSurfelObservations");
        DrawProperty("minSurfelWeight");
        DrawProperty("maxSurfelWeight");
        DrawProperty("minFusedPointCountForSave");
        EditorGUI.indentLevel--;

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Keyframe Capture", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;
        DrawProperty("enableKeyframeCapture");
        if (serializedObject.FindProperty("enableKeyframeCapture").boolValue)
        {
            DrawProperty("maxStoredKeyframes");
            DrawProperty("minKeyframeValidHits");
            DrawProperty("minKeyframeInterval");
            DrawProperty("maxKeyframeInterval");
            DrawProperty("minKeyframeTranslation");
            DrawProperty("minKeyframeRotationDegrees");
            DrawProperty("keyframeCoverageVoxelSize");
            DrawProperty("minNewCoverageRatio");
        }
        EditorGUI.indentLevel--;
    }

    private void DrawProperty(string propertyName)
    {
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        if (property != null)
        {
            EditorGUILayout.PropertyField(property);
        }
    }
}
