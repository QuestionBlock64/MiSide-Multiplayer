using System;

namespace MiSideMultiplayer
{
    [Serializable]
    public sealed class SharedLifeMessage
    {
        public string senderId;
        public string sceneName;
        public string deathSource;
    }
}
