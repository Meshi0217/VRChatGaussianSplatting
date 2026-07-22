using UnityEngine;
using System;

#if UDONSHARP
using VRC.SDKBase;
using UdonSharp;
#else
using System.Collections;
using VRCShader = UnityEngine.Shader;
#endif

namespace VRCLightVolumes {
#if UDONSHARP
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LightVolumeManager : UdonSharpBehaviour
#else
    public class LightVolumeManager : MonoBehaviour
#endif
    {
        public const float Version = 2; // VRC Light Volumes Current version. This value used in shaders (_UdonLightVolumeEnabled) to determine which features are can be used
        [Tooltip("Combined texture containing all Light Volumes' textures.")]
        public Texture LightVolumeAtlas;
        [Tooltip("Combined Texture3D containing all baked Light Volume data. This field is not used at runtime, see LightVolumeAtlas instead. It specifies the base for the post process chain, if given.")]
        public Texture3D LightVolumeAtlasBase;
        [Tooltip("Custom Render Textures that will be applied top to bottom to the Light Volume Atlas at runtime. External scripts can register themselves here using `RegisterPostProcessorCRT`. You probably don't want to mess with this field manually.")]
        public CustomRenderTexture[] AtlasPostProcessors;
        [Tooltip("When enabled, areas outside Light Volumes fall back to light probes. Otherwise, the Light Volume with the smallest weight is used as fallback. It also improves performance.")]
        public bool LightProbesBlending = true;
        [Tooltip("Disables smooth blending with areas outside Light Volumes. Use it if your entire scene's play area is covered by Light Volumes. It also improves performance.")]
        public bool SharpBounds = true;
        [Tooltip("Automatically updates most of the volumes properties in runtime. Enabling/Disabling, Color and Intensity updates automatically even without this option enabled. Position, Rotation and Scale gets updated only for volumes that are marked dynamic.")]
        public bool AutoUpdateVolumes = false;
        [Tooltip("Limits the maximum number of additive volumes that can affect a single pixel. If you have many dynamic additive volumes that may overlap, it's good practice to limit overdraw to maintain performance.")]
        public int AdditiveMaxOverdraw = 4;
        [Tooltip("The minimum brightness at a point due to lighting from a Point Light Volume, before the light is culled. Larger values will result in better performance, but light attenuation will be less physically correct.")]
        public float LightsBrightnessCutoff = 0.35f;
        [Tooltip("All Light Volume instances sorted in decreasing order by weight. You can enable or disable volumes game objects at runtime. Manually disabling unnecessary volumes improves performance.")]
        public LightVolumeInstance[] LightVolumeInstances = new LightVolumeInstance[0];
        [Tooltip("All Point Light Volume instances. You can enable or disable point light volumes game objects at runtime. Manually disabling unnecessary point light volumes improves performance.")]
        public PointLightVolumeInstance[] PointLightVolumeInstances = new PointLightVolumeInstance[0];
        [Tooltip("A texture array that can be used for as Cubemaps, LUT or Cookies")]
        public Texture CustomTextures;
        [Tooltip("Cubemaps count that stored in CustomTextures. Cubemap array elements starts from the beginning, 6 elements each.")]
        public int CubemapsCount = 0;
        [HideInInspector] public bool IsRangeDirty = false;

        private bool _isInitialized = false;
        private float _prevLightsBrightnessCutoff = 0.35f;
#if UDONSHARP
        private bool _isUpdateRequested = false; // Flag that specifies if volumes update requested.
#else
        private Coroutine _updateCoroutine = null; // Coroutine that auto-updates volumes if auto-update enabled (Non-Udon only)
#endif

        // Light Volumes Data
        private int _enabledCount = 0;
        private int _lastEnabledCount = -1;
        private int _additiveCount = 0;
        private int _occlusionCount = 0;

        private Vector4[] _invLocalEdgeSmooth = new Vector4[0];
        private Vector4[] _colors = new Vector4[0];
        private Vector4[] _boundsUvwScale = new Vector4[0];
        private Vector4[] _boundsOcclusionUvw = new Vector4[0];
        private Vector4[] _relativeRotationQuaternion = new Vector4[0];

        // Point Lights Data
        private int _pointLightCount = 0;
        private int _lastPointLightCount = -1;
        private int[] _enabledPointIDs = new int[128];
        private Vector4[] _pointLightPosition;
        private Vector4[] _pointLightColor;
        private Vector4[] _pointLightDirection;
        private Vector4[] _pointLightCustomId;

        // Legacy support Data
        private Matrix4x4[] _invWorldMatrix = new Matrix4x4[0];
        private Vector4[] _boundsUvw = new Vector4[0];
        private Vector4[] _relativeRotation = new Vector4[0];

        // Other
        private int[] _enabledIDs = new int[32];
        private Vector4[] _boundsScale = new Vector4[3];
        private Vector4[] _bounds = new Vector4[6]; // Legacy

        // Public API for other U# scripts
        public int EnabledCount => _enabledCount;
        public int[] EnabledIDs => _enabledIDs;

#region Shader Property IDs
        // Light Volumes
        private int lightVolumeInvLocalEdgeSmoothID;
        private int lightVolumeColorID;
        private int lightVolumeCountID;
        private int lightVolumeAdditiveCountID;
        private int lightVolumeAdditiveMaxOverdrawID;
        private int lightVolumeEnabledID;
        private int lightVolumeVersionID;
        private int lightVolumeProbesBlendID;
        private int lightVolumeSharpBoundsID;
        private int lightVolumeID;
        private int lightVolumeRotationQuaternionID;
        private int lightVolumeInvWorldMatrixID;
        private int lightVolumeUvwScaleID;
        private int lightVolumeOcclusionUvwID;
        private int lightVolumeOcclusionCountID;
        // Point Lights
        private int _pointLightPositionID;
        private int _pointLightColorID;
        private int _pointLightDirectionID;
        private int _pointLightCustomIdID;
        private int _pointLightCountID;
        private int _pointLightCubeCountID;
        private int _pointLightTextureID;
        private int _lightBrightnessCutoffID;
        // Legacy support
        private int _areaLightBrightnessCutoffID;
        private int lightVolumeRotationID;
        private int lightVolumeUvwID;

        // Initializing gloabal shader arrays if needed 
        private void TryInitialize() {

#if !UNITY_EDITOR
            if (_isInitialized) return;
#endif
            // Light Volumes
            lightVolumeInvLocalEdgeSmoothID = VRCShader.PropertyToID("_UdonLightVolumeInvLocalEdgeSmooth");
            lightVolumeInvWorldMatrixID = VRCShader.PropertyToID("_UdonLightVolumeInvWorldMatrix");
            lightVolumeColorID = VRCShader.PropertyToID("_UdonLightVolumeColor");
            lightVolumeCountID = VRCShader.PropertyToID("_UdonLightVolumeCount");
            lightVolumeAdditiveCountID = VRCShader.PropertyToID("_UdonLightVolumeAdditiveCount");
            lightVolumeAdditiveMaxOverdrawID = VRCShader.PropertyToID("_UdonLightVolumeAdditiveMaxOverdraw");
            lightVolumeEnabledID = VRCShader.PropertyToID("_UdonLightVolumeEnabled");
            lightVolumeVersionID = VRCShader.PropertyToID("_UdonLightVolumeVersion");
            lightVolumeProbesBlendID = VRCShader.PropertyToID("_UdonLightVolumeProbesBlend");
            lightVolumeSharpBoundsID = VRCShader.PropertyToID("_UdonLightVolumeSharpBounds");
            lightVolumeID = VRCShader.PropertyToID("_UdonLightVolume");
            lightVolumeRotationQuaternionID = VRCShader.PropertyToID("_UdonLightVolumeRotationQuaternion");
            lightVolumeUvwScaleID = VRCShader.PropertyToID("_UdonLightVolumeUvwScale");
            lightVolumeOcclusionUvwID = VRCShader.PropertyToID("_UdonLightVolumeOcclusionUvw");
            lightVolumeOcclusionCountID = VRCShader.PropertyToID("_UdonLightVolumeOcclusionCount");
            // Point Lights
            _pointLightPositionID = VRCShader.PropertyToID("_UdonPointLightVolumePosition");
            _pointLightColorID = VRCShader.PropertyToID("_UdonPointLightVolumeColor");
            _pointLightDirectionID = VRCShader.PropertyToID("_UdonPointLightVolumeDirection");
            _pointLightCountID = VRCShader.PropertyToID("_UdonPointLightVolumeCount");
            _pointLightCustomIdID = VRCShader.PropertyToID("_UdonPointLightVolumeCustomID");
            _pointLightCubeCountID = VRCShader.PropertyToID("_UdonPointLightVolumeCubeCount");
            _pointLightTextureID = VRCShader.PropertyToID("_UdonPointLightVolumeTexture");
            _lightBrightnessCutoffID = VRCShader.PropertyToID("_UdonLightBrightnessCutoff");
            // Legacy support
            _areaLightBrightnessCutoffID = VRCShader.PropertyToID("_UdonAreaLightBrightnessCutoff");
            lightVolumeRotationID = VRCShader.PropertyToID("_UdonLightVolumeRotation");
            lightVolumeUvwID = VRCShader.PropertyToID("_UdonLightVolumeUvw");

#if UNITY_EDITOR
            if (_isInitialized) return;
#endif
            // Light Volumes
            VRCShader.SetGlobalVectorArray(lightVolumeInvLocalEdgeSmoothID, new Vector4[32]);
            VRCShader.SetGlobalVectorArray(lightVolumeColorID, new Vector4[32]);
            VRCShader.SetGlobalMatrixArray(lightVolumeInvWorldMatrixID, new Matrix4x4[32]);
            VRCShader.SetGlobalVectorArray(lightVolumeRotationQuaternionID, new Vector4[32]);
            VRCShader.SetGlobalVectorArray(lightVolumeUvwScaleID, new Vector4[96]);
            VRCShader.SetGlobalVectorArray(lightVolumeOcclusionUvwID, new Vector4[32]);
            // Point Lights
            VRCShader.SetGlobalVectorArray(_pointLightPositionID, new Vector4[128]);
            VRCShader.SetGlobalVectorArray(_pointLightColorID, new Vector4[128]);
            VRCShader.SetGlobalVectorArray(_pointLightDirectionID, new Vector4[128]);
            VRCShader.SetGlobalVectorArray(_pointLightCustomIdID, new Vector4[128]);
            // Legacy support
            VRCShader.SetGlobalVectorArray(lightVolumeRotationID, new Vector4[64]);
            VRCShader.SetGlobalVectorArray(lightVolumeUvwID, new Vector4[192]);

            _isInitialized = true;
        }

        #endregion

        private bool _old_AutoUpdateVolumes = false;

#if UDONSHARP
        // Works only when changing values directly on UdonBehaviour
        // Low level Udon hacks:
        // _old_(Name) variables are the old values of the variables.
        // _onVarChange_(Name) methods (events) are called when the variable changes.
        public void _onVarChange_AutoUpdateVolumes() {
            if (!_old_AutoUpdateVolumes && AutoUpdateVolumes) RequestUpdateVolumes();
        }
#endif

#if !UDONSHARP || UNITY_EDITOR
        // To make it work when changing values on UdonSharpBehaviour in editor
        private void Update() {
            if (_old_AutoUpdateVolumes != AutoUpdateVolumes) {
                _old_AutoUpdateVolumes = AutoUpdateVolumes;
                if (AutoUpdateVolumes) {
                    RequestUpdateVolumes();
                }
            }
        }
#endif

        private void OnDisable() {
            TryInitialize();
#if !UDONSHARP
            if (_updateCoroutine != null) {
                StopCoroutine(_updateCoroutine);
                _updateCoroutine = null;
            }
#endif
            VRCShader.SetGlobalFloat(lightVolumeEnabledID, 0);
        }

        private void OnEnable() {
            RequestUpdateVolumes();
        }

        private void Start() {
            _isInitialized = false;
            UpdateVolumes(); // Force update volumes first time at start even if auto update is disabled
        }

        // Initrializes Light Volume by adding it to the light volumes array. Automalycally calls in runtime on object spawn
        public void InitializeLightVolume(LightVolumeInstance lightVolume) {
            int count = LightVolumeInstances.Length;
            // If there's an empty element in the array, use it!
            for (int i = 0; i < count; i++) {
                if (LightVolumeInstances[i] == null) {
                    LightVolumeInstances[i] = lightVolume;
                    lightVolume.LightVolumeManager = this;
                    lightVolume.IsInitialized = true;
                    return;
                }
            }
            // No empty element, then increase the array size
            LightVolumeInstance[] targetArray = new LightVolumeInstance[count + 1];
            Array.Copy(LightVolumeInstances, targetArray, count);
            targetArray[count] = lightVolume;
            lightVolume.IsInitialized = true;
            lightVolume.LightVolumeManager = this;
            LightVolumeInstances = targetArray;
        }
        public void InitializePointLightVolume(PointLightVolumeInstance pointLightVolume) {
            int count = PointLightVolumeInstances.Length;
            // If there's an empty element in the array, use it!
            for (int i = 0; i < count; i++) {
                if (PointLightVolumeInstances[i] == null) {
                    PointLightVolumeInstances[i] = pointLightVolume;
                    pointLightVolume.LightVolumeManager = this;
                    pointLightVolume.IsInitialized = true;
                    return;
                }
            }
            // No empty element, then increase the array size
            PointLightVolumeInstance[] targetArray = new PointLightVolumeInstance[count + 1];
            Array.Copy(PointLightVolumeInstances, targetArray, count);
            targetArray[count] = pointLightVolume;
            pointLightVolume.IsInitialized = true;
            pointLightVolume.LightVolumeManager = this;
            PointLightVolumeInstances = targetArray;
        }

        // Requests to update volumes next frame
        public void RequestUpdateVolumes() {
#if UDONSHARP
            if (_isUpdateRequested) return; // Prevent multiple requests
            _isUpdateRequested = true;
            SendCustomEventDelayedFrames(nameof(UpdateVolumesProcess), 1);
#else
            if (_updateCoroutine != null || !isActiveAndEnabled) return;
            _updateCoroutine = StartCoroutine(UpdateVolumesCoroutine());
#endif
        }

#if UDONSHARP
        // Internal method to auto update volumes every frame recursively
        public void UpdateVolumesProcess() {
            if (AutoUpdateVolumes && enabled && gameObject.activeInHierarchy) {
                SendCustomEventDelayedFrames(nameof(UpdateVolumesProcess), 1); // Auto schedule next update if AutoUpdateVolumes is enabled
            } else {
                _isUpdateRequested = false;
            }
            UpdateVolumes(); // Actually update volumes
        }
#else
        private IEnumerator UpdateVolumesCoroutine() {
            do {
                yield return null;
                UpdateVolumes();
            } while (AutoUpdateVolumes);
            _updateCoroutine = null;
        }
#endif

        // Main processing method that recalculates all the volumes data and sets it to the shader variables
        public void UpdateVolumes() {

            TryInitialize();

            if (!enabled || !gameObject.activeInHierarchy) {
                VRCShader.SetGlobalFloat(lightVolumeEnabledID, 0);
                return;
            }

            // Recalculate all lights ranges if LightsBrightnessCutoff changed
            if (_prevLightsBrightnessCutoff != LightsBrightnessCutoff) {
                _prevLightsBrightnessCutoff = LightsBrightnessCutoff;
                IsRangeDirty = true;
            }

            // Searching for enabled volumes. Counting Additive volumes.
            _enabledCount = 0;
            _additiveCount = 0;
            _occlusionCount = 0;
            for (int i = 0; i < LightVolumeInstances.Length && _enabledCount < 32; i++) {
                LightVolumeInstance instance = LightVolumeInstances[i];
                if (instance == null) continue;
                if (instance.gameObject.activeInHierarchy && instance.Intensity != 0 && instance.Color != Color.black && !instance.IsIterartedThrough) {
#if UDONSHARP
    #if COMPILER_UDONSHARP
                    if (instance.IsDynamic) instance.UpdateTransform();
    #else
                    instance.UpdateTransform();
    #endif
#else
                    if (Application.isPlaying) {
                        if (instance.IsDynamic) instance.UpdateTransform();
                    } else {
                        instance.UpdateTransform();
                    }
#endif
                    if (instance.IsAdditive) _additiveCount++;
                    if (instance.BakeOcclusion) _occlusionCount++;
                    _enabledIDs[_enabledCount] = i;
                    _enabledCount++;
                    instance.IsIterartedThrough = true;
                } else {
                    instance.IsIterartedThrough = false;
                }
            }

            // Initializing required arrays
            if (_enabledCount != _lastEnabledCount) {
                _invLocalEdgeSmooth = new Vector4[_enabledCount];
                _relativeRotationQuaternion = new Vector4[_enabledCount];
                _boundsUvwScale = new Vector4[_enabledCount * 3];
                _boundsOcclusionUvw = new Vector4[_enabledCount];
                _colors = new Vector4[_enabledCount];

                // Legacy data arrays
                _invWorldMatrix = new Matrix4x4[_enabledCount];
                _relativeRotation = new Vector4[_enabledCount * 2];
                _boundsUvw = new Vector4[_enabledCount * 6];
                _lastEnabledCount = _enabledCount;
            }

            // Filling arrays with enabled volumes
            for (int i = 0; i < _enabledCount; i++) {

                int enabledId = _enabledIDs[i];
                int i2 = i * 2;
                int i3 = i * 3;
                int i6 = i * 6;

                LightVolumeInstance instance = LightVolumeInstances[enabledId];

                // Reset iterated flag
                instance.IsIterartedThrough = false;

                // Setting volume transform
                _invWorldMatrix[i] = instance.InvWorldMatrix;
                _invLocalEdgeSmooth[i] = instance.InvLocalEdgeSmoothing; // Setting volume edge smoothing

                Vector4 c = instance.Color.linear * instance.Intensity; // Changing volume color
                c.w = instance.IsRotated ? 1 : 0; // Color alpha stores if volume rotated or not
                _colors[i] = c;

                // Setting volume relative rotation
                _relativeRotationQuaternion[i] = instance.RelativeRotation;
                _relativeRotation[i2] = instance.RelativeRotationRow0; // Legacy
                _relativeRotation[i2 + 1] = instance.RelativeRotationRow1; // Legacy

                // Setting volume UVW bounds
                _boundsScale[0] = instance.BoundsUvwMin0;
                _boundsScale[1] = instance.BoundsUvwMin1;
                _boundsScale[2] = instance.BoundsUvwMin2;
                _boundsOcclusionUvw[i] = instance.BakeOcclusion ? instance.BoundsUvwMinOcclusion : -Vector4.one;
                // Legacy
                _bounds[0] = instance.BoundsUvwMin0;
                _bounds[1] = instance.BoundsUvwMax0;
                _bounds[2] = instance.BoundsUvwMin1;
                _bounds[3] = instance.BoundsUvwMax1;
                _bounds[4] = instance.BoundsUvwMin2;
                _bounds[5] = instance.BoundsUvwMax2;

                Array.Copy(_boundsScale, 0, _boundsUvwScale, i3, 3);
                Array.Copy(_bounds, 0, _boundsUvw, i6, 6); // Legacy

            }

            // Searching for enabled point light volumes
            _pointLightCount = 0;
            for (int i = 0; i < PointLightVolumeInstances.Length && _pointLightCount < 128; i++) {
                PointLightVolumeInstance instance = PointLightVolumeInstances[i];
                if (instance == null) continue;
                if (IsRangeDirty) { // If Brightness cutoff changed, force recalculate every light's range
                    instance.UpdateRange();
                }
                if (instance.gameObject.activeInHierarchy && instance.Intensity != 0 && instance.Color != Color.black && !instance.IsIterartedThrough) {
#if UDONSHARP
    #if COMPILER_UDONSHARP
                    if (instance.IsDynamic) instance.UpdateTransform();
    #else
                    instance.UpdateTransform();
    #endif
#else
                    if (Application.isPlaying) {
                        if (instance.IsDynamic) instance.UpdateTransform();
                    } else {
                        instance.UpdateTransform();
                    }
#endif
                    _enabledPointIDs[_pointLightCount] = i;
                    _pointLightCount++;
                    instance.IsIterartedThrough = true;
                } else {
                    instance.IsIterartedThrough = false;
                }
            }

            IsRangeDirty = false; // reset range dirtyness

            // Initializing required arrays
            if (_pointLightCount != _lastPointLightCount) {
                _pointLightPosition = new Vector4[_pointLightCount];
                _pointLightColor = new Vector4[_pointLightCount];
                _pointLightDirection = new Vector4[_pointLightCount];
                _pointLightCustomId = new Vector4[_pointLightCount];
                _lastPointLightCount = _pointLightCount;
            }

            // Filling arrays with enabled point light volumes
            for (int i = 0; i < _pointLightCount; i++) {
                PointLightVolumeInstance instance = PointLightVolumeInstances[_enabledPointIDs[i]];

                // Recalculate squared range of the light light if dirty
                if (IsRangeDirty || instance.IsRangeDirty) {
                    instance.UpdateRange();
                }

                // Reset iterated flag
                instance.IsIterartedThrough = false;

                Vector4 pos = instance.PositionData;
                if (!instance.IsAreaLight()) {
                    if (instance.IsLut()) pos.w /= instance.SquaredScale;
                    else pos.w *= instance.SquaredScale;
                }
                _pointLightPosition[i] = pos;

                Vector4 c = instance.Color.linear * instance.Intensity;
                c.w = instance.AngleData;
                _pointLightColor[i] = c;

                _pointLightDirection[i] = instance.DirectionData;
                _pointLightCustomId[i].x = instance.CustomID;
                _pointLightCustomId[i].y = instance.ShadowmaskIndex;
                _pointLightCustomId[i].z = instance.SquaredRange;
            }

            bool isAtlas = LightVolumeAtlas != null;

            // Setting light volumes version
            VRCShader.SetGlobalFloat(lightVolumeVersionID, Version);

            // Disabling light volumes system if no atlas or no volumes
            if ((!isAtlas || _enabledCount == 0) && _pointLightCount == 0) {
                VRCShader.SetGlobalFloat(lightVolumeEnabledID, 0);
                return;
            }

            // 3D texture and it's parameters
            if (isAtlas) {
                VRCShader.SetGlobalTexture(lightVolumeID, LightVolumeAtlas);
            }

            // Regular Light Volumes
            VRCShader.SetGlobalFloat(lightVolumeCountID, _enabledCount);
            VRCShader.SetGlobalFloat(lightVolumeAdditiveCountID, _additiveCount);
            VRCShader.SetGlobalFloat(lightVolumeOcclusionCountID, _occlusionCount);
            
            // Defines if Light Probes Blending enabled in scene
            VRCShader.SetGlobalFloat(lightVolumeProbesBlendID, LightProbesBlending ? 1 : 0);
            VRCShader.SetGlobalFloat(lightVolumeSharpBoundsID, SharpBounds ? 1 : 0);

            // Max Overdraw
            VRCShader.SetGlobalFloat(lightVolumeAdditiveMaxOverdrawID, AdditiveMaxOverdraw);

            if (_enabledCount != 0) {
                // All light volumes inv Edge smooth
                VRCShader.SetGlobalVectorArray(lightVolumeInvLocalEdgeSmoothID, _invLocalEdgeSmooth);

                // All light volumes UVW
                VRCShader.SetGlobalVectorArray(lightVolumeUvwScaleID, _boundsUvwScale);
                VRCShader.SetGlobalVectorArray(lightVolumeOcclusionUvwID, _boundsOcclusionUvw);

                // Volume Transform Matrix
                VRCShader.SetGlobalMatrixArray(lightVolumeInvWorldMatrixID, _invWorldMatrix);

                // Volume's relative rotation
                VRCShader.SetGlobalVectorArray(lightVolumeRotationQuaternionID, _relativeRotationQuaternion);

                // Volume's color correction
                VRCShader.SetGlobalVectorArray(lightVolumeColorID, _colors);

                // Legacy data setting
                VRCShader.SetGlobalVectorArray(lightVolumeUvwID, _boundsUvw);
                VRCShader.SetGlobalVectorArray(lightVolumeRotationID, _relativeRotation);
            }

            // Point Lights
            VRCShader.SetGlobalFloat(_pointLightCountID, _pointLightCount);
            VRCShader.SetGlobalFloat(_pointLightCubeCountID, CubemapsCount);
            if (_pointLightCount != 0) {
                VRCShader.SetGlobalVectorArray(_pointLightColorID, _pointLightColor);
                VRCShader.SetGlobalVectorArray(_pointLightPositionID, _pointLightPosition);
                VRCShader.SetGlobalVectorArray(_pointLightDirectionID, _pointLightDirection);
                VRCShader.SetGlobalVectorArray(_pointLightCustomIdID, _pointLightCustomId);
                VRCShader.SetGlobalFloat(_lightBrightnessCutoffID, LightsBrightnessCutoff);
                VRCShader.SetGlobalFloat(_areaLightBrightnessCutoffID, LightsBrightnessCutoff); // Legacy
            }
            if(CustomTextures != null) {
                VRCShader.SetGlobalTexture(_pointLightTextureID, CustomTextures);
            }

            // Defines if Light Volumes enabled in scene. 0 if disabled. And a version number if enabled
            VRCShader.SetGlobalFloat(lightVolumeEnabledID, 1);

        }
    }
}