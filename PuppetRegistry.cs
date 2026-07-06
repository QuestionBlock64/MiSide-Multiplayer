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

        private float nextNoPuppetLogTime;
        private const float NoPuppetLogInterval = 10f;

        // ── Apply incoming state ────────────────────────────────────────────────
        public void Apply(RemotePlayerState state, Transform localPlayerRoot,
                          PuppetFactory factory)
        {
            if (state == null || string.IsNullOrEmpty(state.playerId)) return;

            bool isSameScene = IsSameScene(state.sceneName);

            PuppetController puppet;
            bool exists = puppets.TryGetValue(state.playerId, out puppet);

            // ── Different scene → destroy puppet ───────────────────────────────
            if (!isSameScene)
            {
                if (exists && puppet != null)
                {
                    DiagnosticLog.Info(
                        "'" + state.playerId + "' moved to scene '" + state.sceneName +
                        "' — destroying puppet.");
                    Remove(state.playerId);
                }
                return;
            }

            // ── Same scene: create if needed ───────────────────────────────────
            if (!exists || puppet == null)
            {
                DiagnosticLog.Info(
                    "Creating puppet for '" + state.playerId +
                    "' model='" + (state.customModelName ?? "None") + "'.");

                // Pass the remote custom model name so PuppetFactory can attempt
                // to load it via ModelPuppet before falling back to visual clone.
                puppet = factory.Create(
                    state.playerId,
                    state.displayName ?? state.playerId,
                    localPlayerRoot,
                    state.customModelName);

                if (puppet == null)
                {
                    DiagnosticLog.Warning(
                        "No puppet created for '" + state.playerId + "'.");
                    return;
                }

                puppets[state.playerId] = puppet;
                DiagnosticLog.Info(
                    "Registry: " + puppets.Count + " puppet(s): " + BuildList());
            }

            puppet.SetVisible(state.isVisible);
            puppet.ApplySnapshot(state);
        }

        // ── Remove by ID ───────────────────────────────────────────────────────
        public void Remove(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            PuppetController puppet;
            if (!puppets.TryGetValue(playerId, out puppet)) return;
            puppets.Remove(playerId);
            DiagnosticLog.Info(
                "Puppet removed for '" + playerId + "'. " +
                puppets.Count + " remaining.");
            if (puppet != null && puppet.GameObject != null)
                UnityEngine.Object.Destroy(puppet.GameObject);
        }

        // ── Tick ───────────────────────────────────────────────────────────────
        public void Tick()
        {
            foreach (KeyValuePair<string, PuppetController> pair in puppets)
                if (pair.Value != null) pair.Value.Tick();

            if (puppets.Count == 0 && Time.unscaledTime >= nextNoPuppetLogTime)
            {
                nextNoPuppetLogTime = Time.unscaledTime + NoPuppetLogInterval;
                DiagnosticLog.Warning(
                    "No puppet found — no remote players in this scene " +
                    "(or not yet connected). Waiting...");
            }
        }

        // ── LateTick (called from LateUpdate, after Animator runs) ────────────
        public void LateTick()
        {
            foreach (KeyValuePair<string, PuppetController> pair in puppets)
                if (pair.Value != null) pair.Value.LateTick();
        }

        // ── Clear ──────────────────────────────────────────────────────────────
        public void Clear()
        {
            foreach (KeyValuePair<string, PuppetController> pair in puppets)
                if (pair.Value != null && pair.Value.GameObject != null)
                    UnityEngine.Object.Destroy(pair.Value.GameObject);

            int count = puppets.Count;
            puppets.Clear();
            if (count > 0)
                DiagnosticLog.Info(
                    "Puppet registry cleared (" + count + " destroyed).");
        }

        // ── Helpers ────────────────────────────────────────────────────────────
        private static bool IsSameScene(string remoteName)
        {
            if (string.IsNullOrEmpty(remoteName)) return true;
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