using System;

namespace MiSideMultiplayer
{
    [Serializable]
    public sealed class WorldStoryObjectState
    {
        public string senderId;
        public string sceneName;
        public string path;
        public bool   isActive;
        public NetVector3 senderPosition;
    }
}