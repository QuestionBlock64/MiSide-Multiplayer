using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using HarmonyLib;
using UnityEngine;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Patches;
using MiSideMultiplayer.Runtime.Menu;
using MiSideMultiplayer.Runtime.Menu.UI;

namespace MiSideMultiplayer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed partial class MiSideMultiplayerPlugin : BasePlugin
    {
        public const string PluginGuid = "MS_Multiplayer";
        public const string PluginName = "MiSide Multiplayer Beta";
        public const string PluginVersion = "1.0.0-beta.1";

        internal static MiSideMultiplayerRuntime RuntimeDriver;
        internal static MiSideMultiplayerPlugin activeSettingsPlugin;

        private ConfigEntry<string> localPlayerId;
        private ConfigEntry<string> displayName;
        private ConfigEntry<string> visualRootCandidates;
        private ConfigEntry<string> playerRootCandidates;
        private ConfigEntry<bool> enableNetworking;
        private ConfigEntry<string> serverHost;
        private ConfigEntry<int> serverPort;
        private ConfigEntry<string> roomName;
        private ConfigEntry<float> snapshotSendRate;
        private ConfigEntry<float> snapshotHeartbeatSeconds;
        private ConfigEntry<bool> emitLocalSnapshots;
        private ConfigEntry<bool> debugLogging;
        private ConfigEntry<bool> verboseLogging;
        private ConfigEntry<bool> hideNativeLoadingUI;
        private static RuntimeServices runtimeServices;

        public override void Load()
        {
            try { Log.LogInfo("[MITA-DIAG] Load() entered."); } catch { }

            localPlayerId = Config.Bind(
                "Identity", "LocalPlayerId", CreateDefaultPlayerId(),
                "Stable ID sent with local snapshots. Use your server/account ID if you already have one.");

            displayName = Config.Bind(
                "Identity", "DisplayName", "Player",
                "Name sent with local snapshots, chat messages, and join/leave notices.");

            visualRootCandidates = Config.Bind(
                "Puppets", "VisualRootCandidates",
                "CameraMita/Mita,Mita/MitaPerson Mita,MitaPerson Mita,MitaPerson Mita/Body,Mita,Model,Visuals,PlayerModel,Character,Body,Armature,Mesh,Avatar",
                "Comma-separated child names or paths to search for the visual-only player hierarchy.");

            playerRootCandidates = Config.Bind(
                "Puppets", "PlayerRootCandidates",
                "Tamagotchi,World/Tamagotchi,CameraMita,Player,LocalPlayer,PlayerController,FirstPersonController,MainPlayer,PlayerMove,Player_move,PlayerRoot,Character,Personage",
                "Comma-separated root names used by the automatic local-player locator.");

            enableNetworking = Config.Bind(
                "Networking", "EnableNetworking", true,
                "If true, connect to the MiSide multiplayer relay and exchange puppet snapshots.");

            serverHost = Config.Bind(
                "Networking", "ServerHost", RuntimeServices.CentralRelayHost,
                "Deprecated: mod now uses the central relay by default. Ignored at runtime.");

            serverPort = Config.Bind(
                "Networking", "ServerPort", RuntimeServices.CentralRelayPort,
                "Deprecated: mod now uses the central relay by default. Ignored at runtime.");

            roomName = Config.Bind(
                "Networking", "RoomName", "default",
                "Simple client-side room filter. Players only consume packets from the same room.");

            snapshotSendRate = Config.Bind(
                "Networking", "SnapshotSendRate", 40f,
                "Local state snapshots per second.");

            snapshotHeartbeatSeconds = Config.Bind(
                "Networking", "SnapshotHeartbeatSeconds", 1f,
                "Maximum seconds between state snapshots even when the local player is idle.");

            emitLocalSnapshots = Config.Bind(
                "Networking", "EmitLocalSnapshots", true,
                "If true, LocalPlayerSampler raises RpcDispatcher.OutgoingRpc events.");

            debugLogging = Config.Bind(
                "Diagnostics", "DebugLogging", true,
                "If true, logs first snapshot send/receive and puppet creation diagnostics.");

            verboseLogging = Config.Bind(
                "Diagnostics", "VerboseLogging", false,
                "If true, logs HIGH-FREQUENCY session traffic (hello broadcasts, " +
                "roster updates). Off by default because it can flood the BepInEx " +
                "console and crash the game.");

            hideNativeLoadingUI = Config.Bind(
                "Diagnostics", "HideNativeLoadingUI", true,
                "If true, hides the game's native loading bar and text during the " +
                "SceneLoading interstitial. The gradient background stays, so the " +
                "multiplayer ring draws over it. Set false to debug the native UI.");

            DiagnosticLog.Enabled = debugLogging.Value;
            DiagnosticLog.VerboseEnabled = verboseLogging.Value;

            TryWriteVersionFile();
            RegisterIl2CppTypes();

            try
            {
                SceneLoadedEvent.RegisterEvent();
                Log.LogInfo("[MITA-DIAG] SceneLoadedEvent registered.");
            }
            catch (Exception ex)
            {
                Log.LogError("[MITA-DIAG] SceneLoadedEvent.RegisterEvent THREW: " + ex);
            }

            activeSettingsPlugin = this;

            MiSideMultiplayerRuntime driver = AddComponent<MiSideMultiplayerRuntime>();
            RuntimeDriver = driver;

            try
            {
                var loadGo = new GameObject("MiSideMultiplayer.LoadingManager");
                UnityEngine.Object.DontDestroyOnLoad(loadGo);
                loadGo.hideFlags = HideFlags.HideAndDontSave;
                loadGo.AddComponent<MiSideMultiplayer.Runtime.Loading.LoadingStageManager>();
                Log.LogInfo("[MITA-DIAG] LoadingStageManager spawned.");
            }
            catch (Exception ex)
            {
                Log.LogError("[MITA-DIAG] LoadingStageManager spawn THREW: " + ex);
            }

            runtimeServices = new RuntimeServices(
                driver.transform,
                localPlayerId.Value,
                displayName.Value,
                SplitCsv(visualRootCandidates.Value),
                SplitCsv(playerRootCandidates.Value),
                snapshotSendRate.Value,
                snapshotHeartbeatSeconds.Value,
                emitLocalSnapshots.Value,
                enableNetworking.Value,
                serverHost.Value,
                serverPort.Value,
                roomName.Value,
                () => displayName.Value);

            try
            {
                Log.LogInfo("[MITA-DIAG] About to construct Harmony and call MitaPlayerOverride.Initialize().");
                Harmony harmony = new Harmony("com.miside.multiplayer.mita");
                Log.LogInfo("[MITA-DIAG] Harmony instance created, calling Initialize()...");
                MitaPlayerOverride.Configure(localPlayerId.Value);
                MitaPlayerOverride.Initialize(harmony);
                MitaEventSync.Initialize(harmony);
                WorldTransitionSync.Initialize(harmony);
                RuntimeServices.InitializeWorldGuard(harmony);
                MiSideMultiplayer.API.Patchers.HostDepartureHook.Initialize(harmony);
                MiSideMultiplayer.Runtime.Loading.SceneLoadingHook.Initialize(harmony);
                MiSideMultiplayer.Runtime.Loading.SceneManagerHook.Initialize(harmony);
                StorySceneTransitionSync.Initialize(harmony);
                Log.LogInfo("[MITA-DIAG] MitaPlayerOverride.Initialize() returned normally.");

                Log.LogInfo("[MITA-DIAG] Probing MitaWalkTarget type...");
                Type mwtType = FindTypeAcrossAssemblies("MitaWalkTarget");
                if (mwtType == null)
                {
                    Log.LogInfo("[MITA-DIAG] Optional MitaWalkTarget reflection probe did not resolve. " +
                                "This type is not required by host-authoritative BoneSync or quest-object sync.");
                }
                else
                {
                    Log.LogInfo("[MITA-DIAG] MitaWalkTarget found: " + mwtType.FullName +
                                " in '" + mwtType.Assembly.GetName().Name + "'");
                    MethodInfo start  = mwtType.GetMethod("Start",  BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    MethodInfo update = mwtType.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    Log.LogInfo("[MITA-DIAG]   Start()  = " + (start  != null ? "FOUND" : "MISSING"));
                    Log.LogInfo("[MITA-DIAG]   Update() = " + (update != null ? "FOUND" : "MISSING"));
                }
            }
            catch (Exception ex)
            {
                Log.LogError("[MITA-DIAG] Mita init/probe THREW: " + ex);
            }

            try
            {
                Log.LogInfo("[MITA-DIAG] Installing MenuPatch Harmony.");
                Harmony menuHarmony = new Harmony("com.misidemultiplayer.qblib.menu");
                menuHarmony.PatchAll(typeof(MiSideMultiplayer.Runtime.Menu.MenuPatch));
                Log.LogInfo("[MITA-DIAG] MenuPatch installed.");
            }
            catch (Exception ex)
            {
                Log.LogError("[MITA-DIAG] MenuPatch install THREW: " + ex);
            }

            try
            {
                Log.LogInfo("[MITA-DIAG] Installing MenuLocationActivePatch Harmony.");
                Harmony musicHarmony = new Harmony("com.misidemultiplayer.menumusic");
                musicHarmony.PatchAll(typeof(MiSideMultiplayer.Runtime.Menu.Music.MenuLocationActivePatch));
                Log.LogInfo("[MITA-DIAG] MenuLocationActivePatch installed.");
            }
            catch (Exception ex)
            {
                Log.LogError("[MITA-DIAG] MenuLocationActivePatch install THREW: " + ex);
            }

            try
            {
                Log.LogInfo("[MITA-DIAG] Installing MenuLocationActiveFadePatch Harmony.");
                Harmony fadeHarmony = new Harmony("com.misidemultiplayer.menufade");
                fadeHarmony.PatchAll(typeof(MiSideMultiplayer.Runtime.Menu.Patches.MenuLocationActiveFadePatch));
                Log.LogInfo("[MITA-DIAG] MenuLocationActiveFadePatch installed.");
            }
            catch (Exception ex)
            {
                Log.LogError("[MITA-DIAG] MenuLocationActiveFadePatch install THREW: " + ex);
            }

            Log.LogInfo("MiSide multiplayer Runtime initialized for BepInEx 6 IL2CPP.");
        }

        // ── Config getters/setters ──────────────────────────────────────────

        public static string GetDisplayName()   => activeSettingsPlugin?.displayName?.Value ?? "Player";
        public static string GetLocalPlayerId() => activeSettingsPlugin?.localPlayerId?.Value ?? "";
        public static bool   GetDebugLogging()  => activeSettingsPlugin?.debugLogging?.Value ?? true;
        public static bool   GetVerboseLogging() => activeSettingsPlugin?.verboseLogging?.Value ?? false;
        public static bool   GetHideNativeLoadingUI() =>
            activeSettingsPlugin?.hideNativeLoadingUI?.Value ?? true;

        public static string GetRelayHost() => RuntimeServices.CentralRelayHost;
        public static int    GetRelayPort() => RuntimeServices.CentralRelayPort;

        public static void SetDisplayName(string v)
        {
            var p = activeSettingsPlugin; if (p == null) return;
            p.displayName.Value = v ?? "";
            try { p.Config.Save(); } catch { }
        }

        public static void SetDebugLogging(bool v)
        {
            var p = activeSettingsPlugin; if (p == null) return;
            p.debugLogging.Value = v;
            DiagnosticLog.Enabled = v;
            try { p.Config.Save(); } catch { }
        }

        public static void SetVerboseLogging(bool v)
        {
            var p = activeSettingsPlugin; if (p == null) return;
            p.verboseLogging.Value = v;
            DiagnosticLog.VerboseEnabled = v;
            try { p.Config.Save(); } catch { }
        }

        // ── Lobby / session helpers ─────────────────────────────────────────

        public static RpcDispatcher GetRpcDispatcher()
        {
            var s = runtimeServices;
            return s != null ? s.Dispatcher : null;
        }

        public static bool IsRelayConnected()
        {
            var s = runtimeServices;
            return s != null && s.IsRelayConnected;
        }

        public static bool IsRelayReady()
        {
            var s = runtimeServices;
            return s != null && s.IsRelayReady;
        }

        public static bool IsLowConnection()
        {
            var s = runtimeServices;
            return s != null && s.IsLowConnection;
        }

        public static bool ConnectTo(string host, int port, string roomName = null)
        {
            var s = runtimeServices;
            if (s == null)
            {
                DiagnosticLog.Warning("ConnectTo: runtimeServices is null.");
                return false;
            }
            return s.ConnectTo(host, port, roomName);
        }

        public static bool ConnectToCentral(string roomName = null)
        {
            var s = runtimeServices;
            if (s == null)
            {
                DiagnosticLog.Warning("ConnectToCentral: runtimeServices is null.");
                return false;
            }
            return s.ConnectToCentral(roomName);
        }

        public static bool ConnectToCentralAt(int endpointIndex, string roomName = null,
                                              bool tryOtherEndpoints = true)
        {
            var s = runtimeServices;
            if (s == null)
            {
                DiagnosticLog.Warning("ConnectToCentralAt: runtimeServices is null.");
                return false;
            }
            return s.ConnectToCentralAt(endpointIndex, roomName, true, tryOtherEndpoints, false);
        }

        public static int GetCentralEndpointCount() => QBRelayFailover.EndpointCount;
        public static string GetCentralEndpointName(int endpointIndex) => QBRelayFailover.GetName(endpointIndex);

        public static int GetCurrentCentralEndpointIndex()
        {
            var s = runtimeServices;
            return s != null ? s.CurrentCentralEndpointIndex : -1;
        }

        public static void DisconnectRelay()
        {
            var s = runtimeServices;
            if (s != null) s.Disconnect();
        }

        // ── Runtime ticks ───────────────────────────────────────────────────

        private static Type FindTypeAcrossAssemblies(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        internal static void TickRuntime()
        {
            MiSideMultiplayer.API.Notifications.NotificationManager.Tick();
            MultiplayerMenuBranding.Tick();
            // Keep the post-GO native loading-background watchdog on the
            // persistent runtime. LoadingStageManager can stop ticking once
            // its pipeline completes, while the native SceneLoading canvas
            // may still be stranded over the destination scene.
            MiSideMultiplayer.Runtime.Loading.SceneLoadingHook.Tick();

            if (runtimeServices != null)
                runtimeServices.Tick();

            MultiplayerConnectionErrorHandler.Tick();
            MultiplayerErrorWindowMenu.Tick();
            MultiplayerSettingsMenu.Tick();
            MultiplayerLobbyMenu.Tick();
            MultiplayerHostSetupMenu.Tick();
            MultiplayerDirectConnectMenu.Tick();
            MultiplayerPrivateLobbyMenu.Tick();
            MultiplayerPublicLobbyMenu.Tick();

            MiSideMultiplayer.Runtime.UI.MultiplayerConnectingOverlay.Tick();
        }

        internal static void LateTickRuntime()
        {
            if (runtimeServices != null)
                runtimeServices.LateTick();

            MitaController.LateTick();
            MiSideMultiplayer.Runtime.Menu.Music.MultiplayerMenuMusic.LateTick();
        }

        internal static void DrawRuntimeGui()
        {
            if (runtimeServices != null)
                runtimeServices.OnGui();

            MultiplayerSettingsMenu.DrawGui();
            MultiplayerHostSetupMenu.DrawGui();
            MultiplayerDirectConnectMenu.DrawGui();
            MultiplayerPrivateLobbyMenu.DrawGui();
            MultiplayerPublicLobbyMenu.DrawGui();

            MiSideMultiplayer.Runtime.UI.MultiplayerConnectingOverlay.Draw();
        }

        internal static void DisposeRuntime()
        {
            if (runtimeServices != null)
            {
                runtimeServices.Dispose();
                runtimeServices = null;
            }
        }

        private static void RegisterIl2CppTypes()
        {
            ClassInjector.RegisterTypeInIl2Cpp<MiSideMultiplayerRuntime>();
            ClassInjector.RegisterTypeInIl2Cpp<TextFader>();
            ClassInjector.RegisterTypeInIl2Cpp<MultiplayerMenuEnterHandler>();
            ClassInjector.RegisterTypeInIl2Cpp<MiSideMultiplayer.Runtime.Menu.Music.MusicRunner>();
            ClassInjector.RegisterTypeInIl2Cpp<MiSideMultiplayer.API.Credits.CreditsGradient>();
            ClassInjector.RegisterTypeInIl2Cpp<CanvasGroupFader>();
            ClassInjector.RegisterTypeInIl2Cpp<TextColorLock>();

            ClassInjector.RegisterTypeInIl2Cpp<MiSideMultiplayer.Runtime.Loading.LoadingStageManager>();
            ClassInjector.RegisterTypeInIl2Cpp<MiSideMultiplayer.Runtime.MultiplayerLoadingScene>();
        }

        private void TryWriteVersionFile()
        {
            try
            {
                VersionFileWriter.Write(
                    PluginName,
                    PluginVersion,
                    enableNetworking.Value,
                    RuntimeServices.CentralRelayHost,
                    RuntimeServices.CentralRelayPort,
                    roomName.Value);
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not write Data\\Version.txt: " + ex.Message);
            }
        }

        private static string CreateDefaultPlayerId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static string[] SplitCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return new string[0];

            string[] raw = value.Split(',');
            for (int i = 0; i < raw.Length; i++)
                raw[i] = raw[i].Trim();

            return raw;
        }
    }
}
