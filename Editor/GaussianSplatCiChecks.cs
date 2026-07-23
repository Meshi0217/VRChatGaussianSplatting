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

        /// <summary>
        /// Play-mode smoke test: enters play mode on the example scene, renders the main camera
        /// off-screen through URP with the compute sort, verifies the blit scratch RTs were never
        /// created (the lazy-allocation path), then re-renders with the compute shader hidden so
        /// the blit fallback runs, and requires the two rendered frames to be byte-identical.
        /// Needs a graphics device (run without -nographics, without -quit).
        /// </summary>
        public static void VerifyPlayModeSmoke()
        {
            try
            {
                if (!SystemInfo.supportsComputeShaders)
                {
                    throw new System.Exception("compute shaders are unsupported on this editor platform; the smoke test cannot run.");
                }
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(SearchFolder + "/Example Scene.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
                // Keep this state machine's statics alive across the play-mode transition.
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                EditorApplication.playModeStateChanged += OnSmokePlayModeChanged;
                EditorApplication.EnterPlaymode();
            }
            catch (System.Exception e)
            {
                Debug.LogError("GaussianSplatCiChecks FAILED\n" + e);
                EditorApplication.Exit(1);
            }
        }

        const int SmokeWarmupTicks = 30;
        static int s_smokeTicks;
        static RenderTexture s_smokeTarget;

        static void OnSmokePlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode)
            {
                return;
            }
            s_smokeTicks = 0;
            EditorApplication.update += SmokeTick;
        }

        static void SmokeTick()
        {
            try
            {
                s_smokeTicks++;
                if (s_smokeTicks == 1)
                {
                    // The in-world UI fades in over time; keep the compared frames time-invariant.
                    foreach (Canvas canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    {
                        canvas.enabled = false;
                    }
                    s_smokeTarget = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGBFloat);
                    s_smokeTarget.Create();
                }
                Camera camera = Camera.main;
                if (camera == null)
                {
                    throw new System.Exception("the example scene has no main camera in play mode.");
                }
                // Warmup renders let the startup suppression window (8 frames) and the RT-pool
                // bucket debounce (0.2 s) settle before the compared frames.
                RenderCameraTo(camera, s_smokeTarget);
                if (s_smokeTicks < SmokeWarmupTicks)
                {
                    return;
                }
                EditorApplication.update -= SmokeTick;
                RunSmokeChecks(camera);
                Debug.Log("GaussianSplatCiChecks: play-mode smoke test passed.");
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                EditorApplication.update -= SmokeTick;
                Debug.LogError("GaussianSplatCiChecks FAILED\n" + e);
                EditorApplication.Exit(1);
            }
        }

        static void RenderCameraTo(Camera camera, RenderTexture target)
        {
            UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest request =
                new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest { destination = target };
            if (!RenderPipeline.SupportsRenderRequest(camera, request))
            {
                throw new System.Exception("URP rejected the off-screen render request.");
            }
            RenderPipeline.SubmitRenderRequest(camera, request);
        }

        static void RunSmokeChecks(Camera camera)
        {
            GaussianSplatRenderer renderer = Object.FindFirstObjectByType<GaussianSplatRenderer>();
            RadixSort radixSort = Object.FindFirstObjectByType<RadixSort>();
            if (renderer == null || radixSort == null)
            {
                throw new System.Exception("play mode has no GaussianSplatRenderer or RadixSort.");
            }
            if (!radixSort.ComputeSortAvailable())
            {
                throw new System.Exception("the compute sort is unavailable in play mode (missing serialized assets?).");
            }
            if (renderer.splatRenderOrder == null || !renderer.splatRenderOrder.IsCreated())
            {
                throw new System.Exception("the render-order texture was never created, so no sort ran during warmup.");
            }

            // All warmup frames used the compute sort: the blit scratch RTs must never have
            // been allocated (this is the lazy-allocation change under test).
            RenderTexture[] scratch = { radixSort.keyValues0, radixSort.keyValues1, radixSort.histograms, radixSort.prefixSums };
            foreach (RenderTexture rt in scratch)
            {
                if (rt != null && rt.IsCreated())
                {
                    throw new System.Exception("blit scratch RT '" + rt.name + "' was created while the compute sort was active.");
                }
            }

            RenderCameraTo(camera, s_smokeTarget);
            byte[] computeFrame = ReadRenderTexture(s_smokeTarget);
            RequireVisibleContent("compute-sorted frame", computeFrame);
            // Library/ survives the editor exit (Unity wipes Temp/ on shutdown) and is git-ignored.
            SaveFramePng(s_smokeTarget, "Library/gs_ci_smoke.png");

            ComputeShader savedCompute = radixSort.radixSortCompute;
            radixSort.radixSortCompute = null;
            try
            {
                RenderCameraTo(camera, s_smokeTarget);
            }
            finally
            {
                radixSort.radixSortCompute = savedCompute;
            }
            byte[] blitFrame = ReadRenderTexture(s_smokeTarget);

            bool anyCreated = false;
            foreach (RenderTexture rt in scratch)
            {
                anyCreated |= rt != null && rt.IsCreated();
            }
            if (!anyCreated)
            {
                throw new System.Exception("the blit fallback frame did not create any scratch RT, so the fallback cannot have sorted.");
            }

            if (computeFrame.Length != blitFrame.Length)
            {
                throw new System.Exception("frame readbacks differ in size.");
            }
            int mismatched = 0;
            for (int i = 0; i < computeFrame.Length; i++)
            {
                if (computeFrame[i] != blitFrame[i]) mismatched++;
            }
            if (mismatched > 0)
            {
                throw new System.Exception("compute-sorted and blit-sorted frames differ in " + mismatched + " of " + computeFrame.Length + " bytes.");
            }
            Debug.Log("GaussianSplatCiChecks: compute-sorted and blit-sorted frames are byte-identical (" + computeFrame.Length + " bytes).");
        }

        // Saves the rendered frame for humans to look at (the byte comparison is the actual check).
        static void SaveFramePng(RenderTexture source, string path)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = source;
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                readback.Apply();
                System.IO.File.WriteAllBytes(path, readback.EncodeToPNG());
                Debug.Log("GaussianSplatCiChecks: wrote " + path);
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(readback);
            }
        }

        // A blank render (camera drew nothing) is a pass-shaped failure for the byte comparison,
        // so require a meaningful fraction of pixels to differ from the top-left background pixel.
        static void RequireVisibleContent(string label, byte[] frame)
        {
            float r0 = System.BitConverter.ToSingle(frame, 0);
            float g0 = System.BitConverter.ToSingle(frame, 4);
            float b0 = System.BitConverter.ToSingle(frame, 8);
            int texels = frame.Length / 16;
            int distinct = 0;
            for (int texel = 0; texel < texels; texel++)
            {
                int offset = texel * 16;
                if (Mathf.Abs(System.BitConverter.ToSingle(frame, offset) - r0) > 1.0f / 255.0f
                    || Mathf.Abs(System.BitConverter.ToSingle(frame, offset + 4) - g0) > 1.0f / 255.0f
                    || Mathf.Abs(System.BitConverter.ToSingle(frame, offset + 8) - b0) > 1.0f / 255.0f)
                {
                    distinct++;
                }
            }
            float coverage = (float)distinct / texels;
            Debug.Log("GaussianSplatCiChecks: " + label + " -- " + (coverage * 100.0f).ToString("F1") + "% of pixels differ from the background.");
            if (coverage < 0.01f)
            {
                throw new System.Exception(label + ": under 1% of pixels differ from the background; the splats did not render.");
            }
        }

        /// <summary>
        /// Checks the band-limited blit path (clears + combine passes + sort order copy restricted
        /// to the live texel region): first small edit-mode unit tests that pin the band quad's
        /// coverage and orientation exactly, then a play-mode A/B -- one frame rendered with the
        /// band-limited path, one with GaussianSplatBandBlit.ForceFullscreen -- which must be
        /// byte-identical. Needs a graphics device (run without -nographics, without -quit).
        /// </summary>
        public static void VerifyBandBlits()
        {
            try
            {
                RunBandUnitChecks();
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(SearchFolder + "/Example Scene.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                EditorApplication.playModeStateChanged += OnBandPlayModeChanged;
                EditorApplication.EnterPlaymode();
            }
            catch (System.Exception e)
            {
                Debug.LogError("GaussianSplatCiChecks FAILED\n" + e);
                EditorApplication.Exit(1);
            }
        }

        // Pins GaussianSplatBandBlit's rasterized region exactly: fill a small RT with red, band-
        // draw black over rows/columns [0, n), and require zeroes exactly there and red elsewhere.
        static void RunBandUnitChecks()
        {
            RenderTexture rt = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGBFloat);
            rt.Create();
            Texture2D red = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
            red.SetPixel(0, 0, new Color(1f, 0f, 0f, 1f));
            red.Apply();
            Material copyMaterial = null;
            try
            {
                Graphics.Blit(red, rt);
                GaussianSplatBandBlit.Clear(rt, 8);
                CheckBandRegion("band clear (8 rows)", ReadRenderTexture(rt), rt.width, rt.height, 8, rt.width);

                Shader blitCopy = Shader.Find("Hidden/BlitCopy");
                if (blitCopy == null)
                {
                    throw new System.Exception("Hidden/BlitCopy is missing; the band clear cannot work anywhere.");
                }
                copyMaterial = new Material(blitCopy) { hideFlags = HideFlags.HideAndDontSave };
                copyMaterial.mainTexture = Texture2D.blackTexture;
                Graphics.Blit(red, rt);
                GaussianSplatBandBlit.Blit(rt, copyMaterial, 0, 8, 16);
                CheckBandRegion("band rect blit (8x16)", ReadRenderTexture(rt), rt.width, rt.height, 8, 16);
                Debug.Log("GaussianSplatCiChecks: band-blit unit checks passed.");
            }
            finally
            {
                if (copyMaterial != null) Object.DestroyImmediate(copyMaterial);
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(red);
            }
        }

        static void CheckBandRegion(string label, byte[] data, int width, int height, int rows, int columns)
        {
            int wrongInside = 0, wrongOutside = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float r = System.BitConverter.ToSingle(data, (y * width + x) * 16);
                    bool inside = y < rows && x < columns;
                    if (inside && r != 0f) wrongInside++;
                    if (!inside && r != 1f) wrongOutside++;
                }
            }
            if (wrongInside > 0 || wrongOutside > 0)
            {
                throw new System.Exception(label + ": " + wrongInside + " texels inside the band kept the old value and "
                    + wrongOutside + " outside were overwritten -- the band quad's coverage/orientation is wrong.");
            }
        }

        static void OnBandPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode)
            {
                return;
            }
            s_smokeTicks = 0;
            EditorApplication.update += BandSmokeTick;
        }

        static void BandSmokeTick()
        {
            try
            {
                s_smokeTicks++;
                if (s_smokeTicks == 1)
                {
                    foreach (Canvas canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    {
                        canvas.enabled = false;
                    }
                    s_smokeTarget = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGBFloat);
                    s_smokeTarget.Create();
                }
                Camera camera = Camera.main;
                if (camera == null)
                {
                    throw new System.Exception("the example scene has no main camera in play mode.");
                }
                RenderCameraTo(camera, s_smokeTarget);
                if (s_smokeTicks < SmokeWarmupTicks)
                {
                    return;
                }
                EditorApplication.update -= BandSmokeTick;

                RenderCameraTo(camera, s_smokeTarget);
                byte[] bandFrame = ReadRenderTexture(s_smokeTarget);
                RequireVisibleContent("band-limited frame", bandFrame);
                SaveFramePng(s_smokeTarget, "Library/gs_ci_band_smoke.png");

                GaussianSplatBandBlit.ForceFullscreen = true;
                byte[] fullscreenFrame;
                try
                {
                    RenderCameraTo(camera, s_smokeTarget);
                    fullscreenFrame = ReadRenderTexture(s_smokeTarget);
                }
                finally
                {
                    GaussianSplatBandBlit.ForceFullscreen = false;
                }

                if (bandFrame.Length != fullscreenFrame.Length)
                {
                    throw new System.Exception("frame readbacks differ in size.");
                }
                int mismatched = 0;
                for (int i = 0; i < bandFrame.Length; i++)
                {
                    if (bandFrame[i] != fullscreenFrame[i]) mismatched++;
                }
                if (mismatched > 0)
                {
                    throw new System.Exception("band-limited and fullscreen frames differ in " + mismatched + " of " + bandFrame.Length + " bytes.");
                }
                Debug.Log("GaussianSplatCiChecks: band-limited and fullscreen frames are byte-identical (" + bandFrame.Length + " bytes).");
                Debug.Log("GaussianSplatCiChecks: band-blit checks passed.");
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                EditorApplication.update -= BandSmokeTick;
                Debug.LogError("GaussianSplatCiChecks FAILED\n" + e);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// Play-mode check for the Android no-geometry conversion: converts the example scene the
        /// way an Android build would, verifies the converted meshes are index-only (a 4-vertex
        /// dummy buffer instead of the old splatCount * 4 zeroed vertices) with the exact quad
        /// index pattern the shader decodes, renders a frame, then swaps in meshes rebuilt the old
        /// way (real splatCount * 4 vertex buffers, identical indices) and requires the two frames
        /// to be byte-identical. Needs a graphics device (run without -nographics, without -quit).
        /// </summary>
        public static void VerifyAndroidNoGeom()
        {
            try
            {
                UnityEngine.SceneManagement.Scene scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    SearchFolder + "/Example Scene.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
                int converted = GaussianSplatAndroidBuildProcessor.ConvertScene(scene);
                if (converted <= 0)
                {
                    throw new System.Exception("the Android conversion converted no renderers in the example scene.");
                }
                Debug.Log("GaussianSplatCiChecks: converted " + converted + " renderer(s) to the Android no-geom path.");
                // DisableSceneReload keeps the in-memory conversion alive across the play-mode
                // transition (a reload would restore the desktop meshes from disk).
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
                EditorApplication.playModeStateChanged += OnAndroidPlayModeChanged;
                EditorApplication.EnterPlaymode();
            }
            catch (System.Exception e)
            {
                Debug.LogError("GaussianSplatCiChecks FAILED\n" + e);
                EditorApplication.Exit(1);
            }
        }

        static void OnAndroidPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode)
            {
                return;
            }
            s_smokeTicks = 0;
            EditorApplication.update += AndroidSmokeTick;
        }

        static void AndroidSmokeTick()
        {
            try
            {
                s_smokeTicks++;
                if (s_smokeTicks == 1)
                {
                    foreach (Canvas canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    {
                        canvas.enabled = false;
                    }
                    s_smokeTarget = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGBFloat);
                    s_smokeTarget.Create();
                }
                Camera camera = Camera.main;
                if (camera == null)
                {
                    throw new System.Exception("the example scene has no main camera in play mode.");
                }
                RenderCameraTo(camera, s_smokeTarget);
                if (s_smokeTicks < SmokeWarmupTicks)
                {
                    return;
                }
                EditorApplication.update -= AndroidSmokeTick;
                RunAndroidNoGeomChecks(camera);
                Debug.Log("GaussianSplatCiChecks: Android no-geom checks passed.");
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                EditorApplication.update -= AndroidSmokeTick;
                Debug.LogError("GaussianSplatCiChecks FAILED\n" + e);
                EditorApplication.Exit(1);
            }
        }

        static void RunAndroidNoGeomChecks(Camera camera)
        {
            List<MeshFilter> convertedFilters = new List<MeshFilter>();
            foreach (MeshFilter filter in Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (filter.sharedMesh != null && filter.sharedMesh.name.EndsWith("_AndroidNoGeom"))
                {
                    convertedFilters.Add(filter);
                }
            }
            if (convertedFilters.Count == 0)
            {
                throw new System.Exception("no converted (_AndroidNoGeom) meshes survived into play mode.");
            }

            long totalDroppedBytes = 0;
            foreach (MeshFilter filter in convertedFilters)
            {
                totalDroppedBytes += VerifyIndexOnlyMesh(filter);
            }
            Debug.Log("GaussianSplatCiChecks: " + convertedFilters.Count + " converted mesh(es) are index-only; the old vertex buffers would have carried "
                + (totalDroppedBytes / (1024.0 * 1024.0)).ToString("F1") + " MB of zeroes.");

            RenderCameraTo(camera, s_smokeTarget);
            byte[] indexOnlyFrame = ReadRenderTexture(s_smokeTarget);
            RequireVisibleContent("index-only no-geom frame", indexOnlyFrame);
            SaveFramePng(s_smokeTarget, "Library/gs_ci_android_smoke.png");

            // Rebuild every converted mesh the way the old generator did -- a real vertex buffer
            // covering every index value -- and render again. SV_VertexID comes from the index
            // values either way, so the frames must match exactly.
            List<Mesh> legacyMeshes = new List<Mesh>();
            try
            {
                foreach (MeshFilter filter in convertedFilters)
                {
                    Mesh legacy = BuildLegacyStyleMesh(filter.sharedMesh);
                    legacyMeshes.Add(legacy);
                    filter.sharedMesh = legacy;
                }
                RenderCameraTo(camera, s_smokeTarget);
            }
            finally
            {
                foreach (Mesh mesh in legacyMeshes)
                {
                    Object.Destroy(mesh);
                }
            }
            byte[] legacyFrame = ReadRenderTexture(s_smokeTarget);

            if (indexOnlyFrame.Length != legacyFrame.Length)
            {
                throw new System.Exception("frame readbacks differ in size.");
            }
            int mismatched = 0;
            for (int i = 0; i < indexOnlyFrame.Length; i++)
            {
                if (indexOnlyFrame[i] != legacyFrame[i]) mismatched++;
            }
            if (mismatched > 0)
            {
                throw new System.Exception("index-only and legacy-mesh frames differ in " + mismatched + " of " + indexOnlyFrame.Length + " bytes.");
            }
            Debug.Log("GaussianSplatCiChecks: index-only and legacy-mesh frames are byte-identical (" + indexOnlyFrame.Length + " bytes).");
        }

        // Asserts the converted mesh really is index-only and its quad submeshes carry the exact
        // index pattern the no-geom shader decodes (vertexID >> 2 = splat, vertexID & 3 = corner).
        // Returns how many vertex-buffer bytes the old generator would have shipped instead.
        static long VerifyIndexOnlyMesh(MeshFilter filter)
        {
            Mesh mesh = filter.sharedMesh;
            Material[] materials = filter.GetComponent<MeshRenderer>().sharedMaterials;
            int maxIndexValue = 0;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                int[] indices = mesh.GetIndices(submesh);
                for (int i = 0; i < indices.Length; i++)
                {
                    maxIndexValue = Mathf.Max(maxIndexValue, indices[i]);
                }

                Material material = submesh < materials.Length ? materials[submesh] : null;
                bool isSplatSubmesh = material != null && material.shader != null
                    && material.shader.name == GaussianSplatAndroidBuildProcessor.FakeSrgbNoGeomShaderName;
                if (!isSplatSubmesh)
                {
                    continue;
                }
                if (indices.Length == 0 || indices.Length % 6 != 0)
                {
                    throw new System.Exception(mesh.name + " submesh " + submesh + ": splat index count " + indices.Length + " is not a positive multiple of 6.");
                }
                int splatCount = material.HasProperty("_SplatCount") ? material.GetInt("_SplatCount") : 0;
                if (splatCount > 0 && indices.Length != splatCount * 6)
                {
                    throw new System.Exception(mesh.name + " submesh " + submesh + ": " + indices.Length + " indices for _SplatCount " + splatCount + ".");
                }
                int[] expected = GaussianSplatAndroidBuildProcessor.CreateQuadIndices(indices.Length / 6);
                for (int i = 0; i < indices.Length; i++)
                {
                    if (indices[i] != expected[i])
                    {
                        throw new System.Exception(mesh.name + " submesh " + submesh + ": index " + i + " is " + indices[i] + ", expected " + expected[i] + ".");
                    }
                }
            }

            if (mesh.vertexCount > Mathf.Max(4, maxIndexValue / 16))
            {
                throw new System.Exception(mesh.name + ": vertex count " + mesh.vertexCount + " is not index-only (max index value " + maxIndexValue + ").");
            }
            return (long)(maxIndexValue + 1) * 12;
        }

        // The old generator's shape: a vertex buffer actually covering every index value, all
        // zeroed, with the same indices, topologies, and bounds.
        static Mesh BuildLegacyStyleMesh(Mesh source)
        {
            int maxIndexValue = 0;
            for (int submesh = 0; submesh < source.subMeshCount; submesh++)
            {
                int[] indices = source.GetIndices(submesh);
                for (int i = 0; i < indices.Length; i++)
                {
                    maxIndexValue = Mathf.Max(maxIndexValue, indices[i]);
                }
            }

            Mesh mesh = new Mesh();
            mesh.name = source.name + "_LegacyStyle";
            mesh.indexFormat = maxIndexValue > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = new Vector3[maxIndexValue + 1];
            mesh.subMeshCount = source.subMeshCount;
            for (int submesh = 0; submesh < source.subMeshCount; submesh++)
            {
                mesh.SetIndices(source.GetIndices(submesh), source.GetTopology(submesh), submesh, false, 0);
            }
            mesh.bounds = source.bounds;
            return mesh;
        }

        /// <summary>
        /// Runtime equivalence check for the two sort paths. Needs a graphics device (run without
        /// -nographics). Opens the example scene, drives one editor sort to bind the sort inputs,
        /// then verifies that (1) the compute sort leaves the blit scratch RTs released, (2) the
        /// blit fallback auto-creates them when the compute shader is unavailable, and (3) both
        /// paths write byte-identical render orders.
        /// </summary>
        public static void VerifySortRuntime()
        {
            try
            {
                RunSortRuntimeChecks();
                Debug.Log("GaussianSplatCiChecks: sort runtime checks passed.");
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError("GaussianSplatCiChecks FAILED\n" + e);
                EditorApplication.Exit(1);
            }
        }

        static void RunSortRuntimeChecks()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                throw new System.Exception("compute shaders are unsupported on this editor platform; the equivalence check cannot run.");
            }

            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(SearchFolder + "/Example Scene.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
            GaussianSplatRenderer renderer = Object.FindFirstObjectByType<GaussianSplatRenderer>();
            RadixSort radixSort = Object.FindFirstObjectByType<RadixSort>();
            if (renderer == null || radixSort == null)
            {
                throw new System.Exception("example scene is missing the GaussianSplatRenderer or RadixSort component.");
            }

            GameObject cameraObject = new GameObject("SortCiCamera");
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                cameraObject.transform.position = renderer.transform.position + new Vector3(2.0f, 1.5f, 2.0f);
                cameraObject.transform.LookAt(renderer.transform.position);

                if (!renderer.PrepareEditorCameraRender(camera))
                {
                    throw new System.Exception("PrepareEditorCameraRender failed; the sort inputs could not be bound.");
                }
                RenderTexture order = renderer.splatRenderOrder;
                if (order == null || radixSort.elementCount <= 0)
                {
                    throw new System.Exception("no render-order texture or element count after the editor sort.");
                }
                Debug.Log("GaussianSplatCiChecks: sorting " + radixSort.elementCount + " elements into "
                    + order.width + "x" + order.height + " " + order.format + ".");

                // Baseline: the runtime blit path (the actual fallback), forced by hiding the
                // compute shader. In-memory change only; the scene is never saved.
                RenderTexture[] scratch = { radixSort.keyValues0, radixSort.keyValues1, radixSort.histograms, radixSort.prefixSums };
                ComputeShader savedCompute = radixSort.radixSortCompute;
                radixSort.radixSortCompute = null;
                try
                {
                    radixSort.RunFullSort(order, 0);
                }
                finally
                {
                    radixSort.radixSortCompute = savedCompute;
                }
                byte[] blitOrder = ReadRenderTexture(order);
                VerifyOrderIsPermutation("runtime blit baseline", blitOrder, radixSort.elementCount, order.width);

                // 1. The compute path must never touch the blit scratch RTs (this is what lets the
                //    renderer skip pre-creating them when the compute sort is available), and must
                //    produce the identical order to the blit fallback.
                foreach (RenderTexture rt in scratch)
                {
                    if (rt != null) rt.Release();
                }
                radixSort.RunFullSort(order, 0);
                foreach (RenderTexture rt in scratch)
                {
                    if (rt != null && rt.IsCreated())
                    {
                        throw new System.Exception("the compute sort touched blit scratch RT '" + rt.name + "'.");
                    }
                }
                CompareOrders("compute vs runtime blit", ReadRenderTexture(order), blitOrder, radixSort.elementCount, order.width);

                // 2. With the compute shader unavailable again, the blit fallback must auto-create
                //    its scratch RTs (they were released above) and reproduce the baseline exactly.
                radixSort.radixSortCompute = null;
                try
                {
                    radixSort.RunFullSort(order, 0);
                }
                finally
                {
                    radixSort.radixSortCompute = savedCompute;
                }
                bool anyCreated = false;
                foreach (RenderTexture rt in scratch)
                {
                    anyCreated |= rt != null && rt.IsCreated();
                }
                if (!anyCreated)
                {
                    throw new System.Exception("the blit fallback ran without creating any scratch RT, so it cannot actually have sorted.");
                }
                CompareOrders("blit fallback after RT release vs runtime blit", ReadRenderTexture(order), blitOrder, radixSort.elementCount, order.width);
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }

        static byte[] ReadRenderTexture(RenderTexture source)
        {
            RenderTexture temp = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBFloat);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, temp);
                RenderTexture.active = temp;
                Texture2D readback = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false);
                try
                {
                    readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                    readback.Apply();
                    return readback.GetRawTextureData();
                }
                finally
                {
                    Object.DestroyImmediate(readback);
                }
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temp);
            }
        }

        // Inverse of the interleave in RadixSort/Utils.cginc -- rank r lives at texel
        // (Deinterleave(r), Deinterleave(r >> 1)) of the render-order texture.
        static uint DeinterleaveWithZero(uint word)
        {
            word &= 0x55555555;
            word = (word | (word >> 1)) & 0x33333333;
            word = (word | (word >> 2)) & 0x0f0f0f0f;
            word = (word | (word >> 4)) & 0x00ff00ff;
            word = (word | (word >> 8)) & 0x0000ffff;
            return word;
        }

        // Byte offset of the rank's order id in an RGBAFloat readback (16 bytes/texel, id in R).
        static long RankByteOffset(uint rank, int width)
        {
            long x = DeinterleaveWithZero(rank);
            long y = DeinterleaveWithZero(rank >> 1);
            return (y * width + x) * 16;
        }

        // Only the texels holding ranks below the element count are ever read by the draw
        // (GS.cginc culls id >= actualSplatCount before the order fetch), and the two copy
        // shaders deliberately differ in how they pad the rest of the texture -- so both checks
        // walk exactly the readable rank -> texel (Morton) mapping and nothing else.
        static void CompareOrders(string label, byte[] actual, byte[] expected, int elementCount, int width)
        {
            if (actual.Length != expected.Length)
            {
                throw new System.Exception(label + ": readback sizes differ (" + actual.Length + " vs " + expected.Length + " bytes).");
            }
            int mismatched = 0;
            StringBuilder samples = new StringBuilder();
            for (uint rank = 0; rank < (uint)elementCount; rank++)
            {
                long offset = RankByteOffset(rank, width);
                float actualId = System.BitConverter.ToSingle(actual, (int)offset);
                float expectedId = System.BitConverter.ToSingle(expected, (int)offset);
                if (actualId == expectedId)
                {
                    continue;
                }
                mismatched++;
                if (mismatched <= 8)
                {
                    samples.AppendLine("  rank " + rank + ": expected id " + expectedId + ", got " + actualId);
                }
            }
            if (mismatched > 0)
            {
                throw new System.Exception(label + ": " + mismatched + " of " + elementCount + " ranks differ. First mismatches:\n" + samples);
            }
            Debug.Log("GaussianSplatCiChecks: " + label + " -- orders are identical on all " + elementCount + " ranks.");
        }

        static void VerifyOrderIsPermutation(string label, byte[] orderData, int elementCount, int width)
        {
            bool[] seen = new bool[elementCount];
            int outOfRange = 0;
            int duplicates = 0;
            for (uint rank = 0; rank < (uint)elementCount; rank++)
            {
                float idValue = System.BitConverter.ToSingle(orderData, (int)RankByteOffset(rank, width));
                int id = (int)idValue;
                if (idValue != id || id < 0 || id >= elementCount)
                {
                    outOfRange++;
                }
                else if (seen[id])
                {
                    duplicates++;
                }
                else
                {
                    seen[id] = true;
                }
            }
            if (outOfRange > 0 || duplicates > 0)
            {
                throw new System.Exception(label + ": ids are not a permutation of 0.." + (elementCount - 1)
                    + " (" + outOfRange + " out of range, " + duplicates + " duplicated).");
            }
            Debug.Log("GaussianSplatCiChecks: " + label + " -- ids form a permutation of 0.." + (elementCount - 1) + ".");
        }
    }
}
#endif
