using System;
using UnityEngine;

namespace MiSideMultiplayer
{
    internal sealed class RuntimeServices : IDisposable
    {
        private readonly RpcDispatcher     rpcDispatcher;
        private readonly NetworkManager    networkManager;
        private readonly LocalPlayerSampler localPlayerSampler;
        private readonly TcpRelayTransport relayTransport;
        private readonly MitaSampler       mitaSampler;
        private readonly MitaController    mitaController;
        private readonly string            localPlayerId;

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

            rpcDispatcher      = new RpcDispatcher();
            networkManager     = new NetworkManager(runtimeRoot);
            localPlayerSampler = new LocalPlayerSampler();
            mitaSampler        = new MitaSampler();
            mitaController     = new MitaController();

            relayTransport = new TcpRelayTransport(
                rpcDispatcher,
                localPlayerId,
                roomName,
                serverHost,
                serverPort,
                enableNetworking);

            // Configure Mita systems
            mitaController.Configure(localPlayerId);
            mitaSampler.Configure(rpcDispatcher, localPlayerId, snapshotSendRate * 0.5f);

            // Configure network manager with Mita controller
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
        }

        public void LateTick()
        {
            networkManager.LateTick();
        }

        public void Dispose()
        {
            if (rpcDispatcher != null)
                rpcDispatcher.SendPlayerLeft(localPlayerId);

            relayTransport.Dispose();
            networkManager.Dispose();
        }
    }
}
