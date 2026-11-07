using UnityEngine;

namespace Tl
{
    public static class TlBakeReport
    {
        public static string SecondValueMessage<T>(Object context)
        {
            var owner = context == null ? "this object" : context.name;
            return "Another clip on " + owner + " already sets " + typeof(T).Name + " with a different value. Only the first is kept on the object.";
        }

        public static void SecondValueIgnored<T>(Object context)
        {
            Debug.LogWarning(SecondValueMessage<T>(context), context);
        }
    }
}
