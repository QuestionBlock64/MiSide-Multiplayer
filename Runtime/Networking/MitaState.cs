using System;
using System.Collections.Generic;

namespace MiSideMultiplayer
{
    [Serializable]
    public sealed class MitaState
    {
        public string senderId;
        public string sceneName;
        public NetVector3    position;
        public NetQuaternion rotation;
        public Dictionary<string, BoneTransformData> Bones;
    }
}