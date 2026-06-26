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
        private readonly object logLock = new object();
        private readonly Queue<LogEntry> pendingLogs = new Queue<LogEntry>();
        private readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        private Thread workerThread;
        private volatile bool isRunning;
        private volatile bool isConnected;
        private TcpClient client;
        private StreamWriter writer;
        private DateTime nextRetryLogUtc = DateTime.MinValue;
        private bool loggedFirstOutboundState;
        private bool loggedFirstInboundState;
        private bool loggedIgnoredSameId;

        public bool IsConnected
        {
            get { return isConnected; }
        }

        public TcpRelayTransport(
            RpcDispatcher dispatcher,
            string localPlayerId,
            string roomName,
            string host,
            int port,
            bool isEnabled)
        {
            this.dispatcher = dispatcher;
            this.localPlayerId = localPlayerId;
            this.roomName = string.IsNullOrEmpty(roomName) ? "default" : roomName;
            this.host = string.IsNullOrEmpty(host) ? "127.0.0.1" : host;
            this.port = port <= 0 ? 7777 : port;
            this.isEnabled = isEnabled;
        }

        public void Start()
        {
            if (!isEnabled)
            {
                EnqueueLog(false, "[MiSideMultiplayer] Networking disabled. Puppets can still be driven by injected RPCs.");
                return;
            }

            if (dispatcher == null)
            {
                EnqueueLog(true, "[MiSideMultiplayer] Relay transport cannot start: RpcDispatcher is null.");
                return;
            }

            dispatcher.OutgoingRpc += OnOutgoingRpc;
            isRunning = true;

            workerThread = new Thread(WorkerLoop);
            workerThread.IsBackground = true;
            workerThread.Name = "MiSideMultiplayer.Relay";
            workerThread.Start();
        }

        public void Tick()
        {
            while (TryDequeueLog(out LogEntry entry))
            {
                if (entry.IsWarning)
                    Debug.LogWarning(entry.Message);
                else
                    Debug.Log(entry.Message);
            }
        }

        public void Dispose()
        {
            if (dispatcher != null)
                dispatcher.OutgoingRpc -= OnOutgoingRpc;

            isRunning = false;
            CloseConnection();

            if (workerThread != null && workerThread.IsAlive)
                workerThread.Join(500);

            Tick();
        }

        private void WorkerLoop()
        {
            while (isRunning)
            {
                try
                {
                    RunConnection();
                }
                catch (Exception ex)
                {
                    LogRetry("Relay connection failed: " + ex.Message);
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
            TcpClient tcpClient = new TcpClient();
            tcpClient.NoDelay = true;

            Task connectTask = tcpClient.ConnectAsync(host, port);
            if (!connectTask.Wait(TimeSpan.FromSeconds(3)))
            {
                tcpClient.Close();
                throw new TimeoutException("Timed out connecting to " + host + ":" + port);
            }

            connectTask.GetAwaiter().GetResult();

            NetworkStream stream = tcpClient.GetStream();
            StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            StreamWriter streamWriter = new StreamWriter(stream, new UTF8Encoding(false), 4096, true);
            streamWriter.NewLine = "\n";
            streamWriter.AutoFlush = true;

            lock (writerLock)
            {
                client = tcpClient;
                writer = streamWriter;
                isConnected = true;
            }

            EnqueueLog(false, "[MiSideMultiplayer] Connected to relay " + host + ":" + port + " room '" + roomName + "'.");
            SendHello();

            while (isRunning && tcpClient.Connected)
            {
                string line = reader.ReadLine();
                if (line == null)
                    break;

                HandleIncomingLine(line);
            }

            reader.Dispose();
        }

        private void OnOutgoingRpc(string eventName, string jsonPayload)
        {
            if (!isRunning || string.IsNullOrEmpty(eventName))
                return;

            RelayEnvelope envelope = new RelayEnvelope();
            envelope.roomName = roomName;
            envelope.senderId = localPlayerId;
            envelope.eventName = eventName;
            envelope.payload = jsonPayload ?? string.Empty;

            SendEnvelope(envelope);
        }

        private void SendHello()
        {
            RelayEnvelope envelope = new RelayEnvelope();
            envelope.roomName = roomName;
            envelope.senderId = localPlayerId;
            envelope.eventName = RpcDispatcher.TransportHelloEvent;
            envelope.payload = "{}";
            SendEnvelope(envelope);
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
                EnqueueLog(true, "[MiSideMultiplayer] Failed to serialize relay envelope: " + ex.Message);
                return;
            }

            lock (writerLock)
            {
                if (writer == null)
                    return;

                try
                {
                    writer.WriteLine(line);
                    if (envelope.eventName == RpcDispatcher.PlayerStateEvent && !loggedFirstOutboundState)
                    {
                        loggedFirstOutboundState = true;
                        EnqueueLog(false, "[MiSideMultiplayer] Sent first player state packet to relay.");
                    }
                }
                catch (Exception ex)
                {
                    EnqueueLog(true, "[MiSideMultiplayer] Relay send failed: " + ex.Message);
                    CloseConnection();
                }
            }
        }

        private void HandleIncomingLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            try
            {
                RelayEnvelope envelope = JsonSerializer.Deserialize<RelayEnvelope>(line, jsonOptions);
                if (envelope == null)
                    return;

                if (!IsSameRoom(envelope.roomName))
                    return;

                if (!string.IsNullOrEmpty(envelope.senderId) && envelope.senderId == localPlayerId)
                {
                    if (!loggedIgnoredSameId && envelope.eventName == RpcDispatcher.PlayerStateEvent)
                    {
                        loggedIgnoredSameId = true;
                        EnqueueLog(true, "[MiSideMultiplayer] Ignored relay player state with the same LocalPlayerId '" + localPlayerId + "'. Change Identity.LocalPlayerId for same-PC testing.");
                    }

                    return;
                }

                if (envelope.eventName == RpcDispatcher.TransportHelloEvent)
                    return;

                if (envelope.eventName == RpcDispatcher.PlayerStateEvent && !loggedFirstInboundState)
                {
                    loggedFirstInboundState = true;
                    EnqueueLog(false, "[MiSideMultiplayer] Received first player state packet from relay.");
                }

                dispatcher.ReceiveCustom(envelope.eventName, envelope.payload);
            }
            catch (Exception ex)
            {
                EnqueueLog(true, "[MiSideMultiplayer] Failed to read relay packet: " + ex.Message);
            }
        }

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
                if (writer != null)
                {
                    writer.Dispose();
                    writer = null;
                }

                if (client != null)
                {
                    client.Close();
                    client = null;
                }

                isConnected = false;
            }
        }

        private void LogRetry(string message)
        {
            DateTime now = DateTime.UtcNow;
            if (now < nextRetryLogUtc)
                return;

            nextRetryLogUtc = now.AddSeconds(5);
            EnqueueLog(true, "[MiSideMultiplayer] " + message + " Retrying...");
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
                pendingLogs.Enqueue(new LogEntry(isWarning, message));
        }

        private bool TryDequeueLog(out LogEntry entry)
        {
            lock (logLock)
            {
                if (pendingLogs.Count == 0)
                {
                    entry = default(LogEntry);
                    return false;
                }

                entry = pendingLogs.Dequeue();
                return true;
            }
        }

        private sealed class RelayEnvelope
        {
            public string roomName;
            public string senderId;
            public string eventName;
            public string payload;
        }

        private struct LogEntry
        {
            public readonly bool IsWarning;
            public readonly string Message;

            public LogEntry(bool isWarning, string message)
            {
                IsWarning = isWarning;
                Message = message;
            }
        }
    }
}
