/*// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public class GaussianCutout : MonoBehaviour
    {
        public enum Type
        {
            Ellipsoid,
            Box
        }

        // Cutout mode
        public enum CutoutMode
        {
            Discard,       // Traditional cutout: discard the selected interior or exterior completely (default)
            TransparentInner, // Transparent interior: fade inside the box and keep the exterior opaque
            TransparentOuter  // Transparent exterior: fade outside the box and keep the interior opaque
        }

        public Type m_Type = Type.Ellipsoid;
        public CutoutMode m_Mode = CutoutMode.Discard; // Traditional cutout by default
        // m_Invert affects Discard mode only; TransparentInner and TransparentOuter already define opposite effects.
        // Applying m_Invert uniformly to every mode would require more complex logic.
        // For simplicity, the transparent modes are assumed to include the required inversion semantics.
        // m_Invert is retained for compatibility with the original Discard behavior.
        public bool m_Invert = false;

        // Distance over which transparency fades
        [Range(0.0f, 1.0f)] // 0 gives a hard transition; 1 gives a smooth transition
        public float m_TransparencyFadeDistance = 0.1f;

        // Target opacity inside or outside the box, used only by transparent modes
        [Range(0.0f, 1.0f)] // 0 is fully transparent; 1 is fully opaque
        public float m_TargetTransparency = 0.0f; // Fully transparent by default

        public bool[] layersToCut = Array.Empty<bool>();

        // ShaderData must match the corresponding HLSL structure.
        public unsafe struct ShaderData
        {
            public Matrix4x4 matrix;
            public uint typeAndFlags; // Encodes Type and CutoutMode
            public fixed int cutIndices[8];
            public float transparencyFadeDistance; // Fade distance
            public float targetTransparency;       // Target opacity
        }

        public static ShaderData GetShaderData(GaussianCutout self, Matrix4x4 rendererMatrix, GaussianSplatAsset asset)
        {
            ShaderData sd = default;
            if (!(self && self.isActiveAndEnabled))
            {
                sd.typeAndFlags = ~0u; // Sentinel value to disable cutout
                return sd;
            }

            unsafe
            {
                for (int i = 0; i < 8; i++)
                {
                    sd.cutIndices[i] = -1;
                }
            }

            var tr = self.transform;
            sd.matrix = tr.worldToLocalMatrix * rendererMatrix;

            // Encode Type and CutoutMode into typeAndFlags.
            // The lowest 8 bits store Type (Ellipsoid/Box).
            // The next 8 bits store CutoutMode (Discard/TransparentInner/TransparentOuter).
            // The next bit stores m_Invert for Discard mode only.
            uint flags = (uint)self.m_Type;
            flags |= ((uint)self.m_Mode) << 8; // Store CutoutMode in the upper bits
            if (self.m_Mode == CutoutMode.Discard && self.m_Invert) // m_Invert applies only to Discard mode
            {
                flags |= 0x10000u; // Store the invert flag in the next bit
            }
            sd.typeAndFlags = flags;

            // Pass the transparency parameters.
            sd.transparencyFadeDistance = self.m_TransparencyFadeDistance;
            sd.targetTransparency = self.m_TargetTransparency;

            for (int layer = 0; layer < Math.Min(4, self.layersToCut.Length); layer++)
            {
                if (self.layersToCut[layer] && asset.layerInfo.TryGetValue(layer, out int count))
                {
                    int idxFrom = asset.layerInfo.Where(kv => kv.Key < layer).Sum(kv => kv.Value);
                    unsafe
                    {
                        sd.cutIndices[layer * 2] = idxFrom;
                        sd.cutIndices[layer * 2 + 1] = idxFrom + count;
                    }
                }
            }

            return sd;
        }

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            // The Gizmo matrix excludes softness scaling because the shader handles softness and fade distance.
            Gizmos.matrix = transform.localToWorldMatrix;

            var color = Color.magenta;
            color.a = 0.2f;

            if (Selection.Contains(gameObject))
                color.a = 0.9f;
            else
            {
                var activeGo = Selection.activeGameObject;
                if (activeGo != null)
                {
                    var activeSplat = activeGo.GetComponent<GaussianSplatRenderer>();
                    if (activeSplat != null)
                    {
                        if (activeSplat.m_Cutouts != null && activeSplat.m_Cutouts.Contains(this))
                            color.a = 0.5f;
                    }
                }
            }

            // Adjust the Gizmo color to distinguish the active mode.
            if (m_Mode == CutoutMode.TransparentInner || m_Mode == CutoutMode.TransparentOuter)
            {
                color = Color.cyan; // Cyan indicates a transparent mode
                // Draw a slightly larger frame to visualize the fade region.
                Gizmos.color = new Color(color.r, color.g, color.b, color.a * 0.5f);
                if (m_Type == Type.Ellipsoid)
                {
                    Gizmos.DrawWireSphere(Vector3.zero, 1.0f + m_TransparencyFadeDistance);
                    Gizmos.DrawWireSphere(Vector3.zero, 1.0f - m_TransparencyFadeDistance);
                }
                if (m_Type == Type.Box)
                {
                    Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2 * (1.0f + m_TransparencyFadeDistance));
                    Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2 * (1.0f - m_TransparencyFadeDistance));
                }
            }


            Gizmos.color = color;
            if (m_Type == Type.Ellipsoid)
            {
                Gizmos.DrawWireSphere(Vector3.zero, 1.0f);
            }
            if (m_Type == Type.Box)
            {
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2);
            }
        }
#endif // #if UNITY_EDITOR
    }
}*/


// SPDX-License-Identifier: MIT

using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public class GaussianCutout : MonoBehaviour
    {
        public enum Type
        {
            Ellipsoid,
            Box
        }

        public Type m_Type = Type.Ellipsoid;
        public bool m_Invert = false;

        public struct ShaderData // match GaussianCutoutShaderData in CS
        {
            public Matrix4x4 matrix;
            public uint typeAndFlags;
        }

        public static ShaderData GetShaderData(GaussianCutout self, Matrix4x4 rendererMatrix)
        {
            ShaderData sd = default;
            if (self && self.isActiveAndEnabled)
            {
                var tr = self.transform;
                sd.matrix = tr.worldToLocalMatrix * rendererMatrix;
                sd.typeAndFlags = ((uint)self.m_Type) | (self.m_Invert ? 0x100u : 0u);
            }
            else
            {
                sd.typeAndFlags = ~0u;
            }
            return sd;
        }

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            var color = Color.magenta;
            color.a = 0.2f;
            if (Selection.Contains(gameObject))
                color.a = 0.9f;
            else
            {
                // mid amount of alpha if a GS object that contains us as a cutout is selected
                var activeGo = Selection.activeGameObject;
                if (activeGo != null)
                {
                    var activeSplat = activeGo.GetComponent<GaussianSplatRenderer>();
                    if (activeSplat != null)
                    {
                        if (activeSplat.m_Cutouts != null && activeSplat.m_Cutouts.Contains(this))
                            color.a = 0.5f;
                    }
                }
            }

            Gizmos.color = color;
            if (m_Type == Type.Ellipsoid)
            {
                Gizmos.DrawWireSphere(Vector3.zero, 1.0f);
            }
            if (m_Type == Type.Box)
            {
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2);
            }
        }
#endif // #if UNITY_EDITOR
    }
}
