using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class PuppetRegistry
    {
        private readonly Dictionary<string, PuppetController> puppets =
            new Dictionary<string, PuppetController>();

        // ── No-puppet heartbeat ──────────────────────────────────────────────────
        private float nextNoPuppetLogTime;
        private const float NoPuppetLogInterval = 10f;   // warn every 10 s if still empty

        public void Apply(RemotePlayerState state, Transform localPlayerRoot, PuppetFactory factory)
        {
            if (state == null || string.IsNullOrEmpty(state.playerId))
                return;

            PuppetController puppet;
            if (!puppets.TryGetValue(state.playerId, out puppet) || puppet == null)
            {
                DiagnosticLog.Info("Creating remote puppet for '" + state.playerId + "'.");
                puppet = factory.Create(state.playerId, localPlayerRoot);

                if (puppet == null)
                {
                    DiagnosticLog.Warning(
                        "No puppet found — factory.Create returned null for '" +
                        state.playerId + "'. Check that the visual source exists.");
                    return;
                }

                puppets[state.playerId] = puppet;

                DiagnosticLog.Info(
                    "Puppet registry now tracks " + puppets.Count +
                    " remote player(s): " + BuildPlayerList());
            }

            bool isSameScene = string.IsNullOrEmpty(state.sceneName) ||
                               state.sceneName == SceneManager.GetActiveScene().name;

            puppet.SetVisible(state.isVisible && isSameScene);
            puppet.ApplySnapshot(state);
        }

        public void Remove(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
                return;

            PuppetController puppet;
            if (!puppets.TryGetValue(playerId, out puppet))
                return;

            puppets.Remove(playerId);

            DiagnosticLog.Info(
                "Remote player '" + playerId + "' disconnected. " +
                puppets.Count + " puppet(s) remaining.");

            if (puppet != null)
                Object.Destroy(puppet.GameObject);
        }

        public void Tick()
        {
            // Per-puppet tick
            foreach (KeyValuePair<string, PuppetController> pair in puppets)
            {
                if (pair.Value != null)
                    pair.Value.Tick();
            }

            // Rate-limited "no puppet" warning
            if (puppets.Count == 0 && Time.unscaledTime >= nextNoPuppetLogTime)
            {
                nextNoPuppetLogTime = Time.unscaledTime + NoPuppetLogInterval;
                DiagnosticLog.Warning(
                    "No puppet found — no remote players are connected " +
                    "(or server unreachable). Waiting for incoming state...");
            }
        }

        public void Clear()
        {
            foreach (KeyValuePair<string, PuppetController> pair in puppets)
            {
                if (pair.Value != null)
                    Object.Destroy(pair.Value.GameObject);
            }

            puppets.Clear();
            DiagnosticLog.Info("Puppet registry cleared.");
        }

        private string BuildPlayerList()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string id in puppets.Keys)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append("'");
                sb.Append(id);
                sb.Append("'");
            }
            return sb.ToString();
        }
    }
}
