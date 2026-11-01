using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Syncs the item held in the local player's hand to remote puppets.
    ///
    /// Resolution strategy for the clone source (in order):
    ///   1. Per-name cache from previous successful lookups.
    ///   2. LOCAL player's own clone under their hand bone.
    ///   3. LOCAL player's own PlayerMove.item when its name matches.
    ///   4. GameObject.Find (active only).
    ///   5. Resources.FindObjectsOfTypeAll (last resort).
    /// </summary>
    public sealed class HeldItemSync
    {
        private const string RightItemBonePath =
            "Person/Armature/Hips/Spine/Chest/Right shoulder/Right arm/Right elbow/Right wrist/Right item";

        private const string LocalHandRelativePath =
            "Person/Armature/Hips/Spine/Chest/Right shoulder/Right arm/Right elbow/Right wrist/Right item";

        private const string CloneNamePrefix = "MiSidePuppet_Item_";

        // ── Per-puppet state ────────────────────────────────────────────────
        private sealed class PuppetItemState
        {
            public string CurrentItemName = "";
            public bool   CurrentHidden;
            public bool   HasBuiltClones;
            public bool   LoggedLookupFailure;
            public readonly List<GameObject> Clones = new List<GameObject>();
        }

        private readonly Dictionary<PuppetController, PuppetItemState> states =
            new Dictionary<PuppetController, PuppetItemState>();

        // ── Scene-side item cache ───────────────────────────────────────────
        private readonly Dictionary<string, GameObject> sceneItemCache =
            new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private string cachedSceneName = "";

        // ── Reflection cache: PuppetController.latestState ──────────────────
        private static FieldInfo puppetLatestStateField;
        private static bool      puppetLatestStateFieldResolved;

        // ── Reflection cache: local PlayerMove.item ─────────────────────────
        private static PropertyInfo localPlayerMoveItemProp;
        private static bool         localPlayerMoveItemResolved;

        // ── Reflection cache: GameController.scrpm ──────────────────────────
        private static PropertyInfo gameControllerPlayerMoveProp;
        private static bool         gameControllerPlayerMoveResolved;

        // ── Component cache: local PlayerMove instance ──────────────────────
        private static Component cachedLocalPlayerMove;
        private static float     nextLocalPlayerMoveLookupTime;

        // ── Diagnostic rate limit ───────────────────────────────────────────
        private static float nextDiagLogTime;
        private const float DiagLogInterval = 5f;

        // ── Public API ──────────────────────────────────────────────────────

        public void Tick()
        {
            string sceneName = UnityEngine.SceneManagement.SceneManager
                .GetActiveScene().name;
            if (sceneName != cachedSceneName)
            {
                cachedSceneName = sceneName;
                sceneItemCache.Clear();
                cachedLocalPlayerMove = null;
                foreach (var kvp in states)
                {
                    if (kvp.Value == null) continue;
                    kvp.Value.HasBuiltClones = false;
                    kvp.Value.Clones.Clear();
                }
                BuildSceneItemCache();
            }

            List<PuppetController> puppets = new List<PuppetController>();
            foreach (PuppetController p in PuppetRegistry.AllPuppets)
            {
                if (p != null && p.GameObject != null)
                    puppets.Add(p);
            }

            for (int i = 0; i < puppets.Count; i++)
            {
                PuppetController puppet = puppets[i];
                if (puppet == null || puppet.GameObject == null)
                    continue;

                try { ApplyToPuppet(puppet); }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning(
                        "HeldItemSync: ApplyToPuppet threw: " +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }

            PruneDestroyedPuppets(puppets);
        }

        public void Dispose()
        {
            foreach (var kvp in states)
            {
                if (kvp.Value == null) continue;
                DestroyClones(kvp.Value);
            }
            states.Clear();
            sceneItemCache.Clear();
            cachedLocalPlayerMove = null;
        }

        // ── Per-puppet application ──────────────────────────────────────────

        private void ApplyToPuppet(PuppetController puppet)
        {
            string remoteItemName = null;
            bool   remoteHidden   = false;

            if (!TryReadRemoteHeldItem(puppet, out remoteItemName, out remoteHidden))
                return;

            if (remoteItemName == null)
                remoteItemName = "";

            PuppetItemState state = GetOrCreateState(puppet);

            bool nameChanged   = !string.Equals(state.CurrentItemName, remoteItemName, StringComparison.Ordinal);
            bool hiddenChanged = state.CurrentHidden != remoteHidden;

            bool needsBuild = nameChanged
                || (!state.HasBuiltClones && !string.IsNullOrEmpty(remoteItemName));

            bool needsVisibilityUpdate = hiddenChanged;

            if (!needsBuild && !needsVisibilityUpdate)
                return;

            if (needsBuild)
            {
                if (nameChanged)
                {
                    DestroyClones(state);
                    state.CurrentItemName = remoteItemName;
                    state.HasBuiltClones = false;
                    state.LoggedLookupFailure = false;
                }

                if (!string.IsNullOrEmpty(remoteItemName))
                {
                    GameObject source;
                    if (TryGetSceneItem(remoteItemName, out source))
                    {
                        if (!state.HasBuiltClones)
                        {
                            DestroyClones(state);
                            BuildClones(puppet, source, state);
                            if (state.Clones.Count > 0)
                                state.HasBuiltClones = true;
                        }
                    }
                    else
                    {
                        // Only log the first failure per name per puppet.
                        if (nameChanged && !state.LoggedLookupFailure)
                        {
                            state.LoggedLookupFailure = true;
                            DiagnosticLog.Info(
                                "HeldItemSync: no scene item named '" + remoteItemName +
                                "' to clone for puppet '" + puppet.GameObject.name +
                                "' (will retry).");
                        }
                    }
                }
            }

            state.CurrentHidden = remoteHidden;
            SetClonesVisible(state, !remoteHidden);
        }

        private bool TryReadRemoteHeldItem(
            PuppetController puppet, out string itemName, out bool hidden)
        {
            itemName = null;
            hidden   = false;

            if (puppet == null) return false;

            if (!puppetLatestStateFieldResolved)
            {
                puppetLatestStateFieldResolved = true;
                try
                {
                    puppetLatestStateField = typeof(PuppetController).GetField(
                        "latestState",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning(
                        "HeldItemSync: reflection on PuppetController.latestState threw: " +
                        ex.GetType().Name + ": " + ex.Message);
                }

                if (puppetLatestStateField == null)
                {
                    DiagnosticLog.Warning(
                        "HeldItemSync: PuppetController.latestState field NOT FOUND. " +
                        "Held items will not sync.");
                }
                else
                {
                    DiagnosticLog.Info(
                        "HeldItemSync: resolved PuppetController.latestState via reflection.");
                }
            }

            if (puppetLatestStateField == null) return false;

            RemotePlayerState snapshot = null;
            try
            {
                snapshot = puppetLatestStateField.GetValue(puppet) as RemotePlayerState;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "HeldItemSync: reading latestState failed: " +
                    ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            if (snapshot == null) return false;

            itemName = snapshot.heldItemName;
            hidden   = snapshot.heldItemHidden;
            return true;
        }

        private PuppetItemState GetOrCreateState(PuppetController puppet)
        {
            PuppetItemState state;
            if (states.TryGetValue(puppet, out state) && state != null)
                return state;

            state = new PuppetItemState();
            states[puppet] = state;
            return state;
        }

        private void PruneDestroyedPuppets(List<PuppetController> live)
        {
            List<PuppetController> toRemove = null;
            foreach (var kvp in states)
            {
                PuppetController p = kvp.Key;
                bool alive = false;
                for (int i = 0; i < live.Count; i++)
                {
                    if (live[i] == p) { alive = true; break; }
                }
                if (alive) continue;
                if (toRemove == null) toRemove = new List<PuppetController>();
                toRemove.Add(p);
            }
            if (toRemove == null) return;

            for (int i = 0; i < toRemove.Count; i++)
            {
                PuppetItemState s;
                if (states.TryGetValue(toRemove[i], out s) && s != null)
                    DestroyClones(s);
                states.Remove(toRemove[i]);
            }
        }

        // ── Cloning ─────────────────────────────────────────────────────────

        private void BuildClones(PuppetController puppet, GameObject source,
                                 PuppetItemState state)
        {
            Transform hand = puppet.GameObject.transform.Find(RightItemBonePath);

            Transform sourceT = source.transform;
            int sourceChildren = sourceT != null ? sourceT.childCount : 0;

            DiagnosticLog.Info(
                "HeldItemSync[DIAG]: puppet='" + puppet.GameObject.name +
                "' boneFound=" + (hand != null) +
                " source='" + source.name +
                "' sourceChildren=" + sourceChildren +
                "' sourceActiveInHierarchy=" + source.activeInHierarchy);

            if (hand == null)
            {
                DiagnosticLog.Warning(
                    "HeldItemSync: puppet '" + puppet.GameObject.name +
                    "' has no 'Right item' bone. Dumping puppet root children:");
                Transform root = puppet.GameObject.transform;
                for (int i = 0; i < root.childCount; i++)
                {
                    Transform c = root.GetChild(i);
                    DiagnosticLog.Info(
                        "  child[" + i + "] = '" + (c != null ? c.name : "<null>") + "'");
                }
                return;
            }

            if (sourceT == null)
            {
                DiagnosticLog.Warning(
                    "HeldItemSync: source '" + source.name +
                    "' has no transform; cannot clone.");
                return;
            }

            int built = 0;

            for (int i = 0; i < sourceT.childCount; i++)
            {
                Transform child = sourceT.GetChild(i);
                if (child == null) continue;
                if (!HasRenderableDescendant(child)) continue;

                GameObject clone;
                try
                {
                    clone = UnityEngine.Object.Instantiate(child.gameObject);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning(
                        "HeldItemSync: instantiate failed for '" + child.name +
                        "': " + ex.Message);
                    continue;
                }

                clone.name = CloneNamePrefix + child.name;
                clone.transform.SetParent(hand, false);
                clone.transform.localPosition = child.localPosition;
                clone.transform.localRotation = child.localRotation;
                clone.transform.localScale    = child.localScale;
                clone.SetActive(true);

                StripHostileComponents(clone);

                state.Clones.Add(clone);
                built++;
            }

            DiagnosticLog.Info(
                "HeldItemSync: built " + built + " clone(s) for puppet '" +
                puppet.GameObject.name + "' from item '" + source.name + "'.");
        }

        private static bool HasRenderableDescendant(Transform t)
        {
            if (t == null) return false;
            if (t.GetComponent<Renderer>() != null) return true;
            for (int i = 0; i < t.childCount; i++)
            {
                if (HasRenderableDescendant(t.GetChild(i))) return true;
            }
            return false;
        }

        private static void StripHostileComponents(GameObject go)
        {
            try
            {
                Collider[] colliders = go.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                    if (colliders[i] != null)
                        UnityEngine.Object.Destroy(colliders[i]);
            }
            catch (Exception) { }

            try
            {
                Rigidbody[] rbs = go.GetComponentsInChildren<Rigidbody>(true);
                for (int i = 0; i < rbs.Length; i++)
                    if (rbs[i] != null)
                        UnityEngine.Object.Destroy(rbs[i]);
            }
            catch (Exception) { }

            try
            {
                Camera[] cams = go.GetComponentsInChildren<Camera>(true);
                for (int i = 0; i < cams.Length; i++)
                    if (cams[i] != null)
                        UnityEngine.Object.Destroy(cams[i]);
            }
            catch (Exception) { }

            try
            {
                AudioListener[] als = go.GetComponentsInChildren<AudioListener>(true);
                for (int i = 0; i < als.Length; i++)
                    if (als[i] != null)
                        UnityEngine.Object.Destroy(als[i]);
            }
            catch (Exception) { }
        }

        private static void DestroyClones(PuppetItemState state)
        {
            if (state == null) return;
            for (int i = 0; i < state.Clones.Count; i++)
            {
                GameObject clone = state.Clones[i];
                if (clone == null) continue;
                try { UnityEngine.Object.Destroy(clone); }
                catch (Exception) { }
            }
            state.Clones.Clear();
        }

        private static void SetClonesVisible(PuppetItemState state, bool visible)
        {
            if (state == null) return;
            for (int i = 0; i < state.Clones.Count; i++)
            {
                GameObject clone = state.Clones[i];
                if (clone == null) continue;
                try
                {
                    if (clone.activeSelf != visible)
                        clone.SetActive(visible);
                }
                catch (Exception) { }
            }
        }

        // ── Scene item cache ────────────────────────────────────────────────

        private void BuildSceneItemCache()
        {
            DiagnosticLog.Info(
                "HeldItemSync: scene changed to '" + cachedSceneName +
                "'; item cache will be resolved lazily.");
        }

        // ── Local PlayerMove reflection ─────────────────────────────────────

        private static PropertyInfo ResolveLocalPlayerMoveItem()
        {
            if (localPlayerMoveItemResolved) return localPlayerMoveItemProp;
            localPlayerMoveItemResolved = true;

            try
            {
                Type playerMoveType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type candidate = asm.GetType("PlayerMove", false);
                    if (candidate != null) { playerMoveType = candidate; break; }
                }
                if (playerMoveType == null)
                {
                    DiagnosticLog.Warning(
                        "HeldItemSync: PlayerMove type not found in any loaded assembly.");
                    return null;
                }

                localPlayerMoveItemProp = playerMoveType.GetProperty(
                    "item",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (localPlayerMoveItemProp == null)
                {
                    DiagnosticLog.Warning(
                        "HeldItemSync: PlayerMove.item property not found via reflection.");
                }
                else
                {
                    DiagnosticLog.Info(
                        "HeldItemSync: resolved PlayerMove.item via reflection.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "HeldItemSync: reflecting PlayerMove.item failed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }

            return localPlayerMoveItemProp;
        }

        /// <summary>
        /// Locate the local PlayerMove instance.
        ///
        /// PlayerMove is NOT a component on GameController/Player. It is a
        /// property on GameController called "scrpm". We try every component
        /// attached to the GameController GameObject and call the scrpm
        /// getter on each (via reflection) until one returns a non-null
        /// PlayerMove component.
        ///
        /// Do NOT rely on GetType().Name for matching: under Il2CppInterop
        /// the managed wrapper's Name is often the wrapper type, not the
        /// original MonoBehaviour name.
        /// </summary>
        private static Component GetLocalPlayerMove()
        {
            if (cachedLocalPlayerMove != null)
                return cachedLocalPlayerMove;

            if (Time.unscaledTime < nextLocalPlayerMoveLookupTime)
                return null;
            nextLocalPlayerMoveLookupTime = Time.unscaledTime + 1f;

            // ── Path A: call GameController.scrpm on every component ────────
            try
            {
                if (!gameControllerPlayerMoveResolved)
                {
                    gameControllerPlayerMoveResolved = true;
                    Type gcType = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type candidate = asm.GetType("GameController", false);
                        if (candidate != null) { gcType = candidate; break; }
                    }
                    if (gcType != null)
                    {
                        gameControllerPlayerMoveProp = gcType.GetProperty(
                            "scrpm",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    if (gameControllerPlayerMoveProp == null)
                    {
                        DiagnosticLog.Info(
                            "HeldItemSync: GameController.scrpm property not found.");
                    }
                }

                if (gameControllerPlayerMoveProp != null)
                {
                    GameObject gc = GameObject.Find("GameController");
                    if (gc != null)
                    {
                        Component[] comps = gc.GetComponents<Component>();
                        if (comps != null)
                        {
                            for (int i = 0; i < comps.Length; i++)
                            {
                                Component comp = comps[i];
                                if (comp == null) continue;

                                object pm = null;
                                try
                                {
                                    pm = gameControllerPlayerMoveProp.GetValue(comp);
                                }
                                catch (Exception) { continue; }

                                Component pmComp = pm as Component;
                                if (pmComp != null)
                                {
                                    cachedLocalPlayerMove = pmComp;
                                    DiagnosticLog.Info(
                                        "HeldItemSync: resolved PlayerMove via " +
                                        "GameController.scrpm on component #" + i +
                                        " of '" + gc.name + "' -> '" +
                                        pmComp.gameObject.name + "'.");
                                    return pmComp;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "HeldItemSync: GameController.scrpm path failed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }

            // ── Path B: component scan ──────────────────────────────────────
            try
            {
                Il2CppSystem.Type il2cppCompType =
                    Il2CppInterop.Runtime.Il2CppType.From(typeof(Component));

                UnityEngine.Object[] all =
                    Resources.FindObjectsOfTypeAll(il2cppCompType);
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        Component c = all[i] as Component;
                        if (c == null) continue;
                        if (c.GetType().Name == "PlayerMove")
                        {
                            cachedLocalPlayerMove = c;
                            DiagnosticLog.Info(
                                "HeldItemSync: resolved PlayerMove via " +
                                "FindObjectsOfTypeAll on '" +
                                c.gameObject.name + "'.");
                            return c;
                        }
                    }
                }
            }
            catch (Exception) { }

            if (Time.unscaledTime >= nextDiagLogTime)
            {
                nextDiagLogTime = Time.unscaledTime + DiagLogInterval;
                DiagnosticLog.Info(
                    "HeldItemSync: PlayerMove instance not located yet " +
                    "(will retry).");
            }
            return null;
        }

        // ── Scene item lookup ───────────────────────────────────────────────

        private bool TryGetSceneItem(string name, out GameObject source)
        {
            source = null;
            if (string.IsNullOrEmpty(name)) return false;

            // 1. Cache hit.
            if (sceneItemCache.TryGetValue(name, out source) && source != null)
                return true;

            // 2. Preferred: the LOCAL player's own clone under their hand bone.
            try
            {
                string localHeldName = TryGetLocalHeldItemName();
                if (!string.IsNullOrEmpty(localHeldName) &&
                    string.Equals(localHeldName, name, StringComparison.Ordinal))
                {
                    GameObject localHand = FindLocalHandBone();
                    if (localHand != null)
                    {
                        Transform handT = localHand.transform;
                        if (handT != null && handT.childCount > 0)
                        {
                            sceneItemCache[name] = localHand;
                            source = localHand;
                            DiagnosticLog.Info(
                                "HeldItemSync: resolved '" + name +
                                "' via local hand clone (children=" +
                                handT.childCount + ").");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "HeldItemSync: local hand-clone path failed for '" +
                    name + "': " + ex.GetType().Name + ": " + ex.Message);
            }

            // 3. Local PlayerMove.item reference when its name matches.
            try
            {
                PropertyInfo itemProp = ResolveLocalPlayerMoveItem();
                if (itemProp != null)
                {
                    Component pm = GetLocalPlayerMove();
                    if (pm != null)
                    {
                        object heldItem = itemProp.GetValue(pm);
                        GameObject heldGo = heldItem as GameObject;
                        if (heldGo != null &&
                            string.Equals(heldGo.name, name, StringComparison.Ordinal))
                        {
                            sceneItemCache[name] = heldGo;
                            source = heldGo;
                            DiagnosticLog.Info(
                                "HeldItemSync: resolved '" + name +
                                "' via local PlayerMove.item" +
                                " (activeInHierarchy=" + heldGo.activeInHierarchy + ").");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "HeldItemSync: local PlayerMove.item path failed for '" +
                    name + "': " + ex.GetType().Name + ": " + ex.Message);
            }

            // 4. GameObject.Find only finds ACTIVE objects.
            try
            {
                GameObject found = GameObject.Find(name);
                if (found != null)
                {
                    sceneItemCache[name] = found;
                    source = found;
                    DiagnosticLog.Info(
                        "HeldItemSync: resolved '" + name +
                        "' via GameObject.Find (active).");
                    return true;
                }
            }
            catch (Exception) { }

            // 5. Scan every loaded GameObject, including inactive ones.
            try
            {
                Il2CppSystem.Type il2cppGoType =
                    Il2CppInterop.Runtime.Il2CppType.From(typeof(GameObject));

                UnityEngine.Object[] all =
                    Resources.FindObjectsOfTypeAll(il2cppGoType);

                if (all != null)
                {
                    GameObject best = null;
                    for (int i = 0; i < all.Length; i++)
                    {
                        GameObject go = all[i] as GameObject;
                        if (go == null) continue;
                        if (!string.Equals(go.name, name, StringComparison.Ordinal))
                            continue;

                        if (!go.scene.IsValid()) continue;
                        if (string.IsNullOrEmpty(go.scene.name)) continue;

                        if (go.transform.parent == null)
                        {
                            best = go;
                            break;
                        }
                        if (best == null) best = go;
                    }

                    if (best != null)
                    {
                        sceneItemCache[name] = best;
                        source = best;
                        DiagnosticLog.Info(
                            "HeldItemSync: resolved '" + name +
                            "' via Resources.FindObjectsOfTypeAll" +
                            " (activeInHierarchy=" + best.activeInHierarchy +
                            ", scene='" + best.scene.name + "').");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "HeldItemSync: FindObjectsOfTypeAll scan for '" + name +
                    "' failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return false;
        }

        // ── Local player helpers ────────────────────────────────────────────

        private static GameObject FindLocalHandBone()
        {
            try
            {
                GameObject gc = GameObject.Find("GameController");
                if (gc == null) return null;
                Transform p = gc.transform.Find("Player");
                if (p == null) return null;
                Transform hand = p.Find(LocalHandRelativePath);
                if (hand == null) return null;
                return hand.gameObject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string TryGetLocalHeldItemName()
        {
            try
            {
                PropertyInfo itemProp = ResolveLocalPlayerMoveItem();
                if (itemProp == null) return null;

                Component pm = GetLocalPlayerMove();
                if (pm == null) return null;

                object heldItem = itemProp.GetValue(pm);
                if (heldItem == null) return null;

                GameObject heldGo = heldItem as GameObject;
                if (heldGo == null) return null;

                return heldGo.name;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Legacy sidecar. No longer on the read path (HeldItemSync reads
    /// PuppetController.latestState directly). Kept for backward compat.
    /// </summary>
    public static class HeldItemState
    {
        private static readonly Dictionary<PuppetController, RemotePlayerState> latest =
            new Dictionary<PuppetController, RemotePlayerState>();

        public static void Record(PuppetController puppet, RemotePlayerState state)
        {
            if (puppet == null || state == null) return;
            latest[puppet] = state;
        }

        public static RemotePlayerState GetLatestForPuppet(PuppetController puppet)
        {
            if (puppet == null) return null;
            RemotePlayerState s;
            return latest.TryGetValue(puppet, out s) ? s : null;
        }

        public static void Remove(PuppetController puppet)
        {
            if (puppet == null) return;
            latest.Remove(puppet);
        }
    }
}