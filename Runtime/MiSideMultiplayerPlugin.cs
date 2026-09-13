using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using HarmonyLib;
using MiSideMultiplayer.API;

namespace MiSideMultiplayer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed partial class MiSideMultiplayerPlugin : BasePlugin
    {
        public const string PluginGuid = "MS_Multiplayer";
        public const string PluginName = "MiSide Multiplayer Runtime";
        public const string PluginVersion = "0.8.0-pre.1";

        internal static MiSideMultiplayerRuntime RuntimeDriver;

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
        private static RuntimeServices runtimeServices;

        public override void Load()
        {
            // ── MITA-SYNC DIAGNOSTIC ────────────────────────────────────────
            try { Log.LogInfo("[MITA-DIAG] Load() entered."); } catch { }
            // ────────────────────────────────────────────────────────────────

            localPlayerId = Config.Bind(
                "Identity",
                "LocalPlayerId",
                CreateDefaultPlayerId(),
                "Stable ID sent with local snapshots. Use your server/account ID if you already have one.");

            displayName = Config.Bind(
                "Identity",
                "DisplayName",
                "Player",
                "Name sent with local snapshots, chat messages, and join/leave notices.");

            visualRootCandidates = Config.Bind(
                "Puppets",
                "VisualRootCandidates",
                "CameraMita/Mita,Mita/MitaPerson Mita,MitaPerson Mita,MitaPerson Mita/Body,Mita,Model,Visuals,PlayerModel,Character,Body,Armature,Mesh,Avatar",
                "Comma-separated child names or paths to search for the visual-only player hierarchy.");

            playerRootCandidates = Config.Bind(
                "Puppets",
                "PlayerRootCandidates",
                "Tamagotchi,World/Tamagotchi,CameraMita,Player,LocalPlayer,PlayerController,FirstPersonController,MainPlayer,PlayerMove,Player_move,PlayerRoot,Character,Personage",
                "Comma-separated root names used by the automatic local-player locator.");

            enableNetworking = Config.Bind(
                "Networking",
                "EnableNetworking",
                true,
                "If true, connect to the MiSide multiplayer relay and exchange puppet snapshots.");

            serverHost = Config.Bind(
                "Networking",
                "ServerHost",
                "127.0.0.1",
                "Relay server host. Use the host PC's LAN IP when joining another machine.");

            serverPort = Config.Bind(
                "Networking",
                "ServerPort",
                7777,
                "Relay server TCP port.");

            roomName = Config.Bind(
                "Networking",
                "RoomName",
                "default",
                "Simple client-side room filter. Players only consume packets from the same room.");

            snapshotSendRate = Config.Bind(
                "Networking",
                "SnapshotSendRate",
                40f,
                "Local state snapshots per second.");

            snapshotHeartbeatSeconds = Config.Bind(
                "Networking",
                "SnapshotHeartbeatSeconds",
                1f,
                "Maximum seconds between state snapshots even when the local player is idle.");

            emitLocalSnapshots = Config.Bind(
                "Networking",
                "EmitLocalSnapshots",
                true,
                "If true, LocalPlayerSampler raises RpcDispatcher.OutgoingRpc events.");

            debugLogging = Config.Bind(
                "Diagnostics",
                "DebugLogging",
                true,
                "If true, logs first snapshot send/receive and puppet creation diagnostics.");

            DiagnosticLog.Enabled = debugLogging.Value;
            TryWriteVersionFile();
            RegisterIl2CppTypes();

            MiSideMultiplayerRuntime driver = AddComponent<MiSideMultiplayerRuntime>();
            RuntimeDriver = driver;

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

            InitializeMultiplayerSettings();

            // ── MITA-SYNC DIAGNOSTIC ────────────────────────────────────────
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
                Log.LogInfo("[MITA-DIAG] MitaPlayerOverride.Initialize() returned normally.");

                Log.LogInfo("[MITA-DIAG] Probing MitaWalkTarget type...");
                Type mwtType = FindTypeAcrossAssemblies("MitaWalkTarget");
                if (mwtType == null)
                {
                    Log.LogWarning("[MITA-DIAG] MitaWalkTarget type NOT FOUND in any loaded assembly.");
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
            // ────────────────────────────────────────────────────────────────

            Log.LogInfo("MiSide multiplayer Runtime initialized for BepInEx 6 IL2CPP.");
        }

        // ── MITA-SYNC DIAGNOSTIC HELPER ─────────────────────────────────────
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
        // ────────────────────────────────────────────────────────────────────

        internal static void TickRuntime()
        {
            if (runtimeServices != null)
                runtimeServices.Tick();
            TickMultiplayerSettings();
        }

        internal static void LateTickRuntime()
        {
            if (runtimeServices != null)
                runtimeServices.LateTick();
        }

        internal static void DrawRuntimeGui()
        {
            if (runtimeServices != null)
                runtimeServices.OnGui();
            DrawMultiplayerSettingsGui();
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
        }

        private void TryWriteVersionFile()
        {
            try
            {
                VersionFileWriter.Write(
                    PluginName,
                    PluginVersion,
                    enableNetworking.Value,
                    serverHost.Value,
                    serverPort.Value,
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