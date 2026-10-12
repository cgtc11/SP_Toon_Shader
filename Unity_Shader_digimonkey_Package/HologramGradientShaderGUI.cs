using UnityEditor;
using UnityEngine;

namespace DiGiMonkey
{
    public sealed class HologramGradientShaderGUI : ShaderGUI
    {
        public static void DrawAxis(MaterialEditor editor, MaterialProperty axisProperty, bool allowUV)
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
            foreach (MaterialProperty property in properties)
            {
                if ((property.flags & MaterialProperty.PropFlags.HideInInspector) != 0) continue;
                if (property.name == "_GradientAxis") DrawAxis(editor, property, true);
                else if (property.type == MaterialProperty.PropType.Texture)
                {
                    editor.TexturePropertySingleLine(new GUIContent(property.displayName), property);
                    if ((property.flags & MaterialProperty.PropFlags.NoScaleOffset) == 0)
                        editor.TextureScaleOffsetProperty(property);
                }
                else editor.ShaderProperty(property, property.displayName);
            }
            editor.RenderQueueField();
            editor.EnableInstancingField();
            editor.DoubleSidedGIField();
        }
    }
}
