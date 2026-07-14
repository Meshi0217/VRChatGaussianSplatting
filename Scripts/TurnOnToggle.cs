
using UnityEngine;

namespace GaussianSplatting
{

public class TurnOnToggle : MonoBehaviour
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

    public void SelectObject()
    {
        GameObject targetObject = GetTargetObject();
        SelectOnlyTargetObject(targetObject);
    }

    public void Interact()
    {
        SelectObject();
    }

    public void OnTrigger()
    {
        SelectObject();
    }
}

}
