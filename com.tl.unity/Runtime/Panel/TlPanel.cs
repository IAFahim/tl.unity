using System.Globalization;
using System.Text;
using Tl;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Tl.Unity
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Tl/Tl Panel")]
    public sealed class TlPanel : MonoBehaviour
    {
        public const int MaxTargets = 8;

        public readonly struct PanelState
        {
            public PanelState(bool live, ushort tick, byte sixteenths, bool paused, bool reverse, bool ended, byte channel)
            {
                Live = live;
                Tick = tick;
                Sixteenths = sixteenths;
                Paused = paused;
                Reverse = reverse;
                Ended = ended;
                Channel = channel;
            }

            public readonly bool Live;
            public readonly ushort Tick;
            public readonly byte Sixteenths;
            public readonly bool Paused;
            public readonly bool Reverse;
            public readonly bool Ended;
            public readonly byte Channel;
        }

        public int TicksPerSecond = 60;
        public Vector2 Origin = new Vector2(16f, 58f);
        public int FontSize = 16;

        World _world;
        EntityQuery _targets;
        EntityQuery _step;
        int _target;
        int _selected;
        GUIStyle _label;
        GUIStyle _button;

        public int TargetCount => Ready ? Mathf.Min(_targets.CalculateEntityCount(), MaxTargets) : 0;

        public int TimelineCount => TryTarget(out var entity) ? _world.EntityManager.GetBuffer<TlTimelineSet>(entity, true).Length : 0;

        public bool Ready => Bind();

        public void SelectTarget(int index)
        {
            _target = Mathf.Max(0, index);
            _selected = 0;
        }

        public void SelectTimeline(int index) => _selected = Mathf.Max(0, index);

        public string TimelineName(int index)
        {
            if (!TryTarget(out var entity))
                return "-";
            var set = _world.EntityManager.GetBuffer<TlTimelineSet>(entity, true);
            return index >= 0 && index < set.Length ? NameOf(set[index]) : "-";
        }

        public PanelState State()
        {
            if (!TryTimeline(out var entity, out var entry))
                return default;
            foreach (var playback in _world.EntityManager.GetBuffer<Playback>(entity, true))
                if (playback.Timeline == entry.Id)
                {
                    var flags = playback.Flags;
                    return new PanelState(true, playback.Tick, playback.Rate == 0 ? (byte)16 : playback.Rate, (flags & PlaybackFlags.Paused) != 0, (flags & PlaybackFlags.Reverse) != 0, (flags & PlaybackFlags.Ended) != 0, playback.Channel);
                }
            return default;
        }

        public bool WorldForward
        {
            get => Bind() && _step.CalculateEntityCount() == 1 && _step.GetSingleton<TimelineStep>().Forward;
            set
            {
                if (Bind() && _step.CalculateEntityCount() == 1)
                    _step.SetSingleton(new TimelineStep { Forward = value });
            }
        }

        public bool Play() => Send(static (requests, timeline) => requests.Start(timeline));

        public bool Pause() => Send(static (requests, timeline) => requests.Pause(timeline));

        public bool Resume() => Send(static (requests, timeline) => requests.Resume(timeline));

        public bool Stop() => Send(static (requests, timeline) => requests.Stop(timeline));

        public bool ToggleReverse()
        {
            var reverse = !State().Reverse;
            return Send((requests, timeline) => requests.Reverse(timeline, reverse));
        }

        public bool Restart() => SeekTo(0);

        public bool SeekBy(float seconds) => SeekTo(Mathf.Max(0, State().Tick + Mathf.RoundToInt(seconds * TicksPerSecond)));

        public bool SeekTo(int tick)
        {
            var at = (ushort)Mathf.Clamp(tick, 0, ushort.MaxValue);
            return State().Live ? Send((requests, timeline) => requests.Seek(timeline, at)) : Send((requests, timeline) => requests.Start(timeline, at));
        }

        public bool SetSpeed(float multiplier)
        {
            var sixteenths = (ushort)Mathf.Clamp(Mathf.RoundToInt(multiplier * 16f), 1, 255);
            return Send((requests, timeline) => requests.SetSpeed(timeline, sixteenths));
        }

        public bool StopChannel()
        {
            var channel = State().Channel;
            if (channel == 0 || !TryTarget(out var entity))
                return false;
            _world.EntityManager.GetBuffer<TimelineRequest>(entity).StopChannel(channel);
            return true;
        }

        public bool StopAll()
        {
            if (!TryTarget(out var entity))
                return false;
            _world.EntityManager.GetBuffer<TimelineRequest>(entity).StopAll();
            return true;
        }

        bool Send(System.Action<DynamicBuffer<TimelineRequest>, TlTimeline> action)
        {
            if (!TryTimeline(out var entity, out var entry))
                return false;
            action(_world.EntityManager.GetBuffer<TimelineRequest>(entity), new TlTimeline { Asset = entry.Blob.Value.Asset.ToAssetId() });
            return true;
        }

        bool Bind()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
                return false;
            if (_world != world)
            {
                _world = world;
                _targets = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Playback>(), ComponentType.ReadWrite<TimelineRequest>(), ComponentType.ReadOnly<TlTimelineSet>());
                _step = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<TimelineStep>());
            }
            return true;
        }

        bool TryTarget(out Entity entity)
        {
            entity = Entity.Null;
            if (!Bind())
                return false;
            var count = Mathf.Min(_targets.CalculateEntityCount(), MaxTargets);
            if (count == 0)
                return false;
            _target = Mathf.Clamp(_target, 0, count - 1);
            using var entities = _targets.ToEntityArray(Allocator.Temp);
            entity = entities[_target];
            return true;
        }

        bool TryTimeline(out Entity entity, out TlTimelineSet entry)
        {
            entry = default;
            if (!TryTarget(out entity))
                return false;
            var set = _world.EntityManager.GetBuffer<TlTimelineSet>(entity, true);
            if (set.Length == 0)
                return false;
            _selected = Mathf.Clamp(_selected, 0, set.Length - 1);
            entry = set[_selected];
            return true;
        }

        void OnGUI()
        {
            if (!Bind())
                return;
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = FontSize, normal = { textColor = Color.white } };
            _button ??= new GUIStyle(GUI.skin.button) { fontSize = FontSize };
            var count = TargetCount;
            if (count == 0)
                return;
            var height = FontSize + 12f;
            var y = Origin.y;
            GUI.Label(new Rect(Origin.x, y, 900f, height), Status(), _label);
            y += height + 4f;
            var x = Origin.x;
            for (var i = 0; i < count; i++)
                if (Button(ref x, y, "Target " + (i + 1), 90f, i == _target))
                    SelectTarget(i);
            y += height + 4f;
            x = Origin.x;
            var timelines = TimelineCount;
            for (var i = 0; i < timelines; i++)
            {
                var name = TimelineName(i);
                if (Button(ref x, y, name, 12f * name.Length + 24f, i == _selected))
                    SelectTimeline(i);
            }
            y += height + 4f;
            x = Origin.x;
            if (Button(ref x, y, "Play", 70f)) Play();
            if (Button(ref x, y, "Pause", 70f)) Pause();
            if (Button(ref x, y, "Resume", 80f)) Resume();
            if (Button(ref x, y, "Stop", 70f)) Stop();
            if (Button(ref x, y, "Reverse", 90f, State().Reverse)) ToggleReverse();
            y += height + 4f;
            x = Origin.x;
            if (Button(ref x, y, "Restart", 80f)) Restart();
            if (Button(ref x, y, "-1 s", 60f)) SeekBy(-1f);
            if (Button(ref x, y, "+1 s", 60f)) SeekBy(1f);
            y += height + 4f;
            x = Origin.x;
            GUI.Label(new Rect(x, y, 70f, height), "Speed", _label);
            x += 70f;
            var current = State().Sixteenths;
            foreach (var multiplier in new[] { 0.25f, 0.5f, 1f, 2f, 4f })
                if (Button(ref x, y, multiplier.ToString("0.##", CultureInfo.InvariantCulture) + "x", 64f, current == (byte)Mathf.RoundToInt(multiplier * 16f)))
                    SetSpeed(multiplier);
            y += height + 4f;
            x = Origin.x;
            if (Button(ref x, y, "Stop channel", 130f)) StopChannel();
            if (Button(ref x, y, "Stop all", 90f)) StopAll();
            var forward = WorldForward;
            if (Button(ref x, y, forward ? "World: forward" : "World: rewinding", 170f, !forward)) WorldForward = !forward;
        }

        bool Button(ref float x, float y, string text, float width, bool on = false)
        {
            var rect = new Rect(x, y, width, FontSize + 12f);
            x += width + 6f;
            var color = GUI.backgroundColor;
            if (on)
                GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
            var pressed = GUI.Button(rect, text, _button);
            GUI.backgroundColor = color;
            return pressed;
        }

        string Status()
        {
            if (!TryTimeline(out _, out var entry))
                return "no timeline";
            var state = State();
            var name = NameOf(entry);
            if (!state.Live)
                return name + "  not playing";
            var flags = state.Paused ? "  paused" : string.Empty;
            if (state.Reverse)
                flags += "  reverse";
            if (state.Ended)
                flags += "  held";
            return string.Format(CultureInfo.InvariantCulture, "{0}  tick {1} ({2:0.00} s)  speed {3:0.##}x{4}", name, state.Tick, state.Tick / (float)TicksPerSecond, state.Sixteenths / 16f, flags);
        }

        static string NameOf(TlTimelineSet entry)
        {
            ref var bytes = ref entry.Blob.Value.Name;
            var text = new byte[bytes.Length];
            for (var i = 0; i < text.Length; i++)
                text[i] = bytes[i];
            return Encoding.UTF8.GetString(text);
        }
    }
}
