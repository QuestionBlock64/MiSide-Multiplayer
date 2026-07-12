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
        private readonly string               localPlayerId;

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
            string    roomName)
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

            relayTransport = new TcpRelayTransport(
                rpcDispatcher,
                localPlayerId,
                roomName,
                serverHost,
                serverPort,
                enableNetworking);

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
        }

        public void LateTick()
        {
            networkManager.LateTick();
            boneSync.LateTick();
        }

        public void Dispose()
        {
            if (rpcDispatcher != null)
                rpcDispatcher.SendPlayerLeft(localPlayerId);

            relayTransport.Dispose();
            networkManager.Dispose();
            boneSync.Dispose();
        }
    }
}