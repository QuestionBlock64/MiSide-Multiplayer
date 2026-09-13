using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using MiSideMultiplayer.API.Players;

namespace MiSideMultiplayer
{
    public sealed class MitaWalkRedirect : IDisposable
    {
        private static MitaWalkRedirect instance;
        private static bool driverTypeRegistered;

        public static void EnsureRunning(string localDisplayName)
        {
            if (instance != null) return;

            MitaWalkRedirect r = new MitaWalkRedirect();
            r.Configure(localDisplayName);

            if (!driverTypeRegistered)
            {
                try
                {
                    ClassInjector.RegisterTypeInIl2Cpp<MitaWalkRedirectDriver>();
                    driverTypeRegistered = true;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MitaWalkRedirect: driver registration failed: " + ex.Message);
                }
            }

            GameObject driverGO = new GameObject("MiSideMultiplayer.MitaWalkRedirectDriver");
            UnityEngine.Object.DontDestroyOnLoad(driverGO);

            MitaWalkRedirectDriver driver = null;
            try
            {
                driver = driverGO.AddComponent<MitaWalkRedirectDriver>();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaWalkRedirect: AddComponent<Driver> failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }

            if (driver != null)
                driver.Owner = r;
            else
                DiagnosticLog.Warning("MitaWalkRedirect: driver component was null; Tick will not run.");

            instance = r;
        }

        private string localDisplayName = "LocalPlayer";
        private Harmony harmony;
        private bool initialized;

        // Diagnostic log counter — set to a large number to silence once
        // you've confirmed the walk calls.
        private int walkCallLogCount;
        private const int WalkCallLogLimit = 60;

        private float nextClosestLogTime;
        private const float ClosestLogInterval = 1f;
        private float nextRedirectAllowedTime;
        private const float RedirectCooldown = 0.05f;

        private FieldInfo nmaField;
        private FieldInfo tTargetField;
        private FieldInfo targetMoveToField;

        private MitaWalkRedirect() { }

