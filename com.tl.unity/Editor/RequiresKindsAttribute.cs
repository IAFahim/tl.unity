using System;

namespace Tl.Unity.Editor
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class RequiresKindsAttribute : Attribute
    {
        public RequiresKindsAttribute(params Type[] kinds)
        {
            Kinds = kinds ?? Array.Empty<Type>();
        }

        public Type[] Kinds { get; }
    }
}
