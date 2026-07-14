using System.Collections.Generic;
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

            class PassData
            {
                public TextureHandle cameraColor;
                public TextureHandle cameraDepth;
                public TextureHandle linearBackground;
                public TextureHandle srgbBackground;
                public TextureHandle grabTexture;
                public RendererListHandle toSrgb;
                public RendererListHandle toLinear;

                // Render Graph pools PassData objects and hands them back without resetting fields,
                // so these have to be cleared on every record. Leaving them to accumulate meant the
                // second camera of a frame (Scene view alongside Game view) inherited the first
                // camera's renderer lists and tried to execute them again:
                //   "Trying to execute a RendererList that was already executed during this frame."
                public readonly List<RendererListHandle> splatSegments = new List<RendererListHandle>();
                public readonly List<RendererListHandle> alphaMasks = new List<RendererListHandle>();

                public void Reset()
                {
                    splatSegments.Clear();
                    alphaMasks.Clear();
                    // Only assigned when this splat has alpha-mask passes; must not survive a record
                    // where it does not.
                    grabTexture = TextureHandle.nullHandle;
                }
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

                // Reading back from the backbuffer is not allowed, and the copies below are reads.
                if (resourceData.isActiveTargetBackBuffer)
                {
                    return;
                }

                RenderTextureDescriptor copyDesc = cameraData.cameraTargetDescriptor;
                copyDesc.depthBufferBits = 0;
                copyDesc.msaaSamples = 1;
                copyDesc.useMipMap = false;
                copyDesc.autoGenerateMips = false;

                using (var builder = renderGraph.AddUnsafePass<PassData>("Gaussian Splats", out PassData passData))
                {
                    passData.Reset();
                    passData.cameraColor = resourceData.activeColorTexture;
                    passData.cameraDepth = resourceData.activeDepthTexture;
                    builder.UseTexture(passData.cameraColor, AccessFlags.ReadWrite);
                    builder.UseTexture(passData.cameraDepth, AccessFlags.ReadWrite);

                    passData.linearBackground = UniversalRenderer.CreateRenderGraphTexture(renderGraph, copyDesc, "_GS_LinearBackground", false);
                    passData.srgbBackground = UniversalRenderer.CreateRenderGraphTexture(renderGraph, copyDesc, "_GS_SRGBBackground", false);
                    builder.UseTexture(passData.linearBackground, AccessFlags.ReadWrite);
                    builder.UseTexture(passData.srgbBackground, AccessFlags.ReadWrite);

                    int[] maskQueues = GaussianSplatRuntimeRegistry.MaskRenderQueues;
                    if (maskQueues.Length > 0)
                    {
                        passData.grabTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, copyDesc, "_GS_GrabTexture", false);
                        builder.UseTexture(passData.grabTexture, AccessFlags.ReadWrite);
                    }

                    passData.toSrgb = CreateList(renderGraph, renderingData, cameraData, lightData, ToSrgbTag, MinQueue, MaxQueue);
                    passData.toLinear = CreateList(renderGraph, renderingData, cameraData, lightData, ToLinearTag, MinQueue, MaxQueue);
                    builder.UseRendererList(passData.toSrgb);
                    builder.UseRendererList(passData.toLinear);

                    // Split the splat draw around each mask queue so a colour copy can land in between.
                    int segmentStart = MinQueue;
                    for (int i = 0; i < maskQueues.Length; i++)
                    {
                        int maskQueue = maskQueues[i];
                        if (maskQueue < segmentStart || maskQueue > MaxQueue)
                        {
                            continue;
                        }
                        RendererListHandle segment = CreateList(renderGraph, renderingData, cameraData, lightData, SplatTag, segmentStart, maskQueue - 1);
                        RendererListHandle mask = CreateList(renderGraph, renderingData, cameraData, lightData, AlphaMaskTag, maskQueue, maskQueue);
                        builder.UseRendererList(segment);
                        builder.UseRendererList(mask);
                        passData.splatSegments.Add(segment);
                        passData.alphaMasks.Add(mask);
                        segmentStart = maskQueue + 1;
                    }

                    RendererListHandle tail = CreateList(renderGraph, renderingData, cameraData, lightData, SplatTag, segmentStart, MaxQueue);
                    builder.UseRendererList(tail);
                    passData.splatSegments.Add(tail);

                    // SetGlobalTexture below is global state, and an empty scene must not cull the pass
                    // away while a splat is still bound.
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => Execute(data, context));
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

            static void Execute(PassData data, UnsafeGraphContext context)
            {
                CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

                Blitter.BlitCameraTexture(cmd, data.cameraColor, data.linearBackground);
                cmd.SetGlobalTexture(LinearBackgroundId, data.linearBackground);

                context.cmd.SetRenderTarget(data.cameraColor, data.cameraDepth);
                context.cmd.DrawRendererList(data.toSrgb);

                for (int i = 0; i < data.splatSegments.Count; i++)
                {
                    context.cmd.DrawRendererList(data.splatSegments[i]);

                    if (i < data.alphaMasks.Count)
                    {
                        Blitter.BlitCameraTexture(cmd, data.cameraColor, data.grabTexture);
                        cmd.SetGlobalTexture(GrabTextureId, data.grabTexture);
                        context.cmd.SetRenderTarget(data.cameraColor, data.cameraDepth);
                        context.cmd.DrawRendererList(data.alphaMasks[i]);
                    }
                }

                Blitter.BlitCameraTexture(cmd, data.cameraColor, data.srgbBackground);
                cmd.SetGlobalTexture(SrgbBackgroundId, data.srgbBackground);

                context.cmd.SetRenderTarget(data.cameraColor, data.cameraDepth);
                context.cmd.DrawRendererList(data.toLinear);
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
            renderer.EnqueuePass(_pass);
        }
    }
}
