using UnityEditor;
using UnityEngine;

#if UDONSHARP
using VRC.Udon;
#endif

namespace VRCLightVolumes {

    [ExecuteAlways]
    public class PointLightVolume : MonoBehaviour {

        [Tooltip("Defines whether this point light volume can be moved in runtime. Disabling this option slightly improves performance. Don't forget to enable \"Auto Update Volumes\" in your Light Volumes Setup to have this dynamic updates!")]
        public bool Dynamic = false;
        [Tooltip("Enables baked shadows for this light. This setting is only available for static lights, which cannot move. You must re-bake your volumes after changing this setting. This incurs some runtime VRAM and performance overhead.")]
        public bool BakedShadows = false;
        [Tooltip("Shadow radius for the baked shadows. Higher values will produce softer shadows.")]
        [Min(0)] public float BakedShadowRadius = 0.1f;
        [Tooltip("Point light is the most performant type. Area light is the heaviest and best suited for dynamic, movable sources. For static lighting, it's recommended to bake regular additive light volumes instead.")]
        public LightType Type = LightType.PointLight;
        [Tooltip("Physical radius of a light source if it was a matte glowing sphere for a point light, or a flashlight reflector for a spot light. Larger size emmits more light without increasing overall intensity.")]
        [Min(0.0001f)] public float LightSourceSize = 0.25f;
        [Tooltip("Radius in meters beyond which light is culled. Fewer overlapping lights result in better performance.")]
        [Min(0.0001f)] public float Range = 10f;
        [Tooltip("Multiplies the point light volume’s color by this value.")]
        [ColorUsage(showAlpha: false)] public Color Color = Color.white;
        [Tooltip("Brightness of the point light volume.")]
        public float Intensity = 1f;
        [Tooltip("Parametric uses settings to compute light falloff. LUT uses a texture: X - cone falloff, Y - attenuation (Y only for point lights). Cookie projects a texture for spot lights. Cubemap projects a cubemap for point lights.")]
        public LightShape Shape = LightShape.Parametric;
        [Tooltip("Angle of a spotlight cone in degrees.")]
        [Range(0.1f, 360)] public float Angle = 60f;
        [Tooltip("Cone falloff.")]
        [Range(0.001f, 1)] public float Falloff = 1f;
        [Tooltip("X - cone falloff, Y - attenuation. No compression and RGBA Float or RGBA Half format is recommended.")]
        public Texture2D FalloffLUT = null;
        [Tooltip("Projects a square texture for spot lights.")]
        public Texture2D Cookie = null;
        [Tooltip("Projects a cubemap for point lights.")]
        public Cubemap Cubemap = null;
        [Tooltip("Shows overdrawing range gizmo. Less point light volumes intersections - more performance!")]
        public bool DebugRange = false;

        public int CustomID = 0;

        public PointLightVolumeInstance PointLightVolumeInstance;
        public LightVolumeSetup LightVolumeSetup;
#if UDONSHARP
        // UdonBehaviour is a real udon VM script. We need it to change public variables in play mode
        private UdonBehaviour _pointLightVolumeBehaviour = null;
#endif

        private Texture2D _falloffLUTPrev = null;
        private Texture2D _cookiePrev = null;
        private Cubemap _cubemapPrev = null;
        private LightShape _shapePrev = LightShape.Parametric;
        private LightType _typePrev = LightType.PointLight;

        // To check if object was edited this frame
        private Vector3 _prevPos = Vector3.zero;
        private Quaternion _prevRot = Quaternion.identity;
        private Vector3 _prevScl = Vector3.one;

        // Was it changed on Validate?
        private bool _isValidated = false;

        // Looks for LightVolumeSetup and LightVolumeInstance udon script and setups them if needed
        public void SetupDependencies() {
            if (PointLightVolumeInstance == null && !TryGetComponent(out PointLightVolumeInstance)) {
                PointLightVolumeInstance = gameObject.AddComponent<PointLightVolumeInstance>();
            }
#if UDONSHARP
            if (_pointLightVolumeBehaviour == null) {
                TryGetComponent(out _pointLightVolumeBehaviour);
            }
#endif
            if (LightVolumeSetup == null) {
                LightVolumeSetup = FindObjectOfType<LightVolumeSetup>();
                if (LightVolumeSetup == null) {
                    var go = new GameObject("Light Volume Manager");
                    LightVolumeSetup = go.AddComponent<LightVolumeSetup>();
                    LightVolumeSetup.SyncUdonScript();
                }
            }
        }

