using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed class RpcDispatcher
    {
        // ── Event names ───────────────────────────────────────────────────────
        public const string PlayerStateEvent  = "miside.player.state";
        public const string PlayerLeftEvent   = "miside.player.left";
        public const string MitaStateEvent    = "miside.mita.state";
        public const string TransportHelloEvent = "miside.transport.hello";

        // ── Outbound events (subscribed by transport) ─────────────────────────
        public event Action<RemotePlayerState> RemoteStateReceived;
        public event Action<string>            RemotePlayerLeft;
        public event Action<MitaState>         RemoteMitaStateReceived;
        public event Action<string, string>    OutgoingRpc;

        private readonly Queue<QueuedRpc> incomingQueue = new Queue<QueuedRpc>();
        private readonly object           queueLock     = new object();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        // ── Send helpers ──────────────────────────────────────────────────────

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

            PlayerLeftMessage msg = new PlayerLeftMessage();
            msg.playerId = playerId;
            SendCustom(PlayerLeftEvent, JsonSerializer.Serialize(msg, JsonOptions));
        }

        public void SendMitaState(MitaState state)
        {
            if (state == null || string.IsNullOrEmpty(state.senderId))
                return;

            SendCustom(MitaStateEvent, JsonSerializer.Serialize(state, JsonOptions));
        }

        public void SendCustom(string eventName, string jsonPayload)
        {
            if (string.IsNullOrEmpty(eventName))
                return;

            OutgoingRpc?.Invoke(eventName, jsonPayload);
        }

        // ── Receive (thread-safe queue) ───────────────────────────────────────

        public void ReceiveCustom(string eventName, string jsonPayload)
        {
            if (string.IsNullOrEmpty(eventName))
                return;

            lock (queueLock)
                incomingQueue.Enqueue(new QueuedRpc(eventName, jsonPayload));
        }

        // ── Main-thread tick ──────────────────────────────────────────────────

        public void Tick()
        {
            while (TryDequeue(out QueuedRpc rpc))
                DispatchIncomingNow(rpc.EventName, rpc.JsonPayload);
        }

        // ── Private dispatch ──────────────────────────────────────────────────

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
                    RemotePlayerState state =
                        JsonSerializer.Deserialize<RemotePlayerState>(jsonPayload, JsonOptions);
                    RemoteStateReceived?.Invoke(state);
                    return;
                }

                if (eventName == PlayerLeftEvent)
                {
                    PlayerLeftMessage msg =
                        JsonSerializer.Deserialize<PlayerLeftMessage>(jsonPayload, JsonOptions);
                    if (msg != null)
                        RemotePlayerLeft?.Invoke(msg.playerId);
                    return;
                }

                if (eventName == MitaStateEvent)
                {
                    MitaState mitaState =
                        JsonSerializer.Deserialize<MitaState>(jsonPayload, JsonOptions);
                    RemoteMitaStateReceived?.Invoke(mitaState);
                    return;
                }

                Debug.LogWarning("[MiSideMultiplayer] Unhandled RPC event: " + eventName);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MiSideMultiplayer] Failed to dispatch RPC " + eventName + ": " + ex);
            }
        }

        // ── Inner types ───────────────────────────────────────────────────────

        [Serializable]
        private sealed class PlayerLeftMessage { public string playerId; }

        private struct QueuedRpc
        {
            public readonly string EventName;
            public readonly string JsonPayload;

            public QueuedRpc(string eventName, string jsonPayload)
            {
                EventName   = eventName;
                JsonPayload = jsonPayload;
            }
        }
    }
}
