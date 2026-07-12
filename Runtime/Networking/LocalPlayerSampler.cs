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
        private float  sendRate          = 40f;
        private float  heartbeatSeconds  = 1f;
        private bool   emitSnapshots     = true;

        // ── Timing ────────────────────────────────────────────────────────────
        private float nextSendTime;
        private float nextHeartbeatTime;
        private float nextErrorLogTime;
        private float nextGroundTruthLogTime;
        private int   tick;

        // ── Cached state ──────────────────────────────────────────────────────
        private bool              loggedFirstSnapshot;
        private bool              hasLastPosition;
        private Vector3           lastPosition;
        private bool              hasLastSentState;
        private RemotePlayerState lastSentState;

        // ── Dynamic animator parameter discovery ────────────────────────────────
        private Animator                 discoveredForAnimator;
        private bool                     discoveryAttempted;
        private bool                     discoverySucceeded;
        private List<DiscoveredParam>    discoveredParams = new List<DiscoveredParam>();

        private struct DiscoveredParam
        {
            public string name;
            public int hash;
            public AnimatorControllerParameterType type;
        }

        // ── Cached Unity references ───────────────────────────────────────────
        private Transform cachedHeadMirror;
        private Animator  cachedAnimator;
        private float     nextRefCacheTime;
        private const float RefCacheInterval = 5f;

        // ── Confirmed MiSide animator parameters ────────────────────────────────
        private static readonly int HashForward = Animator.StringToHash("Forward");
        private static readonly int HashRight   = Animator.StringToHash("Right");
        private static readonly int HashWalk    = Animator.StringToHash("Walk");
        private static readonly int HashRun     = Animator.StringToHash("Run");
        private static readonly int HashSit     = Animator.StringToHash("Sit");
        private static readonly int HashMove    = Animator.StringToHash("Move");
        private static readonly int HashIdle    = Animator.StringToHash("Idle");

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

                // Refresh cached references on schedule
                if (Time.unscaledTime >= nextRefCacheTime)
                {
                    nextRefCacheTime = Time.unscaledTime + RefCacheInterval;
                    RefreshCachedReferences(localRoot);
                }

                RemotePlayerState state = BuildState(localRoot);

                if (Time.unscaledTime >= nextGroundTruthLogTime && cachedAnimator != null)
                {
                    nextGroundTruthLogTime = Time.unscaledTime + 3f;
                    float gtForward = 0f, gtRight = 0f;
                    try { gtForward = cachedAnimator.GetFloat(HashForward); } catch (Exception) { }
                    try { gtRight   = cachedAnimator.GetFloat(HashRight);   } catch (Exception) { }
                    DiagnosticLog.Info(
                        "  [ground-truth local] animator='" + LocalPlayerLocator.GetPath(cachedAnimator.transform) +
                        "'  ctrl='" + (cachedAnimator.runtimeAnimatorController != null
                                       ? cachedAnimator.runtimeAnimatorController.name : "NULL") + "'" +
                        "  Forward=" + gtForward.ToString("F3") +
                        "  Right="   + gtRight.ToString("F3") +
                        "  computedSpeed=" + state.speed.ToString("F3"));
                }

                bool heartbeatDue = Time.unscaledTime >= nextHeartbeatTime;
                if (!HasMeaningfulChange(state, lastSentState) && !heartbeatDue)
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
                        "  head=" + (cachedHeadMirror != null ? "ok" : "miss") +
                        "  anim=" + (cachedAnimator   != null ? "ok" : "miss") +
                        "  model='" + (state.customModelName ?? "None") + "'");
                }
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= nextErrorLogTime)
                {
                    nextErrorLogTime = Time.unscaledTime + 5f;
                    DiagnosticLog.Warning(
                        "LocalPlayerSampler.Tick failed: " +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        // ── Reference caching ─────────────────────────────────────────────────
        private void RefreshCachedReferences(Transform localRoot)
        {
            if (Time.unscaledTime > -1000000f)
            {
                Transform playerRoot;
                Transform personRoot;
                ResolvePlayerHierarchy(localRoot, out playerRoot, out personRoot);

                Transform head = FindHeadSource(playerRoot, personRoot);
                if (cachedHeadMirror != head)
                {
                    cachedHeadMirror = head;
                    if (cachedHeadMirror != null)
                    {
                        DiagnosticLog.Info(
                            "Head source bound: " +
                            LocalPlayerLocator.GetPath(cachedHeadMirror));
                    }
                    else
                    {
                        DiagnosticLog.Warning(
                            "No HeadPlayer/HeadMirror/head bone found under local player root. " +
                            "Head look sync will use body rotation only.");
                    }
                }

                Animator anim = FindBestAnimator(playerRoot, personRoot);
                if (cachedAnimator != anim)
                {
                    cachedAnimator = anim;
                    if (cachedAnimator != null)
                    {
                        DiagnosticLog.Info(
                            "Animator source bound: " +
                            LocalPlayerLocator.GetPath(cachedAnimator.transform));
                    }
                    else
                    {
                        DiagnosticLog.Warning(
                            "No Animator found under GameController/Player, Person, or " +
                            "Player-level model siblings. Animation will not sync.");
                    }
                }

                return;
            }

            // ── HeadMirror ──
            Transform hm = localRoot.Find("HeadMirror");
            if (hm != null)
            {
                if (cachedHeadMirror != hm)
                {
                    cachedHeadMirror = hm;
                    DiagnosticLog.Info(
                        "HeadMirror bound: " + LocalPlayerLocator.GetPath(hm));
                }
            }
            else if (localRoot.parent != null)
            {
                Transform hp = localRoot.parent.Find("HeadPlayer");
                if (hp != null && hp != localRoot)
                {
                    cachedHeadMirror = hp;
                    DiagnosticLog.Info(
                        "HeadMirror → HeadPlayer at: " +
                        LocalPlayerLocator.GetPath(hp));
                }
            }

            // ── Animator ──
            cachedAnimator = localRoot.GetComponentInChildren<Animator>(true);
            if (cachedAnimator == null && localRoot.parent != null)
            {
                Transform parent = localRoot.parent;
                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform sib = parent.GetChild(i);
                    if (sib == null || sib == localRoot) continue;
                    Animator a = sib.GetComponentInChildren<Animator>(true);
                    if (a != null)
                    {
                        cachedAnimator = a;
                        DiagnosticLog.Info(
                            "Animator found on sibling '" + sib.name + "': " +
                            LocalPlayerLocator.GetPath(a.transform));
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
        private static void ResolvePlayerHierarchy(
            Transform localRoot,
            out Transform playerRoot,
            out Transform personRoot)
        {
            playerRoot = localRoot;
            personRoot = null;

            if (localRoot == null)
                return;

            if (string.Equals(localRoot.name, "Player", StringComparison.OrdinalIgnoreCase))
            {
                personRoot = localRoot.Find("Person");
                return;
            }

            if (string.Equals(localRoot.name, "Person", StringComparison.OrdinalIgnoreCase))
            {
                personRoot = localRoot;
                playerRoot = localRoot.parent;
                return;
            }

            Transform person = localRoot.Find("Person");
            if (person != null)
            {
                personRoot = person;
                playerRoot = localRoot;
                return;
            }

            personRoot = localRoot;
            playerRoot = localRoot.parent != null ? localRoot.parent : localRoot;
        }

        private static Transform FindHeadSource(Transform playerRoot, Transform personRoot)
        {
            Transform t = playerRoot != null ? playerRoot.Find("HeadPlayer") : null;
            if (t != null) return t;

            t = personRoot != null ? personRoot.Find("HeadMirror") : null;
            if (t != null) return t;

            t = playerRoot != null ? playerRoot.Find("Person/HeadMirror") : null;
            if (t != null) return t;

            t = FindHeadBone(personRoot);
            if (t != null) return t;

            return FindHeadBone(playerRoot);
        }

        private static Animator FindBestAnimator(Transform playerRoot, Transform personRoot)
        {
            Animator personAnim = personRoot != null
                ? personRoot.GetComponentInChildren<Animator>(true)
                : null;
            if (personAnim != null && personAnim.runtimeAnimatorController != null)
                return personAnim;

            if (playerRoot != null)
            {
                for (int i = 0; i < playerRoot.childCount; i++)
                {
                    Transform child = playerRoot.GetChild(i);
                    if (child == null || child == personRoot) continue;
                    if (child.name.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (child.name.IndexOf("Camera", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (VisualCloneUtility.CountRenderers(child) <= 0) continue;

                    Animator siblingAnimator = child.GetComponentInChildren<Animator>(true);
                    if (siblingAnimator != null && siblingAnimator.runtimeAnimatorController != null)
                        return siblingAnimator;
                }
            }

            if (personAnim != null)
                return personAnim;

            return playerRoot != null
                ? playerRoot.GetComponentInChildren<Animator>(true)
                : null;
        }

        private static readonly string[] HeadBonePaths =
        {
            "Person/Armature/Hips/Spine/Chest/Neck2/Neck1/Head",
            "Armature/Hips/Spine/Chest/Neck2/Neck1/Head",
            "Armature/Hips/Spine/Chest/Neck1/Head",
            "Armature/Hips/Spine/Chest/Neck/Head",
        };

        private static readonly string[] HeadBoneNames =
        {
            "Head",
            "head",
            "Neck1",
            "Neck",
            "HeadBone",
        };

        private static Transform FindHeadBone(Transform root)
        {
            if (root == null) return null;
            for (int i = 0; i < HeadBonePaths.Length; i++)
            {
                Transform t = root.Find(HeadBonePaths[i]);
                if (t != null) return t;
            }
            for (int i = 0; i < HeadBoneNames.Length; i++)
            {
                Transform t = FindByNameRecursive(root, HeadBoneNames[i]);
                if (t != null) return t;
            }
            return null;
        }

        private static Transform FindByNameRecursive(Transform root, string targetName)
        {
            if (root == null) return null;
            if (string.Equals(root.name, targetName, StringComparison.OrdinalIgnoreCase))
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform r = FindByNameRecursive(root.GetChild(i), targetName);
                if (r != null) return r;
            }
            return null;
        }

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

            Vector3 velocity  = CalculateVelocity(localRoot.position);
            state.velocity    = NetVector3.FromUnity(velocity);
            state.speed       = new Vector3(velocity.x, 0f, velocity.z).magnitude;
            state.lateralSpeed = localRoot.InverseTransformDirection(velocity).x;

            // Head rotation via HeadMirror
            if (cachedHeadMirror != null)
                state.headRotation = NetQuaternion.FromUnity(cachedHeadMirror.rotation);
            else
                state.headRotation = NetQuaternion.FromUnity(localRoot.rotation);

            // Grounded
            bool grounded;
            if (LocalPlayerLocator.TryReadGrounded(localRoot, out grounded))
                state.isGrounded = grounded;

            // ── Animator state hash + normalised time ─────────────────────────
            if (cachedAnimator != null)
            {
                try
                {
                    AnimatorStateInfo info = cachedAnimator.GetCurrentAnimatorStateInfo(0);
                    state.animatorStateHash      = info.shortNameHash;
                    state.animatorFullPathHash   = info.fullPathHash;
                    state.animatorNormalizedTime = info.normalizedTime;
                }
                catch (Exception) { }

                FillKnownAnimatorParams(cachedAnimator, state);
            }

            // ── Custom model name ────────────────────────────────────────────
            state.customModelName =
                CustomModelBridge.GetLocalPlayerModelName(localRoot);

            // ── Bone rotations ───────────────────────────────────────────────
            state.boneRotations = new NetQuaternion[BoneSyncData.Count];
            bool anyBoneFound = false;
            for (int i = 0; i < BoneSyncData.Count; i++)
            {
                Transform bone = localRoot.Find(BoneSyncData.BonePaths[i]);
                if (bone != null)
                {
                    state.boneRotations[i] = NetQuaternion.FromUnity(bone.localRotation);
                    anyBoneFound = true;
                }
                else
                {
                    state.boneRotations[i] = NetQuaternion.Identity;
                }
            }
            state.hasBoneData = anyBoneFound;

            return state;
        }

        // ── Animator parameter sampling ──────────────────────────────────────
        private void FillKnownAnimatorParams(Animator anim, RemotePlayerState state)
        {
            if (discoveredForAnimator != anim)
            {
                discoveredForAnimator = anim;
                discoveryAttempted    = false;
                discoverySucceeded    = false;
                discoveredParams.Clear();
            }

            if (!discoveryAttempted)
            {
                discoveryAttempted = true;
                TryDiscoverParameters(anim);
            }

            List<AnimatorFloatParam> floats = new List<AnimatorFloatParam>();
            List<AnimatorBoolParam>  bools  = new List<AnimatorBoolParam>();
            List<AnimatorIntParam>   ints   = new List<AnimatorIntParam>();

            if (discoverySucceeded)
            {
                for (int i = 0; i < discoveredParams.Count; i++)
                {
                    DiscoveredParam dp = discoveredParams[i];
                    if (string.IsNullOrEmpty(dp.name)) continue;
                    if (dp.type == AnimatorControllerParameterType.Float)
                        TryAddF(anim, dp.name, dp.hash, floats);
                    else if (dp.type == AnimatorControllerParameterType.Bool)
                        TryAddB(anim, dp.name, dp.hash, bools);
                    else if (dp.type == AnimatorControllerParameterType.Int)
                        TryAddI(anim, dp.name, dp.hash, ints);
                }
            }
            else
            {
                TryAddB(anim, "Walk", HashWalk, bools);
                TryAddB(anim, "Run",  HashRun,  bools);
                TryAddB(anim, "Sit",  HashSit,  bools);
                TryAddB(anim, "Move", HashMove, bools);
                TryAddB(anim, "Idle", HashIdle, bools);
            }

            TryAddF(anim, "Forward", HashForward, floats);
            TryAddF(anim, "Right",   HashRight,   floats);

            int layerCount = Mathf.Min(anim.layerCount, 8);
            state.blendWeights = new float[layerCount];
            for (int i = 0; i < layerCount; i++)
            {
                try { state.blendWeights[i] = anim.GetLayerWeight(i); }
                catch (Exception) { }
            }

            state.floatParameters = floats.ToArray();
            state.boolParameters  = bools.ToArray();
            state.intParameters   = ints.ToArray();
        }

        private void TryDiscoverParameters(Animator anim)
        {
            try
            {
                int count = anim.parameterCount;
                if (count <= 0) { discoverySucceeded = false; return; }

                int mangledCount = 0;
                for (int i = 0; i < count; i++)
                {
                    AnimatorControllerParameter p = anim.GetParameter(i);
                    if (p == null) continue;
                    DiscoveredParam dp = new DiscoveredParam();
                    dp.name = p.name;
                    dp.hash = Animator.StringToHash(p.name);
                    dp.type = p.type;
                    discoveredParams.Add(dp);
                    if (string.IsNullOrEmpty(p.name)) mangledCount++;
                }

                if (mangledCount > 0)
                    DiagnosticLog.Warning(
                        "Animator parameter discovery: " + mangledCount + " of " +
                        discoveredParams.Count + " parameter name(s) came back empty.");

                discoverySucceeded = discoveredParams.Count > 0;

                if (discoverySucceeded)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    sb.Append("Animator parameter discovery succeeded (");
                    sb.Append(discoveredParams.Count);
                    sb.Append(" params, controller='");
                    sb.Append(anim.runtimeAnimatorController != null
                              ? anim.runtimeAnimatorController.name : "?");
                    sb.Append("'): ");
                    for (int i = 0; i < discoveredParams.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(string.IsNullOrEmpty(discoveredParams[i].name)
                                  ? "<empty-name>" : discoveredParams[i].name);
                        sb.Append('[');
                        sb.Append(discoveredParams[i].type);
                        sb.Append(']');
                    }
                    DiagnosticLog.Info(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                discoverySucceeded = false;
                discoveredParams.Clear();
                DiagnosticLog.Warning(
                    "Animator parameter enumeration unavailable for controller '" +
                    (anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "?") +
                    "' (" + ex.GetType().Name + ") — using known-name fallback.");
            }
        }

        private static void TryAddF(Animator a, string name, int hash,
                                     List<AnimatorFloatParam> list)
        {
            try
            {
                AnimatorFloatParam p = new AnimatorFloatParam();
                p.name  = name;
                p.value = a.GetFloat(hash);
                list.Add(p);
            }
            catch (Exception) { }
        }

        private static void TryAddB(Animator a, string name, int hash,
                                     List<AnimatorBoolParam> list)
        {
            try
            {
                AnimatorBoolParam p = new AnimatorBoolParam();
                p.name  = name;
                p.value = a.GetBool(hash);
                list.Add(p);
            }
            catch (Exception) { }
        }

        private static void TryAddI(Animator a, string name, int hash,
                                     List<AnimatorIntParam> list)
        {
            try
            {
                AnimatorIntParam p = new AnimatorIntParam();
                p.name  = name;
                p.value = a.GetInteger(hash);
                list.Add(p);
            }
            catch (Exception) { }
        }

        // ── Velocity ──────────────────────────────────────────────────────────
        private Vector3 CalculateVelocity(Vector3 currentPos)
        {
            if (!hasLastPosition)
            {
                hasLastPosition = true;
                lastPosition    = currentPos;
                return Vector3.zero;
            }
            float dt  = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 v = (currentPos - lastPosition) / dt;
            lastPosition = currentPos;

            const float velocityDeadzone = 0.18f;
            if (v.sqrMagnitude < velocityDeadzone * velocityDeadzone)
                v = Vector3.zero;

            return v;
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
            if (cur.animatorStateHash != prev.animatorStateHash) return true;
            if (cur.animatorFullPathHash != prev.animatorFullPathHash) return true;
            if (Mathf.Abs(cur.animatorNormalizedTime - prev.animatorNormalizedTime) > 0.025f) return true;
            if (cur.customModelName != prev.customModelName) return true;
            if (!BoolParamsEqual(cur.boolParameters, prev.boolParameters)) return true;
            if (!FloatParamsEqual(cur.floatParameters, prev.floatParameters)) return true;
            return false;
        }

        private static bool FloatParamsEqual(AnimatorFloatParam[] l, AnimatorFloatParam[] r)
        {
            if (l == null && r == null) return true;
            if (l == null || r == null || l.Length != r.Length) return false;
            for (int i = 0; i < l.Length; i++)
                if (l[i].name != r[i].name || Mathf.Abs(l[i].value - r[i].value) > 0.005f)
                    return false;
            return true;
        }

        private static bool BoolParamsEqual(AnimatorBoolParam[] l, AnimatorBoolParam[] r)
        {
            if (l == null && r == null) return true;
            if (l == null || r == null || l.Length != r.Length) return false;
            for (int i = 0; i < l.Length; i++)
                if (l[i].name != r[i].name || l[i].value != r[i].value) return false;
            return true;
        }
    }
}