        private void Configure(string configuredLocalDisplayName)
        {
            localDisplayName = string.IsNullOrEmpty(configuredLocalDisplayName)
                ? "LocalPlayer"
                : configuredLocalDisplayName;

            if (initialized) return;
            initialized = true;

            try
            {
                Type mitaPersonType = FindType("MitaPerson");
                if (mitaPersonType == null)
                {
                    DiagnosticLog.Warning("MitaWalkRedirect: MitaPerson NOT FOUND. Redirect disabled.");
                    return;
                }

                nmaField          = mitaPersonType.GetField("nma",          BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                tTargetField      = mitaPersonType.GetField("tTarget",      BindingFlags.Instance | BindingFlags.NonPublic);
                targetMoveToField = mitaPersonType.GetField("targetMoveTo", BindingFlags.Instance | BindingFlags.NonPublic);

                MethodInfo prefix = typeof(MitaWalkRedirect).GetMethod(
                    nameof(WalkPrefix), BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(MitaWalkRedirect).GetMethod(
                    nameof(WalkPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                HarmonyMethod prefixMethod = new HarmonyMethod(prefix);
                HarmonyMethod postfixMethod = new HarmonyMethod(postfix);

                harmony = new Harmony("com.miside.multiplayer.mitawalkredirect");
                int patched = 0;

                patched += Patch(mitaPersonType, "AiWalkToTarget",
                    new[] { typeof(Transform), typeof(UnityEngine.Events.UnityEvent) }, prefixMethod, postfixMethod);
                patched += Patch(mitaPersonType, "AiWalkToTarget",
                    new[] { typeof(Transform), typeof(UnityEngine.Events.UnityEvent), typeof(AnimationClip) }, prefixMethod, postfixMethod);
                patched += Patch(mitaPersonType, "AiWalkToTargetTranform",
                    new[] { typeof(Transform), typeof(UnityEngine.Events.UnityEvent) }, prefixMethod, postfixMethod);
                patched += Patch(mitaPersonType, "AiWalkToTargetRotate",
                    new[] { typeof(Transform), typeof(UnityEngine.Events.UnityEvent) }, prefixMethod, postfixMethod);

                DiagnosticLog.Info("MitaWalkRedirect: patched " + patched + " walk overload(s).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaWalkRedirect: init failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private int Patch(Type type, string name, Type[] parameters,
                          HarmonyMethod prefix, HarmonyMethod postfix)
        {
            MethodInfo method = type.GetMethod(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, parameters, null);

            if (method == null)
            {
                DiagnosticLog.Warning("MitaWalkRedirect: " + name + "(" + parameters.Length + " args) NOT FOUND.");
                return 0;
            }

            try { harmony.Patch(method, prefix: prefix, postfix: postfix); return 1; }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaWalkRedirect: failed to patch " + name + ": " + ex.Message);
                return 0;
            }
        }

        // Prefix: block walks entirely for Kind Mita when the target is a player.
        private static bool WalkPrefix(object __instance, Transform _target)
        {
            if (__instance == null) return true;

            if (!MitaHost.IsHost) return true;

            Transform mitaTransform = GetMitaTransform(__instance);
            if (mitaTransform == null) return true;

            if (!IsKindMita(mitaTransform)) return true;
            if (_target == null) return true;
            if (!MitaPlayerResolver.IsInAnyPlayerHierarchy(_target)) return true;

            return false;
        }

        private static void WalkPostfix(object __instance, Transform _target)
        {
            MitaWalkRedirect self = instance;
            if (self == null) return;

            if (!MitaHost.IsHost) return;

            // ── TEMP DIAGNOSTIC ────────────────────────────────────────────
            // Logs every AiWalkToTarget call so we can see who drives Cappie
            // and Mila. Remove once we've identified the caller.
            try
            {
                if (self.walkCallLogCount < WalkCallLogLimit && __instance != null)
                {
                    self.walkCallLogCount++;
                    Transform mt = GetMitaTransform(__instance);
                    DiagnosticLog.Info(
                        "WalkCall #" + self.walkCallLogCount +
                        ": mita=" + (mt != null ? mt.name : "?") +
                        " target=" + (_target != null ? _target.name : "null") +
                        " targetPath=" + (_target != null ? LocalPlayerLocator.GetPath(_target) : "-") +
                        " isLocal=" + (_target != null ? MitaPlayerResolver.IsInLocalHierarchy(_target).ToString() : "n/a") +
                        " caller=" + DescribeCaller());
                }
            }
            catch { }
            // ───────────────────────────────────────────────────────────────

            try
            {
                if (__instance == null || _target == null) return;

                Transform mitaTransform = GetMitaTransform(__instance);
                if (mitaTransform == null) return;

                if (IsKindMita(mitaTransform)) return;

                // Match either the local player root OR anything under it.
                // Some follow scripts pass a child bone instead of the root.
                if (!MitaPlayerResolver.IsInLocalHierarchy(_target)) return;

                if (Time.unscaledTime < self.nextRedirectAllowedTime) return;

                Transform closest = self.FindClosestTo(mitaTransform);
                if (closest == null) return;

                // If the closest is ourselves, keep the original walk.
                Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
                if (closest == localPlayer) return;

                self.nextRedirectAllowedTime = Time.unscaledTime + RedirectCooldown;
                self.ReIssueWalk(__instance, closest);

                DiagnosticLog.Info(
                    "MitaWalkRedirect: redirected walk from " +
                    self.localDisplayName + " (local) to " +
                    self.DescribePlayer(closest) + ".");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaWalkRedirect.WalkPostfix failed: " + ex.Message);
            }
        }

        private static bool IsKindMita(Transform mita)
        {
            if (mita == null) return false;
            string path = LocalPlayerLocator.GetPath(mita);
            return path.Contains("Добрая") || path.Contains("Добр");
        }

        private void ReIssueWalk(object mitaPerson, Transform closest)
        {
            try
            {
                if (tTargetField != null)      tTargetField.SetValue(mitaPerson, closest);
                if (targetMoveToField != null) targetMoveToField.SetValue(mitaPerson, closest);

                if (nmaField != null)
                {
                    UnityEngine.AI.NavMeshAgent nma =
                        nmaField.GetValue(mitaPerson) as UnityEngine.AI.NavMeshAgent;
                    if (nma != null)
                    {
                        if (!nma.enabled) nma.enabled = true;
                        nma.SetDestination(closest.position);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaWalkRedirect.ReIssueWalk failed: " + ex.Message);
            }
        }

        private static Transform GetMitaTransform(object mitaPerson)
        {
            Component c = mitaPerson as Component;
            return c != null ? c.transform : null;
        }

        private Transform FindClosestTo(Transform mitaTransform)
        {
            if (mitaTransform == null) return null;
            Vector3 pos = mitaTransform.position;
            Transform closest = null;
            float bestDist = float.MaxValue;

            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (localPlayer != null)
            {
                float d = Vector3.Distance(pos, localPlayer.position);
                if (d < bestDist) { bestDist = d; closest = localPlayer; }
            }

            foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
            {
                if (puppet == null || puppet.GameObject == null) continue;
                float d = Vector3.Distance(pos, puppet.GameObject.transform.position);
                if (d < bestDist) { bestDist = d; closest = puppet.GameObject.transform; }
            }
            return closest;
        }

        private static string GetPuppetDisplayName(GameObject puppetGO)
        {
            if (puppetGO == null) return "?";
            try
            {
                for (int i = 0; i < puppetGO.transform.childCount; i++)
                {
                    Transform child = puppetGO.transform.GetChild(i);
                    if (child == null) continue;
                    if (!child.name.StartsWith("NameTag_", StringComparison.Ordinal)) continue;

                    TextMesh tm = child.GetComponent<TextMesh>();
                    if (tm != null && !string.IsNullOrEmpty(tm.text))
                        return tm.text;
                }
            }
            catch { }
            return puppetGO.name;
        }

        private string DescribePlayer(Transform t)
        {
            if (t == null) return "null";

            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (localPlayer == t) return localDisplayName + " (local)";

            foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
            {
                if (puppet == null || puppet.GameObject == null) continue;
                if (puppet.GameObject.transform == t)
                    return GetPuppetDisplayName(puppet.GameObject) + " (puppet)";
            }
            return t.gameObject.name;
        }

        public void Tick()
        {
            if (Time.unscaledTime < nextClosestLogTime) return;
            nextClosestLogTime = Time.unscaledTime + ClosestLogInterval;

            Transform mita = FindMitaTransform();
            if (mita == null) return;

            Vector3 pos = mita.position;
            float localDist = float.MaxValue;
            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (localPlayer != null)
                localDist = Vector3.Distance(pos, localPlayer.position);

            string line = "MitaWalkRedirect: closest to Mita — " +
                          localDisplayName + " (local, dist=" +
                          (localDist == float.MaxValue ? "n/a" : localDist.ToString("F2")) + ")";

            int puppetCount = 0;
            float bestPuppetDist = float.MaxValue;
            string bestPuppetName = null;
            foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
            {
                if (puppet == null || puppet.GameObject == null) continue;
                puppetCount++;
                float d = Vector3.Distance(pos, puppet.GameObject.transform.position);
                if (d < bestPuppetDist)
                {
                    bestPuppetDist = d;
                    bestPuppetName = GetPuppetDisplayName(puppet.GameObject);
                }
            }

            line += puppetCount == 0
                ? "; no puppets"
                : "; nearest puppet " + bestPuppetName + " (dist=" + bestPuppetDist.ToString("F2") + ")";

            DiagnosticLog.Info(line);
        }

        private static Transform FindMitaTransform()
        {
            try
            {
                var all = MitaSceneCache.MitaTransforms;
                if (all.Count > 0) return all[0];
            }
            catch { }
            return null;
        }

        private static string DescribeCaller()
        {
            try
            {
                System.Diagnostics.StackTrace trace = new System.Diagnostics.StackTrace(false);
                for (int i = 2; i < trace.FrameCount && i < 6; i++)
                {
                    System.Diagnostics.StackFrame frame = trace.GetFrame(i);
                    MethodBase method = frame.GetMethod();
                    if (method == null) continue;
                    string typeName = method.DeclaringType != null ? method.DeclaringType.Name : "?";
                    if (typeName == "MitaPerson") continue;
                    if (typeName == "MitaWalkRedirect") continue;
                    return typeName + "." + method.Name;
                }
            }
            catch { }
            return "unknown";
        }

        public void Dispose()
        {
            if (harmony != null)
            {
                try { harmony.UnpatchSelf(); } catch { }
                harmony = null;
            }
            if (ReferenceEquals(instance, this))
                instance = null;
        }

        private static Type FindType(string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        public sealed class MitaWalkRedirectDriver : MonoBehaviour
        {
            public MitaWalkRedirect Owner;

            private void Update()
            {
                if (Owner != null) Owner.Tick();
            }

            private void OnDestroy()
            {
                if (Owner != null) { Owner.Dispose(); Owner = null; }
            }
        }
    }
}