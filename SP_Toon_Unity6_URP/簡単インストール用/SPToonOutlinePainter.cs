using System;
using Object = UnityEngine.Object;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#if UNITY_6000_3_OR_NEWER
using SPToonMaterialPropertyType = UnityEngine.Rendering.ShaderPropertyType;
#else
using SPToonMaterialPropertyType = UnityEditor.MaterialProperty.PropType;
#endif
#endif

namespace DiGiMonkey
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class SPToonOutlinePainter : MonoBehaviour
    {
        [Serializable]
        public sealed class Layer
        {
            public string name = "Layer";
            public int materialSlot = -1;
            public bool visible = true, locked;
            [Range(0, 1)] public float opacity = 1;
            public float[] offset, coverage, region, visibility;
            public Layer(int count)
            {
                offset = new float[count]; coverage = new float[count];
                region = new float[count]; visibility = new float[count];
                for (int i = 0; i < count; i++) { offset[i] = 1; region[i] = 1; visibility[i] = 1; }
            }
        }
        public Mesh sourceMesh;
        public List<Layer> layers = new List<Layer>();
        [Min(1)] public float outward = 2;
        [Min(0)] public float inward = 2;
        float appliedOutward, appliedInward;
        public bool showOutline = true, reverseFaces;
        public bool plateOutline, hairOutlineMask, outlineOverHair, faceOutlineOccluder;
        [SerializeField, HideInInspector] internal bool includeChildOutlineRenderers = false;
        [SerializeField, HideInInspector] internal List<Renderer> excludedChildOutlineRenderers = new List<Renderer>();
        sealed class ChildPlate
        {
            public Texture2D directions, neutralPaint;
            public bool curved;
        }
        static readonly Dictionary<Renderer, SPToonOutlinePainter> childStyleOwners = new Dictionary<Renderer, SPToonOutlinePainter>();
        readonly Dictionary<Mesh, ChildPlate> childPlates = new Dictionary<Mesh, ChildPlate>();
        readonly HashSet<Renderer> styledChildren = new HashSet<Renderer>();
        readonly HashSet<Renderer> currentChildren = new HashSet<Renderer>();
        readonly List<Renderer> childRenderers = new List<Renderer>();
        readonly List<Material> childMaterials = new List<Material>();
        double nextChildRefresh;
        Texture2D plateTexture;
        bool curvedPlate;
        Vector3[] plateDirections;
        bool appliedPlateStyle, appliedHairMask, appliedHairOverlay, appliedFaceOccluder;
        static readonly int CurvedPlateID = Shader.PropertyToID("_HullCurvedPlate");
        static readonly int PlateID = Shader.PropertyToID("_HullPlateEnabled");
        static readonly int PlateDataID = Shader.PropertyToID("_HullPlateDirections");
        static readonly int FaceOccluderID = Shader.PropertyToID("_HullFaceOccluder");
        static readonly int HairMaskID = Shader.PropertyToID("_HullHairMask");
        static readonly int OverHairID = Shader.PropertyToID("_HullOverHair");
        [NonSerialized] public Mesh previewMesh;
        Texture2D paintTexture;
        MaterialPropertyBlock block;
        static readonly int EnabledID = Shader.PropertyToID("_HullPaintEnabled");
        static readonly int SizeID = Shader.PropertyToID("_HullPaintDataSize");
        static readonly int DataID = Shader.PropertyToID("_HullPaintData");
        static readonly int ReverseID = Shader.PropertyToID("_HullPaintReverse");
        static readonly int ShowID = Shader.PropertyToID("_HullPaintShow");
        static readonly int CountID = Shader.PropertyToID("_HullPaintVertexCount");
        public Renderer SourceRenderer => GetComponent<Renderer>();
        public Mesh CurrentSource
        {
            get
            {
                var skin = GetComponent<SkinnedMeshRenderer>();
                var filter = GetComponent<MeshFilter>();
                return skin ? skin.sharedMesh : filter ? filter.sharedMesh : null;
            }
        }
        void OnEnable() { UpgradeLayers(); Rebuild(); }
        void OnDisable() { Cleanup(); }
        void OnDestroy() { Cleanup(); }
        void OnValidate() { outward = Mathf.Max(1, outward); inward = Mathf.Max(0, inward); }
        void UpgradeLayers()
        {
            foreach (var layer in layers)
                if (layer.visibility == null && layer.offset != null)
                {
                    layer.visibility = new float[layer.offset.Length];
                    for (int i = 0; i < layer.visibility.Length; i++) layer.visibility[i] = 1;
                }
        }
        void Cleanup()
        {
            ClearChildStyles();
            SetBlocks(false, false);
            if (plateTexture)
            {
                if (Application.isPlaying) Destroy(plateTexture); else DestroyImmediate(plateTexture);
            }
            plateTexture = null;
            plateDirections = null;
            if (paintTexture)
            {
                if (Application.isPlaying) Destroy(paintTexture); else DestroyImmediate(paintTexture);
            }
            paintTexture = null;
            if (previewMesh)
            {
                if (Application.isPlaying) Destroy(previewMesh); else DestroyImmediate(previewMesh);
            }
            previewMesh = null;
        }
        public bool IsValid => SourceRenderer && sourceMesh && sourceMesh.isReadable && layers.TrueForAll(l =>
            l.offset != null && l.offset.Length == sourceMesh.vertexCount &&
            l.coverage != null && l.coverage.Length == sourceMesh.vertexCount &&
            l.region != null && l.region.Length == sourceMesh.vertexCount &&
            l.visibility != null && l.visibility.Length == sourceMesh.vertexCount);
        public void Rebuild()
        {
            Cleanup(); UpgradeLayers();
            appliedPlateStyle = plateOutline;
            appliedHairMask = hairOutlineMask;
            appliedHairOverlay = outlineOverHair;
            appliedFaceOccluder = faceOutlineOccluder;
            SetBlocks(false);
            RefreshChildStyles();
            if (!IsValid || CurrentSource != sourceMesh || SourceRenderer.isPartOfStaticBatch) return;
            previewMesh = Instantiate(sourceMesh);
            previewMesh.name = sourceMesh.name + " Hull Paint Data (generated)";
            previewMesh.hideFlags = HideFlags.HideAndDontSave;
            int width = Mathf.Min(4096, SystemInfo.maxTextureSize, sourceMesh.vertexCount);
            int height = Mathf.CeilToInt((float)sourceMesh.vertexCount / width);
            if (height > SystemInfo.maxTextureSize) return;
            paintTexture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
            {
                name = "SP Toon Hull Paint Vertex Data", hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            if (plateOutline) BuildPlateTexture(width, height);
            Apply();
        }

        struct BoundaryEdge
        {
            public int count;
            public Vector3 outward;
        }

        static void AddBoundaryEdge(Dictionary<(int, int), BoundaryEdge> edges, int from, int to, Vector3 outward)
        {
            if (from == to) return;
            var key = from < to ? (from, to) : (to, from);
            edges.TryGetValue(key, out var edge);
            if (edge.count == 0) edge.outward = outward;
            edge.count++;
            edges[key] = edge;
        }

        public static Vector3[] GetBoundaryDirections(Mesh mesh)
        {
            var positions = mesh.vertices;
            var ids = new Dictionary<Vector3, int>();
            var welded = new int[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                if (!ids.TryGetValue(positions[i], out int index))
                {
                    index = ids.Count;
                    ids.Add(positions[i], index);
                }
                welded[i] = index;
            }

            var edges = new Dictionary<(int, int), BoundaryEdge>();
            for (int slot = 0; slot < mesh.subMeshCount; slot++)
            {
                if (mesh.GetTopology(slot) != MeshTopology.Triangles) continue;
                var indices = mesh.GetTriangles(slot);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    var ab = positions[b] - positions[a];
                    var bc = positions[c] - positions[b];
                    var ca = positions[a] - positions[c];
                    var normal = Vector3.Cross(ab, bc).normalized;
                    if (normal == Vector3.zero) continue;
                    AddBoundaryEdge(edges, welded[a], welded[b], Vector3.Cross(ab, normal).normalized);
                    AddBoundaryEdge(edges, welded[b], welded[c], Vector3.Cross(bc, normal).normalized);
                    AddBoundaryEdge(edges, welded[c], welded[a], Vector3.Cross(ca, normal).normalized);
                }
            }

            var sums = new Vector3[ids.Count];
            var first = new Vector3[ids.Count];
            foreach (var pair in edges)
            {
                if (pair.Value.count != 1) continue;
                var (a, b) = pair.Key;
                var outward = pair.Value.outward;
                sums[a] += outward;
                sums[b] += outward;
                if (first[a] == Vector3.zero) first[a] = outward;
                if (first[b] == Vector3.zero) first[b] = outward;
            }
            var directions = new Vector3[positions.Length];
            for (int i = 0; i < directions.Length; i++)
            {
                int index = welded[i];
                var average = sums[index].normalized;
                directions[i] = average / Mathf.Max(.5f, Vector3.Dot(average, first[index]));
            }
            return directions;
        }

        void BuildPlateTexture(int width, int height)
        {
            plateDirections = GetBoundaryDirections(sourceMesh);
            plateTexture = CreatePlateTexture(width, height, plateDirections);
            curvedPlate = plateTexture && IsCurvedPlate(sourceMesh);
        }

        static bool IsCurvedPlate(Mesh mesh)
        {
            var vertices = mesh.vertices;
            if (vertices.Length < 4) return false;
            Vector3 origin = vertices[0];
            Vector3 edge = Vector3.zero;
            Vector3 normal = Vector3.zero;
            foreach (var vertex in vertices)
            {
                Vector3 offset = vertex - origin;
                if (edge == Vector3.zero)
                {
                    if (offset.sqrMagnitude > 1e-10f) edge = offset.normalized;
                    continue;
                }
                normal = Vector3.Cross(edge, offset);
                if (normal.sqrMagnitude > 1e-10f) break;
            }
            if (normal.sqrMagnitude <= 1e-10f) return false;
            normal.Normalize();
            float tolerance = Mathf.Max(.000001f, mesh.bounds.size.magnitude * .00001f);
            return Array.Exists(vertices, vertex => Mathf.Abs(Vector3.Dot(vertex - origin, normal)) > tolerance);
        }

        static Texture2D CreatePlateTexture(int width, int height, Vector3[] directions)
        {
            if (!Array.Exists(directions, direction => direction != Vector3.zero)) return null;
            var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
            {
                name = "SP Toon Plate Boundary Directions", hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color[width * height];
            for (int i = 0; i < directions.Length; i++)
            {
                var direction = directions[i];
                pixels[i] = new Color(direction.x, direction.y, direction.z, 0);
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        internal static bool SupportsChildStyle(Renderer renderer)
        {
            if (!(renderer is SkinnedMeshRenderer || (renderer is MeshRenderer && renderer.GetComponent<MeshFilter>())))
                return false;
            return Array.Exists(renderer.sharedMaterials, material => material && material.HasProperty(EnabledID));
        }

        internal static SPToonOutlinePainter GetChildStyleParent(Renderer renderer)
        {
            for (var ancestor = renderer.transform.parent; ancestor; ancestor = ancestor.parent)
            {
                var painter = ancestor.GetComponent<SPToonOutlinePainter>();
                if (painter && painter.isActiveAndEnabled && painter.includeChildOutlineRenderers &&
                    !painter.excludedChildOutlineRenderers.Contains(renderer)) return painter;
            }
            return null;
        }

        ChildPlate GetChildPlate(Renderer renderer)
        {
            if (!plateOutline || renderer.isPartOfStaticBatch) return null;
            var mesh = renderer is SkinnedMeshRenderer skinned
                ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>().sharedMesh;
            if (!mesh || !mesh.isReadable || mesh.vertexCount == 0) return null;
            if (childPlates.TryGetValue(mesh, out var cached)) return cached;

            int width = Mathf.Min(4096, SystemInfo.maxTextureSize, mesh.vertexCount);
            int height = Mathf.CeilToInt((float)mesh.vertexCount / width);
            if (height > SystemInfo.maxTextureSize) return null;
            var directions = CreatePlateTexture(width, height, GetBoundaryDirections(mesh));
            if (!directions)
            {
                childPlates.Add(mesh, null);
                return null;
            }

            var neutral = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                name = "SP Toon Inherited Outline Data", hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color32[width * height];
            Array.Fill(pixels, new Color32(255, 255, 0, 255));
            neutral.SetPixels32(pixels);
            neutral.Apply(false, true);
            cached = new ChildPlate { directions = directions, neutralPaint = neutral, curved = IsCurvedPlate(mesh) };
            childPlates.Add(mesh, cached);
            return cached;
        }

        void WriteChildBlock(bool enabled, ChildPlate plate)
        {
            block.SetFloat(EnabledID, enabled ? 1 : 0);
            block.SetFloat(PlateID, plate != null ? 1 : 0);
            block.SetFloat(CurvedPlateID, plate != null && plate.curved ? 1 : 0);
            block.SetTexture(PlateDataID, plate != null ? plate.directions : Texture2D.blackTexture);
            block.SetTexture(DataID, plate != null ? plate.neutralPaint : Texture2D.whiteTexture);
            if (plate != null)
            {
                var texture = plate.neutralPaint;
                block.SetVector(SizeID, new Vector4(texture.width, texture.height, 0, 0));
            }
            block.SetFloat(CountID, plate != null ? plate.neutralPaint.width * plate.neutralPaint.height : 0);
            block.SetFloat(HairMaskID, enabled && hairOutlineMask ? 1 : 0);
            block.SetFloat(FaceOccluderID, enabled && faceOutlineOccluder ? 1 : 0);
            block.SetFloat(OverHairID, enabled && outlineOverHair ? 1 : 0);
            block.SetFloat(ShowID, showOutline ? 1 : 0);
            block.SetFloat(ReverseID, reverseFaces ? 1 : 0);
        }

        void SetChildBlocks(Renderer renderer, bool enabled)
        {
            if (block == null) block = new MaterialPropertyBlock();
            var plate = enabled ? GetChildPlate(renderer) : null;
            renderer.GetPropertyBlock(block);
            WriteChildBlock(enabled, plate);
            renderer.SetPropertyBlock(block);
            renderer.GetSharedMaterials(childMaterials);
            for (int slot = 0; slot < childMaterials.Count; slot++)
            {
                renderer.GetPropertyBlock(block, slot);
                if (block.isEmpty) continue;
                WriteChildBlock(enabled, plate);
                renderer.SetPropertyBlock(block, slot);
            }
        }

        internal void RefreshChildStyles()
        {
            nextChildRefresh = Time.realtimeSinceStartupAsDouble + .25;
            if (!isActiveAndEnabled || !includeChildOutlineRenderers)
            {
                ClearChildStyles();
                return;
            }

            currentChildren.Clear();
            GetComponentsInChildren(true, childRenderers);
            foreach (var renderer in childRenderers)
            {
                if (renderer.gameObject == gameObject || !SupportsChildStyle(renderer) ||
                    renderer.GetComponent<SPToonOutlinePainter>() || GetChildStyleParent(renderer) != this)
                    continue;

                SetChildBlocks(renderer, true);
                childStyleOwners[renderer] = this;
                currentChildren.Add(renderer);
            }

            foreach (var renderer in styledChildren)
                if (!currentChildren.Contains(renderer)) ReleaseChildStyle(renderer);
            styledChildren.Clear();
            styledChildren.UnionWith(currentChildren);
        }

        void ReleaseChildStyle(Renderer renderer)
        {
            if (!childStyleOwners.TryGetValue(renderer, out var owner) || owner != this) return;
            childStyleOwners.Remove(renderer);
            if (renderer && !renderer.GetComponent<SPToonOutlinePainter>()) SetChildBlocks(renderer, false);
        }

        void ClearChildStyles()
        {
            foreach (var renderer in styledChildren) ReleaseChildStyle(renderer);
            styledChildren.Clear();
            currentChildren.Clear();
            foreach (var plate in childPlates.Values)
            {
                if (plate == null) continue;
                if (Application.isPlaying)
                {
                    Destroy(plate.directions);
                    Destroy(plate.neutralPaint);
                }
                else
                {
                    DestroyImmediate(plate.directions);
                    DestroyImmediate(plate.neutralPaint);
                }
            }
            childPlates.Clear();
        }

        public bool HasPlateOutline => plateOutline && plateTexture;
        public bool HasCurvedPlateOutline => HasPlateOutline && curvedPlate;

        public Vector3 PlateDirectionWorld(int vertexIndex)
        {
            var direction = plateDirections[vertexIndex];
            return transform.localToWorldMatrix.MultiplyVector(direction).normalized * direction.magnitude;
        }

        public static Color ComposeVertex(List<Layer> stack, int i, float outward = 2, float inward = 2)
        {
            // Unpainted vertices retain the material's existing Hull Outline.
            float amount = 1, visibility = 1;
            foreach (var layer in stack)
            {
                if (!layer.visible) continue;
                float alpha = Mathf.Clamp01(layer.coverage[i] * layer.opacity * layer.region[i]);
                float displacement = layer.offset[i] * (layer.offset[i] < 0 ? inward : outward);
                amount = Mathf.Lerp(amount, displacement, alpha);
                visibility = Mathf.Lerp(visibility, layer.visibility[i], alpha);
            }
            return new Color(amount, visibility, 0, 1);
        }
        public void Apply()
        {
            if (!previewMesh || !paintTexture || !IsValid) return;
            var colors = new Color[sourceMesh.vertexCount];
            for (int i = 0; i < colors.Length; i++) colors[i] = ComposeVertex(layers, i, outward, inward);
            // Float vertex-color data is uploaded separately: original mesh/color streams stay intact.
            previewMesh.colors = colors;
            var pixels = new Color[paintTexture.width * paintTexture.height];
            Array.Copy(colors, pixels, colors.Length);
            paintTexture.SetPixels(pixels); paintTexture.Apply(false, false);
            appliedOutward = outward; appliedInward = inward;
            SetBlocks(true);
        }
        void ConfigureBlock(bool enabled, bool applyStyle)
        {
            block.SetFloat(EnabledID, enabled ? 1 : 0);
            block.SetFloat(PlateID, enabled && HasPlateOutline ? 1 : 0);
            block.SetFloat(CurvedPlateID, enabled && HasCurvedPlateOutline ? 1 : 0);
            block.SetTexture(PlateDataID, enabled && HasPlateOutline ? plateTexture : Texture2D.blackTexture);
            block.SetFloat(HairMaskID, applyStyle && hairOutlineMask ? 1 : 0);
            block.SetFloat(FaceOccluderID, applyStyle && faceOutlineOccluder ? 1 : 0);
            block.SetFloat(OverHairID, applyStyle && outlineOverHair ? 1 : 0);
            if (!enabled) { block.SetTexture(DataID, Texture2D.whiteTexture); return; }
            block.SetTexture(DataID, paintTexture);
            block.SetVector(SizeID, new Vector4(paintTexture.width, paintTexture.height, 0, 0));
            block.SetFloat(CountID, sourceMesh.vertexCount);
            block.SetFloat(ReverseID, reverseFaces ? 1 : 0);
            block.SetFloat(ShowID, showOutline ? 1 : 0);
        }
        void SetBlocks(bool enabled, bool applyStyle = true)
        {
            var renderer = SourceRenderer;
            if (!renderer) return;
            if (block == null) block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block); ConfigureBlock(enabled, applyStyle); renderer.SetPropertyBlock(block);
            var materials = renderer.sharedMaterials;
            for (int slot = 0; slot < materials.Length; slot++)
            {
                renderer.GetPropertyBlock(block, slot);
                // Preserve existing per-material overrides without introducing new slot overrides.
                if (block.isEmpty) continue;
                ConfigureBlock(enabled, applyStyle); renderer.SetPropertyBlock(block, slot);
            }
        }
        void LateUpdate()
        {
            if ((includeChildOutlineRenderers || styledChildren.Count > 0) &&
                (!Application.isPlaying || Time.realtimeSinceStartupAsDouble >= nextChildRefresh))
                RefreshChildStyles();

            if (plateOutline != appliedPlateStyle || hairOutlineMask != appliedHairMask ||
                outlineOverHair != appliedHairOverlay || faceOutlineOccluder != appliedFaceOccluder)
            {
                Rebuild();
                return;
            }
            if (!paintTexture) return;
            if (appliedOutward != outward || appliedInward != inward) Apply();
            SetBlocks(isActiveAndEnabled && IsValid && CurrentSource == sourceMesh && !SourceRenderer.isPartOfStaticBatch);
        }
    }
}

#if UNITY_EDITOR

namespace DiGiMonkey
{
[CustomEditor(typeof(SPToonOutlinePainter))]
    public sealed class SPToonOutlinePainterInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            var painter = (SPToonOutlinePainter)target;
            EditorGUILayout.HelpBox("既存Hull Outlineの調整とレイヤー編集は専用ウィンドウで行います。", MessageType.Info);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("元メッシュ", painter.sourceMesh, typeof(Mesh), false);
            EditorGUILayout.LabelField("保存レイヤー数", painter.layers.Count.ToString());
            EditorGUI.BeginChangeCheck();
            bool plate = EditorGUILayout.Toggle("片面板の外周線", painter.plateOutline);
            bool hair = EditorGUILayout.Toggle("髪の遮蔽マスク", painter.hairOutlineMask);
            bool occluder = EditorGUILayout.Toggle("顔で眉毛の線を遮る", painter.faceOutlineOccluder);
            bool brow = EditorGUILayout.Toggle("輪郭線を髪の手前に", painter.outlineOverHair);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(painter, "Change SP Toon Outline Style");
                painter.plateOutline = plate;
                painter.hairOutlineMask = hair;
                painter.outlineOverHair = brow;
                painter.faceOutlineOccluder = occluder;
                if (!painter.sourceMesh) painter.sourceMesh = painter.CurrentSource;
                painter.Rebuild();
                PrefabUtility.RecordPrefabInstancePropertyModifications(painter);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(painter.gameObject.scene);
                SceneView.RepaintAll();
            }
            if (painter.SourceRenderer && painter.plateOutline && !painter.HasPlateOutline)
                EditorGUILayout.HelpBox("外周を持つRead/Write有効のメッシュが必要です。", MessageType.Info);
            using (new EditorGUI.DisabledScope(!painter.SourceRenderer))
                if (GUILayout.Button("Outline Paintを開く")) SPToonOutlinePaintWindow.OpenFor(painter);
            DrawChildOutlineSettings(painter);
        }

        static void DrawChildOutlineSettings(SPToonOutlinePainter parent)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("子階層への適用", EditorStyles.boldLabel);
            bool includeChildren = EditorGUILayout.Toggle("子階層も対象にする", parent.includeChildOutlineRenderers);
            if (includeChildren != parent.includeChildOutlineRenderers)
            {
                Undo.RecordObject(parent, "Set child outline scope");
                parent.includeChildOutlineRenderers = includeChildren;
                parent.RefreshChildStyles();
                PrefabUtility.RecordPrefabInstancePropertyModifications(parent);
            }
            if (!includeChildren) return;

            foreach (var renderer in parent.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.gameObject == parent.gameObject || !SPToonOutlinePainter.SupportsChildStyle(renderer))
                    continue;

                bool ownPainter = renderer.GetComponent<SPToonOutlinePainter>();
                bool included = !ownPainter && !parent.excludedChildOutlineRenderers.Contains(renderer);
                using (new EditorGUI.DisabledScope(ownPainter))
                {
                    bool next = EditorGUILayout.ToggleLeft(renderer.name + (ownPainter ? "（個別設定）" : ""), included);
                    if (next == included) continue;

                    Undo.RecordObject(parent, "Select child outline renderer");
                    if (next) parent.excludedChildOutlineRenderers.Remove(renderer);
                    else parent.excludedChildOutlineRenderers.Add(renderer);
                    parent.RefreshChildStyles();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(parent);
                }
            }

            EditorGUILayout.HelpBox("チェックした子の描画設定はこの親で管理します。子にPainterがある場合は、子自身の設定とペイントを使います。", MessageType.Info);
        }
    }
    public sealed class SPToonOutlinePaintWindow : EditorWindow
    {
        [SerializeField] SPToonOutlinePainter painter;
        [SerializeField] Material targetMaterial;
        [Serializable] sealed class TargetSelection
        {
            public SPToonOutlinePainter painter;
            public bool enabled=true;
            public int layer;
        }
        [SerializeField] List<TargetSelection> paintTargets = new List<TargetSelection>();
        readonly Dictionary<SPToonOutlinePainter,SPToonOutlinePaintWindow> workers = new Dictionary<SPToonOutlinePainter,SPToonOutlinePaintWindow>();
        static bool creatingWorker;
        bool isWorker;
        SPToonOutlinePaintWindow coordinator;
        TargetSelection StateFor(SPToonOutlinePainter component)
        {
            return paintTargets.Find(t=>t.painter==component);
        }
        void RememberLayer()
        {
            var state=painter ? StateFor(painter) : null;
            if(state!=null) state.layer=selected;
        }
        void ClearWorkers()
        {
            foreach(var worker in workers.Values) if(worker) {worker.stroke=false;DestroyImmediate(worker);}
            workers.Clear();
        }
        void FocusTarget(SPToonOutlinePainter component)
        {
            RememberLayer(); painter=component;
            var state=StateFor(component);selected=state!=null ? state.layer : 0;
            submesh=-1; Repaint();
        }
        List<Renderer> MaterialTargets()
        {
            var result=new List<Renderer>();
            foreach(var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if(!renderer.gameObject.scene.IsValid() || EditorUtility.IsPersistent(renderer)
                    || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(renderer.gameObject.scene)
                    || Array.IndexOf(renderer.sharedMaterials,targetMaterial)<0) continue;
                if(renderer.GetComponent<MeshFilter>() || renderer is SkinnedMeshRenderer) result.Add(renderer);
            }
            result.Sort((a,b)=>string.CompareOrdinal(a.name,b.name));
            return result;
        }
        void EnableTarget(GameObject go,bool enabled)
        {
            EndBatchStroke(); ClearWorkers();
            var component=go.GetComponent<SPToonOutlinePainter>();
            if (enabled && !component)
            {
                var renderer = go.GetComponent<Renderer>();
                var inherited = renderer ? SPToonOutlinePainter.GetChildStyleParent(renderer) : null;
                component = Undo.AddComponent<SPToonOutlinePainter>(go);
                if (inherited)
                {
                    Undo.RecordObject(component, "Keep inherited outline style");
                    component.plateOutline = inherited.plateOutline;
                    component.hairOutlineMask = inherited.hairOutlineMask;
                    component.outlineOverHair = inherited.outlineOverHair;
                    component.faceOutlineOccluder = inherited.faceOutlineOccluder;
                    component.showOutline = inherited.showOutline;
                    component.reverseFaces = inherited.reverseFaces;
                }
            }
            if(!component) return;
            var state=StateFor(component);
            if(state==null) {state=new TargetSelection{painter=component,enabled=false};paintTargets.Add(state);}
            bool joining=enabled && !state.enabled;
            var reference=painter && painter!=component && StateFor(painter)!=null && StateFor(painter).enabled ? painter : null;
            if(!reference)foreach(var candidate in paintTargets)if(candidate.enabled && candidate.painter && candidate.painter!=component){reference=candidate.painter;break;}
            state.enabled=enabled;
            if(enabled && !component.sourceMesh)
            {
                Undo.RecordObject(component,"Initialize Hull Outline Adjustment");
                component.sourceMesh=component.CurrentSource;
                if(component.sourceMesh && component.sourceMesh.isReadable)
                    component.layers.Add(new SPToonOutlinePainter.Layer(component.sourceMesh.vertexCount){name=go.name});
                Undo.RecordObject(go,"Disable Static Batching For Hull Paint");
                GameObjectUtility.SetStaticEditorFlags(go,GameObjectUtility.GetStaticEditorFlags(go)&~StaticEditorFlags.BatchingStatic);
                EditorUtility.SetDirty(component);PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
            if(joining && reference && component.sourceMesh)
            {
                Undo.RecordObject(component,"Apply Checked Hull Settings");CopySharedSettings(reference,component);
                state.layer=Mathf.Clamp(selected,0,Mathf.Max(0,component.layers.Count-1));
                EditorUtility.SetDirty(component);PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
            if(enabled) {if(!component.previewMesh) component.Rebuild();else component.Apply();}
            if(!painter && enabled) FocusTarget(component);
        }
        SPToonOutlinePaintWindow WorkerFor(TargetSelection state)
        {
            if(!workers.TryGetValue(state.painter,out var worker) || !worker)
            {
                creatingWorker=true;
                try {worker=CreateInstance<SPToonOutlinePaintWindow>();}
                finally {creatingWorker=false;}
                worker.hideFlags=HideFlags.HideAndDontSave;worker.coordinator=this;
                workers[state.painter]=worker;
            }
            worker.painter=state.painter;
            worker.selected=Mathf.Clamp(state.layer,0,Mathf.Max(0,state.painter.layers.Count-1));
            int slot=state.painter.layers.Count>0 ? state.painter.layers[worker.selected].materialSlot : -1;
            if(worker.submesh!=slot || worker.paintHull!=paintHull) worker.ReleaseProbe();
            worker.submesh=slot;worker.paintHull=paintHull;
            worker.radius=radius;worker.strength=strength;worker.hardness=hardness;
            worker.painting=painting;worker.through=through;worker.mode=mode;worker.regionMode=regionMode;
            float multiplier=target*(target<0 && painter ? painter.inward : painter ? painter.outward : 2);
            worker.target=multiplier/Mathf.Max(.0001f,multiplier<0 ? state.painter.inward : state.painter.outward);
            return worker;
        }
        List<SPToonOutlinePaintWindow> PrepareWorkers(bool refreshPose)
        {
            RememberLayer();
            var active=new List<SPToonOutlinePaintWindow>();
            foreach(var state in paintTargets)
            {
                if(!state.enabled || !state.painter || !state.painter.isActiveAndEnabled || !state.painter.IsValid
                    || state.painter.CurrentSource!=state.painter.sourceMesh || state.painter.layers.Count==0) continue;
                var worker=WorkerFor(state);
                if(refreshPose || !worker.collider) worker.Prepare();
                active.Add(worker);
            }
            return active;
        }
        void BeginBatchStroke()
        {
            if(stroke) return;
            Undo.IncrementCurrentGroup();undoGroup=Undo.GetCurrentGroup();
            var objects=new List<Object>();
            foreach(var state in paintTargets)
                if(state.enabled && state.painter && state.painter.IsValid) objects.Add(state.painter);
            if(objects.Count>0) Undo.RegisterCompleteObjectUndo(objects.ToArray(),"Paint Hull Outlines");
            stroke=true;
        }
        void EndBatchStroke()
        {
            foreach(var worker in workers.Values) if(worker) worker.stroke=false;
            EndStroke();
        }

        int selected, submesh = -1, mode, regionMode;
        bool painting, through, stroke;
        float radius = .1f, strength = .2f, hardness = .2f, target = .5f;
        Vector2 scroll;
        Mesh posed;
        GameObject probe;
        MeshCollider collider, sourceCollider;
        Mesh hullMesh;
        [SerializeField] bool paintHull = true;
        int[] hullSources, hullTriangles;
        float[] hullVisibility, brushWeights;
        Vector3[] vertices, normals;
        bool[] allowed;
        int undoGroup;
        double lastDab;
        Vector2 lastMouse;
        readonly string[] modes = { "太さ", "線を消す", "範囲マスク", "調整を戻す" };
        const string SampleFolder = "Assets/model/Materials/";
        const string FaceSampleMenu = "Tools/DiGiMonkey/Create Face Sample";

        [MenuItem(FaceSampleMenu)]
        static void CreateFaceSample()
        {
            if (!EnsureSampleRenderFeatures()) return;

            var skin = AssetDatabase.LoadAssetAtPath<Material>(SampleFolder + "00_MeetMat.mat");
            var hair = AssetDatabase.LoadAssetAtPath<Material>(SampleFolder + "00_MeetMat 1.mat");
            var brow = AssetDatabase.LoadAssetAtPath<Material>(SampleFolder + "00_MeetMat 2.mat");
            if (!skin || !hair || !brow)
            {
                var shader = Shader.Find("DiGiMonkey/SP Toon URP");
                if (!shader)
                {
                    Debug.LogError("Create Face Sample could not find the SP Toon URP shader.");
                    return;
                }

                const string sampleAssets = "Assets/SPToonFaceSample";
                if (!AssetDatabase.IsValidFolder(sampleAssets))
                    AssetDatabase.CreateFolder("Assets", "SPToonFaceSample");

                if (!skin) skin = GetSampleMaterial(sampleAssets, shader, "Face",
                    new Color(1f, .94f, .70f), .022f);
                if (!hair) hair = GetSampleMaterial(sampleAssets, shader, "Hair",
                    new Color(.52f, .44f, .44f), .014f);
                if (!brow) brow = GetSampleMaterial(sampleAssets, shader, "Eyebrows",
                    new Color(.36f, .48f, .31f), .009f);
                if (!skin || !hair || !brow) return;
            }

            var sample = new GameObject("SPToonFaceSample");
            var face = CreateSamplePart(PrimitiveType.Sphere, sample.transform, "Face", Vector3.zero, Vector3.one, skin);
            face.faceOutlineOccluder = true;
            face.Rebuild();

            var hairGroup = new GameObject("Hair");
            hairGroup.transform.SetParent(sample.transform, false);
            CreateSampleHairPlane(hairGroup.transform, "Hair Top", new Vector3(0, .36f, -.52f),
                new Vector2(.74f, .26f), 0, hair);
            CreateSampleHairPlane(hairGroup.transform, "Bangs Left", new Vector3(-.23f, .26f, -.54f),
                new Vector2(.19f, .40f), -17, hair);
            CreateSampleHairPlane(hairGroup.transform, "Bangs Center", new Vector3(.035f, .31f, -.545f),
                new Vector2(.14f, .34f), 17, hair);
            CreateSampleHairPlane(hairGroup.transform, "Bangs Right", new Vector3(.24f, .25f, -.535f),
                new Vector2(.18f, .40f), 20, hair);
            CreateSampleHairPlane(hairGroup.transform, "Hair Back", new Vector3(0, .10f, .53f),
                new Vector2(.72f, .77f), 0, hair, 180);
            CreateSampleHairPlane(hairGroup.transform, "Hair Back Left", new Vector3(-.38f, .07f, .39f),
                new Vector2(.29f, .72f), -8, hair, 135);
            CreateSampleHairPlane(hairGroup.transform, "Hair Back Right", new Vector3(.38f, .07f, .39f),
                new Vector2(.29f, .72f), 8, hair, 225);
            var hairPainter = hairGroup.AddComponent<SPToonOutlinePainter>();
            hairPainter.includeChildOutlineRenderers = true;
            hairPainter.plateOutline = true;
            hairPainter.hairOutlineMask = true;
            hairPainter.Rebuild();

            // Unity's Plane is ten units across and faces upward before rotation.
            var browScale = new Vector3(.021f, 1, .0043f);
            var left = CreateSamplePart(PrimitiveType.Plane, sample.transform, "Brow Left",
                new Vector3(-.16f, .15f, -.478f), browScale, brow);
            var right = CreateSamplePart(PrimitiveType.Plane, sample.transform, "Brow Right",
                new Vector3(.16f, .15f, -.478f), browScale, brow);
            left.transform.localRotation = Quaternion.Euler(-90, 14, -7);
            right.transform.localRotation = Quaternion.Euler(-90, -14, 7);
            foreach (var eyebrow in new[] { left, right })
            {
                eyebrow.plateOutline = true;
                eyebrow.outlineOverHair = true;
                eyebrow.Rebuild();
            }

            Undo.RegisterCreatedObjectUndo(sample, "Create SP Toon Face Sample");
            Selection.activeGameObject = sample;
        }

        static Material GetSampleMaterial(string folder, Shader shader, string name, Color color, float width)
        {
            string path = folder + "/" + name + ".mat";
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset)
            {
                if (asset is Material existing && existing.shader == shader)
                {
                    // Repair sample assets created before the command initialized the shadow palette.
                    bool usesGrayPalette = true;
                    for (int band = 2; band <= 7; band++)
                    {
                        var shade = existing.GetColor("_ToonColor" + band);
                        usesGrayPalette &= Mathf.Abs(shade.r - shade.g) < .001f &&
                            Mathf.Abs(shade.r - shade.b) < .001f;
                    }
                    if (usesGrayPalette)
                    {
                        ApplySamplePalette(existing, existing.GetColor("_BaseColor"));
                        EditorUtility.SetDirty(existing);
                        AssetDatabase.SaveAssetIfDirty(existing);
                    }
                    return existing;
                }
                Debug.LogError("Create Face Sample cannot reuse this asset: " + path);
                return null;
            }

            var material = new Material(shader) { name = name, renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry };
            ApplySamplePalette(material, color);
            material.SetFloat("_HullOutlineWidth", width);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static void ApplySamplePalette(Material material, Color color)
        {
            material.SetColor("_BaseColor", color);
            float[] brightness = { .82f, .68f, .56f, .45f, .36f, .28f };
            for (int band = 2; band <= 7; band++)
            {
                float factor = brightness[band - 2];
                material.SetColor("_ToonColor" + band,
                    new Color(color.r * factor, color.g * factor, color.b * factor, color.a));
            }
            material.SetFloat("_UseOutline", 0);
        }

        static readonly string[] SampleRenderPasses =
        {
            "SPToonHairMask", "SPToonPlateSilhouette", "SPToonHairBoundary", "SPToonBrowInterior",
            "SPToonClearHairDepth", "SPToonFaceOccluderDepth", "SPToonOverHair", "SPToonRestoreHairDepth"
        };

        static bool EnsureSampleRenderFeatures()
        {
            var featureType = Type.GetType("UnityEngine.Rendering.Universal.RenderObjects, Unity.RenderPipelines.Universal.Runtime");
            var activePipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (featureType == null || !activePipeline ||
                new SerializedObject(activePipeline).FindProperty("m_RendererDataList") == null)
            {
                Debug.LogError("Create Face Sample requires an active Universal Render Pipeline asset.");
                return false;
            }

            var pipelines = new HashSet<UnityEngine.Rendering.RenderPipelineAsset> { activePipeline };
            for (int level = 0; level < QualitySettings.names.Length; level++)
            {
                var pipeline = QualitySettings.GetRenderPipelineAssetAt(level);
                if (pipeline) pipelines.Add(pipeline);
            }

            var rendererAssets = new HashSet<ScriptableObject>();
            foreach (var pipeline in pipelines)
            {
                var renderers = new SerializedObject(pipeline).FindProperty("m_RendererDataList");
                if (renderers == null) continue;
                for (int index = 0; index < renderers.arraySize; index++)
                {
                    if (renderers.GetArrayElementAtIndex(index).objectReferenceValue is ScriptableObject renderer &&
                        renderer.GetType().FullName == "UnityEngine.Rendering.Universal.UniversalRendererData")
                        rendererAssets.Add(renderer);
                }
            }
            if (rendererAssets.Count == 0)
            {
                Debug.LogError("Create Face Sample needs a Universal Renderer Data asset.");
                return false;
            }

            foreach (var renderer in rendererAssets)
            {
                if (ConfigureSampleRenderer(renderer, featureType)) continue;
                Debug.LogError("Could not configure SP Toon sample passes on " + AssetDatabase.GetAssetPath(renderer), renderer);
                return false;
            }
            return true;
        }

        static bool ConfigureSampleRenderer(ScriptableObject renderer, Type featureType)
        {
            var data = new SerializedObject(renderer);
            var features = data.FindProperty("m_RendererFeatures");
            var featureMap = data.FindProperty("m_RendererFeatureMap");
            if (features == null || featureMap == null || !AssetDatabase.Contains(renderer)) return false;

            bool changed = false;
            for (int index = 0; index < SampleRenderPasses.Length; index++)
            {
                string tag = SampleRenderPasses[index];
                ScriptableObject feature = null;
                for (int slot = 0; slot < features.arraySize; slot++)
                {
                    if (features.GetArrayElementAtIndex(slot).objectReferenceValue is ScriptableObject candidate &&
                        featureType.IsInstanceOfType(candidate) && candidate.name == tag)
                    {
                        feature = candidate;
                        break;
                    }
                }
                if (!feature)
                {
                    if (!AssetDatabase.IsOpenForEdit(renderer)) return false;
                    feature = CreateInstance(featureType);
                    feature.name = tag;
                    AssetDatabase.AddObjectToAsset(feature, renderer);
                    int slot = features.arraySize;
                    features.arraySize++;
                    features.GetArrayElementAtIndex(slot).objectReferenceValue = feature;
                    changed = true;
                }

                var config = new SerializedObject(feature);
                var settings = config.FindProperty("settings");
                var filters = settings.FindPropertyRelative("filterSettings");
                config.FindProperty("m_Active").boolValue = true;
                settings.FindPropertyRelative("passTag").stringValue = tag;
                // Unique events keep the depth and stencil passes ordered on every renderer.
                settings.FindPropertyRelative("Event").intValue = 300 + index;
                settings.FindPropertyRelative("overrideMode").intValue = 0;
                settings.FindPropertyRelative("overrideDepthState").boolValue = false;
                filters.FindPropertyRelative("RenderQueueType").intValue = 0;
                filters.FindPropertyRelative("LayerMask").intValue = -1;
                var passes = filters.FindPropertyRelative("PassNames");
                passes.arraySize = 1;
                passes.GetArrayElementAtIndex(0).stringValue = tag;
                if (config.ApplyModifiedPropertiesWithoutUndo())
                {
                    featureType.GetMethod("Create")?.Invoke(feature, null);
                    changed = true;
                }
            }
            if (!changed) return true;

            featureMap.arraySize = features.arraySize;
            for (int slot = 0; slot < features.arraySize; slot++)
            {
                var feature = features.GetArrayElementAtIndex(slot).objectReferenceValue;
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long id);
                featureMap.GetArrayElementAtIndex(slot).longValue = id;
            }
            data.ApplyModifiedPropertiesWithoutUndo();
            renderer.GetType().GetMethod("SetDirty", Type.EmptyTypes)?.Invoke(renderer, null);
            EditorUtility.SetDirty(renderer);
            AssetDatabase.SaveAssetIfDirty(renderer);
            return true;
        }

        static SPToonOutlinePainter CreateSamplePart(PrimitiveType primitive, Transform parent, string name, Vector3 position, Vector3 scale, Material material)
        {
            var sphere = GameObject.CreatePrimitive(primitive);
            sphere.name = name;
            sphere.transform.SetParent(parent, false);
            sphere.transform.localPosition = position;
            sphere.transform.localScale = scale;
            DestroyImmediate(sphere.GetComponent<Collider>());
            sphere.GetComponent<MeshRenderer>().sharedMaterial = material;
            var painter = sphere.AddComponent<SPToonOutlinePainter>();
            painter.sourceMesh = painter.CurrentSource;
            return painter;
        }

        static void CreateSampleHairPlane(Transform parent, string name, Vector3 position,
            Vector2 size, float tilt, Material material, float yaw = 0)
        {
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = name;
            plane.transform.SetParent(parent, false);
            plane.transform.localPosition = position;
            plane.transform.localScale = new Vector3(size.x / 10, 1, size.y / 10);
            plane.transform.localRotation = Quaternion.AngleAxis(yaw, Vector3.up) *
                Quaternion.AngleAxis(tilt, Vector3.forward) * Quaternion.Euler(-90, 0, 0);
            DestroyImmediate(plane.GetComponent<Collider>());
            plane.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        [MenuItem(FaceSampleMenu, true)]
        static bool CanCreateFaceSample()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode &&
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().isLoaded;
        }

        [MenuItem("Tools/DiGiMonkey/Outline Paint")]
        public static void Open() { GetWindow<SPToonOutlinePaintWindow>("Outline Paint"); }
        public static void OpenFor(SPToonOutlinePainter target)
        {
            var window = GetWindow<SPToonOutlinePaintWindow>("Outline Paint");
            window.Bind(target); window.Show();
        }
        public static void OpenForMaterial(Material material)
        {
            var window = GetWindow<SPToonOutlinePaintWindow>("Hull Outline 調整");
            bool fresh=window.targetMaterial!=material || window.paintTargets.Count==0;
            if(fresh) window.Bind(null);
            window.targetMaterial = material; window.Show();
            var go = Selection.activeGameObject;
            var renderer = go ? go.GetComponent<Renderer>() : null;
            if (fresh && renderer && Array.IndexOf(renderer.sharedMaterials, material) >= 0) window.StartOn(go);
        }
        void StartOn(GameObject go)
        {
            EnableTarget(go,true);
            var component=go.GetComponent<SPToonOutlinePainter>();
            if(component) FocusTarget(component);
            Selection.activeGameObject=go;
        }
        void OnEnable()
        {
            isWorker=creatingWorker;
            if(isWorker) return;
            if(painter && StateFor(painter)==null) paintTargets.Add(new TargetSelection{painter=painter,layer=selected});
            minSize = new Vector2(460, 620);
            SceneView.duringSceneGui += SceneGUI;
            Undo.undoRedoPerformed += UndoRedo;
            Selection.selectionChanged += Repaint;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }
        void OnDisable()
        {
            if(isWorker) {stroke=false;ReleaseProbe();return;}
            EndBatchStroke(); ClearWorkers(); ReleaseProbe();
            SceneView.duringSceneGui -= SceneGUI;
            Undo.undoRedoPerformed -= UndoRedo;
            Selection.selectionChanged -= Repaint;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
        }
        void PlayModeChanged(PlayModeStateChange state)
        {
            EndBatchStroke(); ClearWorkers(); ReleaseProbe(); painting = false; Repaint();
        }
        void UndoRedo()
        {
            foreach(var state in paintTargets) if(state.painter) state.painter.Rebuild();
            if (painter) selected = Mathf.Clamp(selected, 0, Mathf.Max(0, painter.layers.Count - 1));
            ClearWorkers(); ReleaseProbe(); Repaint(); SceneView.RepaintAll();
        }
        List<SPToonOutlinePainter> CheckedTargets()
        {
            var result=new List<SPToonOutlinePainter>();
            foreach(var state in paintTargets) if(state.enabled && state.painter && !result.Contains(state.painter)) result.Add(state.painter);
            return result;
        }
        void ChangeChecked(string label,Action<SPToonOutlinePainter> action,bool complete=false)
        {
            var targets=CheckedTargets();if(targets.Count==0) return;
            EndBatchStroke();ClearWorkers();ReleaseProbe();RememberLayer();
            var objects=new Object[targets.Count];for(int i=0;i<targets.Count;i++)objects[i]=targets[i];
            if(complete) Undo.RegisterCompleteObjectUndo(objects,label); else Undo.RecordObjects(objects,label);
            foreach(var component in targets)
            {
                action(component);EditorUtility.SetDirty(component);PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                if(component.IsValid) {if(!component.previewMesh)component.Rebuild();else component.Apply();}
            }
            Repaint();SceneView.RepaintAll();
        }
        static SPToonOutlinePainter.Layer EnsureLayer(SPToonOutlinePainter component,int index)
        {
            while(component.layers.Count<=index)component.layers.Add(new SPToonOutlinePainter.Layer(component.sourceMesh.vertexCount){name="Layer "+(component.layers.Count+1)});
            return component.layers[index];
        }
        int LayerIndex(SPToonOutlinePainter component)
        {
            var state=StateFor(component);return Mathf.Clamp(state!=null ? state.layer : selected,0,Mathf.Max(0,component.layers.Count-1));
        }
        void SelectCommonLayer(int index)
        {
            selected=index;
            foreach(var state in paintTargets)if(state.enabled && state.painter)state.layer=Mathf.Clamp(index,0,Mathf.Max(0,state.painter.layers.Count-1));
        }
        void ChangeLayerAt(string label,int index,Action<SPToonOutlinePainter.Layer> action)
        {
            ChangeChecked(label,component=>action(EnsureLayer(component,index)),true);
        }
        void ChangeSelectedLayers(string label,Action<SPToonOutlinePainter.Layer> action)
        {
            ChangeChecked(label,component=>action(EnsureLayer(component,LayerIndex(component))),true);
        }
        void AddCommonLayer()
        {
            ChangeChecked("Add Layers",component=>
            {
                component.layers.Add(new SPToonOutlinePainter.Layer(component.sourceMesh.vertexCount){name="Layer "+(component.layers.Count+1)});
                StateFor(component).layer=component.layers.Count-1;
            },true);
            if(painter)selected=LayerIndex(painter);
        }
        void DuplicateCommonLayer()
        {
            ChangeChecked("Duplicate Layers",component=>
            {
                var original=EnsureLayer(component,LayerIndex(component));
                var copy=JsonUtility.FromJson<SPToonOutlinePainter.Layer>(JsonUtility.ToJson(original));copy.name+=" Copy";
                component.layers.Add(copy);StateFor(component).layer=component.layers.Count-1;
            },true);
            if(painter)selected=LayerIndex(painter);
        }
        void DeleteCommonLayer()
        {
            ChangeChecked("Delete Layers",component=>
            {
                if(component.layers.Count==0)return;
                int index=LayerIndex(component);component.layers.RemoveAt(index);StateFor(component).layer=Mathf.Max(0,index-1);
            },true);
            if(painter)selected=LayerIndex(painter);
        }
        void MoveCommonLayer(int delta)
        {
            ChangeChecked("Move Layers",component=>
            {
                if(component.layers.Count==0)return;
                int index=LayerIndex(component),next=Mathf.Clamp(index+delta,0,component.layers.Count-1);
                var layer=component.layers[index];component.layers.RemoveAt(index);component.layers.Insert(next,layer);StateFor(component).layer=next;
            },true);
            if(painter)selected=LayerIndex(painter);
        }
        void ResetCheckedPainting()
        {
            ChangeChecked("Initialize Hull Outline Paint",component=>
            {
                foreach(var layer in component.layers)
                {
                    Array.Clear(layer.coverage,0,layer.coverage.Length);
                    Array.Fill(layer.offset,1/Mathf.Max(1,component.outward));Array.Fill(layer.visibility,1f);
                }
                component.showOutline=true;component.reverseFaces=false;
            },true);
        }
        bool Mixed<T>(Func<SPToonOutlinePainter,T> getter)
        {
            var targets=CheckedTargets();if(targets.Count<2)return false;
            T value=getter(targets[0]);
            foreach(var component in targets)if(!EqualityComparer<T>.Default.Equals(value,getter(component)))return true;
            return false;
        }
        void CommonToggle(string label,Func<SPToonOutlinePainter,bool> getter,Action<SPToonOutlinePainter,bool> setter)
        {
            EditorGUI.showMixedValue=Mixed(getter);EditorGUI.BeginChangeCheck();
            bool value=EditorGUILayout.Toggle(label,getter(painter));
            if(EditorGUI.EndChangeCheck())ChangeChecked(label,component=>setter(component,value));
            EditorGUI.showMixedValue=false;
        }
        void CommonFloat(string label,Func<SPToonOutlinePainter,float> getter,Action<SPToonOutlinePainter,float> setter)
        {
            EditorGUI.showMixedValue=Mixed(getter);EditorGUI.BeginChangeCheck();
            float value=EditorGUILayout.FloatField(label,getter(painter));
            if(EditorGUI.EndChangeCheck())ChangeChecked(label,component=>setter(component,value));
            EditorGUI.showMixedValue=false;
        }
        static void CopySharedSettings(SPToonOutlinePainter source,SPToonOutlinePainter destination)
        {
            destination.showOutline=source.showOutline;destination.reverseFaces=source.reverseFaces;
            destination.outward=source.outward;destination.inward=source.inward;
            for(int i=0;i<source.layers.Count;i++)
            {
                var a=source.layers[i];var b=EnsureLayer(destination,i);
                b.name=a.name;b.visible=a.visible;b.locked=a.locked;b.opacity=a.opacity;
                b.materialSlot=Mathf.Clamp(a.materialSlot,-1,destination.sourceMesh.subMeshCount-1);
            }
        }
        void Bind(SPToonOutlinePainter next)
        {
            EndBatchStroke(); ClearWorkers(); ReleaseProbe(); painter = next; selected = 0; submesh = -1; painting = false;
            paintTargets.Clear();
            if(next)
            {
                paintTargets.Add(new TargetSelection{painter=next});
                var materials=next.SourceRenderer ? next.SourceRenderer.sharedMaterials : null;
                if(materials!=null && materials.Length>0) targetMaterial=materials[0];
            }
        }
        void OnGUI()
        {
            if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox("ペイントは再生を停止してから行ってください。", MessageType.Info);
                return;
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox("Sceneビューで左ドラッグしてペイント。Altで視点操作。既存のHull Outlineを直接調整します。", MessageType.Info);
            if (targetMaterial)
            {
                EditorGUILayout.LabelField("対象マテリアル：" + targetMaterial.name, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("ペイントする部位（複数ON可）", EditorStyles.miniBoldLabel);
                var candidates=MaterialTargets();
                EditorGUILayout.BeginHorizontal();
                if(GUILayout.Button("全部ON")) foreach(var renderer in candidates) EnableTarget(renderer.gameObject,true);
                if(GUILayout.Button("全部OFF")) {EndBatchStroke();ClearWorkers();foreach(var state in paintTargets) state.enabled=false;}
                EditorGUILayout.EndHorizontal();
                foreach(var renderer in candidates)
                {
                    var component=renderer.GetComponent<SPToonOutlinePainter>();
                    var state=component ? StateFor(component) : null;
                    bool enabled=state!=null && state.enabled;
                    EditorGUILayout.BeginHorizontal();
                    bool nextEnabled=EditorGUILayout.ToggleLeft(renderer.name,enabled);
                    if(nextEnabled!=enabled) EnableTarget(renderer.gameObject,nextEnabled);
                    using(new EditorGUI.DisabledScope(!component || !enabled))
                        if(GUILayout.Button(component==painter ? "基準" : "表示基準",GUILayout.Width(56))) FocusTarget(component);
                    EditorGUILayout.EndHorizontal();
                }
                int enabledCount=paintTargets.FindAll(t=>t.enabled && t.painter).Count;
                EditorGUILayout.LabelField("ON："+enabledCount+"部位 ／ すべての設定・操作を共通適用",EditorStyles.miniLabel);
                EditorGUILayout.Space();
            }
            var next = (SPToonOutlinePainter)EditorGUILayout.ObjectField("設定の表示基準", painter, typeof(SPToonOutlinePainter), true);
            if (next != painter && next) {EnableTarget(next.gameObject,true);FocusTarget(next);}
            var go = Selection.activeGameObject;
            if (go && !go.GetComponent<MeshFilter>() && !go.GetComponent<SkinnedMeshRenderer>())
                EditorGUILayout.HelpBox("今はモデルの親が選択されています。Hierarchyで Mesh_01_Head や Mesh_02_Body など、メッシュを持つ子を選択してください。", MessageType.Info);
            using (new EditorGUI.DisabledScope(!go || (!go.GetComponent<MeshFilter>() && !go.GetComponent<SkinnedMeshRenderer>())))
            {
                if (GUILayout.Button("選択したオブジェクトで開始 / 開く"))
                {
                    StartOn(go);
                }
            }
            var checkedParts=CheckedTargets();
            if(checkedParts.Count>0 && !checkedParts.Contains(painter))FocusTarget(checkedParts[0]);
            if (!painter) { EditorGUILayout.EndScrollView(); return; }
            if (!painter.IsValid || painter.CurrentSource != painter.sourceMesh)
            {
                EditorGUILayout.HelpBox("メッシュが読めないか、元のメッシュと保存データの頂点数が一致しません。Import SettingsのRead/Writeを有効にし、元のメッシュを使用してください。", MessageType.Error);
                EditorGUILayout.EndScrollView(); return;
            }
            if (painter.SourceRenderer.isPartOfStaticBatch)
                EditorGUILayout.HelpBox("Static Batchingを外してからペイントしてください。", MessageType.Error);
            EditorGUILayout.HelpBox("マテリアルの Hull Outline / Width・色・Texture をそのまま使用。未ペイント部分は元の線を保持します。", MessageType.Info);
            EditorGUI.BeginDisabledGroup(checkedParts.Count==0);
            EditorGUILayout.LabelField("以下はチェックした全部位へ反映",EditorStyles.boldLabel);
            CommonToggle("Hull Outlineを表示",component=>component.showOutline,(component,value)=>component.showOutline=value);
            CommonToggle("確認用：描画面を反転",component=>component.reverseFaces,(component,value)=>component.reverseFaces=value);
            CommonFloat("外側の最大倍率（Width基準）",component=>component.outward,(component,value)=>component.outward=Mathf.Max(1,value));
            CommonFloat("内側の最大倍率（Width基準）",component=>component.inward,(component,value)=>component.inward=Mathf.Max(0,value));
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("レイヤー（下の行ほど上に合成）", EditorStyles.boldLabel);
            for (int i = 0; i < painter.layers.Count; i++)
            {
                var layer = painter.layers[i];
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Toggle(selected == i, "", GUILayout.Width(20))) SelectCommonLayer(i);
                EditorGUI.BeginChangeCheck();
                bool visible = GUILayout.Toggle(layer.visible, "表示", GUILayout.Width(45));
                bool locked = GUILayout.Toggle(layer.locked, "固定", GUILayout.Width(45));
                string name = EditorGUILayout.TextField(layer.name);
                if (EditorGUI.EndChangeCheck()) ChangeLayerAt("Edit Layers", i, value => { value.visible = visible; value.locked = locked; value.name = name; });
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("追加")) AddCommonLayer();
            using (new EditorGUI.DisabledScope(painter.layers.Count == 0))
            {
                if (GUILayout.Button("複製")) DuplicateCommonLayer();
                if (GUILayout.Button("削除")) DeleteCommonLayer();
            }
            EditorGUILayout.EndHorizontal();
            if (painter.layers.Count == 0) { EditorGUI.EndDisabledGroup();EditorGUILayout.EndScrollView(); return; }
            selected = Mathf.Clamp(selected, 0, painter.layers.Count - 1);
            RememberLayer();
            var active = painter.layers[selected];
            EditorGUI.BeginChangeCheck();
            float opacity = EditorGUILayout.Slider("選択レイヤーの強さ", active.opacity, 0, 1);
            if (EditorGUI.EndChangeCheck()) ChangeSelectedLayers("Layer Opacity", value => value.opacity = opacity);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(selected == 0))
                if (GUILayout.Button("合成順を下へ")) MoveCommonLayer(-1);
            using (new EditorGUI.DisabledScope(selected >= painter.layers.Count - 1))
                if (GUILayout.Button("合成順を上へ")) MoveCommonLayer(1);
            EditorGUILayout.EndHorizontal();
            string[] choices = new string[painter.sourceMesh.subMeshCount + 1]; choices[0] = "すべて";
            var mats = painter.SourceRenderer.sharedMaterials;
            for (int i = 1; i < choices.Length; i++) choices[i] = "Material " + i + (i <= mats.Length && mats[i - 1] ? " : " + mats[i - 1].name : "");
            if (submesh != active.materialSlot) { submesh = active.materialSlot; ReleaseProbe(); }
            EditorGUI.BeginChangeCheck();
            int slot = EditorGUILayout.Popup("作業対象マテリアル", submesh + 1, choices) - 1;
            if (EditorGUI.EndChangeCheck())
            {
                ChangeChecked("Layer Material Scope", component => EnsureLayer(component,LayerIndex(component)).materialSlot = Mathf.Clamp(slot,-1,component.sourceMesh.subMeshCount-1));
                submesh = slot; ReleaseProbe();
            }
            radius = Mathf.Max(.0001f, EditorGUILayout.FloatField("ブラシ半径（ワールド単位）", radius));
            strength = EditorGUILayout.Slider("強さ", strength, 0, 1);
            hardness = EditorGUILayout.Slider("硬さ", hardness, 0, 1);
            bool nextHull = EditorGUILayout.Popup("塗る対象", paintHull ? 0 : 1,
                new[] { "Hull Outline（見えている線）", "元の面（消した線の復元用）" }) == 0;
            if (nextHull != paintHull) { EndBatchStroke(); ClearWorkers(); paintHull = nextHull; ReleaseProbe(); }
            if (paintHull) EditorGUILayout.HelpBox("黒いHull Outlineの上へ直接ブラシを当ててください。消した場所を戻すときは「元の面」に切り替えます。", MessageType.None);
            using (new EditorGUI.DisabledScope(paintHull))
                through = EditorGUILayout.Toggle("裏側にも貫通して塗る", through);
            mode = GUILayout.Toolbar(mode, modes);
            if (mode == 0)
            {
                float multiplier = target * (target < 0 ? painter.inward : painter.outward);
                multiplier = EditorGUILayout.Slider("今のWidthに対する倍率", multiplier, -painter.inward, painter.outward);
                target = multiplier / Mathf.Max(.0001f, multiplier < 0 ? painter.inward : painter.outward);
                EditorGUILayout.LabelField("1＝今の線、0＝膨らみゼロ、負＝内側へ", EditorStyles.miniLabel);
            }
            if (mode == 2) regionMode = GUILayout.Toolbar(regionMode, new[] { "範囲に加える", "範囲から除く" });
            EditorGUILayout.HelpBox("顔と体が同一メッシュの場合：範囲を全部外す →「範囲に加える」で顔を塗る →「太さ」で作業。範囲はレイヤーごとに保存されます。", MessageType.None);
            using (new EditorGUI.DisabledScope(active.locked))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("範囲を全部含める")) ChangeSelectedLayers("Fill Regions", value => Array.Fill(value.region, 1f));
                if (GUILayout.Button("範囲を全部外す")) ChangeSelectedLayers("Clear Regions", value => Array.Clear(value.region, 0, value.region.Length));
                EditorGUILayout.EndHorizontal();
            }
            painting = GUILayout.Toggle(painting, "Sceneペイントを有効にする", "Button",
                GUILayout.Height(EditorGUIUtility.singleLineHeight * 2 + 4));
            if (GUILayout.Button("チェック部位のペイントを初期化",GUILayout.Height(EditorGUIUtility.singleLineHeight*2+4))) ResetCheckedPainting();
            EditorGUILayout.LabelField("初期化：補正・消去を解除して元の線へ。レイヤーとブラシ設定は残します。",EditorStyles.wordWrappedMiniLabel);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.HelpBox("保存は通常のScene / Prefab保存で行います。レイヤーも保存され、元メッシュは編集されません。頂点数を変える再インポートには対応しません。", MessageType.Info);
            EditorGUILayout.EndScrollView();
        }
        void ReleaseProbe()
        {
            if (probe) DestroyImmediate(probe);
            if (posed) DestroyImmediate(posed);
            if (hullMesh) DestroyImmediate(hullMesh);
            probe = null; posed = null; collider = null; sourceCollider = null; hullMesh = null;
            hullSources = null; hullTriangles = null; hullVisibility = null;
        }
        void Prepare()
        {
            ReleaseProbe();
            posed = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            var skin = painter.GetComponent<SkinnedMeshRenderer>();
            if (skin) skin.BakeMesh(posed);
            else { DestroyImmediate(posed); posed = Instantiate(painter.sourceMesh); posed.hideFlags = HideFlags.HideAndDontSave; }
            vertices = posed.vertices; normals = posed.normals;
            allowed = new bool[vertices.Length];
            if (submesh < 0) Array.Fill(allowed, true);
            else foreach (int index in posed.GetTriangles(submesh)) allowed[index] = true;
            probe = new GameObject("Outline paint ray probe") { hideFlags = HideFlags.HideAndDontSave };
            probe.layer = 2;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(probe, painter.gameObject.scene);
            probe.transform.SetParent(painter.transform, false);
            sourceCollider = probe.AddComponent<MeshCollider>(); sourceCollider.sharedMesh = posed;
            if (paintHull)
            {
                var hullObject = new GameObject("Hull Outline hit surface") { hideFlags = HideFlags.HideAndDontSave, layer = 2 };
                hullObject.transform.SetParent(probe.transform, false);
                collider = hullObject.AddComponent<MeshCollider>();
                RefreshHullGeometry();
            }
            else collider = sourceCollider;
        }
        static float Frac(float value) => value - Mathf.Floor(value);
        static float NoiseHash(Vector3 p)
        {
            p = new Vector3(Frac(p.x * 127.1f), Frac(p.y * 311.7f), Frac(p.z * 74.7f));
            p += Vector3.one * Vector3.Dot(p, p + Vector3.one * 19.19f);
            return Frac(p.x * p.y * p.z);
        }
        static float ValueNoise(Vector3 p)
        {
            Vector3 cell = new Vector3(Mathf.Floor(p.x), Mathf.Floor(p.y), Mathf.Floor(p.z));
            Vector3 f = p - cell;
            Vector3 u = new Vector3(f.x*f.x*(3-2*f.x), f.y*f.y*(3-2*f.y), f.z*f.z*(3-2*f.z));
            return Mathf.Lerp(Mathf.Lerp(Mathf.Lerp(NoiseHash(cell), NoiseHash(cell + Vector3.right), u.x),
                Mathf.Lerp(NoiseHash(cell + Vector3.up), NoiseHash(cell + Vector3.right + Vector3.up), u.x), u.y),
                Mathf.Lerp(Mathf.Lerp(NoiseHash(cell + Vector3.forward), NoiseHash(cell + Vector3.right + Vector3.forward), u.x),
                Mathf.Lerp(NoiseHash(cell + Vector3.up + Vector3.forward), NoiseHash(cell + Vector3.one), u.x), u.y), u.z);
        }
        float HullProperty(Material material, string name, int slot)
        {
            float value = material.GetFloat(name);
            var block = new MaterialPropertyBlock();
            painter.SourceRenderer.GetPropertyBlock(block);
            if (block.HasFloat(name)) value = block.GetFloat(name);
            painter.SourceRenderer.GetPropertyBlock(block, slot);
            if (block.HasFloat(name)) value = block.GetFloat(name);
            return value;
        }
        void RefreshHullGeometry()
        {
            if (!paintHull || !collider || !posed) return;
            collider.sharedMesh = null;
            if (hullMesh) DestroyImmediate(hullMesh);
            var positions = new List<Vector3>();
            var sources = new List<int>(); var visibility = new List<float>(); var triangles = new List<int>();
            var materials = painter.SourceRenderer.sharedMaterials;
            var world = painter.transform.localToWorldMatrix;
            var inverse = painter.transform.worldToLocalMatrix;
            var normalMatrix = inverse.transpose;
            for (int slot = 0; slot < posed.subMeshCount; slot++)
            {
                if (slot >= materials.Length) continue;
                var material = materials[slot];
                if (!material || !material.HasProperty("_HullOutlineWidth") || HullProperty(material,"_UseHullOutline",slot)<.5f) continue;
                float width = HullProperty(material,"_HullOutlineWidth",slot);
                float noiseStrength = HullProperty(material,"_HullNoiseStrength",slot);
                float noiseScale = HullProperty(material,"_HullNoiseScale",slot);
                bool hasPlate = painter.HasPlateOutline;
                int outlineCount = painter.HasCurvedPlateOutline ? 2 : 1;
                for (int outline = 0; outline < outlineCount; outline++)
                {
                    bool usePlate = hasPlate && outline == 0;
                    var mapping = new Dictionary<int,int>();
                    var indices = posed.GetTriangles(slot);
                    foreach (int index in indices)
                    {
                        if (mapping.ContainsKey(index)) continue;
                        var color = SPToonOutlinePainter.ComposeVertex(painter.layers,index,painter.outward,painter.inward);
                        Vector3 position = world.MultiplyPoint3x4(vertices[index]);
                        Vector3 normal = normalMatrix.MultiplyVector(normals[index]).normalized;
                        float noise = ValueNoise(position * noiseScale);
                        if (usePlate) normal = painter.PlateDirectionWorld(index);
                        position += normal * (width * Mathf.LerpUnclamped(1,noise,noiseStrength) * color.r);
                        mapping[index]=positions.Count;
                        positions.Add(inverse.MultiplyPoint3x4(position)); sources.Add(index);
                        visibility.Add(painter.showOutline ? color.g : 0);
                    }
                    for(int i=0;i<indices.Length;i+=3)
                    {
                        int a=mapping[indices[i]], b=mapping[indices[i+1]], c=mapping[indices[i+2]];
                        if (visibility[a] <= .001f && visibility[b] <= .001f && visibility[c] <= .001f) continue;
                        bool drawFront = usePlate != painter.reverseFaces;
                        triangles.Add(a); triangles.Add(drawFront ? b : c); triangles.Add(drawFront ? c : b);
                    }
                }
            }
            hullSources=sources.ToArray(); hullVisibility=visibility.ToArray(); hullTriangles=triangles.ToArray();
            hullMesh=new Mesh {name="Hull Outline picking surface",hideFlags=HideFlags.HideAndDontSave,
                indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            hullMesh.SetVertices(positions); hullMesh.SetTriangles(hullTriangles,0); hullMesh.RecalculateBounds();
            if (hullTriangles.Length>0) collider.sharedMesh=hullMesh;
        }
        static bool FrontRaycast(MeshCollider meshCollider, Ray ray, out RaycastHit hit, float length)
        {
            hit=default;
            bool backfaces=Physics.queriesHitBackfaces;
            try { Physics.queriesHitBackfaces=false; return meshCollider && meshCollider.Raycast(ray,out hit,length); }
            finally { Physics.queriesHitBackfaces=backfaces; }
        }
        bool RawPickSurface(Ray ray, out RaycastHit hit)
        {
            hit=default;
            if (!collider || !painter.SourceRenderer.enabled || !FrontRaycast(collider,ray,out hit,float.MaxValue)) return false;
            if (!paintHull) return true;
            int triangle=hit.triangleIndex*3;
            if(triangle<0 || triangle+2>=hullTriangles.Length) return false;
            Vector3 bary=hit.barycentricCoordinate;
            float visible=hullVisibility[hullTriangles[triangle]]*bary.x+hullVisibility[hullTriangles[triangle+1]]*bary.y+hullVisibility[hullTriangles[triangle+2]]*bary.z;
            if(visible<=.001f) return false;
            if(FrontRaycast(sourceCollider,ray,out RaycastHit body,hit.distance)
                && body.distance < hit.distance-Mathf.Max(.00001f,radius*.001f)) return false;
            return true;
        }
        bool PickSurface(Ray ray,out RaycastHit hit)
        {
            if(!RawPickSurface(ray,out hit)) return false;
            if(coordinator && (paintHull || !through))
                foreach(var pair in coordinator.workers)
                {
                    var state=coordinator.StateFor(pair.Key);var other=pair.Value;
                    if(state==null || !state.enabled || !other || other==this) continue;
                    float tolerance=Mathf.Max(.00001f,radius*.001f);
                    if(FrontRaycast(other.sourceCollider,ray,out var body,hit.distance) && body.distance<hit.distance-tolerance) return false;
                    if(other.RawPickSurface(ray,out var outline) && outline.distance<hit.distance-tolerance) return false;
                }
            return true;
        }
        static float SoftWeight(float distance, float hardness)
        {
            if(distance>=1) return 0;
            float t=hardness>=1 ? 0 : Mathf.Clamp01((distance-hardness)/(1-hardness));
            return 1-t*t*(3-2*t);
        }
        void PaintHullDab(Vector2 mouse, SceneView view, SPToonOutlinePainter.Layer layer, float flow)
        {
            if(!PickSurface(HandleUtility.GUIPointToWorldRay(mouse),out RaycastHit center)) return;
            float pixels=Mathf.Max(1,Vector2.Distance(HandleUtility.WorldToGUIPoint(center.point),
                HandleUtility.WorldToGUIPoint(center.point+view.camera.transform.right*radius)));
            if(brushWeights==null || brushWeights.Length!=vertices.Length) brushWeights=new float[vertices.Length];
            Array.Clear(brushWeights,0,brushWeights.Length);
            // Sample the visible outline under the screen-space soft brush, then map its triangles to source vertices.
            const int steps=4;
            for(int y=-steps;y<=steps;y++) for(int x=-steps;x<=steps;x++)
            {
                Vector2 offset=new Vector2(x,y)/steps;
                float influence=SoftWeight(offset.magnitude,hardness)*strength*flow;
                if(influence>0) AccumulateHullSample(HandleUtility.GUIPointToWorldRay(mouse+offset*pixels),influence,brushWeights);
            }
            for(int i=0;i<brushWeights.Length;i++) if(brushWeights[i]>0) ApplyWeight(layer,i,brushWeights[i]);
        }
        bool AccumulateHullSample(Ray ray,float influence,float[] weights)
        {
            if(!PickSurface(ray,out RaycastHit hit)) return false;
            int start=hit.triangleIndex*3; Vector3 bary=hit.barycentricCoordinate;
            float maximum=Mathf.Max(.0001f,Mathf.Max(bary.x,Mathf.Max(bary.y,bary.z)));
            for(int corner=0;corner<3;corner++)
            {
                int index=hullSources[hullTriangles[start+corner]];
                if(!allowed[index]) continue;
                weights[index]=Mathf.Max(weights[index],influence*bary[corner]/maximum);
            }
            return true;
        }
        void ApplyWeight(SPToonOutlinePainter.Layer layer,int i,float weight)
        {
            if(mode==2) {layer.region[i]=Mathf.Lerp(layer.region[i],regionMode==0 ? 1 : 0,weight);return;}
            if(layer.region[i]<=0) return;
            if(mode==3) {layer.coverage[i]=Mathf.Lerp(layer.coverage[i],0,weight);return;}
            float previous=layer.coverage[i];
            float coverage=previous+(1-previous)*weight;
            float desiredOffset=mode==1 ? 1/painter.outward : target;
            float desiredVisibility=mode==1 ? 0 : 1;
            layer.offset[i]=coverage>0 ? Mathf.Lerp(layer.offset[i]*previous,desiredOffset,weight)/coverage : 0;
            layer.visibility[i]=coverage>0 ? Mathf.Lerp(layer.visibility[i]*previous,desiredVisibility,weight)/coverage : 1;
            layer.coverage[i]=coverage;
        }
        void EndStroke()
        {
            if (!stroke) return;
            stroke = false; Undo.CollapseUndoOperations(undoGroup);
            if (painter) EditorUtility.SetDirty(painter);
        }
        void SceneGUI(SceneView view)
        {
            var original=Event.current;
            if(original.type==EventType.KeyDown && original.keyCode==KeyCode.Escape)
            {painting=false;EndBatchStroke();ClearWorkers();ReleaseProbe();Repaint();original.Use();return;}
            if(original.type==EventType.MouseUp || original.type==EventType.Ignore) EndBatchStroke();
            if(!painting || Application.isPlaying || original.alt) return;
            var active=PrepareWorkers(original.type==EventType.MouseDown && original.button==0);
            if(active.Count==0) return;
            if((original.type==EventType.MouseDown || original.type==EventType.MouseDrag) && original.button==0)
            {
                Ray ray=HandleUtility.GUIPointToWorldRay(original.mousePosition);
                bool editableHit=false;
                foreach(var worker in active)
                {
                    var layer=worker.painter.layers[worker.selected];
                    if(!layer.locked && layer.visible && worker.PickSurface(ray,out _)) editableHit=true;
                }
                if(editableHit && !stroke) BeginBatchStroke();
                if(original.type==EventType.MouseDown)
                    foreach(var worker in active) {worker.lastMouse=original.mousePosition;worker.lastDab=EditorApplication.timeSinceStartup;}
            }
            bool used=false;
            try
            {
                foreach(var worker in active)
                {
                    worker.stroke=stroke;worker.undoGroup=undoGroup;
                    Event.current=new Event(original);
                    worker.SceneGUIForTarget(view);
                    used|=Event.current.type==EventType.Used;
                }
            }
            finally {Event.current=original;}
            if(used) original.Use();
        }
        void SceneGUIForTarget(SceneView view)
        {
            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                painting = false; EndStroke(); ReleaseProbe(); Repaint(); e.Use(); return;
            }
            if (e.type == EventType.MouseUp || e.type == EventType.Ignore) EndStroke();
            if (!painting || !painter || !painter.IsValid || painter.layers.Count == 0 || Application.isPlaying) return;
            selected = Mathf.Clamp(selected, 0, painter.layers.Count - 1);
            if (e.alt) return;
            if (!collider || (!stroke && e.type == EventType.MouseDown)) Prepare();
            if (!collider) return;
            if (paintHull && !stroke && e.type == EventType.MouseMove) RefreshHullGeometry();
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (!PickSurface(ray, out RaycastHit hit)) return;
            Handles.color = mode == 2 ? Color.yellow : mode == 1 ? Color.red : Color.cyan;
            Handles.DrawWireDisc(hit.point, paintHull ? view.camera.transform.forward : hit.normal, radius);
            Handles.DrawWireDisc(hit.point, paintHull ? view.camera.transform.forward : hit.normal, radius * hardness);
            if (mode == 2 && e.type == EventType.Repaint)
            {
                var layer = painter.layers[selected];
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (!allowed[i] || layer.region[i] < .01f) continue;
                    Vector3 p = probe.transform.TransformPoint(vertices[i]);
                    Handles.color = new Color(1, .8f, 0, layer.region[i]);
                    Handles.DotHandleCap(0, p, Quaternion.identity, HandleUtility.GetHandleSize(p) * .012f, EventType.Repaint);
                }
            }
            view.Repaint();
            if (e.button != 0 || (e.type != EventType.MouseDown && e.type != EventType.MouseDrag)) return;
            var active = painter.layers[selected];
            if (active.locked || !active.visible) return;
            if (!stroke)
            {
                Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(painter, "Paint Outline"); stroke = true; lastDab = EditorApplication.timeSinceStartup; lastMouse = e.mousePosition;
            }
            // Sample between pointer events to avoid holes during fast drags.
            double now = EditorApplication.timeSinceStartup;
            float flow = Mathf.Clamp((float)(now - lastDab) * 30, .1f, 1); lastDab = now;
            float pixels = Mathf.Max(1, HandleUtility.WorldToGUIPoint(hit.point + view.camera.transform.right * radius).x - HandleUtility.WorldToGUIPoint(hit.point).x);
            int steps = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(lastMouse, e.mousePosition) / Mathf.Max(1, pixels * .15f)), 1, 256);
            for (int step = 1; step <= steps; step++)
            {
                Vector2 sampleMouse=Vector2.Lerp(lastMouse,e.mousePosition,(float)step/steps);
                if(paintHull) PaintHullDab(sampleMouse,view,active,flow);
                else if(PickSurface(HandleUtility.GUIPointToWorldRay(sampleMouse),out RaycastHit sampleHit))
                    PaintDab(sampleHit.point,view.camera,active,flow);
            }
            lastMouse = e.mousePosition;
            PrefabUtility.RecordPrefabInstancePropertyModifications(painter);
            painter.Apply(); if(paintHull) RefreshHullGeometry(); EditorUtility.SetDirty(painter); e.Use();
        }
        void PaintDab(Vector3 point, Camera camera, SPToonOutlinePainter.Layer layer, float flow)
        {
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!allowed[i]) continue;
                Vector3 p = probe.transform.TransformPoint(vertices[i]);
                float distance = Vector3.Distance(point, p) / radius;
                if (distance >= 1) continue;
                if (!through)
                {
                    Vector3 normal = probe.transform.worldToLocalMatrix.transpose.MultiplyVector(normals[i]).normalized;
                    Vector3 direction = camera.orthographic ? camera.transform.forward : (p - camera.transform.position).normalized;
                    if (Vector3.Dot(normal, -direction) <= 0) continue;
                    Vector3 start = camera.orthographic ? p - direction * (camera.farClipPlane * .5f) : camera.transform.position;
                    float length = Vector3.Distance(start, p);
                    if (collider.Raycast(new Ray(start, direction), out RaycastHit obstruction, length + .001f)
                        && obstruction.distance < length - Mathf.Max(.00001f, radius * .001f)) continue;
                }
                ApplyWeight(layer,i,SoftWeight(distance,hardness)*strength*flow);
            }
        }
    }
}

