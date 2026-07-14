
using UnityEngine;

namespace GaussianSplatting
{

public class QualityToggle : MonoBehaviour
{
    [Range(0.0f, 2.0f)] [SerializeField] public float gaussianScale = 1.0f;
    [Range(0.0f, 1.0f)] [SerializeField] public float alphaCutoff = 0.03f;
    [Tooltip("The Gaussian Splat Renderer that will use the enabled object as the splat object.")]
    public GaussianSplatRenderer gaussianSplatRenderer;

    public void Interact()
    {
        if (gaussianSplatRenderer == null)
        {
            gaussianSplatRenderer = Object.FindFirstObjectByType<GaussianSplatRenderer>();
        }

        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.overrideMaterialProperties = true;
        gaussianSplatRenderer.gaussianScale = gaussianScale;
        gaussianSplatRenderer.alphaCutoff = alphaCutoff;
    }
}

}
