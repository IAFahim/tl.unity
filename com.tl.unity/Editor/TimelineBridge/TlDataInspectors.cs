using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Tl.Unity.Editor
{
    public abstract class TlDataInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (!(target is ITlData data) || data.DataType == null)
                return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(data.DataType.Name, EditorStyles.boldLabel);
            var fields = TlFields.Of(data.DataType);
            foreach (var field in fields)
            {
                var stored = TlFields.Find(data.Fields, field.Name);
                var text = stored == null ? string.Empty : stored.Value;
                if (!TlFields.IsSupported(field.FieldType))
                {
                    EditorGUILayout.HelpBox(field.Name + " is a " + field.FieldType.Name + "; the bridge writes number, bool and enum fields only.", MessageType.Error);
                    continue;
                }
                object value;
                try
                {
                    value = TlFields.Parse(field.FieldType, text);
                }
                catch (Exception)
                {
                    value = Activator.CreateInstance(field.FieldType);
                }
                EditorGUI.BeginChangeCheck();
                var edited = Draw(field.Name, field.FieldType, value);
                if (!EditorGUI.EndChangeCheck())
                    continue;
                Undo.RecordObject(target, "Edit " + field.Name);
                var list = new List<TlField>(data.Fields);
                list.RemoveAll(f => f == null || f.Name == field.Name);
                list.Add(new TlField(field.Name, TlFields.Format(edited)));
                list.Sort((a, b) => Array.FindIndex(fields, f => f.Name == a.Name).CompareTo(Array.FindIndex(fields, f => f.Name == b.Name)));
                data.Fields = list;
                EditorUtility.SetDirty(target);
            }
            foreach (var stored in data.Fields)
                if (stored != null && Array.FindIndex(fields, f => f.Name == stored.Name) < 0)
                    EditorGUILayout.HelpBox("Stored field '" + stored.Name + "' is not on " + data.DataType.Name + "; the bake fails until it is removed.", MessageType.Warning);
        }

        static object Draw(string label, Type type, object value)
        {
            if (type.IsEnum)
                return EditorGUILayout.EnumPopup(label, (Enum)value);
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean: return EditorGUILayout.Toggle(label, (bool)value);
                case TypeCode.Single: return EditorGUILayout.FloatField(label, (float)value);
                case TypeCode.Double: return EditorGUILayout.DoubleField(label, (double)value);
                case TypeCode.Int64: return EditorGUILayout.LongField(label, (long)value);
                case TypeCode.UInt64: return (ulong)Math.Max(0L, EditorGUILayout.LongField(label, (long)(ulong)value));
                case TypeCode.UInt32: return (uint)Math.Max(0L, Math.Min(uint.MaxValue, EditorGUILayout.LongField(label, (uint)value)));
                default:
                    var number = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                    var min = Convert.ToInt64(type.GetField("MinValue").GetValue(null), System.Globalization.CultureInfo.InvariantCulture);
                    var max = Convert.ToInt64(type.GetField("MaxValue").GetValue(null), System.Globalization.CultureInfo.InvariantCulture);
                    var edited = Math.Max(min, Math.Min(max, EditorGUILayout.LongField(label, number)));
                    return Convert.ChangeType(edited, type, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    [CustomEditor(typeof(TlTrackAsset), true)]
    public sealed class TlTrackInspector : TlDataInspector
    {
    }

    [CustomEditor(typeof(TlClipAsset), true)]
    public sealed class TlClipInspector : TlDataInspector
    {
    }

    [CustomEditor(typeof(TlMarkerAsset), true)]
    public sealed class TlMarkerInspector : TlDataInspector
    {
    }

    [CustomEditor(typeof(TimelineBridgeAsset))]
    public sealed class TimelineBridgeInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            if (GUILayout.Button("Bake"))
                TimelineBridgeBake.BakeLogged((TimelineBridgeAsset)target);
        }
    }
}
