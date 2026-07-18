using System;
using System.Reflection;
using System.Linq;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace Appletea.Dev.PointCloud
{
    /// <summary>
    /// Runtime probe for Meta Quest Link Environment Depth issues.
    /// Attach this to any active object in the scene and inspect Unity Console logs.
    /// </summary>
    public sealed class LinkDepthDiagnostics : MonoBehaviour
    {
        [SerializeField] private Camera sourceCamera;
        [SerializeField] private EnvironmentDepthManager depthManager;
        [SerializeField] private EnvironmentRaycastManager raycastManager;
        [SerializeField] private float maxDistance = 5f;
        [SerializeField] private float reportInterval = 1f;
        [SerializeField] private bool logEnabledExtensions;

        private static readonly int EnvironmentDepthTextureId = Shader.PropertyToID("_EnvironmentDepthTexture");
        private static readonly int EnvironmentDepthZBufferParamsId = Shader.PropertyToID("_EnvironmentDepthZBufferParams");
        private float nextReportTime;

        private void Awake()
        {
            if (sourceCamera == null)
            {
                sourceCamera = Camera.main;
            }

            if (depthManager == null)
            {
                depthManager = FindAnyObjectByType<EnvironmentDepthManager>(FindObjectsInactive.Include);
            }

            if (raycastManager == null)
            {
                raycastManager = FindAnyObjectByType<EnvironmentRaycastManager>(FindObjectsInactive.Include);
            }
        }

        private void Start()
        {
            LogStaticRuntimeState();
        }

        private void Update()
        {
            if (Time.unscaledTime < nextReportTime)
            {
                return;
            }

            nextReportTime = Time.unscaledTime + Mathf.Max(0.1f, reportInterval);
            LogFrameState();
        }

        private void LogStaticRuntimeState()
        {
            var loader = XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager != null
                ? XRGeneralSettings.Instance.Manager.activeLoader
                : null;

            Debug.Log(
                "[LinkDepthDiagnostics] Runtime\n" +
                $"  Unity={Application.unityVersion}, platform={Application.platform}, isEditor={Application.isEditor}\n" +
                $"  XR loader={(loader != null ? loader.GetType().FullName : "null")}\n" +
                $"  OpenXR runtime={Safe(() => OpenXRRuntime.name)} {Safe(() => OpenXRRuntime.version)}, api={Safe(() => OpenXRRuntime.apiVersion)}, plugin={Safe(() => OpenXRRuntime.pluginVersion)}\n" +
                $"  XR_META_environment_depth enabled={IsOpenXRExtensionEnabled("XR_META_environment_depth")}, version={Safe(() => OpenXRRuntime.GetExtensionVersion("XR_META_environment_depth").ToString())}\n" +
                $"  XR_META_environment_raycast enabled={IsOpenXRExtensionEnabled("XR_META_environment_raycast")}, version={Safe(() => OpenXRRuntime.GetExtensionVersion("XR_META_environment_raycast").ToString())}\n" +
                $"  XR_FB_scene_capture enabled={IsOpenXRExtensionEnabled("XR_FB_scene_capture")}, version={Safe(() => OpenXRRuntime.GetExtensionVersion("XR_FB_scene_capture").ToString())}\n" +
                $"  USE_SCENE permission={Safe(() => Permission.HasUserAuthorizedPermission("com.oculus.permission.USE_SCENE").ToString())}\n" +
                $"  OVRPlugin initialized={OVRPlugin.initialized}, headset={Safe(() => OVRPlugin.GetSystemHeadsetType().ToString())}, version={Safe(() => OVRPlugin.version.ToString())}");

            if (logEnabledExtensions)
            {
                Debug.Log("[LinkDepthDiagnostics] Enabled OpenXR extensions:\n  " +
                          string.Join("\n  ", Safe(() => OpenXRRuntime.GetEnabledExtensions(), Array.Empty<string>())));
            }
        }

        private void LogFrameState()
        {
            Texture depthTexture = Shader.GetGlobalTexture(EnvironmentDepthTextureId);
            string textureState = depthTexture == null
                ? "null"
                : $"{depthTexture.GetType().Name} {depthTexture.width}x{depthTexture.height}, dimension={depthTexture.dimension}, format={DescribeGraphicsFormat(depthTexture)}, updateCount={depthTexture.updateCount}, nativePtr=0x{depthTexture.GetNativeTexturePtr().ToInt64():X}";

            string raycastState = raycastManager == null || sourceCamera == null
                ? $"skipped camera={(sourceCamera != null)}, raycastManager={(raycastManager != null)}"
                : ProbeRaycasts();

            Debug.Log(
                "[LinkDepthDiagnostics] Frame\n" +
                $"  EnvironmentDepthManager present={depthManager != null}, enabled={(depthManager != null && depthManager.enabled)}, supported={Safe(() => EnvironmentDepthManager.IsSupported.ToString())}, available={(depthManager != null && depthManager.IsDepthAvailable)}\n" +
                $"  EnvironmentRaycastManager present={raycastManager != null}, enabled={(raycastManager != null && raycastManager.enabled)}, supported={Safe(() => EnvironmentRaycastManager.IsSupported.ToString())}\n" +
                $"  camera={(sourceCamera != null ? $"{sourceCamera.name}, pos={sourceCamera.transform.position.ToString("F3")}, fwd={sourceCamera.transform.forward.ToString("F3")}" : "null")}\n" +
                $"  zBufferParams={Shader.GetGlobalVector(EnvironmentDepthZBufferParamsId).ToString("F6")}\n" +
                $"  depthTexture={textureState}\n" +
                $"  depthRaycaster={ProbeDepthRaycasterPixels()}\n" +
                $"  raycasts={raycastState}");
        }

        private string ProbeRaycasts()
        {
            var samples = new[]
            {
                new Vector2(0.5f, 0.5f),
                new Vector2(0.35f, 0.5f),
                new Vector2(0.65f, 0.5f),
                new Vector2(0.5f, 0.35f),
                new Vector2(0.5f, 0.65f),
            };

            var hits = samples.Select(sample =>
            {
                var ray = sourceCamera.ViewportPointToRay(new Vector3(sample.x, sample.y, 0f));
                bool success = raycastManager.Raycast(ray, out EnvironmentRaycastHit hit, maxDistance);
                float distance = Vector3.Distance(sourceCamera.transform.position, hit.point);
                return new
                {
                    sample,
                    success,
                    hit.status,
                    hit.point,
                    distance,
                    hit.normalConfidence
                };
            }).ToArray();

            string summary = string.Join(", ",
                hits.GroupBy(hit => hit.status)
                    .Select(group => $"{group.Key}:{group.Count()}"));

            string details = string.Join(" | ", hits.Select(hit =>
                $"{hit.sample}: ok={hit.success}, status={hit.status}, dist={hit.distance:F3}, point={hit.point.ToString("F3")}, nconf={hit.normalConfidence:F2}"));

            return $"{summary}; {details}";
        }

        private string ProbeDepthRaycasterPixels()
        {
            if (depthManager == null)
            {
                return "skipped no depthManager";
            }

            Component raycaster = depthManager.GetComponents<Component>()
                .FirstOrDefault(component => component != null &&
                                             component.GetType().FullName == "Meta.XR.EnvironmentDepthRaycaster");
            if (raycaster == null)
            {
                return "not-created-yet";
            }

            Type raycasterType = raycaster.GetType();
            bool available = Safe(() => (bool)raycasterType
                .GetField("_isDepthTextureAvailable", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(raycaster), false);

            object pixelsObject = raycasterType
                .GetField("_depthTexturePixels", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(raycaster);

            if (pixelsObject is not NativeArray<float> pixels || !pixels.IsCreated || pixels.Length == 0)
            {
                return $"available={available}, pixels=unavailable";
            }

            int nonZero = 0;
            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;
            double sum = 0;
            int finite = 0;

            for (int i = 0; i < pixels.Length; i++)
            {
                float value = pixels[i];
                if (!float.IsFinite(value))
                {
                    continue;
                }

                finite++;
                if (value != 0f)
                {
                    nonZero++;
                }

                min = Mathf.Min(min, value);
                max = Mathf.Max(max, value);
                sum += value;
            }

            int textureSize = Mathf.RoundToInt(Mathf.Sqrt(pixels.Length / 2f));
            int leftCenter = textureSize / 2 + textureSize / 2 * textureSize;
            int rightCenter = leftCenter + textureSize * textureSize;

            string center = leftCenter >= 0 && rightCenter < pixels.Length
                ? $"leftCenter={pixels[leftCenter]:F6}, rightCenter={pixels[rightCenter]:F6}"
                : "center=unavailable";

            string range = finite > 0
                ? $"min={min:F6}, max={max:F6}, mean={(sum / finite):F6}"
                : "no-finite-values";

            return $"available={available}, pixels={pixels.Length}, nonZero={nonZero}, {range}, {center}";
        }

        private static string DescribeGraphicsFormat(Texture texture)
        {
            return texture is RenderTexture renderTexture
                ? $"{renderTexture.graphicsFormat}/{renderTexture.depthStencilFormat}"
                : texture.graphicsFormat.ToString();
        }

        private static bool IsOpenXRExtensionEnabled(string extension)
        {
            return Safe(() => OpenXRRuntime.IsExtensionEnabled(extension), false);
        }

        private static string Safe(Func<string> value)
        {
            return Safe(value, "<error>");
        }

        private static T Safe<T>(Func<T> value, T fallback)
        {
            try
            {
                return value();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[LinkDepthDiagnostics] Probe failed: {exception.GetType().Name}: {exception.Message}");
                return fallback;
            }
        }
    }
}
