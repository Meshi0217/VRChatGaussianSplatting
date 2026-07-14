using UnityEngine;
using UnityEngine.EventSystems;

namespace GaussianSplatting
{
    // A 3D object only receives IPointerClickHandler callbacks when the scene has an EventSystem,
    // the rendering camera carries a PhysicsRaycaster, and the object itself has a Collider. When
    // any of those is missing the toggle just silently does nothing, which is indistinguishable
    // from it being broken, so name the missing piece instead.
    public static class GaussianSplatClickTarget
    {
        public static void WarnIfNotClickable(Component target)
        {
            if (target == null)
            {
                return;
            }

            string what = target.GetType().Name + " on '" + target.name + "'";

            if (target.GetComponent<Collider>() == null)
            {
                Debug.LogWarning(what + " has no Collider, so screen clicks cannot hit it.", target);
            }

            if (Object.FindFirstObjectByType<EventSystem>() == null)
            {
                Debug.LogWarning(what + " needs an EventSystem in the scene to receive clicks.", target);
            }

            if (Object.FindFirstObjectByType<PhysicsRaycaster>() == null)
            {
                Debug.LogWarning(what + " needs a PhysicsRaycaster on the rendering camera to receive clicks.", target);
            }
        }
    }
}
