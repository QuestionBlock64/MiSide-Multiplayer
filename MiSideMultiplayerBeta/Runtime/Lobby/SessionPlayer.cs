using System;

namespace MiSideMultiplayer.Runtime.Lobby
{
    [Serializable]
    public sealed class SessionPlayer
    {
        public string playerId;
        public string displayName;
        public bool   isReady;
        public bool   isHost;
        public float  lastSeenUtc;
        public string pluginVersion;
        public int    joinOrder;

        public SessionPlayer() { }

        public SessionPlayer(string id, string name, string version)
        {
            playerId      = id;
            displayName   = string.IsNullOrEmpty(name) ? "Player" : name;
            pluginVersion = version ?? "?";
            isReady       = false;
            isHost        = false;
            lastSeenUtc   = NowUtcSeconds();
        }

        public static float NowUtcSeconds()
        {
            return (float)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
        }
    }
}