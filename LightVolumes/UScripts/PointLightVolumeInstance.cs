
using UnityEngine;

#if UDONSHARP
using UdonSharp;
using VRC.SDKBase;
using VRC.Udon;
#endif

namespace VRCLightVolumes {
#if UDONSHARP
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PointLightVolumeInstance : UdonSharpBehaviour
#else
    public class PointLightVolumeInstance : MonoBehaviour
#endif
    {
        [Tooltip("Point light volume color")]
        [ColorUsage(showAlpha: false)] public Color Color;
        [Tooltip("Color multiplies by this value.")]
        public float Intensity = 1;
        [Tooltip("Defines whether this point light volume can be moved in runtime. Disabling this option slightly improves performance. Don't forget to enable \"Auto Update Volumes\" in your Light Volumes Setup to have this dynamic updates!")]
        public bool IsDynamic = false;
        [Tooltip("For point light: XYZ = Position, W = Inverse squared range.\nFor spot light: XYZ = Position, W = Inverse squared range, negated.\nFor area light: XYZ = Position, W = Width.")]
        public Vector4 PositionData;
        [Tooltip("For point light: XYZW = Rotation quaternion.\nFor spot light: XYZ = Direction, W = Cone falloff.\nFor area light: XYZW = Rotation quaternion.")]
        public Vector4 DirectionData;
        [Tooltip("If parametric: Stores 0.\nIf uses custom LUT: Stores LUT ID with positive sign.\nIf uses custom texture: Stores texture ID with negative sign.")]
        public float CustomID;
        [Tooltip("Half-angle of the spotlight cone, in radians.")]
        public float Angle;
        [Tooltip("For point light: unused.\nFor spot light: Cos of outer angle if no custom texture, tan of outer angle otherwise.\nFor area light: 2 + Height.")]
        public float AngleData;
        [Tooltip("Index of the shadowmask channel used by this light. -1 means no shadowmask.")]
        public sbyte ShadowmaskIndex = -1;
        [Tooltip("True if this Point Light Volume added to the Point Light Volumes array in LightVolumeManager. Should be always true for the Point Light Volumes placed in editor. Helps to initialize Point Light Volumes spawned in runtime.")]
        public bool IsInitialized = false;
        [Tooltip("Squared range after which light will be culled. Should be recalculated by executing UpdateRange() method.")]
        public float SquaredRange = 1;
        [Tooltip("Average squared lossy scale of the light. Light Source Size gets multiplied by it at the end. Updates with UpdateTransform() method.")]
        public float SquaredScale = 1;
        [Tooltip("Reference to the Light Volume Manager. Needed for runtime initialization.")]
        public LightVolumeManager LightVolumeManager;

        
        [HideInInspector] public bool IsIterartedThrough = false; // Sets to true by the manager to check if we already iterated through this light. Prevents adding the same lights to the array muntiple times.
        [HideInInspector] public bool IsRangeDirty = false; // Sets to true to recalculate the range automatically by the manager
        private Vector3 _prevPosition = Vector3.zero;
        private Quaternion _prevRotation = Quaternion.identity;
        private Vector3 _prevScale = Vector3.one;

        private Color _old_Color = Color.white;
        private float _old_Intensity = 1;

#if UDONSHARP
        // Works only when changing values directly on UdonBehaviour
        // Low level Udon hacks:
        // _old_(Name) variables are the old values of the variables.
        // _onVarChange_(Name) methods (events) are called when the variable changes.
        public void _onVarChange_Color() {
            if (_old_Color != Color) MarkRangeDirtyAndRequestUpdate();
        }
        public void _onVarChange_Intensity() {
            if (_old_Intensity != Intensity) MarkRangeDirtyAndRequestUpdate();
        }
#endif

#if !UDONSHARP || UNITY_EDITOR
        // To make it work when changing values on UdonSharpBehaviour in editor
        private void Update() {
            if (_old_Color != Color || _old_Intensity != Intensity) {
                _old_Color = Color;
                _old_Intensity = Intensity;
                RquestUpdateVolumes();
            }
        }
#endif

        private void Start() {
#if !UDONSHARP
            if (LightVolumeManager == null) {
                LightVolumeManager = FindObjectOfType<LightVolumeManager>();
            }
#endif
            if (!IsInitialized && LightVolumeManager != null) {
                LightVolumeManager.InitializePointLightVolume(this);
            }
        }

        private void OnEnable() {
            RquestUpdateVolumes();
        }

        private void OnDisable() {
            RquestUpdateVolumes();
        }

        // Checks if it's a spotlight
        public bool IsSpotLight() {
            return PositionData.w < 0;
        }
        
        // Checks if it's a point light
        public bool IsPointLight() {
            return PositionData.w >= 0 && AngleData <= 1.5;
        }

        // Checks if it's an area light
        public bool IsAreaLight() {
            return PositionData.w >= 0 && AngleData > 1.5;
        }

        // Checks if uses custom texture
        public bool IsCustomTexture() {
            return CustomID < 0;
        }

        // Checks if uses LUT
        public bool IsLut() {
            return CustomID > 0;
        }

        // Checks if uses Parametric mode
        public bool IsParametric() {
            return CustomID == 0;
        }

        // Sets Light source size, or a range data for LUT mode
        public void SetLightSourceSize(float size) {
            if (IsLut()) {
                PositionData.w = Mathf.Sign(PositionData.w) / (size * size); // Saving the sign that was here before. Inversed squared range
            } else {
                PositionData.w = Mathf.Sign(PositionData.w) * size * size; // Saving the sign that was here before. Squared light size
            }
            MarkRangeDirtyAndRequestUpdate();
        }

        // Sets LUT ID
        public void SetLut(int id) {
            CustomID = id + 1;
            AngleData = Mathf.Cos(Angle);
            MarkRangeDirtyAndRequestUpdate();
        }

        // Sets Cubemap or a Cookie ID
        public void SetCustomTexture(int id) {
            CustomID = - id - 1;
            if(IsSpotLight()) { // If it's spotlight
                AngleData = Mathf.Tan(Angle);
            }
            MarkRangeDirtyAndRequestUpdate();
        }

        // Sets light into parametric mode
        public void SetParametric() {
            CustomID = 0;
            AngleData = Mathf.Cos(Angle);
            MarkRangeDirtyAndRequestUpdate();
        }

        // Sets light into the point light type
        public void SetPointLight() {
            PositionData.w = Mathf.Abs(PositionData.w);
            MarkRangeDirtyAndRequestUpdate();
        }

        // Sets light into the spot light type with both angle and falloff because angle required to determine falloff anyway
        public void SetSpotLight(float angleDeg, float falloff) {
            Angle = angleDeg * Mathf.Deg2Rad * 0.5f;
            if (IsCustomTexture()) {
                AngleData = Mathf.Tan(Angle); // Using Custom Tex
            } else {
                AngleData = Mathf.Cos(Angle);
                DirectionData.w = 1 / (Mathf.Cos(Angle * (1.0f - Mathf.Clamp01(falloff))) - AngleData);
            }
            PositionData.w = - Mathf.Abs(PositionData.w);
            MarkRangeDirtyAndRequestUpdate();
        }

        // Sets light into the spot light type with angle specified
        public void SetSpotLight(float angleDeg) {
            Angle = angleDeg * Mathf.Deg2Rad * 0.5f;
            if (IsCustomTexture()) {
                AngleData = Mathf.Tan(Angle); // Using Custom Tex
            } else {
                AngleData = Mathf.Cos(Angle);
            }
            PositionData.w = - Mathf.Abs(PositionData.w);
            MarkRangeDirtyAndRequestUpdate();
        }
        
        // Sets light into the area light type
        public void SetAreaLight() {
            PositionData.w = Mathf.Max(Mathf.Abs(transform.lossyScale.x), 0.001f);
            AngleData = 2 + Mathf.Max(Mathf.Abs(transform.lossyScale.y), 0.001f); // Add 2 to get out of [-1; 1] codomain of cosine
            MarkRangeDirtyAndRequestUpdate();
        }

        // Sets light source color
        public void SetColor(Color color) {
            Color = color;
            MarkRangeDirtyAndRequestUpdate();
        }

        // Sets light source intensity
        public void SetIntensity(float intensity) {
            Intensity = intensity;
            MarkRangeDirtyAndRequestUpdate();
        }

        private void MarkRangeDirtyAndRequestUpdate() {
            IsRangeDirty = true;
            RquestUpdateVolumes();
        }

        // Updates data required for shader
        public void UpdateTransform() {

            // Position Update
            Vector3 pos = transform.position;
            if (_prevPosition != pos) {
                _prevPosition = pos;
                UpdatePosition();
            }

            // Rotation Update
            Quaternion rot = transform.rotation;
            if (_prevRotation != rot) {
                _prevRotation = rot;
                UpdateRotation();
            }

            // Scale Update
            Vector3 lscale = transform.lossyScale;
            if (_prevScale != lscale) {
                _prevScale = lscale;
                UpdateScale();
            }
              
        }

        // Force update position
        public void UpdatePosition() {
            Vector3 pos = transform.position;
            PositionData = new Vector4(pos.x, pos.y, pos.z, PositionData.w);
        }
        
        // Force update rotation
        public void UpdateRotation() {
            Quaternion rot = transform.rotation;
            if (IsAreaLight()) {
                DirectionData = new Vector4(rot.x, rot.y, rot.z, rot.w);
            } else if (IsSpotLight() && !IsCustomTexture()) { // If Spot Light with no cookie
                Vector3 dir = transform.forward;
                DirectionData = new Vector4(dir.x, dir.y, dir.z, DirectionData.w);
            } else if (!IsParametric()) { // If Point Light with a cubemap or a spot light with cookie
                rot = Quaternion.Inverse(rot);
                DirectionData = new Vector4(rot.x, rot.y, rot.z, rot.w);
            }
        }

        // Force update scale
        public void UpdateScale() {
            Vector3 lscale = transform.lossyScale;
            if (IsAreaLight()) SetAreaLight();
            SquaredScale = (lscale.x + lscale.y + lscale.z) / 3;
            SquaredScale *= SquaredScale;
            MarkRangeDirtyAndRequestUpdate();
        }

        // Recalculates squared culling range for the light
        public void UpdateRange() {
            float cutoff = LightVolumeManager != null ? LightVolumeManager.LightsBrightnessCutoff : 0.35f;
            if (IsAreaLight()) { // Area light squared distance math
                SquaredRange = ComputeAreaLightSquaredBoundingSphere(Mathf.Abs(SquaredScale / PositionData.w), AngleData - 2, Color, Intensity * Mathf.PI, cutoff);
            } else if(IsLut()) { // LUT - regualar squared range
                SquaredRange = Mathf.Abs(SquaredScale / PositionData.w);
            } else { // Spot and Point light squared distance math
                SquaredRange = ComputePointLightSquaredBoundingSphere(Color, Intensity, Mathf.Abs(SquaredScale * PositionData.w), cutoff);
            }
            IsRangeDirty = false;
        }

        private float ComputeAreaLightSquaredBoundingSphere(float width, float height, Color color, float intensity, float cutoff) {
            float minSolidAngle = Mathf.Clamp(cutoff / (Mathf.Max(color.r, Mathf.Max(color.g, color.b)) * intensity), -Mathf.PI * 2f, Mathf.PI * 2);
            float A = width * height;
            float w2 = width * width;
            float h2 = height * height;
            float B = 0.25f * (w2 + h2);
            float t = Mathf.Tan(0.25f * minSolidAngle);
            float T = t * t;
            float TB = T * B;
            float discriminant = Mathf.Sqrt(TB * TB + 4.0f * T * A * A);
            float d2 = (discriminant - TB) * 0.125f / T;
            return d2;
        }

        private float ComputePointLightSquaredBoundingSphere(Color color, float intensity, float sqSize, float cutoff) {
            float L = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            return Mathf.Max(Mathf.PI * 2 * L * Mathf.Abs(intensity) / (cutoff * cutoff) - 1, 0) * sqSize;
        }

        private void RquestUpdateVolumes() {
#if COMPILER_UDONSHARP
            if (Utilities.IsValid(LightVolumeManager)) 
#else
            if (LightVolumeManager != null)
#endif
                LightVolumeManager.RequestUpdateVolumes();
        }

    }

}