        // Returns currently used custom texture depending on the light parameters
        public Texture GetCustomTexture() {
            if (Shape == LightShape.Parametric || Type == LightType.AreaLight) {
                return null;
            } else if (Type == LightType.PointLight) {
                if(Shape == LightShape.LUT) {
                    return FalloffLUT;
                } else if (Shape == LightShape.Custom) {
                    return Cubemap;
                }
            } else if (Type == LightType.SpotLight) {
                if (Shape == LightShape.LUT) {
                    return FalloffLUT;
                } else if (Shape == LightShape.Custom) {
                    return Cookie;
                }
            }
            return null;
        }

        private void Update() {
            if (gameObject == null) return;
            SetupDependencies();
#if UNITY_EDITOR
            // Regenerate texture array
            if (_falloffLUTPrev != FalloffLUT || _cookiePrev != Cookie || _cubemapPrev != Cubemap || _shapePrev != Shape || _typePrev != Type) {
                _falloffLUTPrev = FalloffLUT;
                _cookiePrev = Cookie;
                _cubemapPrev = Cubemap;
                _shapePrev = Shape;
                _typePrev = Type;
                LightVolumeSetup.GenerateCustomTexturesArray();
            }
            // Sync udon script
            if (_prevPos != transform.position || _prevRot != transform.rotation || _prevScl != transform.localScale) {
                _prevPos = transform.position;
                _prevRot = transform.rotation;
                _prevScl = transform.localScale;
                LightVolumeSetup.SyncUdonScript();
            }

            if (_isValidated) {
                _isValidated = false;
                SyncUdonScript();
                LightVolumeSetup.SyncUdonScript();
            }
#endif
        }

