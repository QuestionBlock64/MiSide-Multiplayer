using System;
using UnityEngine;

namespace MiSideMultiplayer
{
    internal sealed class RuntimeServices : IDisposable
    {
        private readonly RpcDispatcher rpcDispatcher;
        private readonly NetworkManager networkManager;
        private readonly LocalPlayerSampler localPlayerSampler;
        private readonly TcpRelayTransport relayTransport;
        private readonly string localPlayerId;

        public RuntimeServices(
            Transform runtimeRoot,
            string localPlayerId,
            string displayName,
            string[] visualRootCandidates,
            string[] playerRootCandidates,
            float snapshotSendRate,
            float snapshotHeartbeatSeconds,
            bool emitLocalSnapshots,
            bool enableNetworking,
            string serverHost,
            int serverPort,
            string roomName)
        {
            this.localPlayerId = localPlayerId;

            rpcDispatcher = new RpcDispatcher();
            networkManager = new NetworkManager(runtimeRoot);
            localPlayerSampler = new LocalPlayerSampler();
            relayTransport = new TcpRelayTransport(
                rpcDispatcher,
                localPlayerId,
                roomName,
                serverHost,
                serverPort,
                enableNetworking);

            networkManager.Configure(
                rpcDispatcher,
                visualRootCandidates,
                playerRootCandidates,
                localPlayerId);

            localPlayerSampler.Configure(
                rpcDispatcher,
                networkManager,
                localPlayerId,
                displayName,
                snapshotSendRate,
                snapshotHeartbeatSeconds,
                emitLocalSnapshots);

            relayTransport.Start();
        }

        public void Tick()
        {
            relayTransport.Tick();
            rpcDispatcher.Tick();
            networkManager.Tick();
            localPlayerSampler.Tick();
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
