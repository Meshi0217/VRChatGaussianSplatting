using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace VRCLightVolumes {

    [CanEditMultipleObjects]
    [CustomEditor(typeof(PointLightVolume))]
    public class PointLightVolumeEditor : Editor {

        PointLightVolume PointLightVolume;

        private void OnEnable() {
            PointLightVolume = (PointLightVolume)target;
        }

        public override void OnInspectorGUI() {

            serializedObject.Update();

            List<string> hiddenFields = new List<string> { "m_Script", "CustomID", "PointLightVolumeInstance", "LightVolumeSetup" };

            // Only show shadow radius if user requested baked shadows. Also area lights don't have shadow radius - it's implicit. 
            if (!PointLightVolume.BakedShadows || PointLightVolume.Type == PointLightVolume.LightType.AreaLight) {
                hiddenFields.Add("BakedShadowRadius");
            }
            
            if(PointLightVolume.Type == PointLightVolume.LightType.PointLight) {
                hiddenFields.Add("Angle");
                hiddenFields.Add("Falloff");
            }

            if (PointLightVolume.Type == PointLightVolume.LightType.AreaLight) {
                hiddenFields.Add("Angle");
                hiddenFields.Add("Falloff");
                hiddenFields.Add("Shape");
                hiddenFields.Add("Range");
                hiddenFields.Add("FalloffLUT");
                hiddenFields.Add("Cubemap");
                hiddenFields.Add("Cookie");
                hiddenFields.Add("LightSourceSize");
            }

            if (PointLightVolume.Shape == PointLightVolume.LightShape.Parametric) {
                hiddenFields.Add("FalloffLUT");
                hiddenFields.Add("Cubemap");
                hiddenFields.Add("Cookie");
                hiddenFields.Add("Range");
            } else if (PointLightVolume.Shape == PointLightVolume.LightShape.Custom) {
                hiddenFields.Add("Falloff");
                hiddenFields.Add("Range");
                if (PointLightVolume.Type == PointLightVolume.LightType.PointLight) {
                    hiddenFields.Add("FalloffLUT");
                    hiddenFields.Add("Cookie");
                } else if (PointLightVolume.Type == PointLightVolume.LightType.SpotLight) {
                    hiddenFields.Add("FalloffLUT");
                    hiddenFields.Add("Cubemap");
                }
            } else if (PointLightVolume.Shape == PointLightVolume.LightShape.LUT) {
                hiddenFields.Add("Falloff");
                hiddenFields.Add("Cubemap");
                hiddenFields.Add("Cookie");
                hiddenFields.Add("LightSourceSize");
            }

            DrawPropertiesExcluding(serializedObject, hiddenFields.ToArray());

            serializedObject.ApplyModifiedProperties();

        }

        private void DrawVolumeGUI(PointLightVolume pointLightVolume) {

            Transform t = pointLightVolume.transform;
            Vector3 origin = t.position;
            Vector3 lscale = pointLightVolume.transform.lossyScale;
            float scale = (lscale.x + lscale.y + lscale.z) / 3;
            float range = pointLightVolume.Type != PointLightVolume.LightType.AreaLight && (pointLightVolume.Shape != PointLightVolume.LightShape.LUT || pointLightVolume.FalloffLUT == null) ? pointLightVolume.LightSourceSize : pointLightVolume.Range;
            range *= scale;

            if (pointLightVolume.Type == PointLightVolume.LightType.PointLight) { // Point Light Visualization

                // Calculating

                float bounds = 0;

                bool isDebug = pointLightVolume.DebugRange && (pointLightVolume.Shape != PointLightVolume.LightShape.LUT || pointLightVolume.FalloffLUT == null);

                if (isDebug) {
                    bounds = Mathf.Sqrt(ComputePointLightSquaredBoundingSphere(pointLightVolume.Color, pointLightVolume.Intensity, range, pointLightVolume.LightVolumeSetup.LightsBrightnessCutoff));
                }

                // Drawing

                Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
                Handles.color = new Color(1f, 1f, 0f, 0.6f);
                DrawPointLight(origin, range);
                if (isDebug) {
                    DrawPointLight(origin, bounds);
                }

                Handles.zTest = UnityEngine.Rendering.CompareFunction.Greater;
                Handles.color = new Color(1f, 1f, 0f, 0.15f);
                DrawPointLight(origin, range);
                if (isDebug) {
                    DrawPointLight(origin, bounds);
                }

            } else if (pointLightVolume.Type == PointLightVolume.LightType.SpotLight) { // Spot Light Visualization

                // Calculating

                Vector3 forward = t.forward;
                Vector3 right = t.right;
                Vector3 up = t.up;

                float spotAngle = Mathf.Clamp(pointLightVolume.Angle, 0f, 360f);
                float halfAngleRad = spotAngle * 0.5f * Mathf.Deg2Rad;
                
                Vector3[] dirs = new Vector3[] { right, -right, up, -up };
                float bounds = 0;

                bool isDebug = pointLightVolume.DebugRange && (pointLightVolume.Shape != PointLightVolume.LightShape.LUT || pointLightVolume.FalloffLUT == null);

                if (isDebug) {
                    bounds = Mathf.Sqrt(ComputePointLightSquaredBoundingSphere(pointLightVolume.Color, pointLightVolume.Intensity, range, pointLightVolume.LightVolumeSetup.LightsBrightnessCutoff));
                }

                // Drawing

                Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
                Handles.color = new Color(1f, 1f, 0f, 0.6f);
                DrawSpotLight(origin, forward, halfAngleRad, range, dirs);

                if (isDebug)
                    DrawSpotLight(origin, forward, halfAngleRad, bounds, dirs);

                Handles.zTest = UnityEngine.Rendering.CompareFunction.Greater;
                Handles.color = new Color(1f, 1f, 0f, 0.15f);
                DrawSpotLight(origin, forward, halfAngleRad, range, dirs);

                if (isDebug) {
                    DrawSpotLight(origin, forward, halfAngleRad, bounds, dirs);
                }

            } else { // Area light

                float x = Mathf.Max(Mathf.Abs(pointLightVolume.transform.lossyScale.x), 0.001f);
                float y = Mathf.Max(Mathf.Abs(pointLightVolume.transform.lossyScale.y), 0.001f);

                Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
                Handles.color = new Color(1f, 1f, 0f, 0.6f);
                DrawAreaLight(origin, t.rotation, x, y);

                if(pointLightVolume.DebugRange)
                    DrawAreaLightDebug(origin, t.rotation, x, y, pointLightVolume.Color, pointLightVolume.Intensity, pointLightVolume.LightVolumeSetup.LightsBrightnessCutoff);

                Handles.zTest = UnityEngine.Rendering.CompareFunction.Greater;
                Handles.color = new Color(1f, 1f, 0f, 0.15f);
                DrawAreaLight(origin, t.rotation, x, y);

                if (pointLightVolume.DebugRange)
                    DrawAreaLightDebug(origin, t.rotation, x, y, pointLightVolume.Color, pointLightVolume.Intensity, pointLightVolume.LightVolumeSetup.LightsBrightnessCutoff);

            }

        }

        void OnSceneGUI() {
            foreach (var obj in Selection.gameObjects) {
                var volume = obj.GetComponent<PointLightVolume>();
                if (volume != null) {
                    DrawVolumeGUI(volume);
                }
            }
        }

        // Draws a spotlight visualization using precalculated values
        private void DrawSpotLight(Vector3 origin, Vector3 forward, float halfAngleRad, float range, Vector3[] dirs) {

            float centerOffset = range * Mathf.Cos(halfAngleRad);
            Vector3 diskCenter = origin + forward * centerOffset;
            float radius = Mathf.Abs(range) * Mathf.Sin(halfAngleRad);
            float angleDeg = Mathf.Rad2Deg * halfAngleRad;

            Handles.DrawWireDisc(diskCenter, forward, radius);

            foreach (var dir in dirs) {
                Vector3 edge = diskCenter + dir * radius;
                Handles.DrawLine(origin, edge);
                Handles.DrawWireArc(origin, dir, forward, angleDeg, range);
            }
        }

        // Draws a pointlight visualization
        private void DrawPointLight(Vector3 center, float radius) {
            Handles.DrawWireArc(center, Vector3.right, Vector3.up, 360, radius);
            Handles.DrawWireArc(center, Vector3.up, Vector3.forward, 360, radius);
            Handles.DrawWireArc(center, Vector3.forward, Vector3.right, 360, radius);
        }

        private void DrawAreaLight(Vector3 center, Quaternion rotation, float width, float height) {
            Vector3 right = rotation * Vector3.right * (width * 0.5f);
            Vector3 up = rotation * Vector3.up * (height * 0.5f);

            Vector3[] corners = new Vector3[4];
            corners[0] = center + right + up; // Top Right
            corners[1] = center - right + up; // Top Left
            corners[2] = center - right - up; // Bottom Left
            corners[3] = center + right - up; // Bottom Right

            // Draw the rectangle
            Handles.DrawLine(corners[0], corners[1]);
            Handles.DrawLine(corners[1], corners[2]);
            Handles.DrawLine(corners[2], corners[3]);
            Handles.DrawLine(corners[3], corners[0]);
            
            // Draw forward vector
            Handles.DrawLine(center, center + rotation * Vector3.forward * 0.5f);
        }

        private void DrawAreaLightDebug(Vector3 center, Quaternion rotation, float width, float height, Color color, float intensity, float cutoff) {

            // Light normal
            Vector3 up = rotation * Vector3.up;
            Vector3 right = rotation * Vector3.right;
            Vector3 forward = rotation * Vector3.forward;

            // Calculate the bounding sphere of the area light given the cutoff irradiance
            float minSolidAngle = Mathf.Clamp(cutoff / (Mathf.Max(color.r, Mathf.Max(color.g, color.b)) * intensity * Mathf.PI), -Mathf.PI * 2f, Mathf.PI * 2);
            float sqMaxDist = ComputeAreaLightSquaredBoundingSphere(width, height, minSolidAngle);
            float radius = Mathf.Sqrt(sqMaxDist);

            Handles.DrawWireDisc(center, forward, radius);
            Handles.DrawWireArc(center, right, up * radius, 180f, radius);
            Handles.DrawWireArc(center, up, -right * radius, 180f, radius);

        }

        float ComputeAreaLightSquaredBoundingSphere(float width, float height, float minSolidAngle) {
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

        float ComputePointLightSquaredBoundingSphere(Color color, float intensity, float size, float cutoff) {
            float L = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            return Mathf.Max(Mathf.PI * 2 * L * Mathf.Abs(intensity) / (cutoff * cutoff) - 1, 0) * size * size;
        }

    }

}