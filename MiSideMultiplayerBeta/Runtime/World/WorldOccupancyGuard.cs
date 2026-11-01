using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Prevents MiSide's world-streaming system from unloading a house-world
    /// chunk while any player (local or a remote puppet) is standing inside
    /// it.
    ///
    /// Has a grace period after each world activation during which it does
    /// not block anything. Scene setup sometimes needs to deactivate and
    /// reactivate children right after a World wakes up; interfering with
    /// that breaks scenes like Cappie's.
    /// </summary>
    public static class WorldOccupancyGuard
    {
        private static bool _initialized;
        private static Harmony _harmony;

        private const float ActivationGraceSeconds = 60f;

        private sealed class WorldRecord
        {
            public Component WorldComponent;
            public GameObject WorldRoot;
            public Bounds Bounds;
            public readonly HashSet<int> OwnedObjectIds = new HashSet<int>();
            public bool Occupied;
            public float LastOccupancyCheckTime;
            public float ActivatedAt;      // Time.unscaledTime when World.Start ran
        }

        private static readonly Dictionary<int, WorldRecord> _worldsById =
            new Dictionary<int, WorldRecord>();
        private static readonly Dictionary<int, int> _objectToWorldId =
            new Dictionary<int, int>();
        private static readonly HashSet<int> _notWorldOwned =
            new HashSet<int>();

        private const float OccupancyCheckInterval = 0.2f;

        public static void Initialize(Harmony harmony)
        {
            if (_initialized) return;
            _initialized = true;
            _harmony = harmony;

            try
            {
                Type worldType = FindType("World");
                if (worldType == null)
                {
                    DiagnosticLog.Warning("WorldOccupancyGuard: World type NOT FOUND.");
                    return;
                }

                System.Reflection.MethodInfo start = worldType.GetMethod(
                    "Start",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (start != null)
                {
                    System.Reflection.MethodInfo startPostfix =
                        typeof(WorldOccupancyGuard).GetMethod(
                            nameof(WorldStartPostfix),
                            System.Reflection.BindingFlags.Static |
                            System.Reflection.BindingFlags.NonPublic);
                    _harmony.Patch(start, postfix: new HarmonyMethod(startPostfix));
                    DiagnosticLog.Info("WorldOccupancyGuard: patched World.Start.");
                }
                else
                {
                    DiagnosticLog.Warning("WorldOccupancyGuard: World.Start not found.");
                }

                System.Reflection.MethodInfo setActive = typeof(GameObject).GetMethod(
                    "SetActive", new[] { typeof(bool) });
                if (setActive != null)
                {
                    System.Reflection.MethodInfo setActivePrefix =
                        typeof(WorldOccupancyGuard).GetMethod(
                            nameof(SetActivePrefix),
                            System.Reflection.BindingFlags.Static |
                            System.Reflection.BindingFlags.NonPublic);
                    System.Reflection.MethodInfo setActivePostfix =
                        typeof(WorldOccupancyGuard).GetMethod(
                            nameof(SetActivePostfix),
                            System.Reflection.BindingFlags.Static |
                            System.Reflection.BindingFlags.NonPublic);
                    _harmony.Patch(setActive,
                        prefix: new HarmonyMethod(setActivePrefix),
                        postfix: new HarmonyMethod(setActivePostfix));
                    DiagnosticLog.Info("WorldOccupancyGuard: patched GameObject.SetActive.");
                }
                else
                {
                    DiagnosticLog.Warning("WorldOccupancyGuard: GameObject.SetActive not found.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldOccupancyGuard: init failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Invalidate()
        {
            _worldsById.Clear();
            _objectToWorldId.Clear();
            _notWorldOwned.Clear();
        }

        public static void Tick()
        {
            if (_worldsById.Count == 0) return;

            float now = Time.unscaledTime;

            foreach (var kv in _worldsById)
            {
                WorldRecord rec = kv.Value;
                if (rec == null || rec.WorldComponent == null) continue;
                if (now - rec.LastOccupancyCheckTime < OccupancyCheckInterval) continue;
                rec.LastOccupancyCheckTime = now;

                if (rec.WorldRoot != null)
                    rec.Bounds = ComputeBounds(rec.WorldRoot.transform);

                rec.Occupied = IsAnyPlayerInside(rec.Bounds);
            }
        }

        // ── World.Start postfix: capture the world ─────────────────────────

        private static void WorldStartPostfix(Component __instance)
        {
            if (__instance == null) return;

            try
            {
                GameObject root = __instance.gameObject;
                int worldId = __instance.GetInstanceID();

                WorldRecord rec;
                if (!_worldsById.TryGetValue(worldId, out rec) || rec == null)
                {
                    rec = new WorldRecord();
                    rec.WorldComponent = __instance;
                    rec.WorldRoot = root;
                    _worldsById[worldId] = rec;
                }

                rec.ActivatedAt = Time.unscaledTime;
                rec.OwnedObjectIds.Clear();

                Bounds collected = new Bounds(root.transform.position, Vector3.zero);
                bool haveAny = false;
                CollectSubtree(root.transform, rec.OwnedObjectIds, ref collected, ref haveAny);

                rec.Bounds = haveAny ? collected : ComputeBounds(root.transform);

                foreach (int id in rec.OwnedObjectIds)
                    _objectToWorldId[id] = worldId;

                DiagnosticLog.Info(
                    "WorldOccupancyGuard: tracked world '" + root.name +
                    "' id=" + worldId +
                    " objects=" + rec.OwnedObjectIds.Count +
                    " boundsCenter=" + rec.Bounds.center.ToString("F1") +
                    " boundsSize=" + rec.Bounds.size.ToString("F1") +
                    " graceUntil=+" + ActivationGraceSeconds.ToString("F0") + "s");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldOccupancyGuard: WorldStartPostfix failed: " + ex.Message);
            }
        }

        private static void CollectSubtree(Transform node, HashSet<int> ids,
                                           ref Bounds bounds, ref bool haveAny)
        {
            if (node == null) return;

            ids.Add(node.gameObject.GetInstanceID());

            Collider col = node.GetComponent<Collider>();
            if (col != null && col.enabled)
            {
                if (!haveAny)
                {
                    bounds = col.bounds;
                    haveAny = true;
                }
                else
                {
                    bounds.Encapsulate(col.bounds);
                }
            }

            int childCount = node.childCount;
            for (int i = 0; i < childCount; i++)
                CollectSubtree(node.GetChild(i), ids, ref bounds, ref haveAny);
        }

        private static Bounds ComputeBounds(Transform root)
        {
            if (root == null)
                return new Bounds(Vector3.zero, Vector3.zero);

            Bounds bounds = new Bounds(root.position, Vector3.zero);
            bool haveAny = false;

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            if (colliders != null && colliders.Length > 0)
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider c = colliders[i];
                    if (c == null) continue;
                    if (!haveAny)
                    {
                        bounds = c.bounds;
                        haveAny = true;
                    }
                    else
                    {
                        bounds.Encapsulate(c.bounds);
                    }
                }
            }

            if (!haveAny)
                bounds = new Bounds(root.position, new Vector3(20f, 20f, 20f));

            return bounds;
        }

        // ── GameObject.SetActive prefix ────────────────────────────────────

        private static bool SetActivePrefix(GameObject __instance, bool value,
                                            ref SceneActivityProbe.ActiveCallTrace __state)
        {
            __state = SceneActivityProbe.BeginSetActiveTrace(__instance, value);
            if (__instance == null) return true;
            if (value) return true;

            int objId;
            try { objId = __instance.GetInstanceID(); }
            catch { return true; }

            if (_notWorldOwned.Contains(objId)) return true;

            int worldId;
            if (!_objectToWorldId.TryGetValue(objId, out worldId))
            {
                if (FindOwningWorldId(__instance.transform, out worldId))
                {
                    _objectToWorldId[objId] = worldId;
                }
                else
                {
                    _notWorldOwned.Add(objId);
                    return true;
                }
            }

            WorldRecord rec;
            if (!_worldsById.TryGetValue(worldId, out rec) || rec == null)
                return true;

            // Only protect the World root from being unloaded while occupied.
            // Descendant SetActive calls are normal gameplay (dialogue symbols,
            // room chunks, doors, and loading visuals) and must remain untouched.
            if (rec.WorldRoot == null) return true;
            int worldRootId;
            try { worldRootId = rec.WorldRoot.GetInstanceID(); }
            catch { return true; }
            if (objId != worldRootId) return true;

            // Grace period after world activation: allow the scene to run its
            // own setup without interference.
            if (Time.unscaledTime - rec.ActivatedAt < ActivationGraceSeconds)
                return true;

            if (!rec.Occupied) return true;

            SceneActivityProbe.RecordSetActiveBlocked(__state,
                "WorldOccupancyGuard kept the occupied World root active");
            return false;
        }

        private static void SetActivePostfix(GameObject __instance, bool value,
                                             SceneActivityProbe.ActiveCallTrace __state)
        {
            SceneActivityProbe.CompleteSetActiveTrace(__state, __instance);
        }

        private static bool FindOwningWorldId(Transform t, out int worldId)
        {
            worldId = 0;
            Transform current = t;
            while (current != null)
            {
                Component w = current.GetComponent("World");
                if (w != null)
                {
                    worldId = w.GetInstanceID();
                    return true;
                }
                current = current.parent;
            }
            return false;
        }

        // ── Occupancy test ─────────────────────────────────────────────────

        private static bool IsAnyPlayerInside(Bounds worldBounds)
        {
            if (worldBounds.size == Vector3.zero) return false;

            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (localPlayer != null && worldBounds.Contains(localPlayer.position))
                return true;

            foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
            {
                if (puppet == null || puppet.GameObject == null) continue;
                if (worldBounds.Contains(puppet.GameObject.transform.position))
                    return true;
            }

            return false;
        }

        private static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}
