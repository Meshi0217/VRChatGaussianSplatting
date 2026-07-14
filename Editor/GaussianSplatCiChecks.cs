#if UNITY_EDITOR
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Shaders compile lazily, so a clean C# batchmode run says nothing about them. Run this with
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod GaussianSplatting.Editor.GaussianSplatCiChecks.CheckShaders
    /// (without -nographics -- shader compilation needs a graphics device).
    /// </summary>
    public static class GaussianSplatCiChecks
    {
        const string SearchFolder = "Assets/VRChatGaussianSplatting";

        public static void CheckShaders()
        {
            string[] guids = AssetDatabase.FindAssets("t:Shader", new[] { SearchFolder });
            StringBuilder failures = new StringBuilder();
            int checkedCount = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null)
                {
                    continue;
                }

                checkedCount++;
                int messageCount = ShaderUtil.GetShaderMessageCount(shader);
                if (messageCount == 0)
                {
                    continue;
                }

                foreach (ShaderMessage message in ShaderUtil.GetShaderMessages(shader))
                {
                    if (message.severity != ShaderCompilerMessageSeverity.Error)
                    {
                        continue;
                    }
                    failures.AppendLine("SHADER ERROR " + path + " (" + message.file + ":" + message.line + "): " + message.message);
                }
            }

            Debug.Log("GaussianSplatCiChecks: inspected " + checkedCount + " shaders.");
            if (failures.Length > 0)
            {
                Debug.LogError("GaussianSplatCiChecks FAILED\n" + failures);
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log("GaussianSplatCiChecks: no shader errors.");
            EditorApplication.Exit(0);
        }
    }
}
#endif
