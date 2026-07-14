
using UnityEngine;
using UnityEngine.EventSystems;

namespace GaussianSplatting
{

// Clicking this object makes its target splat the only visible one. In VRChat this was an
// Interact() gaze-press; here it is a normal screen click, which needs a Collider on this object
// and a PhysicsRaycaster on the rendering camera (GaussianSplatUiBuilder wires both up).
// The same entry point works from a uGUI Button, since SelectObject() takes no arguments.
public class TurnOnToggle : MonoBehaviour, IPointerClickHandler
{
    [Tooltip("The Gaussian Splat Object that will be enabled when this toggle is activated.")]
    public GameObject targetObject;
    [Tooltip("The automatically discovered Gaussian Splat Object index that will be enabled when this toggle is activated.")]
    public int enableObjectIndex = 0;

    // Sorted by instance id so enableObjectIndex refers to a stable object across calls.
    static GaussianSplatObject[] FindSceneSplatObjects()
    {
        return Object.FindObjectsByType<GaussianSplatObject>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID);
    }

    void Start()
    {
        GaussianSplatClickTarget.WarnIfNotClickable(this);
    }

    GameObject GetTargetObject()
    {
        if (targetObject != null)
        {
            return targetObject;
        }

        GaussianSplatObject[] sceneSplatObjects = FindSceneSplatObjects();
        if (enableObjectIndex < 0 || enableObjectIndex >= sceneSplatObjects.Length || sceneSplatObjects[enableObjectIndex] == null)
        {
            return null;
        }

        return sceneSplatObjects[enableObjectIndex].gameObject;
    }

    void SelectOnlyTargetObject(GameObject selectedObject)
    {
        if (selectedObject == null)
        {
            return;
        }

        GaussianSplatObject[] sceneSplatObjects = FindSceneSplatObjects();
        for (int i = 0; i < sceneSplatObjects.Length; i++)
        {
            GaussianSplatObject splatObject = sceneSplatObjects[i];
            if (splatObject != null)
            {
                splatObject.gameObject.SetActive(false);
            }
        }

        selectedObject.SetActive(true);
        GaussianSplatObject selectedSplatObject = selectedObject.GetComponent<GaussianSplatObject>();
        if (selectedSplatObject != null)
        {
            selectedSplatObject.NotifyRendererEnabled();
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        SelectObject();
    }

    public void SelectObject()
    {
        SelectOnlyTargetObject(GetTargetObject());
    }
}

}
