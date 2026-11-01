using System;
using System.Collections.Generic;
using System.Text.Json;
using MiSideMultiplayer.Runtime.Menu;

namespace MiSideMultiplayer.Runtime.Lobby
{
    public static class SessionDirectory
    {
        public const int    CodeLength   = 6;
        public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        public const string RegisterEvent   = "miside.session.register";
        public const string UnregisterEvent = "miside.session.unregister";
        public const string ListEvent       = "miside.session.list";
        public const string ResolveEvent    = "miside.session.resolve";
        public const string JoinEvent       = "miside.session.join";
        public const string ResponseEvent   = "miside.session.response";

        private static RpcDispatcher _subscribedDispatcher;
        private static Action<List<DirectorySessionEntry>> _listCallback;
        private static int _listCallbackEndpointIndex = -1;
        private static Action<DirectoryJoinByCodeResponse> _resolveCallback;
        private static int _resolveCallbackEndpointIndex = -1;
        private static Action<bool> _registerCallback;
        private static Action<bool, string> _registerDetailedCallback;

        public static bool IsValidCodeChar(char c)
        {
            char up = char.ToUpperInvariant(c);
            return CodeAlphabet.IndexOf(up) >= 0;
        }

        public static string GenerateCode()
        {
            var rng = new System.Random();
            char[] buf = new char[CodeLength];
            for (int i = 0; i < buf.Length; i++)
                buf[i] = CodeAlphabet[rng.Next(CodeAlphabet.Length)];
            return new string(buf);
        }

        public static string GenerateRoomId()
        {
            return "s-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        // ── Send methods ────────────────────────────────────────────────────

        public static void RegisterHost(
            string roomId,
            string sessionName,
            SessionVisibility visibility,
            int maxPlayers,
            string code,
            Action<bool> callback)
        {
            RegisterHostCore(roomId, sessionName, visibility, maxPlayers, code, callback, null);
        }

        public static void RegisterHostDetailed(
            string roomId,
            string sessionName,
            SessionVisibility visibility,
            int maxPlayers,
            string code,
            Action<bool, string> callback)
        {
            RegisterHostCore(roomId, sessionName, visibility, maxPlayers, code, null, callback);
        }

        private static void RegisterHostCore(
            string roomId,
            string sessionName,
            SessionVisibility visibility,
            int maxPlayers,
            string code,
            Action<bool> callback,
            Action<bool, string> detailedCallback)
        {
            EnsureSubscribed();

            var dispatcher = MiSideMultiplayerPlugin.GetRpcDispatcher();
            if (dispatcher == null)
            {
                DiagnosticLog.Warning("SessionDirectory: no dispatcher, cannot register.");
                if (callback != null) callback(false);
                if (detailedCallback != null) detailedCallback(false, "no_dispatcher");
                return;
            }

            _registerCallback = callback;
            _registerDetailedCallback = detailedCallback;

            var req = new
            {
                roomId      = roomId,
                sessionName = sessionName,
                maxPlayers  = maxPlayers,
                visibility  = (int)visibility,
                code        = code ?? ""
            };
            string json = JsonSerializer.Serialize(req);
            dispatcher.SendCustom(RegisterEvent, json);
        }

        public static void UnregisterHost(string roomId)
        {
            EnsureSubscribed();
            var dispatcher = MiSideMultiplayerPlugin.GetRpcDispatcher();
            if (dispatcher == null) return;

            string json = JsonSerializer.Serialize(new { roomId = roomId });
            dispatcher.SendCustom(UnregisterEvent, json);
        }

        public static void ListPublicSessions(Action<List<DirectorySessionEntry>> callback)
        {
            EnsureSubscribed();
            _listCallback = callback;
            _listCallbackEndpointIndex = MiSideMultiplayerPlugin.GetCurrentCentralEndpointIndex();

            var dispatcher = MiSideMultiplayerPlugin.GetRpcDispatcher();
            if (dispatcher == null)
            {
                DiagnosticLog.Warning("SessionDirectory: no dispatcher, cannot list.");
                if (callback != null) callback(new List<DirectorySessionEntry>());
                _listCallback = null;
                _listCallbackEndpointIndex = -1;
                return;
            }
            dispatcher.SendCustom(ListEvent, "{}");
        }

        public static void CancelPendingListRequest()
        {
            _listCallback = null;
            _listCallbackEndpointIndex = -1;
        }

        public static void JoinByCode(string code, Action<DirectoryJoinByCodeResponse> callback)
        {
            EnsureSubscribed();
            _resolveCallback = callback;
            _resolveCallbackEndpointIndex = MiSideMultiplayerPlugin.GetCurrentCentralEndpointIndex();

            var dispatcher = MiSideMultiplayerPlugin.GetRpcDispatcher();
            if (dispatcher == null)
            {
                DiagnosticLog.Warning("SessionDirectory: no dispatcher, cannot resolve.");
                if (callback != null) callback(new DirectoryJoinByCodeResponse { found = false });
                _resolveCallback = null;
                _resolveCallbackEndpointIndex = -1;
                return;
            }
            string json = JsonSerializer.Serialize(new { code = code });
            dispatcher.SendCustom(ResolveEvent, json);
        }

        /// <summary>
        /// Tell the server "I'm a joiner in this room" after the transport
        /// reconnects to the target room. Server validates the room exists
        /// and marks us as a non-host member.
        /// </summary>
        public static void SendJoin(string roomId)
        {
            EnsureSubscribed();
            var dispatcher = MiSideMultiplayerPlugin.GetRpcDispatcher();
            if (dispatcher == null) return;

            string json = JsonSerializer.Serialize(new { roomId = roomId });
            dispatcher.SendCustom(JoinEvent, json);
        }

        // ── Response handling ───────────────────────────────────────────────

        private static void EnsureSubscribed()
        {
            var dispatcher = MiSideMultiplayerPlugin.GetRpcDispatcher();
            if (dispatcher == null) return;

            // Runtime settings can recreate RuntimeServices and its dispatcher.
            // Subscribe to the current dispatcher rather than retaining a dead one.
            if (ReferenceEquals(_subscribedDispatcher, dispatcher)) return;

            if (_subscribedDispatcher != null)
                _subscribedDispatcher.CustomMessageReceivedFromRelay -= OnCustomMessage;

            dispatcher.CustomMessageReceivedFromRelay += OnCustomMessage;
            _subscribedDispatcher = dispatcher;
            DiagnosticLog.Verbose("SessionDirectory: subscribed to the current RpcDispatcher.");
        }

        private static void OnCustomMessage(string eventName, string json, int relayEndpointIndex)
        {
            if (eventName != ResponseEvent) return;

            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl)) return;
                    string type = typeEl.GetString();

