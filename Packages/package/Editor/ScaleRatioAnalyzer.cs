using UnityEngine;
using UnityEditor;
using GaussianSplatting.Runtime;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Analyze the Scale Ratio distribution in a 3DGS asset.
    /// Scale Ratio = minScale / maxScale (0=needle, 1=sphere)
    /// </summary>
    public class ScaleRatioAnalyzer : EditorWindow
    {
        private GaussianSplatRenderer targetRenderer;
        private string analysisResult = "";
        private Vector2 scrollPos;
        
        // Histogram data
        private int[] histogram = new int[10];  // 0-0.1, 0.1-0.2, ..., 0.9-1.0
        private int totalCount;
        private float minRatio, maxRatio, avgRatio, medianRatio;
        
        [MenuItem("Tools/3DGS/Scale Ratio Analyzer")]
        public static void ShowWindow()
        {
            GetWindow<ScaleRatioAnalyzer>("Scale Ratio Analyzer");
        }
        
        void OnGUI()
        {
            GUILayout.Label("Scale Ratio Distribution Analysis", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            
            targetRenderer = (GaussianSplatRenderer)EditorGUILayout.ObjectField(
                "Target GaussianSplatRenderer",
                targetRenderer, 
                typeof(GaussianSplatRenderer), 
                true);
            
            EditorGUILayout.Space();
            
            EditorGUI.BeginDisabledGroup(targetRenderer == null);
            if (GUILayout.Button("Analyze Scale Ratio Distribution", GUILayout.Height(30)))
            {
                AnalyzeScaleRatios();
            }
            EditorGUI.EndDisabledGroup();
            
            EditorGUILayout.Space();
            
            // Display the histogram.
            if (totalCount > 0)
            {
                GUILayout.Label("Histogram", EditorStyles.boldLabel);
                DrawHistogram();
                
                EditorGUILayout.Space();
                GUILayout.Label("Statistics", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Total Points", totalCount.ToString("N0"));
                EditorGUILayout.LabelField("Minimum Ratio", minRatio.ToString("F4"));
                EditorGUILayout.LabelField("Maximum Ratio", maxRatio.ToString("F4"));
                EditorGUILayout.LabelField("Average Ratio", avgRatio.ToString("F4"));
                EditorGUILayout.LabelField("Median Ratio", medianRatio.ToString("F4"));
            }
            
            EditorGUILayout.Space();
            
            // Detailed results
            if (!string.IsNullOrEmpty(analysisResult))
            {
                GUILayout.Label("Detailed Analysis", EditorStyles.boldLabel);
                scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.Height(200));
                EditorGUILayout.TextArea(analysisResult, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
                
                if (GUILayout.Button("Copy to Clipboard"))
                {
                    GUIUtility.systemCopyBuffer = analysisResult;
                    Debug.Log("Analysis results copied to the clipboard.");
                }
            }
        }
        
        void DrawHistogram()
        {
            if (histogram == null || totalCount == 0) return;
            
            float maxHeight = 100f;
            int maxBin = histogram.Max();
            
            Rect rect = GUILayoutUtility.GetRect(position.width - 40, maxHeight + 30);
            float binWidth = rect.width / 10f;
            
            // Draw the background.
            EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));
            
            for (int i = 0; i < 10; i++)
            {
                float barHeight = maxBin > 0 ? (histogram[i] / (float)maxBin) * maxHeight : 0;
                
                // Draw the bar.
                Rect barRect = new Rect(
                    rect.x + i * binWidth + 2, 
                    rect.y + maxHeight - barHeight, 
                    binWidth - 4, 
                    barHeight);
                
                // Color gradient: red (flat) to green (spherical)
                Color barColor = Color.Lerp(new Color(0.8f, 0.2f, 0.2f), new Color(0.2f, 0.8f, 0.2f), i / 9f);
                EditorGUI.DrawRect(barRect, barColor);
                
                // Label
                string label = $"{i * 0.1f:F1}";
                GUI.Label(new Rect(rect.x + i * binWidth, rect.y + maxHeight + 5, binWidth, 20), label, EditorStyles.miniLabel);
            }
        }
        
        void AnalyzeScaleRatios()
        {
            if (targetRenderer == null || !targetRenderer.HasValidAsset)
            {
                analysisResult = "Error: Invalid GaussianSplatRenderer.";
                return;
            }
            
            EditorUtility.DisplayProgressBar("Analyzing", "Reading GPU data...", 0.3f);
            
            try
            {
                // Read the source data.
                var ratios = ReadScaleRatios(targetRenderer);
                if (ratios == null || ratios.Length == 0)
                {
                    analysisResult = "Error: Unable to read scale data.";
                    EditorUtility.ClearProgressBar();
                    return;
                }
                
                EditorUtility.DisplayProgressBar("Analyzing", "Calculating statistics...", 0.7f);
                
                // Calculate statistics.
                totalCount = ratios.Length;
                minRatio = ratios.Min();
                maxRatio = ratios.Max();
                avgRatio = ratios.Average();
                
                // Median
                var sorted = ratios.OrderBy(x => x).ToArray();
                medianRatio = sorted[sorted.Length / 2];
                
                // Histogram
                histogram = new int[10];
                foreach (var r in ratios)
                {
                    int bin = Mathf.Clamp(Mathf.FloorToInt(r * 10), 0, 9);
                    histogram[bin]++;
                }
                
                // Percentiles
                float p10 = sorted[(int)(sorted.Length * 0.1f)];
                float p25 = sorted[(int)(sorted.Length * 0.25f)];
                float p50 = sorted[(int)(sorted.Length * 0.5f)];
                float p75 = sorted[(int)(sorted.Length * 0.75f)];
                float p90 = sorted[(int)(sorted.Length * 0.9f)];
                
                // Analyze the effect of filtering.
                int belowThreshold01 = ratios.Count(r => r < 0.1f);
                int belowThreshold02 = ratios.Count(r => r < 0.2f);
                int belowThreshold03 = ratios.Count(r => r < 0.3f);
                int belowThreshold05 = ratios.Count(r => r < 0.5f);
                
                // Generate the report.
                var sb = new StringBuilder();
                sb.AppendLine($"========== Scale Ratio Analysis Report ==========");
                sb.AppendLine($"Asset: {targetRenderer.asset.name}");
                sb.AppendLine($"Total Gaussians: {totalCount:N0}");
                sb.AppendLine();
                sb.AppendLine("--- Basic Statistics ---");
                sb.AppendLine($"Minimum: {minRatio:F4}");
                sb.AppendLine($"Maximum: {maxRatio:F4}");
                sb.AppendLine($"Average: {avgRatio:F4}");
                sb.AppendLine($"Median: {medianRatio:F4}");
                sb.AppendLine();
                sb.AppendLine("--- Percentiles ---");
                sb.AppendLine($"P10: {p10:F4}");
                sb.AppendLine($"P25: {p25:F4}");
                sb.AppendLine($"P50: {p50:F4}");
                sb.AppendLine($"P75: {p75:F4}");
                sb.AppendLine($"P90: {p90:F4}");
                sb.AppendLine();
                sb.AppendLine("--- Histogram ---");
                for (int i = 0; i < 10; i++)
                {
                    float pct = (histogram[i] / (float)totalCount) * 100f;
                    sb.AppendLine($"[{i * 0.1f:F1} - {(i + 1) * 0.1f:F1}): {histogram[i]:N0} ({pct:F1}%)");
                }
                sb.AppendLine();
                sb.AppendLine("--- Estimated Filtering Impact ---");
                sb.AppendLine($"MaxFlatness=0.1 retains: {belowThreshold01:N0} ({(belowThreshold01 * 100f / totalCount):F1}%)");
                sb.AppendLine($"MaxFlatness=0.2 retains: {belowThreshold02:N0} ({(belowThreshold02 * 100f / totalCount):F1}%)");
                sb.AppendLine($"MaxFlatness=0.3 retains: {belowThreshold03:N0} ({(belowThreshold03 * 100f / totalCount):F1}%)");
                sb.AppendLine($"MaxFlatness=0.5 retains: {belowThreshold05:N0} ({(belowThreshold05 * 100f / totalCount):F1}%)");
                
                analysisResult = sb.ToString();
                Debug.Log(analysisResult);
                
                EditorUtility.ClearProgressBar();
            }
            catch (System.Exception e)
            {
                EditorUtility.ClearProgressBar();
                analysisResult = $"Error: {e.Message}";
                Debug.LogError(e);
            }
        }
        
        float[] ReadScaleRatios(GaussianSplatRenderer gsRenderer)
        {
            // Validate splatCount.
            int count = gsRenderer.splatCount;
            if (count <= 0)
            {
                Debug.LogWarning($"[ScaleRatioAnalyzer] splatCount is {count}. Asset may not be loaded. Try selecting the object in scene first.");
                return null;
            }
            
            // Export the complete data through EditExportData.
            // InputSplatData struct size: 62 floats * 4 bytes = 248 bytes
            int kSplatSize = 248;
            using var gpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, kSplatSize);
            
            if (!gsRenderer.EditExportData(gpuData, false))
            {
                Debug.LogWarning("[ScaleRatioAnalyzer] EditExportData failed");
                return null;
            }
            
            // Read the raw bytes.
            float[] rawFloats = new float[count * 62];
            gpuData.GetData(rawFloats);
            
            // Extract scale ratios.
            var ratios = new List<float>();
            for (int i = 0; i < gsRenderer.splatCount; i++)
            {
                int offset = i * 62;
                
                // Scale at index 55, 56, 57 (raw log-scale)
                float sx = Mathf.Abs(Mathf.Exp(rawFloats[offset + 55]));
                float sy = Mathf.Abs(Mathf.Exp(rawFloats[offset + 56]));
                float sz = Mathf.Abs(Mathf.Exp(rawFloats[offset + 57]));
                
                float minS = Mathf.Min(sx, Mathf.Min(sy, sz));
                float maxS = Mathf.Max(sx, Mathf.Max(sy, sz));
                
                float ratio = maxS > 1e-6f ? (minS / maxS) : 1f;
                ratios.Add(ratio);
            }
            
            return ratios.ToArray();
        }
    }
}
