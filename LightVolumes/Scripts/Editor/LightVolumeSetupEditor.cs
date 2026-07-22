using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System.Collections.Generic;

namespace VRCLightVolumes {
    [CustomEditor(typeof(LightVolumeSetup))]
    public class LightVolumeSetupEditor : Editor {

        private SerializedProperty _volumesProp;
        private SerializedProperty _weightsProp;
        private ReorderableList _lightVolumesList;

        private SerializedProperty _pointLightVolumesProp;
        private ReorderableList _pointLightVolumesList;

        private LightVolumeSetup _lightVolumeSetup;

        private bool _isMultipleInstancesError = false;

        private void OnEnable() {

            int managersCount = FindObjectsByType<LightVolumeManager>(FindObjectsSortMode.None).Length;
            _isMultipleInstancesError = managersCount > 1;

            _lightVolumeSetup = (LightVolumeSetup)target;

            _volumesProp = serializedObject.FindProperty("LightVolumes");
            _weightsProp = serializedObject.FindProperty("LightVolumesWeights");

            // ============ LIGHT VOLUMES LIST ===============

            _lightVolumesList = new ReorderableList(
                serializedObject,
                _volumesProp,
                true, // draggable
                true, // displayHeader
                false, // displayAddButton
                false  // displayRemoveButton
            );

            // Drawing header
            _lightVolumesList.drawHeaderCallback = (Rect rect) => {

                float totalWidth = rect.width;
                float availableWidth = totalWidth - 15f - 4f;
                float weightWidth = 42f;
                float space = 5f;
                float volumeWidth = availableWidth - weightWidth - space;
                float xOffset = rect.x + 15f;

                var headerCountStyle = new GUIStyle(EditorStyles.numberField) {
                    alignment = TextAnchor.MiddleCenter,
                    contentOffset = Vector2.zero,
                    fixedHeight = EditorGUIUtility.singleLineHeight - 3,
                    margin = new RectOffset(0, 0, 0, 0),
                    padding = new RectOffset(0, 0, 0, 0),
                    fontSize = 11
                };
                headerCountStyle.normal.textColor = EditorStyles.label.normal.textColor;

                Rect volumeHeaderRect = new Rect(xOffset, rect.y, volumeWidth, EditorGUIUtility.singleLineHeight);
                Rect weightHeaderRect = new Rect(xOffset + volumeWidth + space, rect.y, weightWidth, EditorGUIUtility.singleLineHeight);
                Rect fieldRect = new Rect(xOffset + 96, rect.y + 2, 32, rect.height);

                GUIContent title = new GUIContent($"Light Volumes ({_lightVolumeSetup.LightVolumes.Count})");
                title.tooltip = "Max 32 can be visible on scene at the same time.";
                EditorGUI.LabelField(volumeHeaderRect, title);
                EditorGUI.LabelField(weightHeaderRect, "Weight");

                EventType eventType = Event.current.type;
                if (eventType == EventType.DragUpdated || eventType == EventType.DragPerform) {
                    if (!rect.Contains(Event.current.mousePosition)) return;
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (eventType == EventType.DragPerform) {
                        DragAndDrop.AcceptDrag();
                        for (int i = 0; i < DragAndDrop.objectReferences.Length; i++) {
                            GameObject go = (GameObject)DragAndDrop.objectReferences[i];
                            if (go.TryGetComponent(out LightVolume volume)) {
                                int newIndex = _volumesProp.arraySize;
                                if (newIndex == 256) break;
                                _volumesProp.arraySize++;
                                _weightsProp.arraySize = _volumesProp.arraySize;
                                _volumesProp.GetArrayElementAtIndex(newIndex).objectReferenceValue = volume;
                                _weightsProp.GetArrayElementAtIndex(newIndex).floatValue = 0;
                            }
                        }
                        Event.current.Use();
                    }
                }

            };

            // Drawing each element
            _lightVolumesList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) => {

                if (index < 0 || index >= _volumesProp.arraySize || index >= _weightsProp.arraySize) return;

                SerializedProperty volumeElement = _volumesProp.GetArrayElementAtIndex(index);
                SerializedProperty weightElement = _weightsProp.GetArrayElementAtIndex(index);

                rect.y += 2; // Top padding
                float totalWidth = rect.width;
                float weightWidth = 45f; // Weight width
                float space = 5f;        // Spacing

                Rect iconRect = new Rect(rect.x, rect.y, totalWidth - weightWidth - space, EditorGUIUtility.singleLineHeight);
                Rect volumeRect = new Rect(rect.x + 24, rect.y, totalWidth - weightWidth - space, EditorGUIUtility.singleLineHeight);
                Rect weightRect = new Rect(rect.x + totalWidth - weightWidth, rect.y, weightWidth, EditorGUIUtility.singleLineHeight);

                if (volumeElement.objectReferenceValue != null && volumeElement.objectReferenceValue.GetType() == typeof(LightVolume)) {
                    var volume = (LightVolume)volumeElement.objectReferenceValue;
                    GUIContent icon = volume.Additive ? EditorGUIUtility.IconContent("d_LightProbes Icon") : EditorGUIUtility.IconContent("d_PreMatLight1@2x");
                    icon.tooltip = volume.Additive ? "Additive Volume" : "Regular Volume";
                    EditorGUI.LabelField(iconRect, icon);
                }

                EditorGUI.LabelField(volumeRect, volumeElement.objectReferenceValue != null ? volumeElement.objectReferenceValue.name : "None");
                EditorGUI.PropertyField(weightRect, weightElement, GUIContent.none);

            };

