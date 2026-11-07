using System;

namespace Tl
{
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class TlClipAttribute : Attribute
    {
        public TlClipAttribute(string menu)
        {
            Menu = menu;
        }

        public string Menu { get; }
    }
}
