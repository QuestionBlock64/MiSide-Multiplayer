using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed class RpcDispatcher
    {
        public const string PlayerStateEvent = "miside.player.state";
        public const string PlayerLeftEvent = "miside.player.left";
        public const string TransportHelloEvent = "miside.transport.hello";

        public event Action<RemotePlayerState> RemoteStateReceived;
        public event Action<string> RemotePlayerLeft;
        public event Action<string, string> OutgoingRpc;

        private readonly Queue<QueuedRpc> incomingQueue = new Queue<QueuedRpc>();
        private readonly object queueLock = new object();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        public void SendState(RemotePlayerState state)
        {
            if (state == null || string.IsNullOrEmpty(state.playerId))
                return;

            SendCustom(PlayerStateEvent, JsonSerializer.Serialize(state, JsonOptions));
        }

        public void SendPlayerLeft(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
                return;

            PlayerLeftMessage message = new PlayerLeftMessage();
            message.playerId = playerId;
            SendCustom(PlayerLeftEvent, JsonSerializer.Serialize(message, JsonOptions));
        }

        public void SendCustom(string eventName, string jsonPayload)
        {
            if (string.IsNullOrEmpty(eventName))
                return;

            OutgoingRpc?.Invoke(eventName, jsonPayload);
        }

        public void ReceiveCustom(string eventName, string jsonPayload)
        {
            if (string.IsNullOrEmpty(eventName))
                return;

            lock (queueLock)
                incomingQueue.Enqueue(new QueuedRpc(eventName, jsonPayload));
        }

        public void Tick()
        {
            while (TryDequeue(out QueuedRpc rpc))
                DispatchIncomingNow(rpc.EventName, rpc.JsonPayload);
        }

        private bool TryDequeue(out QueuedRpc rpc)
        {
            lock (queueLock)
            {
                if (incomingQueue.Count == 0)
                {
                    rpc = default(QueuedRpc);
                    return false;
                }

                rpc = incomingQueue.Dequeue();
                return true;
            }
        }

        private void DispatchIncomingNow(string eventName, string jsonPayload)
        {
            try
            {
                if (eventName == PlayerStateEvent)
                {
                    RemotePlayerState state = JsonSerializer.Deserialize<RemotePlayerState>(jsonPayload, JsonOptions);
                    RemoteStateReceived?.Invoke(state);
                    return;
                }

                if (eventName == PlayerLeftEvent)
                {
                    PlayerLeftMessage message = JsonSerializer.Deserialize<PlayerLeftMessage>(jsonPayload, JsonOptions);
                    if (message != null)
                        RemotePlayerLeft?.Invoke(message.playerId);
                    return;
                }

                Debug.LogWarning("[MiSideMultiplayer] Unhandled RPC event: " + eventName);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MiSideMultiplayer] Failed to dispatch RPC " + eventName + ": " + ex);
            }
        }

        [Serializable]
        private sealed class PlayerLeftMessage
        {
            public string playerId;
        }

        private struct QueuedRpc
        {
            public readonly string EventName;
            public readonly string JsonPayload;

            public QueuedRpc(string eventName, string jsonPayload)
            {
                EventName = eventName;
                JsonPayload = jsonPayload;
            }
        }
    }
}
