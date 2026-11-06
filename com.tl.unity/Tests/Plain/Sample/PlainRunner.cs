using System;
using UnityEngine;

namespace PlainGame
{
    public sealed class PlainRunner : MonoBehaviour
    {
        public TextAsset Library;
        public Transform[] Targets;
        public float UnitsPerMeter = 100f;
        public bool Keyboard = true;

        PlainHost _host;
        Vector3[] _origin;
        bool _rewind;

        public event Action<int, int> CueRaised;

        public PlainHost Host => _host;
        public int Steps { get; private set; }

        void Awake()
        {
            var rows = Targets == null ? 0 : Targets.Length;
            if (Library == null || rows == 0)
                throw new InvalidOperationException("PlainRunner needs a Library asset and at least one target.");
            _host = new PlainHost(Library.bytes, rows);
            _origin = new Vector3[rows];
            for (var row = 0; row < rows; row++)
                _origin[row] = Targets[row].position;
            _host.Start(0, Timelines.Run);
        }

        public void StartTimeline(int row, ushort timeline) => _host.Start(row, timeline);

        public void StopTimeline(int row, ushort timeline) => _host.Stop(row, timeline);

        public void TogglePause(int row, ushort timeline)
        {
            var paused = _host.TryTick(row, timeline, out _) && !_host.IsPaused(row, timeline);
            _host.SetPaused(row, timeline, paused);
        }

        public void Rewind(bool value) => _rewind = value;

        void Update()
        {
            if (_host == null || !Keyboard)
                return;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.J)) _host.Start(0, Timelines.Jump);
            if (Input.GetKeyDown(KeyCode.B)) _host.Start(0, Timelines.SpeedBoost);
            if (Input.GetKeyDown(KeyCode.P)) _host.Start(0, Timelines.PotionHop);
            if (Input.GetKeyDown(KeyCode.W)) _host.Start(0, Timelines.Whistle);
            if (Input.GetKeyDown(KeyCode.S)) _host.Start(0, Timelines.Stomp);
            if (Input.GetKeyDown(KeyCode.R)) _host.Start(0, Timelines.Run);
            if (Input.GetKeyDown(KeyCode.X)) _host.StopAll(0);
            if (Input.GetKeyDown(KeyCode.Space)) TogglePause(0, Timelines.Run);
            _rewind = Input.GetKey(KeyCode.Backspace);
#endif
        }

        void FixedUpdate()
        {
            if (_host == null)
                return;
            _host.Step(!_rewind);
            Steps++;
            Apply();
            ReadCues();
        }

        void Apply()
        {
            for (var row = 0; row < Targets.Length; row++)
            {
                var motion = _host.Motion[row];
                Targets[row].position = _origin[row] + new Vector3(motion.X, motion.Y, 0f) / UnitsPerMeter;
            }
        }

        void ReadCues()
        {
            foreach (var cue in _host.DrainCues())
                CueRaised?.Invoke(cue.Entity.Index, cue.Value.Voice);
        }

        void OnDestroy()
        {
            if (_host == null)
                return;
            _host.Dispose();
            _host = null;
        }
    }
}
