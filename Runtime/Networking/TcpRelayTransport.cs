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
    public sealed class TcpRelayTransport : IDisposable
    {
        private readonly RpcDispatcher dispatcher;
        private readonly string localPlayerId;
        private readonly string roomName;
        private readonly string host;
        private readonly int port;
        private readonly bool isEnabled;
        private readonly object writerLock = new object();
        private readonly object logLock    = new object();
        private readonly Queue<LogEntry>   pendingLogs = new Queue<LogEntry>();
        private readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        private Thread    workerThread;
        private volatile bool isRunning;
        private volatile bool isConnected;
        private TcpClient    client;
        private StreamWriter writer;
        private DateTime     nextRetryLogUtc = DateTime.MinValue;
        private bool loggedFirstOutboundState;
        private bool loggedFirstInboundState;
        private bool loggedIgnoredSameId;
        private int  consecutiveFailures;

        public bool IsConnected { get { return isConnected; } }

        public TcpRelayTransport(
            RpcDispatcher dispatcher,
            string localPlayerId,
            string roomName,
            string host,
            int port,
            bool isEnabled)
        {
            this.dispatcher    = dispatcher;
            this.localPlayerId = localPlayerId;
            this.roomName      = string.IsNullOrEmpty(roomName) ? "default" : roomName;
            this.host          = string.IsNullOrEmpty(host)     ? "127.0.0.1" : host;
            this.port          = port <= 0                       ? 7777         : port;
            this.isEnabled     = isEnabled;
        }

        public void Start()
        {
            if (!isEnabled)
            {
                EnqueueLog(false,
                    "Networking is DISABLED (EnableNetworking=false). " +
                    "Puppets can still be driven via injected RPCs. " +
                    "Set EnableNetworking=true in BepInEx/config to connect.");
                return;
            }

            if (dispatcher == null)
            {
                EnqueueLog(true,
                    "Cannot connect — RpcDispatcher is null. " +
                    "This is an internal error; please report it.");
                return;
            }

            EnqueueLog(false,
                "Starting TCP relay transport — target: " + host + ":" + port +
                "  room='" + roomName + "'  localId='" + localPlayerId + "'");

            dispatcher.OutgoingRpc += OnOutgoingRpc;
            isRunning = true;

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
                    Debug.LogWarning(entry.Message);
                else
                    Debug.Log(entry.Message);
            }
        }

        public void Dispose()
        {
            isRunning = false;
            if (dispatcher != null)
                dispatcher.OutgoingRpc -= OnOutgoingRpc;

            CloseConnection();
            EnqueueLog(false, "Relay transport disposed.");
        }

        // ── Worker thread ──────────────────────────────────────────────────────
        private void WorkerLoop()
        {
            while (isRunning)
            {
                try
                {
                    RunConnection();
                    // If RunConnection returns cleanly (rare), reset failure counter
                    consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    LogRetry(
                        "Connection to relay " + host + ":" + port +
                        " FAILED (#" + consecutiveFailures + "): " + ex.Message);
                }
                finally
                {
                    ClearConnection();
                }

                SleepWhileRunning(1000);
            }
        }

        private void RunConnection()
        {
            EnqueueLog(false,
                "Attempting to connect to relay server at " + host + ":" + port + " ...");

            TcpClient tcpClient = new TcpClient();
            tcpClient.NoDelay = true;

            Task connectTask = tcpClient.ConnectAsync(host, port);
            if (!connectTask.Wait(TimeSpan.FromSeconds(3)))
            {
                tcpClient.Close();
                throw new TimeoutException(
                    "Timed out after 3s connecting to " + host + ":" + port +
                    ". Is the relay server running?");
            }

            connectTask.GetAwaiter().GetResult();

            NetworkStream  stream       = tcpClient.GetStream();
            StreamReader   reader       = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            StreamWriter   streamWriter = new StreamWriter(stream, new UTF8Encoding(false), 4096, true);
            streamWriter.NewLine    = "\n";
            streamWriter.AutoFlush  = true;

            lock (writerLock)
            {
                client      = tcpClient;
                writer      = streamWriter;
                isConnected = true;
            }

            consecutiveFailures = 0;
            EnqueueLog(false,
                "Connected to relay server at " + host + ":" + port +
                "  room='" + roomName + "'. Sending hello...");

            SendHello();

            while (isRunning && tcpClient.Connected)
            {
                string line = reader.ReadLine();
                if (line == null)
                {
                    EnqueueLog(true,
                        "Relay server at " + host + ":" + port +
                        " closed the connection (ReadLine returned null).");
                    break;
                }

                HandleIncomingLine(line);
            }

            EnqueueLog(true,
                "Disconnected from relay server at " + host + ":" + port + ".");
        }

        // ── Outgoing RPC ───────────────────────────────────────────────────────
        private void OnOutgoingRpc(string eventName, string jsonPayload)
        {
            if (!isRunning || string.IsNullOrEmpty(eventName))
                return;

            RelayEnvelope envelope = new RelayEnvelope();
            envelope.roomName  = roomName;
            envelope.senderId  = localPlayerId;
            envelope.eventName = eventName;
            envelope.payload   = jsonPayload ?? string.Empty;

            SendEnvelope(envelope);
        }

        private void SendHello()
        {
            RelayEnvelope env = new RelayEnvelope();
            env.roomName  = roomName;
            env.senderId  = localPlayerId;
            env.eventName = RpcDispatcher.TransportHelloEvent;
            env.payload   = "{}";
            SendEnvelope(env);
        }

        private void SendEnvelope(RelayEnvelope envelope)
        {
            string line;
            try
            {
                line = JsonSerializer.Serialize(envelope, jsonOptions);
            }
            catch (Exception ex)
            {
                EnqueueLog(true, "Failed to serialize relay envelope: " + ex.Message);
                return;
            }

            lock (writerLock)
            {
                if (writer == null)
                {
                    // Not connected yet — drop silently (WorkerLoop will retry)
                    return;
                }

                try
                {
                    writer.WriteLine(line);

                    if (envelope.eventName == RpcDispatcher.PlayerStateEvent &&
                        !loggedFirstOutboundState)
                    {
                        loggedFirstOutboundState = true;
                        EnqueueLog(false,
                            "Sent first local player state packet to relay " +
                            host + ":" + port + ".");
                    }
                }
                catch (Exception ex)
                {
                    EnqueueLog(true,
                        "Relay send failed (connection lost?): " + ex.Message +
                        " — will reconnect.");
                    CloseConnection();
                }
            }
        }

        // ── Incoming packet handler ────────────────────────────────────────────
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

                if (!IsSameRoom(envelope.roomName))
                    return;

                if (!string.IsNullOrEmpty(envelope.senderId) &&
                    envelope.senderId == localPlayerId)
                {
                    if (!loggedIgnoredSameId &&
                        envelope.eventName == RpcDispatcher.PlayerStateEvent)
                    {
                        loggedIgnoredSameId = true;
                        EnqueueLog(true,
                            "Ignored relay packet with same LocalPlayerId '" + localPlayerId +
                            "'. If testing two clients on one PC, set different " +
                            "Identity.LocalPlayerId values in the BepInEx config.");
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
                        "Received first remote player state packet from relay " +
                        host + ":" + port +
                        "  senderId='" + (envelope.senderId ?? "?") + "'.");
                }

                dispatcher.ReceiveCustom(envelope.eventName, envelope.payload);
            }
            catch (Exception ex)
            {
                EnqueueLog(true, "Failed to parse relay packet: " + ex.Message);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private bool IsSameRoom(string remoteRoom)
        {
            if (string.IsNullOrEmpty(remoteRoom))
                return roomName == "default";
            return string.Equals(remoteRoom, roomName, StringComparison.OrdinalIgnoreCase);
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
            }
        }

        private void LogRetry(string message)
        {
            DateTime now = DateTime.UtcNow;
            if (now < nextRetryLogUtc) return;
            nextRetryLogUtc = now.AddSeconds(5);
            EnqueueLog(true, "[MiSideMultiplayer] " + message + " Will retry in 1s...");
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

        // ── Inner types ───────────────────────────────────────────────────────
        private sealed class RelayEnvelope
        {
            public string roomName;
            public string senderId;
            public string eventName;
            public string payload;
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
