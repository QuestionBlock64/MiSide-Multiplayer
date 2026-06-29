using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class LocalPlayerSampler
    {
        // ── Config ────────────────────────────────────────────────────────────
        private RpcDispatcher  rpcDispatcher;
        private NetworkManager networkManager;
        private string localPlayerId;
        private string displayName;
        private float  sendRate             = 20f;
        private float  heartbeatSeconds     = 1f;
        private bool   emitSnapshots        = true;

        // ── Timing ────────────────────────────────────────────────────────────
        private float nextSendTime;
        private float nextHeartbeatTime;
        private float nextErrorLogTime;
        private int   tick;

        // ── State ─────────────────────────────────────────────────────────────
        private bool              loggedFirstSnapshot;
        private bool              hasLastPosition;
        private Vector3           lastPosition;
        private bool              hasLastSentState;
        private RemotePlayerState lastSentState;

        // ── Cached references ─────────────────────────────────────────────────
        // Person/HeadMirror — the Transform MiSide uses to mirror head rotation
        private Transform cachedHeadMirror;
        // The animator we sample (may be on Person or a sibling model)
        private Animator  cachedAnimator;
        private float     nextRefCacheTime;
        private const float RefCacheInterval = 5f;

        // ── Known MiSide animator parameter hashes ────────────────────────────
        // Derived from dump.cs string literals: SpeedForward, InertionRight, HeadMove, etc.
        private static readonly int HashSpeedForward   = Animator.StringToHash("SpeedForward");
        private static readonly int HashInertionRight  = Animator.StringToHash("InertionRight");
        private static readonly int HashHeadMove       = Animator.StringToHash("HeadMove");
        private static readonly int HashMouseSpeed     = Animator.StringToHash("MouseSpeed");
        private static readonly int HashForward        = Animator.StringToHash("Forward");
        private static readonly int HashWalk           = Animator.StringToHash("Walk");
        private static readonly int HashRun            = Animator.StringToHash("Run");
        private static readonly int HashIdle           = Animator.StringToHash("Idle");
        private static readonly int HashSit            = Animator.StringToHash("Sit");
        private static readonly int HashJump           = Animator.StringToHash("Jump");
        private static readonly int HashJumpStop       = Animator.StringToHash("JumpStop");
        private static readonly int HashBedSit         = Animator.StringToHash("BedSit");
        private static readonly int HashKickSit        = Animator.StringToHash("KickSit");
        private static readonly int HashOtherAnimType  = Animator.StringToHash("OtherAnimationType");
        private static readonly int HashOtherAnimHold  = Animator.StringToHash("OtherAnimationHold");
        private static readonly int HashAnimStop       = Animator.StringToHash("animstop");
        private static readonly int HashFaceLayer      = Animator.StringToHash("facelayer");

        // ── Configure ─────────────────────────────────────────────────────────
        public void Configure(
            RpcDispatcher dispatcher,
            NetworkManager manager,
            string configuredLocalPlayerId,
            string configuredDisplayName,
            float  configuredSendRate,
            float  configuredHeartbeatSeconds,
            bool   shouldEmitSnapshots)
        {
            rpcDispatcher    = dispatcher;
            networkManager   = manager;
            localPlayerId    = configuredLocalPlayerId;
            displayName      = configuredDisplayName;
            sendRate         = Mathf.Max(1f, configuredSendRate);
            heartbeatSeconds = Mathf.Max(0.25f, configuredHeartbeatSeconds);
            emitSnapshots    = shouldEmitSnapshots;
        }

        // ── Per-frame tick ────────────────────────────────────────────────────
        public void Tick()
        {
            if (!emitSnapshots || rpcDispatcher == null || networkManager == null)
                return;

            if (Time.unscaledTime < nextSendTime)
                return;

            nextSendTime = Time.unscaledTime + 1f / sendRate;

            if (!networkManager.TryBindLocalPlayer())
                return;

            try
            {
                Transform localRoot = networkManager.LocalPlayerRoot;

                // Refresh cached references periodically
                if (Time.unscaledTime >= nextRefCacheTime)
                {
                    nextRefCacheTime = Time.unscaledTime + RefCacheInterval;
                    RefreshCachedReferences(localRoot);
                }

                RemotePlayerState state = BuildState(localRoot);

                bool isHeartbeatDue = Time.unscaledTime >= nextHeartbeatTime;
                if (!HasMeaningfulChange(state, lastSentState) && !isHeartbeatDue)
                    return;

                lastSentState     = state;
                hasLastSentState  = true;
                nextHeartbeatTime = Time.unscaledTime + heartbeatSeconds;
                rpcDispatcher.SendState(state);

                if (!loggedFirstSnapshot)
                {
                    loggedFirstSnapshot = true;
                    DiagnosticLog.Info(
                        "Sending snapshots as '" + localPlayerId +
                        "' scene='" + state.sceneName + "'" +
                        "  headMirror=" + (cachedHeadMirror != null ? "found" : "missing") +
                        "  animator=" + (cachedAnimator != null ? "found" : "missing"));
                }
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= nextErrorLogTime)
                {
                    nextErrorLogTime = Time.unscaledTime + 5f;
                    DiagnosticLog.Warning(
                        "Local player sampling failed: " +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        // ── Reference caching ─────────────────────────────────────────────────
        private void RefreshCachedReferences(Transform localRoot)
        {
            // ── Head rotation source ──────────────────────────────────────────
            // HeadPlayer (sibling of Person at Player level) is the LookAtIK
            // target.  Its WORLD ROTATION = camera yaw + camera pitch = where the
            // player is actually looking.
            //
            // HeadMirror (child of Person) is the head MESH transform.  MiSide
            // positions HeadMirror to face a mirror camera that sits above and
            // behind the character, so HeadMirror.rotation is always "looking up"
            // in world space — exactly the bug that makes puppet heads tilt up.
            //
            // Fix: prefer HeadPlayer for head rotation sampling.
            Transform playerNode = localRoot.parent ?? localRoot; // GameController/Player
            Transform hp = playerNode.Find("HeadPlayer");
            if (hp != null)
            {
                if (cachedHeadMirror != hp)
                {
                    cachedHeadMirror = hp;
                    DiagnosticLog.Info(
                        "Head rotation source → HeadPlayer at: " +
                        LocalPlayerLocator.GetPath(hp));
                }
            }
            else
            {
                // Broad search if the above fails (unusual scene layout)
                for (int i = 0; i < playerNode.childCount; i++)
                {
                    Transform ch = playerNode.GetChild(i);
                    if (ch != null && ch != localRoot &&
                        ch.name.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (cachedHeadMirror != ch)
                        {
                            cachedHeadMirror = ch;
                            DiagnosticLog.Info(
                                "Head rotation source fallback → '" + ch.name +
                                "' at: " + LocalPlayerLocator.GetPath(ch));
                        }
                        break;
                    }
                }
            }

            // Animator: search on Person itself first, then siblings at Player level.
            // PlayerMove.animPerson may reference an animator on model(Clone) sibling.
            cachedAnimator = localRoot.GetComponentInChildren<Animator>(true);
            if (cachedAnimator == null && localRoot.parent != null)
            {
                Transform parent = localRoot.parent;
                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform sibling = parent.GetChild(i);
                    if (sibling == null || sibling == localRoot)
                        continue;

                    Animator found = sibling.GetComponentInChildren<Animator>(true);
                    if (found != null)
                    {
                        cachedAnimator = found;
                        DiagnosticLog.Info(
                            "Animator found on sibling '" + sibling.name + "': " +
                            LocalPlayerLocator.GetPath(found.transform));
                        break;
                    }
                }
            }

            if (cachedAnimator == null)
                DiagnosticLog.Warning(
                    "No Animator found on Person or any Player-level sibling. " +
                    "Animation will not sync.");
        }

        // ── Build snapshot ────────────────────────────────────────────────────
        private RemotePlayerState BuildState(Transform localRoot)
        {
            RemotePlayerState state = new RemotePlayerState();
            state.playerId    = localPlayerId;
            state.displayName = displayName;
            state.sceneName   = SceneManager.GetActiveScene().name;
            state.position    = NetVector3.FromUnity(localRoot.position);
            state.rotation    = NetQuaternion.FromUnity(localRoot.rotation);
            state.isVisible   = true;
            state.tick        = tick++;

            // Velocity / speed
            Vector3 velocity = CalculateVelocity(localRoot.position);
            state.velocity   = NetVector3.FromUnity(velocity);
            // Forward speed (Y-excluded) — maps to SpeedForward in the animator
            state.speed = new Vector3(velocity.x, 0f, velocity.z).magnitude;
            // Lateral component — maps to InertionRight
            state.lateralSpeed = localRoot.InverseTransformDirection(velocity).x;

            // Head rotation via HeadMirror (Person/HeadMirror confirmed in dump)
            if (cachedHeadMirror != null)
                state.headRotation = NetQuaternion.FromUnity(cachedHeadMirror.rotation);
            else
                state.headRotation = NetQuaternion.FromUnity(localRoot.rotation);

            // Grounded
            bool grounded;
            if (LocalPlayerLocator.TryReadGrounded(localRoot, out grounded))
                state.isGrounded = grounded;

            // Animator — use known MiSide parameter names
            if (cachedAnimator != null)
                FillKnownAnimatorParams(cachedAnimator, state);

            return state;
        }

        // ── Known MiSide animator params (no animator.parameters — unreliable in IL2CPP) ─────
        private static void FillKnownAnimatorParams(Animator animator, RemotePlayerState state)
        {
            List<AnimatorFloatParam> floats = new List<AnimatorFloatParam>();
            List<AnimatorBoolParam>  bools  = new List<AnimatorBoolParam>();
            List<AnimatorIntParam>   ints   = new List<AnimatorIntParam>();

            // ── Float parameters ──────────────────────────────────────────────
            TryAddFloat(animator, "SpeedForward",        HashSpeedForward,  floats);
            TryAddFloat(animator, "InertionRight",       HashInertionRight, floats);
            TryAddFloat(animator, "HeadMove",            HashHeadMove,      floats);
            TryAddFloat(animator, "MouseSpeed",          HashMouseSpeed,    floats);
            TryAddFloat(animator, "Forward",             HashForward,       floats);
            TryAddFloat(animator, "OtherAnimationType",  HashOtherAnimType, floats);
            TryAddFloat(animator, "OtherAnimationHold",  HashOtherAnimHold, floats);

            // ── Bool parameters ───────────────────────────────────────────────
            TryAddBool(animator, "Walk",     HashWalk,      bools);
            TryAddBool(animator, "Run",      HashRun,       bools);
            TryAddBool(animator, "Idle",     HashIdle,      bools);
            TryAddBool(animator, "Sit",      HashSit,       bools);
            TryAddBool(animator, "Jump",     HashJump,      bools);
            TryAddBool(animator, "JumpStop", HashJumpStop,  bools);
            TryAddBool(animator, "BedSit",   HashBedSit,    bools);
            TryAddBool(animator, "KickSit",  HashKickSit,   bools);
            TryAddBool(animator, "animstop", HashAnimStop,  bools);

            // ── Int parameters ────────────────────────────────────────────────
            TryAddInt(animator, "facelayer", HashFaceLayer, ints);

            // Layer weights
            int layerCount = Mathf.Min(animator.layerCount, 8);
            state.blendWeights = new float[layerCount];
            for (int i = 0; i < layerCount; i++)
            {
                try { state.blendWeights[i] = animator.GetLayerWeight(i); }
                catch (Exception) { state.blendWeights[i] = 0f; }
            }

            state.floatParameters = floats.ToArray();
            state.boolParameters  = bools.ToArray();
            state.intParameters   = ints.ToArray();
        }

        private static void TryAddFloat(Animator anim, string name, int hash, List<AnimatorFloatParam> list)
        {
            try
            {
                float val = anim.GetFloat(hash);
                AnimatorFloatParam p = new AnimatorFloatParam();
                p.name = name; p.value = val;
                list.Add(p);
            }
            catch (Exception) { }
        }

        private static void TryAddBool(Animator anim, string name, int hash, List<AnimatorBoolParam> list)
        {
            try
            {
                bool val = anim.GetBool(hash);
                AnimatorBoolParam p = new AnimatorBoolParam();
                p.name = name; p.value = val;
                list.Add(p);
            }
            catch (Exception) { }
        }

        private static void TryAddInt(Animator anim, string name, int hash, List<AnimatorIntParam> list)
        {
            try
            {
                int val = anim.GetInteger(hash);
                AnimatorIntParam p = new AnimatorIntParam();
                p.name = name; p.value = val;
                list.Add(p);
            }
            catch (Exception) { }
        }

        // ── Velocity ──────────────────────────────────────────────────────────
        private Vector3 CalculateVelocity(Vector3 currentPosition)
        {
            if (!hasLastPosition)
            {
                hasLastPosition = true;
                lastPosition    = currentPosition;
                return Vector3.zero;
            }

            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 vel = (currentPosition - lastPosition) / dt;
            lastPosition = currentPosition;
            return vel;
        }

        // ── Change detection ──────────────────────────────────────────────────
        private bool HasMeaningfulChange(RemotePlayerState cur, RemotePlayerState prev)
        {
            if (!hasLastSentState || prev == null) return true;
            if (Vector3.Distance(cur.position.ToUnity(), prev.position.ToUnity()) > 0.0025f) return true;
            if (Quaternion.Dot(cur.rotation.ToUnity(), prev.rotation.ToUnity()) < 0.9995f) return true;
            if (Quaternion.Dot(cur.headRotation.ToUnity(), prev.headRotation.ToUnity()) < 0.999f) return true;
            if (Mathf.Abs(cur.speed - prev.speed) > 0.01f) return true;
            if (Mathf.Abs(cur.lateralSpeed - prev.lateralSpeed) > 0.01f) return true;
            if (cur.isGrounded != prev.isGrounded) return true;
            return !FloatParamsEqual(cur.floatParameters, prev.floatParameters) ||
                   !BoolParamsEqual(cur.boolParameters, prev.boolParameters);
        }

        private static bool FloatParamsEqual(AnimatorFloatParam[] l, AnimatorFloatParam[] r)
        {
            if (l == null && r == null) return true;
            if (l == null || r == null) return false;
            if (l.Length != r.Length) return false;
            for (int i = 0; i < l.Length; i++)
                if (l[i].name != r[i].name || Mathf.Abs(l[i].value - r[i].value) > 0.005f) return false;
            return true;
        }

        private static bool BoolParamsEqual(AnimatorBoolParam[] l, AnimatorBoolParam[] r)
        {
            if (l == null && r == null) return true;
            if (l == null || r == null) return false;
            if (l.Length != r.Length) return false;
            for (int i = 0; i < l.Length; i++)
                if (l[i].name != r[i].name || l[i].value != r[i].value) return false;
            return true;
        }
    }
}