            // On Moving element around
            _lightVolumesList.onReorderCallbackWithDetails = (ReorderableList list, int oldIndex, int newIndex) => {
                if (oldIndex >= 0 && oldIndex < _weightsProp.arraySize && newIndex >= 0 && newIndex < _weightsProp.arraySize) {
                    _weightsProp.MoveArrayElement(oldIndex, newIndex);
                } else if (_weightsProp.arraySize != _volumesProp.arraySize) {
                    // In case of a sync error
                    _weightsProp.arraySize = _volumesProp.arraySize;
                    EditorUtility.SetDirty(target);
                }
                _lightVolumeSetup.SyncUdonScript();
            };

            // On Click on element
            _lightVolumesList.onSelectCallback = (ReorderableList list) => {
                SerializedProperty volumeElement = _volumesProp.GetArrayElementAtIndex(list.index);
                LightVolume volume = volumeElement.objectReferenceValue as LightVolume;
                if (volume != null) EditorGUIUtility.PingObject(volume.gameObject);
            };

            // ================ POINT LIGHT VOLUMES LIST =============

            _pointLightVolumesProp = serializedObject.FindProperty("PointLightVolumes");

            _pointLightVolumesList = new ReorderableList(
                serializedObject,
                _pointLightVolumesProp,
                true, // draggable
                true, // displayHeader
                false, // displayAddButton
                false  // displayRemoveButton
            );

            // Drawing header
            _pointLightVolumesList.drawHeaderCallback = (Rect rect) => {

                float totalWidth = rect.width;
                float availableWidth = totalWidth - 15f - 4f;
                float weightWidth = 42f;
                float space = 5f;
                float volumeWidth = availableWidth - weightWidth - space;
                float xOffset = rect.x + 15f;

                Rect volumeHeaderRect = new Rect(xOffset, rect.y, volumeWidth, EditorGUIUtility.singleLineHeight);

                GUIContent title = new GUIContent($"Point Light Volumes ({_lightVolumeSetup.PointLightVolumes.Count})");
                title.tooltip = "Max 128 can be visible on scene at the same time.";
                EditorGUI.LabelField(volumeHeaderRect, title);

            };

            // Drawing each element
            _pointLightVolumesList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) => {

                if (index < 0 || index >= _pointLightVolumesProp.arraySize) return;

                SerializedProperty volumeElement = _pointLightVolumesProp.GetArrayElementAtIndex(index);

                rect.y += 2; // Top padding
                float totalWidth = rect.width;
                float weightWidth = 45f; // Weight width
                float space = 5f;        // Spacing

                Rect iconRect = new Rect(rect.x, rect.y, totalWidth - weightWidth - space, EditorGUIUtility.singleLineHeight);
                Rect volumeRect = new Rect(rect.x + 24, rect.y, totalWidth - weightWidth - space, EditorGUIUtility.singleLineHeight);
                Rect weightRect = new Rect(rect.x + totalWidth - weightWidth, rect.y, weightWidth, EditorGUIUtility.singleLineHeight);

                if (volumeElement.objectReferenceValue != null && volumeElement.objectReferenceValue.GetType() == typeof(PointLightVolume)) {
                    var volume = (PointLightVolume)volumeElement.objectReferenceValue;
                    GUIContent icon; 
                    if(volume.Type == PointLightVolume.LightType.SpotLight) {
                        icon = EditorGUIUtility.IconContent("d_Spotlight Icon");
                        icon.tooltip = "Spot Light Volume";
                    } else if (volume.Type == PointLightVolume.LightType.AreaLight) {
                        icon = EditorGUIUtility.IconContent("d_AreaLight Icon");
                        icon.tooltip = "Area Light Volume";
                    } else {
                        icon = EditorGUIUtility.IconContent("d_Light Icon");
                        icon.tooltip = "Point Light Volume";
                    }
                    EditorGUI.LabelField(iconRect, icon);
                }

                EditorGUI.LabelField(volumeRect, volumeElement.objectReferenceValue != null ? volumeElement.objectReferenceValue.name : "None");

            };

            // On Moving element around
            _pointLightVolumesList.onReorderCallbackWithDetails = (ReorderableList list, int oldIndex, int newIndex) => {
                _lightVolumeSetup.SyncUdonScript();
            };

