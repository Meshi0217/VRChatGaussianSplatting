namespace VRCLightVolumes {
    [System.Serializable]
    public struct LightVolumeData {

        public float Weight;
        public LightVolumeInstance LightVolumeInstance;

        public LightVolumeData(float weight, LightVolumeInstance lightVolumeInstance) {
            Weight = weight;
            LightVolumeInstance = lightVolumeInstance;
        }

    }
}