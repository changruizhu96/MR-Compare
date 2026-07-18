// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;
using System.Reflection;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GaussianSplatting.Runtime
{
    public class GaussianSplatRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem ms_Instance;

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly HashSet<Camera> m_CameraCommandBuffersDone = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();

        CommandBuffer m_CommandBuffer;
        public GaussianSplatRenderer captureOnlyRenderer;

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                if (m_CameraCommandBuffersDone != null)
                {
                    if (m_CommandBuffer != null)
                    {
                        foreach (var cam in m_CameraCommandBuffersDone)
                        {
                            if (cam)
                                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                        }
                    }
                    m_CameraCommandBuffersDone.Clear();
                }

                m_ActiveSplats.Clear();
                m_CommandBuffer?.Dispose();
                m_CommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        public int CountAllGaussians()
        {
            int count = 0;
            foreach (var splat in m_ActiveSplats)
            {
                count += splat.Item1.asset.splatCount;
            }
            return count;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            if (!GaussianSplatRenderer.ShouldRenderCamera(cam))
                return false;
            // gather all active & valid splat objects
            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                if (!gs.ShouldRenderInCurrentMode)
                    continue;
                if (!gs.HasVisibleSplats)
                    continue;
                if (captureOnlyRenderer != null && gs != captureOnlyRenderer)
                    continue;
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            // sort them by order and depth from camera
            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                var orderA = a.Item1.m_RenderOrder;
                var orderB = b.Item1.m_RenderOrder;
                if (orderA != orderB)
                    return orderB.CompareTo(orderA);
                var trA = a.Item1.transform;
                var trB = b.Item1.transform;
                var posA = camTr.InverseTransformPoint(trA.position);
                var posB = camTr.InverseTransformPoint(trB.position);
                return posA.z.CompareTo(posB.z);
            });

            return true;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {

            Material matComposite = null;
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                gs.EnsureMaterials();
                matComposite = gs.m_MatComposite;
                var mpb = kvp.Item2;
                // Handle DebugBaked mode before any sorting or compute work.
                // =========================================================
                if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugBakedData)
                {
                    // 1. Prepare the renderer data.
                    if (!gs.EnsureBakedBuffers()) continue;

                    // 2. Prepare the material if the renderer has not created it yet.
                    if (gs.m_MatDebugBaked == null)
                    {
                        if (gs.m_ShaderDebugBaked == null)
                            gs.m_ShaderDebugBaked = Shader.Find("Gaussian Splatting/Debug Baked Points (Color)");

                        if (gs.m_ShaderDebugBaked != null)
                            gs.m_MatDebugBaked = new Material(gs.m_ShaderDebugBaked);
                    }

                    // 3. Draw the baked data.
                    if (gs.m_MatDebugBaked != null)
                    {
                        mpb.Clear();
                        // Access all renderer state through gs.
                        mpb.SetBuffer("_BakedPosBuffer", gs.m_GpuBakedPos);
                        mpb.SetBuffer("_BakedColorBuffer", gs.m_GpuBakedColor);
                        mpb.SetMatrix("_MatrixObjectToWorld", gs.transform.localToWorldMatrix);
                        mpb.SetFloat("_SplatSize", gs.m_PointDisplaySize);

                        // Draw using the baked-position count.
                        cmb.DrawProcedural(gs.m_GpuIndexBuffer, gs.transform.localToWorldMatrix, gs.m_MatDebugBaked, 0, MeshTopology.Triangles, 6, gs.m_GpuBakedPos.count, mpb);
                    }

                    // 4. Skip the remaining Gaussian rendering path for this object.
                    continue;
                }

                // sort
                var matrix = gs.transform.localToWorldMatrix;
                if (gs.m_FrameCounter % gs.m_SortNthFrame == 0)
                    gs.SortPoints(cmb, cam, matrix);
                ++gs.m_FrameCounter;

                // cache view
                kvp.Item2.Clear();
                Material displayMat = gs.m_RenderMode switch
                {
                    GaussianSplatRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    GaussianSplatRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null)
                    continue;

                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);

                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);

                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                cmb.BeginSample(s_ProfCalcView);
                gs.CalcViewData(cmb, cam);
                cmb.EndSample(s_ProfCalcView);

                // draw
                int indexCount = 6;
                int instanceCount = gs.splatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (gs.m_RenderMode is GaussianSplatRenderer.RenderMode.DebugBoxes or GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount, mpb);
                cmb.EndSample(s_ProfDraw);
            }
            return matComposite;
        }

        /// <summary>
        /// Render currently gathered splats using an override material.
        /// Intended for auxiliary capture passes (for example, depth capture).
        /// </summary>
        public void RenderSplatsWithMaterial(Camera cam, CommandBuffer cmb, Material overrideMaterial)
        {
            if (overrideMaterial == null)
                return;

            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                if (gs == null)
                    continue;

                gs.EnsureMaterials();

                // Skip special debug-only bake paths for capture passes.
                if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugBakedData)
                {
                    continue;
                }

                var matrix = gs.transform.localToWorldMatrix;
                if (gs.m_FrameCounter % gs.m_SortNthFrame == 0)
                    gs.SortPoints(cmb, cam, matrix);

                var mpb = kvp.Item2;
                mpb.Clear();
                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);
                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, 0);

                gs.CalcViewData(cmb, cam);

                cmb.DrawProcedural(
                    gs.m_GpuIndexBuffer,
                    matrix,
                    overrideMaterial,
                    0,
                    MeshTopology.Triangles,
                    6,
                    gs.splatCount,
                    mpb);
            }
        }

        public bool RenderSplatWithMaterial(Camera cam, CommandBuffer cmb, GaussianSplatRenderer gs, Material overrideMaterial)
        {
            if (cam == null || cmb == null || gs == null || overrideMaterial == null)
                return false;
            if (!gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                return false;

            gs.EnsureMaterials();

            if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugBakedData)
            {
                return false;
            }

            var matrix = gs.transform.localToWorldMatrix;
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            cmb.SetViewProjectionMatrices(cam.worldToCameraMatrix, projection);

            if (gs.m_FrameCounter % gs.m_SortNthFrame == 0)
                gs.SortPoints(cmb, cam, matrix);

            var mpb = new MaterialPropertyBlock();
            gs.SetAssetDataOnMaterial(mpb);
            mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);
            mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);
            mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
            mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
            mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
            mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
            mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
            mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
            mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, 0);
            mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, 0);

            gs.CalcViewData(cmb, cam);
            cmb.DrawProcedural(
                gs.m_GpuIndexBuffer,
                matrix,
                overrideMaterial,
                0,
                MeshTopology.Triangles,
                6,
                gs.splatCount,
                mpb);
            return true;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            m_CommandBuffer ??= new CommandBuffer { name = "RenderGaussianSplats" };
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
            m_CommandBuffer.Clear();
            return m_CommandBuffer;
        }

        void OnPreCullCamera(Camera cam)
        {
            if (!GatherSplatsForCamera(cam))
                return;

            InitialClearCmdBuffer(cam);

            m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // We only need this to determine whether we're rendering into backbuffer or not. However, detection this
            // way only works in BiRP so only do it here.
            m_CommandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

            // compose
            m_CommandBuffer.BeginSample(s_ProfCompose);
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }
    }


    public static class GaussianSplatRendererExtensions
    {
        /// <summary>
        /// Retrieve the internal m_GpuPosData field from GaussianSplatRenderer through reflection.
        /// The renderer must be initialized with its GPU data available.
        /// </summary>
        /// <param name="renderer">GaussianSplatRenderer instance.</param>
        /// <returns>The GraphicsBuffer containing GPU position data.</returns>
        public static GraphicsBuffer GetGpuPosData(this GaussianSplatRenderer renderer)
        {
            // Direct access is possible when m_GpuPosData is internal and this code shares its namespace:
            // return renderer.m_GpuPosData;
            // Otherwise, retrieve it through reflection:
            var field = typeof(GaussianSplatRenderer).GetField("m_GpuPosData", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                return field.GetValue(renderer) as GraphicsBuffer;
            }
            Debug.LogError("Could not access m_GpuPosData through reflection.");
            return null;
        }

        public static GraphicsBuffer GetGpuViewData(this GaussianSplatRenderer renderer)
        {
            if (renderer == null) return null;

            var field = typeof(GaussianSplatRenderer).GetField("m_GpuView", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                return field.GetValue(renderer) as GraphicsBuffer;
            }

            Debug.LogError("Could not access m_GpuView through reflection.");
            return null;
        }

        public static GraphicsBuffer GetGpuSortKeys(this GaussianSplatRenderer renderer)
        {
            if (renderer == null) return null;

            var field = typeof(GaussianSplatRenderer).GetField("m_GpuSortKeys", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
                return field.GetValue(renderer) as GraphicsBuffer;

            Debug.LogError("Could not access m_GpuSortKeys through reflection.");
            return null;
        }

        public static bool PrepareGaussianCutViewData(this GaussianSplatRenderer renderer, Camera camera)
        {
            if (renderer == null || camera == null)
                return false;
            if (!renderer.isActiveAndEnabled || !renderer.HasValidAsset || !renderer.HasValidRenderSetup)
                return false;

            renderer.EnsureMaterials();
            using var cmd = new CommandBuffer { name = "Prepare GaussianCut Contribution View Data" };
            Matrix4x4 matrix = renderer.transform.localToWorldMatrix;
            Matrix4x4 view = camera.worldToCameraMatrix;
            // GaussianCut compares against capture RenderTextures, so prepare the same GPU projection
            // convention Unity uses when splats are rendered into the ID/RGB capture targets.
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            cmd.SetViewProjectionMatrices(view, projection);
            renderer.SortPoints(cmd, camera, matrix);
            renderer.CalcViewData(cmd, camera);
            Graphics.ExecuteCommandBuffer(cmd);
            return true;
        }
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        const string kShaderSplatsName = "Gaussian Splatting/Render Splats";
        const string kShaderCompositeName = "Hidden/Gaussian Splatting/Composite";
        const string kShaderDebugPointsName = "Gaussian Splatting/Debug/Render Points";
        const string kShaderDebugBoxesName = "Gaussian Splatting/Debug/Render Boxes";
        const string kShaderDebugBakedName = "Gaussian Splatting/Debug Baked Points (Color)";

#if UNITY_EDITOR
        const string kShaderSplatsPath = "Packages/package/Shaders/RenderGaussianSplats.shader";
        const string kShaderCompositePath = "Packages/package/Shaders/GaussianComposite.shader";
        const string kShaderDebugPointsPath = "Packages/package/Shaders/GaussianDebugRenderPoints.shader";
        const string kShaderDebugBoxesPath = "Packages/package/Shaders/GaussianDebugRenderBoxes.shader";
        const string kShaderDebugBakedPath = "Packages/package/Shaders/GaussianDebugBaked.shader";
        const string kSplatUtilitiesSurfacePath = "Packages/package/Shaders/SplatUtilitiesSurface.compute";
#endif

        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
            DebugChunkBounds,
            DebugBakedData
        }

        public enum BakeSelectionShape
        {
            Box,
            Sphere
        }
        public GaussianSplatAsset m_Asset;
        [Header("Baking & Debug")]
        public GaussianSplatBakedData m_BakedData;
        [NonSerialized] public float m_LastBakeDurationMs = -1f;
        [NonSerialized] public float m_LastBakeGpuDurationMs = -1f;
        [NonSerialized] public float m_LastBakeCpuDurationMs = -1f;
        [NonSerialized] public int m_LastBakePointCount;
        [NonSerialized] public bool m_HasLastBakeStats;

        [Header("Density Filtering (for Bake)")]
        [Tooltip("Spatial grid size in meters. Smaller values detect density more precisely but may classify edge points as isolated.")]
        [Range(0.01f, 1.0f)]
        public float m_DensityGridSpacing = 0.05f;
        
        [Tooltip("Minimum neighbor count. Points below this value are treated as noise. Set to 0 to disable density filtering.")]
        [Range(0, 20)]
        public int m_DensityMinNeighbors = 2;

        [Header("Coordinate Normalization")]
        [Tooltip("Automatically detect and normalize coordinates to meters when the scene extent is greater than 100.")]
        public bool m_AutoNormalizeCoords = false;

        [Tooltip("Enable spatial ROI filtering during bake.")]
        public bool m_EnableSelectionFilter = false;
        [Tooltip("Selection region shape in world space.")]
        public BakeSelectionShape m_SelectionShape = BakeSelectionShape.Box;
        [Tooltip("Selection region size in world space. For Sphere mode this is ellipsoid diameter.")]
        public Vector3 m_SelectionSize = new Vector3(10f, 10f, 10f);
        [Tooltip("Optional transform used as selection center. If null, renderer transform is used.")]
        public Transform m_SelectionCenterOverride;
        [Tooltip("Use current baked-point centroid as center when no override is provided.")]
        public bool m_SelectionUsePointCentroid = false;
        [Tooltip("Additional world-space offset applied to the selection center.")]
        public Vector3 m_SelectionCenterOffset = Vector3.zero;

        [Tooltip("Rendering order compared to other splats. Within same order splats are sorted by distance. Higher order splats render 'on top of' lower order splats.")]
        public int m_RenderOrder;
        [Range(0.1f, 2.0f)]
        [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0, 3)]
        [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;
        [Range(1, 30)]
        [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 1;
        [Tooltip("Render splats while the editor is not in Play Mode. Disable this to avoid continuous editor GPU work.")]
        public bool m_RenderInEditMode = false;

        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f, 15.0f)] public float m_PointDisplaySize = 3.0f;

        public GaussianCutout[] m_Cutouts;

        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public Shader m_ShaderDebugPoints;
        public Shader m_ShaderDebugBoxes;
        // Rendering resources
        public Shader m_ShaderDebugBaked;
        internal Material m_MatDebugBaked;
        internal GraphicsBuffer m_GpuBakedPos;
        internal GraphicsBuffer m_GpuBakedColor;
        [Tooltip("Gaussian splatting compute shader")]
        public ComputeShader m_CSSplatUtilities;


        int m_SplatCount; // initially same as asset splat count, but editing can change this
        GraphicsBuffer m_GpuSortDistances;
        internal GraphicsBuffer m_GpuSortKeys;
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuView;
        internal GraphicsBuffer m_GpuIndexBuffer;

        // these buffers are only for splat editing, and are lazily created
        GraphicsBuffer m_GpuEditCutouts;
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

        GpuSorting m_Sorter;
        GpuSorting.Args m_SorterArgs;

        internal Material m_MatSplats;
        internal Material m_MatComposite;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;

        internal int m_FrameCounter;
        GaussianSplatAsset m_PrevAsset;
        Hash128 m_PrevHash;
        bool m_Registered;

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

        internal static class Props
        {
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int GSColorMatchEnabled = Shader.PropertyToID("_GSColorMatchEnabled");
            public static readonly int GSColorMatchMatrix = Shader.PropertyToID("_GSColorMatchMatrix");
            public static readonly int GSColorMatchBias = Shader.PropertyToID("_GSColorMatchBias");
            public static readonly int GSColorMatchToneCurve = Shader.PropertyToID("_GSColorMatchToneCurve");
            public static readonly int GSColorMatchLut3DEnabled = Shader.PropertyToID("_GSColorMatchLut3DEnabled");
            public static readonly int GSColorMatchLut3D = Shader.PropertyToID("_GSColorMatchLut3D");
            public static readonly int GSColorMatchStrength = Shader.PropertyToID("_GSColorMatchStrength");
            public static readonly int GSColorMatchInputIsLinear = Shader.PropertyToID("_GSColorMatchInputIsLinear");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
        }

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplatAsset asset => m_Asset;
        public int splatCount => m_SplatCount;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
        }

        public bool HasValidAsset =>
            m_Asset != null &&
            m_Asset.splatCount > 0 &&
            m_Asset.formatVersion == GaussianSplatAsset.kCurrentVersion &&
            m_Asset.posData != null &&
            m_Asset.otherData != null &&
            m_Asset.shData != null &&
            m_Asset.colorData != null;
        public bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;
        internal bool ShouldRenderInCurrentMode => Application.isPlaying || m_RenderInEditMode;
        internal bool HasVisibleSplats => m_SplatScale > 0f && m_OpacityScale > 0f;

        internal static bool ShouldRenderCamera(Camera cam)
        {
            if (cam == null || cam.cameraType == CameraType.Preview)
                return false;

            // MR/XR Game cameras can render continuously while Unity is only editing the scene.
            // Keep Scene View previews, but reserve Game/XR camera rendering for Play Mode.
            if (!Application.isPlaying && cam.cameraType == CameraType.Game)
                return false;

            return true;
        }

        const int kGpuViewDataSize = 40;

        void Reset()
        {
            EnsureDefaultResourceReferences();
        }

        void OnValidate()
        {
            EnsureDefaultResourceReferences();
            RefreshRenderSystemRegistration();
        }

        void EnsureDefaultResourceReferences()
        {
            bool changed = false;

            changed |= TryAssignShader(ref m_ShaderSplats, kShaderSplatsName
#if UNITY_EDITOR
                , kShaderSplatsPath
#endif
            );
            changed |= TryAssignShader(ref m_ShaderComposite, kShaderCompositeName
#if UNITY_EDITOR
                , kShaderCompositePath
#endif
            );
            changed |= TryAssignShader(ref m_ShaderDebugPoints, kShaderDebugPointsName
#if UNITY_EDITOR
                , kShaderDebugPointsPath
#endif
            );
            changed |= TryAssignShader(ref m_ShaderDebugBoxes, kShaderDebugBoxesName
#if UNITY_EDITOR
                , kShaderDebugBoxesPath
#endif
            );
            changed |= TryAssignShader(ref m_ShaderDebugBaked, kShaderDebugBakedName
#if UNITY_EDITOR
                , kShaderDebugBakedPath
#endif
            );
#if UNITY_EDITOR
            if (m_CSSplatUtilities == null)
            {
                m_CSSplatUtilities = AssetDatabase.LoadAssetAtPath<ComputeShader>(kSplatUtilitiesSurfacePath);
                changed |= m_CSSplatUtilities != null;
            }

            if (changed && !Application.isPlaying)
            {
                EditorUtility.SetDirty(this);
            }
#endif
        }

        static bool TryAssignShader(ref Shader field, string shaderName
#if UNITY_EDITOR
            , string assetPath
#endif
        )
        {
            if (field != null)
                return false;

#if UNITY_EDITOR
            field = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
            if (field != null)
                return true;
#endif

            field = Shader.Find(shaderName);
            return field != null;
        }

        void CreateResourcesForAsset()
        {
            if (!HasValidAsset)
                return;

            m_SplatCount = asset.splatCount;
            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
            m_GpuPosData.SetData(asset.posData.GetData<uint>());
            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
            m_GpuOtherData.SetData(asset.otherData.GetData<uint>());
            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
            m_GpuSHData.SetData(asset.shData.GetData<uint>());
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
            tex.Apply(false, true);
            m_GpuColorData = tex;
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int)(asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                { name = "GaussianChunkData" };
                m_GpuChunks.SetData(asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                m_GpuChunksValid = true;
            }
            else
            {
                // just a dummy chunk buffer
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                { name = "GaussianChunkData" };
                m_GpuChunksValid = false;
            }

            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.splatCount, kGpuViewDataSize);
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            // cube indices, most often we use only the first quad
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });

            InitSortBuffers(splatCount);
        }

        void InitSortBuffers(int count)
        {
            m_GpuSortDistances?.Dispose();
            m_GpuSortKeys?.Dispose();
            m_SorterArgs.resources.Dispose();

            EnsureSorterAndRegister();

            m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortDistances" };
            m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortIndices" };

            // init keys buffer to splat indices
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
            m_CSSplatUtilities.SetInt(Props.SplatCount, m_GpuSortDistances.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.SetIndices, (m_GpuSortDistances.count + (int)gsX - 1) / (int)gsX, 1, 1);

            m_SorterArgs.inputKeys = m_GpuSortDistances;
            m_SorterArgs.inputValues = m_GpuSortKeys;
            m_SorterArgs.count = (uint)count;
            if (m_Sorter.Valid)
                m_SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);
        }

        bool resourcesAreSetUp => m_ShaderSplats != null && m_ShaderComposite != null && m_ShaderDebugPoints != null &&
                                  m_ShaderDebugBoxes != null && m_CSSplatUtilities != null && SystemInfo.supportsComputeShaders;

        public void EnsureMaterials()
        {
            if (m_MatSplats == null && resourcesAreSetUp)
            {
                m_MatSplats = new Material(m_ShaderSplats) { name = "GaussianSplats" };
                m_MatComposite = new Material(m_ShaderComposite) { name = "GaussianClearDstAlpha" };
                m_MatDebugPoints = new Material(m_ShaderDebugPoints) { name = "GaussianDebugPoints" };
                m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) { name = "GaussianDebugBoxes" };
                ClearColorCalibrationOnMaterial(m_MatComposite);
            }
        }

        public float colorCalibrationEnabled
        {
            get
            {
                EnsureMaterials();
                return m_MatComposite.GetFloat(Props.GSColorMatchEnabled);
            }
            set
            {
                EnsureMaterials();
                m_MatComposite.SetFloat(Props.GSColorMatchEnabled, value);
            }
        }

        public void ApplyColorCalibration(Matrix4x4 matrix, Vector3 bias, Texture toneCurve, Texture lut3D, float strength = 1f, bool inputIsLinear = false)
        {
            EnsureMaterials();
            m_MatComposite.SetMatrix(Props.GSColorMatchMatrix, matrix);
            m_MatComposite.SetVector(Props.GSColorMatchBias, new Vector4(bias.x, bias.y, bias.z, 0f));
            m_MatComposite.SetTexture(Props.GSColorMatchToneCurve, toneCurve);
            m_MatComposite.SetFloat(Props.GSColorMatchLut3DEnabled, lut3D != null ? 1f : 0f);
            m_MatComposite.SetFloat(Props.GSColorMatchStrength, strength);
            m_MatComposite.SetFloat(Props.GSColorMatchInputIsLinear, inputIsLinear ? 1f : 0f);
            if (lut3D != null)
                m_MatComposite.SetTexture(Props.GSColorMatchLut3D, lut3D);
            m_MatComposite.SetFloat(Props.GSColorMatchEnabled, 1f);
        }

        public void ClearColorCalibration()
        {
            EnsureMaterials();
            ClearColorCalibrationOnMaterial(m_MatComposite);
        }

        static void ClearColorCalibrationOnMaterial(Material material)
        {
            material.SetFloat(Props.GSColorMatchEnabled, 0f);
            material.SetFloat(Props.GSColorMatchLut3DEnabled, 0f);
            material.SetFloat(Props.GSColorMatchStrength, 1f);
            material.SetFloat(Props.GSColorMatchInputIsLinear, 0f);
            material.SetMatrix(Props.GSColorMatchMatrix, Matrix4x4.identity);
            material.SetVector(Props.GSColorMatchBias, Vector4.zero);
        }

        public void EnsureSorterAndRegister()
        {
            if (m_Sorter == null && resourcesAreSetUp)
            {
                m_Sorter = new GpuSorting(m_CSSplatUtilities);
            }

            RefreshRenderSystemRegistration();
        }

        void RefreshRenderSystemRegistration()
        {
            if (!resourcesAreSetUp || !ShouldRenderInCurrentMode)
            {
                UnregisterFromRenderSystem();
                return;
            }

            if (!m_Registered)
            {
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
                m_Registered = true;
            }
        }

        void UnregisterFromRenderSystem()
        {
            if (!m_Registered)
                return;

            GaussianSplatRenderSystem.instance.UnregisterSplat(this);
            m_Registered = false;
        }

        public void OnEnable()
        {
            m_FrameCounter = 0;
            if (!resourcesAreSetUp)
                return;

            EnsureMaterials();

            CreateResourcesForAsset();
            RefreshRenderSystemRegistration();
        }

        void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            ComputeShader cs = m_CSSplatUtilities;
            int kernelIndex = (int)kernel;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, m_GpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, m_GpuSortKeys);
            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_GpuEditCutouts);
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        public static int CompleteGaussianCount()
        {
            return GaussianSplatRenderSystem.instance.CountAllGaussians();
        }

        // Release baked GPU resources.
        void DisposeBakedResources()
        {
            if (m_GpuBakedPos != null) { m_GpuBakedPos.Dispose(); m_GpuBakedPos = null; }
            if (m_GpuBakedColor != null) { m_GpuBakedColor.Dispose(); m_GpuBakedColor = null; }
            // Destroy the material because it may have been created at runtime.
            if (m_MatDebugBaked != null) { DestroyImmediate(m_MatDebugBaked); m_MatDebugBaked = null; }
        }

        void DisposeResourcesForAsset()
        {
            DestroyImmediate(m_GpuColorData);

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);

            DisposeBuffer(ref m_GpuView);
            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);
            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            DisposeBuffer(ref m_GpuEditCutouts);

            m_SorterArgs.resources.Dispose();

            m_SplatCount = 0;
            m_GpuChunksValid = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        internal bool EnsureBakedBuffers()
        {
            // Check whether baked data is available.
            if (m_BakedData == null || m_BakedData.PointCount == 0) return false;

            int count = m_BakedData.PointCount;

            // Rebuild the buffers when they are missing, invalid, or have the wrong length.
            if (m_GpuBakedPos == null || m_GpuBakedPos.count != count || !m_GpuBakedPos.IsValid())
            {
                DisposeBakedResources(); // Release the old resources first.

                m_GpuBakedPos = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 12); // float3
                m_GpuBakedPos.SetData(m_BakedData.positions);

                m_GpuBakedColor = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16); // float4
                m_GpuBakedColor.SetData(m_BakedData.colors);
            }
            return true;
        }

        public void OnDisable()
        {
            DisposeResourcesForAsset();
            DisposeBakedResources();
            UnregisterFromRenderSystem();

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatComposite);
            DestroyImmediate(m_MatDebugPoints);
            DestroyImmediate(m_MatDebugBoxes);
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam)
        {
            if (!ShouldRenderCamera(cam))
                return;

            var tr = transform;

            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            bool useXrEyeTextureSize =
                cam.cameraType == CameraType.Game &&
                cam.stereoEnabled &&
                eyeW > 0 &&
                eyeH > 0;
            Vector4 screenPar = new Vector4(
                useXrEyeTextureSize ? eyeW : screenW,
                useXrEyeTextureSize ? eyeH : screenH,
                0,
                0);
            Vector4 camPos = cam.transform.position;

            // calculate view dependent data for each splat
            SetAssetDataOnCS(cmb, KernelIndices.CalcViewData);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);

            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcViewData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcViewData, (m_GpuView.count + (int)gsX - 1) / (int)gsX, 1, 1);
        }




        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (!ShouldRenderCamera(cam))
                return;

            Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            // calculate distance to the camera for each splat
            cmd.BeginSample(s_ProfSort);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortDistances, m_GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortKeys, m_GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)m_Asset.posFormat);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcDistances, out uint gsX, out _, out _);
            cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, (m_GpuSortDistances.count + (int)gsX - 1) / (int)gsX, 1, 1);

            // sort the splats
            EnsureSorterAndRegister();
            m_Sorter.Dispatch(cmd, m_SorterArgs);
            cmd.EndSample(s_ProfSort);
        }

        public void Update()
        {
            RefreshRenderSystemRegistration();

            var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
            if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
            {
                m_PrevAsset = m_Asset;
                m_PrevHash = curHash;
                if (resourcesAreSetUp)
                {
                    DisposeResourcesForAsset();
                    CreateResourcesForAsset();
                }
                else
                {
                    Debug.LogError($"{nameof(GaussianSplatRenderer)} component is not set up correctly (Resource references are missing), or platform does not support compute shaders");
                }
            }
        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.ClearBuffer, Props.DstBuffer, buf);
            m_CSSplatUtilities.SetInt(Props.BufferSize, buf.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.ClearBuffer, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.ClearBuffer, (int)((buf.count + gsX - 1) / gsX), 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.SrcBuffer, src);
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.DstBuffer, dst);
            m_CSSplatUtilities.SetInt(Props.BufferSize, dst.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.OrBuffers, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.OrBuffers, (int)((dst.count + gsX - 1) / gsX), 1, 1);
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            if (m_GpuEditSelected == null)
            {
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            m_CSSplatUtilities.SetBuffer((int)KernelIndices.InitEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.InitEditData, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.UpdateEditData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, (int)((m_GpuEditSelected.count + gsX - 1) / gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f, 0.1f, 0.1f);
            editSelectedBounds = bounds;
        }

        void UpdateCutoutsBuffer()
        {
            int bufferSize = m_Cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuEditCutouts == null || m_GpuEditCutouts.count != bufferSize)
            {
                m_GpuEditCutouts?.Dispose();
                m_GpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (m_Cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < m_Cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(m_Cutouts[i], matrix);
                }
            }

            m_GpuEditCutouts.SetData(data);
            data.Dispose();
        }

        bool EnsureEditingBuffers()
        {
            if (!HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) { name = "GaussianSplatSelected" };
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) { name = "GaussianSplatSelectedInit" };
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) { name = "GaussianSplatDeleted" };
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) { name = "GaussianSplatEditData" }; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
            }
            return m_GpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) { name = "GaussianSplatEditPosMouseDown" };
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }
        public void EditStoreOtherMouseDown()
        {
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) { name = "GaussianSplatEditOtherMouseDown" };
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }




        // -------------------------------------------------------------------------
        // Capture helpers with aligned memory layout and explicit buffer binding
        // -------------------------------------------------------------------------

        // 1. Define the 32-byte structure that matches the shader layout.
        struct CapturedPoint
        {
            public Vector3 pos;
            public float pad;    // Padding
            public Vector4 color;
        }

        // 2. Bind the compute buffers required by a kernel.
        void SetComputeBuffersForKernel(CommandBuffer cmb, int kernel)
        {
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernel, "_SplatPos", m_GpuPosData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernel, "_SplatOther", m_GpuOtherData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernel, "_SplatSH", m_GpuSHData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernel, "_SplatChunks", m_GpuChunks);
            if (m_GpuColorData != null) cmb.SetComputeTextureParam(m_CSSplatUtilities, kernel, "_SplatColor", m_GpuColorData);
        }

        public void SetPreprocessorComputeBuffers(int kernel)
        {
            // Prefer filtered baked positions when they are available.
            if (m_GpuBakedPos != null && m_GpuBakedPos.count > 0)
            {
                // Use BakedData positions.
                m_CSSplatUtilities.SetBuffer(kernel, "_SplatPos", m_GpuBakedPos);

                // BakedData contains only positions and colors.
                // The compute shader still expects the other, SH, and chunk buffers, so bind the source data.
                m_CSSplatUtilities.SetBuffer(kernel, "_SplatOther", m_GpuOtherData);
                m_CSSplatUtilities.SetBuffer(kernel, "_SplatSH", m_GpuSHData);
                m_CSSplatUtilities.SetBuffer(kernel, "_SplatChunks", m_GpuChunks);

                if (m_GpuBakedColor != null)
                    m_CSSplatUtilities.SetBuffer(kernel, "_SplatColor", m_GpuBakedColor);
                else if (m_GpuColorData != null)
                    m_CSSplatUtilities.SetTexture(kernel, "_SplatColor", m_GpuColorData);
            }
            else
            {
                // Fall back to the source data.
                m_CSSplatUtilities.SetBuffer(kernel, "_SplatPos", m_GpuPosData);
                m_CSSplatUtilities.SetBuffer(kernel, "_SplatOther", m_GpuOtherData);
                m_CSSplatUtilities.SetBuffer(kernel, "_SplatSH", m_GpuSHData);
                m_CSSplatUtilities.SetBuffer(kernel, "_SplatChunks", m_GpuChunks);
                if (m_GpuColorData != null)
                    m_CSSplatUtilities.SetTexture(kernel, "_SplatColor", m_GpuColorData);
            }
        }


        // 3. Capture and bake the data.
        public void CaptureAndBakeData(float minOpacity = 0f, float scaleRatio = 1f, bool enableDensity = false)
        {
            var bakeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var stageStopwatch = System.Diagnostics.Stopwatch.StartNew();
            m_HasLastBakeStats = false;
            m_LastBakeDurationMs = -1f;
            m_LastBakeGpuDurationMs = -1f;
            m_LastBakeCpuDurationMs = -1f;
            m_LastBakePointCount = 0;

            if (!EnsureEditingBuffers())
            {
                Debug.LogError("[Bake] Asset is not ready.");
                return;
            }

            // Scene dimensions and density-filter parameters
            Vector3 sceneBounds = m_Asset.boundsMax - m_Asset.boundsMin;
            float sceneVolume = sceneBounds.x * sceneBounds.y * sceneBounds.z;
            float maxDim = Mathf.Max(sceneBounds.x, Mathf.Max(sceneBounds.y, sceneBounds.z));
            
            // Calculate the automatic coordinate-normalization scale.
            float coordScale = 1.0f;
            float effectiveGridSpacing = m_DensityGridSpacing;
            
            if (m_AutoNormalizeCoords && maxDim > 100f)
            {
                // Normalize the largest dimension to approximately 10 meters.
                coordScale = 10f / maxDim;
                effectiveGridSpacing = m_DensityGridSpacing / coordScale;  // Preserve the effective grid spacing.
                Debug.Log($"[Bake Auto-Normalize] Detected large scene (max={maxDim:F1}), applying scale={coordScale:F6}");
                Debug.Log($"[Bake Auto-Normalize] Effective Grid Spacing: {effectiveGridSpacing:F4} units");
            }
            
            float gridVolume = effectiveGridSpacing * effectiveGridSpacing * effectiveGridSpacing;
            float expectedPointsPerGrid = (float)m_SplatCount * gridVolume / sceneVolume;
            Debug.Log($"[Bake Diagnostics] Scene Bounds: {sceneBounds} (volume: {sceneVolume:F2})");
            Debug.Log($"[Bake Diagnostics] Grid Spacing: {effectiveGridSpacing:F4} units, Min Neighbors: {m_DensityMinNeighbors}");
            Debug.Log($"[Bake Diagnostics] Expected pts/grid: {expectedPointsPerGrid:F2}, Total splats: {m_SplatCount}");

            const int GRID_SIZE = 2000003;
            using var gridBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, GRID_SIZE, 4);

            // Stride = 32
            using var captureBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 32);

            using var cmb = new CommandBuffer { name = "AdvancedBake" };

            // Pass the parameters with the adjusted grid spacing.
            cmb.SetComputeFloatParam(m_CSSplatUtilities, "_FilterMinOpacity", minOpacity);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, "_FilterScaleRatio", scaleRatio);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, "_FilterGridSpacing", effectiveGridSpacing);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_FilterMinNeighbors", m_DensityMinNeighbors);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_FilterEnableDensity", enableDensity ? 1 : 0);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_SplatCount", m_SplatCount);

            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            int groupSize = 1024;
            int threadGroups = (m_SplatCount + groupSize - 1) / groupSize;
            int gridGroups = (GRID_SIZE + groupSize - 1) / groupSize;

            // Dispatch the kernels.
            if (enableDensity)
            {
                int kClear = m_CSSplatUtilities.FindKernel("CSClearDensityGrid");
                cmb.SetComputeBufferParam(m_CSSplatUtilities, kClear, "_DensityGridBuffer", gridBuffer);
                cmb.DispatchCompute(m_CSSplatUtilities, kClear, gridGroups, 1, 1);

                int kBuild = m_CSSplatUtilities.FindKernel("CSBuildDensityGrid");
                SetComputeBuffersForKernel(cmb, kBuild); // Bind the shared input buffers.
                cmb.SetComputeBufferParam(m_CSSplatUtilities, kBuild, "_DensityGridBuffer", gridBuffer);
                cmb.DispatchCompute(m_CSSplatUtilities, kBuild, threadGroups, 1, 1);
            }

            int kCap = m_CSSplatUtilities.FindKernel("CSAdvancedCapture");
            SetComputeBuffersForKernel(cmb, kCap); // Bind the shared input buffers.

            // Bind gridBuffer unconditionally because the kernel always declares it.
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kCap, "_DensityGridBuffer", gridBuffer);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kCap, "_CaptureBuffer", captureBuffer);

            cmb.DispatchCompute(m_CSSplatUtilities, kCap, threadGroups, 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            // Read the captured data back.
            CapturedPoint[] rawData = new CapturedPoint[m_SplatCount];
            captureBuffer.GetData(rawData);
            stageStopwatch.Stop();
            m_LastBakeGpuDurationMs = (float)stageStopwatch.Elapsed.TotalMilliseconds;

            stageStopwatch.Restart();

            System.Collections.Generic.List<Vector3> validPos = new();
            System.Collections.Generic.List<Color> validCol = new();
            System.Collections.Generic.List<float> validOpacities = new();  // Store opacity separately.

            for (int i = 0; i < rawData.Length; i++)
            {
                if (rawData[i].color.w > 0.0001f)
                {
                    validPos.Add(rawData[i].pos);
                    // Preserve the source alpha (opacity) instead of forcing it to 1.
                    validCol.Add(new Color(rawData[i].color.x, rawData[i].color.y, rawData[i].color.z, rawData[i].color.w));
                    validOpacities.Add(rawData[i].color.w);  // Store opacity separately.
                }
            }

#if UNITY_EDITOR
            ApplyOutlierFilters(validPos, validCol, validOpacities);

            GaussianSplatBakedData newData = ScriptableObject.CreateInstance<GaussianSplatBakedData>();
            newData.positions = validPos.ToArray();
            newData.colors = validCol.ToArray();
            newData.opacities = validOpacities.ToArray(); // Store the opacity array.

            string originalPath = UnityEditor.AssetDatabase.GetAssetPath(m_Asset);
            string dir = System.IO.Path.GetDirectoryName(originalPath);
            string name = System.IO.Path.GetFileNameWithoutExtension(originalPath);
            
            string suffix = "_Baked";
            if (!enableDensity && scaleRatio > 0.9f) suffix = "_OpacityOnly";
            else if (!enableDensity && scaleRatio <= 0.9f) suffix = "_Geometric";
            else if (enableDensity) suffix = "_Full";

            string newPath = $"{dir}/{name}{suffix}.asset";
            if (UnityEditor.AssetDatabase.LoadAssetAtPath<GaussianSplatBakedData>(newPath) != null)
            {
                UnityEditor.AssetDatabase.DeleteAsset(newPath);
            }
            UnityEditor.AssetDatabase.CreateAsset(newData, newPath);
            UnityEditor.AssetDatabase.SaveAssets();
            
            this.m_BakedData = newData;
            UnityEditor.EditorUtility.SetDirty(this);
            stageStopwatch.Stop();
            bakeStopwatch.Stop();
            m_LastBakeDurationMs = (float)bakeStopwatch.Elapsed.TotalMilliseconds;
            m_LastBakeCpuDurationMs = (float)stageStopwatch.Elapsed.TotalMilliseconds;
            m_LastBakePointCount = validPos.Count;
            m_HasLastBakeStats = true;
            Debug.Log($"[Bake Success] {suffix} | Points: {validPos.Count} | Total: {m_LastBakeDurationMs:F1} ms | GPU+Readback: {m_LastBakeGpuDurationMs:F1} ms | CPU+Save: {m_LastBakeCpuDurationMs:F1} ms | Path: {newPath}");
#endif
        }

        private void ApplyOutlierFilters(List<Vector3> positions, List<Color> colors, List<float> opacities)
        {
            if (positions == null || colors == null || opacities == null)
                return;
            if (positions.Count == 0 || colors.Count != positions.Count || opacities.Count != positions.Count)
                return;

            if (!m_EnableSelectionFilter)
                return;

            int countBeforeAll = positions.Count;

            if (positions.Count > 0)
            {
                int before = positions.Count;
                bool[] keep = ComputeSelectionMask(positions, out Vector3 centerWorld);
                ApplyMaskInPlace(positions, colors, opacities, keep);
                Debug.Log($"[Selection ROI] shape={m_SelectionShape}, center={centerWorld}, size={m_SelectionSize}, kept={positions.Count}/{before}");
            }

            Debug.Log($"[Selection ROI Summary] kept={positions.Count}/{countBeforeAll}");
        }

        private static void ApplyMaskInPlace(List<Vector3> positions, List<Color> colors, List<float> opacities, bool[] keepMask)
        {
            int count = positions.Count;
            if (keepMask == null || keepMask.Length != count)
                return;

            int keptCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (keepMask[i]) keptCount++;
            }

            if (keptCount == count)
                return;

            List<Vector3> filteredPos = new List<Vector3>(keptCount);
            List<Color> filteredCol = new List<Color>(keptCount);
            List<float> filteredOpacity = new List<float>(keptCount);

            for (int i = 0; i < count; i++)
            {
                if (!keepMask[i]) continue;
                filteredPos.Add(positions[i]);
                filteredCol.Add(colors[i]);
                filteredOpacity.Add(opacities[i]);
            }

            positions.Clear();
            colors.Clear();
            opacities.Clear();

            positions.AddRange(filteredPos);
            colors.AddRange(filteredCol);
            opacities.AddRange(filteredOpacity);
        }

        private bool[] ComputeSelectionMask(IReadOnlyList<Vector3> pointsLocal, out Vector3 centerWorld)
        {
            int count = pointsLocal.Count;
            bool[] keep = new bool[count];
            centerWorld = ResolveSelectionCenterWorld(pointsLocal);

            Vector3 halfExtents = m_SelectionSize * 0.5f;
            Matrix4x4 localToWorld = transform.localToWorldMatrix;

            for (int i = 0; i < count; i++)
            {
                Vector3 worldPoint = localToWorld.MultiplyPoint3x4(pointsLocal[i]);
                keep[i] = IsPointInsideSelectionRegion(worldPoint, centerWorld, halfExtents);
            }

            return keep;
        }

        private Vector3 ResolveSelectionCenterWorld(IReadOnlyList<Vector3> pointsLocal)
        {
            if (m_SelectionCenterOverride != null)
            {
                return m_SelectionCenterOverride.position + m_SelectionCenterOffset;
            }

            if (m_SelectionUsePointCentroid && pointsLocal != null && pointsLocal.Count > 0)
            {
                Matrix4x4 localToWorld = transform.localToWorldMatrix;
                Vector3 sum = Vector3.zero;
                for (int i = 0; i < pointsLocal.Count; i++)
                {
                    sum += localToWorld.MultiplyPoint3x4(pointsLocal[i]);
                }
                return (sum / pointsLocal.Count) + m_SelectionCenterOffset;
            }

            return transform.position + m_SelectionCenterOffset;
        }

        private bool IsPointInsideSelectionRegion(Vector3 worldPoint, Vector3 centerWorld, Vector3 halfExtents)
        {
            Vector3 d = worldPoint - centerWorld;
            if (m_SelectionShape == BakeSelectionShape.Box)
            {
                return Mathf.Abs(d.x) <= halfExtents.x &&
                       Mathf.Abs(d.y) <= halfExtents.y &&
                       Mathf.Abs(d.z) <= halfExtents.z;
            }

            float hx = Mathf.Max(halfExtents.x, 1e-6f);
            float hy = Mathf.Max(halfExtents.y, 1e-6f);
            float hz = Mathf.Max(halfExtents.z, 1e-6f);
            float nx = d.x / hx;
            float ny = d.y / hy;
            float nz = d.z / hz;
            return nx * nx + ny * ny + nz * nz <= 1f;
        }

        // -------------------------------------------------------------------------
        // Offline data capture and baking
        // Kept inside GaussianSplatRenderer near the end of the class.
        // -------------------------------------------------------------------------

        // Temporary structure that must match the compute-shader memory layout.
        // float3 (12 bytes) + float4 (16 bytes) = 28 bytes


        //     struct CapturedPoint 
        //     { 
        //         public Vector3 pos; 
        //         public Vector4 color; 
        //     }

        //     /// <summary>
        //     /// Run the compute shader, filter the captured data, and save it as an .asset file.
        //     /// </summary>
        //     /// <param name="minOpacity">Opacity threshold in the range 0-1.</param>
        //     public void CaptureAndBakeData(float minOpacity)
        //     {
        //         // 1. Check whether the rendering resources are ready.
        //         if (!EnsureEditingBuffers()) 
        //         {
        //             Debug.LogError("[Bake] Rendering resources are not initialized. Enable the component in Play Mode or Edit Mode.");
        //             return;
        //         }

        //         // 2. Prepare the compute shader and buffers.
        //         int kernelIdx = m_CSSplatUtilities.FindKernel("CSCaptureData");
        //         if (kernelIdx < 0)
        //         {
        //             Debug.LogError("[Bake] Kernel 'CSCaptureData' was not found. Check SplatUtilities.compute.");
        //             return;
        //         }

        //         // Create the output buffer (count x stride).
        //         using var captureBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 28);

        //         // 3. Set parameters and dispatch the GPU work.
        //         using var cmb = new CommandBuffer { name = "CaptureData" };

        //         // Bind the input resources required for decoding.
        //         cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIdx, "_SplatPos", m_GpuPosData);
        //         cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIdx, "_SplatOther", m_GpuOtherData);
        //         cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIdx, "_SplatSH", m_GpuSHData);
        //         cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIdx, "_SplatChunks", m_GpuChunks);
        //         if(m_GpuColorData != null) 
        //             cmb.SetComputeTextureParam(m_CSSplatUtilities, kernelIdx, "_SplatColor", m_GpuColorData);

        //         // Bind the output buffer.
        //         cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIdx, "_CaptureBuffer", captureBuffer);

        //         // Set uniforms.
        //         uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
        //         cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)format);
        //         cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        //         cmb.SetComputeIntParam(m_CSSplatUtilities, "_SplatCount", m_SplatCount);

        //         // Execute.
        //         int groups = (m_SplatCount + 1023) / 1024;
        //         cmb.DispatchCompute(m_CSSplatUtilities, kernelIdx, groups, 1, 1);
        //         Graphics.ExecuteCommandBuffer(cmb);

        //         // 4. Read data back to the CPU synchronously.
        //         CapturedPoint[] rawData = new CapturedPoint[m_SplatCount];
        //         captureBuffer.GetData(rawData);

        //         // 5. Filter the data on the CPU.
        //         System.Collections.Generic.List<Vector3> validPos = new();
        //         System.Collections.Generic.List<Color> validCol = new();

        //         for(int i=0; i<rawData.Length; i++)
        //         {
        //             // rawData[i].color.w stores opacity.
        //             if(rawData[i].color.w >= minOpacity)
        //             {
        //                 validPos.Add(rawData[i].pos);
        //                 // Convert float4 to Color and set alpha to 1 for opaque display.
        //                 validCol.Add(new Color(rawData[i].color.x, rawData[i].color.y, rawData[i].color.z, 1f));
        //             }
        //         }

        // #if UNITY_EDITOR
        //         // 6. Create and save the ScriptableObject.
        //         // File output runs only in the Editor.

        //         // Instantiate the data container.
        //         GaussianSplatBakedData newData = ScriptableObject.CreateInstance<GaussianSplatBakedData>();
        //         newData.positions = validPos.ToArray();
        //         newData.colors = validCol.ToArray();

        //         // Save beside the source asset with the _Baked suffix.
        //         string originalPath = UnityEditor.AssetDatabase.GetAssetPath(m_Asset);
        //         if (string.IsNullOrEmpty(originalPath))
        //         {
        //             Debug.LogError("[Bake] Could not resolve the source asset path.");
        //             return;
        //         }

        //         string dir = System.IO.Path.GetDirectoryName(originalPath);
        //         string name = System.IO.Path.GetFileNameWithoutExtension(originalPath);
        //         string newPath = $"{dir}/{name}_Baked.asset";

        //         // Create the asset file.
        //         UnityEditor.AssetDatabase.CreateAsset(newData, newPath);
        //         UnityEditor.AssetDatabase.SaveAssets();

        //         // 7. Assign the new data to this renderer.
        //         this.m_BakedData = newData;
        //         UnityEditor.EditorUtility.SetDirty(this); // Mark the scene as modified.

        //         Debug.Log($"[Bake] Success. Source points: {m_SplatCount}; valid points: {validPos.Count}. Saved to: {newPath}");

        //         // Select the new file in the Project window.
        //         UnityEditor.Selection.activeObject = newData;
        // #endif
        //     }


        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }


        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ScaleSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.SelectAll, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.InvertSelection, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ExportData, "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (newSplatCount <= 0 || newSplatCount > GaussianSplatAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            int posStride = (int)(asset.posData.dataSize / asset.splatCount);
            int otherStride = (int)(asset.otherData.dataSize / asset.splatCount);
            int shStride = (int)(asset.shData.dataSize / asset.splatCount);

            // create new GPU buffers
            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            // selected/deleted buffers
            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) { name = "GaussianSplatSelected" };
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) { name = "GaussianSplatSelectedInit" };
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) { name = "GaussianSplatDeleted" };
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            var newGpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSplatCount, kGpuViewDataSize);
            InitSortBuffers(newSplatCount);

            // copy existing data over into new buffers
            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            // use the new buffers and the new splat count
            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            DestroyImmediate(m_GpuColorData);
            m_GpuView.Dispose();

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuView = newGpuView;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);

            m_SplatCount = newSplatCount;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            SetAssetDataOnCS(cmb, KernelIndices.CopySplats);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)kernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)kernel, (int)((count + gsX - 1) / gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        public GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;
    }
}
