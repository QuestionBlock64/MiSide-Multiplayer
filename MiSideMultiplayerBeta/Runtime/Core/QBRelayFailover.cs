namespace MiSideMultiplayer
{
    /// <summary>
    /// The equivalent multiplayer relay servers and connection-time health
    /// policy. A lobby belongs to one selected server; failover is only used
    /// before that server is selected, and players joining a lobby reconnect
    /// to the exact server that reported it.
    /// </summary>
    internal static class QBRelayFailover
    {
        private sealed class Endpoint
        {
            public readonly string Host;
            public readonly int Port;
            public readonly string Name;

            public Endpoint(string host, int port, string name)
            {
                Host = host;
                Port = port;
                Name = name;
            }
        }

        // Add future relay servers here. The list has no primary/secondary
        // roles: a client selects a starting server and checks the others in
        // sequence if it does not receive a healthy heartbeat.
        private static readonly Endpoint[] Endpoints =
        {
            new Endpoint("78.154.103.17", 16289, "Server 1"),
            new Endpoint("78.154.103.41", 12434, "Server 2"),
            new Endpoint("78.154.103.15", 16585, "Server 3"),
            new Endpoint("78.154.103.17", 16295, "Server 4"),
            new Endpoint("78.154.103.14", 16715, "Server 5")
        };

        // Only used as initial defaults in the deprecated endpoint config.
        // Runtime session connections use the full pool below.
        public static string InitialHost { get { return GetHost(0); } }
        public static int InitialPort { get { return GetPort(0); } }

        // The relay heartbeat interval is two seconds. Six seconds gives the
        // server room for a couple of delayed beats before failing over.
        public const float HandshakeTimeoutSeconds = 6f;

        public static int EndpointCount { get { return Endpoints.Length; } }

        public static string GetHost(int index)
        {
            return Endpoints[NormalizeIndex(index)].Host;
        }

        public static int GetPort(int index)
        {
            return Endpoints[NormalizeIndex(index)].Port;
        }

        public static string GetName(int index)
        {
            return Endpoints[NormalizeIndex(index)].Name;
        }

        public static int GetStableStartIndex(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return 0;
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < playerId.Length; i++)
                {
                    hash ^= char.ToUpperInvariant(playerId[i]);
                    hash *= 16777619;
                }
                return (int)(hash % (uint)EndpointCount);
            }
        }

        public static int GetIndex(string host, int port)
        {
            for (int i = 0; i < Endpoints.Length; i++)
                if (string.Equals(Endpoints[i].Host, host, System.StringComparison.OrdinalIgnoreCase) &&
                    Endpoints[i].Port == port)
                    return i;
            return -1;
        }

        private static int NormalizeIndex(int index)
        {
            if (EndpointCount == 0) return 0;
            int normalized = index % EndpointCount;
            return normalized < 0 ? normalized + EndpointCount : normalized;
        }
    }
}
