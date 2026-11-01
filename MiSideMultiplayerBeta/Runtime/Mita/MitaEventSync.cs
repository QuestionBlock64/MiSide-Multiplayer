using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace MiSideMultiplayer
{
    public static class MitaEventSync
    {
        public const string MitaRpcEvent = "miside.mita.rpc";

        private static readonly List<MitaEvent> _pending = new List<MitaEvent>();
        private static readonly object _pendingLock = new object();
        private static readonly Dictionary<string, string> _lastMoveTargetByMita =
            new Dictionary<string, string>();
        private static readonly Dictionary<string, Dictionary<string, MitaEvent>> _latestEventsByMita =
            new Dictionary<string, Dictionary<string, MitaEvent>>();
        private static readonly Dictionary<string, long> _lastAppliedSequenceByMita =
            new Dictionary<string, long>();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        private static bool _initialized;
        private static Harmony _harmony;
        private static RpcDispatcher _dispatcher;
        private static string _localPlayerId = "";
        private static long _nextEventSequence;
        private static Type _mitaPersonType;
        private static string _mitaPersonTypeName;
        // Host events invoke the game's own methods so their face/state side
        // effects remain intact. Permit those calls through the joiner command
        // gate while rejecting the same methods when local quest/AI scripts
        // try to drive the non-authoritative Mita.
        private static int _applyingHostEventDepth;

        public static void Initialize(Harmony harmony)
        {
            if (_initialized) return;
            _initialized = true;
            _harmony = harmony;

            try
            {
                _mitaPersonType = FindType("MitaPerson");
                _mitaPersonTypeName = _mitaPersonType != null ? _mitaPersonType.Name : null;

                int installed = 0;
                installed += PatchMethod(_mitaPersonType, "FaceEmotion",
                    new[] { typeof(string) }, nameof(FaceEmotionPrefix));
                installed += PatchMethod(_mitaPersonType, "FaceEmotionFast",
                    new[] { typeof(string) }, nameof(FaceEmotionFastPrefix));
                installed += PatchMethod(_mitaPersonType, "FaceEmotionOff",
                    Type.EmptyTypes, nameof(FaceEmotionOffPrefix));
                installed += PatchMethod(_mitaPersonType, "FaceEmotionFastOff",
                    Type.EmptyTypes, nameof(FaceEmotionFastOffPrefix));
                installed += PatchMethod(_mitaPersonType, "FaceLayer",
                    new[] { typeof(int) }, nameof(FaceLayerPrefix));
                installed += PatchMethod(_mitaPersonType, "FaceLayerFast",
                    new[] { typeof(int) }, nameof(FaceLayerFastPrefix));
                installed += PatchOptionalMethod(_mitaPersonType, "SetState",
                    new[] { typeof(int) }, nameof(SetStatePrefix));
                installed += PatchOptionalMethod(_mitaPersonType, "AiWalkToTarget",
                    new[] { typeof(Transform), typeof(UnityEngine.Events.UnityEvent) },
                    nameof(AiWalkToTargetPrefix));
                installed += PatchOptionalMethod(_mitaPersonType, "AiWalkToTarget",
                    new[] { typeof(Transform), typeof(UnityEngine.Events.UnityEvent), typeof(AnimationClip) },
                    nameof(AiWalkToTargetPrefix));
                installed += PatchOptionalMethod(_mitaPersonType, "AiWalkToTargetTranform",
                    new[] { typeof(Transform), typeof(UnityEngine.Events.UnityEvent) },
                    nameof(AiWalkToTargetPrefix));
                installed += PatchOptionalMethod(_mitaPersonType, "AiWalkToTargetRotate",
                    new[] { typeof(Transform), typeof(UnityEngine.Events.UnityEvent) },
                    nameof(AiWalkToTargetPrefix));
                installed += PatchOptionalMethod(_mitaPersonType, "AiShraplyStop",
                    Type.EmptyTypes, nameof(AiShraplyStopPrefix));
                // Scene 5's knife cue is a direct Location5_World method call,
                // not just an Animator or active-state change. Relay the call
                // so every peer runs the same game-side event and side effects.
                installed += PatchOptionalMethod(FindType("Location5_World"), "MitaKnife",
                    Type.EmptyTypes, nameof(MitaKnifePrefix));

                // Dialogue replay stays disabled: both clients run the same
                // quest scripts, and replaying dialogue duplicates HUD text.
                DiagnosticLog.Info("MitaEventSync: patched " + installed + " event hook(s).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaEventSync: init failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Configure(RpcDispatcher dispatcher, string playerId)
        {
            _localPlayerId = playerId ?? "";
            if (ReferenceEquals(_dispatcher, dispatcher)) return;

            if (_dispatcher != null)
                _dispatcher.CustomMessageReceived -= OnCustomMessage;

            _dispatcher = dispatcher;
            if (_dispatcher != null)
                _dispatcher.CustomMessageReceived += OnCustomMessage;
        }

        public static MitaEvent[] DrainPending(bool _)
        {
            lock (_pendingLock)
            {
                if (_pending.Count == 0) return Array.Empty<MitaEvent>();
                MitaEvent[] events = _pending.ToArray();
                _pending.Clear();
                return events;
            }
        }

        public static long LastSequence
        {
            get { lock (_pendingLock) return _nextEventSequence; }
        }

        public static MitaEvent[] GetSnapshotEvents(string mitaPath)
        {
            if (string.IsNullOrEmpty(mitaPath)) return Array.Empty<MitaEvent>();

            lock (_pendingLock)
            {
                Dictionary<string, MitaEvent> latest;
                if (!_latestEventsByMita.TryGetValue(mitaPath, out latest) || latest == null || latest.Count == 0)
                    return Array.Empty<MitaEvent>();

                List<MitaEvent> events = new List<MitaEvent>(latest.Values);
                events.Sort(delegate(MitaEvent a, MitaEvent b)
                {
                    return a.sequence.CompareTo(b.sequence);
                });
                return events.ToArray();
            }
        }

        public static void ResetForScene(string sceneName)
        {
            lock (_pendingLock)
            {
                _pending.Clear();
                _latestEventsByMita.Clear();
                _lastAppliedSequenceByMita.Clear();
                _lastMoveTargetByMita.Clear();
                _nextEventSequence = 0;
            }
        }

        public static void ResetAppliedSequences()
        {
            lock (_pendingLock) _lastAppliedSequenceByMita.Clear();
        }

        public static void SendSetState(string mitaPath, int stateId)
        {
            SendEvent(CreateEvent(mitaPath, "setState", null, stateId));
        }

        public static void SendMoveToNode(string mitaPath, string targetPath, Vector3 targetPosition)
        {
            SendMoveToNode(mitaPath, targetPath, null, targetPosition);
        }

        public static void SendMoveToNode(string mitaPath, string targetPath,
                                          string targetPlayerId, Vector3 targetPosition)
        {
            MitaEvent ev = CreateEvent(mitaPath, "moveToNode", targetPath, StableNodeId(targetPath, targetPosition));
            ev.targetPlayerId = targetPlayerId;
            ev.targetPosition = NetVector3.FromUnity(targetPosition);
            SendEvent(ev);
        }

        private static MitaEvent CreateEvent(string mitaPath, string kind, string stringValue, int intValue)
        {
            return new MitaEvent
            {
                senderId = _localPlayerId,
                sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                mitaPath = mitaPath,
                kind = kind,
                stringValue = stringValue,
                intValue = intValue
            };
        }

        private static void Record(string mitaPath, string kind, string stringValue = null, int intValue = 0)
        {
            if ((!MitaHost.IsHost && !MitaHost.IsScene5SharedMode) ||
                _applyingHostEventDepth > 0)
                return;
            SendEvent(CreateEvent(mitaPath, kind, stringValue, intValue));
        }

        private static void SendEvent(MitaEvent ev)
        {
            if (ev == null ||
                (!MitaHost.IsHost && !MitaHost.IsScene5SharedMode) ||
                _applyingHostEventDepth > 0)
                return;

            RpcDispatcher dispatcher = _dispatcher;
            lock (_pendingLock)
            {
                if (ev.sequence <= 0) ev.sequence = ++_nextEventSequence;
                else if (ev.sequence > _nextEventSequence) _nextEventSequence = ev.sequence;
                CacheLatestEvent(ev);
            }

            if (dispatcher == null)
            {
                lock (_pendingLock)
                {
                    if (_pending.Count >= 256) _pending.RemoveAt(0);
                    _pending.Add(ev);
                }
                return;
            }

            try
            {
                dispatcher.SendCustom(MitaRpcEvent, JsonSerializer.Serialize(ev, JsonOptions));
                DiagnosticLog.Verbose("MitaEventSync: sent " +
                                      (MitaHost.IsHost ? "host" : "peer") +
                                      " RPC kind='" + ev.kind +
                                      "' path='" + ev.mitaPath + "' sequence=" + ev.sequence + ".");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaEventSync: failed to send '" + ev.kind + "': " + ex.Message);
            }
        }

        private static void OnCustomMessage(string eventName, string json)
        {
            if (eventName != MitaRpcEvent) return;

            try
            {
                MitaEvent ev = JsonSerializer.Deserialize<MitaEvent>(json, JsonOptions);
                bool sharedScene = MitaHost.IsScene5SharedMode;
                if (ev == null || string.IsNullOrEmpty(ev.kind) ||
                    string.IsNullOrEmpty(ev.mitaPath) ||
                    string.IsNullOrEmpty(ev.senderId) ||
                    ev.senderId == _localPlayerId ||
                    (!sharedScene && (MitaHost.IsHost ||
                        string.IsNullOrEmpty(MitaHost.HostPlayerId) ||
                        ev.senderId != MitaHost.HostPlayerId)))
                    return;

                if (!string.IsNullOrEmpty(ev.sceneName) &&
                    ev.sceneName != UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)
                    return;

                ApplyLiveEvent(ev);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaEventSync: receive failed: " + ex.Message);
            }
        }

        public static void ApplyLiveEvent(MitaEvent ev)
        {
            if (ev == null) return;

            if (ev.sequence > 0)
            {
                lock (_pendingLock)
                {
                    long lastSequence;
                    string sequenceKey = SequenceKey(ev.senderId, ev.mitaPath);
                    if (_lastAppliedSequenceByMita.TryGetValue(sequenceKey, out lastSequence) &&
                        ev.sequence <= lastSequence)
                        return;
                    _lastAppliedSequenceByMita[sequenceKey] = ev.sequence;
                }
            }

            ApplyEvent(ev);
        }

        public static void ApplySnapshotEvents(string mitaPath, MitaEvent[] events,
                                               long lastEventSequence)
        {
            if (!string.IsNullOrEmpty(mitaPath) && events != null && events.Length > 0)
            {
                List<MitaEvent> ordered = new List<MitaEvent>();
                for (int i = 0; i < events.Length; i++)
                {
                    MitaEvent ev = events[i];
                    if (ev != null && ev.mitaPath == mitaPath) ordered.Add(ev);
                }
                ordered.Sort(delegate(MitaEvent a, MitaEvent b)
                {
                    return a.sequence.CompareTo(b.sequence);
                });

                for (int i = 0; i < ordered.Count; i++)
                    ApplyEvent(ordered[i]);
            }

            if (string.IsNullOrEmpty(mitaPath) || lastEventSequence <= 0) return;
            lock (_pendingLock)
            {
                long previous;
                string sequenceKey = SequenceKey(MitaHost.HostPlayerId, mitaPath);
                if (!_lastAppliedSequenceByMita.TryGetValue(sequenceKey, out previous) ||
                    lastEventSequence > previous)
                    _lastAppliedSequenceByMita[sequenceKey] = lastEventSequence;
            }
        }

        private static void CacheLatestEvent(MitaEvent ev)
        {
            string path = ev.mitaPath ?? "";
            if (path.Length == 0) return;

            Dictionary<string, MitaEvent> latest;
            if (!_latestEventsByMita.TryGetValue(path, out latest) || latest == null)
            {
                latest = new Dictionary<string, MitaEvent>();
                _latestEventsByMita[path] = latest;
            }

            string category;
            switch (ev.kind)
            {
                case "faceEmotion":
                case "faceEmotionFast":
                case "faceEmotionOff":
                case "faceEmotionFastOff":
                    category = "emotion";
                    break;
                case "faceLayer":
                case "faceLayerFast":
                    category = "faceLayer";
                    break;
                case "moveToNode":
                case "stopWalking":
                    category = "movement";
                    break;
                default:
                    category = ev.kind ?? "";
                    break;
            }

            latest[category] = ev;
        }

        private static int PatchMethod(Type type, string methodName, Type[] args, string prefixName)
        {
            if (type == null) return 0;
            MethodInfo target = type.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, args, null);
            if (target == null)
            {
                DiagnosticLog.Warning("MitaEventSync: " + type.Name + "." + methodName + " NOT FOUND.");
                return 0;
            }

            return PatchResolvedMethod(target, methodName, prefixName);
        }

        private static int PatchOptionalMethod(Type type, string methodName, Type[] args, string prefixName)
        {
            if (type == null) return 0;
            MethodInfo target = type.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, args, null);
            return target == null ? 0 : PatchResolvedMethod(target, methodName, prefixName);
        }

        private static int PatchResolvedMethod(MethodInfo target, string methodName, string prefixName)
        {
            MethodInfo prefix = typeof(MitaEventSync).GetMethod(
                prefixName, BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                return 1;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaEventSync: failed to patch " + methodName + ": " + ex.Message);
                return 0;
            }
        }

        private static string PathOf(Component component)
        {
            return component == null ? "" : MitaBoneSync.GetNetworkPath(component.transform);
        }

        private static bool FaceEmotionPrefix(Component __instance, string _name)
        { Record(PathOf(__instance), "faceEmotion", _name); return ShouldRunMitaCommand(); }
        private static bool FaceEmotionFastPrefix(Component __instance, string _name)
        { Record(PathOf(__instance), "faceEmotionFast", _name); return ShouldRunMitaCommand(); }
        private static bool FaceEmotionOffPrefix(Component __instance)
        { Record(PathOf(__instance), "faceEmotionOff"); return ShouldRunMitaCommand(); }
        private static bool FaceEmotionFastOffPrefix(Component __instance)
        { Record(PathOf(__instance), "faceEmotionFastOff"); return ShouldRunMitaCommand(); }
        private static bool FaceLayerPrefix(Component __instance, int _index)
        { Record(PathOf(__instance), "faceLayer", null, _index); return ShouldRunMitaCommand(); }
        private static bool FaceLayerFastPrefix(Component __instance, int _index)
        { Record(PathOf(__instance), "faceLayerFast", null, _index); return ShouldRunMitaCommand(); }
        private static bool SetStatePrefix(Component __instance, int _stateId)
        { Record(PathOf(__instance), "setState", null, _stateId); return ShouldRunMitaCommand(); }

        private static bool AiShraplyStopPrefix(Component __instance)
        { Record(PathOf(__instance), "stopWalking"); return ShouldRunMitaCommand(); }

        private static bool MitaKnifePrefix(Component __instance)
        { Record(PathOf(__instance), "mitaKnife"); return ShouldRunMitaCommand(); }

        private static bool AiWalkToTargetPrefix(Component __instance, object[] __args)
        {
            if (!MitaHost.IsHost && !MitaHost.IsScene5SharedMode)
                return ShouldRunMitaCommand();
            if (__args == null || __args.Length == 0) return true;
            Transform target = __args[0] as Transform;
            if (target == null) return true;

            string mitaPath = PathOf(__instance);
            string targetPlayerId = MitaHost.ResolvePlayerIdForTransform(target);
            string targetPath = string.IsNullOrEmpty(targetPlayerId)
                ? LocalPlayerLocator.GetPath(target)
                : "";
            if (string.IsNullOrEmpty(mitaPath) ||
                (string.IsNullOrEmpty(targetPath) && string.IsNullOrEmpty(targetPlayerId))) return true;

            string cacheKey = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "\n" + mitaPath;
            string previous;
            string moveTargetKey = string.IsNullOrEmpty(targetPlayerId)
                ? targetPath
                : "player:" + targetPlayerId;
            if (_lastMoveTargetByMita.TryGetValue(cacheKey, out previous) &&
                string.Equals(previous, moveTargetKey, StringComparison.Ordinal))
                return true;

            _lastMoveTargetByMita[cacheKey] = moveTargetKey;
            SendMoveToNode(mitaPath, targetPath, targetPlayerId, target.position);
            return true;
        }

        private static bool ShouldRunMitaCommand()
        {
            return MitaHost.IsHost || MitaHost.IsScene5SharedMode ||
                   _applyingHostEventDepth > 0;
        }

        private static string SequenceKey(string senderId, string mitaPath)
        {
            return (senderId ?? string.Empty) + "\n" + (mitaPath ?? string.Empty);
        }

        public static void ApplyEvent(MitaEvent ev)
        {
            if (ev == null || string.IsNullOrEmpty(ev.kind)) return;

            _applyingHostEventDepth++;
            try
            {
                DiagnosticLog.Verbose("MitaEventSync: applying host RPC kind='" + ev.kind +
                                      "' path='" + ev.mitaPath + "' sequence=" + ev.sequence + ".");
                switch (ev.kind)
                {
                    case "faceEmotion":
                        InvokeMitaPerson(ev.mitaPath, "FaceEmotion", new object[] { ev.stringValue ?? "" });
                        break;
                    case "faceEmotionFast":
                        InvokeMitaPerson(ev.mitaPath, "FaceEmotionFast", new object[] { ev.stringValue ?? "" });
                        break;
                    case "faceEmotionOff":
                        // MitaPerson.Update owns the gradual fade, but joiner
                        // MitaPerson components are disabled with local AI.
                        // Use the game's immediate off method so the face
                        // layer still changes without re-enabling its loop.
                        InvokeMitaPerson(ev.mitaPath, "FaceEmotionFastOff", null);
                        break;
                    case "faceEmotionFastOff":
                        InvokeMitaPerson(ev.mitaPath, "FaceEmotionFastOff", null);
                        break;
                    case "faceLayer":
                        // The gradual FaceLayer transition is driven by
                        // MitaPerson.Update; apply it immediately on joiners.
                        InvokeMitaPerson(ev.mitaPath, "FaceLayerFast", new object[] { ev.intValue });
                        break;
                    case "faceLayerFast":
                        InvokeMitaPerson(ev.mitaPath, "FaceLayerFast", new object[] { ev.intValue });
                        break;
                    case "setState":
                        InvokeMitaPerson(ev.mitaPath, "SetState", new object[] { ev.intValue });
                        break;
                    case "moveToNode":
                        ApplyMoveToNode(ev);
                        break;
                    case "stopWalking":
                        InvokeMitaPerson(ev.mitaPath, "AiShraplyStop", null);
                        break;
                    case "mitaKnife":
                        InvokeComponentMethod(ev.mitaPath, "Location5_World", "MitaKnife", null);
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaEventSync.ApplyEvent('" + ev.kind + "') failed: " + ex.Message);
            }
            finally
            {
                _applyingHostEventDepth--;
            }
        }

        private static void ApplyMoveToNode(MitaEvent ev)
        {
            GameObject mita = FindByPath(ev.mitaPath);
            if (mita == null) return;

            // The joiner must not run Mita's pathfinding or AI loop. This RPC
            // records the host's current intent; authoritative position and
            // BoneSync snapshots drive the remote view.
            DiagnosticLog.Verbose("MitaEventSync: received host move target for '" +
                                  ev.mitaPath + "'; awaiting authoritative position updates.");
        }

        private static void InvokeMitaPerson(string mitaPath, string methodName, object[] args)
        {
            if (string.IsNullOrEmpty(_mitaPersonTypeName)) return;
            GameObject mitaGO = FindByPath(mitaPath);
            if (mitaGO == null) return;
            Component mita = mitaGO.GetComponent(_mitaPersonTypeName);
            if (mita == null) return;

            MethodInfo method = mita.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) return;
            method.Invoke(mita, args);
        }

        private static void InvokeComponentMethod(string objectPath, string typeName,
                                                  string methodName, object[] args)
        {
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName)) return;
            GameObject target = FindByPath(objectPath);
            if (target == null) return;

            Component[] components;
            try { components = target.GetComponents<Component>(); }
            catch { return; }
            if (components == null) return;

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null ||
                    !string.Equals(component.GetType().Name, typeName, StringComparison.Ordinal))
                    continue;
                MethodInfo method = component.GetType().GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, args == null ? Type.EmptyTypes : GetArgumentTypes(args), null);
                if (method == null) return;
                method.Invoke(component, args);
                return;
            }
        }

        private static Type[] GetArgumentTypes(object[] args)
        {
            Type[] types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++)
                types[i] = args[i] != null ? args[i].GetType() : typeof(object);
            return types;
        }

        private static GameObject FindByPath(string path)
        {
            return MitaController.ResolveLocalMitaGameObject(path) ??
                   MitaBoneSync.FindGameObjectByNetworkPath(path);
        }

        private static int StableNodeId(string targetPath, Vector3 position)
        {
            string key = string.IsNullOrEmpty(targetPath)
                ? Math.Round(position.x, 1).ToString(CultureInfo.InvariantCulture) + "," +
                  Math.Round(position.y, 1).ToString(CultureInfo.InvariantCulture) + "," +
                  Math.Round(position.z, 1).ToString(CultureInfo.InvariantCulture)
                : targetPath;
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < key.Length; i++)
                {
                    hash ^= key[i];
                    hash *= 16777619;
                }
                return (int)hash;
            }
        }

        private static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(name, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }
    }
}
