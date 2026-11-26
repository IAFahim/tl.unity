#if UNITY_EDITOR
using System;
using UnityEngine;

namespace Tl.Unity
{
    [AddComponentMenu("")]
    public sealed class TlFrameRecorderHost : MonoBehaviour
    {
        public Action OnDestroyed;

        void OnDestroy() => OnDestroyed?.Invoke();
    }
}
#endif