                    switch (type)
                    {
                        case "session.registered":
                        {
                            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                            string reason = root.TryGetProperty("reason", out var reasonEl)
                                ? reasonEl.GetString() : string.Empty;
                            var detailed = _registerDetailedCallback;
                            _registerDetailedCallback = null;
                            var cb = _registerCallback;
                            _registerCallback = null;
                            if (detailed != null) detailed(ok, reason);
                            else cb?.Invoke(ok);
                            break;
                        }
                        case "session.list.result":
                        {
                            if (_listCallback == null ||
                                (_listCallbackEndpointIndex >= 0 &&
                                 relayEndpointIndex != _listCallbackEndpointIndex))
                            {
                                DiagnosticLog.Verbose("SessionDirectory: ignored stale list response from relay " +
                                    relayEndpointIndex + " (expected " + _listCallbackEndpointIndex + ").");
                                break;
                            }

                            var list = new List<DirectorySessionEntry>();
                            if (root.TryGetProperty("sessions", out var arr) &&
                                arr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var el in arr.EnumerateArray())
                                {
                                    try
                                    {
                                        if (el.ValueKind != JsonValueKind.Object) continue;

                                        string sessionId = el.TryGetProperty("sessionId", out var e1) ? e1.GetString() : null;
                                        if (string.IsNullOrWhiteSpace(sessionId)) continue;

                                        list.Add(new DirectorySessionEntry
                                        {
                                            sessionId       = sessionId,
                                            sessionName     = el.TryGetProperty("sessionName", out var e2) ? e2.GetString() : null,
                                            hostDisplayName = el.TryGetProperty("hostDisplayName", out var e3) ? e3.GetString() : null,
                                            playerCount     = el.TryGetProperty("playerCount", out var e4) ? e4.GetInt32() : 0,
                                            maxPlayers      = el.TryGetProperty("maxPlayers", out var e5) ? e5.GetInt32() : 0,
                                            visibility      = (SessionVisibility)(el.TryGetProperty("visibility", out var e6) ? e6.GetInt32() : 0),
                                            // Bind this listing to the server
                                            // that actually produced the response.
                                            // The transport may reconnect before
                                            // the callback consumes the row.
                                            relayEndpointIndex = relayEndpointIndex
                                        });
                                    }
                                    catch (Exception ex)
                                    {
                                        DiagnosticLog.Warning("SessionDirectory: skipped malformed public-session entry: " + ex.Message);
                                    }
                                }
                            }
                            var cb = _listCallback; _listCallback = null;
                            _listCallbackEndpointIndex = -1;
                            cb?.Invoke(list);
                            break;
                        }
                        case "session.resolve.result":
                        {
                            if (_resolveCallback == null ||
                                (_resolveCallbackEndpointIndex >= 0 &&
                                 relayEndpointIndex != _resolveCallbackEndpointIndex))
                            {
                                DiagnosticLog.Verbose("SessionDirectory: ignored stale code response from relay " +
                                    relayEndpointIndex + " (expected " + _resolveCallbackEndpointIndex + ").");
                                break;
                            }

                            bool found = root.TryGetProperty("found", out var fEl) && fEl.GetBoolean();
                            var resp = new DirectoryJoinByCodeResponse
                            {
                                found = found,
                                relayEndpointIndex = relayEndpointIndex
                            };
                            if (found)
                            {
                                resp.sessionId = root.TryGetProperty("sessionId", out var si) ? si.GetString() : null;
                                resp.visibility = (SessionVisibility)(root.TryGetProperty("visibility", out var vis)
                                    ? vis.GetInt32() : (int)SessionVisibility.Private);
                            }
                            var cb = _resolveCallback; _resolveCallback = null;
                            _resolveCallbackEndpointIndex = -1;
                            cb?.Invoke(resp);
                            break;
                        }
                        case "session.joined":
                        {
                            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                            if (!ok)
                            {
                                string reason = root.TryGetProperty("reason", out var rEl) ? rEl.GetString() : "unknown";
                                DiagnosticLog.Warning("SessionDirectory: join rejected — " + reason);
                                MultiplayerConnectionErrorHandler.ShowJoinRejected(reason);
                            }
                            else
                            {
                                DiagnosticLog.Info("SessionDirectory: joined session as non-host.");
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SessionDirectory: failed to handle response: " + ex.Message);
            }
        }
    }
}
