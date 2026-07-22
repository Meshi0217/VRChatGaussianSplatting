#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace VRCLightVolumes 
{
    public static class LightVolumeOcclusionBaker {

        private static class ShaderConstants {
            public static readonly int CountID = Shader.PropertyToID("_Count");
            public static readonly int CountIndexID = Shader.PropertyToID("_CountIndex");
            public static readonly int TextureID = Shader.PropertyToID("_Texture");
            public static readonly int TextureWidthID = Shader.PropertyToID("_TextureWidth");
            public static readonly int TextureHeightID = Shader.PropertyToID("_TextureHeight");
            public static readonly int OcclusionID = Shader.PropertyToID("_Occlusion");
            public static readonly int OcclusionCountID = Shader.PropertyToID("_OcclusionCount");

            public const string UnlitShaderPath = "Packages/red.sim.lightvolumes/Shaders/Editor/OcclusionShader.shader";
            public const string ComputeShaderPath = "Packages/red.sim.lightvolumes/Shaders/Editor/CountUnoccludedPixels.compute";
            
            public const string CountKernel = "CountUnoccludedPixels";
            public const string RatioKernel = "ComputeOcclusionRatio";
        }
        
        private static float AreaLightFOV(
            Vector3 probePos,
            Vector3 lightPos,
            Vector3 right,
            Vector3 up,
            Vector2 size) {
            
            var probeToLight = lightPos - probePos;
            var sqDist = probeToLight.sqrMagnitude;
            var invSqDist = 1.0f / sqDist;

            // Calculate half-size edge vectors
            var halfRight = right * (size.x * 0.5f);
            var halfUp = up * (size.y * 0.5f);

            // Project both edges onto the eye-plane
            var projRight = halfRight - Vector3.Dot(halfRight, probeToLight) * invSqDist * probeToLight;
            var projUp = halfUp - Vector3.Dot(halfUp, probeToLight) * invSqDist * probeToLight;
            
            // Calculate FOV given the distance to the furthest corner
            float r2 = Mathf.Max((projRight + projUp).sqrMagnitude, (projRight - projUp).sqrMagnitude);
            return 2.0f * Mathf.Atan(Mathf.Sqrt(r2 * invSqDist)) * Mathf.Rad2Deg;
            
        }
        
        private static float[] ComputeOcclusionFactors(
            Vector3[] probePositions,
            int[] perProbeLightIndices,
            IList<PointLightVolume> pixelLights,
            float[] pixelLightRadii,
            Vector2[] pixelLightAreas,
            int resolution = 256, string infoString = "") {
            
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            // Check how many probes need occlusion so we can report progress.
            int totalProbesNeedingOcclusion = 0;
            for (int probeIdx = 0; probeIdx < probePositions.Length; probeIdx++) {
                for (int channel = 0; channel < 4; channel++) {
                    if (perProbeLightIndices[probeIdx * 4 + channel] >= 0) {
                        totalProbesNeedingOcclusion++;
                    }
                }
            }
            
            // Get required assets
            Mesh sphereMesh = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");
            Mesh quadMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            Shader occlusionShader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderConstants.UnlitShaderPath);
            Material whiteMat = new Material(occlusionShader) { color = Color.white };
            Material blackMat = new Material(occlusionShader) { color = Color.black };
            ComputeShader countShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderConstants.ComputeShaderPath);
            int countKernel = countShader.FindKernel(ShaderConstants.CountKernel);
            countShader.GetKernelThreadGroupSizes(countKernel, out uint countKernelX, out uint countKernelY, out _);
            int ratioKernel = countShader.FindKernel(ShaderConstants.RatioKernel);
            countShader.GetKernelThreadGroupSizes(ratioKernel, out uint ratioKernelX, out _, out _);

            // Create and initialize GPU resources
            RenderTexture tempRT = RenderTexture.GetTemporary(resolution, resolution, 16, RenderTextureFormat.R8);
            var nullRT = new RenderTargetIdentifier(BuiltinRenderTextureType.None);
            Debug.Assert(tempRT.width % countKernelX == 0 && tempRT.height % countKernelY == 0);
            using GraphicsBuffer occlusionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, probePositions.Length * 4, sizeof(float));
            float[] occlusionBufferInit = new float[occlusionBuffer.count];
            Array.Fill(occlusionBufferInit, 1.0f); // Initialize to 1.0f - unoccluded
            occlusionBuffer.SetData(occlusionBufferInit);
            using GraphicsBuffer countBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, probePositions.Length * 4 * 2, sizeof(uint));
            countBuffer.SetData(new uint[countBuffer.count]);

            // Find all GI contributors - these are the occluders
            MeshRenderer[] occluders = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(mr => GameObjectUtility.AreStaticEditorFlagsSet(mr.gameObject, StaticEditorFlags.ContributeGI)).ToArray();

            // Filter out occluders without valid mesh data
            List<MeshRenderer> validOccluders = new List<MeshRenderer>();
            List<Mesh> validOccluderMeshes = new List<Mesh>();
            List<Matrix4x4> validOccluderMatrices = new List<Matrix4x4>();
            List<Bounds> validOccluderBounds = new List<Bounds>();

            for (int i = 0; i < occluders.Length; i++) {
                MeshFilter meshFilter = occluders[i].GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null) {
                    validOccluders.Add(occluders[i]);
                    validOccluderMeshes.Add(meshFilter.sharedMesh);
                    validOccluderMatrices.Add(Matrix4x4.TRS(occluders[i].transform.position, occluders[i].transform.rotation, occluders[i].transform.lossyScale));
                    validOccluderBounds.Add(occluders[i].bounds);
                }
            }

            occluders = validOccluders.ToArray();
            Mesh[] occluderMeshes = validOccluderMeshes.ToArray();
            Matrix4x4[] occluderMatrices = validOccluderMatrices.ToArray();
            Bounds[] occluderBounds = validOccluderBounds.ToArray();

            // Set up command buffer with uniforms that don't change per probe
            using CommandBuffer cmd = new CommandBuffer();
            cmd.name = "Light Volume Occlusion Baking";
            cmd.SetComputeBufferParam(countShader, countKernel, ShaderConstants.CountID, countBuffer);
            cmd.SetComputeTextureParam(countShader, countKernel, ShaderConstants.TextureID, tempRT);
            cmd.SetComputeIntParam(countShader, ShaderConstants.TextureWidthID, tempRT.width);
            cmd.SetComputeIntParam(countShader, ShaderConstants.TextureHeightID, tempRT.height);
            cmd.SetComputeBufferParam(countShader, ratioKernel, ShaderConstants.CountID, countBuffer);
            cmd.SetComputeBufferParam(countShader, ratioKernel, ShaderConstants.OcclusionID, occlusionBuffer);
            
            // Setup some state for culling and progress reporting
            int probesProcessed = 0;
            Plane[] cullingPlanes = new Plane[6];
            List<int> occluderIndices = new List<int>(occluders.Length);
            
            // Rasterize the scene from each probes perspective, for each light that affects the probe.
            for (int probeIdx = 0; probeIdx < probePositions.Length; probeIdx++) {
                for (int channel = 0; channel < 4; channel++) {
                    // Check if we ran out of lights affecting the probe
                    int sampleIndex = probeIdx * 4 + channel;
                    int lightIndex = perProbeLightIndices[sampleIndex];
                    if (lightIndex < 0)
                        continue;

                    PointLightVolume pixelLight = pixelLights[lightIndex];
                    Vector3 lightPosition = pixelLight.transform.position;
                    float lightRadius = pixelLightRadii[lightIndex];
                    
                    Vector3 probePosition = probePositions[probeIdx];
                    float distanceToLight = Vector3.Distance(probePosition, lightPosition);
                    float farClip = distanceToLight + lightRadius + 0.001f; // Add a bit of wiggle room to avoid precision issues

                    // Area lights need special handling
                    bool isQuad = pixelLight.Type == PointLightVolume.LightType.AreaLight;
                    
                    // Calculate model, view, and projection matrices
                    Matrix4x4 lightToWorld;
                    float fov;
                    if (isQuad) {
                        lightToWorld = Matrix4x4.TRS(lightPosition, pixelLight.transform.rotation, pixelLight.transform.lossyScale); // Model, quad
                        fov = AreaLightFOV(probePosition, lightPosition, pixelLight.transform.right, pixelLight.transform.up, pixelLightAreas[lightIndex]);
                    } else {
                        lightToWorld = Matrix4x4.TRS(lightPosition, Quaternion.identity, 2 * lightRadius * Vector3.one); // Model, point
                        fov = 2.0f * Mathf.Asin(Mathf.Clamp(lightRadius / distanceToLight, -1, 1)) * Mathf.Rad2Deg;
                    }
                    fov = Mathf.Min(fov, 179);
                    Matrix4x4 probeToWorld = Matrix4x4.LookAt(probePosition, lightPosition, Vector3.up);
                    Matrix4x4 yFlipMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, 1, -1));
                    Matrix4x4 worldToProbe = yFlipMatrix * probeToWorld.inverse; // View
                    Matrix4x4 probeToClip = Matrix4x4.Perspective(fov, 1, 0.01f, farClip); // Projection

                    // Cull occluders
                    GeometryUtility.CalculateFrustumPlanes(probeToClip * worldToProbe, cullingPlanes);
                    occluderIndices.Clear();
                    for (int occluderIdx = 0; occluderIdx < occluders.Length; occluderIdx++) {
                        if (GeometryUtility.TestPlanesAABB(cullingPlanes, occluderBounds[occluderIdx])) {
                            occluderIndices.Add(occluderIdx);
                        }
                    }
                    // If no potential occluders - the probe is fully unoccluded, so skip doing any work.
                    if (occluderIndices.Count == 0)
                        continue;
                    
                    // Draw the light mesh first
                    cmd.SetViewProjectionMatrices(worldToProbe, probeToClip);
                    cmd.SetRenderTarget(tempRT);
                    cmd.ClearRenderTarget(true, true, Color.black);
                    cmd.DrawMesh(isQuad ? quadMesh : sphereMesh, lightToWorld, whiteMat);
                    cmd.SetRenderTarget(nullRT);
                    
                    // Count unocccluded pixels - this is the total area of the light
                    cmd.SetComputeIntParam(countShader, ShaderConstants.CountIndexID, sampleIndex * 2 + 0);
                    cmd.DispatchCompute(countShader, countKernel, tempRT.width / (int)countKernelX, tempRT.height / (int)countKernelY, 1);
                    
                    // Draw each occluder
                    cmd.SetRenderTarget(tempRT);
                    foreach (int occluderIdx in occluderIndices) {
                        cmd.DrawMesh(occluderMeshes[occluderIdx], occluderMatrices[occluderIdx], blackMat);
                    }
                    cmd.SetRenderTarget(nullRT);
                    
                    // Count unoccluded pixels again - this is the area of the light that is not occluded
                    cmd.SetComputeIntParam(countShader, ShaderConstants.CountIndexID, sampleIndex * 2 + 1);
                    cmd.DispatchCompute(countShader, countKernel, tempRT.width / (int)countKernelX, tempRT.height / (int)countKernelY, 1);
                    
                    // Report progress and flush the command buffer. We don't want to do this too often, as it will hurt bake performance.
                    // But doing flushing too rarely will cause CommandBuffer operations to become slow.
                    if (probesProcessed++ % 1024 == 0) {
                        float progress = (float)probesProcessed / totalProbesNeedingOcclusion;
                        string progressTitle = "Light Volume Shadow Mask Baking " + infoString;
                        string progressMessage = $"Dispatching probe bakes ({probesProcessed}/{totalProbesNeedingOcclusion})";
                        if (EditorUtility.DisplayCancelableProgressBar(progressTitle, progressMessage, progress)) {
                            EditorUtility.ClearProgressBar();
                            return occlusionBufferInit;
                        }
                        Graphics.ExecuteCommandBuffer(cmd);
                        cmd.Clear();
                    }
                }
            }
            
            // Compute the ratio of unoccluded pixels to total pixels
            cmd.SetComputeIntParam(countShader, ShaderConstants.OcclusionCountID, occlusionBuffer.count);
            cmd.DispatchCompute(countShader, ratioKernel, (occlusionBuffer.count + (int)ratioKernelX - 1) / (int)ratioKernelX,1,1);
            
            // Read back the occlusion data
            EditorUtility.DisplayProgressBar("Light Volume Shadow Mask Baking " + infoString, "Waiting for GPU to finish...", -1);
            float[] occlusion = new float[occlusionBuffer.count];
            cmd.RequestAsyncReadback(occlusionBuffer, readback => {
                using NativeArray<float> occlusionReadback = readback.GetData<float>();
                occlusionReadback.CopyTo(occlusion);
            });
            cmd.WaitAllAsyncReadbackRequests();
            Graphics.ExecuteCommandBuffer(cmd);
            
            EditorUtility.ClearProgressBar();
            
            // Cleanup
            RenderTexture.ReleaseTemporary(tempRT);
            Object.DestroyImmediate(blackMat);
            Object.DestroyImmediate(whiteMat);

            stopwatch.Stop();
            Debug.Log("[LightVolumeOcclusionBaker] Shadow mask baking took " + stopwatch.ElapsedMilliseconds + " ms for " + probePositions.Length + " probes.");
            
            return occlusion;
            
        }

        // Matches behavior of LV_ComputeAreaLightSquaredBoundingSphere() in LightVolumes.cginc
        private static float ComputeAreaLightBoundingRadius(float width, float height, Color color, float areaLightBrightnessCutoff) {

            float minSolidAngle = Mathf.Clamp(areaLightBrightnessCutoff / (Mathf.Max(color.r, Mathf.Max(color.g, color.b))), -Mathf.PI * 2f, Mathf.PI * 2);
            float A = width * height;
            float w2 = width * width;
            float h2 = height * height;
            float B = 0.25f * (w2 + h2);
            float t = Mathf.Tan(0.25f * minSolidAngle);
            float T = t * t;
            float TB = T * B;
            float discriminant = Mathf.Sqrt(TB * TB + 4.0f * T * A * A);
            float d2 = (discriminant - TB) * 0.125f / T;
            return Mathf.Sqrt(d2);
            
        }

        private static float ComputePointLightSquaredBoundingSphere(Color color, float intenisty, float size, float cutoff) {
            float L = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            return Mathf.Max(Mathf.PI * 2 * L * Mathf.Abs(intenisty) / (cutoff * cutoff) - 1, 0) * size * size;
        }

        // For each shadow casting light, precompute a few properties to avoid doing it repeatedly during the bake.
        // These are: The range of influence of the light, the (shadow) radius of the light, and the area of the light if it is an area light.
        public static void ComputeLightProperties(
            IList<PointLightVolume> shadowLights,
            Vector3Int volumeResolution,
            Vector3 volumeSize,
            float lightBrightnessCutoff,
            out float[] shadowLightInfluenceRadii,
            out float[] shadowLightRadii,
            out Vector2[] shadowLightArea) {
            
            // If light is too small, it may not get rasterized at all. To prevent that, clamp it to a voxel at minimum.
            float voxelRadius = Mathf.Max(volumeSize.x / volumeResolution.x, volumeSize.y / volumeResolution.y, volumeSize.z / volumeResolution.z) / 2.0f;
            
            // Precompute bounding sphere radius of each shadow casting light
            shadowLightInfluenceRadii = new float[shadowLights.Count];
            shadowLightRadii = new float[shadowLights.Count];
            shadowLightArea = new Vector2[shadowLights.Count];
            for (int lightIdx = 0; lightIdx < shadowLights.Count; lightIdx++) {
                // Don't care about non-shadow casting lights
                var light = shadowLights[lightIdx];
                if (!light.BakedShadows)
                    continue;
                
                float lightInfluenceRadius = light.Range;
                float lightRadius = light.BakedShadowRadius;
                if (light.Type == PointLightVolume.LightType.AreaLight) {
                    float width = Mathf.Max(Mathf.Abs(light.transform.lossyScale.x), 0.001f);
                    float height = Mathf.Max(Mathf.Abs(light.transform.lossyScale.y), 0.001f);
                    lightInfluenceRadius = ComputeAreaLightBoundingRadius(width, height, light.Color * light.Intensity, lightBrightnessCutoff);
                    lightRadius = Mathf.Sqrt(width * width + height * height) / 2.0f;
                    shadowLightArea[lightIdx] = new Vector2(width, height);
                } else if (light.Shape != PointLightVolume.LightShape.LUT || light.FalloffLUT == null) {
                    lightInfluenceRadius = Mathf.Sqrt(ComputePointLightSquaredBoundingSphere(light.Color, light.Intensity, light.LightSourceSize, lightBrightnessCutoff));
                }
                shadowLightInfluenceRadii[lightIdx] = lightInfluenceRadius;
                shadowLightRadii[lightIdx] = Mathf.Max(voxelRadius, lightRadius);
            }
            
        }
        
        // Compute shadowmask indices for each pixel light, in range [0; 3] (1 for each color channel).
        // Lights that don't use shadowmask will have -1 in the index.
        // Always returns 128 indices - the maximum number of pixel lights.
        public static sbyte[] ComputeShadowmaskIndices(IList<PointLightVolume> pixelLights, float[] shadowLightInfluenceRadii) {
            
            sbyte[] shadowmaskIndices = new sbyte[128];
            Array.Fill<sbyte>(shadowmaskIndices, -1);
            
            // Pre-allocate some stuff outside the loop to avoid many allocations.
            List<int> overlaps = new List<int>();
            bool[] availableIndices = new bool[4];
            Array.Fill(availableIndices, true);
            
            // Compute shadowmask indices for each pixel light
            for (int lightIdx = 0; lightIdx < 128; lightIdx++) {
                // No light at this index
                if (lightIdx >= pixelLights.Count)
                    continue;
                
                // Skip non-shadow casting lights
                var light = pixelLights[lightIdx];
                if (!light.BakedShadows)
                    continue;
                
                // Get the bounding sphere
                Vector3 lightPosition = light.transform.position;
                float lightRadius = shadowLightInfluenceRadii[lightIdx];

                // Check each other shadow casting light for overlaps
                overlaps.Clear();
                for (int otherLightIdx = 0; otherLightIdx < pixelLights.Count; otherLightIdx++) {
                    // Skip self and non-shadow casting lights
                    var otherLight = pixelLights[otherLightIdx];
                    if (light == otherLight || !otherLight.BakedShadows)
                        continue;
                    
                    // If the distance between the lights is less than the sum of their radii, they overlap
                    float distance = Vector3.Distance(lightPosition, otherLight.transform.position);
                    bool overlap = distance < (lightRadius + shadowLightInfluenceRadii[otherLightIdx]);

                    if (overlap)
                        overlaps.Add(otherLightIdx);
                }

                // For each overlapping light, remove its index
                Array.Fill(availableIndices, true);
                foreach (var otherLightIdx in overlaps) {
                    sbyte otherShadowmaskIndex = shadowmaskIndices[otherLightIdx];
                    
                    // No shadowmask index for this light
                    if (otherShadowmaskIndex < 0)
                        continue;
                    
                    availableIndices[otherShadowmaskIndex] = false;
                }
                
                // Pick the first available shadowmask index
                sbyte shadowmaskIndex = -1;
                for (sbyte bitIdx = 0; bitIdx < 4; bitIdx++) {
                    if (availableIndices[bitIdx]) {
                        shadowmaskIndex = bitIdx;
                        break;
                    }
                }
                shadowmaskIndices[lightIdx] = shadowmaskIndex;

                // Warn user if no shadowmask index is available
                if (shadowmaskIndex < 0) {
                    var go = light.gameObject;
                    Debug.LogWarning($"[LightVolumeOcclusionBaker] Failed to allocate a shadowmask for light '{go.name}'. There are too many other shadow casting lights nearby it!", go);
                }
            }

            return shadowmaskIndices;
            
        }

        // Gaussian blur the occlusion texture to smooth it out
        private static Color[] BlurOcclusionData(Vector3Int volumeResolution, Color[] occlusionColors) {
            
            // 1D gaussian kernel
            float[] kernel = { 1.0f, 2.0f, 1.0f };
            float kernelSum = 64.0f;
            
            Color[] occlusionColorsBlurred = new Color[occlusionColors.Length];
            for (int voxelZ = 0; voxelZ < volumeResolution.z; voxelZ++)
                for (int voxelY = 0; voxelY < volumeResolution.y; voxelY++)
                    for (int voxelX = 0; voxelX < volumeResolution.x; voxelX++) {
                        int centerIdx = voxelX + voxelY * volumeResolution.x + voxelZ * volumeResolution.x * volumeResolution.y;

                        Color sum = Color.clear;
                        float weightSum = 0.0f;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++) {
                                    int xx = voxelX + dx;
                                    int yy = voxelY + dy;
                                    int zz = voxelZ + dz;
                                    if (xx < 0 || yy < 0 || zz < 0 || xx >= volumeResolution.x || yy >= volumeResolution.y || zz >= volumeResolution.z) continue;
                                    
                                    float weight = (kernel[dx+1] * kernel[dy+1] * kernel[dz+1]) / kernelSum;
                                    weightSum += weight;
                                                    
                                    int nIdx = xx + yy * volumeResolution.x + zz * volumeResolution.x * volumeResolution.y;
                                    sum += occlusionColors[nIdx] * weight;
                                }
                        
                        occlusionColorsBlurred[centerIdx] = sum / weightSum;
                    }
            
            return occlusionColorsBlurred;
            
        }

        // Computes a 3D texture with up to 4 occlusion values for each probe position.
        // Shadowmask indices must be computed for each light beforehand.
        public static Texture3D ComputeOcclusionTexture(
            Vector3Int volumeResolution,
            Vector3[] probePositions,
            IList<PointLightVolume> shadowLights,
            float[] shadowLightInfluenceRadii,
            float[] shadowLightRadii,
            Vector2[] shadowLightArea,
            bool blurOcclusion, string infoString = "") {
            
            // For each probe, we need to find the lights that affect it. 4 entries per probe. -1 means no light affects this probe.
            int[] perProbeLights = new int[volumeResolution.x * volumeResolution.y * volumeResolution.z * 4];
            Array.Fill(perProbeLights, -1);
            bool[] slotFilled = new bool[4];
            bool anyLightsAffectVolume = false;
            for (int probeIdx = 0; probeIdx < probePositions.Length; probeIdx++) {
                Array.Fill(slotFilled, false);
                for (int lightIdx = 0; lightIdx < shadowLights.Count; lightIdx++) {
                    // Don't care about disabled lights
                    var light = shadowLights[lightIdx];
                    if (!light.enabled || !light.gameObject.activeInHierarchy)
                        continue;
                    
                    // Don't care about lights with no shadowmask index
                    sbyte shadowmaskIndex = light.PointLightVolumeInstance.ShadowmaskIndex;
                    if (shadowmaskIndex < 0)
                        continue;

                    // Check if the probe is in range of the light
                    Vector3 lightPosition = light.transform.position;
                    float lightRadius = shadowLightInfluenceRadii[lightIdx];
                    if (Vector3.Distance(probePositions[probeIdx], lightPosition) > lightRadius)
                        continue;
                    
                    // Area lights only affect probes in front of them
                    if (light.Type == PointLightVolume.LightType.AreaLight && Vector3.Dot(light.transform.forward, (probePositions[probeIdx] - lightPosition).normalized) < 0.0f)
                        continue;
                    
                    // Assign the light to the probe's shadowmask slot
                    perProbeLights[probeIdx * 4 + Mathf.Clamp(shadowmaskIndex, 0, 3)] = lightIdx;
                    anyLightsAffectVolume = true;
                    
                    // If we already filled all slots, we can stop
                    slotFilled[shadowmaskIndex] = true;
                    if (slotFilled[0] && slotFilled[1] && slotFilled[2] && slotFilled[3])
                        break;
                }
            }

            // If no lights affect the volume, no need to bake. Just return null texture.
            if (!anyLightsAffectVolume)
                return null;
            
            // Calculate occlusion factors for each probe position and populate the texture
            float[] occlusionFactors = ComputeOcclusionFactors(probePositions, perProbeLights, shadowLights, shadowLightRadii, shadowLightArea, 256, infoString);
            Color[] occlusionColors = new Color[volumeResolution.x * volumeResolution.y * volumeResolution.z];
            for (int texelIdx = 0; texelIdx < occlusionColors.Length; texelIdx++) {
                occlusionColors[texelIdx] = new Color(
                    occlusionFactors[texelIdx * 4 + 0],
                    occlusionFactors[texelIdx * 4 + 1],
                    occlusionFactors[texelIdx * 4 + 2],
                    occlusionFactors[texelIdx * 4 + 3]);
            }
            
            // If requested, blur occlusion to soften the shadows
            if (blurOcclusion)
                occlusionColors = BlurOcclusionData(volumeResolution, occlusionColors);

            TextureFormat format = TextureFormat.RGBAHalf;
            Texture3D tex = new Texture3D(volumeResolution.x, volumeResolution.y, volumeResolution.z, format, false) {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            tex.SetPixels(occlusionColors);
            tex.Apply(updateMipmaps: false);
            
            return tex;
        }
    }
}
#endif
