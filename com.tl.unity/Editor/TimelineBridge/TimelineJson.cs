using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    public readonly struct TimelineSpec
    {
        public TimelineSpec(string name, TimelineEnd end, string next, string channel)
        {
            Name = name;
            End = end;
            Next = next;
            Channel = channel;
        }

        public readonly string Name;
        public readonly TimelineEnd End;
        public readonly string Next;
        public readonly string Channel;
    }

    public static class TimelineJson
    {
        public const double HalfTolerance = 1e-9;
        public const double GridTolerance = 1e-6;

        public static ushort Ticks(double seconds, uint tickRate)
        {
            if (tickRate == 0)
                throw new ArgumentOutOfRangeException(nameof(tickRate), "tickRate must be above 0.");
            if (double.IsNaN(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds), "Time " + seconds.ToString(CultureInfo.InvariantCulture) + " s is negative.");
            var ticks = Math.Floor(seconds * tickRate + 0.5 + HalfTolerance);
            if (ticks > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(seconds), "Time " + seconds.ToString(CultureInfo.InvariantCulture) + " s is " + ticks + " ticks, above the 65535 tick limit.");
            return (ushort)ticks;
        }

        public static string Write(TimelineAsset timeline, TimelineSpec spec, uint tickRate, List<string> warnings)
        {
            if (timeline == null)
                throw new ArgumentNullException(nameof(timeline));
            var where = "Timeline '" + spec.Name + "'";
            if (string.IsNullOrEmpty(spec.Name) || TimelineBridgeAsset.ConstantName(spec.Name).Length == 0)
                throw new InvalidOperationException(where + " has no usable name.");
            if (spec.End == TimelineEnd.Next && string.IsNullOrEmpty(spec.Next))
                throw new InvalidOperationException(where + " ends with Next but names no next timeline in the bridge.");
            var duration = Time(timeline.duration, tickRate, where + " duration", warnings);
            if (duration == 0)
                throw new InvalidOperationException(where + " is empty: its duration is 0 ticks.");
            var json = new JsonText();
            json.Open('{');
            json.Key("name").String(spec.Name);
            json.Key("duration").Raw(duration.ToString(CultureInfo.InvariantCulture));
            if (spec.End == TimelineEnd.Loop)
                json.Key("loop").Raw("true");
            if (!string.IsNullOrEmpty(spec.Channel))
                json.Key("channel").String(spec.Channel);
            if (spec.End == TimelineEnd.Next)
                json.Key("next").String(spec.Next);
            if (spec.End == TimelineEnd.Hold)
                json.Key("hold").Raw("true");
            var markers = new List<(ushort Tick, int Order, TlMarkerAsset Marker)>();
            var seen = new HashSet<IMarker>();
            var tracks = new List<TlTrackAsset>();
            foreach (var track in timeline.GetOutputTracks())
            {
                Collect(track, where, tickRate, duration, markers, seen, warnings);
                if (track is MarkerTrack)
                    continue;
                if (track.muted)
                {
                    warnings.Add(where + " track '" + track.name + "' is muted and was skipped.");
                    continue;
                }
                if (!(track is TlTrackAsset tlTrack))
                {
                    warnings.Add(where + " track '" + track.name + "' (" + track.GetType().Name + ") is not a Tl track and was ignored.");
                    continue;
                }
                var clipCount = 0;
                foreach (var _ in tlTrack.GetClips())
                    clipCount++;
                if (clipCount == 0)
                {
                    warnings.Add(where + " track '" + track.name + "' has no clips and was skipped.");
                    continue;
                }
                tracks.Add(tlTrack);
            }
            if (timeline.markerTrack != null)
                Collect(timeline.markerTrack, where, tickRate, duration, markers, seen, warnings);
            if (tracks.Count == 0)
                throw new InvalidOperationException(where + " has no Tl track with clips.");
            json.Key("tracks").Open('[');
            foreach (var track in tracks)
                WriteTrack(json, track, where, tickRate, duration, warnings);
            json.Close(']');
            if (markers.Count > 0)
            {
                markers.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));
                json.Key("markers").Open('[');
                foreach (var marker in markers)
                {
                    json.Open('{');
                    TypeKeys(json, marker.Marker.DataType, where + " marker at tick " + marker.Tick);
                    json.Key("tick").Raw(marker.Tick.ToString(CultureInfo.InvariantCulture));
                    Data(json, marker.Marker, where + " marker at tick " + marker.Tick);
                    json.Close('}');
                }
                json.Close(']');
            }
            json.Close('}');
            return json.ToString();
        }

        static void WriteTrack(JsonText json, TlTrackAsset track, string where, uint tickRate, ushort duration, List<string> warnings)
        {
            var at = where + " track '" + track.name + "'";
            var clips = new List<(TimelineClip Clip, int Order)>();
            var order = 0;
            foreach (var clip in track.GetClips())
                clips.Add((clip, order++));
            clips.Sort((a, b) => a.Clip.start != b.Clip.start ? a.Clip.start.CompareTo(b.Clip.start) : a.Order.CompareTo(b.Order));
            json.Open('{');
            TypeKeys(json, track.DataType, at);
            Data(json, track, at);
            json.Key("clips").Open('[');
            foreach (var (clip, _) in clips)
            {
                var clipAt = at + " clip '" + clip.displayName + "' at " + clip.start.ToString("R", CultureInfo.InvariantCulture) + " s";
                if (!(clip.asset is TlClipAsset data))
                    throw new InvalidOperationException(clipAt + " is not a Tl clip (" + (clip.asset == null ? "missing" : clip.asset.GetType().Name) + ").");
                var start = Time(clip.start, tickRate, clipAt + " start", warnings);
                var end = Time(clip.end, tickRate, clipAt + " end", warnings);
                if (end <= start)
                    throw new InvalidOperationException(clipAt + " is shorter than one tick after rounding.");
                if (end > duration)
                    throw new InvalidOperationException(clipAt + " ends at tick " + end + ", after the timeline duration " + duration + ".");
                json.Open('{');
                TypeKeys(json, data.DataType, clipAt);
                json.Key("start").Raw(start.ToString(CultureInfo.InvariantCulture));
                json.Key("end").Raw(end.ToString(CultureInfo.InvariantCulture));
                Data(json, data, clipAt);
                json.Close('}');
            }
            json.Close(']');
            json.Close('}');
        }

        static void Collect(TrackAsset track, string where, uint tickRate, ushort duration, List<(ushort, int, TlMarkerAsset)> markers, HashSet<IMarker> seen, List<string> warnings)
        {
            foreach (var marker in track.GetMarkers())
            {
                if (!seen.Add(marker))
                    continue;
                var at = where + " marker at " + marker.time.ToString("R", CultureInfo.InvariantCulture) + " s on '" + track.name + "'";
                if (!(marker is TlMarkerAsset tlMarker))
                {
                    warnings.Add(at + " (" + marker.GetType().Name + ") is not a Tl marker and was ignored.");
                    continue;
                }
                var tick = Time(marker.time, tickRate, at, warnings);
                if (tick >= duration)
                    throw new InvalidOperationException(at + " is at tick " + tick + ", at or after the timeline duration " + duration + ".");
                markers.Add((tick, markers.Count, tlMarker));
            }
        }

        static ushort Time(double seconds, uint tickRate, string what, List<string> warnings)
        {
            ushort ticks;
            try
            {
                ticks = Ticks(seconds, tickRate);
            }
            catch (ArgumentOutOfRangeException error)
            {
                throw new InvalidOperationException(what + ": " + error.Message.Split('\n')[0]);
            }
            var exact = seconds * tickRate;
            if (Math.Abs(exact - Math.Round(exact)) > GridTolerance)
                warnings.Add(what + " is " + exact.ToString("0.###", CultureInfo.InvariantCulture) + " ticks, rounded to " + ticks + ". Set the Timeline frame rate to the library tickRate (" + tickRate + ") and snap to frames.");
            return ticks;
        }

        static void TypeKeys(JsonText json, Type type, string where)
        {
            if (type == null)
                throw new InvalidOperationException(where + " has no data type.");
            if (type.IsNested)
                throw new InvalidOperationException(where + ": data type '" + type.FullName + "' is nested. Declare it at namespace level.");
            if (!string.IsNullOrEmpty(type.Namespace))
                json.Key("namespace").String(type.Namespace);
            json.Key("type").String(type.Name);
        }

        static void Data(JsonText json, ITlData data, string where)
        {
            var type = data.DataType;
            var fields = TlFields.Of(type);
            if (data.Fields != null)
                foreach (var stored in data.Fields)
                    if (stored != null && Array.FindIndex(fields, f => f.Name == stored.Name) < 0)
                        throw new InvalidOperationException(where + ": field '" + stored.Name + "' is not on " + type.Name + ". Remove it in the Inspector or restore the field.");
            if (fields.Length == 0)
            {
                json.Key("data").Raw("{}");
                return;
            }
            json.Key("data").Open('{');
            foreach (var field in fields)
            {
                if (!TlFields.IsSupported(field.FieldType))
                    throw new InvalidOperationException(where + ": field '" + field.Name + "' of " + type.Name + " is a " + field.FieldType.Name + ". The bridge writes number, bool and enum fields only.");
                var stored = TlFields.Find(data.Fields, field.Name);
                object value;
                try
                {
                    value = TlFields.Parse(field.FieldType, stored == null ? string.Empty : stored.Value);
                }
                catch (Exception error) when (error is FormatException || error is OverflowException || error is ArgumentException)
                {
                    throw new InvalidOperationException(where + ": field '" + field.Name + "' value '" + stored.Value + "' is not a " + field.FieldType.Name + ".");
                }
                var text = TlFields.Format(value);
                if (field.FieldType.IsEnum && Enum.IsDefined(field.FieldType, value))
                    json.Key(field.Name).String(text);
                else
                    json.Key(field.Name).Raw(text);
            }
            json.Close('}');
        }

        sealed class JsonText
        {
            readonly StringBuilder _text = new StringBuilder();
            readonly Stack<bool> _first = new Stack<bool>();
            bool _afterKey;

            public JsonText Open(char bracket)
            {
                Value();
                _text.Append(bracket);
                _first.Push(true);
                return this;
            }

            public JsonText Close(char bracket)
            {
                var empty = _first.Pop();
                if (!empty)
                    Line();
                _text.Append(bracket);
                return this;
            }

            public JsonText Key(string name)
            {
                Separator();
                Quote(name);
                _text.Append(": ");
                _afterKey = true;
                return this;
            }

            public JsonText String(string value)
            {
                Value();
                Quote(value);
                return this;
            }

            public JsonText Raw(string value)
            {
                Value();
                _text.Append(value);
                return this;
            }

            public override string ToString()
            {
                return _text.ToString();
            }

            void Value()
            {
                if (_afterKey)
                {
                    _afterKey = false;
                    return;
                }
                if (_first.Count > 0)
                    Separator();
            }

            void Separator()
            {
                if (_first.Count == 0)
                    return;
                if (!_first.Peek())
                    _text.Append(',');
                _first.Pop();
                _first.Push(false);
                Line();
            }

            void Line()
            {
                _text.Append('\n').Append(' ', _first.Count * 2);
            }

            void Quote(string value)
            {
                _text.Append('"');
                foreach (var c in value ?? string.Empty)
                {
                    switch (c)
                    {
                        case '"': _text.Append("\\\""); break;
                        case '\\': _text.Append("\\\\"); break;
                        case '\n': _text.Append("\\n"); break;
                        case '\r': _text.Append("\\r"); break;
                        case '\t': _text.Append("\\t"); break;
                        default:
                            if (c < ' ')
                                _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                _text.Append(c);
                            break;
                    }
                }
                _text.Append('"');
            }
        }
    }
}
