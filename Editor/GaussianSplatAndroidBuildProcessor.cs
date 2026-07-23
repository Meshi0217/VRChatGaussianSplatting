#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GaussianSplatting.Editor
{
    public class GaussianSplatAndroidBuildProcessor : IProcessSceneWithReport
    {
        const string GeomShaderName = "VRChatGaussianSplatting/GaussianSplatting";
        const string NoGeomShaderName = "VRChatGaussianSplatting/GaussianSplattingNoGeom";
        internal const string FakeSrgbNoGeomShaderName = "VRChatGaussianSplatting/GaussianSplattingNoGeomSimpleBackToFront";
        const string ToSrgbShaderName = "VRChatGaussianSplatting/ToSRGB";
        const string ToLinearShaderName = "VRChatGaussianSplatting/ToLinear";
        const string AlphaDepthMaskShaderName = "VRChatGaussianSplatting/AlphaDepthMask";

        public int callbackOrder => 1000;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!BuildPipeline.isBuildingPlayer || report == null || report.summary.platform != BuildTarget.Android)
            {
                return;
            }
            ConvertScene(scene);
        }

        // The build conversion, minus the build-only guards, so the open scene can be converted in the
        // editor. This is the only way to see the no-geometry path without a device build: the
        // geometry-shader splats carry '#pragma exclude_renderers gles', so an Android-target editor
        // (or a mobile GPU) drops them entirely, and OnProcessScene never runs in play mode. Converting
        // in-editor lets the desktop GPU (with MockHMD) render exactly what a Quest build would.
        [UnityEditor.MenuItem("Gaussian Splatting/Convert Open Scene To Android No-Geometry (preview)")]
        static void ConvertOpenScenePreview()
        {
            Scene scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            int converted = ConvertScene(scene);
            Debug.Log("Gaussian Splatting: converted " + converted + " splat renderer(s) in '" + scene.name +
                      "' to the Android no-geometry path. This is an unsaved in-editor preview -- enter Play mode to test (MockHMD included), then reopen the scene WITHOUT saving to discard it.");
        }

        internal static int ConvertScene(Scene scene)
        {
            Shader fallbackShader = Shader.Find(FakeSrgbNoGeomShaderName);
            if (fallbackShader == null)
            {
                Debug.LogWarning("Gaussian splat Android conversion skipped: shader '" + FakeSrgbNoGeomShaderName + "' was not found.");
                return 0;
            }
            if (HasGaussianSplats(scene))
            {
                GaussianSplatRenderer.EnsureSceneRendererExists(scene);
                ApplyAndroidLowQuality(scene);
                DisableAndroidCameraHdr(scene);
            }

            int convertedCount = 0;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                MeshRenderer[] renderers = roots[i].GetComponentsInChildren<MeshRenderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    if (ConvertRenderer(renderers[rendererIndex], fallbackShader))
                    {
                        convertedCount++;
                    }
                }
            }
            return convertedCount;
        }

        static void ApplyAndroidLowQuality(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GaussianSplatRenderer[] renderers = roots[i].GetComponentsInChildren<GaussianSplatRenderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    GaussianSplatRenderer renderer = renderers[rendererIndex];
                    renderer.SetQualityLow();
                }
            }
        }

        static void DisableAndroidCameraHdr(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                Camera[] cameras = roots[i].GetComponentsInChildren<Camera>(true);
                for (int cameraIndex = 0; cameraIndex < cameras.Length; cameraIndex++)
                {
                    cameras[cameraIndex].allowHDR = false;
                }
            }
        }

        static bool HasGaussianSplats(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].GetComponentInChildren<GaussianSplatObject>(true) != null)
                {
                    return true;
                }
            }
            return false;
        }

        static bool ConvertRenderer(MeshRenderer renderer, Shader fallbackShader)
        {
            if (renderer == null)
            {
                return false;
            }

            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            Mesh sourceMesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (meshFilter == null || sourceMesh == null)
            {
                return false;
            }

            Material[] materials = renderer.sharedMaterials;
            List<SubmeshConversion> submeshes = new List<SubmeshConversion>(materials.Length);
            List<Material> convertedMaterials = new List<Material>(materials.Length);
            bool changed = false;
            int drawableCount = Mathf.Min(materials.Length, sourceMesh.subMeshCount);
            for (int i = 0; i < drawableCount; i++)
            {
                Material material = materials[i];
                string shaderName = GetShaderName(material);
                if (IsRemovedGrabPassShader(shaderName))
                {
                    changed = true;
                    continue;
                }

                bool convert = IsSplatShader(shaderName);
                Material outputMaterial = material;
                if (convert)
                {
                    changed = true;
                }
                if (convert && shaderName != FakeSrgbNoGeomShaderName)
                {
                    outputMaterial = new Material(material);
                    outputMaterial.shader = fallbackShader;
                    outputMaterial.name = material.name + "_AndroidFakeSRGB";
                    outputMaterial.renderQueue = material.renderQueue;
                }

                submeshes.Add(new SubmeshConversion(i, convertedMaterials.Count, convert));
                convertedMaterials.Add(outputMaterial);
            }

            if (!changed)
            {
                return false;
            }

            Mesh convertedMesh = CreateNoGeomMesh(sourceMesh, convertedMaterials, submeshes);
            renderer.sharedMaterials = convertedMaterials.ToArray();
            meshFilter.sharedMesh = convertedMesh;
            return true;
        }

        // The no-geom vertex shader reads nothing but SV_VertexID, which on an indexed draw is the
        // index buffer VALUE -- so the quad "vertices" exist only as index values (splat * 4 +
        // corner) and the vertex buffer stays a 4-vertex dummy. MeshUpdateFlags.DontValidateIndices
        // lets the indices exceed the vertex count; nothing ever fetches them (appdata declares no
        // POSITION under GS_NO_GEOM). This replaces the old splatCount * 4 zeroed Vector3 buffer,
        // which shipped ~48 MB of zeroes per million splats in the APK, RAM, and VRAM.
        static Mesh CreateNoGeomMesh(Mesh sourceMesh, List<Material> materials, List<SubmeshConversion> submeshes)
        {
            const MeshUpdateFlags NoValidation = MeshUpdateFlags.DontValidateIndices
                | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;

            int[][] submeshIndices = new int[submeshes.Count][];
            MeshTopology[] topologies = new MeshTopology[submeshes.Count];
            bool anyUnconverted = false;
            int totalIndexCount = 0;
            int maxIndexValue = 0;
            for (int i = 0; i < submeshes.Count; i++)
            {
                SubmeshConversion submesh = submeshes[i];
                if (submesh.convertToQuads)
                {
                    submeshIndices[i] = CreateQuadIndices(GetSubmeshSplatCount(sourceMesh, materials[submesh.materialIndex], submesh.sourceSubmesh));
                    topologies[i] = MeshTopology.Triangles;
                }
                else
                {
                    submeshIndices[i] = sourceMesh.GetIndices(submesh.sourceSubmesh);
                    topologies[i] = sourceMesh.GetTopology(submesh.sourceSubmesh);
                    anyUnconverted = true;
                }
                totalIndexCount += submeshIndices[i].Length;
                for (int index = 0; index < submeshIndices[i].Length; index++)
                {
                    maxIndexValue = Mathf.Max(maxIndexValue, submeshIndices[i][index]);
                }
            }

            // Unconverted submeshes were already drawn with all-zero vertices by the previous
            // implementation; keep that shape for them, at the source vertex count, so their
            // indices stay in range.
            int vertexCount = anyUnconverted ? Mathf.Max(4, sourceMesh.vertexCount) : 4;

            Mesh mesh = new Mesh();
            mesh.name = sourceMesh.name + "_AndroidNoGeom";
            mesh.vertices = new Vector3[vertexCount];
            mesh.SetIndexBufferParams(totalIndexCount, maxIndexValue > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16);

            int indexStart = 0;
            mesh.subMeshCount = submeshes.Count;
            for (int i = 0; i < submeshes.Count; i++)
            {
                if (mesh.indexFormat == IndexFormat.UInt16)
                {
                    ushort[] shortIndices = new ushort[submeshIndices[i].Length];
                    for (int index = 0; index < shortIndices.Length; index++)
                    {
                        shortIndices[index] = (ushort)submeshIndices[i][index];
                    }
                    mesh.SetIndexBufferData(shortIndices, 0, indexStart, shortIndices.Length, NoValidation);
                }
                else
                {
                    mesh.SetIndexBufferData(submeshIndices[i], 0, indexStart, submeshIndices[i].Length, NoValidation);
                }
                mesh.SetSubMesh(i, new SubMeshDescriptor(indexStart, submeshIndices[i].Length, topologies[i]), NoValidation);
                indexStart += submeshIndices[i].Length;
            }

            mesh.bounds = sourceMesh.bounds;
            return mesh;
        }

        static string GetShaderName(Material material)
        {
            return material != null && material.shader != null ? material.shader.name : string.Empty;
        }

        static bool IsSplatShader(string shaderName)
        {
            return shaderName == GeomShaderName || shaderName == NoGeomShaderName || shaderName == FakeSrgbNoGeomShaderName;
        }

        static bool IsRemovedGrabPassShader(string shaderName)
        {
            return shaderName == ToSrgbShaderName || shaderName == ToLinearShaderName || shaderName == AlphaDepthMaskShaderName;
        }

        static int GetSubmeshSplatCount(Mesh sourceMesh, Material material, int subMesh)
        {
            int materialSplatCount = material != null && material.HasProperty("_SplatCount") ? material.GetInt("_SplatCount") : 0;
            if (materialSplatCount > 0)
            {
                return materialSplatCount;
            }
            return sourceMesh != null && subMesh >= 0 && subMesh < sourceMesh.subMeshCount && sourceMesh.GetTopology(subMesh) == MeshTopology.Points
                ? (int)sourceMesh.GetIndexCount(subMesh) * 32
                : 0;
        }

        internal static int[] CreateQuadIndices(int splatCount)
        {
            int[] indices = new int[splatCount * 6];
            for (int splatIndex = 0; splatIndex < splatCount; splatIndex++)
            {
                int vertex = splatIndex * 4;
                int index = splatIndex * 6;
                indices[index] = vertex;
                indices[index + 1] = vertex + 1;
                indices[index + 2] = vertex + 2;
                indices[index + 3] = vertex + 2;
                indices[index + 4] = vertex + 1;
                indices[index + 5] = vertex + 3;
            }
            return indices;
        }

        struct SubmeshConversion
        {
            public readonly int sourceSubmesh;
            public readonly int materialIndex;
            public readonly bool convertToQuads;

            public SubmeshConversion(int sourceSubmesh, int materialIndex, bool convertToQuads)
            {
                this.sourceSubmesh = sourceSubmesh;
                this.materialIndex = materialIndex;
                this.convertToQuads = convertToQuads;
            }
        }
    }
}
#endif
