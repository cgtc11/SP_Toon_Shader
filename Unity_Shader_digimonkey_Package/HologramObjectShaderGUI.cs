using UnityEditor;
using UnityEngine;

namespace DiGiMonkey
{
    public sealed class HologramObjectShaderGUI : ShaderGUI
    {
        private static readonly string[] Modes = { "Original UV", "Planar", "Box (Triplanar)", "Cylindrical (Y Axis)", "Spherical (Y Axis)" };
        private static readonly string[] Planes = { "Front (XY / Z Direction)", "Side (ZY / X Direction)", "Top (XZ / Y Direction)" };

        private static void Popup(MaterialEditor editor, MaterialProperty property, string label, string[] options)
        {
            EditorGUI.showMixedValue = property.hasMixedValue;
            EditorGUI.BeginChangeCheck();
            int selected = EditorGUILayout.Popup(label, Mathf.Clamp(Mathf.RoundToInt(property.floatValue), 0, options.Length - 1), options);
            if (EditorGUI.EndChangeCheck())
            {
                editor.RegisterPropertyChangeUndo(label);
                property.floatValue = selected;
            }
            EditorGUI.showMixedValue = false;
        }

        private static void Vector3Property(MaterialEditor editor, MaterialProperty property, string label, bool positive)
        {
            Vector4 old = property.vectorValue;
            EditorGUI.showMixedValue = property.hasMixedValue;
            EditorGUI.BeginChangeCheck();
            Vector3 value = EditorGUILayout.Vector3Field(label, new Vector3(old.x, old.y, old.z));
            if (EditorGUI.EndChangeCheck())
            {
                editor.RegisterPropertyChangeUndo(label);
                if (positive)
                    value = new Vector3(Mathf.Max(Mathf.Abs(value.x), 0.0001f), Mathf.Max(Mathf.Abs(value.y), 0.0001f), Mathf.Max(Mathf.Abs(value.z), 0.0001f));
                property.vectorValue = new Vector4(value.x, value.y, value.z, old.w);
            }
            EditorGUI.showMixedValue = false;
        }

        private static void DrawGradientAxis(MaterialEditor editor, MaterialProperty axisProperty, bool allowUV)
        {
            string[] labels = allowUV ? new[] { "UV", "Local X", "Local Y", "Local Z" }
                : new[] { "Local X", "Local Y", "Local Z" };
            int[] values = allowUV ? new[] { 4, 1, 2, 3 } : new[] { 1, 2, 3 };
            int effectiveAxis = -1;
            bool mixed = false;
            foreach (Object target in editor.targets)
            {
                if (!(target is Material material)) continue;
                int axis = Mathf.RoundToInt(material.GetFloat("_GradientAxis"));
                if (axis == 0)
                    axis = material.GetFloat("_GradientUseZAxis") > 0.5f ? 3 : (allowUV ? 4 : 2);
                if (effectiveAxis < 0) effectiveAxis = axis;
                else if (effectiveAxis != axis) mixed = true;
            }
            int index = Mathf.Max(0, System.Array.IndexOf(values, effectiveAxis));
            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            int selected = EditorGUILayout.Popup("Gradient Axis", index, labels);
            if (EditorGUI.EndChangeCheck())
            {
                editor.RegisterPropertyChangeUndo("Gradient Axis");
                axisProperty.floatValue = values[selected];
            }
            EditorGUI.showMixedValue = false;
            EditorGUILayout.HelpBox("Choose the local axis that matches the height of your model. Local Z may point forward. Use Invert Top/Bottom to reverse the gradient.", MessageType.Info);
        }

        public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties)
        {
            MaterialProperty texture = FindProperty("_BaseMap", properties);
            MaterialProperty mode = FindProperty("_ProjectionMode", properties);
            editor.TexturePropertySingleLine(new GUIContent("Mask / Detail Texture"), texture);
            editor.TextureScaleOffsetProperty(texture);
            EditorGUILayout.LabelField("Texture Projection (Model Space)", EditorStyles.boldLabel);
            Popup(editor, mode, "Projection Mode", Modes);
            bool projected = mode.hasMixedValue || mode.floatValue > 0.5f;
            using (new EditorGUI.DisabledScope(!projected))
            {
                if (mode.hasMixedValue || Mathf.RoundToInt(mode.floatValue) == 1)
                    Popup(editor, FindProperty("_ProjectionPlane", properties), "Projection Plane", Planes);
                Vector3Property(editor, FindProperty("_ProjectionCenter", properties), "Projection Center (Local XYZ)", false);
                Vector3Property(editor, FindProperty("_ProjectionSize", properties), "Projection Size (Local XYZ)", true);
                if (mode.hasMixedValue || Mathf.RoundToInt(mode.floatValue) == 2)
                    editor.ShaderProperty(FindProperty("_ProjectionBlend", properties), "Box Blend Sharpness");
            }
            EditorGUILayout.HelpBox("Use Tiling to scale the texture and Offset to move it. Projection center and size use model-local coordinates. Cylindrical and spherical mappings have a seam at the back (-Z).", MessageType.Info);
            EditorGUILayout.Space();
            foreach (MaterialProperty property in properties)
            {
                if (property.name == "_BaseMap" || property.name.StartsWith("_Projection")
                    || (property.flags & MaterialProperty.PropFlags.HideInInspector) != 0) continue;
                if (property.name == "_GradientAxis") DrawGradientAxis(editor, property, false);
                else editor.ShaderProperty(property, property.displayName);
            }
            editor.RenderQueueField();
            editor.EnableInstancingField();
            editor.DoubleSidedGIField();
        }
    }
}
