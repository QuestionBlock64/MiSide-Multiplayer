using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed class RpcDispatcher
    {
        public const string PlayerStateEvent      = "miside.player.state";
        public const string PlayerLeftEvent       = "miside.player.left";
        public const string MitaStateEvent        = "miside.mita.state";
        public const string DoorStateEvent        = "miside.world.door";
        public const string StoryObjectStateEvent = "miside.world.storyobj";
        public const string BoneSyncEvent         = "miside.player.bonesync";
        public const string TransportHelloEvent   = "miside.transport.hello";

        public event Action<RemotePlayerState>     RemoteStateReceived;
        public event Action<string>                RemotePlayerLeft;
        public event Action<MitaState>              RemoteMitaStateReceived;
        public event Action<WorldDoorState>         RemoteDoorStateReceived;
        public event Action<WorldStoryObjectState>  RemoteStoryObjectStateReceived;
        public event Action<BoneSyncMessage>        RemoteBoneSyncReceived;
        public event Action<string, string>         OutgoingRpc;

        private readonly Queue<QueuedRpc> incomingQueue = new Queue<QueuedRpc>();
        private readonly object           queueLock     = new object();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        public void SendState(RemotePlayerState state)
        {
            if (state == null || string.IsNullOrEmpty(state.playerId)) return;
            SendCustom(PlayerStateEvent, JsonSerializer.Serialize(state, JsonOptions));
        }

        public void SendPlayerLeft(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            PlayerLeftMessage msg = new PlayerLeftMessage();
            msg.playerId = playerId;
            SendCustom(PlayerLeftEvent, JsonSerializer.Serialize(msg, JsonOptions));
        }

        public void SendMitaState(MitaState state)
        {
            if (state == null || string.IsNullOrEmpty(state.senderId)) return;
            SendCustom(MitaStateEvent, JsonSerializer.Serialize(state, JsonOptions));
        }

        public void SendDoorState(WorldDoorState state)
        {
            if (state == null || string.IsNullOrEmpty(state.path)) return;
            SendCustom(DoorStateEvent, JsonSerializer.Serialize(state, JsonOptions));
        }

        public void SendStoryObjectState(WorldStoryObjectState state)
        {
            if (state == null || string.IsNullOrEmpty(state.path)) return;
            SendCustom(StoryObjectStateEvent, JsonSerializer.Serialize(state, JsonOptions));
        }

        public void SendBoneSync(BoneSyncMessage msg)
        {
            if (msg == null || msg.Bones == null || msg.Bones.Count == 0) return;
            SendCustom(BoneSyncEvent, JsonSerializer.Serialize(msg, JsonOptions));
        }

        public void SendCustom(string eventName, string jsonPayload)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            OutgoingRpc?.Invoke(eventName, jsonPayload);
        }

        public void ReceiveCustom(string eventName, string jsonPayload)
        {
            if (string.IsNullOrEmpty(eventName)) return;
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
                    PlayerLeftMessage msg = JsonSerializer.Deserialize<PlayerLeftMessage>(jsonPayload, JsonOptions);
                    if (msg != null) RemotePlayerLeft?.Invoke(msg.playerId);
                    return;
                }
                if (eventName == MitaStateEvent)
                {
                    MitaState mitaState = JsonSerializer.Deserialize<MitaState>(jsonPayload, JsonOptions);
                    RemoteMitaStateReceived?.Invoke(mitaState);
                    return;
                }
                if (eventName == DoorStateEvent)
                {
                    WorldDoorState doorState = JsonSerializer.Deserialize<WorldDoorState>(jsonPayload, JsonOptions);
                    RemoteDoorStateReceived?.Invoke(doorState);
                    return;
                }
                if (eventName == StoryObjectStateEvent)
                {
                    WorldStoryObjectState storyState = JsonSerializer.Deserialize<WorldStoryObjectState>(jsonPayload, JsonOptions);
                    RemoteStoryObjectStateReceived?.Invoke(storyState);
                    return;
                }
                if (eventName == BoneSyncEvent)
                {
                    BoneSyncMessage boneMsg = JsonSerializer.Deserialize<BoneSyncMessage>(jsonPayload, JsonOptions);
                    RemoteBoneSyncReceived?.Invoke(boneMsg);
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
        private sealed class PlayerLeftMessage { public string playerId; }

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