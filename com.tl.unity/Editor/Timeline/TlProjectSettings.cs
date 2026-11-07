using UnityEditor;
using UnityEngine;

namespace Tl.Unity.Editor
{
    [FilePath("ProjectSettings/TlSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class TlProjectSettings : ScriptableSingleton<TlProjectSettings>
    {
        public const int DefaultTicksPerSecond = 60;

        [SerializeField] int _ticksPerSecond = DefaultTicksPerSecond;

        public uint TicksPerSecond => (uint)(_ticksPerSecond > 0 ? _ticksPerSecond : DefaultTicksPerSecond);

        public void SetTicksPerSecond(int value)
        {
            var clamped = Mathf.Clamp(value, 1, 1000);
            if (clamped == _ticksPerSecond)
                return;
            _ticksPerSecond = clamped;
            Save(true);
        }
    }
}
