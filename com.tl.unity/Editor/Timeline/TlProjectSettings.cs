using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Tl.Unity.Editor
{
    [FilePath("ProjectSettings/TlSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class TlProjectSettings : ScriptableSingleton<TlProjectSettings>
    {
        public const int DefaultTicksPerSecond = 60;
        public const int ChannelCount = 15;

        [SerializeField] int _ticksPerSecond = DefaultTicksPerSecond;
        [SerializeField] string[] _channelNames = new string[0];

        public uint TicksPerSecond => (uint)(_ticksPerSecond > 0 ? _ticksPerSecond : DefaultTicksPerSecond);

        public void SetTicksPerSecond(int value)
        {
            var clamped = Mathf.Clamp(value, 1, 1000);
            if (clamped == _ticksPerSecond)
                return;
            _ticksPerSecond = clamped;
            Save(true);
        }

        public string ChannelName(int channel)
        {
            if (channel < 1 || channel > ChannelCount)
                return string.Empty;
            var custom = _channelNames != null && channel <= _channelNames.Length ? _channelNames[channel - 1] : null;
            return string.IsNullOrWhiteSpace(custom) ? channel.ToString(CultureInfo.InvariantCulture) : custom.Trim();
        }

        public bool HasCustomChannelName(int channel) => ChannelName(channel) != channel.ToString(CultureInfo.InvariantCulture);

        public bool TrySetChannelName(int channel, string name, out string error)
        {
            error = null;
            if (channel < 1 || channel > ChannelCount)
            {
                error = "Channels are numbered 1 to " + ChannelCount + ".";
                return false;
            }
            var wanted = string.IsNullOrWhiteSpace(name) ? channel.ToString(CultureInfo.InvariantCulture) : name.Trim();
            for (var other = 1; other <= ChannelCount; other++)
                if (other != channel && ChannelName(other) == wanted)
                {
                    error = "Channel " + other + " is already called " + wanted + ".";
                    return false;
                }
            if (_channelNames == null || _channelNames.Length < ChannelCount)
            {
                var grown = new string[ChannelCount];
                if (_channelNames != null)
                    System.Array.Copy(_channelNames, grown, _channelNames.Length);
                _channelNames = grown;
            }
            _channelNames[channel - 1] = wanted == channel.ToString(CultureInfo.InvariantCulture) ? string.Empty : wanted;
            Save(true);
            return true;
        }

        public void ResetChannelNames()
        {
            _channelNames = new string[0];
            Save(true);
        }
    }
}
