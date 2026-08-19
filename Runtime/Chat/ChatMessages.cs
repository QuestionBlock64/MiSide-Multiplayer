using System;

namespace MiSideMultiplayer
{
    [Serializable]
    public sealed class ChatMessagePayload
    {
        public string senderId;
        public string displayName;
        public string sceneName;
        public NetVector3 position;
        public string text;
    }

    [Serializable]
    public sealed class ChatSystemMessagePayload
    {
        public string text;
        public string color;
        public string sceneName;
    }

    [Serializable]
    public sealed class ServerResponsePayload
    {
        public string text;
        public string color;
    }
}
