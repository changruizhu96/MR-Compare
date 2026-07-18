// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;
using GaussianSplatRenderer = GaussianSplatting.Runtime.GaussianSplatRenderer;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderer))]
    [CanEditMultipleObjects]
    public class GaussianSplatRendererEditor : UnityEditor.Editor
    {
        const string kPrefExportBake = "nesnausk.GaussianSplatting.ExportBakeTransform";

        SerializedProperty m_PropAsset;
        SerializedProperty m_PropRenderOrder;
        SerializedProperty m_PropSplatScale;
        SerializedProperty m_PropOpacityScale;
        SerializedProperty m_PropSHOrder;
        SerializedProperty m_PropSHOnly;
        SerializedProperty m_PropSortNthFrame;
        SerializedProperty m_PropRenderInEditMode;
        SerializedProperty m_PropRenderMode;
        SerializedProperty m_PropPointDisplaySize;
        SerializedProperty m_PropCutouts;
        SerializedProperty m_PropShaderSplats;
        SerializedProperty m_PropShaderComposite;
        SerializedProperty m_PropShaderDebugPoints;
        SerializedProperty m_PropShaderDebugBoxes;
        SerializedProperty m_PropShaderDebugBaked;
        SerializedProperty m_PropCSSplatUtilities;



        // Ablation-study controls
        float m_BakeMinOpacity = 0f;
        float m_BakeScaleRatio = 1f;
        bool m_BakeEnableDensity = false;
        float m_LastAutoBakeScaleRatio = -1f;

        // BakedData reference and baking parameters
        SerializedProperty m_PropBakedData;
        //float m_BakeMinOpacity = 0.1f;
        
        // Density-filtering properties
        SerializedProperty m_PropDensityGridSpacing;
        SerializedProperty m_PropDensityMinNeighbors;
        SerializedProperty m_PropAutoNormalizeCoords;
        SerializedProperty m_PropEnableSelectionFilter;
        SerializedProperty m_PropSelectionShape;
        SerializedProperty m_PropSelectionSize;
        SerializedProperty m_PropSelectionCenterOverride;
        SerializedProperty m_PropSelectionUsePointCentroid;
        SerializedProperty m_PropSelectionCenterOffset;

        bool m_ResourcesExpanded = false;
        int m_CameraIndex = 0;

        bool m_ExportBakeTransform;

        static int s_EditStatsUpdateCounter = 0;

        static HashSet<GaussianSplatRendererEditor> s_AllEditors = new();

        public static void BumpGUICounter()
        {
            ++s_EditStatsUpdateCounter;
        }

        public static void RepaintAll()
        {
            foreach (var e in s_AllEditors)
                e.Repaint();
        }

        public void OnEnable()
        {
            m_ExportBakeTransform = EditorPrefs.GetBool(kPrefExportBake, false);

            m_PropAsset = serializedObject.FindProperty("m_Asset");
            m_PropRenderOrder = serializedObject.FindProperty("m_RenderOrder");
            m_PropSplatScale = serializedObject.FindProperty("m_SplatScale");
            m_PropOpacityScale = serializedObject.FindProperty("m_OpacityScale");
            m_PropSHOrder = serializedObject.FindProperty("m_SHOrder");
            m_PropSHOnly = serializedObject.FindProperty("m_SHOnly");
            m_PropSortNthFrame = serializedObject.FindProperty("m_SortNthFrame");
            m_PropRenderInEditMode = serializedObject.FindProperty("m_RenderInEditMode");
            m_PropRenderMode = serializedObject.FindProperty("m_RenderMode");
            m_PropPointDisplaySize = serializedObject.FindProperty("m_PointDisplaySize");
            m_PropCutouts = serializedObject.FindProperty("m_Cutouts");
            m_PropShaderSplats = serializedObject.FindProperty("m_ShaderSplats");
            m_PropShaderComposite = serializedObject.FindProperty("m_ShaderComposite");
            m_PropShaderDebugPoints = serializedObject.FindProperty("m_ShaderDebugPoints");
            m_PropShaderDebugBoxes = serializedObject.FindProperty("m_ShaderDebugBoxes");
            m_PropShaderDebugBaked = serializedObject.FindProperty("m_ShaderDebugBaked"); // Baked-data debug shader
            m_PropCSSplatUtilities = serializedObject.FindProperty("m_CSSplatUtilities");

            // Find the m_BakedData property.
            m_PropBakedData = serializedObject.FindProperty("m_BakedData");

            // Find the density-filtering properties.
            m_PropDensityGridSpacing = serializedObject.FindProperty("m_DensityGridSpacing");
            m_PropDensityMinNeighbors = serializedObject.FindProperty("m_DensityMinNeighbors");
            m_PropAutoNormalizeCoords = serializedObject.FindProperty("m_AutoNormalizeCoords");
            m_PropEnableSelectionFilter = serializedObject.FindProperty("m_EnableSelectionFilter");
            m_PropSelectionShape = serializedObject.FindProperty("m_SelectionShape");
            m_PropSelectionSize = serializedObject.FindProperty("m_SelectionSize");
            m_PropSelectionCenterOverride = serializedObject.FindProperty("m_SelectionCenterOverride");
            m_PropSelectionUsePointCentroid = serializedObject.FindProperty("m_SelectionUsePointCentroid");
            m_PropSelectionCenterOffset = serializedObject.FindProperty("m_SelectionCenterOffset");


            s_AllEditors.Add(this);

        }

        public void OnDisable()
        {
            s_AllEditors.Remove(this);
        }

        public override void OnInspectorGUI()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs)
                return;

            serializedObject.Update();

            GUILayout.Label("Data Asset", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropAsset);

            if (!gs.HasValidAsset)
            {
                var msg = gs.asset != null && gs.asset.formatVersion != GaussianSplatAsset.kCurrentVersion
                    ? "Gaussian Splat asset version is not compatible, please recreate the asset"
                    : "Gaussian Splat asset is not assigned or is empty";
                EditorGUILayout.HelpBox(msg, MessageType.Error);
            }

            EditorGUILayout.Space();
            GUILayout.Label("Render Options", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropRenderOrder);
            EditorGUILayout.PropertyField(m_PropSplatScale);
            EditorGUILayout.PropertyField(m_PropOpacityScale);
            EditorGUILayout.PropertyField(m_PropSHOrder);
            EditorGUILayout.PropertyField(m_PropSHOnly);
            EditorGUILayout.PropertyField(m_PropSortNthFrame);
            EditorGUILayout.PropertyField(m_PropRenderInEditMode);
            if (!Application.isPlaying && m_PropRenderInEditMode != null && !m_PropRenderInEditMode.boolValue)
            {
                EditorGUILayout.HelpBox("Edit Mode rendering is disabled; splats will render in Play Mode.", MessageType.Info);
            }

            EditorGUILayout.Space();
            GUILayout.Label("Debugging Tweaks", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropRenderMode);
            if (m_PropRenderMode.intValue is (int)GaussianSplatRenderer.RenderMode.DebugPoints or (int)GaussianSplatRenderer.RenderMode.DebugPointIndices)
                EditorGUILayout.PropertyField(m_PropPointDisplaySize);

            // =================================================================================
            // Baking and debug-data tools
            // =================================================================================
            // EditorGUILayout.Space();
            // GUILayout.Label("Baking (Debug Data)", EditorStyles.boldLabel);

            // // Show the external data slot.
            // EditorGUILayout.PropertyField(m_PropBakedData);

            // // Baking controls
            // GUILayout.BeginHorizontal();
            // m_BakeMinOpacity = EditorGUILayout.Slider("Min Opacity", m_BakeMinOpacity, 0f, 1f);
            // if (GUILayout.Button("Bake", GUILayout.Width(60)))
            // {
            //     // Invoke the renderer's baking function.
            //     gs.CaptureAndBakeData(m_BakeMinOpacity);
            // }
            // GUILayout.EndHorizontal();

            // // Show the point count when baked data is available.
            // if (gs.m_BakedData != null)
            // {
            //     EditorGUILayout.HelpBox($"Baked: {gs.m_BakedData.PointCount:N0} points", MessageType.Info);
            // }
            // =================================================================================



            // =================================================================================
            // Ablation Study panel
            // =================================================================================
            // 1. Show the currently referenced asset.
            EditorGUILayout.PropertyField(m_PropBakedData);

            // 2. Parameter controls
            EditorGUI.indentLevel++;
            m_BakeMinOpacity = EditorGUILayout.Slider("Min Opacity", m_BakeMinOpacity, 0f, 1f);
            using (new EditorGUILayout.HorizontalScope())
            {
                m_BakeScaleRatio = EditorGUILayout.Slider(new GUIContent("Max Flatness", "1.0=No Filter, 0.1=Strict Surface"), m_BakeScaleRatio, 0.0f, 1.0f);
                if (GUILayout.Button(new GUIContent("Auto", "Set Max Flatness to the median scale ratio of the current splats"), GUILayout.Width(52)))
                {
                    if (TryAutoPickBakeScaleRatio(gs, out float autoRatio))
                    {
                        m_BakeScaleRatio = autoRatio;
                        m_LastAutoBakeScaleRatio = autoRatio;
                        Repaint();
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Auto Flatness", "Failed to read scale ratio distribution from the current renderer.", "OK");
                    }
                }
            }
            if (m_LastAutoBakeScaleRatio >= 0f)
            {
                EditorGUILayout.LabelField("Auto Flatness (Median)", m_LastAutoBakeScaleRatio.ToString("F4"));
            }
            m_BakeEnableDensity = EditorGUILayout.Toggle(new GUIContent("Density Filter", "Remove floating noise"), m_BakeEnableDensity);
            if (m_BakeEnableDensity)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(m_PropDensityGridSpacing, new GUIContent("Grid Spacing", "Spatial grid size in meters. Smaller values are more precise but may remove edge points."));
                EditorGUILayout.PropertyField(m_PropDensityMinNeighbors, new GUIContent("Min Neighbors", "Set to 0 to disable density filtering."));
                EditorGUILayout.PropertyField(m_PropAutoNormalizeCoords, new GUIContent("Auto Normalize Coords", "Detect large coordinates and adjust Grid Spacing for millimeter- or centimeter-scale scenes."));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(m_PropEnableSelectionFilter, new GUIContent("Enable Selection ROI"));
            if (m_PropEnableSelectionFilter.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(m_PropSelectionShape, new GUIContent("ROI Shape"));
                EditorGUILayout.PropertyField(m_PropSelectionSize, new GUIContent("ROI Size"));
                EditorGUILayout.PropertyField(m_PropSelectionCenterOverride, new GUIContent("Center Override"));
                EditorGUILayout.PropertyField(m_PropSelectionUsePointCentroid, new GUIContent("Use Point Centroid"));
                EditorGUILayout.PropertyField(m_PropSelectionCenterOffset, new GUIContent("Center Offset"));
                EditorGUI.indentLevel--;
            }

            EditorGUI.indentLevel--;

            // 3. Action button
            EditorGUILayout.Space(2);
            if (GUILayout.Button("Bake & Generate Asset", GUILayout.Height(30)))
            {
                // Invoke the renderer's updated logic.
                serializedObject.ApplyModifiedProperties();
                gs.CaptureAndBakeData(m_BakeMinOpacity, m_BakeScaleRatio, m_BakeEnableDensity);
            }

            // 4. Status information
            if (gs.m_BakedData != null)
            {
                string bakeInfo = gs.m_HasLastBakeStats
                    ? $"Current Data: {gs.m_BakedData.PointCount:N0} points\nLast Bake Total: {gs.m_LastBakeDurationMs:F1} ms\nGPU+Readback: {gs.m_LastBakeGpuDurationMs:F1} ms\nCPU+Save: {gs.m_LastBakeCpuDurationMs:F1} ms"
                    : $"Current Data: {gs.m_BakedData.PointCount:N0} points";
                EditorGUILayout.HelpBox(bakeInfo, MessageType.Info);
            }
            // =================================================================================

            EditorGUILayout.Space();
            m_ResourcesExpanded = EditorGUILayout.Foldout(m_ResourcesExpanded, "Resources", true, EditorStyles.foldoutHeader);
            if (m_ResourcesExpanded)
            {
                EditorGUILayout.PropertyField(m_PropShaderSplats);
                EditorGUILayout.PropertyField(m_PropShaderComposite);
                EditorGUILayout.PropertyField(m_PropShaderDebugPoints);
                EditorGUILayout.PropertyField(m_PropShaderDebugBoxes);
                EditorGUILayout.PropertyField(m_PropShaderDebugBaked); // Baked-data debug shader
                EditorGUILayout.PropertyField(m_PropCSSplatUtilities);
            }
            bool validAndEnabled = gs && gs.enabled && gs.gameObject.activeInHierarchy && gs.HasValidAsset;
            if (validAndEnabled && !gs.HasValidRenderSetup)
            {
                EditorGUILayout.HelpBox("Shader resources are not set up", MessageType.Error);
                validAndEnabled = false;
            }

            if (validAndEnabled && targets.Length == 1)
            {
                EditCameras(gs);
                EditGUI(gs);
            }
            if (validAndEnabled && targets.Length > 1)
            {
                MultiEditGUI();
            }

            serializedObject.ApplyModifiedProperties();
        }

        void EditCameras(GaussianSplatRenderer gs)
        {
            var asset = gs.asset;
            var cameras = asset.cameras;
            if (cameras != null && cameras.Length != 0)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Cameras", EditorStyles.boldLabel);
                var camIndex = EditorGUILayout.IntSlider("Camera", m_CameraIndex, 0, cameras.Length - 1);
                camIndex = math.clamp(camIndex, 0, cameras.Length - 1);
                if (camIndex != m_CameraIndex)
                {
                    m_CameraIndex = camIndex;
                    gs.ActivateCamera(camIndex);
                }
            }
        }

        void MultiEditGUI()
        {
            DrawSeparator();
            CountTargetSplats(out var totalSplats, out var totalObjects);
            EditorGUILayout.LabelField("Total Objects", $"{totalObjects}");
            EditorGUILayout.LabelField("Total Splats", $"{totalSplats:N0}");
            if (totalSplats > GaussianSplatAsset.kMaxSplats)
            {
                EditorGUILayout.HelpBox($"Can't merge, too many splats (max. supported {GaussianSplatAsset.kMaxSplats:N0})", MessageType.Warning);
                return;
            }

            var targetGs = (GaussianSplatRenderer)target;
            if (!targetGs || !targetGs.HasValidAsset || !targetGs.isActiveAndEnabled)
            {
                EditorGUILayout.HelpBox($"Can't merge into {target.name} (no asset or disable)", MessageType.Warning);
                return;
            }

            if (targetGs.asset.chunkData != null)
            {
                EditorGUILayout.HelpBox($"Can't merge into {target.name} (needs to use Very High quality preset)", MessageType.Warning);
                return;
            }
            if (GUILayout.Button($"Merge into {target.name}"))
            {
                MergeSplatObjects();
            }
        }

        void CountTargetSplats(out int totalSplats, out int totalObjects)
        {
            totalObjects = 0;
            totalSplats = 0;
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplatRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                ++totalObjects;
                totalSplats += gs.splatCount;
            }
        }

        void MergeSplatObjects()
        {
            CountTargetSplats(out var totalSplats, out _);
            if (totalSplats > GaussianSplatAsset.kMaxSplats)
                return;
            var targetGs = (GaussianSplatRenderer)target;

            int copyDstOffset = targetGs.splatCount;
            targetGs.EditSetSplatCount(totalSplats);
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplatRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                if (gs == targetGs)
                    continue;
                gs.EditCopySplatsInto(targetGs, 0, copyDstOffset, gs.splatCount);
                copyDstOffset += gs.splatCount;
                gs.gameObject.SetActive(false);
            }
            Debug.Assert(copyDstOffset == totalSplats, $"Merge count mismatch, {copyDstOffset} vs {totalSplats}");
            Selection.activeObject = targetGs;
        }

        void EditGUI(GaussianSplatRenderer gs)
        {
            ++s_EditStatsUpdateCounter;

            DrawSeparator();
            bool wasToolActive = ToolManager.activeContextType == typeof(GaussianToolContext);
            GUILayout.BeginHorizontal();
            bool isToolActive = GUILayout.Toggle(wasToolActive, "Edit", EditorStyles.miniButton);
            using (new EditorGUI.DisabledScope(!gs.editModified))
            {
                if (GUILayout.Button("Reset", GUILayout.ExpandWidth(false)))
                {
                    if (EditorUtility.DisplayDialog("Reset Splat Modifications?",
                            $"This will reset edits of {gs.name} to match the {gs.asset.name} asset. Continue?",
                            "Yes, reset", "Cancel"))
                    {
                        gs.enabled = false;
                        gs.enabled = true;
                    }
                }
            }

            GUILayout.EndHorizontal();
            if (!wasToolActive && isToolActive)
            {
                ToolManager.SetActiveContext<GaussianToolContext>();
                if (Tools.current == Tool.View)
                    Tools.current = Tool.Move;
            }

            if (wasToolActive && !isToolActive)
            {
                ToolManager.SetActiveContext<GameObjectToolContext>();
            }

            if (isToolActive && gs.asset.chunkData != null)
            {
                EditorGUILayout.HelpBox("Splat move/rotate/scale tools need Very High splat quality preset", MessageType.Warning);
            }

            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Cutout"))
            {
                GaussianCutout cutout = ObjectFactory.CreateGameObject("GSCutout", typeof(GaussianCutout)).GetComponent<GaussianCutout>();
                Transform cutoutTr = cutout.transform;
                cutoutTr.SetParent(gs.transform, false);
                cutoutTr.localScale = (gs.asset.boundsMax - gs.asset.boundsMin) * 0.25f;
                gs.m_Cutouts ??= Array.Empty<GaussianCutout>();
                ArrayUtility.Add(ref gs.m_Cutouts, cutout);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
                Selection.activeGameObject = cutout.gameObject;
            }
            if (GUILayout.Button("Use All Cutouts"))
            {
                gs.m_Cutouts = FindObjectsByType<GaussianCutout>(FindObjectsSortMode.InstanceID);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }

            if (GUILayout.Button("No Cutouts"))
            {
                gs.m_Cutouts = Array.Empty<GaussianCutout>();
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }
            GUILayout.EndHorizontal();
            EditorGUILayout.PropertyField(m_PropCutouts);

            bool hasCutouts = gs.m_Cutouts != null && gs.m_Cutouts.Length != 0;
            bool modifiedOrHasCutouts = gs.editModified || hasCutouts;

            var asset = gs.asset;
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            m_ExportBakeTransform = EditorGUILayout.Toggle("Export in world space", m_ExportBakeTransform);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool(kPrefExportBake, m_ExportBakeTransform);
            }

            if (GUILayout.Button("Export PLY"))
                ExportPlyFile(gs, m_ExportBakeTransform);
            if (asset.posFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                asset.scaleFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                asset.colorFormat > GaussianSplatAsset.ColorFormat.Float16x4 ||
                asset.shFormat > GaussianSplatAsset.SHFormat.Float16)
            {
                EditorGUILayout.HelpBox(
                    "It is recommended to use High or VeryHigh quality preset for editing splats, lower levels are lossy",
                    MessageType.Warning);
            }

            bool displayEditStats = isToolActive || modifiedOrHasCutouts;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Splats", $"{gs.splatCount:N0}");
            if (displayEditStats)
            {
                EditorGUILayout.LabelField("Cut", $"{gs.editCutSplats:N0}");
                EditorGUILayout.LabelField("Deleted", $"{gs.editDeletedSplats:N0}");
                EditorGUILayout.LabelField("Selected", $"{gs.editSelectedSplats:N0}");
                if (hasCutouts)
                {
                    if (s_EditStatsUpdateCounter > 10)
                    {
                        gs.UpdateEditCountsAndBounds();
                        s_EditStatsUpdateCounter = 0;
                    }
                }
            }
        }

        static void DrawSeparator()
        {
            EditorGUILayout.Space(12f, true);
            GUILayout.Box(GUIContent.none, "sv_iconselector_sep", GUILayout.Height(2), GUILayout.ExpandWidth(true));
            EditorGUILayout.Space();
        }

        bool HasFrameBounds()
        {
            return true;
        }

        Bounds OnGetFrameBounds()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs || !gs.HasValidRenderSetup)
                return new Bounds(Vector3.zero, Vector3.one);
            Bounds bounds = default;
            bounds.SetMinMax(gs.asset.boundsMin, gs.asset.boundsMax);
            if (gs.editSelectedSplats > 0)
            {
                bounds = gs.editSelectedBounds;
            }
            bounds.extents *= 0.7f;
            return TransformBounds(gs.transform, bounds);
        }

        public static Bounds TransformBounds(Transform tr, Bounds bounds)
        {
            var center = tr.TransformPoint(bounds.center);

            var ext = bounds.extents;
            var axisX = tr.TransformVector(ext.x, 0, 0);
            var axisY = tr.TransformVector(0, ext.y, 0);
            var axisZ = tr.TransformVector(0, 0, ext.z);

            // sum their absolute value to get the world extents
            ext.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
            ext.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.z);
            ext.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

            return new Bounds { center = center, extents = ext };
        }

        static unsafe void ExportPlyFile(GaussianSplatRenderer gs, bool bakeTransform)
        {
            var path = EditorUtility.SaveFilePanel(
                "Export Gaussian Splat PLY file", "", $"{gs.asset.name}-edit.ply", "ply");
            if (string.IsNullOrWhiteSpace(path))
                return;

            int kSplatSize = UnsafeUtility.SizeOf<Utils.InputSplatData>();
            using var gpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gs.splatCount, kSplatSize);

            if (!gs.EditExportData(gpuData, bakeTransform))
                return;

            Utils.InputSplatData[] data = new Utils.InputSplatData[gpuData.count];
            gpuData.GetData(data);

            var gpuDeleted = gs.GpuEditDeleted;
            uint[] deleted = new uint[gpuDeleted.count];
            gpuDeleted.GetData(deleted);

            // count non-deleted splats
            int aliveCount = 0;
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                    ++aliveCount;
            }

            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            // note: this is a long string! but we don't use multiline literal because we want guaranteed LF line ending
            var header = $"ply\nformat binary_little_endian 1.0\nelement vertex {aliveCount}\nproperty float x\nproperty float y\nproperty float z\nproperty float nx\nproperty float ny\nproperty float nz\nproperty float f_dc_0\nproperty float f_dc_1\nproperty float f_dc_2\nproperty float f_rest_0\nproperty float f_rest_1\nproperty float f_rest_2\nproperty float f_rest_3\nproperty float f_rest_4\nproperty float f_rest_5\nproperty float f_rest_6\nproperty float f_rest_7\nproperty float f_rest_8\nproperty float f_rest_9\nproperty float f_rest_10\nproperty float f_rest_11\nproperty float f_rest_12\nproperty float f_rest_13\nproperty float f_rest_14\nproperty float f_rest_15\nproperty float f_rest_16\nproperty float f_rest_17\nproperty float f_rest_18\nproperty float f_rest_19\nproperty float f_rest_20\nproperty float f_rest_21\nproperty float f_rest_22\nproperty float f_rest_23\nproperty float f_rest_24\nproperty float f_rest_25\nproperty float f_rest_26\nproperty float f_rest_27\nproperty float f_rest_28\nproperty float f_rest_29\nproperty float f_rest_30\nproperty float f_rest_31\nproperty float f_rest_32\nproperty float f_rest_33\nproperty float f_rest_34\nproperty float f_rest_35\nproperty float f_rest_36\nproperty float f_rest_37\nproperty float f_rest_38\nproperty float f_rest_39\nproperty float f_rest_40\nproperty float f_rest_41\nproperty float f_rest_42\nproperty float f_rest_43\nproperty float f_rest_44\nproperty float opacity\nproperty float scale_0\nproperty float scale_1\nproperty float scale_2\nproperty float rot_0\nproperty float rot_1\nproperty float rot_2\nproperty float rot_3\nend_header\n";
            fs.Write(Encoding.UTF8.GetBytes(header));
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                {
                    var splat = data[i];
                    byte* ptr = (byte*)&splat;
                    fs.Write(new ReadOnlySpan<byte>(ptr, kSplatSize));
                }
            }

            Debug.Log($"Exported PLY {path} with {aliveCount:N0} splats");
        }

        bool TryAutoPickBakeScaleRatio(GaussianSplatRenderer gs, out float medianRatio)
        {
            medianRatio = 1f;
            if (gs == null || !gs.HasValidAsset)
                return false;

            try
            {
                float[] ratios = ReadScaleRatios(gs);
                if (ratios == null || ratios.Length == 0)
                    return false;

                Array.Sort(ratios);
                int mid = ratios.Length / 2;
                medianRatio = (ratios.Length & 1) == 0
                    ? (ratios[mid - 1] + ratios[mid]) * 0.5f
                    : ratios[mid];
                medianRatio = Mathf.Clamp01(medianRatio);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Auto Flatness] Failed to compute median scale ratio: {e.Message}");
                return false;
            }
        }

        float[] ReadScaleRatios(GaussianSplatRenderer gs)
        {
            int count = gs.splatCount;
            if (count <= 0)
                return null;

            int kSplatSize = UnsafeUtility.SizeOf<Utils.InputSplatData>();
            using var gpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, kSplatSize);
            if (!gs.EditExportData(gpuData, false))
                return null;

            float[] rawFloats = new float[count * 62];
            gpuData.GetData(rawFloats);

            float[] ratios = new float[count];
            for (int i = 0; i < count; i++)
            {
                int offset = i * 62;
                float sx = Mathf.Abs(Mathf.Exp(rawFloats[offset + 55]));
                float sy = Mathf.Abs(Mathf.Exp(rawFloats[offset + 56]));
                float sz = Mathf.Abs(Mathf.Exp(rawFloats[offset + 57]));
                float minS = Mathf.Min(sx, Mathf.Min(sy, sz));
                float maxS = Mathf.Max(sx, Mathf.Max(sy, sz));
                ratios[i] = maxS > 1e-6f ? (minS / maxS) : 1f;
            }

            return ratios;
        }

    }
}
