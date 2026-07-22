using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRCLightVolumes {
    public class LightProbePlacerWindow : EditorWindow {

        private LightVolume _lightVolume;

        private bool _adaptiveResolution = true;
        private float _voxelsPerUnit = 2;
        private Vector3Int _resolution = new Vector3Int(16, 16, 16);

        // Light probes world positions
        private Vector3[] _probesPositions = new Vector3[0];
        private bool _isWindowActive = false;

        // Preview
        private Material _previewMaterial;
        private Mesh _previewMesh;
        private ComputeBuffer _posBuf;
        private ComputeBuffer _argsBuf;
        static readonly int _previewPosID = Shader.PropertyToID("_Positions");
        static readonly int _previewScaleID = Shader.PropertyToID("_Scale");

        public static LightProbePlacerWindow Show(LightVolume volume) {
            LightProbePlacerWindow window = ScriptableObject.CreateInstance<LightProbePlacerWindow>();
            window._lightVolume = volume;
            window._resolution = volume.Resolution / 4;
            window._voxelsPerUnit = volume.VoxelsPerUnit / 4;
            window._adaptiveResolution = volume.AdaptiveResolution;
            window.titleContent = new GUIContent("Generate Light Probes");
            window.position = new Rect(Screen.width / 2, Screen.height / 2, 220f, 150f);
            window.minSize = new Vector2(220f, 150f);
            window.Show();
            return window;
        }

        private void OnEnable() {

            const float width = 220f;
            const float height = 150f;

            Vector2 center = new Vector2(
                Screen.currentResolution.width / 2f - width / 2f,
                Screen.currentResolution.height / 2f - height / 2f
            );

            position = new Rect(center, new Vector2(width, height));

            SceneView.duringSceneGui += OnSceneGUI;
            _isWindowActive = true;

        }

        private void OnDisable() {

            SceneView.duringSceneGui -= OnSceneGUI;
            ReleasePreviewBuffers();
            _isWindowActive = false;

        }

        private void OnSceneGUI(SceneView sceneView) {

            if (!_isWindowActive) return;

            Vector3[] pPositions; // Draw only 1000 000 first probes!
            if (_probesPositions.Length > 1000000) {
                pPositions = new Vector3[1000000];
                System.Array.Copy(_probesPositions, pPositions, 1000000);
            } else {
                pPositions = _probesPositions;
            }

            // Initialize Buffers
            if (_posBuf == null || _posBuf.count != pPositions.Length) {
                ReleasePreviewBuffers();
                _posBuf = new ComputeBuffer(pPositions.Length, sizeof(float) * 3);
                _argsBuf = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
            }

            // Generate Sphere mesh
            if (_previewMesh == null) {
                _previewMesh = LVUtils.GenerateIcoSphere(0.5f, 0);
            }

            // Create Material
            if (_previewMaterial == null) {
                _previewMaterial = new Material(Shader.Find("Hidden/LightVolumesPreview"));
            }

            // Calculating radius
            Vector3 scale = _lightVolume.GetScale();
            Vector3 res = _resolution;
            float radius = Mathf.Min(scale.z / res.z, Mathf.Min(scale.x / res.x, scale.y / res.y)) / 3;

            // Setting data to buffers
            _posBuf.SetData(pPositions);
            _previewMaterial.SetBuffer(_previewPosID, _posBuf);
            _previewMaterial.SetFloat(_previewScaleID, radius);
            _argsBuf.SetData(new uint[] { _previewMesh.GetIndexCount(0), (uint)pPositions.Length, _previewMesh.GetIndexStart(0), (uint)_previewMesh.GetBaseVertex(0), 0 });

            Bounds bounds = LVUtils.BoundsFromTRS(_lightVolume.GetMatrixTRS());
            Graphics.DrawMeshInstancedIndirect(_previewMesh, 0, _previewMaterial, bounds, _argsBuf, 0, null, ShadowCastingMode.Off, false, _lightVolume.gameObject.layer);

        }

        void ReleasePreviewBuffers() {
            if (_posBuf != null) { _posBuf.Release(); _posBuf = null; }
            if (_argsBuf != null) { _argsBuf.Release(); _argsBuf = null; }
        }

        private void OnGUI() {

            if (_lightVolume == null) {
                Close();
                return;
            }

            const float padding = 10f;

            Rect paddedRect = new Rect(padding, padding, position.width - padding * 2, position.height - padding * 2);

            GUILayout.BeginArea(paddedRect);

            EditorGUILayout.LabelField(_lightVolume.gameObject.name, EditorStyles.boldLabel);

            _adaptiveResolution = EditorGUILayout.Toggle("Adaptive Resolution", _adaptiveResolution);
            if (_adaptiveResolution) {
                _voxelsPerUnit = EditorGUILayout.FloatField("Voxels Per Unit", _voxelsPerUnit);
            }

            _resolution = EditorGUILayout.Vector3IntField("Resolution", _resolution);
            Recalculate();

            GUILayout.Space(10);
            if (GUILayout.Button("Create Light Probe Group")) {
                CreateLightProbeGroup();
                Close();
            }

            GUILayout.EndArea();
            SceneView.RepaintAll();
        }

        private void CreateLightProbeGroup() {
            GameObject go = new GameObject("Light Probes - " + _lightVolume.gameObject.name);
            go.transform.parent = _lightVolume.transform;
            go.transform.SetPositionAndRotation(go.transform.position, go.transform.rotation);
            LightProbeGroup probeGroup = go.AddComponent<LightProbeGroup>();
            probeGroup.probePositions = _probesPositions;
            EditorGUIUtility.PingObject(go);
            Selection.activeObject = go;
        }

        public void Recalculate() {
            if (_adaptiveResolution) RecalculateAdaptiveResolution();
            RecalculateProbesPositions();
        }

        // Recalculates resolution based on Adaptive Resolution
        public void RecalculateAdaptiveResolution() {
            Vector3 count = Vector3.Scale(Vector3.one, _lightVolume.GetScale()) * _voxelsPerUnit;
            int x = Mathf.Max((int)Mathf.Round(count.x), 1);
            int y = Mathf.Max((int)Mathf.Round(count.y), 1);
            int z = Mathf.Max((int)Mathf.Round(count.z), 1);
            _resolution = new Vector3Int(x, y, z);
        }

        // Recalculates probes world positions
        public void RecalculateProbesPositions() {
            _probesPositions = new Vector3[_resolution.x * _resolution.y * _resolution.z];
            Vector3 offset = new Vector3(0.5f, 0.5f, 0.5f);
            var pos = _lightVolume.GetPosition();
            var rot = _lightVolume.GetRotation();
            var scl = _lightVolume.GetScale();
            int id = 0;
            Vector3 localPos;
            for (int z = 0; z < _resolution.z; z++) {
                for (int y = 0; y < _resolution.y; y++) {
                    for (int x = 0; x < _resolution.x; x++) {
                        localPos = new Vector3((float)(x + 0.5f) / _resolution.x, (float)(y + 0.5f) / _resolution.y, (float)(z + 0.5f) / _resolution.z) - offset;
                        _probesPositions[id] = LVUtils.TransformPoint(localPos, pos, rot, scl);
                        id++;
                    }
                }
            }
        }

    }
}