namespace DiGiMonkey
{
    public sealed class SPToonURPShaderGUI : ShaderGUI
    {
        [System.Serializable]
        private sealed class SettingsClipboard
        {
            public string marker = "SPToonURP.Settings.v1";
            public string shaderName;
            public int renderQueue;
            public string[] keywords;
            public SettingValue[] values;
        }

        [System.Serializable]
        private sealed class SettingValue
        {
            public string name;
            public int type;
            public float number;
            public int integer;
            public Color color;
            public Vector4 vector;
        }

        private static SPToonMaterialPropertyType GetPropertyType(MaterialProperty property)
        {
#if UNITY_6000_3_OR_NEWER
            return property.propertyType;
#else
            return property.type;
#endif
        }

        private static bool DrawSettingsClipboard(MaterialEditor editor, MaterialProperty[] properties)
        {
            SettingsClipboard clipboard = null;
            string clipboardText = EditorGUIUtility.systemCopyBuffer;
            if (!string.IsNullOrEmpty(clipboardText) && clipboardText.Contains("SPToonURP.Settings.v1"))
            {
                try { clipboard = JsonUtility.FromJson<SettingsClipboard>(clipboardText); }
                catch (System.ArgumentException) { }
            }
            Material source = editor.target as Material;
            bool canPaste = clipboard != null && clipboard.marker == "SPToonURP.Settings.v1"
                && clipboard.values != null && source != null
                && clipboard.shaderName == source.shader.name;
            bool pasted = false;
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(editor.targets.Length != 1 || source == null))
            {
                if (GUILayout.Button("Copy Values & Colors"))
                {
                    var values = new System.Collections.Generic.List<SettingValue>();
                    foreach (MaterialProperty property in properties)
                    {
                        var value = new SettingValue { name = property.name, type = (int)GetPropertyType(property) };
                        switch (GetPropertyType(property))
                        {
                            case SPToonMaterialPropertyType.Color: value.color = property.colorValue; break;
                            case SPToonMaterialPropertyType.Vector: value.vector = property.vectorValue; break;
                            case SPToonMaterialPropertyType.Float:
                            case SPToonMaterialPropertyType.Range: value.number = property.floatValue; break;
                            case SPToonMaterialPropertyType.Int: value.integer = property.intValue; break;
                            case SPToonMaterialPropertyType.Texture: value.vector = property.textureScaleAndOffset; break;
                            default: continue;
                        }
                        values.Add(value);
                    }
                    var copied = new SettingsClipboard
                    {
                        shaderName = source.shader.name,
                        renderQueue = source.renderQueue,
                        keywords = source.shaderKeywords,
                        values = values.ToArray()
                    };
                    EditorGUIUtility.systemCopyBuffer = JsonUtility.ToJson(copied);
                }
            }
            using (new EditorGUI.DisabledScope(!canPaste))
            {
                if (GUILayout.Button("Paste Values & Colors"))
                {
                    Undo.RecordObjects(editor.targets, "Paste SP Toon Settings");
                    foreach (SettingValue value in clipboard.values)
                    {
                        MaterialProperty property = Find(value.name, properties);
                        if (property == null || (int)GetPropertyType(property) != value.type) continue;
                        switch (GetPropertyType(property))
                        {
                            case SPToonMaterialPropertyType.Color: property.colorValue = value.color; break;
                            case SPToonMaterialPropertyType.Vector: property.vectorValue = value.vector; break;
                            case SPToonMaterialPropertyType.Float:
                            case SPToonMaterialPropertyType.Range: property.floatValue = value.number; break;
                            case SPToonMaterialPropertyType.Int: property.intValue = value.integer; break;
                            case SPToonMaterialPropertyType.Texture: property.textureScaleAndOffset = value.vector; break;
                        }
                    }
                    foreach (Object target in editor.targets)
                    {
                        if (target is not Material material) continue;
                        material.shaderKeywords = clipboard.keywords ?? new string[0];
                        material.renderQueue = clipboard.renderQueue;
                        EditorUtility.SetDirty(material);
                    }
                    editor.PropertiesChanged();
                    pasted = true;
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Copies values, colors, modes, and tiling/offset. Keeps destination textures.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);
            return pasted;
        }
        private static MaterialProperty Find(
            string propertyName,
            MaterialProperty[] properties)
        {
            return FindProperty(propertyName, properties, false);
        }

