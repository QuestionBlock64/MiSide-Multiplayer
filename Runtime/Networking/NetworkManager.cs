using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class NetworkManager : IDisposable
    {
        // ── State ────────────────────────────────────────────────────────────────
        private RpcDispatcher rpcDispatcher;
        private PuppetRegistry puppetRegistry;
        private PuppetFactory puppetFactory;
        private Transform localPlayerRoot;
        private string localPlayerId;
        private string[] visualRootCandidates;
        private string activeSceneName;
        private float nextBindWarningTime;
        private float nextDiagnosticTime;
        private bool boundSuccessfully;
        private readonly HashSet<string> seenRemotePlayers = new HashSet<string>();

        public Transform LocalPlayerRoot  => localPlayerRoot;
        public string    LocalPlayerId    => localPlayerId;

        // ── Construction ─────────────────────────────────────────────────────────
        public NetworkManager(Transform runtimeRoot)
        {
            puppetRegistry  = new PuppetRegistry();
            activeSceneName = SceneManager.GetActiveScene().name;

            GameObject puppetRoot = new GameObject("RemotePuppets");
            puppetRoot.transform.SetParent(runtimeRoot, false);
            puppetFactory = new PuppetFactory(puppetRoot.transform);
        }

        public void Configure(
            RpcDispatcher dispatcher,
            string[]      visualCandidates,
            string[]      playerCandidates,   // kept for API compat, ignored by binding
            string        configuredLocalPlayerId)
        {
            rpcDispatcher       = dispatcher;
            visualRootCandidates = visualCandidates ?? new string[0];
            localPlayerId       = configuredLocalPlayerId;

            puppetFactory.SetVisualRootCandidates(visualRootCandidates);
            Subscribe();

            DiagnosticLog.Info(
                "NetworkManager configured. Local player will be pinned to " +
                LocalPlayerLocator.HardcodedPlayerPathDisplay + " exclusively.");
        }

        public void SetLocalPlayer(Transform playerRoot)
        {
            localPlayerRoot = playerRoot;
        }

        // ── Core binding: always GameController\Player\Person ────────────────────
        public bool TryBindLocalPlayer()
        {
            // Already bound and still valid?
            if (localPlayerRoot != null && localPlayerRoot.gameObject != null)
                return true;

            // Reset bound flag if root went away.
            if (localPlayerRoot == null && boundSuccessfully)
            {
                boundSuccessfully = false;
                DiagnosticLog.Warning(
                    "Previously bound " + LocalPlayerLocator.HardcodedPlayerPathDisplay +
                    " is gone (scene change?). Will re-bind on next tick.");
            }

            // Try the one and only hard-coded path.
            Transform found = LocalPlayerLocator.FindHardcodedPlayerPath();

            if (found != null)
            {
                localPlayerRoot  = found;
                boundSuccessfully = true;
                DiagnosticLog.Info(
                    "Player Bound to " + LocalPlayerLocator.HardcodedPlayerPathDisplay +
                    "  (scene: " + SceneManager.GetActiveScene().name + ")");
                return true;
            }

            // ── Couldn't find it — rate-limited warning + diagnostics ──────────
            if (Time.unscaledTime >= nextBindWarningTime)
            {
                nextBindWarningTime = Time.unscaledTime + 5f;

                DiagnosticLog.Warning(
                    "Could not find " + LocalPlayerLocator.HardcodedPlayerPathDisplay +
                    " — check if the scene has it.");

                // Run diagnostics only every 15 s to avoid spam.
                if (Time.unscaledTime >= nextDiagnosticTime)
                {
                    nextDiagnosticTime = Time.unscaledTime + 15f;
                    RunBindDiagnostics();
                }
            }

            return false;
        }

        // ── Scene-change tick ────────────────────────────────────────────────────
        public void Tick()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != activeSceneName)
            {
                activeSceneName   = sceneName;
                localPlayerRoot   = null;
                boundSuccessfully = false;

                DiagnosticLog.Info(
                    "Scene changed to '" + sceneName +
                    "'. Player binding cleared — will re-search for " +
                    LocalPlayerLocator.HardcodedPlayerPathDisplay + ".");
            }

            puppetRegistry.Tick();
        }

        public void Dispose()
        {
            Unsubscribe();
            puppetRegistry.Clear();
        }

        // ── Remote-player events ─────────────────────────────────────────────────
        private void Subscribe()
        {
            if (rpcDispatcher == null)
                return;

            rpcDispatcher.RemoteStateReceived -= OnRemoteStateReceived;
            rpcDispatcher.RemotePlayerLeft    -= OnRemotePlayerLeft;
            rpcDispatcher.RemoteStateReceived += OnRemoteStateReceived;
            rpcDispatcher.RemotePlayerLeft    += OnRemotePlayerLeft;
        }

        private void Unsubscribe()
        {
            if (rpcDispatcher == null)
                return;

            rpcDispatcher.RemoteStateReceived -= OnRemoteStateReceived;
            rpcDispatcher.RemotePlayerLeft    -= OnRemotePlayerLeft;
        }

        private void OnRemoteStateReceived(RemotePlayerState state)
        {
            if (state == null || string.IsNullOrEmpty(state.playerId))
                return;

            if (!string.IsNullOrEmpty(localPlayerId) && state.playerId == localPlayerId)
            {
                DiagnosticLog.Warning(
                    "Ignored remote state with the same LocalPlayerId '" + localPlayerId +
                    "'. Change Identity.LocalPlayerId when testing multiple clients on one PC.");
                return;
            }

            if (!seenRemotePlayers.Contains(state.playerId))
            {
                seenRemotePlayers.Add(state.playerId);
                DiagnosticLog.Info(
                    "First remote state from '" + state.playerId +
                    "' in scene '" + state.sceneName + "'.");
            }

            if (!TryBindLocalPlayer())
            {
                DiagnosticLog.Warning(
                    "Cannot create puppet yet: " +
                    LocalPlayerLocator.HardcodedPlayerPathDisplay +
                    " not found in current scene.");
                return;
            }

            puppetRegistry.Apply(state, localPlayerRoot, puppetFactory);
        }

        private void OnRemotePlayerLeft(string playerId)
        {
            puppetRegistry.Remove(playerId);
        }

        // ── Diagnostics (skinnedfind + find *) ───────────────────────────────────
        private static void RunBindDiagnostics()
        {
            DiagnosticLog.Info("─── Running bind diagnostics ─────────────────────────────");

            // skinnedfind
            try
            {
                string skinnedReport = LocalPlayerLocator.RunSkinnedFind();
                DiagnosticLog.Info(skinnedReport);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[skinnedfind] failed: " + ex.Message);
            }

            // find *
            try
            {
                string findReport = LocalPlayerLocator.RunFindAll();
                DiagnosticLog.Info(findReport);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[find *] failed: " + ex.Message);
            }

            DiagnosticLog.Info("──────────────────────────────────────────────────────────");
        }
    }
}
