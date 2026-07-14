#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// The splats need three things from the project that this repository cannot ship, because the
    /// URP assets and ProjectSettings live outside it:
    ///
    ///  1. GaussianSplatRendererFeature registered on every URP renderer. Without it, nothing draws.
    ///  2. An alpha channel in the camera colour target. URP's default (HDR on, 32-bit) selects
    ///     B10G11R11_UFloatPack32, which has no alpha -- and the splats composite with
    ///     Blend OneMinusDstAlpha One and stencil out fully-covered pixels by testing destination
    ///     alpha. With no alpha the result is wrong, with no error and no warning. This is the one
    ///     that will waste your afternoon.
    ///  3. Render Graph. The feature is a Render Graph pass; compatibility mode never runs it.
    ///
    /// MSAA is also forced off: the feature reads the camera colour target mid-frame, and the splat
    /// shaders were never designed to rely on MSAA anyway.
    /// </summary>
    public static class GaussianSplatUrpSetup
    {
        public class Issue
        {
            public string Message;
            public bool Fixable;
        }

        [MenuItem("Gaussian Splatting/Setup URP Renderer")]
        static void SetupMenuItem()
        {
            List<Issue> issues = Validate();
            if (issues.Count == 0)
            {
                Debug.Log("Gaussian Splatting: URP setup is already correct.");
                return;
            }

            Fix();

            List<Issue> remaining = Validate();
            if (remaining.Count == 0)
            {
                Debug.Log("Gaussian Splatting: URP setup complete.");
                return;
            }

            foreach (Issue issue in remaining)
            {
                Debug.LogError("Gaussian Splatting: " + issue.Message);
            }
        }

        public static List<Issue> Validate()
        {
            List<Issue> issues = new List<Issue>();

            List<UniversalRenderPipelineAsset> assets = CollectUrpAssets();
            if (assets.Count == 0)
            {
                issues.Add(new Issue
                {
                    Message = "No Universal Render Pipeline asset is active. Assign one under Project Settings > Graphics.",
                    Fixable = false,
                });
                return issues;
            }

            if (IsRenderCompatibilityModeEnabled())
            {
                issues.Add(new Issue
                {
                    Message = "Render Graph is disabled (Compatibility Mode). Gaussian splats will not draw at all. Turn it off under Project Settings > Graphics > Render Graph.",
                    Fixable = true,
                });
            }

            foreach (UniversalRenderPipelineAsset asset in assets)
            {
                foreach (ScriptableRendererData rendererData in asset.rendererDataList)
                {
                    if (rendererData != null && FindFeature(rendererData) == null)
                    {
                        issues.Add(new Issue
                        {
                            Message = "'" + rendererData.name + "' has no GaussianSplatRendererFeature, so no splats will be drawn.",
                            Fixable = true,
                        });
                    }
                }

                if (!HasAlphaChannel(asset))
                {
                    issues.Add(new Issue
                    {
                        Message = "'" + asset.name + "' renders HDR at 32-bit precision, which selects a colour format with no alpha channel. Splat compositing depends on destination alpha and will be wrong (silently). Disable HDR, or set HDR Precision to 64 bits.",
                        Fixable = true,
                    });
                }

                if (asset.msaaSampleCount > 1)
                {
                    issues.Add(new Issue
                    {
                        Message = "'" + asset.name + "' has MSAA enabled. The splat pass reads the colour target mid-frame and does not benefit from MSAA.",
                        Fixable = true,
                    });
                }
            }

            return issues;
        }

        public static void Fix()
        {
            if (IsRenderCompatibilityModeEnabled())
            {
                SetRenderCompatibilityMode(false);
            }

            foreach (UniversalRenderPipelineAsset asset in CollectUrpAssets())
            {
                foreach (ScriptableRendererData rendererData in asset.rendererDataList)
                {
                    if (rendererData != null && FindFeature(rendererData) == null)
                    {
                        AddFeature(rendererData);
                    }
                }

                SerializedObject assetObject = new SerializedObject(asset);
                bool changed = false;

                if (!HasAlphaChannel(asset))
                {
                    // Disabling HDR gives R8G8B8A8_SRGB, matching what the shaders were written
                    // against under the built-in pipeline. 64-bit HDR also carries alpha, but it
                    // changes how the sRGB round trip in ToSRGB/ToLinear behaves, so prefer LDR.
                    assetObject.FindProperty("m_SupportsHDR").boolValue = false;
                    changed = true;
                }

                if (asset.msaaSampleCount > 1)
                {
                    assetObject.FindProperty("m_MSAA").intValue = 1;
                    changed = true;
                }

                if (changed)
                {
                    assetObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(asset);
                }
            }

            AssetDatabase.SaveAssets();
        }

        static bool HasAlphaChannel(UniversalRenderPipelineAsset asset)
        {
            if (!asset.supportsHDR)
            {
                return true;
            }
            if (asset.hdrColorBufferPrecision == HDRColorBufferPrecision._64Bits)
            {
                return true;
            }
            return PlayerSettings.preserveFramebufferAlpha;
        }

        static List<UniversalRenderPipelineAsset> CollectUrpAssets()
        {
            List<UniversalRenderPipelineAsset> assets = new List<UniversalRenderPipelineAsset>();

            void Add(RenderPipelineAsset candidate)
            {
                UniversalRenderPipelineAsset urp = candidate as UniversalRenderPipelineAsset;
                if (urp != null && !assets.Contains(urp))
                {
                    assets.Add(urp);
                }
            }

            Add(GraphicsSettings.defaultRenderPipeline);
            for (int i = 0; i < QualitySettings.count; i++)
            {
                Add(QualitySettings.GetRenderPipelineAssetAt(i));
            }

            return assets;
        }

        static ScriptableRendererFeature FindFeature(ScriptableRendererData rendererData)
        {
            foreach (ScriptableRendererFeature feature in rendererData.rendererFeatures)
            {
                if (feature is GaussianSplatRendererFeature)
                {
                    return feature;
                }
            }
            return null;
        }

        // Mirrors what URP's own ScriptableRendererDataEditor.AddComponent does: the feature is a
        // sub-asset of the renderer data, and m_RendererFeatureMap has to stay index-aligned with
        // m_RendererFeatures or URP will drop the reference on the next reload.
        static void AddFeature(ScriptableRendererData rendererData)
        {
            GaussianSplatRendererFeature feature = ScriptableObject.CreateInstance<GaussianSplatRendererFeature>();
            feature.name = "GaussianSplatRendererFeature";
            Undo.RegisterCreatedObjectUndo(feature, "Add Gaussian Splat Renderer Feature");

            if (EditorUtility.IsPersistent(rendererData))
            {
                AssetDatabase.AddObjectToAsset(feature, rendererData);
            }
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long localId);

            SerializedObject serializedData = new SerializedObject(rendererData);
            SerializedProperty features = serializedData.FindProperty("m_RendererFeatures");
            SerializedProperty featureMap = serializedData.FindProperty("m_RendererFeatureMap");

            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;

            featureMap.arraySize++;
            featureMap.GetArrayElementAtIndex(featureMap.arraySize - 1).longValue = localId;

            serializedData.ApplyModifiedProperties();
            EditorUtility.SetDirty(rendererData);
        }

        static bool IsRenderCompatibilityModeEnabled()
        {
            return GraphicsSettings.TryGetRenderPipelineSettings(out RenderGraphSettings settings)
                && settings.enableRenderCompatibilityMode;
        }

        static void SetRenderCompatibilityMode(bool enabled)
        {
            if (!GraphicsSettings.TryGetRenderPipelineSettings(out RenderGraphSettings settings))
            {
                return;
            }
            settings.enableRenderCompatibilityMode = enabled;
        }
    }
}
#endif
