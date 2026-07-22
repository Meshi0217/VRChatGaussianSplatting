using UnityEngine;
using UnityEditor;
using UnityEngine.Experimental.Rendering;
using System.Collections.Generic;
using System.Collections;

namespace VRCLightVolumes {

    public static class TextureArrayGenerator {

        // Creates a Texture2DArray from a Texture2D or a Cubemaps list. Deduplicates everything if the assets are the same and also returns an array of uniique IDs in the texture array for each of the source textures
        // Note that uniqueIDs length will not be the same as Texture2DArray lenght if there are Cubemaps represented in the source list. One cubemap uses 6 Texture2DArray slots.
        public static IEnumerator CreateTexture2DArrayAsync(List<Texture> textures, int res, TextureFormat format, System.Action<Texture2DArray, int[]> onComplete) {

            List<RenderTexture> processedTextures = new List<RenderTexture>();
            Texture2D temp = null;

#if UNITY_EDITOR
            int progressId = Progress.Start("Generating 2D Array", "Generating Light Volumes 2D Array", Progress.Options.Sticky);
            const int progressStepsCount = 3; // Steps count for progress bar
#endif

            try {

                int count = textures.Count;
                if (textures == null || count == 0) {
                    yield break;
                }

                // Deduplication
                var uniqueIDs = new int[count]; // IDs in unique textures list for each source texture element 
                List<Texture> uniqueTextures = new List<Texture>(); // Deduplicated textures list
                for (int i = 0; i < count; i++) {

#if UNITY_EDITOR
                    Progress.Report(progressId, (float)((float) i / count) / progressStepsCount, $"Finding unique textures ({i + 1}/{count})");
#endif

                    int id = uniqueTextures.IndexOf(textures[i]);
                    if (id == -1) { // It's a new unique texture
                        uniqueTextures.Add(textures[i]);
                        uniqueIDs[i] = uniqueTextures.Count - 1;
                    } else { // This is a duplicated texture
                        uniqueIDs[i] = id;
                    }
                    yield return null;
                }
                int uniqueCount = uniqueTextures.Count;

                // Rescaling and bliting textures into render textures
                for (int i = 0; i < uniqueCount; i++) {
#if UNITY_EDITOR
                    Progress.Report(progressId, (float)((float)i / uniqueCount + 1) / progressStepsCount, $"Rescaling textures ({i + 1}/{uniqueCount})");
#endif
                    if (uniqueTextures[i].GetType() == typeof(Cubemap)) {
                        Cubemap cube = (Cubemap)uniqueTextures[i];
                        processedTextures.AddRange(CreateCubemap(cube, res, format));
                    } else if (uniqueTextures[i].GetType() == typeof(Texture2D)) {
                        Texture2D tex = (Texture2D)uniqueTextures[i];
                        processedTextures.Add(CreateTexture(tex, res, format));
                    }
                    yield return null;
                }
                int processedCount = processedTextures.Count;

                // Copying data from render textures to Texture2DArray
                Texture2DArray array = new Texture2DArray(res, res, processedCount, GraphicsFormatUtility.GetGraphicsFormat(format, true), TextureCreationFlags.None, 0);
                array.anisoLevel = 0;
                array.filterMode = FilterMode.Trilinear;
                array.wrapMode = TextureWrapMode.Clamp;
                temp = new Texture2D(res, res, format, false, true); // Temp texture to read pixels from RT to
                for (int i = 0; i < processedCount; i++) {
#if UNITY_EDITOR
                    Progress.Report(progressId, (float)((float)i / processedCount + 2) / progressStepsCount, $"Writing to texture array ({i + 1}/{processedCount})");
#endif
                    RenderTexture.active = processedTextures[i];
                    if (RenderTexture.active == null || temp == null) yield break; // In case the textures were destroyed
                    temp.ReadPixels(new Rect(0, 0, res, res), 0, 0);
                    temp.Apply(false);
                    Graphics.CopyTexture(temp, 0, 0, array, i, 0);
                    yield return null;
                }
                array.Apply(false);

                onComplete?.Invoke(array, uniqueIDs);

            } finally {

#if UNITY_EDITOR
                Progress.Finish(progressId);
                Progress.Remove(progressId);
#endif
                // Release and destroy the textures
                RenderTexture.active = null;
                if(temp != null) {
                    Object.DestroyImmediate(temp);
                }
                for (int i = 0; i < processedTextures.Count; i++) {
                    if (processedTextures[i] != null) {
                        processedTextures[i].Release();
                        Object.DestroyImmediate(processedTextures[i]);
                    }
                }

            }

        }

        // Blits all cubemap faces into a render texture array with specified resolution
        public static RenderTexture[] CreateCubemap(Cubemap cubemap, int res, TextureFormat format) {
            Material mat = null;
            try {
                RenderTexture[] rt = new RenderTexture[6];
                mat = new Material(Shader.Find("Hidden/CubeFace"));
                mat.SetTexture("_MainTex", cubemap);
                for (int i = 0; i < 6; i++) {
                    mat.SetInt("_FaceIndex", i);
                    rt[i] = new RenderTexture(res, res, 0);
                    rt[i].graphicsFormat = GraphicsFormatUtility.GetGraphicsFormat(format, true);
                    rt[i].enableRandomWrite = false;
                    rt[i].Create();
                    Graphics.Blit(null, rt[i], mat);
                }
                return rt;
            } finally {
                Graphics.SetRenderTarget(null);
                if (mat != null) {
                    Object.DestroyImmediate(mat);
                }

            }
        }

        // Blits texture into a render texture with specified resolution
        public static RenderTexture CreateTexture(Texture2D tex, int res, TextureFormat format) {
            try {
                RenderTexture rt = new RenderTexture(res, res, 0);
                rt.graphicsFormat = GraphicsFormatUtility.GetGraphicsFormat(format, true);
                rt.enableRandomWrite = false;
                rt.Create();
                Graphics.Blit(tex, rt);
                return rt;
            } finally {
                Graphics.SetRenderTarget(null);
            }
        }

    }

}