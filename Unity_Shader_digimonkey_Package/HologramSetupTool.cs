using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HologramURP.Editor
{
    public static class HologramSetupTool
    {
        private const string Root = "Assets/HologramURP";
        private const string MaterialFolder = Root + "/Materials";
        private const string MeshFolder = Root + "/Meshes";
        private const string SettingsFolder = Root + "/Settings";
        private const string VolumeProfilePath = SettingsFolder + "/HologramBloomProfile.asset";

        private const float ConeHeight = 1.9f;
        private const float OutlineConeTopRadius = 0.78f;
        private const float FillConeTopRadius = 1.2f;

        [MenuItem("Tools/Hologram URP/Create Materials")]
        public static void CreateMaterials()
        {
            EnsureFolder("Assets", "HologramURP");
            EnsureFolder(Root, "Materials");
            EnsureFolder(Root, "Meshes");
            EnsureFolder(Root, "Settings");

            CreateMaterialIfMissing(MaterialFolder + "/M_HologramObject.mat", "DiGiMonkey/Hologram Object");
            CreateMaterialIfMissing(MaterialFolder + "/M_HologramBeam.mat", "DiGiMonkey/Hologram Beam");
            CreateMaterialIfMissing(MaterialFolder + "/M_HologramBeamFillRadial.mat", "DiGiMonkey/Hologram Beam Fill Radial");
            CreateMaterialIfMissing(MaterialFolder + "/M_HologramXRayOutline.mat", "DiGiMonkey/XRay Outline");
            CreateMaterialIfMissing(MaterialFolder + "/M_HologramXRayMask.mat", "DiGiMonkey/XRay Mask");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Hologram URP materials are ready in Assets/HologramURP/Materials.");
        }

        [MenuItem("Tools/Hologram URP/Create Demo Objects")]
        public static void CreateDemoObjects()
        {
            CreateMaterials();

            Material objectMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + "/M_HologramObject.mat");
            Material beamMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + "/M_HologramBeam.mat");
            Material beamFillMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + "/M_HologramBeamFillRadial.mat");

            GameObject root = new GameObject("Hologram Demo");
            Undo.RegisterCreatedObjectUndo(root, "Create Hologram Demo");

            GameObject globe = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            globe.name = "Hologram Object (assign your globe texture)";
            globe.transform.SetParent(root.transform);
            globe.transform.localPosition = new Vector3(0f, 1.55f, 0f);
            globe.transform.localScale = Vector3.one * 1.5f;
            globe.GetComponent<Renderer>().sharedMaterial = objectMaterial;

            GameObject beam = new GameObject("Projection Beam Outline");
            beam.transform.SetParent(root.transform);
            beam.transform.localPosition = Vector3.zero;
            MeshFilter filter = beam.AddComponent<MeshFilter>();
            MeshRenderer renderer = beam.AddComponent<MeshRenderer>();
            filter.sharedMesh = GetOrCreateConeMesh("HologramProjectionCone.asset", ConeHeight, OutlineConeTopRadius);
            renderer.sharedMaterial = beamMaterial;

            GameObject beamFill = new GameObject("Projection Beam Interior and Radial Rays");
            beamFill.transform.SetParent(root.transform);
            beamFill.transform.localPosition = Vector3.zero;
            MeshFilter fillFilter = beamFill.AddComponent<MeshFilter>();
            MeshRenderer fillRenderer = beamFill.AddComponent<MeshRenderer>();
            fillFilter.sharedMesh = GetOrCreateConeMesh("HologramProjectionConeWide.asset", ConeHeight, FillConeTopRadius);
            fillRenderer.sharedMaterial = beamFillMaterial;

            SetupBloomAndPostProcessing();

            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
            Debug.Log("Hologram demo and Bloom setup created.");
        }

        [MenuItem("Tools/Hologram URP/Xray/Create XRay Outline Demo Objects")]
        public static void CreateXRayDemoObjects()
        {
            CreateMaterials();

            Material xrayMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + "/M_HologramXRayOutline.mat");
            Material xrayMaskMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + "/M_HologramXRayMask.mat");

            GameObject root = new GameObject("XRay Outline Demo");
            Undo.RegisterCreatedObjectUndo(root, "Create XRay Outline Demo");

            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Occluding Wall";
            wall.transform.SetParent(root.transform);
            wall.transform.localPosition = new Vector3(0f, 1f, 0f);
            wall.transform.localScale = new Vector3(3f, 2.5f, 0.3f);

            GameObject character = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            character.name = "XRay Character (assign your own body material to Element 0)";
            character.transform.SetParent(root.transform);
            character.transform.localPosition = new Vector3(0f, 1f, 1.2f);

            Shader litShader = Shader.Find("Universal Render Pipeline/Lit");
            Material bodyMaterial = litShader != null ? new Material(litShader) { name = "M_XRayCharacterBody_Demo" } : null;

            Renderer characterRenderer = character.GetComponent<Renderer>();
            characterRenderer.sharedMaterials = bodyMaterial != null
                ? new[] { bodyMaterial, xrayMaskMaterial, xrayMaterial }
                : new[] { xrayMaskMaterial, xrayMaterial };

            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
            Debug.Log("XRay Outline demo created. Element 0 is the normal body material, Element 1 is XRay Mask, Element 2 is the XRay overlay that only shows through the wall.");
        }

        [MenuItem("Tools/Hologram URP/Xray/Add XRay To Selected Object")]
        public static void AddXRayToSelectedObject()
        {
            GameObject[] selectedObjects = Selection.gameObjects;
            if (selectedObjects == null || selectedObjects.Length == 0)
            {
                Debug.LogWarning("Select one or more character objects first.");
                return;
            }

            CreateMaterials();
            Material maskMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + "/M_HologramXRayMask.mat");
            Material xrayMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + "/M_HologramXRayOutline.mat");

            int changed = 0;
            System.Collections.Generic.HashSet<Renderer> visited = new System.Collections.Generic.HashSet<Renderer>();
            System.Collections.Generic.List<Renderer> targets = new System.Collections.Generic.List<Renderer>();
            foreach (GameObject selectedObject in selectedObjects)
            {
                foreach (Renderer found in selectedObject.GetComponentsInChildren<Renderer>(true))
                {
                    if (visited.Add(found))
                        targets.Add(found);
                }
            }

            foreach (Renderer targetRenderer in targets)
            {
                if (!(targetRenderer is MeshRenderer) && !(targetRenderer is SkinnedMeshRenderer))
                    continue;

                Material[] current = targetRenderer.sharedMaterials;
                bool hasMask = System.Array.IndexOf(current, maskMaterial) >= 0;
                bool hasXRay = System.Array.IndexOf(current, xrayMaterial) >= 0;
                if (hasMask && hasXRay)
                    continue;

                System.Collections.Generic.List<Material> list = new System.Collections.Generic.List<Material>(current);
                if (!hasMask) list.Add(maskMaterial);
                if (!hasXRay) list.Add(xrayMaterial);

                Undo.RecordObject(targetRenderer, "Add XRay Materials");
                targetRenderer.sharedMaterials = list.ToArray();
                EditorUtility.SetDirty(targetRenderer);
                changed++;

                Mesh mesh = null;
                if (targetRenderer is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
                else if (targetRenderer.TryGetComponent(out MeshFilter filter)) mesh = filter.sharedMesh;
                if (mesh != null && mesh.subMeshCount > 1)
                    Debug.LogWarning(targetRenderer.name + " has multiple submeshes: the XRay materials only apply to the last submesh.");
            }

            Debug.Log("XRay Mask + XRay Outline were added to " + changed + " renderer(s).");
        }

        [MenuItem("Tools/Hologram URP/Xray/Remove XRay From Selected Objects")]
        public static void RemoveXRayFromSelectedObjects()
        {
            GameObject[] selectedObjects = Selection.gameObjects;
            if (selectedObjects == null || selectedObjects.Length == 0)
            {
                Debug.LogWarning("Select one or more character objects first.");
                return;
            }

            System.Collections.Generic.HashSet<Renderer> visited = new System.Collections.Generic.HashSet<Renderer>();
            int changed = 0;
            foreach (GameObject selectedObject in selectedObjects)
            {
                foreach (Renderer targetRenderer in selectedObject.GetComponentsInChildren<Renderer>(true))
                {
                    if (!visited.Add(targetRenderer))
                        continue;

                    System.Collections.Generic.List<Material> kept = new System.Collections.Generic.List<Material>();
                    bool removedAny = false;
                    foreach (Material material in targetRenderer.sharedMaterials)
                    {
                        if (material != null && material.shader != null &&
                            (material.shader.name == "DiGiMonkey/XRay Mask" || material.shader.name == "DiGiMonkey/XRay Outline"))
                        {
                            removedAny = true;
                            continue;
                        }
                        kept.Add(material);
                    }

                    if (!removedAny)
                        continue;

                    Undo.RecordObject(targetRenderer, "Remove XRay Materials");
                    targetRenderer.sharedMaterials = kept.ToArray();
                    EditorUtility.SetDirty(targetRenderer);
                    changed++;
                }
            }

            Debug.Log("XRay Mask + XRay Outline were removed from " + changed + " renderer(s).");
        }

        [MenuItem("Tools/Hologram URP/Setup Bloom and Post Processing")]
        public static void SetupBloomAndPostProcessing()
        {
            EnsureFolder("Assets", "HologramURP");
            EnsureFolder(Root, "Settings");

            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, VolumeProfilePath);
            }

            if (!profile.TryGet(out Bloom bloom))
                bloom = profile.Add<Bloom>(true);

            bloom.active = true;
            bloom.intensity.Override(1.6f);
            bloom.threshold.Override(0.7f);
            bloom.scatter.Override(0.7f);
            EditorUtility.SetDirty(profile);

            GameObject volumeObject = GameObject.Find("Hologram Global Volume");
            if (volumeObject == null)
            {
                volumeObject = new GameObject("Hologram Global Volume");
                Undo.RegisterCreatedObjectUndo(volumeObject, "Create Hologram Global Volume");
            }

            Volume volume = volumeObject.GetComponent<Volume>();
            if (volume == null)
                volume = Undo.AddComponent<Volume>(volumeObject);
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.sharedProfile = profile;
            EditorUtility.SetDirty(volume);

            Camera camera = Camera.main;
            if (camera == null)
                camera = Object.FindFirstObjectByType<Camera>();

            if (camera != null)
            {
                camera.allowHDR = true;
                UniversalAdditionalCameraData cameraData = camera.GetUniversalAdditionalCameraData();
                cameraData.renderPostProcessing = true;
                LayerMask volumeMask = cameraData.volumeLayerMask;
                volumeMask.value |= 1 << volumeObject.layer;
                cameraData.volumeLayerMask = volumeMask;
                EditorUtility.SetDirty(camera);
                EditorUtility.SetDirty(cameraData);
            }
            else
            {
                Debug.LogWarning("Camera was not found. Create a Camera, then run Bloom setup again.");
            }

            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urpAsset)
            {
                urpAsset.supportsHDR = true;
                EditorUtility.SetDirty(urpAsset);
            }

            AssetDatabase.SaveAssets();
            Debug.Log("Bloom enabled: Global Volume, Camera Post Processing, Camera HDR and URP HDR were configured.");
        }

        [MenuItem("Tools/Hologram URP/Fit Selected Object Vertical Gradient")]
        public static void FitSelectedObjectVerticalGradient()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Select a hologram object first.");
                return;
            }

            Renderer targetRenderer = selected.GetComponent<Renderer>();
            if (targetRenderer == null)
                targetRenderer = selected.GetComponentInChildren<Renderer>();

            if (targetRenderer == null || !TryGetLocalBounds(targetRenderer, out Bounds bounds))
            {
                Debug.LogWarning("A MeshRenderer, MeshFilter or SkinnedMeshRenderer with valid bounds was not found.");
                return;
            }

            int changedMaterials = 0;
            foreach (Material material in targetRenderer.sharedMaterials)
            {
                if (material == null || !material.HasProperty("_GradientBottomY") || !material.HasProperty("_GradientTopY"))
                    continue;

                bool useZAxis = material.HasProperty("_GradientUseZAxis") && material.GetFloat("_GradientUseZAxis") > 0.5f;
                int axis = material.HasProperty("_GradientAxis") ? Mathf.RoundToInt(material.GetFloat("_GradientAxis")) : 0;
                bool isObject = material.shader.name == "DiGiMonkey/Hologram Object";
                if (axis == 0) axis = useZAxis ? 3 : (isObject ? 2 : 4);
                if (axis == 4)
                {
                    Debug.LogWarning(material.name + ": Gradient Axis is UV; choose Local X, Y or Z to fit mesh bounds.");
                    continue;
                }
                float axisMin = axis == 1 ? bounds.min.x : (axis == 2 ? bounds.min.y : bounds.min.z);
                float axisMax = axis == 1 ? bounds.max.x : (axis == 2 ? bounds.max.y : bounds.max.z);

                Undo.RecordObject(material, "Fit Hologram Vertical Gradient");
                material.SetFloat("_GradientBottomY", axisMin);
                material.SetFloat("_GradientTopY", axisMax);
                EditorUtility.SetDirty(material);
                changedMaterials++;
            }

            AssetDatabase.SaveAssets();
            if (changedMaterials > 0)
                Debug.Log($"Vertical gradient fitted to {targetRenderer.name}.");
            else
                Debug.LogWarning("No local-axis hologram gradient was fitted. Check materials and Gradient Axis.");
        }

        [MenuItem("Tools/Hologram URP/Add Fill Radial To Selected Beam")]
        public static void AddFillRadialToSelectedBeam()
        {
            GameObject selected = Selection.activeGameObject;
            MeshFilter sourceFilter = selected != null ? selected.GetComponent<MeshFilter>() : null;
            if (selected == null || sourceFilter == null || sourceFilter.sharedMesh == null)
            {
                Debug.LogWarning("Select the existing projection beam object first.");
                return;
            }

            CreateMaterials();
            Material fillMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + "/M_HologramBeamFillRadial.mat");

            GameObject fillObject = new GameObject(selected.name + " Interior and Radial Rays");
            Undo.RegisterCreatedObjectUndo(fillObject, "Add Hologram Fill Radial Beam");
            fillObject.transform.SetParent(selected.transform.parent);
            fillObject.transform.localPosition = selected.transform.localPosition;
            fillObject.transform.localRotation = selected.transform.localRotation;
            fillObject.transform.localScale = selected.transform.localScale;

            MeshFilter fillFilter = fillObject.AddComponent<MeshFilter>();
            MeshRenderer fillRenderer = fillObject.AddComponent<MeshRenderer>();
            fillFilter.sharedMesh = sourceFilter.sharedMesh;
            fillRenderer.sharedMaterial = fillMaterial;
            Selection.activeGameObject = fillObject;
            Debug.Log("Interior fill and radial rays were added over the selected beam.");
        }

        private static bool TryGetLocalBounds(Renderer targetRenderer, out Bounds bounds)
        {
            if (targetRenderer is SkinnedMeshRenderer skinnedRenderer)
            {
                bounds = skinnedRenderer.localBounds;
                return true;
            }

            MeshFilter meshFilter = targetRenderer.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                bounds = meshFilter.sharedMesh.bounds;
                return true;
            }

            bounds = default;
            return false;
        }

        private static void CreateMaterialIfMissing(string path, string shaderName)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
                return;

            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError("Shader not found: " + shaderName);
                return;
            }

            AssetDatabase.CreateAsset(new Material(shader), path);
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, child);
        }

        private static Mesh GetOrCreateConeMesh(string assetFileName, float height, float topRadius)
        {
            string meshPath = MeshFolder + "/" + assetFileName;
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existing != null)
                return existing;

            const int segments = 48;
            Vector3[] vertices = new Vector3[(segments + 1) * 2];
            Vector3[] normals = new Vector3[vertices.Length];
            Vector2[] uvs = new Vector2[vertices.Length];
            int[] triangles = new int[segments * 6];

            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float angle = t * Mathf.PI * 2f;
                Vector3 radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                vertices[i * 2] = Vector3.zero;
                vertices[i * 2 + 1] = radial * topRadius + Vector3.up * height;
                normals[i * 2] = radial;
                normals[i * 2 + 1] = radial;
                uvs[i * 2] = new Vector2(t, 0f);
                uvs[i * 2 + 1] = new Vector2(t, 1f);
            }

            for (int i = 0; i < segments; i++)
            {
                int v = i * 2;
                int tri = i * 6;
                triangles[tri] = v;
                triangles[tri + 1] = v + 1;
                triangles[tri + 2] = v + 3;
                triangles[tri + 3] = v;
                triangles[tri + 4] = v + 3;
                triangles[tri + 5] = v + 2;
            }

            Mesh mesh = new Mesh { name = "Hologram Projection Cone" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, meshPath);
            AssetDatabase.SaveAssets();
            return mesh;
        }
    }
}
