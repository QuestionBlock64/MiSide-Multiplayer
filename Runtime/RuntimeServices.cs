using System;
using HarmonyLib;
using UnityEngine;

namespace MiSideMultiplayer
{
    internal sealed class RuntimeServices : IDisposable
    {
        private readonly RpcDispatcher        rpcDispatcher;
        private readonly NetworkManager       networkManager;
        private readonly LocalPlayerSampler   localPlayerSampler;
        private readonly TcpRelayTransport    relayTransport;
        private readonly MitaSampler          mitaSampler;
        private readonly MitaController       mitaController;
        private readonly WorldDoorSync        worldDoorSync;
        private readonly WorldStoryObjectSync worldStoryObjectSync;
        private readonly BoneSync             boneSync;
        private readonly ChatController       chatController;
        private readonly SharedLifeController  sharedLifeController;
        private readonly string               localPlayerId;
        private readonly InventorySharingController inventorySharingController;

        private readonly UnityEngine.Events.UnityAction<
            UnityEngine.SceneManagement.Scene,
            UnityEngine.SceneManagement.LoadSceneMode> _sceneLoadedHandler;

        public RuntimeServices(
            Transform runtimeRoot,
            string    localPlayerId,
            string    displayName,
            string[]  visualRootCandidates,
            string[]  playerRootCandidates,
            float     snapshotSendRate,
            float     snapshotHeartbeatSeconds,
            bool      emitLocalSnapshots,
            bool      enableNetworking,
            string    serverHost,
            int       serverPort,
            string    roomName,
            Func<string> displayNameProvider)
        {
            this.localPlayerId = localPlayerId;

            CustomModelBridge.TryInitialize();
            PlayerFallGuard.Initialize();

            rpcDispatcher        = new RpcDispatcher();
            networkManager       = new NetworkManager(runtimeRoot);
            localPlayerSampler   = new LocalPlayerSampler();
            mitaSampler          = new MitaSampler();
            mitaController       = new MitaController();
            worldDoorSync        = new WorldDoorSync();
            worldStoryObjectSync = new WorldStoryObjectSync();
            boneSync             = new BoneSync();
            chatController       = new ChatController(
                rpcDispatcher,
                networkManager,
                localPlayerId,
                displayNameProvider);

            sharedLifeController = new SharedLifeController(rpcDispatcher, localPlayerId);
            relayTransport = new TcpRelayTransport(
                rpcDispatcher,
                localPlayerId,
                roomName,
                serverHost,
                serverPort,
                enableNetworking,
                displayNameProvider);
            inventorySharingController = new InventorySharingController(rpcDispatcher, localPlayerId);

            mitaController.Configure(localPlayerId);
            mitaSampler.Configure(rpcDispatcher, localPlayerId, snapshotSendRate * 0.5f);

            worldDoorSync.Configure(rpcDispatcher, localPlayerId);
            worldStoryObjectSync.Configure(rpcDispatcher, localPlayerId);
            boneSync.Configure(rpcDispatcher, localPlayerId);

            networkManager.Configure(
                rpcDispatcher,
                visualRootCandidates,
                playerRootCandidates,
                localPlayerId,
                mitaController);

            localPlayerSampler.Configure(
                rpcDispatcher,
                networkManager,
                localPlayerId,
                displayName,
                snapshotSendRate,
                snapshotHeartbeatSeconds,
                emitLocalSnapshots);

            try
            {
                _sceneLoadedHandler =
                    Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<
                        UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene,
                                                       UnityEngine.SceneManagement.LoadSceneMode>>(OnSceneLoaded);

                UnityEngine.SceneManagement.SceneManager.sceneLoaded += _sceneLoadedHandler;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("RuntimeServices: failed to subscribe sceneLoaded: " +
                                      ex.GetType().Name + ": " + ex.Message);
                _sceneLoadedHandler = null;
            }

            relayTransport.Start();

            DiagnosticLog.Info(
                "RuntimeServices started. LocalPlayerId='" + localPlayerId +
                "'  DisplayName='" + displayName + "'");
        }

        public static void InitializeWorldGuard(Harmony harmony)
        {
            WorldOccupancyGuard.Initialize(harmony);
        }

        private static void OnSceneLoaded(
            UnityEngine.SceneManagement.Scene scene,
            UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            try { MitaSceneCache.Invalidate(); } catch { }
            try { WorldOccupancyGuard.Invalidate(); } catch { }
        }

        public void Tick()
        {
            PerfProbe.Begin(); relayTransport.Tick();             PerfProbe.End("relay");
            PerfProbe.Begin(); rpcDispatcher.Tick();              PerfProbe.End("rpc");
            PerfProbe.Begin(); networkManager.Tick();             PerfProbe.End("network");
            PerfProbe.Begin(); localPlayerSampler.Tick();         PerfProbe.End("localPlayer");
            PerfProbe.Begin(); mitaSampler.Tick();                PerfProbe.End("mitaSampler");
            PerfProbe.Begin(); mitaController.Tick();             PerfProbe.End("mitaController");
            PerfProbe.Begin(); worldDoorSync.Tick();              PerfProbe.End("doors");
            PerfProbe.Begin(); worldStoryObjectSync.Tick();       PerfProbe.End("story");
            PerfProbe.Begin(); boneSync.Tick();                   PerfProbe.End("boneSync");
            PerfProbe.Begin(); chatController.Tick();             PerfProbe.End("chat");
            PerfProbe.Begin(); inventorySharingController.Tick(); PerfProbe.End("inventory");
            PerfProbe.Begin(); WorldOccupancyGuard.Tick();        PerfProbe.End("occupancy");
            PerfProbe.Begin(); PlayerFallGuard.Tick();            PerfProbe.End("fallGuard");
            PerfProbe.FrameDone();
        }

        public void LateTick()
        {
            PerfProbe.Begin(); networkManager.LateTick(); PerfProbe.End("networkLate");
            PerfProbe.Begin(); boneSync.LateTick();       PerfProbe.End("boneLate");
        }

        public void OnGui()
        {
            chatController.OnGui();
        }

        public void Dispose()
        {
            if (_sceneLoadedHandler != null)
            {
                try
                {
                    UnityEngine.SceneManagement.SceneManager.sceneLoaded -= _sceneLoadedHandler;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("RuntimeServices: failed to unsubscribe sceneLoaded: " +
                                          ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (rpcDispatcher != null)
                rpcDispatcher.SendPlayerLeft(localPlayerId);

            relayTransport.Dispose();
            sharedLifeController.Dispose();
            chatController.Dispose();
            inventorySharingController.Dispose();
            networkManager.Dispose();
            boneSync.Dispose();
        }
    }
}