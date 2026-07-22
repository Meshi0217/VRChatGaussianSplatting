
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRCLightVolumes {
	
    [InitializeOnLoad]
    sealed class SceneLightVolumeL0Mode
    {
        static class Cache
        {
            public static readonly Shader lightVolumeL0Shader = Shader.Find("Hidden/LV_DebugDisplayL0");
        }

        static readonly SceneView.CameraMode lightVolumeL0Mode;

        static readonly HashSet<SceneView> setupSceneViews = new HashSet<SceneView>();

        static SceneLightVolumeL0Mode()
        {
            lightVolumeL0Mode = SceneView.AddCameraMode("VRC Light Volumes L0", "Light Volumes Debug");

            SceneView.beforeSceneGui += view =>
            {
                if (setupSceneViews.Add(view))
                {
                    view.onCameraModeChanged += cameraMode =>
                    {
                        if (cameraMode == lightVolumeL0Mode)
                        {
                            view.SetSceneViewShaderReplace(Cache.lightVolumeL0Shader, string.Empty);
                        }
                        else if (view.cameraMode.drawMode == DrawCameraMode.Textured)
                        {
                            view.SetSceneViewShaderReplace(null, string.Empty);
                        }
                    };
                }
            };
        }
    }
    
    [InitializeOnLoad]
    sealed class SceneLightVolumeL1Mode
    {
        static class Cache
        {
            public static readonly Shader lightVolumeL1Shader = Shader.Find("Hidden/LV_DebugDisplayL1");
        }

        static readonly SceneView.CameraMode lightVolumeL1Mode;

        static readonly HashSet<SceneView> setupSceneViews = new HashSet<SceneView>();

        static SceneLightVolumeL1Mode()
        {
            lightVolumeL1Mode = SceneView.AddCameraMode("VRC Light Volumes L1", "Light Volumes Debug");

            SceneView.beforeSceneGui += view =>
            {
                if (setupSceneViews.Add(view))
                {
                    view.onCameraModeChanged += cameraMode =>
                    {
                        if (cameraMode == lightVolumeL1Mode)
                        {
                            view.SetSceneViewShaderReplace(Cache.lightVolumeL1Shader, string.Empty);
                        }
                        else if (view.cameraMode.drawMode == DrawCameraMode.Textured)
                        {
                            view.SetSceneViewShaderReplace(null, string.Empty);
                        }
                    };
                }
            };
        }
    }
}