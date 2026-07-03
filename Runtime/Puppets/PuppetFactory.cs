using System;
using UnityEngine;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Creates remote-puppet GameObjects by cloning the correct visual hierarchy.
    ///
    /// Visual-source priority (based on dump.cs analysis + observed runtime behaviour):
    ///
    ///  PRIORITY 1 — Named custom model sibling (e.g. "model(Clone)")
    ///    The MS_CustomModels loader instantiates a self-contained VRM clone as a
    ///    sibling of Person under GameController/Player.  Self-contained means it has
    ///    its own bones + SkinnedMeshRenderers, so cloning that sibling alone is
    ///    correct and gives the right visual immediately.
    ///    NOTE: if the remote player also has a custom model we try ModelPuppet first.
    ///    Only KNOWN names are matched (see CustomModelSiblingNames) — no generic
    ///    "pick whichever sibling has a renderer" scan, since that previously
    ///    misidentified small utility props (e.g. a door-handle mesh) as a model.
    ///
    ///  PRIORITY 2 — Player-level clone (DEFAULT SKIN PATH — always the fallback)
    ///    The default body lives inside GameController/Player/Person:
    ///      Person/Armature/Hips/... (skeleton)
    ///      Person/HeadMirror / Person/HairMirror (SkinnedMeshRenderers)
    ///      Person/* body meshes     (all disabled by PlayerMove.HideBody in FPP)
    ///    Cloning the PLAYER parent directly (not just Person) captures everything
    ///    in one hierarchy, including HeadPlayer which HeadMirror needs.
    ///    ForceRenderersVisible() re-enables every renderer hidden by HideBody().
    ///    We then shift the Player clone so Person lands at the puppet root's origin.
    /// </summary>
    public sealed class PuppetFactory
    {
        private readonly Transform puppetParent;
        private string[] visualRootCandidates = new string[0];

        // Sibling names to probe at the Player level for a custom model.
        private static readonly string[] CustomModelSiblingNames =
        {
            "model(Clone)", "model", "Model", "PlayerModel",
            "Body",         "Character", "Visuals", "Mesh",
        };

        public PuppetFactory(Transform parent)
        {
            puppetParent = parent;
        }

        public void SetVisualRootCandidates(string[] candidates)
        {
            visualRootCandidates = candidates ?? new string[0];
        }

        public PuppetController Create(string playerId, string displayName,
                                       Transform localPlayerRoot,
                                       string    remoteCustomModelName = null)
        {
            if (localPlayerRoot == null)
            {
                DiagnosticLog.Error(
                    "Cannot create puppet for '" + playerId + "': localPlayerRoot is null.");
                return null;
            }

            Transform personRoot;
            Transform playerLevel = ResolvePlayerLevel(localPlayerRoot, out personRoot);
            bool localRootIsPlayerLevel = playerLevel == localPlayerRoot;

            if (playerLevel == null)
            {
                DiagnosticLog.Error(
                    "Cannot create puppet for '" + playerId +
                    "': could not resolve GameController/Player from local root '" +
                    LocalPlayerLocator.GetPath(localPlayerRoot) + "'.");
                return null;
            }

            // Build puppet root first so we can pass it to ModelPuppet if needed.
            GameObject root = new GameObject("RemotePuppet_" + Sanitize(playerId));
            if (puppetParent != null)
                root.transform.SetParent(puppetParent, false);

            PuppetController controller = new PuppetController(root);

            // ── Try MS_CustomModels ModelPuppet API first ─────────────────────
            // If the remote player has a custom model AND ModelPuppet is available,
            // delegate the visual entirely to MS_CustomModels.  Fall through if this
            // fails so the standard clone path still produces something.
            bool modelPuppetApplied = false;
            if (!string.IsNullOrEmpty(remoteCustomModelName) &&
                remoteCustomModelName != "None")
            {
                DiagnosticLog.Info(
                    "Puppet '" + playerId + "' — remote custom model: '" +
                    remoteCustomModelName + "'. Attempting ModelPuppet load.");
                modelPuppetApplied =
                    CustomModelBridge.TryApplyModelToPuppet(root, remoteCustomModelName);
            }

            // ── If ModelPuppet handled it, skip the clone entirely ─────────────
            if (modelPuppetApplied)
            {
                // ModelPuppet takes over; just bind with no cloned visual.
                // showFallbackMarker = false — ModelPuppet supplies the visual.
                controller.Bind(playerId, displayName, null, false);
                DiagnosticLog.Info(
                    "Puppet '" + playerId + "' visual delegated to ModelPuppet.");
                PuppetSafety.ValidateVisualOnly(root);
                return controller;
            }

            // ── Standard clone path ───────────────────────────────────────────
            Transform sourceVisualRoot;
            bool      preserveOffset;
            SelectVisualSource(personRoot, playerLevel,
                               out sourceVisualRoot, out preserveOffset);

            if (sourceVisualRoot == null)
            {
                DiagnosticLog.Error(
                    "Cannot create puppet for '" + playerId +
                    "': no visual source found (Player siblings, Player-level clone," +
                    " scene scan all failed).");
                UnityEngine.Object.Destroy(root);
                return null;
            }

            // ── Clone visual ──────────────────────────────────────────────────
            Transform visualClone =
                VisualCloneUtility.InstantiateVisualOnlyHierarchy(
                    sourceVisualRoot, localPlayerRoot, root.transform, preserveOffset);

            if (visualClone == null)
            {
                visualClone = VisualCloneUtility.CloneVisualOnlyHierarchy(
                    sourceVisualRoot, root.transform);
                if (preserveOffset)
                    VisualCloneUtility.AlignCloneRootToSourceReference(
                        sourceVisualRoot, localPlayerRoot, visualClone);
                else
                    VisualCloneUtility.PlaceCloneAtPuppetRoot(sourceVisualRoot, visualClone);
                VisualCloneUtility.ForceRenderersVisible(visualClone);
            }

            // ── Align Player-level clone so Person sits at puppet root ─────────
            if (!localRootIsPlayerLevel &&
                !preserveOffset &&
                sourceVisualRoot == playerLevel &&
                visualClone != null &&
                personRoot != null)
            {
                AlignPlayerCloneToPersonRoot(visualClone, personRoot.name);
            }

            int renderers = VisualCloneUtility.CountRenderers(visualClone);
            controller.Bind(playerId, displayName, visualClone, renderers == 0);

            DiagnosticLog.Info(
                "Puppet ready: '" + playerId + "' (" + displayName + ")  " +
                renderers + " renderer(s)  source='" + sourceVisualRoot.name + "'.");

            if (renderers == 0)
                DiagnosticLog.Warning(
                    "Puppet '" + playerId + "' has 0 renderers after clone. " +
                    "Fallback capsule shown. Check SanitizeVisualClone log.");
            else
                LogRendererDiagnostics(playerId, visualClone);

            PuppetSafety.ValidateVisualOnly(root);
            return controller;
        }

        // ── Renderer diagnostics ────────────────────────────────────────────────
        // Prints per-renderer enabled/layer/shadow/active state once per puppet.
        // If a puppet is STILL invisible despite renderers > 0, this log dump
        // pinpoints exactly which flag is wrong instead of requiring more guessing.
        private static void LogRendererDiagnostics(string playerId, Transform visualClone)
        {
            try
            {
                Renderer[] renderers = visualClone.GetComponentsInChildren<Renderer>(true);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("Renderer diagnostics for '" + playerId + "':\n");
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer r = renderers[i];
                    if (r == null) continue;
                    sb.Append("  [" + i + "] " + r.gameObject.name +
                              "  enabled=" + r.enabled +
                              "  activeInHierarchy=" + r.gameObject.activeInHierarchy +
                              "  layer=" + r.gameObject.layer +
                              "  shadowMode=" + r.shadowCastingMode +
                              "  bounds=" + r.bounds.size.ToString("F2") + "\n");
                }
                DiagnosticLog.Info(sb.ToString());
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("LogRendererDiagnostics failed: " + ex.Message);
            }
        }

        // ── Visual-source selection ───────────────────────────────────────────
        private static Transform ResolvePlayerLevel(
            Transform localPlayerRoot,
            out Transform personRoot)
        {
            personRoot = null;
            if (localPlayerRoot == null)
                return null;

            if (string.Equals(localPlayerRoot.name, "Player", StringComparison.OrdinalIgnoreCase))
            {
                personRoot = localPlayerRoot.Find("Person");
                return localPlayerRoot;
            }

            if (string.Equals(localPlayerRoot.name, "Person", StringComparison.OrdinalIgnoreCase))
            {
                personRoot = localPlayerRoot;
                return localPlayerRoot.parent;
            }

            Transform person = localPlayerRoot.Find("Person");
            if (person != null)
            {
                personRoot = person;
                return localPlayerRoot;
            }

            personRoot = localPlayerRoot;
            return localPlayerRoot.parent;
        }

        private static void SelectVisualSource(
            Transform personRoot,
            Transform playerLevel,
            out Transform sourceVisualRoot,
            out bool      preserveOffset)
        {
            sourceVisualRoot = null;
            preserveOffset   = false;

            // ── PRIORITY 1: custom-model sibling at Player level ──────────────
            // A VRM / custom skin loader instantiates a self-contained sibling
            // (e.g. "model(Clone)") that has its own bones — clone it directly.
            // NOTE: we only match KNOWN names here. An earlier "unnamed sibling
            // scan" (pick whichever Player-level child has the most renderers)
            // was removed — it could misidentify small utility props (e.g. a
            // doorknob prop under "Hand Left Door") as "the model", which is
            // the exact false-positive bug already found and fixed once in
            // CustomModelBridge.DetectFromHierarchy. Simpler and more
            // predictable: named match or straight to Priority 2.
            for (int i = 0; i < CustomModelSiblingNames.Length; i++)
            {
                Transform sib = playerLevel.Find(CustomModelSiblingNames[i]);
                if (sib == null || sib == personRoot) continue;
                if (VisualCloneUtility.CountRenderers(sib) > 0)
                {
                    sourceVisualRoot = sib;
                    DiagnosticLog.Info(
                        "Visual source: named custom model sibling '" + sib.name + "'.");
                    return;
                }
            }

            // ── PRIORITY 2: clone from Player level (default skin path) ───────
            // The default MiSide body lives inside Person (HeadMirror, HairMirror,
            // body meshes).  FindVisualRoot() historically found only HeadMirror
            // (the first SMR inside Person), giving a headless puppet.
            // Cloning the full Player parent captures everything:
            //   • Person/Armature/... (skeleton — correct bone references)
            //   • Person/HeadMirror, Person/HairMirror (mirror-visibility meshes)
            //   • Person/* body meshes (re-enabled by ForceRenderersVisible)
            //   • HeadPlayer (camera rig — camera removed by sanitise)
            sourceVisualRoot = playerLevel;
            preserveOffset   = false;
            DiagnosticLog.Info(
                "Visual source: Player level '" + playerLevel.name +
                "' (default skin — direct clone of Player, full skeleton + body meshes).");
        }

        // ── Person-alignment helper ───────────────────────────────────────────
        /// <summary>
        /// After cloning GameController/Player, find the Person child inside the
        /// clone and offset the clone root so Person lands at (0,0,0) of the puppet.
        /// </summary>
        private static void AlignPlayerCloneToPersonRoot(
            Transform playerClone, string personName)
        {
            if (playerClone == null) return;

            Transform personInClone = null;

            // Direct child search first (fastest)
            for (int i = 0; i < playerClone.childCount; i++)
            {
                Transform ch = playerClone.GetChild(i);
                if (ch != null &&
                    string.Equals(ch.name, personName, StringComparison.OrdinalIgnoreCase))
                {
                    personInClone = ch;
                    break;
                }
            }

            // Deep fallback
            if (personInClone == null)
                personInClone = FindByName(playerClone, personName);

            if (personInClone != null)
            {
                Vector3 offset = personInClone.localPosition;
                if (offset.sqrMagnitude > 0.0001f)
                {
                    playerClone.localPosition = -offset;
                    DiagnosticLog.Info(
                        "AlignPlayerClone: Person offset " + offset +
                        " compensated → clone moved to " + playerClone.localPosition);
                }
            }
        }

        private static Transform FindByName(Transform root, string name)
        {
            if (root == null) return null;
            if (string.Equals(root.name, name, StringComparison.OrdinalIgnoreCase))
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform r = FindByName(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Unknown";
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                    chars[i] = '_';
            return new string(chars);
        }
    }
}
