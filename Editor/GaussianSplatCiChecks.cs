#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Shaders compile lazily, so a clean C# batchmode run says nothing about them. Run this with
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod GaussianSplatting.Editor.GaussianSplatCiChecks.CheckShaders
    /// (without -nographics -- shader compilation needs a graphics device).
    /// </summary>
    public static class GaussianSplatCiChecks
    {
        const string SearchFolder = "Assets/VRChatGaussianSplatting";

        public static void CheckShaders()
        {
            string[] guids = AssetDatabase.FindAssets("t:Shader", new[] { SearchFolder });
            StringBuilder failures = new StringBuilder();
            int checkedCount = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null)
                {
                    continue;
                }

                checkedCount++;
                int messageCount = ShaderUtil.GetShaderMessageCount(shader);
                if (messageCount == 0)
                {
                    continue;
                }

                foreach (ShaderMessage message in ShaderUtil.GetShaderMessages(shader))
                {
                    // "Both vertex and fragment programs must be present in a shader snippet" is only
                    // a warning, but it means the snippet was thrown away and the shader renders
                    // nothing. Unity 6 raises it whenever the entry-point pragmas sit in an included
                    // file, which is exactly how these shaders were written for Unity 2022 -- so a
                    // check that only looked at Errors reported a clean bill of health for shaders
                    // that were, in fact, entirely dead. Treat it as fatal.
                    bool fatal = message.severity == ShaderCompilerMessageSeverity.Error
                        || message.message.Contains("must be present in a shader snippet");
                    if (!fatal)
                    {
                        continue;
                    }
                    failures.AppendLine("SHADER " + message.severity.ToString().ToUpperInvariant() + " " + path + " (" + message.file + ":" + message.line + "): " + message.message);
                }
            }

            Debug.Log("GaussianSplatCiChecks: inspected " + checkedCount + " shaders.");
            if (failures.Length > 0)
            {
                Debug.LogError("GaussianSplatCiChecks FAILED\n" + failures);
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log("GaussianSplatCiChecks: no shader errors.");
            EditorApplication.Exit(0);
        }

        // URP draws a pass only when its LightMode tag is one it recognises. A pass with no LightMode
        // tag counts as SRPDefaultUnlit and is drawn. A shader with no matching pass at all renders
        // magenta -- and Shader.isSupported does NOT catch this: the Standard shader compiles fine
        // and reports isSupported = true, it just has nothing URP will draw (ForwardBase/ForwardAdd).
        static readonly HashSet<string> UrpLightModes = new HashSet<string>
        {
            "", "SRPDefaultUnlit", "UniversalForward", "UniversalForwardOnly", "UniversalGBuffer", "Universal2D",
            "DepthOnly", "DepthNormals", "ShadowCaster", "Meta", "MotionVectors",
            // this package's own passes, drawn by GaussianSplatRendererFeature
            "GaussianSplat", "GaussianSplatToSRGB", "GaussianSplatToLinear", "GaussianSplatAlphaMask",
        };

        static bool UrpCanDraw(Shader shader)
        {
            // Deliberately not Shader.isSupported: that needs a graphics device, so it reports false
            // for everything under -batchmode and would fail this check on every material. The pass
            // metadata below is GPU-independent, and ShaderHasError covers the broken-shader case.
            if (shader == null || shader.name == "Hidden/InternalErrorShader" || ShaderUtil.ShaderHasError(shader))
            {
                return false;
            }
            for (int subshader = 0; subshader < shader.subshaderCount; subshader++)
            {
                for (int pass = 0; pass < shader.GetPassCountInSubshader(subshader); pass++)
                {
                    string lightMode = shader.FindPassTagValue(subshader, pass, new ShaderTagId("LightMode")).name ?? "";
                    if (UrpLightModes.Contains(lightMode))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Fails on any material in this package that URP cannot draw, i.e. that renders magenta.
        /// </summary>
        public static void VerifyMaterials()
        {
            bool failed = false;
            int checkedCount = 0;

            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { SearchFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    continue;
                }
                checkedCount++;
                if (!UrpCanDraw(material.shader))
                {
                    Debug.LogError("MATERIAL RENDERS MAGENTA: " + path + " uses '" + (material.shader != null ? material.shader.name : "NULL") + "', which URP has no pass for.");
                    failed = true;
                }
            }

            Debug.Log("GaussianSplatCiChecks: inspected " + checkedCount + " materials.");
            if (!failed)
            {
                Debug.Log("GaussianSplatCiChecks: no magenta materials.");
            }
            EditorApplication.Exit(failed ? 1 : 0);
        }

        /// <summary>
        /// Opens each example scene and reports whether it is actually playable: no missing scripts,
        /// a camera, an EventSystem with an input module that works under the Input System package,
        /// a PhysicsRaycaster for the click toggles, and UI buttons whose onClick actually points at
        /// something. Grepping the YAML cannot answer any of this -- components are stored by script
        /// GUID, not by name.
        /// </summary>
        public static void VerifyScenes()
        {
            string[] guids = AssetDatabase.FindAssets("t:Scene", new[] { SearchFolder });
            bool failed = false;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                UnityEngine.SceneManagement.Scene scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, UnityEditor.SceneManagement.OpenSceneMode.Single);

                int missingScripts = 0;
                int buttons = 0;
                int wiredButtons = 0;
                bool hasCamera = false;
                bool hasPhysicsRaycaster = false;
                bool hasEventSystem = false;
                bool hasInputModule = false;
                bool hasLegacyInputModule = false;
                int splatObjects = 0;
                bool hasRenderer = false;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                    }

                    hasCamera |= root.GetComponentInChildren<Camera>(true) != null;
                    hasPhysicsRaycaster |= root.GetComponentInChildren<UnityEngine.EventSystems.PhysicsRaycaster>(true) != null;
                    hasEventSystem |= root.GetComponentInChildren<UnityEngine.EventSystems.EventSystem>(true) != null;
                    hasInputModule |= root.GetComponentInChildren<UnityEngine.InputSystem.UI.InputSystemUIInputModule>(true) != null;
                    hasLegacyInputModule |= root.GetComponentInChildren<UnityEngine.EventSystems.StandaloneInputModule>(true) != null;
                    hasRenderer |= root.GetComponentInChildren<GaussianSplatRenderer>(true) != null;
                    splatObjects += root.GetComponentsInChildren<GaussianSplatObject>(true).Length;

                    foreach (UnityEngine.UI.Button button in root.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                    {
                        buttons++;
                        for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                        {
                            if (button.onClick.GetPersistentTarget(i) != null && !string.IsNullOrEmpty(button.onClick.GetPersistentMethodName(i)))
                            {
                                wiredButtons++;
                                break;
                            }
                        }
                    }
                }

                Debug.Log(
                    "SCENE " + path +
                    "\n  missing scripts: " + missingScripts +
                    "\n  camera: " + hasCamera + ", PhysicsRaycaster: " + hasPhysicsRaycaster +
                    "\n  EventSystem: " + hasEventSystem + ", InputSystemUIInputModule: " + hasInputModule + ", legacy StandaloneInputModule: " + hasLegacyInputModule +
                    "\n  GaussianSplatRenderer: " + hasRenderer + ", GaussianSplatObjects: " + splatObjects +
                    "\n  buttons: " + buttons + " (wired onClick: " + wiredButtons + ")");

                if (missingScripts > 0) { Debug.LogError("FAIL " + path + ": " + missingScripts + " missing scripts"); failed = true; }
                if (!hasCamera) { Debug.LogError("FAIL " + path + ": no camera"); failed = true; }
                if (!hasEventSystem || !hasInputModule) { Debug.LogError("FAIL " + path + ": EventSystem/InputSystemUIInputModule missing"); failed = true; }
                if (hasLegacyInputModule) { Debug.LogError("FAIL " + path + ": StandaloneInputModule throws under the Input System package"); failed = true; }
                if (!hasPhysicsRaycaster) { Debug.LogError("FAIL " + path + ": no PhysicsRaycaster, 3D click toggles cannot fire"); failed = true; }
                if (!hasRenderer) { Debug.LogError("FAIL " + path + ": no GaussianSplatRenderer"); failed = true; }
                if (buttons > 0 && wiredButtons != buttons) { Debug.LogError("FAIL " + path + ": " + (buttons - wiredButtons) + " of " + buttons + " buttons have no onClick target"); failed = true; }
            }

            EditorApplication.Exit(failed ? 1 : 0);
        }
    }
}
#endif
