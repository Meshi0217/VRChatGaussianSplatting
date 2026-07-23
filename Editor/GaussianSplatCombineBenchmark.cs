#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Measures the GPU cost of the four GSLODCombine attribute passes (positions, rotations,
    /// scales, colors) on the current desktop GPU, to size the potential win of merging them into
    /// one MRT pass (each pass currently re-runs the LOD selection mip-descent). A same-size plain
    /// copy blit is measured as the bandwidth floor. Diagnostic tool, not a CI gate. Run with
    ///   Unity.exe -batchmode -projectPath . -executeMethod GaussianSplatting.Editor.GaussianSplatCombineBenchmark.Run
    /// (without -nographics, without -quit).
    /// </summary>
    public static class GaussianSplatCombineBenchmark
    {
        const string ScenePath = "Assets/VRChatGaussianSplatting/Example Scene.unity";
        const int WarmupIterations = 10;
        const int TimedIterations = 100;

        public static void Run()
        {
            try
            {
                RunBenchmark();
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError("GaussianSplatCombineBenchmark FAILED\n" + e);
                EditorApplication.Exit(1);
            }
        }

        static void RunBenchmark()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(ScenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
            GaussianSplatRenderer renderer = Object.FindFirstObjectByType<GaussianSplatRenderer>();
            GaussianSplatCombiner combiner = Object.FindFirstObjectByType<GaussianSplatCombiner>();
            if (renderer == null || combiner == null)
            {
                throw new System.Exception("example scene is missing the GaussianSplatRenderer or GaussianSplatCombiner.");
            }

            GameObject cameraObject = new GameObject("CombineBenchCamera");
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                cameraObject.transform.position = renderer.transform.position + new Vector3(2.0f, 1.5f, 2.0f);
                cameraObject.transform.LookAt(renderer.transform.position);
                // Runs the full editor sort path once: selection + all four combine passes, leaving
                // the combine material's textures/params bound exactly as the real per-frame combine
                // uses them, so standalone re-blits below re-execute the identical fragment work.
                if (!renderer.PrepareEditorCameraRender(camera))
                {
                    throw new System.Exception("PrepareEditorCameraRender failed; the combine inputs could not be bound.");
                }

                Material combineMaterial = (Material)GetField(combiner, "lodUnifiedCombineMaterial");
                RenderTexture positions = (RenderTexture)GetField(combiner, "combinedPositions");
                RenderTexture rotations = (RenderTexture)GetField(combiner, "combinedRotations");
                RenderTexture scales = (RenderTexture)GetField(combiner, "combinedScales");
                RenderTexture colors = (RenderTexture)GetField(combiner, "combinedColors");
                int actualCount = (int)GetField(combiner, "_combinedActualSplatCount");
                if (combineMaterial == null || positions == null || rotations == null || scales == null || colors == null)
                {
                    throw new System.Exception("combine material or combined output RTs are unbound after the editor sort.");
                }

                Debug.Log("GaussianSplatCombineBenchmark: " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + "), "
                    + positions.width + "x" + positions.height + " combined RTs, " + actualCount + " selected splats, "
                    + TimedIterations + " iterations/pass.");

                // The per-frame fullscreen raster set: 4 clears + 4 combine passes (UpdateTextures)
                // plus the sort's fullscreen order copy. Then the same work restricted to the block
                // rows that actually hold [0, selected) -- what a viewport-limited implementation
                // would rasterize -- measured on band-height RTs of the same width and formats.
                double clearMs = TimeClears(positions, rotations, scales, colors);
                double positionsMs = TimeBlits(combineMaterial, positions, "positions", 0, null);
                double rotationsMs = TimeBlits(combineMaterial, rotations, "rotations", 1, null);
                double scalesMs = TimeBlits(combineMaterial, scales, "scales", 2, null);
                double colorsMs = TimeBlits(combineMaterial, colors, "colors", 3, null);
                double combineMs = positionsMs + rotationsMs + scalesMs + colorsMs;

                RadixSort radixSort = Object.FindFirstObjectByType<RadixSort>();
                RenderTexture order = renderer.splatRenderOrder;
                double sortMs = -1.0;
                if (radixSort != null && order != null && radixSort.elementCount > 0)
                {
                    for (int i = 0; i < WarmupIterations; i++) radixSort.RunFullSort(order, 0);
                    ForceGpuSync(order);
                    System.Diagnostics.Stopwatch sortWatch = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < TimedIterations; i++) radixSort.RunFullSort(order, 0);
                    ForceGpuSync(order);
                    sortWatch.Stop();
                    sortMs = sortWatch.Elapsed.TotalMilliseconds / TimedIterations;
                }

                // Band height covering [0, actualCount): 16 splats per 4x4 block, blocks fill row
                // bands bottom-up in index order, so the live region is the first blockRows * 4 rows.
                int blocksPerRow = Mathf.Max(1, positions.width >> 2);
                int blockRows = (Mathf.Max(1, actualCount) + 16 * blocksPerRow - 1) / (16 * blocksPerRow);
                int bandHeight = Mathf.Min(positions.height, blockRows * 4);
                double bandClearMs, bandCombineMs;
                {
                    RenderTexture bandPositions = new RenderTexture(positions.width, bandHeight, 0, positions.format);
                    RenderTexture bandRotations = new RenderTexture(rotations.width, bandHeight, 0, rotations.format);
                    RenderTexture bandScales = new RenderTexture(scales.width, bandHeight, 0, scales.format);
                    RenderTexture bandColors = new RenderTexture(colors.width, bandHeight, 0, colors.format);
                    try
                    {
                        bandClearMs = TimeClears(bandPositions, bandRotations, bandScales, bandColors);
                        bandCombineMs = TimeBlits(combineMaterial, bandPositions, "band positions", 0, null)
                            + TimeBlits(combineMaterial, bandRotations, "band rotations", 1, null)
                            + TimeBlits(combineMaterial, bandScales, "band scales", 2, null)
                            + TimeBlits(combineMaterial, bandColors, "band colors", 3, null);
                    }
                    finally
                    {
                        ReleaseRT(bandPositions); ReleaseRT(bandRotations); ReleaseRT(bandScales); ReleaseRT(bandColors);
                    }
                }

                double fullscreenMs = clearMs + combineMs;
                double bandMs = bandClearMs + bandCombineMs;
                Debug.Log("GaussianSplatCombineBenchmark results (ms, " + positions.width + "x" + positions.height
                    + " vs " + positions.width + "x" + bandHeight + " band for " + actualCount + " splats):\n"
                    + "  4 clears fullscreen:         " + clearMs.ToString("F3") + "\n"
                    + "  4 combine passes fullscreen: " + combineMs.ToString("F3")
                    + " (pos " + positionsMs.ToString("F3") + ", rot " + rotationsMs.ToString("F3")
                    + ", scale " + scalesMs.ToString("F3") + ", color " + colorsMs.ToString("F3") + ")\n"
                    + "  clears+combine fullscreen:   " + fullscreenMs.ToString("F3") + "\n"
                    + "  clears+combine band-limited: " + bandMs.ToString("F3")
                    + " (saving " + (fullscreenMs - bandMs).ToString("F3") + " ms/frame, "
                    + (100.0 * (fullscreenMs - bandMs) / fullscreenMs).ToString("F1") + "%)\n"
                    + "  full compute sort (keys + 3 rounds + fullscreen order copy): " + (sortMs >= 0 ? sortMs.ToString("F3") : "n/a"));
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }

        // Times the 4 black-clear blits UpdateTextures issues before every combine, as one unit.
        static double TimeClears(RenderTexture positions, RenderTexture rotations, RenderTexture scales, RenderTexture colors)
        {
            for (int i = 0; i < WarmupIterations; i++)
            {
                Graphics.Blit(Texture2D.blackTexture, positions);
                Graphics.Blit(Texture2D.blackTexture, rotations);
                Graphics.Blit(Texture2D.blackTexture, scales);
                Graphics.Blit(Texture2D.blackTexture, colors);
            }
            ForceGpuSync(colors);
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < TimedIterations; i++)
            {
                Graphics.Blit(Texture2D.blackTexture, positions);
                Graphics.Blit(Texture2D.blackTexture, rotations);
                Graphics.Blit(Texture2D.blackTexture, scales);
                Graphics.Blit(Texture2D.blackTexture, colors);
            }
            ForceGpuSync(colors);
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds / TimedIterations;
        }

        static void ReleaseRT(RenderTexture rt)
        {
            if (rt != null)
            {
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        // Times `TimedIterations` fullscreen blits of one combine pass (or, with a null material, a
        // plain copy of `copySource`) with GPU sync before and after, returning ms per blit.
        static double TimeBlits(Material material, RenderTexture target, string label, int pass, Texture copySource)
        {
            for (int i = 0; i < WarmupIterations; i++)
            {
                BlitOnce(material, target, pass, copySource);
            }
            ForceGpuSync(target);

            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < TimedIterations; i++)
            {
                BlitOnce(material, target, pass, copySource);
            }
            ForceGpuSync(target);
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds / TimedIterations;
        }

        static void BlitOnce(Material material, RenderTexture target, int pass, Texture copySource)
        {
            if (material != null)
            {
                Graphics.Blit(null, target, material, pass);
            }
            else
            {
                Graphics.Blit(copySource, target);
            }
        }

        // A tiny readback stalls until every queued command on the target has completed.
        static void ForceGpuSync(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D pixel = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
            try
            {
                RenderTexture.active = target;
                pixel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                pixel.Apply();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(pixel);
            }
        }

        static object GetField(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
            {
                throw new System.Exception("field '" + name + "' not found on " + instance.GetType().Name + " (was it renamed?).");
            }
            return field.GetValue(instance);
        }
    }
}
#endif
