using UnityEngine;
#if UDONSHARP
using VRC.SDKBase;
using UdonSharp;
#endif

namespace VRCLightVolumes {
#if UDONSHARP
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LightVolumeInstance : UdonSharpBehaviour
#else
    public class LightVolumeInstance : MonoBehaviour
#endif
    {

        [Tooltip("Changing the color is useful for animating Additive volumes. You can even control the R, G, B channels separately this way.")]
        [ColorUsage(showAlpha: false)] public Color Color = Color.white;
        [Tooltip("Color multiplies by this value.")]
        public float Intensity = 1;
        [Tooltip("Defines whether this volume can be moved in runtime. Disabling this option slightly improves performance. You can even change it in runtime. Don't forget to enable \"Auto Update Volumes\" in your Light Volumes Setup to have this dynamic updates!")]
        public bool IsDynamic = false;
        [Tooltip("Additive volumes apply their light on top of others as an overlay. Useful for movable lights like flashlights, projectors, disco balls, etc. They can also project light onto static lightmapped objects if the surface shader supports it.")]
        public bool IsAdditive = false;
        [Tooltip("Inverse rotation of the pose the volume was baked in. Automatically recalculated for dynamic volumes with auto-update, or manually via the UpdateRotation() method.")]
        public Quaternion InvBakedRotation = Quaternion.identity;
        [Space]
        [Tooltip("Min bounds of Texture0 in 3D atlas space. W stores Scale X.)")]
        public Vector4 BoundsUvwMin0 = new Vector4();
        [Tooltip("Min bounds of Texture1 in 3D atlas space. W stores Scale Y.")]
        public Vector4 BoundsUvwMin1 = new Vector4();
        [Tooltip("Min bounds of Texture2 in 3D atlas space. W stores Scale Z.")]
        public Vector4 BoundsUvwMin2 = new Vector4();
        [Tooltip("Min bounds of occlusion texture in 3D atlas space.")]
        public Vector4 BoundsUvwMinOcclusion = new Vector4();
        [Space]
        [Tooltip("Max bounds of Texture0 in 3D atlas space. (Legacy)")]
        public Vector4 BoundsUvwMax0 = new Vector4();
        [Tooltip("Max bounds of Texture1 in 3D atlas space. (Legacy)")]
        public Vector4 BoundsUvwMax1 = new Vector4();
        [Tooltip("Max bounds of Texture2 in 3D atlas space. (Legacy)")]
        public Vector4 BoundsUvwMax2 = new Vector4();
        [Space]
        [Tooltip("Inversed edge smoothing in 3D atlas space. Recalculates via SetSmoothBlending(float radius) method.")]
        public Vector4 InvLocalEdgeSmoothing = new Vector4();
        [Tooltip("Inversed TRS matrix of this volume that transforms it into the 1x1x1 cube. Recalculates via the UpdateRotation() method.")]
        public Matrix4x4 InvWorldMatrix = Matrix4x4.identity;
        [Tooltip("Current volume's rotation relative to the rotation it was baked with. Mandatory for dynamic volumes. Recalculates via the UpdateRotation() method.")]
        public Vector4 RelativeRotation = new Vector4(0, 0, 0, 1);
        [Tooltip("Current volume's rotation matrix row 0 relative to the rotation it was baked with. Mandatory for dynamic volumes. Recalculates via the UpdateRotation() method. (Legacy)")]
        public Vector3 RelativeRotationRow0 = Vector3.zero;
        [Tooltip("Current volume's rotation matrix row 1 relative to the rotation it was baked with. Mandatory for dynamic volumes. Recalculates via the UpdateRotation() method. (Legacy)")]
        public Vector3 RelativeRotationRow1 = Vector3.zero;
        [Tooltip("True if there is any relative rotation. No relative rotation improves performance. Recalculated via the UpdateRotation() method.")]
        public bool IsRotated = false;
        [Tooltip("True if the volume has baked occlusion.")]
        public bool BakeOcclusion = false;
        [Tooltip("True if this Light Volume added to the Light Volumes array in LightVolumeManager. Should be always true for the Light Volumes placed in editor. Helps to initialize Light Volumes spawned in runtime.")]
        public bool IsInitialized = false;
        [Tooltip("Reference to the Light Volume Manager. Needed for runtime initialization.")]
        public LightVolumeManager LightVolumeManager;

        [HideInInspector] // Sets to true by the manager to check if we already iterated through this light. Prevents adding the same lights to the array muntiple times.
        public bool IsIterartedThrough = false;
        
        private Color _old_Color = Color.white;
        private float _old_Intensity = 1f;

#if UDONSHARP
        // Low level Udon hacks:
        // _old_(Name) variables are the old values of the variables.
        // _onVarChange_(Name) methods (events) are called when the variable changes.
        // Without udon it should be chacked in update
        public void _onVarChange_Color() {
            if (_old_Color != Color)
                RequestUpdateVolumes();
        }
        public void _onVarChange_Intensity() {
            if (_old_Intensity != Intensity)
                RequestUpdateVolumes();
        }
#endif

#if !UDONSHARP || UNITY_EDITOR
        // To make it work when changing values on UdonSharpBehaviour in editor
        private void Update() { 
            if (_old_Color != Color || _old_Intensity != Intensity) {
                _old_Color = Color;
                _old_Intensity = Intensity;
                RequestUpdateVolumes();
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
                LightVolumeManager.InitializeLightVolume(this);
            }
        }

        private void OnEnable() {
            RequestUpdateVolumes();
        }

        private void OnDisable() {
            RequestUpdateVolumes();
        }

        // Calculates and sets invLocalEdgeBlending
        public void SetSmoothBlending(float radius) {
            Vector3 scl = transform.lossyScale;
            InvLocalEdgeSmoothing = scl / Mathf.Max(radius, 0.00001f);
            RequestUpdateVolumes();
        }

        // Recalculates inv TRS matrix and Relative L1 rotation
        public void UpdateTransform() {
            Quaternion transformRot = transform.rotation;
            InvWorldMatrix = Matrix4x4.TRS(transform.position, transformRot, transform.lossyScale).inverse;
            Quaternion rot = transformRot * InvBakedRotation;
            IsRotated = Quaternion.Dot(rot, Quaternion.identity) < 0.999999f;

            Matrix4x4 m = Matrix4x4.Rotate(rot);

            Vector4 row0 = m.GetRow(0);
            row0.w = 0;
            RelativeRotationRow0 = row0;
            Vector4 row1 = m.GetRow(1);
            row1.w = 0;
            RelativeRotationRow1 = row1;

            RelativeRotation = new Vector4(rot.x, rot.y, rot.z, rot.w);
        }

        private void RequestUpdateVolumes() {
#if COMPILER_UDONSHARP
            if (Utilities.IsValid(LightVolumeManager)) 
#else
            if (LightVolumeManager != null)
#endif
                LightVolumeManager.RequestUpdateVolumes();
        }

    }
}