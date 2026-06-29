using System;
using UnityEngine;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Snapshot of Mita's transform and animator state, broadcast by the
    /// Mita authority player and applied by all others.
    /// Path in scene: World/Mita
    /// </summary>
    [Serializable]
    public sealed class MitaState
    {
        public string senderId;
        public string sceneName;
        public NetVector3    position;
        public NetQuaternion rotation;
        public float         speed;
        public string        currentAnimation;
        public AnimatorFloatParam[] floatParameters;
        public AnimatorBoolParam[]  boolParameters;
        public int tick;
    }
}
