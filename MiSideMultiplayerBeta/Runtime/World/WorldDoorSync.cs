using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Syncs ObjectDoor state across players by directly matching the door's
    /// REAL rotation, continuously, rather than trying to recompute it.
    /// </summary>
    public sealed class WorldDoorSync
    {
        private static WorldDoorSync _active;

        private RpcDispatcher dispatcher;
        private string        localPlayerId;

        private float nextSampleTime;
        private float nextFullResyncTime;
        private float nextDebugLogTime;
        private const float SampleInterval     = 0.35f;
        private const float FullResyncInterval = 8f;
        private const float DebugLogInterval   = 2f;

        private readonly Dictionary<string, FieldInfo> openFieldCache = new Dictionary<string, FieldInfo>();
        private readonly Dictionary<string, bool>       lastKnownOpen = new Dictionary<string, bool>();

        private sealed class DoorInfo
        {
            public HingeJoint hinge;
            public Rigidbody  rb;
            public MethodInfo openedMethod;
            public MethodInfo closedMethod;
            public Quaternion targetRotation;
            public bool       desiredOpen;
            public bool       beingForced;
        }

        private readonly Dictionary<string, DoorInfo> doorInfo = new Dictionary<string, DoorInfo>();
        private readonly HashSet<string> firedOpenOnce  = new HashSet<string>();
        private readonly HashSet<string> firedCloseOnce = new HashSet<string>();

        private bool loggedFieldMissingWarning;
        private bool loggedGetValueFailure;
        private bool loggedFirstSend;

        public void Configure(RpcDispatcher rpcDispatcher, string playerId)
        {
            dispatcher    = rpcDispatcher;
            localPlayerId = playerId;
            _active       = this;

            if (dispatcher != null)
            {
                dispatcher.RemoteDoorStateReceived -= OnRemoteDoorState;
                dispatcher.RemoteDoorStateReceived += OnRemoteDoorState;
            }
        }

        /// <summary>
        /// Drops all per-scene cached state. Called from WorldTransitionSync
        /// when a new world activates within the same scene, so doors keyed
        /// by the previous world's paths don't survive.
        /// </summary>
        public static void ResetCachesForWorldChange()
        {
            WorldDoorSync s = _active;
            if (s == null) return;

            s.openFieldCache.Clear();
            s.lastKnownOpen.Clear();
            s.doorInfo.Clear();
            s.firedOpenOnce.Clear();
            s.firedCloseOnce.Clear();
            s.nextSampleTime     = 0f;
            s.nextFullResyncTime = 0f;

            DiagnosticLog.Info("WorldDoorSync: caches cleared for world transition.");
        }

        public static void BroadcastSnapshot()
        {
            WorldDoorSync sync = _active;
            if (sync == null || sync.dispatcher == null || !MitaHost.IsHost) return;

            WorldObjectRegistry.EnsureScannedForCurrentScene();
            sync.nextSampleTime = 0f;
            sync.nextFullResyncTime = 0f;
            sync.SampleAndSend();
            DiagnosticLog.Info("WorldDoorSync: host sent a full door snapshot for the loading handshake.");
        }

        public void Tick()
        {
            if (dispatcher == null) return;

            WorldObjectRegistry.EnsureScannedForCurrentScene();

            SampleAndSend();
            ForceApplyAllTick();
        }

        private void SampleAndSend()
        {
            bool timeToSample = Time.unscaledTime >= nextSampleTime;
            bool timeToResync = Time.unscaledTime >= nextFullResyncTime;
            if (!timeToSample && !timeToResync) return;

            if (timeToSample) nextSampleTime     = Time.unscaledTime + SampleInterval;
            if (timeToResync) nextFullResyncTime = Time.unscaledTime + FullResyncInterval;

            string sceneName = SceneManager.GetActiveScene().name;

            foreach (KeyValuePair<string, Component> pair in WorldObjectRegistry.AllDoors)
            {
                string    path = pair.Key;
                Component door = pair.Value;
                if (door == null) continue;

                FieldInfo field;
                if (!openFieldCache.TryGetValue(path, out field))
                {
                    try { field = door.GetType().GetField("open", BindingFlags.Public | BindingFlags.Instance); }
                    catch (Exception) { field = null; }
                    openFieldCache[path] = field;

                    if (field == null && !loggedFieldMissingWarning)
                    {
                        loggedFieldMissingWarning = true;
                        DiagnosticLog.Warning(
                            "WorldDoorSync: could not reflect ObjectDoor.open field on '" +
                            path + "' — door sync may not work. (One-time warning.)");
                    }
                }
                if (field == null) continue;

                bool currentOpen;
                try { currentOpen = (bool)field.GetValue(door); }
                catch (Exception ex)
                {
                    if (!loggedGetValueFailure)
                    {
                        loggedGetValueFailure = true;
                        DiagnosticLog.Warning(
                            "WorldDoorSync: field.GetValue() threw reading '" + path +
                            "'.open (" + ex.GetType().Name + ": " + ex.Message + "). One-time warning.");
                    }
                    continue;
                }

                bool known;
                bool hasKnown = lastKnownOpen.TryGetValue(path, out known);
                bool changed  = !hasKnown || known != currentOpen;
                if (!changed && !timeToResync) continue;

                lastKnownOpen[path] = currentOpen;

                WorldDoorState msg = new WorldDoorState();
                msg.senderId      = localPlayerId;
                msg.sceneName     = sceneName;
                msg.path          = path;
                msg.isOpen        = currentOpen;
                msg.localRotation = NetQuaternion.FromUnity(door.transform.localRotation);
                dispatcher.SendDoorState(msg);

                if (!loggedFirstSend)
                {
                    loggedFirstSend = true;
                    DiagnosticLog.Info(
                        "WorldDoorSync: sending door state for '" + path + "' = " + currentOpen +
                        "  rot=" + door.transform.localRotation.eulerAngles.ToString("F1"));
                }
            }
        }

        private void OnRemoteDoorState(WorldDoorState state)
        {
            if (state == null || string.IsNullOrEmpty(state.path)) return;
            if (!string.IsNullOrEmpty(localPlayerId) && state.senderId == localPlayerId) return;

            Component door;
            if (!WorldObjectRegistry.TryGetDoor(state.path, out door) || door == null)
                return;

            DoorInfo info = GetOrCaptureInfo(state.path, door);
            if (info == null) return;

            info.desiredOpen   = state.isOpen;
            info.targetRotation = state.localRotation.ToUnity();
            info.beingForced   = true;

            if (info.rb != null && !info.rb.isKinematic)
            {
                try { info.rb.isKinematic = true; } catch (Exception) { }
            }

            DiagnosticLog.Info(
                "WorldDoorSync: received '" + state.path + "' isOpen=" + state.isOpen +
                "  targetRot=" + info.targetRotation.eulerAngles.ToString("F1") +
                "  (from '" + state.senderId + "')");
        }

        private DoorInfo GetOrCaptureInfo(string path, Component door)
        {
            DoorInfo info;
            if (doorInfo.TryGetValue(path, out info))
                return info;

            info = new DoorInfo();
            doorInfo[path] = info;

            try
            {
                GameObject go = door.gameObject;
                info.hinge = go.GetComponent<HingeJoint>();
                info.rb    = go.GetComponent<Rigidbody>();

                Type t = door.GetType();
                info.openedMethod = t.GetMethod("DoorOpened", BindingFlags.NonPublic | BindingFlags.Instance);
                info.closedMethod = t.GetMethod("DoorClosed", BindingFlags.NonPublic | BindingFlags.Instance);

                DiagnosticLog.Info(
                    "WorldDoorSync: captured info for '" + path + "'" +
                    "  hinge=" + (info.hinge != null ? "found" : "MISSING") +
                    "  rb=" + (info.rb != null ? "found" : "MISSING") +
                    "  DoorOpened=" + (info.openedMethod != null ? "OK" : "MISSING") +
                    "  DoorClosed=" + (info.closedMethod != null ? "OK" : "MISSING"));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldDoorSync: GetOrCaptureInfo failed for '" + path + "': " + ex.Message);
            }

            return info;
        }

        private void ForceApplyAllTick()
        {
            bool doDebugLog = Time.unscaledTime >= nextDebugLogTime;
            if (doDebugLog) nextDebugLogTime = Time.unscaledTime + DebugLogInterval;

            foreach (KeyValuePair<string, DoorInfo> pair in doorInfo)
            {
                string   path = pair.Key;
                DoorInfo info = pair.Value;
                if (info == null || !info.beingForced) continue;

                Component door;
                if (!WorldObjectRegistry.TryGetDoor(path, out door) || door == null) continue;

                const float slerpSpeed = 10f;
                float t = 1f - Mathf.Exp(-slerpSpeed * Time.deltaTime);
                door.transform.localRotation = Quaternion.Slerp(
                    door.transform.localRotation, info.targetRotation, t);

                float angleDiff = Quaternion.Angle(door.transform.localRotation, info.targetRotation);

                if (doDebugLog)
                {
                    float hingeAngle = float.NaN;
                    if (info.hinge != null)
                    {
                        try { hingeAngle = info.hinge.angle; } catch (Exception) { }
                    }
                    DiagnosticLog.Info(
                        "  [door force] '" + path + "'" +
                        "  desiredOpen=" + info.desiredOpen +
                        "  currentRot=" + door.transform.localRotation.eulerAngles.ToString("F1") +
                        "  targetRot=" + info.targetRotation.eulerAngles.ToString("F1") +
                        "  angleDiff=" + angleDiff.ToString("F1") +
                        "  hingeAngle=" + (float.IsNaN(hingeAngle) ? "n/a" : hingeAngle.ToString("F1")));
                }

                if (angleDiff < 1f)
                {
                    door.transform.localRotation = info.targetRotation;
                    FireEventMethodOnce(path, info, info.desiredOpen);
                    info.beingForced = false;
                    if (info.rb != null)
                    {
                        try { info.rb.isKinematic = false; } catch (Exception) { }
                    }
                }
            }
        }

        private void FireEventMethodOnce(string path, DoorInfo info, bool isOpen)
        {
            HashSet<string> firedSet = isOpen ? firedOpenOnce : firedCloseOnce;
            if (firedSet.Contains(path)) return;

            (isOpen ? firedCloseOnce : firedOpenOnce).Remove(path);
            firedSet.Add(path);

            MethodInfo method = isOpen ? info.openedMethod : info.closedMethod;
            if (method == null) return;

            try
            {
                Component door;
                if (WorldObjectRegistry.TryGetDoor(path, out door) && door != null)
                    method.Invoke(door, null);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "WorldDoorSync: " + (isOpen ? "DoorOpened" : "DoorClosed") +
                    "() invoke threw for '" + path + "': " + ex.Message);
            }
        }
    }
}
