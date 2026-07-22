using System.Collections.Generic;
using System.Linq;
namespace VRCLightVolumes {
    public static class LightVolumeDataSorter {

        public static List<LightVolumeData> SortData(List<LightVolumeData> lightVolumeDataList) {
            lightVolumeDataList.RemoveAll(item => item.LightVolumeInstance == null);
            var sorted = lightVolumeDataList.OrderByDescending(item => item.LightVolumeInstance.IsAdditive).ThenByDescending(item => item.Weight).ToList();
            return sorted;
        }

        public static LightVolumeInstance[] GetData(List<LightVolumeData> sortedData) {
            int count = sortedData.Count;
            LightVolumeInstance[] volumes = new LightVolumeInstance[count];
            for (int i = 0; i < count; ++i) {
                volumes[i] = sortedData[i].LightVolumeInstance;
            }
            return volumes;
        }

    }
}