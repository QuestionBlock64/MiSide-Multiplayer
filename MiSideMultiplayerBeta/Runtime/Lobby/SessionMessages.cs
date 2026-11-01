using System;
using System.Collections.Generic;

namespace MiSideMultiplayer.Runtime.Lobby
{
    public static class SessionEvents
    {
        public const string PlayerHello        = "miside.session.hello";
        public const string PlayerReady        = "miside.session.ready";
        public const string SceneSelected      = "miside.session.scene";
        public const string SessionStart       = "miside.session.start";
        public const string SceneLoaded        = "miside.session.sceneloaded";
        public const string SessionInfo        = "miside.session.info";

        public const string PlayerJoined       = "miside.session.player.joined";
        public const string PlayerLeft         = "miside.session.player.left";
        public const string HostLeft           = "miside.session.host.left";

        public const string LoadingClientReady   = "miside.loading.clientReady";
        public const string LoadingStageFinished = "miside.loading.stageFinished";
        public const string LoadingPhase         = "miside.loading.phase";
        public const string LoadingDone          = "miside.loading.done";

        public const string ColorAssign          = "miside.session.colorassign";   // NEW
    }

    [Serializable]
    public sealed class SessionHelloMessage
    {
        public string playerId;
        public string displayName;
        public string pluginVersion;
        public bool   isHost;
        public bool   isReady;
        public string selectedScene;
    }

    [Serializable]
    public sealed class SessionReadyMessage
    {
        public string playerId;
        public bool   isReady;
    }

    [Serializable]
    public sealed class SessionSceneSelectedMessage
    {
        public string sceneName;
        public string selectedByPlayerId;
    }

    [Serializable]
    public sealed class SessionStartMessage
    {
        public string sceneName;
        public string startedByPlayerId;
        public int    expectedPlayerCount;
    }

    [Serializable]
    public sealed class SessionSceneLoadedMessage
    {
        public string playerId;
        public string sceneName;
    }

    [Serializable]
    public sealed class SessionInfoMessage
    {
        public string            sessionName;
        public string            hostPlayerId;
        public int               maxPlayers;
        public SessionVisibility visibility;
        public string            code;
    }

    [Serializable]
    public sealed class SessionPlayerJoinedNotice
    {
        public string type;
        public string roomId;
        public string playerId;
        public string playerName;
    }

    [Serializable]
    public sealed class SessionPlayerLeftNotice
    {
        public string type;
        public string roomId;
        public string playerId;
        public string playerName;
    }

    [Serializable]
    public sealed class SessionHostLeftNotice
    {
        public string type;
        public string roomId;
        public string playerId;
        public string playerName;
    }

    // NEW — host → specific joiner: "your joinOrder is N."
    [Serializable]
    public sealed class SessionColorAssignMessage
    {
        public string playerId;
        public int    joinOrder;
    }


    [Serializable]
    public sealed class DirectorySessionEntry
    {
        public string            sessionId;
        public string            sessionName;
        public string            hostDisplayName;
        public int               playerCount;
        public int               maxPlayers;
        public bool              inGame;
        public SessionVisibility visibility;
        // Client-local metadata identifying the relay shard that returned this
        // row. It is not sent over the server protocol.
        public int               relayEndpointIndex = -1;
    }

    [Serializable]
    public sealed class DirectoryListResponse
    {
        public List<DirectorySessionEntry> sessions;
    }

    [Serializable]
    public sealed class DirectoryJoinByCodeRequest
    {
        public string code;
    }

    [Serializable]
    public sealed class DirectoryJoinByCodeResponse
    {
        public bool   found;
        public string sessionId;
        public string hostAddress;
        public int    hostPort;
        public SessionVisibility visibility;
        // Client-local source server; private-code joins must reconnect to
        // the same relay that resolved the lobby.
        public int    relayEndpointIndex = -1;
    }
}
