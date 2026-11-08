using System;
using Tl;

namespace PlainGame
{
    public sealed unsafe class PlainHost : IDisposable
    {
        readonly TimelineWorld _world;
        readonly PlaybackTable _players;
        readonly KindStore _kinds;
        readonly KindPulses<int> _damage;
        readonly KindPulses<int> _stat;
        Events<Cue> _cues;
        bool _disposed;

        public readonly int Rows;
        public readonly Motion[] Motion;
        public readonly WalkStat[] WalkStat;
        public readonly JumpPower[] JumpPower;
        public readonly Stamina[] Stamina;
        public readonly Health[] Health;

        public PlainHost(byte[] library, int rows, float walkStat = 1f, float jumpPower = 2f, int health = 1000)
        {
            if (library == null || library.Length == 0)
                throw new ArgumentException("The library bytes are empty.", nameof(library));
            Rows = rows;
            Motion = new Motion[rows];
            WalkStat = new WalkStat[rows];
            JumpPower = new JumpPower[rows];
            Stamina = new Stamina[rows];
            Health = new Health[rows];
            for (var row = 0; row < rows; row++)
            {
                WalkStat[row] = new WalkStat { Value = walkStat };
                JumpPower[row] = new JumpPower { Value = jumpPower };
                Health[row] = new Health { Value = health };
            }
            InstallGeneratedCode();
            _world = new TimelineWorld(library);
            _players = new PlaybackTable(rows);
            _kinds = new KindStore(_world, _players);
            _damage = _kinds.Pulses<int>("PlainGame.Damage");
            _stat = _kinds.Pulses<int>("PlainGame.StatChange");
            _cues = new Events<Cue>(16);
        }

        static void InstallGeneratedCode()
        {
            var mine = typeof(PlainHost).Assembly;
            foreach (var module in TlGenerated.Modules)
                if (module.GetType().Assembly == mine)
                    return;
            TlSystems.TlModule.Install();
        }

        public bool IsDisposed => _disposed;

        public long DroppedPulses => _kinds.DroppedPulses;

        public void Start(int row, ushort timeline, ushort at = 0) => _players.Start(row, timeline, at);

        public void Stop(int row, ushort timeline) => _players.Stop(row, timeline);

        public void StopChannel(int row, byte channel) => _players.StopChannel(row, channel);

        public void StopAll(int row) => _players.StopAll(row);

        public bool IsPlaying(int row, ushort timeline) => _players.IsPlaying(row, timeline);

        public int Playing(int row)
        {
            var count = 0;
            var slots = _players.View.SlotOf(row, 0);
            for (var slot = 0; slot < _players.CountOf(row); slot++)
                if (slots[slot].Timeline != 0 && (slots[slot].Flags & PlaybackFlags.Stopping) == 0)
                    count++;
            return count;
        }

        public bool TryTick(int row, ushort timeline, out int tick)
        {
            var slots = _players.View.SlotOf(row, 0);
            for (var slot = 0; slot < _players.CountOf(row); slot++)
                if (slots[slot].Timeline == timeline)
                {
                    tick = slots[slot].Tick;
                    return true;
                }
            tick = 0;
            return false;
        }

        public bool IsPaused(int row, ushort timeline)
        {
            var slots = _players.View.SlotOf(row, 0);
            for (var slot = 0; slot < _players.CountOf(row); slot++)
                if (slots[slot].Timeline == timeline)
                    return (slots[slot].Flags & PlaybackFlags.Paused) != 0;
            return false;
        }

        public void SetPaused(int row, ushort timeline, bool paused)
        {
            var slots = _players.View.SlotOf(row, 0);
            for (var slot = 0; slot < _players.CountOf(row); slot++)
                if (slots[slot].Timeline == timeline)
                    slots[slot].Flags = paused ? slots[slot].Flags | PlaybackFlags.Paused : slots[slot].Flags & ~PlaybackFlags.Paused;
        }

        public void Damage(int row, int amount) => _damage.To(row, amount);

        public void ChangeStamina(int row, int delta) => _stat.To(row, delta);

        public void Step(bool forward = true)
        {
            _world.Tick(_players, forward);
            new Movement { Motion = Motion, WalkStat = WalkStat, JumpPower = JumpPower, Stamina = Stamina, Cues = _cues.Writer }.Run(_world, _players, forward);
            new Vitals { Health = Health }.Run(_world, _players, forward);
        }

        public Span<EventRecord<Cue>> DrainCues() => _cues.Drain();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _cues.Dispose();
            _kinds.Dispose();
            _players.Dispose();
            _world.Dispose();
        }
    }
}
