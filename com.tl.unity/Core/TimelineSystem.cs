using System;

namespace Tl;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class TimelineSystemAttribute : Attribute;

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class TimelineSystemMemberAttribute : Attribute
{
    public TimelineSystemMemberAttribute(Type system) => System = system;
    public Type System { get; }
}

[AttributeUsage(AttributeTargets.Enum)]
public sealed class TimelineSlotKeysAttribute : Attribute;
