using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.LowLevel.Unsafe;

namespace Tl.Unity
{
    public struct KindPulseHit
    {
        public ArchetypeChunk Chunk;
        public Entity Entity;
        public int Cell;
        public int Row;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public unsafe struct KindPulseLane
    {
        public KindPulseHit* Items;
        public int Count;
        public int Capacity;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(in ArchetypeChunk chunk, Entity entity, int row, int cell)
        {
            if (Count == Capacity) Grow();
            Items[Count++] = new KindPulseHit { Chunk = chunk, Entity = entity, Cell = cell, Row = row };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow()
        {
            long nextLong = Capacity == 0 ? 64L : (long)Capacity * 2L;
            var itemSize = UnsafeUtility.SizeOf<KindPulseHit>();
            if (nextLong > int.MaxValue || nextLong * itemSize > int.MaxValue)
                throw new InvalidOperationException("Tl: pulse lane buffer would exceed 2 GB.");
            var next = (int)nextLong;
            var bytes = nextLong * itemSize;
            var block = (KindPulseHit*)UnsafeUtility.Malloc(bytes, 16, Allocator.Persistent);
            if (Items != null)
            {
                if (Count > 0) UnsafeUtility.MemCpy(block, Items, (long)Count * itemSize);
                UnsafeUtility.Free(Items, Allocator.Persistent);
            }
            Items = block;
            Capacity = next;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Rewind(Entity entity)
        {
            while (Count > 0 && Items[Count - 1].Entity == entity) Count--;
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct KindWorkerCache
    {
        public EntityArchetype Archetype;
        public ulong Reach;
        public byte Flags;
    }

    public unsafe struct KindIndex
    {
        public int TimelineCount;
        public int KindCount;
        public global::Tl.LibraryTimeline* Timelines;
        public global::Tl.KindSectionView Sec;
        public ulong* Closure;
        public uint* Pairs;
        public int* PairStart;
        public int* WindowBase;
        public int* FirstStage;
        public int* StageCount;
        public int* StepStart;
        public global::Tl.LibraryStep** StepTable;
        public global::Tl.LibraryStep** WindowStep;
        public byte** MarkerValues;
        public int WindowTotal;
        public int StepTableLength;
        public int PairCount;
        public ComponentType* EntryTypes;
        public ComponentType* ActiveTypes;
        public ComponentType* GrowTypes;
        public int* GrowStart;
        public ulong Bound;
        public int* IdKeys;
        public int* IdKinds;
        public int IdCount;
        public ushort* Routes;
        public int* EntryStride;
        public int* OwnerKeys;
        public int* OwnerKinds;
        public int* OwnerIds;
        public int OwnerCount;
        public global::Tl.KindCalm Calm;
        public global::Tl.KindScore Score;
        public global::Tl.KindValues* Values;
        public uint* SpanMeta;
        public int* PulseBase;
        public byte* PulseBits;
        public ulong* PulseMasks;
        public global::Tl.KindPulseValues* PulseValuesTable;
        public ulong* TablePulses;
        public int ThreadCount;
        public KindPulseLane* PulseLanes;
        public ulong* TickFastPulses;
        public byte* HeldBufferKinds;
        public int* SlowCount;
        public int* SpillCount;
        public int* HeldSeen;
        public int* TickOrderVersion;
        public int* SampledCount;
        public int* PrevSampledCount;
        public KindWorkerCache* WorkerCache;
        public UnsafeUntypedBufferAccessor* WorkerBuffers;
        public EnabledMask* WorkerMasks;
        public KindDense* Dense;
        public ulong* PairBloom;
        public ulong SpanKinds;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong PairBit(ulong pairKey) => 1ul << (int)(pairKey >> 58);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool LacksPairs(ushort timeline, ulong pairs) => PairBloom != null && (uint)(timeline - 1) < (uint)TimelineCount && (PairBloom[timeline - 1] & pairs) == 0;

        public bool IsCreated => Timelines != null || KindCount == 0 && Closure != null;

        public global::Tl.LibraryKind* Kind(int kind) => Sec.Kinds + kind;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool NeedsBufferScan(int kind) => (uint)kind >= (uint)KindCount || SlowCount == null || HeldBufferKinds == null || TickFastPulses == null || SlowCount[kind] != 0 || HeldBufferKinds[kind] != 0 || (*TickFastPulses >> kind & 1) == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool NeedsSpanValues(int kind) => (uint)kind < (uint)KindCount && Values != null && Values[kind].Data == null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool NeedsPulseValues(int kind) => (uint)kind < (uint)KindCount && PulseValuesTable != null && PulseValuesTable[kind].Data == null && PulseValuesTable[kind].Cells >= 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PushPulse(int kind, int threadIndex, in ArchetypeChunk chunk, Entity entity, int row, int cell)
        {
            var pv = PulseValuesTable + kind;
            if (pv->Start[cell] == pv->Start[cell + 1]) return;
            if ((uint)threadIndex >= (uint)ThreadCount)
                throw new InvalidOperationException("Tl: worker thread index exceeded KindIndex.ThreadCount.");
            PulseLanes[kind * ThreadCount + threadIndex].Push(in chunk, entity, row, cell);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RewindPulse(int kind, int threadIndex, Entity entity)
        {
            if ((uint)threadIndex >= (uint)ThreadCount)
                throw new InvalidOperationException("Tl: worker thread index exceeded KindIndex.ThreadCount.");
            PulseLanes[kind * ThreadCount + threadIndex].Rewind(entity);
        }

        public bool HasForeign(int kind)
        {
            for (var i = 0; i < OwnerCount; i++)
                if (OwnerKinds[i] == kind) return true;
            return false;
        }

        public global::Tl.KindTables Tables => new global::Tl.KindTables
        {
            Timelines = Timelines,
            TimelineCount = TimelineCount,
            Sec = Sec,
            KindCount = KindCount,
            Closure = Closure,
            Pairs = Pairs,
            PairStart = PairStart,
            SpanMeta = SpanMeta,
            PulseBase = PulseBase,
            PulseBits = PulseBits,
            PulseMasks = PulseMasks,
            FastPulseMask = TickFastPulses != null ? *TickFastPulses : 0ul,
        };

        public global::Tl.LibraryStep* StepOf(ushort timeline, int window, ushort tick)
        {
            var t = timeline - 1;
            if ((uint)t >= (uint)TimelineCount) return null;
            var g = WindowBase[t] + window;
            var start = StepStart[g];
            if (start < 0) return null;
            var tl = Timelines + t;
            var stage = global::Tl.LibraryViewReader.StageAt(tl, tick);
            if (stage == null) return null;
            var s = (int)(stage - tl->Stages) - FirstStage[g];
            return (uint)s < (uint)StageCount[g] ? StepTable[start + s] : null;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Tl.LibraryStep* StepInWindow(ushort timeline, int window, ushort tick)
        {
            var t = timeline - 1;
            if ((uint)t >= (uint)TimelineCount) return null;
            var step = WindowStep[WindowBase[t] + window];
            if (step == null) return StepAcrossStages(timeline, window, tick);
            return tick < step->WindowStart || tick >= step->WindowEnd ? null : step;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        global::Tl.LibraryStep* StepAcrossStages(ushort timeline, int window, ushort tick)
        {
            var step = StepOf(timeline, window, tick);
            return step == null || tick < step->WindowStart || tick >= step->WindowEnd ? null : step;
        }

        public int SpanCount(ushort timeline, int tick, int kind)
        {
            var cell = Score.CellOf(timeline, tick, kind);
            return cell < 0 ? 0 : Score.Start[cell + 1] - Score.Start[cell];
        }

        public byte* MarkerOf(ushort timeline, int window)
        {
            var t = timeline - 1;
            if ((uint)t >= (uint)TimelineCount) return null;
            return MarkerValues[WindowBase[t] + window];
        }

        public uint MarkerPair(ushort timeline, int window)
        {
            var tk = Sec.TimelineOf(timeline);
            return tk == null ? uint.MaxValue : Sec.Windows(tk)[window].Pair;
        }

        public int OwnerOf(int kindId, int key)
        {
            var kind = KindOf(kindId);
            if (kind < 0) return -1;
            for (var i = 0; i < OwnerCount; i++)
                if (OwnerKinds[i] == kind && OwnerKeys[i] == key)
                    return OwnerIds[i];
            return -1;
        }

        void Verify(int kind, in KindDescriptor descriptor, KindRegistry registry, List<(int Kind, int Key, int Owner)> owners)
        {
            var name = Sec.NameOf(kind);
            var row = Sec.Kinds[kind];
            if (row.ValueBytes != (uint)descriptor.ValueBytes)
                throw new InvalidOperationException("Kind '" + name + "' is " + descriptor.ValueBytes + " bytes in code but " + row.ValueBytes + " in the loaded library; the timelines were baked for other code, let the SubScene rebake after the compile.");
            var size = UnsafeUtility.SizeOf(descriptor.Entry);
            if (size != descriptor.EntryBytes || descriptor.EntryBytes < row.EntryBytes || descriptor.EntryBytes < 8 + ((descriptor.ValueBytes + 3) & ~3))
                throw new InvalidOperationException("Kind '" + name + "' entry is " + size + " bytes in code but " + row.EntryBytes + " in the loaded library; the timelines were baked for other code, let the SubScene rebake after the compile.");
            EntryStride[kind] = descriptor.EntryBytes;
            var code = new Dictionary<string, ushort>(StringComparer.Ordinal);
            var all = new List<string>();
            Add(code, all, descriptor.Signature, 0);
            var owner = 1;
            for (var i = 0; i < registry.Feeds.Count; i++)
            {
                if (registry.Feeds[i].Name != name) continue;
                owners.Add((kind, registry.Feeds[i].Key, owner));
                Add(code, all, registry.Feeds[i].Signature, owner++);
            }
            var have = new System.Text.StringBuilder();
            var matched = 0;
            var differs = false;
            for (var f = 0; f < Sec.FeederCount; f++)
            {
                if (Sec.Feeders[f].Kind != kind) continue;
                var line = Sec.Text(Sec.Feeders[f].ConsumerOffset, Sec.Feeders[f].ConsumerLength) + "." + Sec.Text(Sec.Feeders[f].MethodOffset, Sec.Feeders[f].MethodLength) + ":" + Sec.Feeders[f].Trigger;
                if (have.Length > 0) have.Append(' ');
                have.Append(line);
                if (code.TryGetValue(line, out var route))
                {
                    Routes[f] = route;
                    matched++;
                }
                else differs = true;
            }
            if (differs || matched != all.Count)
                throw new InvalidOperationException("Kind '" + name + "' feeders in the loaded library differ from the compiled game; the timelines were baked for other code, let the SubScene rebake after the compile.\nlibrary: " + have + "\ncode:    " + string.Join(" ", all));
        }

        static void Add(Dictionary<string, ushort> code, List<string> all, string signature, int owner)
        {
            if (string.IsNullOrEmpty(signature)) return;
            var lines = signature.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                code[lines[i]] = (ushort)((owner << 8) | i);
                all.Add(lines[i]);
            }
        }
        public static int IdOf(string name)
        {
            var hash = 2166136261u;
            foreach (var c in name)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return unchecked((int)hash);
        }

        public int KindOf(int id)
        {
            int low = 0, high = IdCount - 1;
            while (low <= high)
            {
                var mid = (low + high) >> 1;
                var key = IdKeys[mid];
                if (key == id) return IdKinds[mid];
                if (key < id) low = mid + 1;
                else high = mid - 1;
            }
            return -1;
        }

        public void Bind(KindRegistry registry)
        {
            EntryTypes = Zeroed<ComponentType>(Math.Max(1, KindCount));
            ActiveTypes = Zeroed<ComponentType>(Math.Max(1, KindCount));
            IdKeys = Zeroed<int>(Math.Max(1, KindCount));
            IdKinds = Zeroed<int>(Math.Max(1, KindCount));
            EntryStride = Zeroed<int>(Math.Max(1, KindCount));
            Routes = Zeroed<ushort>(Math.Max(1, KindCount == 0 ? 0 : Sec.FeederCount));
            var owners = new List<(int Kind, int Key, int Owner)>();
            IdCount = 0;
            Bound = 0;
            var ids = new SortedDictionary<int, int>();
            for (var k = 0; k < KindCount; k++)
            {
                var name = Sec.NameOf(k);
                var id = IdOf(name);
                if (ids.TryGetValue(id, out var other))
                    throw new InvalidOperationException("Kinds '" + Sec.NameOf(other) + "' and '" + name + "' have the same kind id " + id + "; rename one of the kinds.");
                ids.Add(id, k);
                var r = registry.IndexOf(name);
                if (r < 0) continue;
                var d = registry[r];
                Verify(k, in d, registry, owners);
                EntryTypes[k] = ComponentType.ReadWrite(d.Entry);
                ActiveTypes[k] = ComponentType.ReadWrite(d.Active);
                Bound |= 1ul << k;
            }
            var grow = new List<ComponentType>();
            GrowStart = Zeroed<int>(KindCount + 2);
            for (var k = 0; k < KindCount; k++)
            {
                GrowStart[k] = grow.Count;
                if ((Bound & (1ul << k)) == 0) continue;
                var d = registry[registry.IndexOf(Sec.NameOf(k))];
                grow.Add(ComponentType.ReadWrite(d.Entry));
                grow.Add(ComponentType.ReadWrite(d.Active));
                grow.Add(ComponentType.ReadWrite(d.Enabled));
                foreach (var column in d.Columns)
                    grow.Add(ComponentType.ReadWrite(column));
            }
            GrowStart[KindCount] = grow.Count;
            foreach (var buffer in registry.EventBuffers)
                grow.Add(ComponentType.ReadWrite(buffer));
            GrowStart[KindCount + 1] = grow.Count;
            GrowTypes = Zeroed<ComponentType>(Math.Max(1, grow.Count));
            for (var i = 0; i < grow.Count; i++)
                GrowTypes[i] = grow[i];
            OwnerCount = owners.Count;
            OwnerKeys = Zeroed<int>(Math.Max(1, owners.Count));
            OwnerKinds = Zeroed<int>(Math.Max(1, owners.Count));
            OwnerIds = Zeroed<int>(Math.Max(1, owners.Count));
            for (var i = 0; i < owners.Count; i++)
            {
                OwnerKeys[i] = owners[i].Key;
                OwnerKinds[i] = owners[i].Kind;
                OwnerIds[i] = owners[i].Owner;
            }
            foreach (var pair in ids)
            {
                if ((Bound & (1ul << pair.Value)) == 0) continue;
                IdKeys[IdCount] = pair.Key;
                IdKinds[IdCount] = pair.Value;
                IdCount++;
            }
        }

        public static KindIndex Build(in global::Tl.LibraryView view, global::Tl.TimelineWorld world)
        {
            var index = default(KindIndex);
            try
            {
                index.TimelineCount = view.TimelineCount;
                index.Timelines = view.Timelines;
                index.Sec = view.Kinds;
                index.KindCount = view.Kinds.IsEmpty ? 0 : view.Kinds.KindCount;
                var count = view.TimelineCount;
                index.Closure = Zeroed<ulong>(Math.Max(1, count));
                index.PairStart = Zeroed<int>(count + 2);
                index.WindowBase = Zeroed<int>(count + 1);
                var pairs = new List<uint>();
                for (var t = 0; t < count; t++)
                {
                    var at = t;
                    for (var hop = 0; hop <= count; hop++)
                    {
                        if (index.KindCount != 0) index.Closure[t] |= index.Sec.Timelines[at].KindMask;
                        if (view.Timelines[at].Next == 0) break;
                        at = view.Timelines[at].Next - 1;
                    }
                    index.PairStart[t] = pairs.Count;
                    if (index.KindCount == 0 || index.Sec.Timelines[t].KindMask == 0) continue;
                    var tk = index.Sec.Timelines + t;
                    var events = index.Sec.Events(tk);
                    var set = new SortedSet<uint>();
                    for (var n = 0u; n < tk->EventCount; n++)
                        if (events[n].Op != (byte)global::Tl.KindEventOp.Remove) set.Add(((uint)events[n].Window << 16) | events[n].Feeder);
                    pairs.AddRange(set);
                }
                index.PairStart[count] = pairs.Count;
                index.PairCount = pairs.Count;
                index.Pairs = Zeroed<uint>(Math.Max(1, pairs.Count));
                for (var n = 0; n < pairs.Count; n++) index.Pairs[n] = pairs[n];
                index.SpanMeta = Zeroed<uint>(Math.Max(1, count));
                index.PulseBase = Zeroed<int>(Math.Max(1, count));
                long pulseTicks = 0;
                for (var t = 0; t < count; t++)
                {
                    var dur = view.Timelines[t].Duration;
                    index.SpanMeta[t] = dur > 0 && dur <= 65536u ? (dur | (view.Timelines[t].Loop != 0 ? 0x8000_0000u : 0u)) : 0u;
                    index.PulseBase[t] = -1;
                    if (index.KindCount == 0 || index.Sec.Timelines[t].KindMask == 0 || dur == 0) continue;
                    var tk = index.Sec.Timelines + t;
                    var events = index.Sec.Events(tk);
                    var anyPulse = false;
                    for (var n = 0u; n < tk->EventCount && !anyPulse; n++)
                        anyPulse = events[n].Op >= (byte)global::Tl.KindEventOp.PulseStart;
                    if (anyPulse)
                    {
                        if (pulseTicks > int.MaxValue)
                            throw new InvalidOperationException("Tl: pulse mask table would exceed 2 GB.");
                        index.PulseBase[t] = (int)pulseTicks;
                        pulseTicks += dur;
                    }
                }
                if (pulseTicks > int.MaxValue / 2 || pulseTicks * 2L * sizeof(ulong) > int.MaxValue)
                    throw new InvalidOperationException("Tl: pulse mask table would exceed 2 GB.");
                index.PulseBits = Zeroed<byte>(Math.Max(1, (int)pulseTicks));
                index.PulseMasks = Zeroed<ulong>(Math.Max(1, (int)(pulseTicks * 2L)));
                for (var t = 0; t < count; t++)
                {
                    var pb = index.PulseBase[t];
                    if (pb < 0) continue;
                    var tk = index.Sec.Timelines + t;
                    var events = index.Sec.Events(tk);
                    var dur = (int)view.Timelines[t].Duration;
                    for (var tick = 0; tick < dur; tick++)
                    {
                        var eb = index.Sec.EventBitsAt(tk, tick);
                        byte b = 0;
                        for (var d = 0; d < 2; d++)
                        {
                            if ((eb & (1 << d)) == 0) continue;
                            ulong pm = 0;
                            var to = index.Sec.EventEnd(tk, tick, d);
                            for (var n = index.Sec.EventStart(tk, tick, d); n < to; n++)
                                if (events[n].Op >= (byte)global::Tl.KindEventOp.PulseStart)
                                    pm |= 1ul << index.Sec.Feeders[events[n].Feeder].Kind;
                            if (pm != 0)
                            {
                                b |= (byte)(1 << d);
                                index.PulseMasks[((pb + tick) << 1) + d] = pm;
                            }
                        }
                        index.PulseBits[pb + tick] = b;
                    }
                }
                var tables = index.Tables;
                var calm = Math.Max(1, global::Tl.KindCalm.Length(in tables));
                index.Calm = global::Tl.KindCalm.Build(in tables, Zeroed<byte>(calm), Zeroed<byte>(calm), Zeroed<int>(Math.Max(1, count)));
                BuildSteps(ref index, view, world);
                index.PairBloom = Zeroed<ulong>(Math.Max(1, count));
                for (var t = 0; t < count; t++)
                {
                    var tl = view.Timelines + t;
                    ulong bloom = 0;
                    for (var s = 0; s < tl->StageCount; s++)
                        for (var i = 0; i < tl->Stages[s].StepCount; i++)
                            bloom |= PairBit(tl->Stages[s].Steps[i].PairKey);
                    index.PairBloom[t] = bloom;
                    if (index.KindCount != 0) index.SpanKinds |= index.Sec.Timelines[t].KindMask;
                }
                var steps = new ScoreSteps { Index = index };
                index.Score = global::Tl.KindScore.Build(in tables, ref steps);
                index.Values = Zeroed<global::Tl.KindValues>(Math.Max(1, index.KindCount));
                index.PulseValuesTable = Zeroed<global::Tl.KindPulseValues>(Math.Max(1, index.KindCount));
                index.TablePulses = Zeroed<ulong>(1);
                index.ThreadCount = Math.Max(1, global::Unity.Jobs.LowLevel.Unsafe.JobsUtility.ThreadIndexCount);
                var laneCount = (long)index.KindCount * index.ThreadCount;
                if (laneCount > int.MaxValue || laneCount * UnsafeUtility.SizeOf<KindPulseLane>() > int.MaxValue)
                    throw new InvalidOperationException("Tl: pulse lane table would exceed 2 GB.");
                index.PulseLanes = Zeroed<KindPulseLane>(Math.Max(1, (int)laneCount));
                index.WorkerCache = Zeroed<KindWorkerCache>(Math.Max(1, index.ThreadCount));
                if (laneCount > 0 && (laneCount * UnsafeUtility.SizeOf<UnsafeUntypedBufferAccessor>() > int.MaxValue || laneCount * UnsafeUtility.SizeOf<EnabledMask>() > int.MaxValue))
                    throw new InvalidOperationException("Tl: worker scratch table would exceed 2 GB.");
                index.WorkerBuffers = Zeroed<UnsafeUntypedBufferAccessor>(Math.Max(1, (int)laneCount));
                index.WorkerMasks = Zeroed<EnabledMask>(Math.Max(1, (int)laneCount));
                index.TickFastPulses = Zeroed<ulong>(1);
                index.HeldBufferKinds = Zeroed<byte>(Math.Max(1, index.KindCount));
                for (var k = 0; k < index.KindCount; k++) index.HeldBufferKinds[k] = 1;
                index.SlowCount = Zeroed<int>(Math.Max(1, index.KindCount));
                index.SpillCount = Zeroed<int>(Math.Max(1, index.KindCount));
                index.HeldSeen = Zeroed<int>(Math.Max(1, index.KindCount));
                index.TickOrderVersion = Zeroed<int>(1);
                index.SampledCount = Zeroed<int>(1);
                index.PrevSampledCount = Zeroed<int>(1);
                *index.SampledCount = 1;
                *index.PrevSampledCount = 1;
                if (index.KindCount != 0) index.Dense = KindDense.Create(view.Timelines, count);
                return index;
            }
            catch
            {
                index.Dispose();
                throw;
            }
        }

        public void SpanValues<TFill>(int kind, ref TFill fill) where TFill : struct, global::Tl.IKindFill, IKindDenseFill
        {
            if ((uint)kind >= (uint)KindCount || Values[kind].Data != null) return;
            var views = Zeroed<global::Tl.KindValues>(64);
            var row = Zeroed<int>(Math.Max(1, TimelineCount));
            byte* data = null;
            var committed = false;
            try
            {
                ulong kinds;
                if (Dense != null && Dense->Fits)
                {
                    var b = KindDenseBuild.Of(in Score, Timelines, views, row, PulseBase, PulseBits, ref fill);
                    kinds = b.Kinds;
                    if (kinds != 0)
                    {
                        var bytes = (long)b.Rows * b.Stride * 2;
                        if (bytes > int.MaxValue)
                            throw new InvalidOperationException("Tl: a precombined kind table would exceed 2 GB.");
                        data = ZeroedBytes(Math.Max(1L, bytes), 64);
                        b.Fill(data, ref fill);
                        if (b.CellLane >= 0)
                        {
                            Dense->Table = data;
                            Dense->Stride = b.Stride;
                            Dense->CellLane = b.CellLane;
                            for (var k = 0; k < 64; k++) Dense->KeyLanes[k] = b.KeyOf(k);
                        }
                    }
                }
                else
                {
                    var b = global::Tl.KindBuild.Of(in Score, Timelines, views, row, true, ref fill);
                    kinds = b.Kinds;
                    if (kinds != 0)
                    {
                        var bytes = (long)b.Rows * b.Stride;
                        if (bytes > int.MaxValue)
                            throw new InvalidOperationException("Tl: a precombined kind table would exceed 2 GB.");
                        data = ZeroedBytes(Math.Max(1L, bytes), 64);
                        b.Fill(data, ref fill);
                    }
                }
                if (kinds != 0)
                {
                    for (var k = 0; k < KindCount; k++)
                        if ((kinds >> k & 1) != 0)
                            Values[k] = views[k];
                    if (Dense != null) Dense->TableEpoch++;
                    committed = true;
                }
            }
            finally
            {
                if (!committed)
                {
                    Free(row);
                    Free(data);
                }
                Free(views);
            }
        }

        public void PulseValues<T, TEval>(int kind, ref TEval eval) where T : unmanaged where TEval : struct, global::Tl.IKindSpanValues<T>
        {
            if ((uint)kind >= (uint)KindCount) return;
            var v = PulseValuesTable + kind;
            if (v->Data != null || v->Cells < 0) return;
            if (HasForeign(kind)) { v->Cells = -1; return; }
            long cellsLong = 0;
            for (var tl = 0; tl < TimelineCount; tl++)
                if (PulseBase[tl] >= 0)
                    cellsLong = Math.Max(cellsLong, ((long)PulseBase[tl] + Timelines[tl].Duration) * 2L);
            if (cellsLong > int.MaxValue - 1)
                throw new InvalidOperationException("Tl: precombined pulse table would exceed 2 GB.");
            var cells = (int)cellsLong;
            var start = Zeroed<int>(cells + 1);
            byte* data = null;
            var committed = false;
            try
            {
                var feeders = Sec.Feeders;
                var total = 0;
                var stride = UnsafeUtility.SizeOf<T>();
                var align = UnsafeUtility.AlignOf<T>();
                if (align < 16) align = 16;
                for (var pass = 0; pass < 2; pass++)
                {
                    if (pass == 1)
                    {
                        start[cells] = total;
                        var dataBytes = (long)Math.Max(1, total) * stride;
                        if (dataBytes > int.MaxValue)
                            throw new InvalidOperationException("Tl: precombined pulse table would exceed 2 GB.");
                        data = ZeroedBytes(dataBytes, align);
                    }
                    var cursor = 0;
                    for (var tl = 0; tl < TimelineCount; tl++)
                    {
                        var pb = PulseBase[tl];
                        if (pb < 0) continue;
                        var id = (ushort)(tl + 1);
                        var tk = Sec.Timelines + tl;
                        var events = Sec.Events(tk);
                        var dur = (int)Timelines[tl].Duration;
                        for (var tick = 0; tick < dur; tick++)
                        {
                            var bits = PulseBits[pb + tick];
                            for (var d = 0; d < 2; d++)
                            {
                                var cell = ((pb + tick) << 1) + d;
                                if (pass == 0) start[cell] = total;
                                if ((bits & (1 << d)) == 0 || (PulseMasks[cell] >> kind & 1) == 0) continue;
                                var to = Sec.EventEnd(tk, tick, d);
                                for (var n = Sec.EventStart(tk, tick, d); n < to; n++)
                                {
                                    var ev = events[n];
                                    if (ev.Op < (byte)global::Tl.KindEventOp.PulseStart) continue;
                                    var feeder = feeders + ev.Feeder;
                                    if (feeder->Kind != kind) continue;
                                    var route = Routes[ev.Feeder];
                                    if ((route >> 8) != 0) continue;
                                    var step = StepInWindow(id, ev.Window, (ushort)tick);
                                    if (step == null) continue;
                                    if (eval.Evaluate(route & 0xff, step, Timelines + tl, (ushort)tick, d == 1, out var val))
                                    {
                                        if (pass == 0)
                                        {
                                            if (total == int.MaxValue)
                                                throw new InvalidOperationException("Tl: precombined pulse table would exceed 2 GB.");
                                            total++;
                                        }
                                        else ((T*)data)[cursor++] = val;
                                    }
                                }
                            }
                        }
                    }
                }
                v->Start = start;
                v->Data = data;
                v->Stride = stride;
                v->Cells = cells;
                v->Bytes = (long)(cells + 1) * 4 + (long)total * stride;
                *TablePulses |= 1ul << kind;
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    Free(start);
                    Free(data);
                }
            }
        }

        static void BuildSteps(ref KindIndex index, in global::Tl.LibraryView view, global::Tl.TimelineWorld world)
        {
            var count = view.TimelineCount;
            var total = 0;
            for (var t = 0; t < count; t++)
            {
                index.WindowBase[t] = total;
                if (index.KindCount != 0 && index.Sec.Timelines[t].KindMask != 0) total += (int)index.Sec.Timelines[t].WindowCount;
            }
            index.WindowBase[count] = total;
            index.WindowTotal = total;
            index.FirstStage = Zeroed<int>(Math.Max(1, total));
            index.StageCount = Zeroed<int>(Math.Max(1, total));
            index.StepStart = Zeroed<int>(Math.Max(1, total));
            index.MarkerValues = (byte**)Zeroed<IntPtr>(Math.Max(1, total));
            var single = new List<(int window, int start)>();
            for (var g = 0; g < total; g++) index.StepStart[g] = -1;
            var table = new List<IntPtr>();
            for (var t = 0; t < count; t++)
            {
                if (index.KindCount == 0 || index.Sec.Timelines[t].KindMask == 0) continue;
                var tk = index.Sec.Timelines + t;
                var tl = view.Timelines + t;
                var windows = index.Sec.Windows(tk);
                for (var w = 0; w < tk->WindowCount; w++)
                {
                    var win = windows[w];
                    var g = index.WindowBase[t] + w;
                    if ((win.Flags & 1u) != 0)
                    {
                        if (world != null && global::Tl.MarkerTableRegistry.TryResolve(world, tl->Id, win.Pair, 1, out var value)) index.MarkerValues[g] = value;
                        continue;
                    }
                    int first = -1, last = -1;
                    for (var s = 0; s < tl->StageCount; s++)
                    {
                        if (tl->Stages[s].Start <= win.Start && win.Start < tl->Stages[s].End) first = s;
                        if (tl->Stages[s].Start <= win.End - 1 && win.End - 1 < tl->Stages[s].End) last = s;
                    }
                    if (first < 0 || last < first) continue;
                    index.StepStart[g] = table.Count;
                    index.FirstStage[g] = first;
                    index.StageCount[g] = last - first + 1;
                    if (first == last && EndsAscending(tl) && (first == 0 || tl->Stages[first - 1].End <= win.Start)) single.Add((g, table.Count));
                    for (var s = first; s <= last; s++)
                    {
                        var found = IntPtr.Zero;
                        for (var n = 0; n < tl->Stages[s].StepCount; n++)
                        {
                            var step = tl->Stages[s].Steps + n;
                            if (step->TrackIndex == win.Track && step->Pair == win.Pair && step->WindowStart == win.Start && step->WindowEnd == win.End) { found = (IntPtr)step; break; }
                        }
                        table.Add(found);
                    }
                }
            }
            index.StepTableLength = table.Count;
            index.StepTable = (global::Tl.LibraryStep**)Zeroed<IntPtr>(Math.Max(1, table.Count));
            for (var i = 0; i < table.Count; i++) index.StepTable[i] = (global::Tl.LibraryStep*)table[i];
            index.WindowStep = (global::Tl.LibraryStep**)Zeroed<IntPtr>(Math.Max(1, total));
            foreach (var (window, start) in single) index.WindowStep[window] = index.StepTable[start];
        }

        struct ScoreSteps : global::Tl.IKindSteps
        {
            public KindIndex Index;

            public global::Tl.LibraryStep* StepOf(ushort timeline, int window, ushort tick) => Index.StepInWindow(timeline, window, tick);
        }

        static bool EndsAscending(global::Tl.LibraryTimeline* tl)
        {
            for (var s = 1; s < tl->StageCount; s++)
                if (tl->Stages[s].End < tl->Stages[s - 1].End) return false;
            return true;
        }

        public void Dispose()
        {
            Free(PairBloom);
            Free(Closure);
            Free(Pairs);
            Free(PairStart);
            Free(WindowBase);
            Free(FirstStage);
            Free(StageCount);
            Free(StepStart);
            Free(StepTable);
            Free(WindowStep);
            Free(MarkerValues);
            Free(EntryTypes);
            Free(ActiveTypes);
            Free(GrowTypes);
            Free(GrowStart);
            Free(IdKeys);
            Free(IdKinds);
            Free(Routes);
            Free(EntryStride);
            Free(OwnerKeys);
            Free(OwnerKinds);
            Free(OwnerIds);
            Free(Calm.Forward);
            Free(Calm.Backward);
            Free(Calm.Start);
            for (var k = 0; Values != null && k < Math.Max(1, KindCount); k++)
                if (Values[k].Shift == 0)
                {
                    Free(Values[k].Row);
                    Free(Values[k].Data);
                }
            Free(Values);
            Free(SpanMeta);
            Free(PulseBase);
            Free(PulseBits);
            Free(PulseMasks);
            Free(TablePulses);
            if (PulseLanes != null)
            {
                var total = Math.Max(1, KindCount * ThreadCount);
                for (var i = 0; i < total; i++) Free(PulseLanes[i].Items);
                Free(PulseLanes);
            }
            Free(WorkerCache);
            Free(WorkerBuffers);
            Free(WorkerMasks);
            Free(TickFastPulses);
            Free(HeldBufferKinds);
            Free(SlowCount);
            Free(SpillCount);
            Free(HeldSeen);
            Free(TickOrderVersion);
            Free(SampledCount);
            Free(PrevSampledCount);
            for (var k = 0; PulseValuesTable != null && k < Math.Max(1, KindCount); k++)
            {
                Free(PulseValuesTable[k].Start);
                Free(PulseValuesTable[k].Data);
            }
            Free(PulseValuesTable);
            KindDense.Release(Dense);
            Score.Free();
            this = default;
        }

        static T* Zeroed<T>(int count) where T : unmanaged
        {
            var bytes = (long)count * UnsafeUtility.SizeOf<T>();
            var p = UnsafeUtility.Malloc(bytes, 16, Allocator.Persistent);
            UnsafeUtility.MemClear(p, bytes);
            return (T*)p;
        }

        static byte* ZeroedBytes(long bytes, int align)
        {
            var p = UnsafeUtility.Malloc(bytes, align, Allocator.Persistent);
            UnsafeUtility.MemClear(p, bytes);
            return (byte*)p;
        }

        static void Free(void* p)
        {
            if (p != null) UnsafeUtility.Free(p, Allocator.Persistent);
        }
    }
}
