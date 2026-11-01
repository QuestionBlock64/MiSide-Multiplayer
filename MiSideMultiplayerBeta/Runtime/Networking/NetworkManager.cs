using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class NetworkManager : IDisposable
    {
        // ── Menu scene names that trigger a full puppet clear ──────────────────
        private static readonly string[] MenuSceneNames =
        {
            "Menu", "MainMenu", "MenuScene", "StartMenu", "TitleScreen", "Title"
        };

        // ── State ──────────────────────────────────────────────────────────────
        private RpcDispatcher   rpcDispatcher;
        private PuppetRegistry  puppetRegistry;
        private PuppetFactory   puppetFactory;
        private MitaController  mitaController;
        private Transform       localPlayerRoot;
        private string          localPlayerId;
        private string[]        visualRootCandidates;
        private string          activeSceneName;
        private float           nextBindWarningTime;
        private float           nextDiagnosticTime;
        private bool            boundSuccessfully;
        private float           nextLocalPosLogTime;
        private const float     LocalPosLogInterval = 5f;
        private readonly HashSet<string> seenRemotePlayers = new HashSet<string>();

        public Transform LocalPlayerRoot { get { return localPlayerRoot; } }
        public string    LocalPlayerId   { get { return localPlayerId;   } }

        // ── Construction ───────────────────────────────────────────────────────
        public NetworkManager(Transform runtimeRoot)
        {
            puppetRegistry  = new PuppetRegistry();
            activeSceneName = SceneManager.GetActiveScene().name;

            GameObject puppetRoot = new GameObject("RemotePuppets");
            puppetRoot.transform.SetParent(runtimeRoot, false);
            puppetFactory = new PuppetFactory(puppetRoot.transform);
        }

        // ── Configure (with MitaController) ───────────────────────────────────
        public void Configure(
            RpcDispatcher dispatcher,
            string[]      visualCandidates,
            string[]      playerCandidates,
            string        configuredLocalPlayerId,
            MitaController mita)
        {
            rpcDispatcher        = dispatcher;
            visualRootCandidates = visualCandidates ?? new string[0];
            localPlayerId        = configuredLocalPlayerId;
            mitaController       = mita;

            puppetFactory.SetVisualRootCandidates(visualRootCandidates);
            Subscribe();

            DiagnosticLog.Info(
                "NetworkManager configured." +
                "  LocalPlayerId='" + localPlayerId + "'" +
                "  Player path=" + LocalPlayerLocator.HardcodedPlayerPathDisplay);
        }

        // ── Legacy overload (no MitaController) ───────────────────────────────
        public void Configure(
            RpcDispatcher dispatcher,
            string[]      visualCandidates,
            string[]      playerCandidates,
            string        configuredLocalPlayerId)
        {
            Configure(dispatcher, visualCandidates, playerCandidates,
                      configuredLocalPlayerId, null);
        }

        public void SetLocalPlayer(Transform playerRoot)
        {
            localPlayerRoot = playerRoot;
        }

        // ── Hardcoded player binding ───────────────────────────────────────────
        public bool TryBindLocalPlayer()
        {
            // Already bound and valid?
            if (localPlayerRoot != null && localPlayerRoot.gameObject != null)
                return true;

            // Root disappeared (scene change?)
            if (localPlayerRoot == null && boundSuccessfully)
            {
                boundSuccessfully = false;
                DiagnosticLog.Warning(
                    "Previously bound " + LocalPlayerLocator.HardcodedPlayerPathDisplay +
                    " is gone (scene change?). Will re-bind.");
            }

            Transform found = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (found != null)
            {
                localPlayerRoot   = found;
                boundSuccessfully = true;

                Vector3 pos = found.position;
                DiagnosticLog.Info(
                    "Local player bound to " + LocalPlayerLocator.HardcodedPlayerPathDisplay +
                    "  scene=" + SceneManager.GetActiveScene().name +
                    "  pos=(" + pos.x.ToString("F2") + ", " +
                               pos.y.ToString("F2") + ", " +
                               pos.z.ToString("F2") + ")");
                return true;
            }

            // Rate-limited failure log
            if (Time.unscaledTime >= nextBindWarningTime)
            {
                nextBindWarningTime = Time.unscaledTime + 5f;
                DiagnosticLog.Warning(
                    "Cannot find local player at " + LocalPlayerLocator.HardcodedPlayerPathDisplay +
                    " in scene '" + SceneManager.GetActiveScene().name +
                    "'. Is the player in the scene?");

                if (Time.unscaledTime >= nextDiagnosticTime)
                {
                    nextDiagnosticTime = Time.unscaledTime + 15f;
                    RunBindDiagnostics();
                }
            }
            return false;
        }

        // ── Per-frame tick ─────────────────────────────────────────────────────
        public void Tick()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != activeSceneName)
            {
                DiagnosticLog.Info(
                    "Scene changed: '" + activeSceneName + "' → '" + sceneName +
                    "'. Clearing all puppets and re-binding player.");

                activeSceneName   = sceneName;
                localPlayerRoot   = null;
                boundSuccessfully = false;
                puppetRegistry.Clear();

                if (IsMenuScene(sceneName))
                    DiagnosticLog.Info(
                        "Entered menu scene '" + sceneName +
                        "' — no puppets will be created here.");
            }

            // Periodic local player location log
            if (localPlayerRoot != null &&
                localPlayerRoot.gameObject != null &&
                Time.unscaledTime >= nextLocalPosLogTime)
            {
                nextLocalPosLogTime = Time.unscaledTime + LocalPosLogInterval;
                Vector3 lpos = localPlayerRoot.position;
                DiagnosticLog.Info(
                    "LocalPlayer [" + localPlayerId + "]" +
                    "  scene=" + sceneName +
                    "  x=" + lpos.x.ToString("F3") +
                    "  y=" + lpos.y.ToString("F3") +
                    "  z=" + lpos.z.ToString("F3"));
            }

            puppetRegistry.Tick();
        }

        // ── Remote state handler ───────────────────────────────────────────────
        private void OnRemoteStateReceived(RemotePlayerState state)
        {
            if (state == null || string.IsNullOrEmpty(state.playerId))
                return;

            // Ignore our own echo
            if (!string.IsNullOrEmpty(localPlayerId) && state.playerId == localPlayerId)
                return;

            // First-time-seen log
            if (!seenRemotePlayers.Contains(state.playerId))
            {
                seenRemotePlayers.Add(state.playerId);
                DiagnosticLog.Info(
                    "First state received from remote player '" + state.playerId +
                    "' (" + (state.displayName ?? "?") + ")" +
                    "  scene=" + state.sceneName +
                    "  pos=(" + state.position.x.ToString("F2") + ", " +
                               state.position.y.ToString("F2") + ", " +
                               state.position.z.ToString("F2") + ")");
            }

            // Don't create puppets in a menu scene
            if (IsMenuScene(activeSceneName))
                return;

            if (!TryBindLocalPlayer())
            {
                DiagnosticLog.Warning(
                    "Cannot create puppet for '" + state.playerId + "' yet — " +
                    LocalPlayerLocator.HardcodedPlayerPathDisplay +
                    " not found. Will retry next frame.");
                return;
            }

            puppetRegistry.Apply(state, localPlayerRoot, puppetFactory);
        }

        private void OnRemotePlayerLeft(string playerId)
        {
            DiagnosticLog.Info(
                "Remote player '" + playerId + "' left the room. Destroying puppet.");
            puppetRegistry.Remove(playerId);
        }

        // ── Mita state handler ─────────────────────────────────────────────────
        private void OnRemoteMitaStateReceived(MitaState state)
        {
            if (mitaController != null)
                mitaController.OnRemoteMitaStateReceived(state);
        }

        // ── Dispose ────────────────────────────────────────────────────────────
        public void LateTick()
        {
            puppetRegistry.LateTick();
        }

        public void Dispose()
        {
            Unsubscribe();
            puppetRegistry.Clear();
        }

        // ── Subscribe/Unsubscribe ──────────────────────────────────────────────
        private void Subscribe()
        {
            if (rpcDispatcher == null) return;
            rpcDispatcher.RemoteStateReceived     -= OnRemoteStateReceived;
            rpcDispatcher.RemotePlayerLeft        -= OnRemotePlayerLeft;
            rpcDispatcher.RemoteMitaStateReceived -= OnRemoteMitaStateReceived;
            rpcDispatcher.RemoteStateReceived     += OnRemoteStateReceived;
            rpcDispatcher.RemotePlayerLeft        += OnRemotePlayerLeft;
            rpcDispatcher.RemoteMitaStateReceived += OnRemoteMitaStateReceived;
        }

        private void Unsubscribe()
        {
            if (rpcDispatcher == null) return;
            rpcDispatcher.RemoteStateReceived     -= OnRemoteStateReceived;
            rpcDispatcher.RemotePlayerLeft        -= OnRemotePlayerLeft;
            rpcDispatcher.RemoteMitaStateReceived -= OnRemoteMitaStateReceived;
        }

        // ── Helpers ────────────────────────────────────────────────────────────
        private static bool IsMenuScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            for (int i = 0; i < MenuSceneNames.Length; i++)
                if (string.Equals(sceneName, MenuSceneNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            return sceneName.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void RunBindDiagnostics()
        {
            DiagnosticLog.Info("─── Bind diagnostics ─────────────────────────────────────");
            try   { DiagnosticLog.Info(LocalPlayerLocator.RunSkinnedFind()); }
            catch (Exception ex) { DiagnosticLog.Warning("[skinnedfind] failed: " + ex.Message); }
            try   { DiagnosticLog.Info(LocalPlayerLocator.RunFindAll()); }
            catch (Exception ex) { DiagnosticLog.Warning("[find *] failed: " + ex.Message); }
            DiagnosticLog.Info("──────────────────────────────────────────────────────────");
        }
    }
}
