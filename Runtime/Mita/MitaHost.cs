using UnityEngine;

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

        public static string HostPlayerId
        {
            get
            {
                if (_resolvedHostId.Length > 0) return _resolvedHostId;

                if (_configuredHostId.Length > 0 && IsPlayerKnown(_configuredHostId))
                {
                    _resolvedHostId = _configuredHostId;
                    DiagnosticLog.Info("MitaHost: using configured host '" + _resolvedHostId + "'.");
                    return _resolvedHostId;
                }

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
                string host = HostPlayerId;
                return host.Length > 0 && host == _localPlayerId;
            }
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