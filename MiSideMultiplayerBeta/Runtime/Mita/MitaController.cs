using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class MitaController
    {
        private sealed class AnimatorRuntimeState
        {
            public Animator Animator;
            public float Speed;
            public bool ApplyRootMotion;
            public AnimatorCullingMode CullingMode;
            public bool Enabled;
        }

        private sealed class BonePoseSample
        {
            public float ReceivedAt;
            public Dictionary<string, BoneTransformData> Bones;
        }

        private sealed class Entry
        {
            public Component MitaPerson;
            public Transform MitaRoot;
            public Transform PersonRoot;
            public NavMeshAgent Nma;
            public MethodInfo MagnetToTargetMethod;
            public MethodInfo MagnetOffMethod;
            public FieldInfo MagnetTargetField;
            public GameObject MagnetProxy;
            public GameObject GO;
            public Animator[] Animators;
            public Behaviour[] AiBehaviours;
            public string DisabledAiSignature;
            public bool HasState;
            public string LastSnapshotId;
            public bool WarnedMissing;
            public bool AiSuppressed;
            public bool HasCorrection;
            public bool AiComponentSuppressed;
            public bool LocalGameCardAIWasRestored;
            public bool LocalPuppetLockLogged;
            public bool AnimatorFreezeLogged;
            public int LastFrozenAnimatorCount;
            public Dictionary<string, BoneTransformData> LatestBones;
            public readonly List<BonePoseSample> BoneSamples = new List<BonePoseSample>(8);
            public Dictionary<string, BoneTransformData> InterpolatedBones;
            public bool HasRootTarget;
            public Vector3 RootTargetPosition;
            public Quaternion RootTargetRotation = Quaternion.identity;
            public Vector3 RootPositionVelocity;
            public bool WarnedMissingBoneTargets;
            public string StateAuthorityId;
            public bool HasNavMeshEnabledState;
            public bool NavMeshEnabledBeforePuppet;
            public readonly Dictionary<int, bool> AiEnabledBeforePuppet =
                new Dictionary<int, bool>();
            public readonly Dictionary<int, AnimatorRuntimeState> AnimatorStates =
                new Dictionary<int, AnimatorRuntimeState>();
            public float NextBoneApplyLogTime;
            public float NextBoneMismatchLogTime;
            public float NextAnimatorScanTime;
            public float NextAiScanTime;
            public float NextPersonRootScanTime;
            public float NextVerboseStateLogTime;
        }

        private static MitaController _active;

        private string localPlayerId;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly Dictionary<int, float> _lastHostTimeByLocalMita = new Dictionary<int, float>();
        private string lastSceneName;
        private bool hasReceivedHostState;

        private const float PositionCorrectionThreshold = 0.005f;
        private const float RotationCorrectionThresholdDegrees = 0.15f;
        private const float RootSmoothTimeSeconds = 0.075f;
        private const float RootTeleportDistance = 8f;
        private const float SyncDiagnosticIntervalSeconds = 5f;
        private const float BoneInterpolationDelaySeconds = 0.12f;
        private const int MaxBufferedBoneSamples = 8;

        public void Configure(string playerId)
        {
            localPlayerId = playerId;
            MitaHost.Configure(playerId, MitaPlayerOverride.ConfiguredHostId);
            _active = this;
        }

        public static void ForceRescan()
        {
            MitaController c = _active;
            if (c == null) return;
            c._entries.Clear();
            c._lastHostTimeByLocalMita.Clear();
            c.lastSceneName = SceneManager.GetActiveScene().name;
            c.hasReceivedHostState = false;
            MitaBoneSync.ClearBindings();
            MitaEventSync.ResetAppliedSequences();
            DiagnosticLog.Info("MitaController: ForceRescan complete.");
        }

        public static bool HasReceivedHostState
        {
            get { return _active != null && _active.hasReceivedHostState; }
        }

        public static bool HasAppliedSnapshotState(string path, string snapshotId)
        {
            MitaController controller = _active;
            if (controller == null || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(snapshotId))
                return false;

            Entry entry;
            return controller._entries.TryGetValue(path, out entry) && entry != null &&
                   entry.MitaPerson != null && entry.HasState &&
                   !entry.HasCorrection &&
                   string.Equals(entry.LastSnapshotId, snapshotId, StringComparison.Ordinal);
        }

        internal static void ReceiveGameCardMitaState(MitaState state)
        {
            MitaController controller = _active;
            if (controller != null) controller.OnRemoteMitaStateReceived(state);
        }

        public static void SuppressLocalAIForAll()
        {
            MitaController controller = _active;
            if (controller == null || MitaHost.IsHost || MitaHost.IsScene5SharedMode) return;

            try
            {
                UnityEngine.Object[] allObjects = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i].TryCast<GameObject>();
                    if (go == null || !go.activeInHierarchy || !go.scene.IsValid() || !go.scene.isLoaded)
                        continue;

                    Component person = go.GetComponent("MitaPerson");
                    if (person == null) continue;

                    string path = MitaBoneSync.GetNetworkPath(go.transform);
                    if (MinigameSync.IsLocalGameCardMitaPath(path)) continue;
                    Entry entry = controller.GetOrCreateEntry(path, go.transform.position);
                    if (entry != null) controller.SuppressLocalAI(entry);
                }

                DiagnosticLog.Info("MitaController: suppressed local Mita AI before authoritative host state arrived.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaController.SuppressLocalAIForAll failed: " + ex.Message);
            }
        }

        public void OnRemoteMitaStateReceived(MitaState state)
        {
            if (state == null || string.IsNullOrEmpty(state.senderId)) return;
            if (state.senderId == localPlayerId) return;

            if (MinigameSync.IsGameCardMitaPath(state.mitaPath))
            {
                // Game Card's Mita is simulated locally by the player who
                // owns the minigame. Ignore legacy pose packets from builds
                // that still replicated it through MitaSync.
                return;
            }

            // Scene 5 runs local Mita AI on every peer. Use the host's load
            // snapshot as an initial pose, then let each local Mita own its AI.
            if (MitaHost.IsScene5SharedMode && !state.isFullSnapshot) return;

            if (!MitaHost.IsHost && state.senderId == MitaHost.HostPlayerId)
            {
                try
                {
                    ApplyRemoteState(state);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MitaController: failed to process host state for '" +
                                          (state.mitaPath ?? "<no path>") + "': " +
                                          ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private void ApplyRemoteState(MitaState state)
        {
            if (string.IsNullOrEmpty(state.mitaPath))
            {
                DiagnosticLog.Warning("MitaController: state with no mitaPath, ignoring.");
                return;
            }

            if (!string.IsNullOrEmpty(state.sceneName) &&
                state.sceneName != SceneManager.GetActiveScene().name)
                return;

            Entry e = GetOrCreateEntry(state.mitaPath, state.position.ToUnity());
            if (e == null) return;

            // A Mita can be replaced/reparented by a quest transition. Several
            // host paths may therefore resolve to the same local rendered
            // character. Drop delayed packets from the old path and retire its
            // stale entry so it cannot overwrite the new authoritative pose.
            int localMitaId;
            try { localMitaId = e.MitaPerson.GetInstanceID(); }
            catch { return; }
            if (!string.Equals(e.StateAuthorityId, state.senderId, StringComparison.Ordinal))
            {
                _lastHostTimeByLocalMita.Remove(localMitaId);
                e.BoneSamples.Clear();
                e.InterpolatedBones = null;
                e.LatestBones = null;
                e.StateAuthorityId = state.senderId;
            }
            if (state.hostSendTime > 0f)
            {
                float lastHostTime;
                if (_lastHostTimeByLocalMita.TryGetValue(localMitaId, out lastHostTime) &&
                    state.hostSendTime + 0.001f < lastHostTime)
                {
                    DiagnosticLog.Verbose("MitaController: ignored stale host pose for '" +
                                          state.mitaPath + "' (host time " +
                                          state.hostSendTime.ToString("F3") + " < " +
                                          lastHostTime.ToString("F3") + ").");
                    return;
                }
                _lastHostTimeByLocalMita[localMitaId] = state.hostSendTime;
            }
            RetireSupersededBindings(state.mitaPath, e);

            RefreshPersonRoot(e, state.mitaPath);

            if (!e.MitaPerson.gameObject.activeInHierarchy)
                e.MitaPerson.gameObject.SetActive(true);

            if (!MitaHost.IsScene5SharedMode)
                SuppressLocalAI(e);
            MitaLifeEmotionSync.Apply(e.GO, state.lifeEmotion);
            hasReceivedHostState = true;
            bool firstState = !e.HasState;
            Transform poseRoot = GetPoseRoot(e);
            if (poseRoot == null) return;
            float positionError = Vector3.Distance(poseRoot.position, state.position.ToUnity());
            SetRootTarget(e, state.position.ToUnity(), state.rotation.ToUnity(), firstState);
            if (state.Bones != null && state.Bones.Count > 0)
            {
                e.LatestBones = state.Bones;
                AddBonePoseSample(e, state.Bones);
            }
            if (state.isFullSnapshot || firstState || Time.unscaledTime >= e.NextVerboseStateLogTime)
            {
                e.NextVerboseStateLogTime = Time.unscaledTime + SyncDiagnosticIntervalSeconds;
                DiagnosticLog.Info("[MITA-SYNC RX] authority='" + state.senderId + "' scene='" +
                                   state.sceneName + "' path='" + state.mitaPath + "' pos=" +
                                   state.position.ToUnity() + " error=" +
                                   positionError.ToString("F2") + "m bones=" +
                                   (state.Bones == null ? 0 : state.Bones.Count) + " fullSnapshot=" +
                                   state.isFullSnapshot + " correctionPending=" + e.HasCorrection +
                                   " localAIStopped=" + e.AiSuppressed + ".");
            }
            if (!e.HasState)
                DiagnosticLog.Info("MitaController: syncing '" + state.mitaPath +
                                   "' from authority '" + state.senderId + "'.");
            e.HasState = true;

            MitaGazeState.SetRemote(state.mitaPath, state.gazeTargetPlayerId);

            if (e.MagnetToTargetMethod != null && e.MagnetOffMethod != null && e.MagnetTargetField != null)
            {
                try
                {
                    Transform currentMagnet = e.MagnetTargetField.GetValue(e.MitaPerson) as Transform;

                    if (state.magnetActive)
                    {
                        if (e.MagnetProxy == null)
                        {
                            e.MagnetProxy = new GameObject("MitaMagnetProxy_" +
                                state.mitaPath.GetHashCode().ToString("X8"));
                            e.MagnetProxy.hideFlags = HideFlags.HideAndDontSave;
                        }
                        e.MagnetProxy.transform.position = state.magnetTargetPos.ToUnity();
                        e.MagnetProxy.transform.rotation = state.magnetTargetRot.ToUnity();

                        if (currentMagnet == null || currentMagnet.position != e.MagnetProxy.transform.position)
                            e.MagnetToTargetMethod.Invoke(e.MitaPerson, new object[] { e.MagnetProxy.transform });
                    }
                    else
                    {
                        if (currentMagnet != null)
                            e.MagnetOffMethod.Invoke(e.MitaPerson, null);
                        if (e.MagnetProxy != null)
                        {
                            UnityEngine.Object.Destroy(e.MagnetProxy);
                            e.MagnetProxy = null;
                        }
                    }
                }
                catch { }
            }

            if (state.isFullSnapshot)
            {
                MitaEventSync.ApplySnapshotEvents(
                    state.mitaPath, state.events, state.lastEventSequence);
                e.LastSnapshotId = state.snapshotId;
                DiagnosticLog.Verbose("MitaController: applied snapshot record " +
                                      (state.snapshotIndex + 1) + "/" + state.snapshotCount +
                                      " path='" + state.mitaPath + "'.");
            }
            else if (state.events != null && state.events.Length > 0)
            {
                for (int i = 0; i < state.events.Length; i++)
                {
                    MitaEvent ev = state.events[i];
                    if (ev == null || ev.mitaPath != state.mitaPath) continue;
                    MitaEventSync.ApplyLiveEvent(ev);
                }
            }
        }

        private void SuppressLocalAI(Entry e)
        {
            if (e == null || e.MitaPerson == null) return;
            if (MinigameSync.IsLocalGameCardMitaPath(
                    MitaBoneSync.GetNetworkPath(e.GO.transform)))
            {
                RestoreLocalGameCardAI(e);
                return;
            }

            try
            {
                e.LocalGameCardAIWasRestored = false;
                if (e.Nma != null && e.Nma.enabled)
                {
                    if (!e.HasNavMeshEnabledState)
                    {
                        e.NavMeshEnabledBeforePuppet = e.Nma.enabled;
                        e.HasNavMeshEnabledState = true;
                    }
                    e.Nma.enabled = false;
                }

                SuppressGameMitaBehaviours(e);
                e.AiSuppressed = e.AiComponentSuppressed &&
                                 (e.Nma == null || !e.Nma.enabled);
            }
            catch (Exception ex)
            {
                e.AiSuppressed = false;
                DiagnosticLog.Warning("MitaController.SuppressLocalAI failed: " + ex.Message);
            }

            FreezeLocalAnimation(e);
        }

        private static void SuppressGameMitaBehaviours(Entry e)
        {
            if (e == null || e.GO == null) return;

            if (e.AiBehaviours == null || Time.unscaledTime >= e.NextAiScanTime)
            {
                e.NextAiScanTime = Time.unscaledTime + 0.25f;
                List<Behaviour> behaviours = new List<Behaviour>();
                HashSet<int> seen = new HashSet<int>();
                CollectGameMitaBehaviours(e.GO.transform, behaviours, seen);
                if (e.PersonRoot != null && !e.PersonRoot.IsChildOf(e.GO.transform))
                    CollectGameMitaBehaviours(e.PersonRoot, behaviours, seen);
                e.AiBehaviours = behaviours.ToArray();
            }

            bool allDisabled = true;
            for (int i = 0; i < e.AiBehaviours.Length; i++)
            {
                Behaviour behaviour = e.AiBehaviours[i];
                if (behaviour == null) continue;
                try
                {
                    int instanceId = behaviour.GetInstanceID();
                    if (!e.AiEnabledBeforePuppet.ContainsKey(instanceId))
                        e.AiEnabledBeforePuppet[instanceId] = behaviour.enabled;
                    if (behaviour.enabled) behaviour.enabled = false;
                    if (behaviour.enabled) allDisabled = false;
                }
                catch { allDisabled = false; }
            }
            e.AiComponentSuppressed = allDisabled && e.AiBehaviours.Length > 0;

            string signature = DescribeBehaviours(e.AiBehaviours);
            if (!e.LocalPuppetLockLogged ||
                !string.Equals(signature, e.DisabledAiSignature, StringComparison.Ordinal))
            {
                e.LocalPuppetLockLogged = true;
                e.DisabledAiSignature = signature;
                DiagnosticLog.Info("MitaController: joiner puppet lock on '" + e.GO.name +
                                   "'; disabled " + e.AiBehaviours.Length +
                                   " game Mita behaviour(s) across logic and visual roots [" +
                                   signature + "]; NavMeshAgent=" +
                                   (e.Nma == null ? "missing" : e.Nma.enabled.ToString()) +
                                   ". Remote Mita snapshots supply its pose.");
            }
        }

        private static void RestoreLocalGameCardAI(Entry e)
        {
            if (e == null || e.GO == null || e.LocalGameCardAIWasRestored) return;

            try
            {
                List<Behaviour> currentBehaviours = new List<Behaviour>();
                HashSet<int> currentIds = new HashSet<int>();
                CollectGameMitaBehaviours(e.GO.transform, currentBehaviours, currentIds);
                if (e.PersonRoot != null && !e.PersonRoot.IsChildOf(e.GO.transform))
                    CollectGameMitaBehaviours(e.PersonRoot, currentBehaviours, currentIds);
                if (currentBehaviours.Count > 0)
                {
                    for (int i = 0; i < currentBehaviours.Count; i++)
                    {
                        Behaviour behaviour = currentBehaviours[i];
                        if (behaviour == null) continue;
                        int id = behaviour.GetInstanceID();
                        bool enabledBefore;
                        if (e.AiEnabledBeforePuppet.TryGetValue(id, out enabledBefore) &&
                            behaviour.enabled != enabledBefore)
                        {
                            behaviour.enabled = enabledBefore;
                        }
                    }
                }
                if (e.Nma != null && e.HasNavMeshEnabledState &&
                    e.Nma.enabled != e.NavMeshEnabledBeforePuppet)
                    e.Nma.enabled = e.NavMeshEnabledBeforePuppet;
                foreach (KeyValuePair<int, AnimatorRuntimeState> pair in e.AnimatorStates)
                {
                    AnimatorRuntimeState state = pair.Value;
                    Animator animator = state != null ? state.Animator : null;
                    if (animator == null) continue;
                    animator.speed = state.Speed;
                    animator.applyRootMotion = state.ApplyRootMotion;
                    animator.cullingMode = state.CullingMode;
                    animator.enabled = state.Enabled;
                }
                e.AiSuppressed = false;
                e.AiComponentSuppressed = false;
                e.AnimatorFreezeLogged = false;
                e.LastFrozenAnimatorCount = 0;
                e.HasCorrection = false;
                e.HasRootTarget = false;
                e.LocalGameCardAIWasRestored = true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaController: restoring local Game Card Mita AI failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void CollectGameMitaBehaviours(Transform root,
                                                       List<Behaviour> result,
                                                       HashSet<int> seen)
        {
            if (root == null || result == null || seen == null) return;

            Behaviour[] components = root.GetComponentsInChildren<Behaviour>(true);
            if (components == null) return;

            string gameAssembly = typeof(MitaPerson).Assembly.GetName().Name;
            for (int i = 0; i < components.Length; i++)
            {
                Behaviour behaviour = components[i];
                if (behaviour == null || behaviour is Animator ||
                    behaviour is NavMeshAgent || !IsGameMitaBehaviour(behaviour, gameAssembly))
                    continue;

                int instanceId;
                try { instanceId = behaviour.GetInstanceID(); }
                catch { continue; }
                if (seen.Add(instanceId)) result.Add(behaviour);
            }
        }

        private static bool IsGameMitaBehaviour(Behaviour behaviour, string gameAssembly)
        {
            try
            {
                string assemblyName = behaviour.GetType().Assembly.GetName().Name;
                return string.Equals(assemblyName, gameAssembly, StringComparison.Ordinal) ||
                       assemblyName.StartsWith("Assembly-CSharp", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string DescribeBehaviours(Behaviour[] behaviours)
        {
            if (behaviours == null || behaviours.Length == 0) return "none found";
            List<string> names = new List<string>();
            for (int i = 0; i < behaviours.Length && i < 16; i++)
            {
                Behaviour behaviour = behaviours[i];
                if (behaviour == null) continue;
                string name;
                try { name = behaviour.GetType().Name; }
                catch { name = "<unknown>"; }
                if (!names.Contains(name)) names.Add(name);
            }
            return string.Join(", ", names.ToArray());
        }

        private static void FreezeLocalAnimation(Entry e)
        {
            if (e == null || e.GO == null) return;

            if (Time.unscaledTime >= e.NextAnimatorScanTime)
            {
                // Mita can replace its visual model while a quest transition
                // runs. Rebind promptly so a newly-created Animator cannot
                // overwrite the host pose for a long scan interval.
                e.NextAnimatorScanTime = Time.unscaledTime + 0.25f;
                try
                {
                    Transform animationRoot = e.PersonRoot != null
                        ? e.PersonRoot
                        : e.GO.transform;
                    Animator[] current = animationRoot.GetComponentsInChildren<Animator>(true);
                    if (!SameAnimators(e.Animators, current))
                    {
                        e.Animators = current;
                        e.AnimatorFreezeLogged = false;
                    }
                }
                catch
                {
                    e.Animators = null;
                    e.AnimatorFreezeLogged = false;
                }
            }

            if (e.Animators == null)
            {
                e.AnimatorFreezeLogged = false;
                e.LastFrozenAnimatorCount = 0;
                return;
            }
            int frozen = 0;
            for (int i = 0; i < e.Animators.Length; i++)
            {
                Animator animator = e.Animators[i];
                if (animator == null) continue;
                try
                {
                    int instanceId = animator.GetInstanceID();
                    if (!e.AnimatorStates.ContainsKey(instanceId))
                    {
                        e.AnimatorStates[instanceId] = new AnimatorRuntimeState
                        {
                            Animator = animator,
                            Speed = animator.speed,
                            ApplyRootMotion = animator.applyRootMotion,
                            CullingMode = animator.cullingMode,
                            Enabled = animator.enabled
                        };
                    }
                    if (animator.speed != 0f) animator.speed = 0f;
                    if (animator.applyRootMotion) animator.applyRootMotion = false;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    if (!animator.enabled) animator.enabled = true;
                    frozen++;
                }
                catch { }
            }

            if (frozen > 0 &&
                (!e.AnimatorFreezeLogged || e.LastFrozenAnimatorCount != frozen))
            {
                DiagnosticLog.Info("MitaController: locked " + frozen +
                                   " local Animator(s) at speed 0 for '" + e.GO.name +
                                   "'; host BoneSync overwrites the pose in LateTick.");
            }
            e.AnimatorFreezeLogged = frozen > 0;
            e.LastFrozenAnimatorCount = frozen;
        }

        private static bool SameAnimators(Animator[] first, Animator[] second)
        {
            if (first == null || second == null) return first == second;
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++)
                if (first[i] != second[i]) return false;
            return true;
        }

        private static void RefreshPersonRoot(Entry e, string path)
        {
            if (e == null || GetPoseRoot(e) == null) return;

            if (e.PersonRoot != null && Time.unscaledTime < e.NextPersonRootScanTime) return;
            e.NextPersonRootScanTime = Time.unscaledTime + 0.5f;

            Transform previous = e.PersonRoot;
            Transform refreshed = MitaBoneSync.FindPersonRoot(e.MitaRoot);
            if (refreshed == null || refreshed == previous) return;

            e.PersonRoot = refreshed;
            e.WarnedMissingBoneTargets = false;
            DiagnosticLog.Info("[MITA-SYNC RIG] refreshed joiner rig root for '" + path +
                               "' to '" + MitaBoneSync.GetNetworkPath(refreshed) + "'.");
        }

        private static void SetRootTarget(Entry e, Vector3 targetPosition,
                                          Quaternion targetRotation, bool firstState)
        {
            Transform poseRoot = GetPoseRoot(e);
            if (poseRoot == null) return;

            float positionError = Vector3.Distance(poseRoot.position, targetPosition);
            float rotationError = Quaternion.Angle(poseRoot.rotation, targetRotation);
            e.RootTargetPosition = targetPosition;
            e.RootTargetRotation = targetRotation;
            if (firstState || !e.HasRootTarget || positionError > RootTeleportDistance)
            {
                poseRoot.SetPositionAndRotation(targetPosition, targetRotation);
                e.RootPositionVelocity = Vector3.zero;
                e.HasRootTarget = true;
                e.HasCorrection = false;
                return;
            }

            e.HasRootTarget = true;
            e.HasCorrection = positionError > PositionCorrectionThreshold ||
                              rotationError > RotationCorrectionThresholdDegrees;
        }

        private static Transform GetPoseRoot(Entry e)
        {
            return e == null ? null : (e.PersonRoot != null ? e.PersonRoot : e.MitaRoot);
        }

        public void Tick()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != lastSceneName)
            {
                lastSceneName = sceneName;
                MitaEventSync.ResetAppliedSequences();
                foreach (var kv in _entries)
                {
                    Entry e = kv.Value;
                    if (e != null && e.MagnetProxy != null)
                    {
                        UnityEngine.Object.Destroy(e.MagnetProxy);
                        e.MagnetProxy = null;
                    }
                }
                _entries.Clear();
                _lastHostTimeByLocalMita.Clear();
                MitaBoneSync.ClearBindings();
                hasReceivedHostState = false;
            }

            bool hostOrSharedScene = MitaHost.IsHost || MitaHost.IsScene5SharedMode;
            bool hasRemoteGameCardOwner = MinigameSync.HasRemoteGameCardOwner;
            if (hostOrSharedScene && !hasRemoteGameCardOwner) return;

            // Keep reasserting the puppet lock. Quest scripts can toggle
            // components while a scene transition or animation is running.
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                Entry e = pair.Value;
                if (e == null || e.MitaPerson == null) continue;
                RefreshPersonRoot(e, pair.Key);
                if (MinigameSync.IsGameCardMitaPath(pair.Key))
                {
                    if (MinigameSync.IsLocalGameCardOwner)
                        RestoreLocalGameCardAI(e);
                    else if (hasRemoteGameCardOwner)
                        SuppressLocalAI(e);
                }
                else if (!hostOrSharedScene)
                    SuppressLocalAI(e);
            }
        }

        public static void LateTick()
        {
            MitaSampler.TickLate();

            MitaController controller = _active;
            if (controller == null || MitaHost.IsScene5SharedMode ||
                (MitaHost.IsHost && !MinigameSync.HasRemoteGameCardOwner)) return;
            controller.ApplyRemoteCorrections();
            controller.ApplyLatestBoneSnapshots();
        }

        private void ApplyRemoteCorrections()
        {
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                Entry e = pair.Value;
                if (e == null || e.MitaPerson == null || !e.HasState ||
                    !e.HasRootTarget || GetPoseRoot(e) == null)
                    continue;
                if (MinigameSync.IsLocalGameCardMitaPath(pair.Key)) continue;

                try
                {
                    Transform poseRoot = GetPoseRoot(e);
                    float deltaTime = Time.unscaledDeltaTime;
                    if (deltaTime <= 0f) continue;

                    float positionError = Vector3.Distance(poseRoot.position, e.RootTargetPosition);
                    float rotationError = Quaternion.Angle(poseRoot.rotation, e.RootTargetRotation);
                    e.HasCorrection = positionError > PositionCorrectionThreshold ||
                                      rotationError > RotationCorrectionThresholdDegrees;
                    if (!e.HasCorrection)
                    {
                        e.RootPositionVelocity = Vector3.zero;
                        continue;
                    }

                    poseRoot.position = Vector3.SmoothDamp(
                        poseRoot.position, e.RootTargetPosition,
                        ref e.RootPositionVelocity, RootSmoothTimeSeconds,
                        Mathf.Infinity, deltaTime);
                    float rotationT = 1f - Mathf.Exp(-24f * deltaTime);
                    poseRoot.rotation = Quaternion.Slerp(
                        poseRoot.rotation, e.RootTargetRotation, rotationT);

                    float finalPositionError = Vector3.Distance(poseRoot.position, e.RootTargetPosition);
                    float finalRotationError = Quaternion.Angle(poseRoot.rotation, e.RootTargetRotation);
                    if (finalPositionError <= PositionCorrectionThreshold &&
                        finalRotationError <= RotationCorrectionThresholdDegrees)
                    {
                        poseRoot.SetPositionAndRotation(e.RootTargetPosition, e.RootTargetRotation);
                        e.RootPositionVelocity = Vector3.zero;
                        e.HasCorrection = false;
                    }
                }
                catch (Exception ex)
                {
                    if (Time.unscaledTime >= e.NextBoneMismatchLogTime)
                    {
                        e.NextBoneMismatchLogTime = Time.unscaledTime + SyncDiagnosticIntervalSeconds;
                        DiagnosticLog.Warning("MitaController: root correction failed for '" +
                                              pair.Key + "': " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        private void ApplyLatestBoneSnapshots()
        {
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                Entry e = pair.Value;
                if (e == null) continue;
                try
                {
                    if (MinigameSync.IsLocalGameCardMitaPath(pair.Key))
                    {
                        RestoreLocalGameCardAI(e);
                        continue;
                    }
                    FreezeLocalAnimation(e);
                    RefreshPersonRoot(e, pair.Key);
                    if (!e.HasState || e.PersonRoot == null) continue;

                    Dictionary<string, BoneTransformData> pose = GetBufferedBonePose(e);
                    if (pose == null) pose = e.LatestBones;
                    int applied = pose == null ? 0 : MitaBoneSync.Apply(e.PersonRoot, pose);
                    if (Time.unscaledTime >= e.NextBoneApplyLogTime)
                    {
                        e.NextBoneApplyLogTime = Time.unscaledTime + SyncDiagnosticIntervalSeconds;
                        DiagnosticLog.Info("[MITA-SYNC APPLY] scene='" + SceneManager.GetActiveScene().name +
                                           "' path='" + pair.Key + "' matched=" + applied + "/" +
                                           (pose == null ? 0 : pose.Count) + " bufferedSamples=" + e.BoneSamples.Count +
                                           " hostBones=" + (pose == null ? 0 : pose.Count) +
                                           " rootPos=" + GetPoseRoot(e).position +
                                           " animatorLocked=" + e.AnimatorFreezeLogged + ".");
                    }
                    if (pose != null && applied < pose.Count &&
                        Time.unscaledTime >= e.NextBoneMismatchLogTime)
                    {
                        e.NextBoneMismatchLogTime = Time.unscaledTime + SyncDiagnosticIntervalSeconds;
                        DiagnosticLog.Warning("[MITA-SYNC MISMATCH] path='" + pair.Key +
                                              "' hostSent=" + pose.Count +
                                              " joinerApplied=" + applied +
                                              " personRoot='" + e.PersonRoot.name + "'; " +
                                              MitaBoneSync.DescribeApplyCandidates(e.PersonRoot, e.LatestBones));
                    }
                }
                catch (Exception ex)
                {
                    if (Time.unscaledTime >= e.NextBoneMismatchLogTime)
                    {
                        e.NextBoneMismatchLogTime = Time.unscaledTime + SyncDiagnosticIntervalSeconds;
                        DiagnosticLog.Warning("MitaController: bone apply failed for '" + pair.Key +
                                              "': " + ex.GetType().Name + ": " + ex.Message + ". " +
                                              MitaBoneSync.DescribeRig(e.PersonRoot));
                    }
                }
            }
        }

        private static void AddBonePoseSample(Entry entry,
                                               Dictionary<string, BoneTransformData> bones)
        {
            if (entry == null || bones == null || bones.Count == 0) return;
            float receivedAt = Time.unscaledTime;
            if (entry.BoneSamples.Count > 0)
            {
                BonePoseSample last = entry.BoneSamples[entry.BoneSamples.Count - 1];
                if (last != null && ReferenceEquals(last.Bones, bones)) return;
                if (last != null && receivedAt <= last.ReceivedAt)
                    receivedAt = last.ReceivedAt + 0.0001f;
            }

            BonePoseSample sample = new BonePoseSample();
            sample.ReceivedAt = receivedAt;
            sample.Bones = bones;
            entry.BoneSamples.Add(sample);
            while (entry.BoneSamples.Count > MaxBufferedBoneSamples)
                entry.BoneSamples.RemoveAt(0);
        }

        private static Dictionary<string, BoneTransformData> GetBufferedBonePose(Entry entry)
        {
            if (entry == null || entry.BoneSamples.Count == 0) return null;
            if (entry.BoneSamples.Count == 1) return entry.BoneSamples[0].Bones;

            float renderTime = Time.unscaledTime - BoneInterpolationDelaySeconds;
            BonePoseSample first = entry.BoneSamples[0];
            if (renderTime <= first.ReceivedAt) return first.Bones;

            for (int i = 1; i < entry.BoneSamples.Count; i++)
            {
                BonePoseSample next = entry.BoneSamples[i];
                if (renderTime > next.ReceivedAt) continue;
                BonePoseSample previous = entry.BoneSamples[i - 1];
                float span = next.ReceivedAt - previous.ReceivedAt;
                if (span <= 0.0001f) return next.Bones;
                float amount = Mathf.Clamp01((renderTime - previous.ReceivedAt) / span);
                Dictionary<string, BoneTransformData> pose = MitaBoneSync.Interpolate(
                    previous.Bones, next.Bones, amount, entry.InterpolatedBones);
                if (pose != previous.Bones && pose != next.Bones)
                    entry.InterpolatedBones = pose;
                return pose;
            }

            return entry.BoneSamples[entry.BoneSamples.Count - 1].Bones;
        }

        private Entry GetOrCreateEntry(string path, Vector3 expectedPosition)
        {
            Entry e;
            if (_entries.TryGetValue(path, out e) && e != null && e.MitaPerson != null)
                return e;

            GameObject go = FindMitaForHostPath(path, expectedPosition, true);
            if (go == null)
            {
                if (e == null || !e.WarnedMissing)
                {
                    DiagnosticLog.Warning("MitaController: could not resolve mitaPath '" + path + "' locally.");
                    if (e == null) e = new Entry();
                    e.WarnedMissing = true;
                    _entries[path] = e;
                }
                return null;
            }

            Component comp = go.GetComponent("MitaPerson");
            if (comp == null)
            {
                DiagnosticLog.Warning("MitaController: object at '" + path + "' has no MitaPerson.");
                return null;
            }

            Entry fresh = new Entry();
            fresh.GO = go;
            fresh.MitaPerson = comp;
            fresh.MitaRoot = go.transform;
            fresh.PersonRoot = MitaBoneSync.FindPersonRoot(go.transform);
            fresh.Nma = MitaUnityAccess.ResolveAgent(comp, go);
            Type t = comp.GetType();
            fresh.MagnetTargetField = t.GetField("magnetTarget", BindingFlags.NonPublic | BindingFlags.Instance);
            fresh.MagnetToTargetMethod = t.GetMethod("MagnetToTarget");
            fresh.MagnetOffMethod = t.GetMethod("MagnetOff");

            _entries[path] = fresh;
            int syncTransforms = MitaBoneSync.CountSyncTransforms(fresh.PersonRoot);
            DiagnosticLog.Info("MitaController: bound to '" + go.name + "' at '" + path +
                               "' personRoot='" + (fresh.PersonRoot != null ? fresh.PersonRoot.name : "<missing>") +
                               "' poseRoot='" + (fresh.PersonRoot != null
                                   ? MitaBoneSync.GetNetworkPath(fresh.PersonRoot)
                                   : "<missing>") +
                               "' syncTransforms=" + syncTransforms +
                               " agent=" + (fresh.Nma != null ? "bound" : "<missing>") + ".");
            if (syncTransforms == 0)
                DiagnosticLog.Warning("MitaController: no Armature transforms found under '" +
                                      path + "'.");
            return fresh;
        }

        private void RetireSupersededBindings(string activePath, Entry active)
        {
            if (active == null || active.MitaPerson == null) return;

            List<string> stalePaths = null;
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                if (string.Equals(pair.Key, activePath, StringComparison.Ordinal)) continue;
                Entry previous = pair.Value;
                if (previous == null) continue;

                bool samePerson = previous.MitaPerson == active.MitaPerson;
                bool sameMitaObject = previous.MitaRoot != null && active.MitaRoot != null &&
                                      previous.MitaRoot == active.MitaRoot;
                // Each MitaPerson gets an independent entry even when two
                // actors happen to share a rendered rig root. Retire aliases
                // only when they point to the same game actor.
                if (!samePerson && !sameMitaObject) continue;

                if (previous.MagnetProxy != null)
                    UnityEngine.Object.Destroy(previous.MagnetProxy);
                if (stalePaths == null) stalePaths = new List<string>();
                stalePaths.Add(pair.Key);
            }

            if (stalePaths == null) return;
            for (int i = 0; i < stalePaths.Count; i++)
                _entries.Remove(stalePaths[i]);
        }

        private static GameObject FindByPath(string path)
        {
            return MitaBoneSync.FindGameObjectByNetworkPath(path);
        }

        internal static GameObject ResolveLocalMitaGameObject(string hostPath)
        {
            MitaController controller = _active;
            if (controller != null)
            {
                Entry entry;
                if (controller._entries.TryGetValue(hostPath ?? string.Empty, out entry) &&
                    entry != null && entry.GO != null)
                    return entry.GO;
                return controller.FindMitaForHostPath(hostPath, Vector3.zero, false);
            }
            return FindByPath(hostPath);
        }

        private GameObject FindMitaForHostPath(string hostPath,
                                                Vector3 expectedPosition,
                                                bool useExpectedPosition)
        {
            GameObject exact = FindByPath(hostPath);
            if (exact != null && exact.activeInHierarchy &&
                exact.GetComponent("MitaPerson") != null)
                return exact;

            string hostName = GetNormalizedMitaName(hostPath);
            if (string.IsNullOrEmpty(hostName)) return null;

            UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
            GameObject best = null;
            Transform bestPoseRoot = null;
            int bestNameScore = 0;
            int bestRigScore = -1;
            float bestDistance = float.MaxValue;
            bool bestIsReusedAlias = false;

            for (int i = 0; i < objects.Length; i++)
            {
                GameObject candidate = objects[i].TryCast<GameObject>();
                if (candidate == null || !candidate.activeInHierarchy ||
                    !candidate.scene.IsValid() || !candidate.scene.isLoaded)
                    continue;

                Component person = candidate.GetComponent("MitaPerson");
                if (person == null) continue;

                Transform poseRoot = MitaBoneSync.FindPersonRoot(candidate.transform);
                bool isReusedAlias;
                if (IsMitaAlreadyBoundToAnotherPath(hostPath, candidate, person,
                                                    expectedPosition, useExpectedPosition,
                                                    out isReusedAlias))
                    continue;

                string candidateName = GetNormalizedMitaName(candidate.name);
                int nameScore = string.Equals(hostName, candidateName, StringComparison.Ordinal)
                    ? 2
                    : (candidateName.IndexOf(hostName, StringComparison.Ordinal) >= 0 ? 1 : 0);
                if (nameScore == 0) continue;

                int rigScore = MitaBoneSync.CountSyncTransforms(poseRoot);
                float distance = useExpectedPosition && poseRoot != null
                    ? Vector3.Distance(poseRoot.position, expectedPosition)
                    : 0f;

                if (best == null || (bestIsReusedAlias && !isReusedAlias) ||
                    (bestIsReusedAlias == isReusedAlias && nameScore > bestNameScore) ||
                    (bestIsReusedAlias == isReusedAlias && nameScore == bestNameScore &&
                     rigScore > bestRigScore) ||
                    (bestIsReusedAlias == isReusedAlias && nameScore == bestNameScore &&
                     rigScore == bestRigScore && distance < bestDistance))
                {
                    best = candidate;
                    bestPoseRoot = poseRoot;
                    bestNameScore = nameScore;
                    bestRigScore = rigScore;
                    bestDistance = distance;
                    bestIsReusedAlias = isReusedAlias;
                }
            }

            if (best != null)
            {
                DiagnosticLog.Warning("MitaController: mapped host Mita path '" + hostPath +
                                      "' to local '" + MitaBoneSync.GetNetworkPath(best.transform) +
                                      "' by character name; rigTransforms=" + bestRigScore +
                                      (useExpectedPosition && bestPoseRoot != null
                                          ? " distance=" + bestDistance.ToString("F2") + "m."
                                          : "."));
            }
            return best;
        }

        private bool IsMitaAlreadyBoundToAnotherPath(string hostPath,
                                                      GameObject candidate,
                                                      Component person,
                                                      Vector3 expectedPosition,
                                                      bool useExpectedPosition,
                                                      out bool isReusedAlias)
        {
            isReusedAlias = false;
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                if (string.Equals(pair.Key, hostPath, StringComparison.Ordinal)) continue;
                Entry existing = pair.Value;
                if (existing == null || existing.MitaPerson == null) continue;
                bool sameActor = existing.MitaPerson == person ||
                                 existing.MitaRoot == candidate.transform ||
                                 existing.GO == candidate;
                if (!sameActor) continue;

                // A host Mita can acquire a new hierarchy path after a quest
                // reparent. Reuse its old local binding only when its last
                // authoritative position clearly identifies the same actor.
                if (useExpectedPosition && (!existing.HasRootTarget ||
                    Vector3.Distance(existing.RootTargetPosition, expectedPosition) <= 2f))
                {
                    isReusedAlias = true;
                    continue;
                }
                return true;
            }
            return false;
        }

        private static string GetNormalizedMitaName(string pathOrName)
        {
            if (string.IsNullOrEmpty(pathOrName)) return string.Empty;
            int slash = pathOrName.LastIndexOf('/');
            string name = slash >= 0 ? pathOrName.Substring(slash + 1) : pathOrName;
            int duplicate = name.LastIndexOf('~');
            int siblingIndex;
            if (duplicate > 0 && int.TryParse(name.Substring(duplicate + 1), out siblingIndex))
                name = name.Substring(0, duplicate);
            name = name.Trim();
            const string cloneSuffix = " (Clone)";
            if (name.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - cloneSuffix.Length).Trim();
            const string personPrefix = "MitaPerson ";
            if (name.StartsWith(personPrefix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(personPrefix.Length).Trim();
            return name.ToLowerInvariant();
        }
    }
}
