using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace GaussianSplatting
{
    /// <summary>
    /// Draws the Gaussian splat sequence, replacing the GrabPass chain the shaders used under the
    /// built-in pipeline.
    ///
    /// URP has no GrabPass, and it draws the entire transparent queue in one DrawObjectsPass, so a
    /// renderer feature cannot be injected between two render queues. The splat shaders therefore
    /// carry their own LightMode tags, which keeps them out of URP's transparent pass entirely and
    /// lets this feature own their ordering:
    ///
    ///   copy colour -> _GS_LinearBackground   (what GrabPass "_LinearBackground" did)
    ///   ToSRGB        rewrites the target in gamma space with alpha 0
    ///   splats        accumulate front-to-back with Blend OneMinusDstAlpha One
    ///     copy colour -> _GS_GrabTexture      (before each AlphaDepthMask, which needs dst alpha)
    ///     AlphaDepthMask stencils out fully covered pixels
    ///   copy colour -> _GS_SRGBBackground
    ///   ToLinear      subtracts the background back out and returns to linear space
    ///
    /// _GS_LinearBackground stays bound through to ToLinear, which reads it as well -- that mirrors
    /// GrabPass's global texture lifetime and is why the copy is not folded into ToSRGB.
    ///
    /// Each step is its own raster pass. An unsafe pass with manual SetRenderTarget was tried first,
    /// and it broke single-pass stereo: the manual target binding and the Blitter copies only touched
    /// one eye slice, so VR (MockHMD and Quest alike) came out black. Raster passes let URP set up XR
    /// single-pass for every draw and copy, at the cost of splitting what was one pass into several --
    /// which is fine here, since each step reads and writes different textures anyway (the only reason
    /// the unsafe pass existed was to read and write the camera colour in one pass).
    ///
    /// Requires an alpha channel in the camera colour target. URP's default (HDR on, 32-bit) picks
    /// B10G11R11_UFloatPack32, which has none, and the whole front-to-back scheme then fails
    /// silently. GaussianSplatUrpSetup checks for this.
    /// </summary>
    [DisallowMultipleRendererFeature("Gaussian Splat")]
    public class GaussianSplatRendererFeature : ScriptableRendererFeature
    {
        class SplatPass : ScriptableRenderPass
        {
            static readonly ShaderTagId SplatTag = new ShaderTagId("GaussianSplat");
            static readonly ShaderTagId ToSrgbTag = new ShaderTagId("GaussianSplatToSRGB");
            static readonly ShaderTagId AlphaMaskTag = new ShaderTagId("GaussianSplatAlphaMask");
            static readonly ShaderTagId ToLinearTag = new ShaderTagId("GaussianSplatToLinear");

            static readonly int LinearBackgroundId = Shader.PropertyToID("_GS_LinearBackground");
            static readonly int SrgbBackgroundId = Shader.PropertyToID("_GS_SRGBBackground");
            static readonly int GrabTextureId = Shader.PropertyToID("_GS_GrabTexture");

            // Transparent+499 .. Transparent+600 is where the importer puts everything, but stay
            // permissive: startRenderQueue is user-editable.
            const int MinQueue = 2501;
            const int MaxQueue = 5000;

            class CopyPassData
            {
                public TextureHandle source;
            }

            class DrawPassData
            {
                public RendererListHandle list;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!GaussianSplatRuntimeRegistry.HasActiveSplats)
                {
                    return;
                }

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();

                TextureHandle cameraColor = resourceData.activeColorTexture;
                TextureHandle cameraDepth = resourceData.activeDepthTexture;

                // No sRGB correction (Android's fake-sRGB conversion, or a no-sRGB import): the splats
                // are a plain back-to-front draw with no grab passes, so skip the camera-colour copies,
                // the empty ToSRGB/ToLinear passes and the intermediate texture. Just draw the splats.
                if (!GaussianSplatRuntimeRegistry.UsesGrabPasses)
                {
                    DrawTag(renderGraph, cameraColor, cameraDepth, renderingData, cameraData, lightData, SplatTag, MinQueue, MaxQueue, "GS Splats");
                    return;
                }

                // The copies below sample the camera colour, which is not allowed against the backbuffer.
                if (resourceData.isActiveTargetBackBuffer)
                {
                    return;
                }

                RenderTextureDescriptor copyDesc = cameraData.cameraTargetDescriptor;
                copyDesc.depthBufferBits = 0;
                copyDesc.msaaSamples = 1;
                copyDesc.useMipMap = false;
                copyDesc.autoGenerateMips = false;

                CopyToGlobal(renderGraph, cameraColor, copyDesc, "_GS_LinearBackground", LinearBackgroundId);
                DrawTag(renderGraph, cameraColor, cameraDepth, renderingData, cameraData, lightData, ToSrgbTag, MinQueue, MaxQueue, "GS ToSRGB");

                int[] maskQueues = GaussianSplatRuntimeRegistry.MaskRenderQueues;
                int segmentStart = MinQueue;
                for (int i = 0; i < maskQueues.Length; i++)
                {
                    int maskQueue = maskQueues[i];
                    if (maskQueue < segmentStart || maskQueue > MaxQueue)
                    {
                        continue;
                    }
                    DrawTag(renderGraph, cameraColor, cameraDepth, renderingData, cameraData, lightData, SplatTag, segmentStart, maskQueue - 1, "GS Splats");
                    CopyToGlobal(renderGraph, cameraColor, copyDesc, "_GS_GrabTexture", GrabTextureId);
                    DrawTag(renderGraph, cameraColor, cameraDepth, renderingData, cameraData, lightData, AlphaMaskTag, maskQueue, maskQueue, "GS AlphaMask");
                    segmentStart = maskQueue + 1;
                }
                DrawTag(renderGraph, cameraColor, cameraDepth, renderingData, cameraData, lightData, SplatTag, segmentStart, MaxQueue, "GS Splats");

                CopyToGlobal(renderGraph, cameraColor, copyDesc, "_GS_SRGBBackground", SrgbBackgroundId);
                DrawTag(renderGraph, cameraColor, cameraDepth, renderingData, cameraData, lightData, ToLinearTag, MinQueue, MaxQueue, "GS ToLinear");
            }

            // Copies the camera colour into a new texture and binds it as a global for the passes that
            // follow. As a raster pass the blit runs under XR single-pass, so it copies both eye slices.
            static void CopyToGlobal(RenderGraph renderGraph, TextureHandle cameraColor, RenderTextureDescriptor desc, string name, int propertyId)
            {
                TextureHandle copy = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name, false);
                using (var builder = renderGraph.AddRasterRenderPass<CopyPassData>(name, out CopyPassData passData))
                {
                    passData.source = cameraColor;
                    builder.UseTexture(cameraColor, AccessFlags.Read);
                    builder.SetRenderAttachment(copy, 0, AccessFlags.Write);
                    builder.SetGlobalTextureAfterPass(copy, propertyId);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((CopyPassData data, RasterGraphContext context) =>
                        Blitter.BlitTexture(context.cmd, data.source, new Vector4(1f, 1f, 0f, 0f), 0f, false));
                }
            }

            static void DrawTag(
                RenderGraph renderGraph,
                TextureHandle cameraColor,
                TextureHandle cameraDepth,
                UniversalRenderingData renderingData,
                UniversalCameraData cameraData,
                UniversalLightData lightData,
                ShaderTagId tag,
                int lowerQueue,
                int upperQueue,
                string name)
            {
                RendererListHandle list = CreateList(renderGraph, renderingData, cameraData, lightData, tag, lowerQueue, upperQueue);
                using (var builder = renderGraph.AddRasterRenderPass<DrawPassData>(name, out DrawPassData passData))
                {
                    passData.list = list;
                    builder.UseRendererList(list);
                    // Write, not clear: the camera colour and depth carry the scene forward, which is
                    // what the splats blend against and what the mask pass tests. Depth is Write so the
                    // mask pass can put its stencil there.
                    builder.SetRenderAttachment(cameraColor, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(cameraDepth, AccessFlags.Write);
                    // The splat and fullscreen shaders sample _GS_*Background as globals bound by
                    // CopyToGlobal above.
                    builder.UseAllGlobalTextures(true);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((DrawPassData data, RasterGraphContext context) =>
                        context.cmd.DrawRendererList(data.list));
                }
            }

            static RendererListHandle CreateList(
                RenderGraph renderGraph,
                UniversalRenderingData renderingData,
                UniversalCameraData cameraData,
                UniversalLightData lightData,
                ShaderTagId tag,
                int lowerQueue,
                int upperQueue)
            {
                // Strict render-queue order: the whole scheme depends on ToSRGB, the splat chunks, the
                // masks and ToLinear running in exactly the order the importer numbered them. Distance
                // sorting would shuffle them.
                DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(tag, renderingData, cameraData, lightData, SortingCriteria.RenderQueue);
                FilteringSettings filteringSettings = new FilteringSettings(new RenderQueueRange(lowerQueue, upperQueue));
                return renderGraph.CreateRendererList(new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings));
            }
        }

        SplatPass _pass;

        public override void Create()
        {
            _pass = new SplatPass
            {
                // The splats are transparent and must composite over everything URP has drawn, but
                // they own their own ordering, so they run after URP's transparent pass rather than
                // inside it.
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents,
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Only the grab-pass (sRGB) path samples the camera colour, and only that path needs an
            // intermediate texture: URP renders straight to the backbuffer in Forward with no
            // post/MSAA/HDR (the mobile tier), where activeColorTexture is the backbuffer and the
            // copies cannot run. Requesting it unconditionally cost every mobile frame an intermediate
            // texture even for the back-to-front path that never reads the colour target. The registry
            // is already up to date here -- the sort runs from beginCameraRendering, before this.
            // URP reads requiresIntermediateTexture off the enqueued passes, so setting it here works.
            _pass.requiresIntermediateTexture = GaussianSplatRuntimeRegistry.UsesGrabPasses;
            renderer.EnqueuePass(_pass);
        }
    }
}
