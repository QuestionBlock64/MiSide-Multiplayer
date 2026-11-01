using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed class RpcDispatcher
    {
        private const string MinigameSignalEvent = "miside.minigame.signal";
        public const string PlayerStateEvent      = "miside.player.state";
        public const string PlayerLeftEvent       = "miside.player.left";
        public const string MitaStateEvent        = "miside.mita.state";
        public const string DoorStateEvent        = "miside.world.door";
        public const string StoryObjectStateEvent = "miside.world.storyobj";
        public const string BoneSyncEvent         = "miside.player.bonesync";
        public const string TransportHelloEvent   = "miside.transport.hello";
        public const string ChatMessageEvent      = "miside.chat.message";
        public const string ChatSystemEvent       = "miside.chat.system";
        public const string ServerResponseEvent   = "miside.server.response";
        public const string DeathLinkEvent        = "miside.deathlink";
        public const string InventoryClaimRequestEvent   = "miside.inventory.claim.request";
        public const string InventoryClaimResultEvent    = "miside.inventory.claim.result";
        public const string InventoryKeyAddedEvent       = "miside.inventory.key.add";
        public const string InventoryConsumeRequestEvent = "miside.inventory.consume.request";
        public const string InventoryConsumeResultEvent  = "miside.inventory.consume.result";
        public const string InventoryKeyRemovedEvent     = "miside.inventory.key.removed";
        public const string InventorySnapshotEvent       = "miside.inventory.snapshot";
        public const string Scene5BasementDoorEvent      = "miside.world.scene5.basementdoor.event";

        public event Action<RemotePlayerState>     RemoteStateReceived;
        public event Action<string>                RemotePlayerLeft;
        public event Action<MitaState>              RemoteMitaStateReceived;
        public event Action<WorldDoorState>         RemoteDoorStateReceived;
        public event Action<WorldStoryObjectState>  RemoteStoryObjectStateReceived;
        public event Action<BoneSyncMessage>        RemoteBoneSyncReceived;
        public event Action<ChatMessagePayload>     ChatMessageReceived;
        public event Action<ChatSystemMessagePayload> ChatSystemMessageReceived;
        public event Action<ServerResponsePayload>  ServerResponseReceived;
        public event Action<SharedLifeMessage>      DeathLinkReceived;
        public event Action<string, string>         OutgoingRpc;
        public event Action<InventoryClaimResult>   InventoryClaimResultReceived;
        public event Action<InventoryKeyChange>     InventoryKeyAddedReceived;
        public event Action<InventoryConsumeResult> InventoryConsumeResultReceived;
        public event Action<InventoryKeyRemoval>    InventoryKeyRemovedReceived;
        public event Action<InventorySnapshot>      InventorySnapshotReceived;
        public event Action<string, string>         CustomMessageReceived;
        public event Action<string, string, int>     CustomMessageReceivedFromRelay;

        private readonly Queue<QueuedRpc> incomingQueue = new Queue<QueuedRpc>();
        private readonly LinkedList<QueuedRpc> incomingMitaStateQueue =
            new LinkedList<QueuedRpc>();
        private readonly Dictionary<string, LinkedListNode<QueuedRpc>> pendingMitaStates =
            new Dictionary<string, LinkedListNode<QueuedRpc>>();
        private readonly object queueLock = new object();
        private long nextIncomingSequence;
        private long coalescedMitaStateCount;
        private float nextMitaQueueLogTime;

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

        public void SendChatMessage(ChatMessagePayload message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.text)) return;
            SendCustom(ChatMessageEvent, JsonSerializer.Serialize(message, JsonOptions));
        }

        public void SendDeathLink(SharedLifeMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.senderId) ||
                string.IsNullOrEmpty(message.sceneName) || string.IsNullOrEmpty(message.deathSource))
                return;

            SendCustom(DeathLinkEvent, JsonSerializer.Serialize(message, JsonOptions));
        }

        public void SendInventoryClaimRequest(InventoryClaimRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.sceneName) ||
                string.IsNullOrEmpty(request.itemPath) ||
                string.IsNullOrWhiteSpace(request.senderId))
                return;

            SendCustom(InventoryClaimRequestEvent, JsonSerializer.Serialize(request, JsonOptions));
        }

        public void SendInventoryClaimResult(InventoryClaimResult result)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.sceneName) ||
                string.IsNullOrWhiteSpace(result.itemPath))
                return;

            SendCustom(InventoryClaimResultEvent, JsonSerializer.Serialize(result, JsonOptions));
        }

        public void SendInventoryKeyAdded(InventoryKeyChange change)
        {
            if (change == null || string.IsNullOrEmpty(change.sceneName) ||
                string.IsNullOrEmpty(change.itemPath))
                return;

            SendCustom(InventoryKeyAddedEvent, JsonSerializer.Serialize(change, JsonOptions));
        }

        public void SendInventoryConsumeRequest(InventoryConsumeRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.sceneName) ||
                string.IsNullOrEmpty(request.itemPath))
                return;

            SendCustom(InventoryConsumeRequestEvent, JsonSerializer.Serialize(request, JsonOptions));
        }

        public void SendInventoryKeyRemoved(InventoryKeyRemoval removal)
        {
            if (removal == null || string.IsNullOrWhiteSpace(removal.sceneName) ||
                string.IsNullOrWhiteSpace(removal.itemPath) ||
                string.IsNullOrWhiteSpace(removal.senderId))
                return;

            SendCustom(InventoryKeyRemovedEvent, JsonSerializer.Serialize(removal, JsonOptions));
        }

        public void SendCustom(string eventName, string jsonPayload)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            OutgoingRpc?.Invoke(eventName, jsonPayload);
        }

        public void ReceiveCustom(string eventName, string jsonPayload)
        {
            ReceiveCustom(eventName, jsonPayload, -1);
        }

        public void ReceiveCustom(string eventName, string jsonPayload, int relayEndpointIndex)
        {
            if (string.IsNullOrEmpty(eventName)) return;

            bool isMitaPriorityEvent = IsMitaPriorityEvent(eventName, jsonPayload);
            string mitaStateKey = GetLiveMitaStateKey(eventName, jsonPayload);
            int queueCount;
            lock (queueLock)
            {
                QueuedRpc rpc = new QueuedRpc(eventName, jsonPayload, ++nextIncomingSequence,
                                              relayEndpointIndex);
                if (!isMitaPriorityEvent)
                {
                    incomingQueue.Enqueue(rpc);
                }
                else if (string.IsNullOrEmpty(mitaStateKey))
                {
                    // Keep Mita control events and full snapshots ordered in
                    // the same fast lane as live states, while preserving all
                    // of them (only superseded live poses are coalesced).
                    incomingMitaStateQueue.AddLast(rpc);
                }
                else
                {
                    LinkedListNode<QueuedRpc> previous;
                    if (pendingMitaStates.TryGetValue(mitaStateKey, out previous))
                    {
                        incomingMitaStateQueue.Remove(previous);
                        coalescedMitaStateCount++;
                    }

                    LinkedListNode<QueuedRpc> latest = incomingMitaStateQueue.AddLast(rpc);
                    pendingMitaStates[mitaStateKey] = latest;
                }
                queueCount = incomingQueue.Count + incomingMitaStateQueue.Count;
            }

            if (eventName == MiSideMultiplayer.Runtime.Lobby.SessionEvents.HostLeft)
            {
                DiagnosticLog.Verbose(
                    "[HOST-LEFT TRACE 2/5] RpcDispatcher.ReceiveCustom enqueued host-left " +
                    "(queueCount=" + queueCount + ", payloadLength=" +
                    (jsonPayload == null ? 0 : jsonPayload.Length) + ").");
            }
        }

        public void Tick()
        {
            while (TryDequeue(out QueuedRpc rpc))
            {
                if (rpc.EventName == MiSideMultiplayer.Runtime.Lobby.SessionEvents.HostLeft)
                {
                    DiagnosticLog.Verbose(
                        "[HOST-LEFT TRACE 3/5] RpcDispatcher.Tick dequeued host-left; " +
                        "calling DispatchIncomingNow.");
                }
                DispatchIncomingNow(rpc.EventName, rpc.JsonPayload, rpc.RelayEndpointIndex);
            }

            if (Time.unscaledTime >= nextMitaQueueLogTime)
            {
                long coalesced;
                lock (queueLock)
                {
                    coalesced = coalescedMitaStateCount;
                    coalescedMitaStateCount = 0;
                }

                if (coalesced > 0)
                {
                    nextMitaQueueLogTime = Time.unscaledTime + 5f;
                    DiagnosticLog.Info("RpcDispatcher: Mita receive lane coalesced " + coalesced +
                                       " stale live pose update(s); only the latest queued pose per Mita was kept.");
                }
            }
        }

        private bool TryDequeue(out QueuedRpc rpc)
        {
            lock (queueLock)
            {
                if (incomingQueue.Count == 0 && incomingMitaStateQueue.Count == 0)
                {
                    rpc = default(QueuedRpc);
                    return false;
                }

                LinkedListNode<QueuedRpc> firstMita = incomingMitaStateQueue.First;
                if (firstMita != null)
                {
                    rpc = firstMita.Value;
                    incomingMitaStateQueue.RemoveFirst();
                    string key = GetLiveMitaStateKey(rpc.EventName, rpc.JsonPayload);
                    LinkedListNode<QueuedRpc> pending;
                    if (!string.IsNullOrEmpty(key) &&
                        pendingMitaStates.TryGetValue(key, out pending) &&
                        ReferenceEquals(pending, firstMita))
                        pendingMitaStates.Remove(key);
                }
                else
                {
                    rpc = incomingQueue.Dequeue();
                }
                return true;
            }
        }

        private static bool IsMitaPriorityEvent(string eventName, string jsonPayload)
        {
            return eventName == MitaStateEvent ||
                   eventName == MitaEventSync.MitaRpcEvent ||
                   eventName == MitaSnapshotSync.RequestEvent ||
                   eventName == MitaSnapshotSync.CompleteEvent ||
                   (eventName == MinigameSignalEvent &&
                    string.Equals(ExtractJsonString(jsonPayload, "kind"),
                                  "card-mita-state", StringComparison.Ordinal));
        }

        private static string GetLiveMitaStateKey(string eventName, string jsonPayload)
        {
            if (eventName != MitaStateEvent && eventName != MinigameSignalEvent)
                return null;
            if (string.IsNullOrEmpty(jsonPayload) ||
                (eventName == MitaStateEvent &&
                 jsonPayload.IndexOf("\"isFullSnapshot\":true", StringComparison.Ordinal) >= 0))
                return null;

            if (eventName == MinigameSignalEvent &&
                !string.Equals(ExtractJsonString(jsonPayload, "kind"),
                               "card-mita-state", StringComparison.Ordinal))
                return null;

            string senderId = ExtractJsonString(jsonPayload, "senderId");
            string mitaPath = ExtractJsonString(jsonPayload, "mitaPath");
            if (string.IsNullOrEmpty(mitaPath)) return null;
            return (senderId ?? string.Empty) + "\u001f" + mitaPath;
        }

        private static string ExtractJsonString(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldName)) return null;
            string marker = "\"" + fieldName + "\":\"";
            int valueStart = json.IndexOf(marker, StringComparison.Ordinal);
            if (valueStart < 0) return null;
            valueStart += marker.Length;

            bool escaped = false;
            for (int i = valueStart; i < json.Length; i++)
            {
                char c = json[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '"')
                    return json.Substring(valueStart, i - valueStart);
            }

            return null;
        }

        private void DispatchIncomingNow(string eventName, string jsonPayload, int relayEndpointIndex)
        {
            DiagnosticLog.Verbose("RpcDispatcher: dispatching '" + eventName + "'.");

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
                if (eventName == ChatMessageEvent)
                {
                    ChatMessagePayload message = JsonSerializer.Deserialize<ChatMessagePayload>(jsonPayload, JsonOptions);
                    if (message != null) ChatMessageReceived?.Invoke(message);
                    return;
                }
                if (eventName == ChatSystemEvent)
                {
                    ChatSystemMessagePayload message = JsonSerializer.Deserialize<ChatSystemMessagePayload>(jsonPayload, JsonOptions);
                    if (message != null) ChatSystemMessageReceived?.Invoke(message);
                    return;
                }
                if (eventName == ServerResponseEvent)
                {
                    ServerResponsePayload response = JsonSerializer.Deserialize<ServerResponsePayload>(jsonPayload, JsonOptions);
                    if (response != null) ServerResponseReceived?.Invoke(response);
                    return;
                }
                if (eventName == DeathLinkEvent)
                {
                    SharedLifeMessage message = JsonSerializer.Deserialize<SharedLifeMessage>(jsonPayload, JsonOptions);
                    if (message != null) DeathLinkReceived?.Invoke(message);
                    return;
                }
                if (eventName == InventoryClaimResultEvent)
                {
                    InventoryClaimResult result = JsonSerializer.Deserialize<InventoryClaimResult>(jsonPayload, JsonOptions);
                    if (result != null) InventoryClaimResultReceived?.Invoke(result);
                    return;
                }
                if (eventName == InventoryKeyAddedEvent)
                {
                    InventoryKeyChange change = JsonSerializer.Deserialize<InventoryKeyChange>(jsonPayload, JsonOptions);
                    if (change != null) InventoryKeyAddedReceived?.Invoke(change);
                    return;
                }
                if (eventName == InventoryConsumeResultEvent)
                {
                    InventoryConsumeResult result = JsonSerializer.Deserialize<InventoryConsumeResult>(jsonPayload, JsonOptions);
                    if (result != null) InventoryConsumeResultReceived?.Invoke(result);
                    return;
                }
                if (eventName == InventoryKeyRemovedEvent)
                {
                    InventoryKeyRemoval removal = JsonSerializer.Deserialize<InventoryKeyRemoval>(jsonPayload, JsonOptions);
                    if (removal != null) InventoryKeyRemovedReceived?.Invoke(removal);
                    return;
                }
                if (eventName == InventorySnapshotEvent)
                {
                    InventorySnapshot snapshot = JsonSerializer.Deserialize<InventorySnapshot>(jsonPayload, JsonOptions);
                    if (snapshot != null) InventorySnapshotReceived?.Invoke(snapshot);
                    return;
                }

                var custom = CustomMessageReceived;
                CustomMessageReceivedFromRelay?.Invoke(eventName, jsonPayload, relayEndpointIndex);
                if (eventName == MiSideMultiplayer.Runtime.Lobby.SessionEvents.HostLeft)
                {
                    int subscriberCount = custom == null ? 0 : custom.GetInvocationList().Length;
                    DiagnosticLog.Verbose(
                        "[HOST-LEFT TRACE 4/5] DispatchIncomingNow reached CustomMessageReceived " +
                        "(subscriberCount=" + subscriberCount + ").");

                    if (custom == null)
                    {
                        DiagnosticLog.Verbose(
                            "[HOST-LEFT TRACE 4/5] No CustomMessageReceived subscribers; host-left dispatch ends here.");
                    }
                    else
                    {
                        custom(eventName, jsonPayload);
                        DiagnosticLog.Verbose(
                            "[HOST-LEFT TRACE 4/5] All CustomMessageReceived handlers returned for host-left.");
                    }
                }
                else if (custom != null)
                {
                    custom(eventName, jsonPayload);
                }
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
            public readonly long Sequence;
            public readonly int RelayEndpointIndex;
            public QueuedRpc(string eventName, string jsonPayload, long sequence, int relayEndpointIndex)
            {
                EventName = eventName;
                JsonPayload = jsonPayload;
                Sequence = sequence;
                RelayEndpointIndex = relayEndpointIndex;
            }
        }
    }
}
