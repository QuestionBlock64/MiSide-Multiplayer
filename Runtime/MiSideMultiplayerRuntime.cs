using System;
using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed class MiSideMultiplayerRuntime : MonoBehaviour
    {
        public MiSideMultiplayerRuntime(IntPtr pointer) : base(pointer)
        {
        }

        private void Awake()
        {
            gameObject.name = "MiSideMultiplayer.Runtime";
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            MiSideMultiplayerPlugin.TickRuntime();
        }

        private void OnDestroy()
        {
            MiSideMultiplayerPlugin.DisposeRuntime();
        }
    }
}
