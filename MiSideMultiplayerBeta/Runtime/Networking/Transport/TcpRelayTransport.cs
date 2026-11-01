using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MiSideMultiplayer
{
    public enum RelayFailureKind
    {
        ConnectionError,
        ConnectionReset,
        TimedOut,
        ConnectionClosed
    }

    public sealed class TcpRelayTransport : IDisposable
    {
        private readonly RpcDispatcher dispatcher;
        private readonly string localPlayerId;
        private readonly string roomName;
        private readonly string host;
        private readonly int port;
        private readonly int relayEndpointIndex;
        private readonly bool isEnabled;
        private readonly Func<string> displayNameProvider;
        private readonly object writerLock = new object();
        private readonly object outgoingLock = new object();
        private readonly LinkedList<RelayEnvelope> outgoingQueue = new LinkedList<RelayEnvelope>();
        private readonly LinkedList<RelayEnvelope> priorityOutgoingQueue =
            new LinkedList<RelayEnvelope>();
        private readonly AutoResetEvent outgoingSignal = new AutoResetEvent(false);
        private readonly object logLock    = new object();
        private readonly Queue<LogEntry>   pendingLogs = new Queue<LogEntry>();
        private readonly Queue<RelayFailureKind> pendingFailures = new Queue<RelayFailureKind>();
        private readonly object failureLock = new object();
        private readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        private Thread    workerThread;
        private Thread    outgoingThread;
        private volatile bool isRunning;
        private volatile bool isConnected;
        private volatile bool serverHandshakeComplete;
        private TcpClient    client;
        private StreamWriter writer;
        private DateTime     nextRetryLogUtc = DateTime.MinValue;
        private bool loggedFirstOutboundState;
        private bool loggedFirstInboundState;
        private bool loggedIgnoredSameId;
        private bool loggedFirstMitaQueued;
        private bool loggedOutgoingQueueOverflow;
        private int  consecutiveFailures;
        private bool failureReported;
        private long lastServerKeepaliveTimestamp;

        private int    currentBackoffMs = 1000;
        private const int MinBackoffMs   = 1000;
        private const int MaxBackoffMs   = 30000;
        private const int MaxOutgoingPackets = 4096;
        private const int MaxNormalOutgoingPackets = 3072;

        private const string SessionResponseEventName = "miside.session.response";
        private const string ServerPingEventName = "miside.transport.ping";
        private const int RelayReadTimeoutMs = 8000;
        // Server keepalives arrive every two seconds. Warn after roughly one
        // missed heartbeat plus jitter, rather than waiting for multiple lost
        // heartbeats and hiding shorter but noticeable stalls.
        private const long LowConnectionThresholdMilliseconds = 3500;


        public bool IsConnected { get { return isConnected; } }
        public bool IsServerReady { get { return isConnected && serverHandshakeComplete; } }
        public bool IsLowConnection
        {
            get
            {
                if (!isConnected) return false;
                long lastPing = Interlocked.Read(ref lastServerKeepaliveTimestamp);
                return lastPing <= 0 || Environment.TickCount64 - lastPing >= LowConnectionThresholdMilliseconds;
            }
        }
        public event Action<RelayFailureKind> ConnectionFailed;

        public TcpRelayTransport(
            RpcDispatcher dispatcher,
            string localPlayerId,
            string roomName,
            string host,
            int port,
            bool isEnabled,
            Func<string> displayNameProvider)
        {
            this.dispatcher    = dispatcher;
            this.localPlayerId = localPlayerId;
            this.roomName      = string.IsNullOrEmpty(roomName) ? "default" : roomName;
            this.host          = string.IsNullOrEmpty(host)     ? "127.0.0.1" : host;
            this.port          = port <= 0                       ? 7777         : port;
            this.relayEndpointIndex = QBRelayFailover.GetIndex(this.host, this.port);
            this.isEnabled     = isEnabled;
            this.displayNameProvider = displayNameProvider;
        }

        public void Start()
        {
            if (!isEnabled)
            {
                EnqueueLog(false, "Networking is DISABLED (EnableNetworking=false).");
                return;
            }

            if (dispatcher == null)
            {
                EnqueueLog(true, "Cannot connect — RpcDispatcher is null.");
                return;
            }

            EnqueueLog(false,
                "[RELAY] Starting transport." +
                "  endpoint='" + host + ":" + port + "'" +
                "  room='" + roomName + "'" +
                "  localId='" + localPlayerId + "'");

            dispatcher.OutgoingRpc += OnOutgoingRpc;
            isRunning = true;
            serverHandshakeComplete = false;

            outgoingThread = new Thread(OutgoingLoop);
            outgoingThread.IsBackground = true;
            outgoingThread.Name = "MiSideMultiplayer.RelayWriter";
            outgoingThread.Start();

            workerThread           = new Thread(WorkerLoop);
            workerThread.IsBackground = true;
            workerThread.Name      = "MiSideMultiplayer.Relay";
            workerThread.Start();
        }

        public void Tick()
        {
            LogEntry entry;
            while (TryDequeueLog(out entry))
            {
                if (entry.IsWarning)
                    DiagnosticLog.Warning(entry.Message);
                else
                    DiagnosticLog.Info(entry.Message);
            }

            RelayFailureKind failure;
            while (TryDequeueFailure(out failure))
            {
                Action<RelayFailureKind> handler = ConnectionFailed;
                if (handler != null) handler(failure);
            }
        }

        public void Dispose()
        {
            isRunning = false;
            outgoingSignal.Set();
            if (dispatcher != null)
                dispatcher.OutgoingRpc -= OnOutgoingRpc;

            CloseConnection();
            EnqueueLog(false, "[RELAY] Transport disposed.");
        }

        private void WorkerLoop()
        {
            while (isRunning)
            {
                bool firstAttemptOfStreak = consecutiveFailures == 0;

                if (firstAttemptOfStreak)
                    EnqueueLog(false, "[RELAY] Attempting connect → " + host + ":" + port);

                try
                {
                    RunConnection();
                    consecutiveFailures = 0;
                    currentBackoffMs    = MinBackoffMs;
                }
                catch (SocketException sex)
                {
                    consecutiveFailures++;
                    LogRetry(
                        "[RELAY] Connect to " + host + ":" + port +
                        " FAILED (#" + consecutiveFailures + ")" +
                        "  socketError=" + sex.SocketErrorCode +
                        "  errCode=" + sex.ErrorCode +
                        "  msg='" + sex.Message + "'");
                    BumpBackoff();
                    ReportFailure(IsConnectionReset(sex) ? RelayFailureKind.ConnectionReset : RelayFailureKind.ConnectionError);
                }
                catch (AggregateException aex)
                {
                    consecutiveFailures++;
                    string inner = aex.InnerException != null
                        ? aex.InnerException.GetType().Name + ": " + aex.InnerException.Message
                        : aex.Message;
                    LogRetry(
                        "[RELAY] Connect to " + host + ":" + port +
                        " FAILED (#" + consecutiveFailures + ")" +
                        "  aggregate: " + inner);
                    BumpBackoff();
                    ReportFailure(RelayFailureKind.ConnectionError);
                }
                catch (TimeoutException tex)
                {
                    consecutiveFailures++;
                    LogRetry("[RELAY] Connect to " + host + ":" + port +
                             " TIMED OUT (#" + consecutiveFailures + ")  " + tex.Message);
                    BumpBackoff();
                    ReportFailure(RelayFailureKind.TimedOut);
                }
                catch (IOException ioex)
                {
                    consecutiveFailures++;
                    LogRetry("[RELAY] Connection stopped responding (#" +
                             consecutiveFailures + ")  " + ioex.Message);
                    BumpBackoff();
                    ReportFailure(IsConnectionReset(ioex) ? RelayFailureKind.ConnectionReset : RelayFailureKind.TimedOut);
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    LogRetry(
                        "[RELAY] Connect to " + host + ":" + port +
                        " FAILED (#" + consecutiveFailures + ")" +
                        "  " + ex.GetType().Name + ": " + ex.Message);
                    BumpBackoff();
                    ReportFailure(RelayFailureKind.ConnectionError);
                }
                finally
                {
                    ClearConnection();
                }

                if (isRunning)
                    SleepWhileRunning(currentBackoffMs);
            }
        }

        private void BumpBackoff()
        {
            currentBackoffMs *= 2;
            if (currentBackoffMs > MaxBackoffMs) currentBackoffMs = MaxBackoffMs;
        }

        private void RunConnection()
        {
            TcpClient tcpClient = new TcpClient();
            tcpClient.NoDelay = true;

            IAsyncResult ar = tcpClient.BeginConnect(host, port, null, null);
            bool completed = ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            if (!completed)
            {
                tcpClient.Close();
                throw new TimeoutException("Timed out after 5s connecting to " + host + ":" + port + ".");
            }

            tcpClient.EndConnect(ar);

            NetworkStream  stream       = tcpClient.GetStream();
            // The relay pings every two seconds. If that stops, leave the
            // session promptly rather than leaving players frozen in it.
            stream.ReadTimeout = RelayReadTimeoutMs;
            StreamReader   reader       = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            StreamWriter   streamWriter = new StreamWriter(stream, new UTF8Encoding(false), 4096, true);
            streamWriter.NewLine    = "\n";
            streamWriter.AutoFlush  = true;

            // The hello must be the first line on every connection. Write it
            // from this worker before exposing the stream to the async queue.
            SendHello(streamWriter);

            lock (writerLock)
            {
                client      = tcpClient;
                writer      = streamWriter;
                isConnected = true;
                Interlocked.Exchange(ref lastServerKeepaliveTimestamp, Environment.TickCount64);
            }
            outgoingSignal.Set();

            EnqueueLog(false,
                "[RELAY] CONNECTED to " + host + ":" + port +
                "  room='" + roomName + "'. Sending hello...");

            while (isRunning && tcpClient.Connected)
            {
                string line = reader.ReadLine();
                if (line == null)
                {
                    EnqueueLog(true, "[RELAY] Server closed the connection (ReadLine returned null).");
                    ReportFailure(RelayFailureKind.ConnectionClosed);
                    break;
                }

                HandleIncomingLine(line);
            }

            EnqueueLog(true, "[RELAY] Disconnected from " + host + ":" + port + ".");
        }

        private void OnOutgoingRpc(string eventName, string jsonPayload)
        {
            if (!isRunning || string.IsNullOrEmpty(eventName))
                return;

            RelayEnvelope envelope = new RelayEnvelope();
            envelope.roomId    = roomName;
            envelope.roomName  = roomName;
            envelope.senderId  = localPlayerId;
            envelope.eventName = eventName;
            envelope.payload   = jsonPayload ?? string.Empty;
            envelope.displayName = GetDisplayName();

            SendEnvelope(envelope);
        }

        private void SendHello(StreamWriter streamWriter)
        {
            RelayEnvelope env = new RelayEnvelope();
            env.roomId    = roomName;
            env.roomName  = roomName;
            env.senderId  = localPlayerId;
            env.eventName = RpcDispatcher.TransportHelloEvent;
            env.payload   = "{}";
            env.displayName = GetDisplayName();
            env.sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            string line = JsonSerializer.Serialize(env, jsonOptions);
            streamWriter.WriteLine(line);
        }

        private void SendEnvelope(RelayEnvelope envelope)
        {
            EnqueueOutgoing(envelope);
        }

        private void EnqueueOutgoing(RelayEnvelope envelope)
        {
            if (envelope == null || !isRunning) return;

            envelope.coalesceKey = GetCoalesceKey(envelope.eventName, envelope.payload);
            string supersededKey = GetSupersededKey(envelope);
            bool highPriority = IsHighPriorityEvent(envelope.eventName);
            lock (outgoingLock)
            {
                if (!string.IsNullOrEmpty(envelope.coalesceKey))
                {
                    RemoveCoalesced(outgoingQueue, envelope.coalesceKey, supersededKey);
                    RemoveCoalesced(priorityOutgoingQueue, envelope.coalesceKey, supersededKey);
                }

                int queuedCount = outgoingQueue.Count + priorityOutgoingQueue.Count;
                if (queuedCount >= MaxOutgoingPackets ||
                    (!highPriority && outgoingQueue.Count >= MaxNormalOutgoingPackets))
                {
                    if (!loggedOutgoingQueueOverflow)
                    {
                        loggedOutgoingQueueOverflow = true;
                        EnqueueLog(true,
                            "[RELAY] Outgoing queue reached its bounded capacity; dropping a packet to bound memory.");
                    }
                    return;
                }

                if (highPriority) priorityOutgoingQueue.AddLast(envelope);
                else outgoingQueue.AddLast(envelope);
            }

            if (envelope.eventName == RpcDispatcher.MitaStateEvent && !loggedFirstMitaQueued)
            {
                loggedFirstMitaQueued = true;
                EnqueueLog(false,
                    "[RELAY] Mita state snapshots are queued for background TCP writes.");
            }
            outgoingSignal.Set();
        }

        private void OutgoingLoop()
        {
            while (isRunning)
            {
                outgoingSignal.WaitOne(250);
                while (isRunning)
                {
                    RelayEnvelope envelope = DequeueOutgoing();
                    if (envelope == null) break;

                    StreamWriter currentWriter;
                    lock (writerLock) currentWriter = writer;
                    if (currentWriter == null)
                    {
                        RequeueOutgoing(envelope);
                        break;
                    }

                    try
                    {
                        string line = JsonSerializer.Serialize(envelope, jsonOptions);
                        lock (writerLock)
                        {
                            if (writer == null)
                            {
                                RequeueOutgoing(envelope);
                                break;
                            }

                            writer.WriteLine(line);
                            if (envelope.eventName == RpcDispatcher.PlayerStateEvent &&
                                !loggedFirstOutboundState)
                            {
                                loggedFirstOutboundState = true;
                                EnqueueLog(false,
                                    "[RELAY] Sent first local player state packet to " +
                                    host + ":" + port + ".");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        EnqueueLog(true,
                            "[RELAY] Background send failed (connection lost?): " + ex.Message + " — will reconnect.");
                        ReportFailure(IsConnectionReset(ex) ? RelayFailureKind.ConnectionReset : RelayFailureKind.ConnectionClosed);
                        CloseConnection();
                        break;
                    }
                }
            }
        }

        private RelayEnvelope DequeueOutgoing()
        {
            lock (outgoingLock)
            {
                LinkedList<RelayEnvelope> queue = priorityOutgoingQueue.Count > 0
                    ? priorityOutgoingQueue
                    : outgoingQueue;
                if (queue.First == null) return null;
                RelayEnvelope envelope = queue.First.Value;
                queue.RemoveFirst();
                return envelope;
            }
        }

        private void RequeueOutgoing(RelayEnvelope envelope)
        {
            if (envelope == null) return;
            lock (outgoingLock)
            {
                if (!string.IsNullOrEmpty(envelope.coalesceKey))
                {
                    if (ContainsCoalesceKey(outgoingQueue, envelope.coalesceKey) ||
                        ContainsCoalesceKey(priorityOutgoingQueue, envelope.coalesceKey))
                        return;
                }

                if (IsHighPriorityEvent(envelope.eventName))
                    priorityOutgoingQueue.AddFirst(envelope);
                else
                    outgoingQueue.AddFirst(envelope);
            }
        }

        private static void RemoveCoalesced(LinkedList<RelayEnvelope> queue,
                                            string coalesceKey, string supersededKey)
        {
            LinkedListNode<RelayEnvelope> node = queue.First;
            while (node != null)
            {
                LinkedListNode<RelayEnvelope> next = node.Next;
                if (string.Equals(node.Value.coalesceKey, coalesceKey, StringComparison.Ordinal) ||
                    (!string.IsNullOrEmpty(supersededKey) &&
                     string.Equals(node.Value.coalesceKey, supersededKey, StringComparison.Ordinal)))
                    queue.Remove(node);
                node = next;
            }
        }

        private static bool ContainsCoalesceKey(LinkedList<RelayEnvelope> queue, string coalesceKey)
        {
            for (LinkedListNode<RelayEnvelope> node = queue.First;
                 node != null; node = node.Next)
                if (string.Equals(node.Value.coalesceKey, coalesceKey, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool IsHighPriorityEvent(string eventName)
        {
            return eventName == "miside.minigame.signal" ||
                   eventName == RpcDispatcher.InventoryClaimRequestEvent ||
                   eventName == RpcDispatcher.InventoryClaimResultEvent ||
                   eventName == RpcDispatcher.InventoryKeyAddedEvent ||
                   eventName == RpcDispatcher.InventoryKeyRemovedEvent ||
                   eventName == RpcDispatcher.Scene5BasementDoorEvent ||
                   eventName == RpcDispatcher.MitaStateEvent ||
                   eventName == MitaEventSync.MitaRpcEvent ||
                   eventName == MitaSnapshotSync.RequestEvent ||
                   eventName == MitaSnapshotSync.CompleteEvent;
        }

        private static string GetCoalesceKey(string eventName, string payload)
        {
            if (eventName == RpcDispatcher.PlayerStateEvent ||
                eventName == RpcDispatcher.BoneSyncEvent)
                return eventName;

            if (eventName == "miside.minigame.signal")
            {
                string kind = ExtractJsonString(payload, "kind");
                string senderId = ExtractJsonString(payload, "senderId");
                if (string.IsNullOrEmpty(senderId)) return null;
                if (string.Equals(kind, "card-mita-state", StringComparison.Ordinal))
                {
                    string mitaPath = ExtractJsonString(payload, "mitaPath");
                    return string.IsNullOrEmpty(mitaPath)
                        ? null
                        : eventName + ":mita:" + senderId + ":" + mitaPath;
                }
                if (string.Equals(kind, "card-state", StringComparison.Ordinal))
                    return eventName + ":card-state:" + senderId;
                return null;
            }

            if (eventName == RpcDispatcher.MitaStateEvent)
            {
                string mitaPath = ExtractJsonString(payload, "mitaPath");
                if (string.IsNullOrEmpty(mitaPath)) return null;
                bool fullSnapshot = !string.IsNullOrEmpty(payload) &&
                    payload.IndexOf("\"isFullSnapshot\":true", StringComparison.Ordinal) >= 0;
                if (!fullSnapshot) return eventName + ":" + mitaPath;

                string snapshotId = ExtractJsonString(payload, "snapshotId");
                return eventName + ":snapshot:" + (snapshotId ?? string.Empty) + ":" + mitaPath;
            }

            return null;
        }

        private static string GetSupersededKey(RelayEnvelope envelope)
        {
            if (envelope == null || envelope.eventName != RpcDispatcher.MitaStateEvent ||
                string.IsNullOrEmpty(envelope.coalesceKey))
                return null;

            string snapshotPrefix = RpcDispatcher.MitaStateEvent + ":snapshot:";
            if (!envelope.coalesceKey.StartsWith(snapshotPrefix, StringComparison.Ordinal))
                return null;

            int pathSeparator = envelope.coalesceKey.IndexOf(':', snapshotPrefix.Length);
            if (pathSeparator < 0 || pathSeparator == envelope.coalesceKey.Length - 1)
                return null;
            string mitaPath = envelope.coalesceKey.Substring(pathSeparator + 1);
            return RpcDispatcher.MitaStateEvent + ":" + mitaPath;
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

        private void HandleIncomingLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            try
            {
                RelayEnvelope envelope =
                    JsonSerializer.Deserialize<RelayEnvelope>(line, jsonOptions);

                if (envelope == null)
                    return;

                if (envelope.eventName == MiSideMultiplayer.Runtime.Lobby.SessionEvents.HostLeft)
                {
                    DiagnosticLog.Verbose(
                        "[HOST-LEFT TRACE 1/5] Transport deserialized host-left envelope: " +
                        "senderId='" + (envelope.senderId ?? "?") + "', " +
                        "room='" + (envelope.roomName ?? "?") + "', " +
                        "localPlayerId='" + localPlayerId + "', localRoom='" + roomName + "'.");
                }

                bool bypassRoom =
                    envelope.eventName == RpcDispatcher.ServerResponseEvent ||
                    envelope.eventName == SessionResponseEventName;

                if (string.IsNullOrWhiteSpace(envelope.roomId) || !IsSameRoom(envelope.roomId) ||
                    (!bypassRoom && !IsSameRoom(envelope.roomName)))
                {
                    if (envelope.eventName == MiSideMultiplayer.Runtime.Lobby.SessionEvents.HostLeft)
                    {
                        DiagnosticLog.Verbose(
                            "[HOST-LEFT TRACE 1/5] Transport dropped host-left envelope: room mismatch.");
                    }
                    DiagnosticLog.Verbose("[RELAY] Dropped cross-room packet '" + envelope.eventName +
                                          "' (was '" + envelope.roomName + "', we are '" + roomName + "').");
                    return;
                }

                // The relay sends this every two seconds. Use its arrival time
                // as a main-thread-safe connection freshness signal; a delayed
                // or missing heartbeat warns before the socket timeout fires.
                if (envelope.eventName == ServerPingEventName)
                {
                    if (string.Equals(envelope.senderId, "SERVER", StringComparison.Ordinal))
                    {
                        Interlocked.Exchange(ref lastServerKeepaliveTimestamp, Environment.TickCount64);
                        serverHandshakeComplete = true;
                    }
                    return;
                }

                if (!string.IsNullOrEmpty(envelope.senderId) &&
                    envelope.senderId == localPlayerId)
                {
                    if (envelope.eventName == MiSideMultiplayer.Runtime.Lobby.SessionEvents.HostLeft)
                    {
                        DiagnosticLog.Verbose(
                            "[HOST-LEFT TRACE 1/5] Transport dropped host-left envelope: senderId matches localPlayerId.");
                    }
                    if (!loggedIgnoredSameId &&
                        envelope.eventName == RpcDispatcher.PlayerStateEvent)
                    {
                        loggedIgnoredSameId = true;
                        EnqueueLog(true,
                            "[RELAY] Ignored relay packet with same LocalPlayerId '" +
                            localPlayerId + "'.");
                    }
                    return;
                }

                if (envelope.eventName == RpcDispatcher.TransportHelloEvent)
                    return;

                if (envelope.eventName == RpcDispatcher.PlayerStateEvent &&
                    !loggedFirstInboundState)
                {
                    loggedFirstInboundState = true;
                    EnqueueLog(false,
                        "[RELAY] Received first remote player state packet from " +
                        host + ":" + port + "  senderId='" + (envelope.senderId ?? "?") + "'.");
                }

                DiagnosticLog.Verbose("[RELAY] Incoming '" + envelope.eventName +
                                      "' from '" + (envelope.senderId ?? "?") + "'.");

                if (envelope.eventName == MiSideMultiplayer.Runtime.Lobby.SessionEvents.HostLeft)
                {
                    DiagnosticLog.Verbose(
                        "[HOST-LEFT TRACE 1/5] Transport accepted host-left envelope; " +
                        "calling RpcDispatcher.ReceiveCustom.");
                }

                dispatcher.ReceiveCustom(envelope.eventName, envelope.payload, relayEndpointIndex);
            }
            catch (Exception ex)
            {
                // Include a snippet of the offending line so a parse failure
                // can actually be diagnosed (which event/field was involved)
                // instead of just knowing *that* one happened. This does not
                // affect the read loop: one bad line is skipped and we keep
                // reading, so a single corrupted packet should never itself
                // desync the whole session — but if a critical event (e.g.
                // SessionStart) is the one that gets mangled, the affected
                // side will otherwise hang with no way to tell why.
                string preview = line;
                if (preview != null && preview.Length > 200)
                    preview = preview.Substring(0, 200) + "...(truncated, " + line.Length + " chars total)";

                EnqueueLog(true, "[RELAY] Failed to parse packet: " + ex.Message +
                                 " | raw='" + (preview ?? "<null>") + "'");
            }
        }

        private bool IsSameRoom(string remoteRoom)
        {
            if (string.IsNullOrEmpty(remoteRoom))
                return roomName == "default";
            return string.Equals(remoteRoom, roomName, StringComparison.OrdinalIgnoreCase);
        }

        private string GetDisplayName()
        {
            string displayName = displayNameProvider != null ? displayNameProvider() : null;
            return string.IsNullOrWhiteSpace(displayName) ? "Player" : displayName.Trim();
        }

        private void CloseConnection()
        {
            lock (writerLock)
            {
                if (client != null)
                    client.Close();
            }
        }

        private void ClearConnection()
        {
            lock (writerLock)
            {
                if (writer != null) { writer.Dispose(); writer = null; }
                if (client != null) { client.Close();   client = null; }
                isConnected = false;
                serverHandshakeComplete = false;
            }
        }

        private void LogRetry(string message)
        {
            DateTime now = DateTime.UtcNow;
            if (now < nextRetryLogUtc) return;
            nextRetryLogUtc = now.AddSeconds(5);
            EnqueueLog(true, message + "  Will retry in " + currentBackoffMs + "ms.");
        }

        private void SleepWhileRunning(int milliseconds)
        {
            int slept = 0;
            while (isRunning && slept < milliseconds)
            {
                Thread.Sleep(100);
                slept += 100;
            }
        }

        private static bool IsConnectionReset(Exception ex)
        {
            var socket = ex as SocketException;
            if (socket != null)
                return socket.SocketErrorCode == SocketError.ConnectionReset;

            var io = ex as IOException;
            var innerSocket = io != null ? io.InnerException as SocketException : null;
            return innerSocket != null &&
                   innerSocket.SocketErrorCode == SocketError.ConnectionReset;
        }

        private void ReportFailure(RelayFailureKind failure)
        {
            if (!isRunning || failureReported) return;
            failureReported = true;
            lock (failureLock) pendingFailures.Enqueue(failure);
        }

        private bool TryDequeueFailure(out RelayFailureKind failure)
        {
            lock (failureLock)
            {
                if (pendingFailures.Count == 0)
                {
                    failure = RelayFailureKind.ConnectionError;
                    return false;
                }
                failure = pendingFailures.Dequeue();
                return true;
            }
        }

        private void EnqueueLog(bool isWarning, string message)
        {
            lock (logLock)
                pendingLogs.Enqueue(new LogEntry(isWarning, "[MiSideMultiplayer] " + message));
        }

        private bool TryDequeueLog(out LogEntry entry)
        {
            lock (logLock)
            {
                if (pendingLogs.Count == 0) { entry = default(LogEntry); return false; }
                entry = pendingLogs.Dequeue();
                return true;
            }
        }

        private sealed class RelayEnvelope
        {
            public string roomId;
            public string roomName;
            public string senderId;
            public string eventName;
            public string payload;
            public string displayName;
            public string sceneName;
            [System.Text.Json.Serialization.JsonIgnore]
            public string coalesceKey;
        }

        private struct LogEntry
        {
            public readonly bool   IsWarning;
            public readonly string Message;
            public LogEntry(bool isWarning, string message)
            {
                IsWarning = isWarning;
                Message   = message;
            }
        }
    }
}
