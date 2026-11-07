using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    [InitializeOnLoad]
#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    static class TlTimelineSection
    {
        static readonly GUIContent ChannelLabel = new GUIContent("Channel", "Only one timeline plays per channel: starting another one on the same channel replaces it. None means no channel.");
        static readonly GUIContent EndLabel = new GUIContent("When it ends", "Stop, Loop, hold the last frame, or play the next timeline.");
        static readonly GUIContent NextLabel = new GUIContent("Play next", "The timeline that starts when this one ends.");
        static readonly string[] Channels = BuildChannels();

        static TlTimelineSection()
        {
            UnityEditor.Editor.finishedDefaultHeaderGUI += Draw;
        }

        static string[] BuildChannels()
        {
            var names = new string[16];
            names[0] = "None";
            for (var i = 1; i < names.Length; i++)
                names[i] = "Channel " + i;
            return names;
        }

        static void Draw(UnityEditor.Editor editor)
        {
            if (editor.targets.Length != 1 || !(editor.target is TimelineAsset timeline))
                return;
            var settings = TlDirectorTimelines.Settings(timeline);
            var end = settings != null ? settings.End : TlEnd.Stop;
            var next = settings != null ? settings.Next : null;
            var channel = settings != null ? settings.Channel : 0;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Tl", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                channel = EditorGUILayout.Popup(ChannelLabel, Mathf.Clamp(channel, 0, 15), Channels);
                end = (TlEnd)EditorGUILayout.EnumPopup(EndLabel, end);
                if (end == TlEnd.Next)
                    next = (TimelineAsset)EditorGUILayout.ObjectField(NextLabel, next, typeof(TimelineAsset), false);
                if (EditorGUI.EndChangeCheck())
                    TlInspectorModel.SetSettings(timeline, end, end == TlEnd.Next ? next : null, channel);
                var hint = TlInspectorModel.FrameRateHint(timeline, TlProjectSettings.instance.TicksPerSecond);
                if (hint != null)
                {
                    EditorGUILayout.HelpBox(hint, MessageType.Info);
                    if (GUILayout.Button("Use " + TlProjectSettings.instance.TicksPerSecond + " frames per second"))
                    {
                        Undo.RecordObject(timeline, "Tl Frame Rate");
                        var editorSettings = timeline.editorSettings;
                        editorSettings.frameRate = TlProjectSettings.instance.TicksPerSecond;
                        EditorUtility.SetDirty(timeline);
                    }
                }
                EditorGUILayout.LabelField("Status", TlInspectorModel.Status(timeline), EditorStyles.wordWrappedLabel);
            }
        }
    }

    [CustomEditor(typeof(PlayableDirector))]
    [CanEditMultipleObjects]
#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public sealed class TlDirectorEditor : UnityEditor.Editor
    {
        const string FoldoutKey = "Tl.DirectorFoldout";
        static readonly Type UnityDirectorEditor = Type.GetType("UnityEditor.DirectorEditor, UnityEditor");
        UnityEditor.Editor _unity;

        void OnDisable()
        {
            if (_unity != null)
                DestroyImmediate(_unity);
        }

        public override void OnInspectorGUI()
        {
            if (UnityDirectorEditor != null)
            {
                if (_unity == null)
                    _unity = CreateEditor(targets, UnityDirectorEditor);
                _unity.OnInspectorGUI();
            }
            else
                DrawDefaultInspector();
            if (targets.Length != 1)
                return;
            var director = (PlayableDirector)target;
            var also = director.GetComponent<TlAlsoPlays>();
            var plays = TlInspectorModel.Plays(director, also);
            var hasTl = plays.Exists(t => TlInspectorModel.TlTracks(t) > 0);
            var open = SessionState.GetBool(FoldoutKey, true);
            open = EditorGUILayout.Foldout(open, "Tl", true);
            SessionState.SetBool(FoldoutKey, open);
            if (!open)
                return;
            using (new EditorGUI.IndentLevelScope())
            {
                foreach (var message in TlInspectorModel.DirectorMessages(director, also))
                    EditorGUILayout.HelpBox(message, MessageType.Warning);
                DrawAlsoPlays(director, also);
                if (hasTl)
                {
                    var names = string.Join(", ", plays.ConvertAll(t => t.name));
                    EditorGUILayout.LabelField("Plays", names, EditorStyles.wordWrappedLabel);
                }
            }
        }

        void DrawAlsoPlays(PlayableDirector director, TlAlsoPlays also)
        {
            EditorGUILayout.LabelField(new GUIContent("Also Plays", "Timelines this object can play besides its Playable, for example when code or another timeline starts them."));
            using (new EditorGUI.IndentLevelScope())
            {
                var count = also == null ? 0 : also.Timelines.Count;
                for (var i = 0; i < count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUI.BeginChangeCheck();
                    var picked = (TimelineAsset)EditorGUILayout.ObjectField(also.Timelines[i], typeof(TimelineAsset), false);
                    var remove = GUILayout.Button("-", GUILayout.Width(22));
                    EditorGUILayout.EndHorizontal();
                    if (EditorGUI.EndChangeCheck() || remove)
                    {
                        Undo.RecordObject(also, "Also Plays");
                        if (remove)
                            also.Timelines.RemoveAt(i);
                        else
                            also.Timelines[i] = picked;
                        EditorUtility.SetDirty(also);
                        GUIUtility.ExitGUI();
                    }
                }
                EditorGUI.BeginChangeCheck();
                var added = (TimelineAsset)EditorGUILayout.ObjectField("Add", null, typeof(TimelineAsset), false);
                if (EditorGUI.EndChangeCheck() && added != null)
                {
                    also = TlInspectorModel.AlsoPlays(director, true);
                    Undo.RecordObject(also, "Also Plays");
                    also.Timelines.Add(added);
                    EditorUtility.SetDirty(also);
                }
            }
        }
    }
}
