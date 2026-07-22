#if UNITY_EDITOR
using UnityEditor;

[InitializeOnLoad]
public static class LightVolumesDefineManager {
    const string DEFINE_SYMBOL = "VRC_LIGHT_VOLUMES";

    static LightVolumesDefineManager() {
        var targetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
        var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(targetGroup);
        if (!defines.Contains(DEFINE_SYMBOL)) {
            defines += ";" + DEFINE_SYMBOL;
            PlayerSettings.SetScriptingDefineSymbolsForGroup(targetGroup, defines);
        }
    }
}
#endif