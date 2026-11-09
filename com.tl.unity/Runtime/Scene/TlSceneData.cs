using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tl.Unity
{
    public sealed class TlSceneData
    {
        const uint Magic = 0x43534C54u;
        const uint Version = 1u;

        public readonly struct State
        {
            public State(string type, byte[] bytes)
            {
                Type = type;
                Bytes = bytes;
            }

            public readonly string Type;
            public readonly byte[] Bytes;
        }

        public readonly struct Play
        {
            public Play(ushort timeline, ushort tick)
            {
                Timeline = timeline;
                Tick = tick;
            }

            public readonly ushort Timeline;
            public readonly ushort Tick;
        }

        public readonly struct Request
        {
            public Request(int step, int entity, global::Tl.RequestOp op, ushort target, ushort value)
            {
                Step = step;
                Entity = entity;
                Op = op;
                Target = target;
                Value = value;
            }

            public readonly int Step;
            public readonly int Entity;
            public readonly global::Tl.RequestOp Op;
            public readonly ushort Target;
            public readonly ushort Value;
        }

        public readonly struct Turn
        {
            public Turn(int step, bool forward)
            {
                Step = step;
                Forward = forward;
            }

            public readonly int Step;
            public readonly bool Forward;
        }

        public sealed class EntityData
        {
            public string Name = string.Empty;
            public readonly List<State> States = new List<State>();
            public readonly List<Play> Plays = new List<Play>();
        }

        public readonly List<string> Columns = new List<string>();
        public readonly List<EntityData> Entities = new List<EntityData>();
        public readonly List<Request> Requests = new List<Request>();
        public readonly List<Turn> Turns = new List<Turn>();

        public bool ForwardAt(int step, bool current)
        {
            for (var i = 0; i < Turns.Count; i++)
                if (Turns[i].Step == step)
                    current = Turns[i].Forward;
            return current;
        }

        public byte[] ToBytes()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(Columns.Count);
                foreach (var column in Columns)
                    writer.Write(column);
                writer.Write(Entities.Count);
                foreach (var entity in Entities)
                {
                    writer.Write(entity.Name);
                    writer.Write(entity.States.Count);
                    foreach (var state in entity.States)
                    {
                        writer.Write(state.Type);
                        writer.Write(state.Bytes.Length);
                        writer.Write(state.Bytes);
                    }
                    writer.Write(entity.Plays.Count);
                    foreach (var play in entity.Plays)
                    {
                        writer.Write(play.Timeline);
                        writer.Write(play.Tick);
                    }
                }
                writer.Write(Requests.Count);
                foreach (var request in Requests)
                {
                    writer.Write(request.Step);
                    writer.Write(request.Entity);
                    writer.Write((byte)request.Op);
                    writer.Write(request.Target);
                    writer.Write(request.Value);
                }
                writer.Write(Turns.Count);
                foreach (var turn in Turns)
                {
                    writer.Write(turn.Step);
                    writer.Write(turn.Forward);
                }
            }
            return stream.ToArray();
        }

        public static TlSceneData FromBytes(byte[] bytes)
        {
            using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8, false);
            if (reader.ReadUInt32() != Magic)
                throw new InvalidDataException("Tl scene data: wrong header.");
            if (reader.ReadUInt32() != Version)
                throw new InvalidDataException("Tl scene data: unsupported version.");
            var scene = new TlSceneData();
            for (var n = reader.ReadInt32(); n > 0; n--)
                scene.Columns.Add(reader.ReadString());
            for (var n = reader.ReadInt32(); n > 0; n--)
            {
                var entity = new EntityData { Name = reader.ReadString() };
                for (var s = reader.ReadInt32(); s > 0; s--)
                {
                    var type = reader.ReadString();
                    entity.States.Add(new State(type, reader.ReadBytes(reader.ReadInt32())));
                }
                for (var p = reader.ReadInt32(); p > 0; p--)
                    entity.Plays.Add(new Play(reader.ReadUInt16(), reader.ReadUInt16()));
                scene.Entities.Add(entity);
            }
            for (var n = reader.ReadInt32(); n > 0; n--)
                scene.Requests.Add(new Request(reader.ReadInt32(), reader.ReadInt32(), (global::Tl.RequestOp)reader.ReadByte(), reader.ReadUInt16(), reader.ReadUInt16()));
            for (var n = reader.ReadInt32(); n > 0; n--)
                scene.Turns.Add(new Turn(reader.ReadInt32(), reader.ReadBoolean()));
            return scene;
        }
    }
}
