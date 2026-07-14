#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Strips the leftover VRChat components from prefabs and scenes that were authored against the
    /// VRChat SDK.
    ///
    /// GaussianSplatObject and friends were always serialized as ordinary MonoBehaviours; the
    /// UdonBehaviour sat beside them as a separate component. With the SDK gone those UdonBehaviours
    /// deserialize as missing scripts, so removing them is all that is needed -- the splat data,
    /// meshes, textures and materials are untouched.
    /// </summary>
    public static class GaussianSplatVrcCleanup
    {
        const string SearchFolder = "Assets/VRChatGaussianSplatting";

        [MenuItem("Gaussian Splatting/Cleanup/Strip VRChat Components From Prefabs")]
        public static void StripPrefabs()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { SearchFolder });
            int changedPrefabs = 0;
            int removedComponents = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                if (root == null)
                {
                    continue;
                }

                int removed = RemoveMissingScripts(root);
                if (removed > 0)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    changedPrefabs++;
                    removedComponents += removed;
                    Debug.Log("Stripped " + removed + " missing script(s) from " + path);
                }

                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            Debug.Log("GaussianSplatVrcCleanup: removed " + removedComponents + " component(s) from " + changedPrefabs + " prefab(s).");
        }

        [MenuItem("Gaussian Splatting/Cleanup/Strip VRChat Components From Open Scene")]
        public static void StripOpenScene()
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            int removed = RemoveMissingScriptsInScene(scene);
            if (removed > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            Debug.Log("GaussianSplatVrcCleanup: removed " + removed + " component(s) from scene " + scene.path + ".");
        }

        static int RemoveMissingScriptsInScene(Scene scene)
        {
            int removed = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                removed += RemoveMissingScripts(root);
            }
            return removed;
        }

        static int RemoveMissingScripts(GameObject root)
        {
            int removed = 0;
            List<Transform> transforms = new List<Transform>(root.GetComponentsInChildren<Transform>(true));
            foreach (Transform transform in transforms)
            {
                if (transform == null)
                {
                    continue;
                }
                removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
            }
            return removed;
        }
    }
}
#endif
