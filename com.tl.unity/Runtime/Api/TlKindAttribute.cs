using System;

namespace Tl
{
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class TlKindAttribute : Attribute
    {
        public KindEntries Accepts { get; set; }
        public KindMode Mode { get; set; }
        public int Order { get; set; }
        public string Group { get; set; }
        public bool FoldsPulses { get; set; }
        public int Inline { get; set; }
        public int GamePulses { get; set; }
    }
}
