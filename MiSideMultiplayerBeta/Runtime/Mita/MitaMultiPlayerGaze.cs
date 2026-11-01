using System;
using System.Collections.Generic;
using HarmonyLib;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Players;
using UnityEngine;

namespace MiSideMultiplayer
{
    [HarmonyPatch]
    public static class MitaMultiPlayerGaze
    {
        private const string FieldLookObject      = "lookObject";
        private const string FieldLookObjectEyes  = "lookObjectEyes";
        private const string FieldForwardPerson   = "forwardPerson";
        private const string FieldOffLimit        = "offLimitAngleLook";
        private const string FieldTimeLookEyes    = "timeLookObjectEyes";
        private const string FieldTimeCheckEyes   = "timeCheckEyes";
        private const string FieldEyesRandom      = "eyesPositionRandom";
        private const string FieldRandomLook      = "randomLook";
        private const string FieldPivotLegsRotate = "timeAnimationPivotLegsRotate";
        private const string FieldActive          = "active";

        private static bool VerboseGazeLog = false;

        private static float _nextLogTime;

        // Per-Mita cached path. Paths don't change frame to frame.
        private static readonly Dictionary<int, string> _pathByInstanceId =
            new Dictionary<int, string>();

        // Cached per-interval scene flags.
        private const float SceneFlagInterval = 0.5f;
        private static float _nextSceneFlagRefresh;
        private static bool _cachedMinigameActive;
        private static bool _cachedCappieInScene;

        public static void ClearPathCache()
        {
            _pathByInstanceId.Clear();
        }

        // ── Scene-flag lookups ──────────────────────────────────────────────

        private static readonly string[] MinigamePrefixes =
        {
            "TamagotchiGame_",
            "Tamagotchi_MiniGame",
        };

        private static void RefreshSceneFlagsIfStale()
        {
            if (Time.unscaledTime < _nextSceneFlagRefresh) return;
            _nextSceneFlagRefresh = Time.unscaledTime + SceneFlagInterval;

            _cachedMinigameActive = ComputeMinigameActive();
            _cachedCappieInScene = ComputeCappieInScene();
        }

