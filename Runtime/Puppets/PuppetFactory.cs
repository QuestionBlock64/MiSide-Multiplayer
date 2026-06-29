using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed class PuppetFactory
    {
        private readonly Transform puppetParent;
        private string[] visualRootCandidates = new string[0];

        // Sibling names to probe at the Player level (GameController/Player/???)
        // "model(Clone)" is what appears in Image 1's hierarchy under Player.
        private static readonly string[] ModelSiblingNames =
        {
            "model(Clone)", "model", "Model", "PlayerModel",
            "Body", "Character", "Visuals", "Mesh",
        };

        public PuppetFactory(Transform parent)
        {
            puppetParent = parent;
        }

        public void SetVisualRootCandidates(string[] candidates)
        {
            visualRootCandidates = candidates ?? new string[0];
        }

        public PuppetController Create(string playerId, string displayName, Transform localPlayerRoot)
        {
            if (localPlayerRoot == null)
            {
                DiagnosticLog.Error("Cannot create puppet for '" + playerId + "': localPlayerRoot is null.");
                return null;
            }

            // ── 1. Try well-known siblings of Person at the Player level ──────
            // Skeleton is inside Person (Person/Armature/Hips confirmed in dump),
            // but model(Clone) may hold additional skinned meshes / the animator.
            Transform sourceVisualRoot = null;
            bool      preserveOffset   = true;

            Transform playerParent = localPlayerRoot.parent; // GameController/Player
            if (playerParent != null)
            {
                // Named sibling search
                for (int i = 0; i < ModelSiblingNames.Length; i++)
                {
                    Transform sibling = playerParent.Find(ModelSiblingNames[i]);
                    if (sibling == null || sibling == localPlayerRoot) continue;
                    if (VisualCloneUtility.CountRenderers(sibling) > 0)
                    {
                        sourceVisualRoot = sibling;
                        preserveOffset   = false; // model is at Player level, no offset needed
                        DiagnosticLog.Info(
                            "Visual source found as Player-level sibling '" +
                            sibling.name + "': " + LocalPlayerLocator.GetPath(sibling));
                        break;
                    }
                }

                // If not found by name, scan all siblings for any that have renderers
                if (sourceVisualRoot == null)
                {
                    int bestCount = 0;
                    for (int i = 0; i < playerParent.childCount; i++)
                    {
                        Transform child = playerParent.GetChild(i);
                        if (child == null || child == localPlayerRoot) continue;
                        int n = VisualCloneUtility.CountRenderers(child);
                        if (n > bestCount)
                        {
                            bestCount        = n;
                            sourceVisualRoot = child;
                            preserveOffset   = false;
                        }
                    }
                    if (sourceVisualRoot != null)
                        DiagnosticLog.Info(
                            "Visual source found via sibling scan: '" +
                            sourceVisualRoot.name + "' (" +
                            VisualCloneUtility.CountRenderers(sourceVisualRoot) + " renderers).");
                }
            }

            // ── 2. Try inside localPlayerRoot (Person) itself ─────────────────
            if (sourceVisualRoot == null)
            {
                sourceVisualRoot = LocalPlayerLocator.FindVisualRoot(
                    localPlayerRoot, visualRootCandidates);
                if (sourceVisualRoot != null)
                {
                    preserveOffset = true;
                    DiagnosticLog.Info(
                        "Visual source found inside Person: " +
                        LocalPlayerLocator.GetPath(sourceVisualRoot));
                }
            }

            // ── 2b. Person itself has renderers — clone Player (its parent) ───
            // The default MiSide character keeps all meshes (Arms, Clothes,
            // HeadMirror, HairMirror) as direct children of Person, not as
            // Player-level siblings.  Cloning Person alone loses HeadPlayer
            // (which is HeadMirror's rootBone, causing the headless puppet).
            // Cloning Player captures everything with correct bone references.
            // Person is at local (0,0,0) in Player so world offset is zero.
            if (sourceVisualRoot == null && localPlayerRoot.parent != null
                && VisualCloneUtility.CountRenderers(localPlayerRoot) > 0)
            {
                sourceVisualRoot = localPlayerRoot.parent;   // GameController/Player
                preserveOffset   = false;
                DiagnosticLog.Info(
                    "Visual source: Person has " +
                    VisualCloneUtility.CountRenderers(localPlayerRoot) +
                    " renderer(s) inside it — cloning Player parent '" +
                    sourceVisualRoot.name + "' to include HeadPlayer/HeadMirror rootBone.");
            }

            // ── 3. Broad scene scan fallback ──────────────────────────────────
            if (sourceVisualRoot == null)
            {
                preserveOffset   = false;
                sourceVisualRoot = LocalPlayerLocator.FindBestSceneVisualRoot(visualRootCandidates);
                if (sourceVisualRoot != null)
                    DiagnosticLog.Warning(
                        "Visual source found via broad scene scan: " +
                        LocalPlayerLocator.GetPath(sourceVisualRoot) +
                        " — this may be the wrong model.");
            }

            if (sourceVisualRoot == null)
            {
                DiagnosticLog.Error(
                    "Cannot create puppet for '" + playerId +
                    "': no visual source found (tried Player siblings, Person, scene scan).");
                return null;
            }

            // ── Build puppet root ─────────────────────────────────────────────
            GameObject root = new GameObject("RemotePuppet_" + Sanitize(playerId));
            if (puppetParent != null)
                root.transform.SetParent(puppetParent, false);

            PuppetController controller = new PuppetController(root);

            // ── Clone visual ──────────────────────────────────────────────────
            Transform visualClone = VisualCloneUtility.InstantiateVisualOnlyHierarchy(
                sourceVisualRoot, localPlayerRoot, root.transform, preserveOffset);

            if (visualClone == null)
            {
                // Manual clone fallback
                visualClone = VisualCloneUtility.CloneVisualOnlyHierarchy(
                    sourceVisualRoot, root.transform);
                if (preserveOffset)
                    VisualCloneUtility.AlignCloneRootToSourceReference(
                        sourceVisualRoot, localPlayerRoot, visualClone);
                else
                    VisualCloneUtility.PlaceCloneAtPuppetRoot(sourceVisualRoot, visualClone);

                VisualCloneUtility.ForceRenderersVisible(visualClone);
            }

            int renderers = VisualCloneUtility.CountRenderers(visualClone);
            controller.Bind(playerId, displayName, visualClone, renderers == 0);

            DiagnosticLog.Info(
                "Puppet ready: '" + playerId + "' (" + displayName + ")  " +
                renderers + " renderer(s)  source='" + sourceVisualRoot.name + "'.");

            if (renderers == 0)
                DiagnosticLog.Warning(
                    "Puppet visual clone for '" + playerId +
                    "' has 0 renderers — will show fallback capsule.");

            PuppetSafety.ValidateVisualOnly(root);
            return controller;
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
