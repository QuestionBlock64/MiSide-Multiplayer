using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace MiSideMultiplayer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class MiSideMultiplayerPlugin : BasePlugin
    {
        public const string PluginGuid = "com.miside.multiplayer.puppets";
        public const string PluginName = "MiSide Multiplayer Puppet Runtime";
        public const string PluginVersion = "1.0.0";

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
            localPlayerId = Config.Bind(
                "Identity",
                "LocalPlayerId",
                CreateDefaultPlayerId(),
                "Stable ID sent with local snapshots. Use your server/account ID if you already have one.");

            displayName = Config.Bind(
                "Identity",
                "DisplayName",
                Environment.UserName,
                "Name sent with local snapshots.");

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
                20f,
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
                roomName.Value);

            Log.LogInfo("MiSide multiplayer puppet runtime initialized for BepInEx 6 IL2CPP.");
        }

        internal static void TickRuntime()
        {
            if (runtimeServices != null)
                runtimeServices.Tick();
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
            string machine = Environment.MachineName;
            string user = Environment.UserName;
            return machine + "_" + user;
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
