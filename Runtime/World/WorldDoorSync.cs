using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Syncs ObjectDoor state across players by directly matching the door's
    /// REAL rotation, continuously, rather than trying to recompute or
    /// trigger it indirectly.
    ///
    /// THIRD correction for this file, and this one should actually work.
    /// First version wrote the "open" field directly (no effect — that
    /// field is an output of physics, not an input). Second version called
    /// the private DoorOpened()/DoorClosed() methods (still no visible
    /// effect — those only react to the angle already having changed, they
    /// don't cause it to change). Third attempt tried computing the open
    /// rotation manually via HingeJoint.axis — but axis/anchor/motor/spring
    /// are ALL STRIPPED from this IL2CPP build (confirmed via dump: only
    /// HingeJoint.limits and .angle survived linker stripping, since
    /// MiSide's own code apparently never calls the others directly), so
    /// that would have thrown every single tick.
    ///
    /// This version sidesteps needing the axis at all: the SENDER reads the
    /// door's ACTUAL, real, physically-simulated transform.localRotation
    /// (a completely safe, always-available Transform property — no
    /// reflection, no stripped API) at the moment "open" changes, and sends
    /// THAT real rotation directly. The RECEIVER just Slerps its own copy of
    /// the door toward that exact rotation every tick, with the Rigidbody
    /// set kinematic so physics doesn't fight it. Same principle as
    /// player position/rotation sync — transmit the real value, don't
    /// recompute it from scratch.
    ///
    /// DoorOpened()/DoorClosed() are still invoked (best-effort, wrapped
    /// safely) once we're close to the target, purely so "open"/sound stay
    /// roughly consistent — but they are no longer load-bearing for the
    /// visual result, which now comes from the direct rotation match.
    /// </summary>
    public sealed class WorldDoorSync
    {
        private RpcDispatcher dispatcher;
        private string        localPlayerId;

        private float nextSampleTime;
        private float nextFullResyncTime;
        private float nextDebugLogTime;
        private const float SampleInterval     = 0.35f;
        private const float FullResyncInterval = 8f;
        private const float DebugLogInterval   = 2f;

        // ── Sender-side caches ──────────────────────────────────────────────────
        private readonly Dictionary<string, FieldInfo> openFieldCache = new Dictionary<string, FieldInfo>();
        private readonly Dictionary<string, bool>       lastKnownOpen = new Dictionary<string, bool>();

        // ── Per-door receiver-side state ────────────────────────────────────────
        private sealed class DoorInfo
        {
            public HingeJoint hinge;         // only used for the safe .angle diagnostic read now
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

            if (dispatcher != null)
            {
                dispatcher.RemoteDoorStateReceived -= OnRemoteDoorState;
                dispatcher.RemoteDoorStateReceived += OnRemoteDoorState;
            }
        }

        public void Tick()
        {
            if (dispatcher == null) return;

            WorldObjectRegistry.EnsureScannedForCurrentScene();

            SampleAndSend();
            ForceApplyAllTick();   // every frame, not gated by the sample interval
        }

        // ── Sender side ──────────────────────────────────────────────────────────
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

        // ── Receiver side ────────────────────────────────────────────────────────
        private void OnRemoteDoorState(WorldDoorState state)
        {
            if (state == null || string.IsNullOrEmpty(state.path)) return;
            if (!string.IsNullOrEmpty(localPlayerId) && state.senderId == localPlayerId) return;

            Component door;
            if (!WorldObjectRegistry.TryGetDoor(state.path, out door) || door == null)
                return;   // normal — different room/scene, or not scanned yet

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

        // Gathers what we need for a door the first time we touch it.
        // HingeJoint/Rigidbody are standard Unity component refs (no
        // reflection). DoorOpened/DoorClosed are private game methods
        // (reflection, best-effort, used only for sound/flag consistency —
        // not load-bearing for the visual result anymore).
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

        // Runs every frame for every door we're actively forcing — Slerps
        // the door's real rotation toward the received target, and fires
        // the matching event method (best-effort) once we're close.
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

                const float slerpSpeed = 10f;   // per-second, exponential approach
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
                    door.transform.localRotation = info.targetRotation;   // snap the last bit, avoid asymptotic creep
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
