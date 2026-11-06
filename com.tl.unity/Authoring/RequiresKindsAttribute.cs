using System;

namespace Tl.Unity
{
    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public sealed class RequiresKindsAttribute : Attribute
    {
        public RequiresKindsAttribute(params Type[] kinds)
        {
            Kinds = kinds ?? Array.Empty<Type>();
        }

        public Type[] Kinds { get; }
    }
}
