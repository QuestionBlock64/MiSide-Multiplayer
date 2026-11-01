using UnityEngine;
using UnityEngine.SceneManagement;
using MiSideMultiplayer.Runtime.Lobby;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Single, stable host for Mita's AI. Host runs Mita's local AI and
    /// broadcasts her state; non-host applies received state.
    /// </summary>
    public static class MitaHost
    {
        private static string _localPlayerId = "";
        private static string _configuredHostId = "";
        private static string _resolvedHostId = "";

        private static System.Reflection.FieldInfo _puppetPlayerIdField;

        public static void Configure(string localPlayerId, string configuredHostId)
        {
            _localPlayerId = localPlayerId ?? "";
            _configuredHostId = configuredHostId ?? "";
            _resolvedHostId = "";
        }

        public static string LocalPlayerId { get { return _localPlayerId; } }

        /// <summary>
        /// Scene 5 has local Mita AI on every peer. Mita commands are still
        /// broadcast, but the host must not puppet-lock the joiners' actors.
        /// </summary>
        public static bool IsScene5SharedMode
        {
            get
            {
                return string.Equals(SceneManager.GetActiveScene().name,
                                     "Scene 5 - StartHorror",
                                     System.StringComparison.Ordinal);
            }
        }

        public static string HostPlayerId
        {
            get
            {
                string sessionHostId;
                if (TryGetSessionHostId(out sessionHostId))
                {
                    _resolvedHostId = sessionHostId;
                    return _resolvedHostId;
                }

                if (_configuredHostId.Length > 0)
                {
                    _resolvedHostId = _configuredHostId;
                    return _resolvedHostId;
                }

                if (_resolvedHostId.Length > 0) return _resolvedHostId;

                string best = _localPlayerId;
                foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
                {
                    if (puppet == null || puppet.GameObject == null) continue;
                    string pid = PuppetPlayerId(puppet);
                    if (string.IsNullOrEmpty(pid)) continue;
                    if (string.CompareOrdinal(pid, best) < 0) best = pid;
                }

                if (best != _resolvedHostId)
                {
                    _resolvedHostId = best;
                    DiagnosticLog.Info("MitaHost: resolved host '" + _resolvedHostId +
                                       "' (local='" + _localPlayerId + "').");
                }
                return _resolvedHostId;
            }
        }

        public static bool IsHost
        {
            get
            {
                SessionController session = SessionController.Active;
                if (session != null) return session.IsHost;

                string host = HostPlayerId;
                return host.Length > 0 && host == _localPlayerId;
            }
        }

        private static bool TryGetSessionHostId(out string hostPlayerId)
        {
            hostPlayerId = "";
            SessionController session = SessionController.Active;
            if (session == null) return false;

            if (session.IsHost)
            {
                hostPlayerId = _localPlayerId;
                return hostPlayerId.Length > 0;
            }

            foreach (SessionPlayer player in session.Players.Values)
            {
                if (player == null || !player.isHost || string.IsNullOrWhiteSpace(player.playerId)) continue;
                hostPlayerId = player.playerId;
                return true;
            }

            return false;
        }

        public static Transform ResolveGazeTarget(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return null;

            if (playerId == _localPlayerId)
                return LocalPlayerLocator.FindHardcodedPlayerPath();

            foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
            {
                if (puppet == null || puppet.GameObject == null) continue;
                if (PuppetPlayerId(puppet) == playerId)
                    return puppet.GameObject.transform;
            }
            return null;
        }

        public static string ResolvePlayerIdForTransform(Transform t)
        {
            if (t == null) return null;

            Transform local = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (local != null && IsUnder(t, local)) return _localPlayerId;

            foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
            {
                if (puppet == null || puppet.GameObject == null) continue;
                if (IsUnder(t, puppet.GameObject.transform))
                    return PuppetPlayerId(puppet);
            }
            return null;
        }

        private static bool IsUnder(Transform t, Transform root)
        {
            while (t != null) { if (t == root) return true; t = t.parent; }
            return false;
        }

        private static bool IsPlayerKnown(string playerId)
        {
            if (playerId == _localPlayerId) return true;
            foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
            {
                if (puppet == null || puppet.GameObject == null) continue;
                if (PuppetPlayerId(puppet) == playerId) return true;
            }
            return false;
        }

        private static string PuppetPlayerId(PuppetController puppet)
        {
            if (_puppetPlayerIdField == null)
            {
                _puppetPlayerIdField = typeof(PuppetController).GetField(
                    "playerId",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
            }
            if (_puppetPlayerIdField == null) return null;
            try { return _puppetPlayerIdField.GetValue(puppet) as string; }
            catch { return null; }
        }
    }
}
