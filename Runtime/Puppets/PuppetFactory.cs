using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed class PuppetFactory
    {
        private readonly Transform puppetParent;
        private string[] visualRootCandidates = new string[0];

        public PuppetFactory(Transform parent)
        {
            puppetParent = parent;
        }

        public void SetVisualRootCandidates(string[] candidates)
        {
            visualRootCandidates = candidates ?? new string[0];
        }

        public PuppetController Create(string playerId, Transform localPlayerRoot)
        {
            if (localPlayerRoot == null)
            {
                Debug.LogWarning("[MiSideMultiplayer] Cannot create puppet: local player root is null.");
                return null;
            }

            bool preserveSourceOffset = true;
            Transform sourceVisualRoot = LocalPlayerLocator.FindVisualRoot(localPlayerRoot, visualRootCandidates);
            if (sourceVisualRoot == null)
            {
                preserveSourceOffset = false;
                sourceVisualRoot = LocalPlayerLocator.FindBestSceneVisualRoot(visualRootCandidates);
            }

            if (sourceVisualRoot == null)
            {
                Debug.LogWarning("[MiSideMultiplayer] Cannot create puppet: visual source not found.");
                return null;
            }

            DiagnosticLog.Info("Using visual source for puppet clone: " + LocalPlayerLocator.GetPath(sourceVisualRoot));

            GameObject root = new GameObject("RemotePuppet_" + SanitizeName(playerId));
            if (puppetParent != null)
                root.transform.SetParent(puppetParent, false);

            PuppetController controller = new PuppetController(root);
            Transform visualRoot = VisualCloneUtility.InstantiateVisualOnlyHierarchy(
                sourceVisualRoot,
                localPlayerRoot,
                root.transform,
                preserveSourceOffset);

            if (visualRoot == null)
            {
                visualRoot = VisualCloneUtility.CloneVisualOnlyHierarchy(sourceVisualRoot, root.transform);
                if (preserveSourceOffset)
                    VisualCloneUtility.AlignCloneRootToSourceReference(sourceVisualRoot, localPlayerRoot, visualRoot);
                else
                    VisualCloneUtility.PlaceCloneAtPuppetRoot(sourceVisualRoot, visualRoot);

                VisualCloneUtility.ForceRenderersVisible(visualRoot);
            }

            int rendererCount = VisualCloneUtility.CountRenderers(visualRoot);
            controller.Bind(playerId, visualRoot, rendererCount == 0);

            DiagnosticLog.Info(
                "Remote puppet direct visual clone ready for '" +
                playerId +
                "' with " +
                rendererCount +
                " renderer(s).");

            if (rendererCount == 0)
                Debug.LogWarning("[MiSideMultiplayer] Puppet visual clone has no renderers.");

            PuppetSafety.ValidateVisualOnly(root);
            return controller;
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "Unknown";

            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                    chars[i] = '_';
            }

            return new string(chars);
        }
    }
}
