using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(AllInOneRegistration))]
public class AllInOneRegistrationEditor : Editor
{
    private static readonly GUIContent[] GicpModelOptions =
    {
        new GUIContent("GICP"),
        new GUIContent("VGICP")
    };

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        // Get a reference to the script being inspected.
        AllInOneRegistration script = (AllInOneRegistration)target;
        SerializedProperty workflowMode = serializedObject.FindProperty("workflowMode");
        EditorGUILayout.PropertyField(workflowMode);
        bool isLoadMode = workflowMode.enumValueIndex == (int)AllInOneRegistration.WorkflowMode.Load;

        if (!isLoadMode)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("targetFormat"));
        }
        EditorGUILayout.PropertyField(serializedObject.FindProperty("sourceType"));
        if (!isLoadMode)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("roughAligner"));
        }

        EditorGUILayout.LabelField("References", EditorStyles.boldLabel);
        switch (script.sourceType)
        {
            case AllInOneRegistration.SourceFormat.GaussianSplating:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("gaussianSourceRenderer"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("useBakedData"));
                break;
            case AllInOneRegistration.SourceFormat.Mesh:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("sourceMesh"));
                break;

        }
        if (!isLoadMode)
        {
            switch (script.targetFormat)
            {
                case AllInOneRegistration.TargetFormat.preScan:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("pointCloudTargetFile"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("AnchorUuidFile"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("anchorHolder"));

                    break;
                case AllInOneRegistration.TargetFormat.realTimeScan:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("scannerTarget"));
                    break;
                case AllInOneRegistration.TargetFormat.roomMesh:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("roomMeshEventTarget"));
                    break;
                case AllInOneRegistration.TargetFormat.effectMesh:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("effectMeshEventTarget"));
                    break;
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("allowRepeatedAlignment"));
        }

        if (!isLoadMode)
        {
            switch (script.roughAligner)
            {
                case AllInOneRegistration.RoughAlignMode.Teaser:
                    EditorGUILayout.Space(10);
                    EditorGUILayout.LabelField("Teaser++ Parameter", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("estimateScaling"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("voxelSizeTeaser"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("maxIterationsTeaser"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("noiseBoundTeaser"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("normalRadiusTeaser"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("fpfhRadiusTeaser"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("matcherRatioTeaser"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("rotationCostThresholdTeaser"));
                    break;
            }
        }

        if (!isLoadMode)
        {
            // --- (V)GICP Section with Conditional Logic ---
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("(V)GICP Parameter", EditorStyles.boldLabel);

        // Find the 'useGICP' property
        SerializedProperty useGicpProperty = serializedObject.FindProperty("useGICP");

        // Always draw the main 'useGICP' toggle. Its tooltip will be used automatically.
        EditorGUILayout.PropertyField(useGicpProperty);

        // NEW: Check if the 'useGICP' checkbox is ticked.
        if (useGicpProperty.boolValue)
        {
            // Indent the GICP parameters to show they are sub-settings of 'useGICP'.
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("voxelSizeGICP"));
            SerializedProperty modelNameProperty = serializedObject.FindProperty("modelName");
            int selectedModel = Mathf.Clamp(modelNameProperty.intValue, 0, GicpModelOptions.Length - 1);
            selectedModel = GUILayout.Toolbar(selectedModel, GicpModelOptions);
            modelNameProperty.intValue = selectedModel;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxIterGICP"));
            if (selectedModel == 1)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("voxelResolutionGICP"));
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxCorrespondenceDistanceGICP"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("correspondenceRandomnessGICP"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("epsilonGICP"));

            // Reset the indent level back to normal.
            EditorGUI.indentLevel--;
        }



        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Selection & Interaction", EditorStyles.boldLabel);
        SerializedProperty isBoxSelection = serializedObject.FindProperty("isBoxSelection");
        EditorGUILayout.PropertyField(isBoxSelection);


        if (isBoxSelection.boolValue)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("boxSize"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("selectionGizmoShape"));

            SerializedProperty showSelectionGizmo = serializedObject.FindProperty("showSelectionGizmo");
            EditorGUILayout.PropertyField(showSelectionGizmo);
            if (showSelectionGizmo.boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("selectionGizmoColor"));
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("selectionCenterOverride"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("usePointCloudCentroidAsSelectionCenter"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("selectionCenterOffset"));
        }
        // Draw the remaining properties that are always visible

        //EditorGUILayout.PropertyField(serializedObject.FindProperty("isBoxSelection"));
        SerializedProperty isTargetPointVisualization = serializedObject.FindProperty("isTargetPointVisualization");
        EditorGUILayout.PropertyField(isTargetPointVisualization, new GUIContent("Is Target Point Visualization"));
            if (isTargetPointVisualization.boolValue)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("pointCloudVFX"), new GUIContent("Target Point VFX"));
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField(isLoadMode ? "Load Alignment Result" : "Save Alignment Result", EditorStyles.boldLabel);

        SerializedProperty isSaving = serializedObject.FindProperty("isSaving");
        if (!isLoadMode)
        {
            EditorGUILayout.PropertyField(isSaving);
        }

        if (isLoadMode || isSaving.boolValue)
        {
            SerializedProperty alignmentReferenceMode = serializedObject.FindProperty("alignmentReferenceMode");
            EditorGUILayout.PropertyField(alignmentReferenceMode);

            bool usesSpatialAnchor = alignmentReferenceMode.enumValueIndex == (int)AllInOneRegistration.AlignmentReferenceMode.SpatialAnchor;
            bool usesRoomMesh = alignmentReferenceMode.enumValueIndex == (int)AllInOneRegistration.AlignmentReferenceMode.RoomMesh;
            bool usesEffectMesh = alignmentReferenceMode.enumValueIndex == (int)AllInOneRegistration.AlignmentReferenceMode.EffectMesh;
            if (usesSpatialAnchor && (isLoadMode || script.targetFormat != AllInOneRegistration.TargetFormat.preScan))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("AnchorUuidFile"), new GUIContent(isLoadMode ? "Anchor UUID File" : "Save Anchor UUID File"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("anchorHolder"), new GUIContent(isLoadMode ? "Anchor Holder" : "Save Anchor Holder"));
            }

            if (usesRoomMesh)
            {
                if (isLoadMode)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("roomMeshEventTarget"));
                }
                else if (script.targetFormat != AllInOneRegistration.TargetFormat.roomMesh)
                {
                    EditorGUILayout.HelpBox("Room Mesh can only be resolved when Target Format is Room Mesh (Legacy).", MessageType.Warning);
                }
            }

            if (usesEffectMesh)
            {
                if (isLoadMode || script.targetFormat != AllInOneRegistration.TargetFormat.effectMesh)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("effectMeshEventTarget"));
                }
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("alignmentFile"));
        }

        // Apply any changes made in the inspector.
        serializedObject.ApplyModifiedProperties();



    } 
}
