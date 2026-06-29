using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class PuppetRegistry
    {
        private readonly Dictionary<string, PuppetController> puppets =
            new Dictionary<string, PuppetController>();

        // ── "No puppet" heartbeat ──────────────────────────────────────────────
        private float nextNoPuppetLogTime;
        private const float NoPuppetLogInterval = 10f;

        // ── Apply incoming remote state ────────────────────────────────────────
        public void Apply(RemotePlayerState state, Transform localPlayerRoot, PuppetFactory factory)
        {
            if (state == null || string.IsNullOrEmpty(state.playerId))
                return;

            bool isSameScene = IsSameScene(state.sceneName);

            PuppetController puppet;
            bool exists = puppets.TryGetValue(state.playerId, out puppet);

            // ── Different scene: destroy puppet if it exists ───────────────────
            if (!isSameScene)
            {
                if (exists && puppet != null)
                {
                    DiagnosticLog.Info(
                        "Remote player '" + state.playerId +
                        "' moved to scene '" + state.sceneName +
                        "' — destroying their puppet.");
                    Remove(state.playerId);
                }
                return;   // Do not create a puppet for a player in a different scene.
            }

            // ── Same scene: create if needed ──────────────────────────────────
            if (!exists || puppet == null)
            {
                string dname = state.displayName ?? state.playerId;

                DiagnosticLog.Info(
                    "Creating puppet for '" + state.playerId + "' (" + dname + ")" +
                    "  scene=" + state.sceneName +
                    "  pos=(" + state.position.x.ToString("F2") + ", " +
                               state.position.y.ToString("F2") + ", " +
                               state.position.z.ToString("F2") + ")");

                puppet = factory.Create(state.playerId, dname, localPlayerRoot);

                if (puppet == null)
                {
                    DiagnosticLog.Warning(
                        "No puppet found — factory returned null for '" + state.playerId +
                        "'. Check that a valid visual source exists in the current scene.");
                    return;
                }

                puppets[state.playerId] = puppet;
                DiagnosticLog.Info(
                    "Puppet registry: " + puppets.Count + " puppet(s) active: " + BuildList());
            }

            puppet.SetVisible(state.isVisible);
            puppet.ApplySnapshot(state);
        }

        // ── Remove by ID ──────────────────────────────────────────────────────
        public void Remove(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
                return;

            PuppetController puppet;
            if (!puppets.TryGetValue(playerId, out puppet))
                return;

            puppets.Remove(playerId);
            DiagnosticLog.Info(
                "Puppet removed for '" + playerId + "'. " +
                puppets.Count + " puppet(s) remaining.");

            if (puppet != null && puppet.GameObject != null)
                UnityEngine.Object.Destroy(puppet.GameObject);
        }

        // ── Tick ──────────────────────────────────────────────────────────────
        public void Tick()
        {
            foreach (KeyValuePair<string, PuppetController> pair in puppets)
                if (pair.Value != null)
                    pair.Value.Tick();

            // Rate-limited "no puppet" warning
            if (puppets.Count == 0 && Time.unscaledTime >= nextNoPuppetLogTime)
            {
                nextNoPuppetLogTime = Time.unscaledTime + NoPuppetLogInterval;
                DiagnosticLog.Warning(
                    "No puppet found — no remote players are currently connected, " +
                    "or no one is in the same scene. Waiting for incoming state...");
            }
        }

        // ── LateTick (called from LateUpdate, after Animator runs) ────────────
        public void LateTick()
        {
            foreach (KeyValuePair<string, PuppetController> pair in puppets)
                if (pair.Value != null)
                    pair.Value.LateTick();
        }

        // ── Clear all ─────────────────────────────────────────────────────────
        public void Clear()
        {
            foreach (KeyValuePair<string, PuppetController> pair in puppets)
                if (pair.Value != null && pair.Value.GameObject != null)
                    UnityEngine.Object.Destroy(pair.Value.GameObject);

            int count = puppets.Count;
            puppets.Clear();

            if (count > 0)
                DiagnosticLog.Info("Puppet registry cleared (" + count + " puppet(s) destroyed).");
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static bool IsSameScene(string remoteName)
        {
            // Empty scene name = unknown; optimistic — let visibility handle it.
            if (string.IsNullOrEmpty(remoteName))
                return true;

            return remoteName == SceneManager.GetActiveScene().name;
        }

        private string BuildList()
        {
            StringBuilder sb = new StringBuilder();
            foreach (string id in puppets.Keys)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append('\''); sb.Append(id); sb.Append('\'');
            }
            return sb.ToString();
        }
    }
}