        private static void DrawProperty(
            MaterialEditor editor,
            MaterialProperty property,
            string label = null)
        {
            if (property == null) return;
            editor.ShaderProperty(
                property,
                string.IsNullOrEmpty(label)
                    ? property.displayName
                    : label);
        }

        private static void DrawTextureColor(
            MaterialEditor editor,
            MaterialProperty texture,
            MaterialProperty color,
            string label)
        {
            if (texture == null || color == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            editor.ColorProperty(color, "Color");
            editor.TexturePropertySingleLine(new GUIContent("Texture"), texture);
            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }

        private static void DrawTextureOnly(
            MaterialEditor editor,
            MaterialProperty texture,
            string label)
        {
            if (texture == null) return;
            editor.TexturePropertySingleLine(new GUIContent(label), texture);
        }

        public override void OnGUI(
            MaterialEditor materialEditor,
            MaterialProperty[] properties)
        {
            if (DrawSettingsClipboard(materialEditor, properties)) return;

            // ── Lighting ──
            MaterialProperty useHDR          = Find("_UseHDR",          properties);
            MaterialProperty hdrStrength     = Find("_HDRStrength",     properties);
            MaterialProperty lightPosition   = Find("_LightPosition",   properties);

            // ── Hull Outline ──
            MaterialProperty useHullOutline      = Find("_UseHullOutline",   properties);
            MaterialProperty hullOutlineWidth    = Find("_HullOutlineWidth", properties);
            MaterialProperty hullOutlineColor    = Find("_HullOutlineColor", properties);
            MaterialProperty hullOutlineMap      = Find("_HullOutlineMap",   properties);
            MaterialProperty hullNoiseStrength = Find("_HullNoiseStrength", properties);
            MaterialProperty hullNoiseScale    = Find("_HullNoiseScale",    properties);

            // ── Outline ──
            MaterialProperty useOutline      = Find("_UseOutline",      properties);
            MaterialProperty reverseOutline  = Find("_ReverseOutline",  properties);
            MaterialProperty outlineThickness= Find("_OutlineThickness",properties);
            MaterialProperty outlineBlur     = Find("_OutlineBlur",     properties);
            MaterialProperty outlineColor    = Find("_OutlineColor",    properties);
            MaterialProperty outlineMap      = Find("_OutlineMap",      properties);

            // ── Inline ──
            MaterialProperty inlineThickness = Find("_InlineThickness", properties);
            MaterialProperty inlineBlur      = Find("_InlineBlur",      properties);
            MaterialProperty inlineOnTop     = Find("_InlineOnTop",     properties);
            MaterialProperty inlineColor     = Find("_InlineColor",     properties);
            MaterialProperty inlineMap       = Find("_InlineMap",       properties);
            MaterialProperty curvatureMap    = Find("_CurvatureMap",    properties);
            MaterialProperty invertCurvature = Find("_InvertCurvature", properties);

            // ── Toon Gradation ──
            MaterialProperty toonParam2 = Find("_ToonParam2", properties);
            MaterialProperty toonParam3 = Find("_ToonParam3", properties);
            MaterialProperty toonParam4 = Find("_ToonParam4", properties);
            MaterialProperty toonParam5 = Find("_ToonParam5", properties);
            MaterialProperty toonParam6 = Find("_ToonParam6", properties);
            MaterialProperty toonParam7 = Find("_ToonParam7", properties);
            MaterialProperty toonBlur   = Find("_ToonBlur",   properties);

            // ── Normal Map ──
            MaterialProperty normalMap      = Find("_NormalMap",      properties);
            MaterialProperty normalStrength = Find("_NormalStrength",  properties);

            // ── AO ──
            MaterialProperty aoMap      = Find("_AOMap",      properties);
            MaterialProperty aoStrength = Find("_AOStrength", properties);
            MaterialProperty aoColor    = Find("_AOColor",    properties);

            // ── Rim ──
            MaterialProperty useRim      = Find("_UseRim",       properties);
            MaterialProperty rimStrength = Find("_RimStrength",  properties);
            MaterialProperty rimSharpness= Find("_RimSharpness", properties);
            MaterialProperty rimColor    = Find("_RimColor",     properties);

            // ── Thickness ──
            MaterialProperty useThickness      = Find("_UseThickness",      properties);
            MaterialProperty thicknessMap      = Find("_ThicknessMap",      properties);
            MaterialProperty thicknessStrength = Find("_ThicknessStrength", properties);
            MaterialProperty thicknessColor    = Find("_ThicknessColor",    properties);

            // ── MatCap ──
            MaterialProperty useMatCap      = Find("_UseMatCap",      properties);
            MaterialProperty matCapMap      = Find("_MatCapMap",       properties);
            MaterialProperty matCapStrength = Find("_MatCapStrength",  properties);
            MaterialProperty matCapBlend    = Find("_MatCapBlend",     properties);

            // ── Base Colors / Textures ──
            MaterialProperty baseColor  = Find("_BaseColor", properties);
            MaterialProperty baseMap    = Find("_BaseMap",   properties);

            MaterialProperty toonColor2 = Find("_ToonColor2", properties);
            MaterialProperty toonColor3 = Find("_ToonColor3", properties);
            MaterialProperty toonColor4 = Find("_ToonColor4", properties);
            MaterialProperty toonColor5 = Find("_ToonColor5", properties);
            MaterialProperty toonColor6 = Find("_ToonColor6", properties);
            MaterialProperty toonColor7 = Find("_ToonColor7", properties);

            MaterialProperty toonMap2 = Find("_ToonMap2", properties);
            MaterialProperty toonMap3 = Find("_ToonMap3", properties);
            MaterialProperty toonMap4 = Find("_ToonMap4", properties);
            MaterialProperty toonMap5 = Find("_ToonMap5", properties);
            MaterialProperty toonMap6 = Find("_ToonMap6", properties);
            MaterialProperty toonMap7 = Find("_ToonMap7", properties);

            // ── Surface ──
            MaterialProperty surfaceMode  = Find("_SurfaceMode", properties);
            MaterialProperty opacityMap    = Find("_OpacityMap",    properties);
            MaterialProperty invertOpacity = Find("_InvertOpacity", properties);
            MaterialProperty alphaClip     = Find("_AlphaClip",     properties);
            MaterialProperty opacityLevel = Find("_OpacityLevel", properties);
            MaterialProperty opacity = Find("_Opacity", properties);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("SP Toon URP v2.1.0", EditorStyles.boldLabel);

            // ────────────────────────────
            //  Lighting
            // ────────────────────────────
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Lighting", EditorStyles.boldLabel);

            DrawProperty(materialEditor, useHDR, "HDR Enabled");
            bool hdrEnabled = useHDR != null && useHDR.floatValue > 0.5f;

            if (hdrEnabled)
            {
                DrawProperty(materialEditor, hdrStrength, "HDR Ambient Strength");
                EditorGUILayout.HelpBox(
                    "HDR Enabled uses each Unity light's direct N dot L, "
                    + "so the light/shadow boundary matches a normally lit "
                    + "sphere more closely. Point and Spot attenuation and "
                    + "shadows are included. HDR Ambient Strength adds only "
                    + "an optional Skybox/HDRI offset. HDR Disabled uses "
                    + "only the manual Light Position.",
                    MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(hdrEnabled))
            {
                if (lightPosition != null)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Light Position", EditorStyles.miniBoldLabel);

                    Vector4 current = lightPosition.vectorValue;
                    Vector3 numericValue = new Vector3(current.x, current.y, current.z);

                    EditorGUI.BeginChangeCheck();
                    numericValue = EditorGUILayout.Vector3Field("Numeric", numericValue);

                    float sliderX = EditorGUILayout.Slider("X Slider", numericValue.x, -20f, 20f);
                    float sliderY = EditorGUILayout.Slider("Y Slider", numericValue.y, -20f, 20f);
                    float sliderZ = EditorGUILayout.Slider("Z Slider", numericValue.z, -20f, 20f);

                    if (EditorGUI.EndChangeCheck())
                    {
                        lightPosition.vectorValue = new Vector4(
                            sliderX, sliderY, sliderZ, current.w);
                    }
                }
            }

            // ────────────────────────────
            //  Hull Outline (Inverted Hull)
            // ────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Hull Outline (Inverted Hull)", EditorStyles.boldLabel);
            if (GUILayout.Button("このHull Outlineをペイント調整")) SPToonOutlinePaintWindow.OpenForMaterial(materialEditor.target as Material);
            EditorGUILayout.LabelField("今あるHull Outlineを、部位ごとに直接補正", EditorStyles.miniLabel);

            DrawProperty(materialEditor, useHullOutline, "Use Hull Outline");
            bool hullEnabled = useHullOutline != null && useHullOutline.floatValue > 0.5f;

            using (new EditorGUI.DisabledScope(!hullEnabled))
            {
                DrawProperty(materialEditor, hullOutlineWidth, "Width");
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Hull Outline Color / Texture", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                if (hullOutlineColor != null)
                    materialEditor.ColorProperty(hullOutlineColor, "Color");
                DrawTextureOnly(materialEditor, hullOutlineMap, "Texture");
                EditorGUI.indentLevel--;
                EditorGUILayout.EndVertical();
                DrawProperty(materialEditor, hullNoiseStrength, "Noise Strength");
                DrawProperty(materialEditor, hullNoiseScale,    "Noise Scale");
            }

            // ────────────────────────────
            //  Outline
            // ────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Outline", EditorStyles.boldLabel);

            DrawProperty(materialEditor, useOutline);
            DrawProperty(materialEditor, reverseOutline);
            DrawProperty(materialEditor, outlineThickness);
            DrawProperty(materialEditor, outlineBlur);
            DrawTextureColor(materialEditor, outlineMap, outlineColor, "Outline Color / Texture");

            // ────────────────────────────
            //  Inline
            // ────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Inline", EditorStyles.boldLabel);

            DrawProperty(materialEditor, inlineThickness);
            DrawProperty(materialEditor, inlineBlur);
            DrawProperty(materialEditor, inlineOnTop, "Inline on Top");
            DrawTextureColor(materialEditor, inlineMap, inlineColor, "Inline Color / Texture");
            DrawTextureOnly(materialEditor, curvatureMap, "Inline Curvature Map");
            DrawProperty(materialEditor, invertCurvature);

            // ────────────────────────────
            //  Toon Gradation
            // ────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Toon Gradation", EditorStyles.boldLabel);

            DrawTextureColor(materialEditor, baseMap, baseColor, "Base Color / Texture");

            DrawProperty(materialEditor, toonParam2);
            DrawTextureColor(materialEditor, toonMap2, toonColor2, "Toon Color 2 / Texture");

            DrawProperty(materialEditor, toonParam3);
            DrawTextureColor(materialEditor, toonMap3, toonColor3, "Toon Color 3 / Texture");

            DrawProperty(materialEditor, toonParam4);
            DrawTextureColor(materialEditor, toonMap4, toonColor4, "Toon Color 4 / Texture");

            DrawProperty(materialEditor, toonParam5);
            DrawTextureColor(materialEditor, toonMap5, toonColor5, "Toon Color 5 / Texture");

            DrawProperty(materialEditor, toonParam6);
            DrawTextureColor(materialEditor, toonMap6, toonColor6, "Toon Color 6 / Texture");

            DrawProperty(materialEditor, toonParam7);
            DrawTextureColor(materialEditor, toonMap7, toonColor7, "Toon Color 7 / Texture");

            DrawProperty(materialEditor, toonBlur);

            if (baseMap != null)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Base Texture Mapping", EditorStyles.miniBoldLabel);
                materialEditor.TextureScaleOffsetProperty(baseMap);
                EditorGUILayout.EndVertical();
            }


            // ────────────────────────────
            //  Normal Map
            // ────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Normal Map", EditorStyles.boldLabel);

            DrawTextureOnly(materialEditor, normalMap, "Normal Map");
            DrawProperty(materialEditor, normalStrength, "Strength");

            // ────────────────────────────
            //  Ambient Occlusion
            // ────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Ambient Occlusion", EditorStyles.boldLabel);

            DrawTextureOnly(materialEditor, aoMap, "AO Map");
            DrawProperty(materialEditor, aoStrength, "Strength");
            if (aoColor != null)
                materialEditor.ColorProperty(aoColor, "AO Color (shadow tint)");

            // ────────────────────────────
            //  Rim Light
            // ────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Rim Light", EditorStyles.boldLabel);

            DrawProperty(materialEditor, useRim, "Use Rim Light");
            bool rimEnabled = useRim != null && useRim.floatValue > 0.5f;
            using (new EditorGUI.DisabledScope(!rimEnabled))
            {
                DrawProperty(materialEditor, rimStrength,  "Strength");
                DrawProperty(materialEditor, rimSharpness, "Sharpness");
                if (rimColor != null)
                    materialEditor.ColorProperty(rimColor, "Rim Color");
            }

            // ────────────────────────────
            //  Thickness SSS
            // ────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Thickness (SSS)", EditorStyles.boldLabel);

            DrawProperty(materialEditor, useThickness, "Use Thickness");
            bool thickEnabled = useThickness != null && useThickness.floatValue > 0.5f;
            using (new EditorGUI.DisabledScope(!thickEnabled))
            {
                DrawTextureOnly(materialEditor, thicknessMap, "Thickness Map");
                DrawProperty(materialEditor, thicknessStrength, "Strength");
                if (thicknessColor != null)
                    materialEditor.ColorProperty(thicknessColor, "Thickness Color");
            }

            // ────────────────────────────
            //  MatCap
            // ────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("MatCap (World Space Normals)", EditorStyles.boldLabel);

            DrawProperty(materialEditor, useMatCap, "Use MatCap");
            bool matCapEnabled = useMatCap != null && useMatCap.floatValue > 0.5f;
            using (new EditorGUI.DisabledScope(!matCapEnabled))
            {
                DrawTextureOnly(materialEditor, matCapMap, "MatCap Texture");
                DrawProperty(materialEditor, matCapStrength, "Strength");
                DrawProperty(materialEditor, matCapBlend,    "Blend Mode (Add / Multiply)");
            }

            // ────────────────────────────
            //  Surface
            // ────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Surface", EditorStyles.boldLabel);

            // ── Surface Mode dropdown ──
            int currentMode = surfaceMode != null ? (int)surfaceMode.floatValue : 0;
            string[] modeNames = new string[]
            {
                "Opaque",
                "Cutout",
                "Transparent",
                "Cutout Both Sides",
                "Transparent Both Sides"
            };
            EditorGUI.BeginChangeCheck();
            int selectedMode = EditorGUILayout.Popup("Surface Mode", currentMode, modeNames);
            if (EditorGUI.EndChangeCheck())
            {
                if (surfaceMode != null)
                    surfaceMode.floatValue = (float)selectedMode;
            }
            currentMode = selectedMode;
            bool isTransparentMode = (currentMode == 2 || currentMode == 4);
            using (new EditorGUI.DisabledScope(!isTransparentMode))
            {
                DrawProperty(materialEditor, opacity, "Opacity");
            }
            DrawTextureOnly(materialEditor, opacityMap, "Opacity Map");
            DrawProperty(materialEditor, invertOpacity, "Invert Opacity Map");
            bool isCutoutMode = (currentMode == 1 || currentMode == 3);
            using (new EditorGUI.DisabledScope(!isCutoutMode))
            {
                DrawProperty(materialEditor, alphaClip, "Alpha Clip (0/1)");
            }
            bool alphaClipEnabled = alphaClip != null && alphaClip.floatValue > 0.5f;
            using (new EditorGUI.DisabledScope(!alphaClipEnabled))
            {
                DrawProperty(materialEditor, opacityLevel, "Alpha Clip Threshold");
            }


            if (EditorGUI.EndChangeCheck())
            {
                foreach (Object target in materialEditor.targets)
                {
                    if (target is not Material material) continue;
                    material.SetFloat("_UseHDR", hdrEnabled ? 1.0f : 0.0f);

                    int mode = (int)material.GetFloat("_SurfaceMode");
                    // Cull: 0=Off(両面) 2=Back(片面)
                    // ZWrite: 0=Off 1=On
                    // Blend: SrcBlend/DstBlend
                    //   One/Zero = 不透明
                    //   SrcAlpha/OneMinusSrcAlpha = 半透明
                    switch (mode)
                    {
                        case 0: // Opaque
                            material.SetFloat("_SurfaceCull",      2); // Back
                            material.SetFloat("_SurfaceZWrite",    1);
                            material.SetFloat("_SurfaceSrcBlend",  1); // One
                            material.SetFloat("_SurfaceDstBlend",  0); // Zero
                            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
                            break;
                        case 1: // Cutout
                            material.SetFloat("_SurfaceCull",      2); // Back
                            material.SetFloat("_SurfaceZWrite",    1);
                            material.SetFloat("_SurfaceSrcBlend",  1); // One
                            material.SetFloat("_SurfaceDstBlend",  0); // Zero
                            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                            break;
                        case 2: // Transparent
                            material.SetFloat("_SurfaceCull",      2); // Back
                            material.SetFloat("_SurfaceZWrite",    0);
                            material.SetFloat("_SurfaceSrcBlend",  5); // SrcAlpha
                            material.SetFloat("_SurfaceDstBlend",  10); // OneMinusSrcAlpha
                            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                            break;
                        case 3: // Cutout Both Sides
                            material.SetFloat("_SurfaceCull",      0); // Off
                            material.SetFloat("_SurfaceZWrite",    1);
                            material.SetFloat("_SurfaceSrcBlend",  1); // One
                            material.SetFloat("_SurfaceDstBlend",  0); // Zero
                            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                            break;
                        case 4: // Transparent Both Sides
                            material.SetFloat("_SurfaceCull",      0); // Off
                            material.SetFloat("_SurfaceZWrite",    0);
                            material.SetFloat("_SurfaceSrcBlend",  5); // SrcAlpha
                            material.SetFloat("_SurfaceDstBlend",  10); // OneMinusSrcAlpha
                            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                            break;
                    }
                    EditorUtility.SetDirty(material);
                }
            }
        }
    }
}

#endif