        public void SyncUdonScript() {
            if (gameObject == null) return;
            SetupDependencies();
#if UDONSHARP
            if (Application.isPlaying) {
                // To sync variables in play-mode, we need to do it directly to the UdonBehaviour
                _pointLightVolumeBehaviour.SetProgramVariable("IsDynamic", Dynamic);
                _pointLightVolumeBehaviour.SetProgramVariable("Color", Color);
                _pointLightVolumeBehaviour.SetProgramVariable("Intensity", Intensity);
                _pointLightVolumeBehaviour.SetProgramVariable("IsRangeDirty", true);
                // Udon does not support methods with parameters, so under the hood, it's just some global variables.
                // We can first set these parameters and then exetute a parameterless method.
                if (Type == LightType.PointLight) { // Point light
                    if (Shape == LightShape.Custom && Cubemap != null) { // Use Custom Cubemap Texture
                        // SetRange(LightSourceSize)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", LightSourceSize);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetCustomTexture(CustomID)
                        _pointLightVolumeBehaviour.SetProgramVariable("__1_id__param", CustomID);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetCustomTexture");
                    } else if (Shape == LightShape.LUT && FalloffLUT != null) { // Use LUT
                        // SetRange(Range)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", Range);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetLut(CustomID)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_id__param", CustomID);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLut");
                    } else { // Use this light in parametric mode
                        // SetRange(LightSourceSize)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", LightSourceSize);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetParametric()
                        _pointLightVolumeBehaviour.SendCustomEvent("SetParametric");
                    }
                    // Use it as Point Light
                    // SetPointLight()
                    _pointLightVolumeBehaviour.SendCustomEvent("SetPointLight");
                    _pointLightVolumeBehaviour.SendCustomEvent("UpdateRotation");
                } else if (Type == LightType.SpotLight) { // Spot Light
                    // SetRange(Range)
                    _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", Range);
                    _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                    if (Shape == LightShape.Custom && Cookie != null) { // Use Cookie Texture
                        // SetRange(LightSourceSize)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", LightSourceSize);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetCustomTexture(CustomID)
                        _pointLightVolumeBehaviour.SetProgramVariable("__1_id__param", CustomID);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetCustomTexture");
                    } else if (Shape == LightShape.LUT && FalloffLUT != null) { // Use LUT
                        // SetRange(Range)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", Range);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetLut(CustomID)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_id__param", CustomID);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLut");
                    } else { // Use this light in parametric mode
                        // SetRange(LightSourceSize)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", LightSourceSize);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetParametric()
                        _pointLightVolumeBehaviour.SendCustomEvent("SetParametric");
                    }
                    // Don't use custom tex
                    // SetSpotLight(Angle, Falloff)
                    _pointLightVolumeBehaviour.SetProgramVariable("__0_angleDeg__param", Angle);
                    _pointLightVolumeBehaviour.SetProgramVariable("__0_falloff__param", Falloff);
                    _pointLightVolumeBehaviour.SendCustomEvent("__0_SetSpotLight");
                    _pointLightVolumeBehaviour.SendCustomEvent("UpdateRotation");

                } else if (Type == LightType.AreaLight) { // Area light
                    // SetAreaLight()
                    _pointLightVolumeBehaviour.SendCustomEvent("SetAreaLight");
                    _pointLightVolumeBehaviour.SendCustomEvent("UpdateRotation");
                }

            } else {
#endif
                PointLightVolumeInstance.IsInitialized = true; // Always override to true in editor with no play mode!
                PointLightVolumeInstance.LightVolumeManager = LightVolumeSetup.LightVolumeManager;

                PointLightVolumeInstance.IsDynamic = Dynamic;
                PointLightVolumeInstance.Color = Color;
                PointLightVolumeInstance.Intensity = Intensity;
                PointLightVolumeInstance.IsRangeDirty = true;

                if (Type == LightType.PointLight) { // Point light
                    if (Shape == LightShape.Custom && Cubemap != null) {
                        PointLightVolumeInstance.SetLightSourceSize(LightSourceSize);
                        PointLightVolumeInstance.SetCustomTexture(CustomID); // Use Custom Cubemap Texture
                    } else if (Shape == LightShape.LUT && FalloffLUT != null) {
                        PointLightVolumeInstance.SetLightSourceSize(Range);
                        PointLightVolumeInstance.SetLut(CustomID); // Use LUT
                    } else {
                        PointLightVolumeInstance.SetLightSourceSize(LightSourceSize);
                        PointLightVolumeInstance.SetParametric(); // Use this light in parametric mode
                    }
                    PointLightVolumeInstance.SetPointLight(); // Use it as Point Light
                    PointLightVolumeInstance.UpdateRotation();
                } else if (Type == LightType.SpotLight) { // Spot Light
                    PointLightVolumeInstance.SetLightSourceSize(Range);
                    if (Shape == LightShape.Custom && Cookie != null) {
                        PointLightVolumeInstance.SetLightSourceSize(LightSourceSize);
                        PointLightVolumeInstance.SetCustomTexture(CustomID); // Use Cookie Texture
                    } else if (Shape == LightShape.LUT && FalloffLUT != null) {
                        PointLightVolumeInstance.SetLightSourceSize(Range);
                        PointLightVolumeInstance.SetLut(CustomID); // Use LUT
                    } else {
                        PointLightVolumeInstance.SetLightSourceSize(LightSourceSize);
                        PointLightVolumeInstance.SetParametric(); // Use this light in parametric mode
                    }
                    PointLightVolumeInstance.SetSpotLight(Angle, Falloff); // Don't use custom tex
                    PointLightVolumeInstance.UpdateRotation();
                } else if (Type == LightType.AreaLight) { // Area light
                    PointLightVolumeInstance.SetAreaLight();
                    PointLightVolumeInstance.UpdateRotation();
                }

#if UNITY_EDITOR
                // Mark changes to ensure prefab modifications are recorded
                LVUtils.MarkDirty(PointLightVolumeInstance);
#endif

#if UDONSHARP
            }
#endif
        }

        private void Reset() {
            SetupDependencies();
            SyncUdonScript();
            LightVolumeSetup.RefreshVolumesList();
            LightVolumeSetup.SyncUdonScript();
        }

        private void OnEnable() {
            SetupDependencies();
            LightVolumeSetup.RefreshVolumesList();
            LightVolumeSetup.SyncUdonScript();
        }

        private void OnDisable() {
            if (LightVolumeSetup != null) {
                LightVolumeSetup.RefreshVolumesList();
                LightVolumeSetup.SyncUdonScript();
            }
        }

        private void OnDestroy() {
            if (LightVolumeSetup != null) {
                FalloffLUT = null;
                Cookie = null;
                Cubemap = null;
#if UNITY_EDITOR
                LightVolumeSetup.GenerateCustomTexturesArray();
#endif
                LightVolumeSetup.RefreshVolumesList();
                LightVolumeSetup.SyncUdonScript();
            }
        }

        private void OnValidate() {
            _isValidated = true;
        }

        public enum LightShape {
            Parametric,
            LUT,
            Custom
        }

        public enum LightType {
            PointLight,
            SpotLight,
            AreaLight,
        }

    }

}
