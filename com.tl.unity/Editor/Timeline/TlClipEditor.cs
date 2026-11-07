using UnityEditor;

namespace Tl.Unity.Editor
{
    [CustomEditor(typeof(TlClip))]
    public sealed class TlClipEditor : UnityEditor.Editor
    {
        public const string MissingType = "This clip's code was not found (a compile error, or the clip type was renamed or removed). Its values are kept; fix the code to edit them.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var data = serializedObject.FindProperty(nameof(TlClip.Data));
            if (data == null || data.managedReferenceValue == null)
            {
                EditorGUILayout.HelpBox(MissingType, MessageType.Warning);
                return;
            }
            var end = data.GetEndProperty();
            var field = data.Copy();
            var enter = true;
            while (field.NextVisible(enter) && !SerializedProperty.EqualContents(field, end))
            {
                enter = false;
                EditorGUILayout.PropertyField(field, true);
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}