            // On Click on element
            _pointLightVolumesList.onSelectCallback = (ReorderableList list) => {
                SerializedProperty volumeElement = _pointLightVolumesProp.GetArrayElementAtIndex(list.index);
                PointLightVolume volume = volumeElement.objectReferenceValue as PointLightVolume;
                if (volume != null) EditorGUIUtility.PingObject(volume.gameObject);
            };

        }

        public override void OnInspectorGUI() {
            serializedObject.Update();

            if (_volumesProp.arraySize != _weightsProp.arraySize) {
                _weightsProp.arraySize = _volumesProp.arraySize;
                for (int i = 0; i < _volumesProp.arraySize; i++) {
                    if (i >= _weightsProp.arraySize - (_volumesProp.arraySize - _weightsProp.arraySize)) {
                        SerializedProperty weightElement = _weightsProp.GetArrayElementAtIndex(i);
                        weightElement.floatValue = i;
                    }
                }
                EditorUtility.SetDirty(target);
            }

            GUILayout.Space(10);

            if (LVUtils.IsInPrefabAsset(_lightVolumeSetup)) {
                EditorGUILayout.HelpBox("This component is part of a prefab asset (not in the scene). Please, use one that is placed on your scene!", MessageType.Warning);
                GUILayout.Space(10);
            } else if (_isMultipleInstancesError) {
                EditorGUILayout.HelpBox("Multiple Light Volume Managers detected in the scene. Please ensure only one is active to avoid unexpected behavior!", MessageType.Error);
                GUILayout.Space(10);
            }

            ulong vCount = 0;
            if (_lightVolumeSetup.LightVolumeManager != null && _lightVolumeSetup.LightVolumeManager.LightVolumeAtlasBase != null) {
                var tex = _lightVolumeSetup.LightVolumeManager.LightVolumeAtlasBase;
                vCount = (ulong)tex.width * (ulong)tex.height * (ulong)tex.depth;

                if (_lightVolumeSetup.LightVolumeManager.AtlasPostProcessors != null) {
                    foreach (var crt in _lightVolumeSetup.LightVolumeManager.AtlasPostProcessors) {
                        if (crt != null) {
                            vCount += (ulong)crt.width * (ulong)crt.height * (ulong)crt.volumeDepth;
                        }
                    }
                }
            }

            GUILayout.Label($"Atlas size in VRAM: {SizeInVRAM(vCount)} MB");
            GUILayout.Label($"Atlas size in bundle: {SizeInBundle(vCount)} MB (Approximately)");

            GUILayout.Space(10);

            List<string> hiddenFields = new List<string>() { "m_Script", "LightVolumes", "PointLightVolumes", "LightVolumesWeights", "LightVolumeAtlas", "LightVolumeDataList", "LightVolumeManager", "_bakingModePrev", "IsLegacyUVWConverted" };

            if (_lightVolumeSetup.LightVolumes.Count > 0)
                _lightVolumesList.DoLayoutList();

            if (_lightVolumeSetup.PointLightVolumes.Count > 0) {
                _pointLightVolumesList.DoLayoutList();
            } else {
                hiddenFields.Add("Resolution");
                hiddenFields.Add("Format");
                hiddenFields.Add("LightsBrightnessCutoff");
            }

            GUILayout.Space(-15);


            if (_lightVolumeSetup.BakingMode != LightVolumeSetup.Baking.Bakery) {
                hiddenFields.Add("FixLightProbesL1");
                if (!_lightVolumeSetup.DilateInvalidProbes) {
                    hiddenFields.Add("DilationIterations");
                    hiddenFields.Add("DilationBackfaceBias");
                }
            } else {
                hiddenFields.Add("DilateInvalidProbes");
                hiddenFields.Add("DilationIterations");
                hiddenFields.Add("DilationBackfaceBias");
            }

            DrawPropertiesExcluding(serializedObject, hiddenFields.ToArray());

            GUILayout.Space(5);

            GUILayout.BeginHorizontal();

            if (_lightVolumeSetup.LightVolumes.Count > 0) {
                if (GUILayout.Button(new GUIContent("Pack Light Volumes", "Repacks Light Volumes 3D Atlas. Should be done manually if you added new volumes to your scene, or made some changes with their 3D textures."))) {
                    _lightVolumeSetup.GenerateAtlas();
                }
            }

            int lvcount = _lightVolumeSetup.LightVolumes.Count;
            if (lvcount > 0) {

                bool isShadowMask = false;
                for (int i = 0; i < lvcount; i++) {
                    if (_lightVolumeSetup.LightVolumes[i].PointLightShadows && _lightVolumeSetup.LightVolumes[i].LightVolumeInstance != null) {
                        isShadowMask = true;
                        break;
                    }
                }

                GUILayout.Space(5);

                GUI.enabled = isShadowMask;
                if (GUILayout.Button(new GUIContent("Bake Shadow Mask", "Calculates baked shadows for Point Light Volumes with PointLightShadows flag enabled"))) {
                    _lightVolumeSetup.BakeOcclusionVolumes();
                }
                GUI.enabled = true;

            }

            GUILayout.EndHorizontal();

            serializedObject.ApplyModifiedProperties();

        }

        // Real size in VRAM
        string SizeInVRAM(ulong vCount) {
            double mb = vCount * 8 / (double)(1024 * 1024);
            return mb.ToString("0.00");
        }

        // Approximate size in Asset bundle
        string SizeInBundle(ulong vCount) {
            double mb = vCount * 8 * 0.315f / (double)(1024 * 1024);
            return mb.ToString("0.00");
        }

    }
}