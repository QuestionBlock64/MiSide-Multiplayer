using System;
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

            relayTransport.Start();

            DiagnosticLog.Info(
                "RuntimeServices started. LocalPlayerId='" + localPlayerId +
                "'  DisplayName='" + displayName + "'");
        }

        public void Tick()
        {
            relayTransport.Tick();
            rpcDispatcher.Tick();
            networkManager.Tick();
            localPlayerSampler.Tick();
            mitaSampler.Tick();
            mitaController.Tick();
            worldDoorSync.Tick();
            worldStoryObjectSync.Tick();
            boneSync.Tick();
            chatController.Tick();
            inventorySharingController.Tick();
        }

        public void LateTick()
        {
            networkManager.LateTick();
            boneSync.LateTick();
        }

        public void OnGui()
        {
            chatController.OnGui();
        }

        public void Dispose()
        {
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