        private static bool ComputeMinigameActive()
        {
            try
            {
                var all = MitaSceneCache.AllBehaviours;
                for (int i = 0; i < all.Count; i++)
                {
                    MonoBehaviour mb = all[i];
                    if (mb == null) continue;
                    if (!mb.enabled) continue;
                    if (!mb.gameObject.activeInHierarchy) continue;

                    string name = mb.GetType().Name;
                    for (int p = 0; p < MinigamePrefixes.Length; p++)
                    {
                        if (name.StartsWith(MinigamePrefixes[p], StringComparison.Ordinal))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool ComputeCappieInScene()
        {
            try
            {
                var all = MitaSceneCache.MitaTransforms;
                for (int i = 0; i < all.Count; i++)
                {
                    Transform t = all[i];
                    if (t == null) continue;
                    string p = GetCachedPath(t);
                    if (p.Contains("Кепка") || p.Contains("Кеп"))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsMinigameActive()
        {
            RefreshSceneFlagsIfStale();
            return _cachedMinigameActive;
        }

        private static bool IsCappieInScene()
        {
            RefreshSceneFlagsIfStale();
            return _cachedCappieInScene;
        }

        // ── Cached path ─────────────────────────────────────────────────────

        private static string GetCachedPath(Transform t)
        {
            if (t == null) return "";
            int id = t.GetInstanceID();
            string p;
            if (_pathByInstanceId.TryGetValue(id, out p)) return p;
            p = LocalPlayerLocator.GetPath(t);
            _pathByInstanceId[id] = p;
            return p;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static Transform FindMitaTransform(Character_Look look)
        {
            if (look == null) return null;
            Transform current = look.transform;
            while (current != null)
            {
                if (current.GetComponent("MitaPerson") != null)
                    return current;
                current = current.parent;
            }
            return null;
        }

        private static Transform ClosestHeadOrNull(Character_Look look)
        {
            Transform mita = FindMitaTransform(look);
            if (mita == null || MitaPlayerResolver.IsGameCardMita(mita)) return null;
            return MitaPlayerResolver.ClosestLookTarget(mita.position);
        }

        private static bool IsKindMita(Transform mita)
        {
            if (mita == null) return false;
            string path = GetCachedPath(mita);
            return path.Contains("Добрая") || path.Contains("Добр");
        }

        // ── ForceClosest ────────────────────────────────────────────────────

        private static void ForceClosest(Character_Look look, string tag)
        {
            if (look == null) return;

            Transform mita = FindMitaTransform(look);
            if (mita == null || MitaPlayerResolver.IsGameCardMita(mita)) return;

            string mitaPath = GetCachedPath(mita);
            if (string.IsNullOrEmpty(mitaPath)) return;

            if (!QBLib.Fields.ReadBool(look, FieldActive, true))
            {
                if (MitaHost.IsHost) MitaGazeState.SetCurrent(mitaPath, "");
                return;
            }

            if (IsMinigameActive())
            {
                if (MitaHost.IsHost) MitaGazeState.SetCurrent(mitaPath, "");
                return;
            }

            if (IsKindMita(mita) && IsCappieInScene())
            {
                if (MitaHost.IsHost) MitaGazeState.SetCurrent(mitaPath, "");
                return;
            }

            Transform target;
            if (MitaHost.IsHost)
            {
                target = MitaPlayerResolver.ClosestLookTarget(mita.position);
                if (target != null)
                {
                    string id = MitaHost.ResolvePlayerIdForTransform(target);
                    string previousId = MitaGazeState.GetCurrent(mitaPath);
                    MitaGazeState.SetCurrent(mitaPath, id ?? "");
                    if (!string.Equals(previousId, id ?? "", StringComparison.Ordinal))
                        DiagnosticLog.Info("[MITA-GAZE-TARGET] scene='" +
                                           UnityEngine.SceneManagement.SceneManager.GetActiveScene().name +
                                           "' mita='" + mitaPath + "' player='" + (id ?? "") +
                                           "' roster=" + MitaPlayerRosterSync.GetKnownPlayerIds(
                                               UnityEngine.SceneManagement.SceneManager.GetActiveScene().name).Count +
                                           ".");
                }
                else
                {
                    MitaGazeState.SetCurrent(mitaPath, "");
                }
            }
            else
            {
                string remoteId = MitaGazeState.GetRemote(mitaPath);
                Transform root = MitaHost.ResolveGazeTarget(remoteId);
                target = root != null ? MitaPlayerResolver.ResolveHead(root) : null;
            }

            if (target == null)
            {
                ClearPlayerLookTargets(look);
                return;
            }

            try
            {
                QBLib.Fields.WriteRef(look, FieldLookObject, target);
                QBLib.Fields.WriteRef(look, FieldLookObjectEyes, target);
                QBLib.Fields.WriteBool(look, FieldRandomLook, false);
            }
            catch { }

            try
            {
                Vector3 toTarget = target.position - mita.position;
                toTarget.y = 0f;

                if (toTarget.sqrMagnitude > 0.04f)
                {
                    float facing = Vector3.Dot(mita.forward, toTarget.normalized);
                    float pivot = QBLib.Fields.ReadFloat(look, FieldPivotLegsRotate, 1f);

                    if (facing < 0.85f && pivot >= 1f)
                    {
                        look.StartRotate(target.position);
                    }
                }
            }
            catch { }

            if (VerboseGazeLog && Time.unscaledTime >= _nextLogTime)
            {
                _nextLogTime = Time.unscaledTime + 1f;
                DiagnosticLog.Info("Gaze[" + tag + "] mita=" + mita.name +
                                   " path=" + mitaPath +
                                   " -> " + target.name +
                                   " at " + target.position.ToString("F2"));
            }
        }

        private static void ClearPlayerLookTargets(Character_Look look)
        {
            if (look == null) return;
            try
            {
                QBLib.Fields.WriteRef(look, FieldLookObject, null);
                QBLib.Fields.WriteRef(look, FieldLookObjectEyes, null);
                QBLib.Fields.WriteBool(look, FieldRandomLook, false);
            }
            catch { }
        }

        // ── NMA redirect (safety net for scripts that call SetDestination directly)

        private static void RedirectNmaIfNeeded(MitaPerson mita)
        {
            if (mita == null) return;
            if (!MitaHost.IsHost) return;

            Transform mitaTransform = mita.transform;
            if (mitaTransform == null) return;
            if (MitaPlayerResolver.IsGameCardMita(mitaTransform)) return;

            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (localPlayer == null) return;

            var nma = mita.nma;
            if (nma == null || !nma.enabled) return;

            Vector3 dest;
            try { dest = nma.destination; }
            catch { return; }

            // Only redirect if the destination is currently near the local player.
            Vector3 toLocal = localPlayer.position - dest;
            if (toLocal.sqrMagnitude > 4f) return;

            Transform closest = MitaPlayerResolver.ClosestTo(mitaTransform.position);
            if (closest == null || closest == localPlayer) return;

            float distLocal   = Vector3.Distance(mitaTransform.position, localPlayer.position);
            float distClosest = Vector3.Distance(mitaTransform.position, closest.position);
            if (distClosest >= distLocal - 1f) return;

            try
            {
                nma.SetDestination(closest.position);

                if (VerboseGazeLog && Time.unscaledTime >= _nextLogTime)
                {
                    _nextLogTime = Time.unscaledTime + 1f;
                    DiagnosticLog.Info(
                        "MitaWalk (nma): " + mitaTransform.name +
                        " retargeted NMA from local to closest at " +
                        closest.position.ToString("F2"));
                }
            }
            catch { }
        }

        // ── Harmony patches ────────────────────────────────────────────────

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character_Look), "Update")]
        public static void UpdatePrefix(Character_Look __instance)
        {
            ForceClosest(__instance, "Update-pre");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character_Look), "LateUpdate")]
        public static void LateUpdatePrefix(Character_Look __instance)
        {
            ForceClosest(__instance, "LateUpdate-pre");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MitaLifeEmotion), "Update")]
        public static bool MitaLifeEmotionUpdatePrefix(MitaLifeEmotion __instance)
        {
            if (__instance == null) return true;

            MitaPerson mp = __instance.GetComponent<MitaPerson>();
            if (mp == null) return true;

            if (mp.nma != null && mp.nma.enabled) return true;

            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MitaLifeEmotion), "Update")]
        public static void MitaLifeEmotionUpdatePostfix(MitaLifeEmotion __instance)
        {
            if (__instance == null) return;
            MitaPerson mp = __instance.GetComponent<MitaPerson>();
            if (mp == null || mp.lookLife == null) return;
            ForceClosest(mp.lookLife, "MitaLifeEmotion-post");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MitaPerson), "Update")]
        public static void MitaPersonUpdatePostfix(MitaPerson __instance)
        {
            if (__instance == null) return;

            // Gaze redirect.
            Character_Look look = __instance.lookLife;
            if (look != null)
                ForceClosest(look, "MitaPerson-post");

            // Walk redirect: catch scripts that call nma.SetDestination
            // directly, bypassing AiWalkToTarget.
            RedirectNmaIfNeeded(__instance);
        }

        // ── LookOn* redirects ─────────────────────────────────────────────

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character_Look), nameof(Character_Look.LookOnObject))]
        public static void LookOnObjectPrefix(Character_Look __instance, ref Transform _target)
        {
            if (_target == null) return;
            if (IsMinigameActive()) return;
            if (!MitaPlayerResolver.IsInLocalHierarchy(_target)) return;

            Transform head = ClosestHeadOrNull(__instance);
            if (head == null) return;

            _target = head;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character_Look), nameof(Character_Look.LookOnObjectSmooth))]
        public static void LookOnObjectSmoothPrefix(Character_Look __instance, ref Transform _target)
        {
            if (_target == null) return;
            if (IsMinigameActive()) return;
            if (!MitaPlayerResolver.IsInLocalHierarchy(_target)) return;

            Transform head = ClosestHeadOrNull(__instance);
            if (head == null) return;

            _target = head;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character_Look), nameof(Character_Look.LookOnPlayer))]
        public static bool LookOnPlayerPrefix(Character_Look __instance)
        {
            if (IsMinigameActive()) return true;

            Transform head = ClosestHeadOrNull(__instance);
            if (head == null) return true;

            try { __instance.LookOnObject(head); }
            catch { }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character_Look), nameof(Character_Look.LookOnPlayerAndRotate))]
        public static bool LookOnPlayerAndRotatePrefix(Character_Look __instance)
        {
            if (IsMinigameActive()) return true;

            Transform head = ClosestHeadOrNull(__instance);
            if (head == null) return true;

            try
            {
                __instance.StartRotate(head.position);
                __instance.LookOnObject(head);
            }
            catch { }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character_Look), nameof(Character_Look.LookRotatePriorityOnPlayer))]
        public static bool LookRotatePriorityOnPlayerPrefix(Character_Look __instance)
        {
            if (IsMinigameActive()) return true;

            Transform head = ClosestHeadOrNull(__instance);
            if (head == null) return true;

            try
            {
                __instance.StartRotate(head.position);
                __instance.LookOnObject(head);
                __instance.PriorityLook(head);
            }
            catch { }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character_Look), nameof(Character_Look.PriorityLookOnPlayer))]
        public static bool PriorityLookOnPlayerPrefix(Character_Look __instance)
        {
            if (IsMinigameActive()) return true;

            Transform head = ClosestHeadOrNull(__instance);
            if (head == null) return true;

            try { __instance.PriorityLook(head); }
            catch { }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character_Look), nameof(Character_Look.PriorityLookAndOnPlayer))]
        public static bool PriorityLookAndOnPlayerPrefix(Character_Look __instance)
        {
            if (IsMinigameActive()) return true;

            Transform head = ClosestHeadOrNull(__instance);
            if (head == null) return true;

            try
            {
                __instance.PriorityLook(head);
                __instance.LookOnObject(head);
            }
            catch { }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character_Look), nameof(Character_Look.EyesLookOnPlayer))]
        public static bool EyesLookOnPlayerPrefix(Character_Look __instance, float _time)
        {
            if (IsMinigameActive()) return true;

            Transform head = ClosestHeadOrNull(__instance);
            if (head == null) return true;

            try
            {
                QBLib.Fields.WriteRef(__instance, FieldLookObjectEyes, head);
                QBLib.Fields.WriteFloat(__instance, FieldTimeLookEyes, _time);
                QBLib.Fields.WriteFloat(__instance, FieldTimeCheckEyes,
                                        _time + UnityEngine.Random.Range(0.5f, 3f));
                QBLib.Fields.WriteVector3(__instance, FieldEyesRandom, Vector3.zero);
            }
            catch { }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character_Look), "CheckLookForEyes")]
        public static bool CheckLookForEyesPrefix(Character_Look __instance)
        {
            if (IsMinigameActive()) return true;

            Transform head = ClosestHeadOrNull(__instance);
            if (head == null) return true;

            try
            {
                Transform forwardPerson =
                    QBLib.Fields.ReadRef<Transform>(__instance, FieldForwardPerson);
                bool offLimit =
                    QBLib.Fields.ReadBool(__instance, FieldOffLimit, false);

                bool shouldLook = false;
                if (forwardPerson != null)
                {
                    float dot = Vector3.Dot(forwardPerson.forward,
                        Vector3.Normalize(head.position - forwardPerson.position));
                    shouldLook = (dot > 0.18f || offLimit) && UnityEngine.Random.Range(0, 9) > 5;
                }

                if (shouldLook)
                {
                    QBLib.Fields.WriteRef(__instance, FieldLookObjectEyes, head);
                    QBLib.Fields.WriteFloat(__instance, FieldTimeLookEyes,
                                            UnityEngine.Random.Range(0.3f, 1f));
                }

                QBLib.Fields.WriteFloat(__instance, FieldTimeCheckEyes,
                                        UnityEngine.Random.Range(1f, 5f));
                QBLib.Fields.WriteVector3(__instance, FieldEyesRandom, Vector3.zero);
            }
            catch { }

            return false;
        }
    }
}
