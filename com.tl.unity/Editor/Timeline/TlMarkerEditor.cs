using UnityEditor;

namespace Tl.Unity.Editor
{
    [CustomEditor(typeof(TlMarker))]
    public sealed class TlMarkerEditor : UnityEditor.Editor
    {
        public const string MissingType = "This marker's code was not found (a compile error, or the marker type was renamed or removed). Its values are kept; fix the code to edit them.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var time = serializedObject.FindProperty("m_Time");
            if (time != null)
                EditorGUILayout.PropertyField(time);
            var data = serializedObject.FindProperty(nameof(TlMarker.Data));
            if (data == null || data.managedReferenceValue == null)
            {
                EditorGUILayout.HelpBox(MissingType, MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
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
