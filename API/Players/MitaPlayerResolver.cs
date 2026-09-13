using System.Collections.Generic;
using UnityEngine;

namespace MiSideMultiplayer.API.Players
{
    public static class MitaPlayerResolver
    {
        // ── Local ───────────────────────────────────────────────────────────

        public static Transform Local()
        {
            return LocalPlayerLocator.FindHardcodedPlayerPath();
        }

        public static IEnumerable<Transform> All()
        {
            Transform local = Local();
            if (local != null)
                yield return local;

            foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
            {
                if (puppet == null || puppet.GameObject == null) continue;
                yield return puppet.GameObject.transform;
            }
        }

        public static IEnumerable<Transform> AllExcept(Transform exclude)
        {
            foreach (Transform t in All())
            {
                if (t == null || t == exclude) continue;
                yield return t;
            }
        }

        public static Transform ClosestTo(Vector3 position)
        {
            Transform closest = null;
            float bestDist = float.MaxValue;

            foreach (Transform t in All())
            {
                if (t == null) continue;
                float d = Vector3.Distance(position, t.position);
                if (d < bestDist) { bestDist = d; closest = t; }
            }

            return closest;
        }

        public static Transform ClosestTo(Vector3 position, Transform exclude)
        {
            Transform closest = null;
            float bestDist = float.MaxValue;

            foreach (Transform t in AllExcept(exclude))
            {
                if (t == null) continue;
                float d = Vector3.Distance(position, t.position);
                if (d < bestDist) { bestDist = d; closest = t; }
            }

            return closest;
        }

        public static Transform ClosestRemoteTo(Vector3 position)
        {
            Transform local = Local();
            Transform closest = null;
            float bestDist = float.MaxValue;

            foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
            {
                if (puppet == null || puppet.GameObject == null) continue;
                if (puppet.GameObject.transform == local) continue;

                float d = Vector3.Distance(position, puppet.GameObject.transform.position);
                if (d < bestDist) { bestDist = d; closest = puppet.GameObject.transform; }
            }

            return closest;
        }

        public static bool IsLocal(Transform t)
        {
            return t != null && t == Local();
        }

        public static Transform OrClosest(Transform maybeLocal, Vector3 position)
        {
            if (maybeLocal == null) return ClosestTo(position);
            if (IsLocal(maybeLocal)) return ClosestTo(position);
            return maybeLocal;
        }

        // ── Hierarchy helpers ──────────────────────────────────────────────

        public static bool IsInLocalHierarchy(Transform t)
        {
            if (t == null) return false;
            Transform local = Local();
            if (local == null) return false;
            return IsUnder(t, local);
        }

        public static bool IsInAnyPuppetHierarchy(Transform t)
        {
            if (t == null) return false;

            foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
            {
                if (puppet == null || puppet.GameObject == null) continue;
                if (IsUnder(t, puppet.GameObject.transform)) return true;
            }
            return false;
        }

        public static bool IsInAnyPlayerHierarchy(Transform t)
        {
            return IsInLocalHierarchy(t) || IsInAnyPuppetHierarchy(t);
        }

        private static bool IsUnder(Transform t, Transform root)
        {
            if (t == null || root == null) return false;
            while (t != null)
            {
                if (t == root) return true;
                t = t.parent;
            }
            return false;
        }

        // ── Look-target helpers ────────────────────────────────────────────
        //
        // We want the closest player's head-height transform, not the root.
        // The player model has a dedicated "HeadPlayer" anchor (see
        // LocalPlayerSampler) plus the usual armature chain. If neither exists
        // (e.g. a custom model without the standard skeleton), we parent a
        // synthetic anchor 1.55m above the player root so the IK still aims at
        // head height instead of the floor.

        private static readonly Dictionary<int, Transform> _headCache =
            new Dictionary<int, Transform>();
        private static readonly Dictionary<int, Transform> _syntheticAnchors =
            new Dictionary<int, Transform>();

        public static Transform ClosestLookTarget(Vector3 position)
        {
            Transform root = ClosestTo(position);
            if (root == null) return null;
            return ResolveHead(root);
        }

        public static Transform ResolveHead(Transform root)
        {
            if (root == null) return null;

            int id = root.GetInstanceID();
            Transform cached;
            if (_headCache.TryGetValue(id, out cached) && cached != null)
                return cached;

            Transform head = null;

            // 1. Dedicated head-height anchors (matches LocalPlayerSampler).
            head = root.Find("HeadPlayer");
            if (head == null) head = root.Find("HeadMirror");
            if (head == null) head = root.Find("Person/HeadMirror");

            // 2. Standard armature bone paths, most specific first.
            if (head == null) head = root.Find("Person/Armature/Hips/Spine/Chest/Neck2/Neck1/Head");
            if (head == null) head = root.Find("Armature/Hips/Spine/Chest/Neck2/Neck1/Head");
            if (head == null) head = root.Find("Armature/Hips/Spine/Chest/Neck1/Head");
            if (head == null) head = root.Find("Armature/Hips/Spine/Chest/Neck/Head");

            // 3. Name search. "Neck1" is a distinctive bone name that will not
            //    collide with mesh transforms named "Head" (MiSide's character
            //    models have a mesh called "Head", which is why searching for
            //    "Head" first is a trap).
            if (head == null) head = FindByNameRecursive(root, "Neck1", maxDepth: 8);
            if (head == null) head = FindByNameRecursive(root, "Head", maxDepth: 8);

            // 4. Last resort: a synthetic anchor parented to the root at eye
            //    height. Better than aiming at the feet.
            if (head == null)
            {
                Transform anchor;
                if (_syntheticAnchors.TryGetValue(id, out anchor) && anchor != null)
                {
                    head = anchor;
                }
                else
                {
                    GameObject go = new GameObject("MitaLookHeadAnchor");
                    go.transform.SetParent(root, false);
                    go.transform.localPosition = new Vector3(0f, 1.55f, 0f);
                    anchor = go.transform;
                    _syntheticAnchors[id] = anchor;
                    head = anchor;
                }
            }

            _headCache[id] = head;
            return head;
        }

        private static Transform FindByNameRecursive(Transform t, string name, int maxDepth)
        {
            if (t == null || maxDepth <= 0) return null;

            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);
                if (child == null) continue;

                if (child.name == name) return child;

                Transform hit = FindByNameRecursive(child, name, maxDepth - 1);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}