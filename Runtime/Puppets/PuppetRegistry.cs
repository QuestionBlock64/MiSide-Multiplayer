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

        // ── Static accessor for MitaController head tracking ──────────────────
        public static IEnumerable<PuppetController> AllPuppets
        {
            get
            {
                if (_instance == null)
                    return new PuppetController[0];
                return _instance.puppets.Values;
            }
        }

        private static PuppetRegistry _instance;

        public PuppetRegistry()
        {
            _instance = this;
        }

        // ── Apply incoming state ────────────────────────────────────────────────
        public void Apply(RemotePlayerState state, Transform localPlayerRoot,
                          PuppetFactory factory)
        {
            if (state == null || string.IsNullOrEmpty(state.playerId)) return;

            bool isSameScene = IsSameScene(state.sceneName);

            PuppetController puppet;
            bool exists = puppets.TryGetValue(state.playerId, out puppet);

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

            if (!exists || puppet == null)
            {
                DiagnosticLog.Info(
                    "Creating puppet for '" + state.playerId +
                    "' model='" + (state.customModelName ?? "None") + "'.");

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

        public void Remove(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            PuppetController puppet;
            if (!puppets.TryGetValue(playerId, out puppet)) return;
            puppets.Remove(playerId);

            BoneSync.ClearPending(playerId);

            DiagnosticLog.Info(
                "Puppet removed for '" + playerId + "'. " +
                puppets.Count + " remaining.");
            if (puppet != null && puppet.GameObject != null)
                UnityEngine.Object.Destroy(puppet.GameObject);
        }

        public GameObject GetPuppet(string playerId)
        {
            PuppetController controller;
            if (puppets.TryGetValue(playerId, out controller) && controller != null)
                return controller.GameObject;
            return null;
        }

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

        public void LateTick()
        {
            foreach (KeyValuePair<string, PuppetController> pair in puppets)
                if (pair.Value != null) pair.Value.LateTick();
        }

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