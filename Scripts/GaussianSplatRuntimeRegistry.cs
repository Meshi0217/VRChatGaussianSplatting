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

        static readonly SortedSet<int> _maskQueues = new SortedSet<int>();
        static int[] _publishedMaskQueues = new int[0];

        /// <summary>Render queues holding an AlphaDepthMask pass, ascending. Empty is the common case.</summary>
        public static int[] MaskRenderQueues { get { return _publishedMaskQueues; } }

        /// <summary>False when no splat is bound, so the feature can skip its colour copies entirely.</summary>
        public static bool HasActiveSplats { get; private set; }

        public static void BeginBinding()
        {
            _maskQueues.Clear();
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
                if (material != null && material.shader != null && material.shader.name == MaskShaderName)
                {
                    _maskQueues.Add(material.renderQueue);
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
