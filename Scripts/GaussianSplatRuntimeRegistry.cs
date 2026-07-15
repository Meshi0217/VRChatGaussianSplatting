using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>
    /// The bridge between the renderer and GaussianSplatRendererFeature.
    ///
    /// The feature has to copy the colour target immediately before each AlphaDepthMask pass, which
    /// means it needs to know at which render queues those passes sit so it can split the splat draw
    /// around them. Only the renderer knows: the importer assigns each material in a splat's material
    /// array a queue of startRenderQueue + index, so the mask queues depend on how the splat was
    /// imported. The renderer republishes them whenever it rebinds its sort target.
    /// </summary>
    public static class GaussianSplatRuntimeRegistry
    {
        const string MaskShaderName = "VRChatGaussianSplatting/AlphaDepthMask";
        const string ToSrgbShaderName = "VRChatGaussianSplatting/ToSRGB";
        const string ToLinearShaderName = "VRChatGaussianSplatting/ToLinear";

        static readonly SortedSet<int> _maskQueues = new SortedSet<int>();
        static int[] _publishedMaskQueues = new int[0];
        static bool _usesGrabPasses;

        /// <summary>Render queues holding an AlphaDepthMask pass, ascending. Empty is the common case.</summary>
        public static int[] MaskRenderQueues { get { return _publishedMaskQueues; } }

        /// <summary>False when no splat is bound, so the feature can skip its colour copies entirely.</summary>
        public static bool HasActiveSplats { get; private set; }

        /// <summary>
        /// True when a splat is imported with sRGB colour correction, i.e. when its material array
        /// contains ToSRGB / ToLinear / AlphaDepthMask passes that read a copy of the camera colour.
        /// Android's fake-sRGB conversion and desktop no-sRGB imports drop those passes, leaving a
        /// plain back-to-front splat draw -- so the feature can skip the camera-colour copies, the
        /// empty ToSRGB/ToLinear passes and the forced intermediate texture entirely.
        /// </summary>
        public static bool UsesGrabPasses { get { return _usesGrabPasses; } }

        public static void BeginBinding()
        {
            _maskQueues.Clear();
            _usesGrabPasses = false;
        }

        public static void RegisterMaterials(Material[] materials)
        {
            if (materials == null)
            {
                return;
            }
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null || material.shader == null)
                {
                    continue;
                }
                string shaderName = material.shader.name;
                if (shaderName == MaskShaderName)
                {
                    _maskQueues.Add(material.renderQueue);
                    _usesGrabPasses = true;
                }
                else if (shaderName == ToSrgbShaderName || shaderName == ToLinearShaderName)
                {
                    _usesGrabPasses = true;
                }
            }
        }

        public static void EndBinding()
        {
            HasActiveSplats = true;
            if (Matches(_publishedMaskQueues, _maskQueues))
            {
                return;
            }
            int[] published = new int[_maskQueues.Count];
            _maskQueues.CopyTo(published);
            _publishedMaskQueues = published;
        }

        public static void Clear()
        {
            HasActiveSplats = false;
            _usesGrabPasses = false;
            _maskQueues.Clear();
            _publishedMaskQueues = new int[0];
        }

        static bool Matches(int[] published, SortedSet<int> current)
        {
            if (published.Length != current.Count)
            {
                return false;
            }
            int index = 0;
            foreach (int queue in current)
            {
                if (published[index++] != queue)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
