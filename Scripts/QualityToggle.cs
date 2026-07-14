
using UnityEngine;
using UnityEngine.EventSystems;

namespace GaussianSplatting
{

// Clicking this object applies a quality preset to the scene renderer. In VRChat this was an
// Interact() gaze-press; here it is a normal screen click, which needs a Collider on this object
// and a PhysicsRaycaster on the rendering camera (GaussianSplatUiBuilder wires both up).
// The same entry point works from a uGUI Button, since Apply() takes no arguments.
public class QualityToggle : MonoBehaviour, IPointerClickHandler
{
    [Range(0.0f, 2.0f)] [SerializeField] public float gaussianScale = 1.0f;
    [Range(0.0f, 1.0f)] [SerializeField] public float alphaCutoff = 0.03f;
    [Tooltip("The Gaussian Splat Renderer that will use the enabled object as the splat object.")]
    public GaussianSplatRenderer gaussianSplatRenderer;

    void Start()
    {
        GaussianSplatClickTarget.WarnIfNotClickable(this);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        Apply();
    }

    public void Apply()
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
