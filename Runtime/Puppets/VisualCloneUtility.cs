using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MiSideMultiplayer
{
    public static class VisualCloneUtility
    {
        public static Transform InstantiateVisualOnlyHierarchy(
            Transform sourceRoot,
            Transform sourceReferenceRoot,
            Transform parent)
        {
            return InstantiateVisualOnlyHierarchy(sourceRoot, sourceReferenceRoot, parent, true);
        }

        public static Transform InstantiateVisualOnlyHierarchy(
            Transform sourceRoot,
            Transform sourceReferenceRoot,
            Transform parent,
            bool preserveSourceOffset)
        {
            if (sourceRoot == null)
                return null;

            GameObject clone = UnityEngine.Object.Instantiate(sourceRoot.gameObject);
            clone.name = sourceRoot.name + "_PuppetVisual";

            Transform cloneTransform = clone.transform;
            cloneTransform.SetParent(parent, false);
            if (preserveSourceOffset)
                AlignCloneRootToSourceReference(sourceRoot, sourceReferenceRoot, cloneTransform);
            else
                PlaceCloneAtPuppetRoot(sourceRoot, cloneTransform);

            // Copy MaterialPropertyBlocks BEFORE sanitization.
            // Material_ColorVariables (a MonoBehaviour) sets skin/hair colour per-renderer
            // via SetPropertyBlock(). After SanitizeVisualClone removes MonoBehaviours,
            // those blocks are gone and the clone's skin renders magenta (default shader
            // colour). Copying them here preserves skin tone on the puppet.
            CopyMaterialPropertyBlocks(sourceRoot, cloneTransform);

            SanitizeVisualClone(cloneTransform, sourceRoot);
            ForceRenderersVisible(cloneTransform);
            return cloneTransform;
        }

        public static Transform CloneVisualOnlyHierarchy(Transform sourceRoot, Transform parent)
        {
            if (sourceRoot == null)
                return null;

            Dictionary<Transform, Transform> transformMap = new Dictionary<Transform, Transform>();
            Transform cloneRoot = CloneTransformTree(sourceRoot, parent, transformMap);

            foreach (KeyValuePair<Transform, Transform> pair in transformMap)
                CopyWhitelistedVisualComponents(pair.Key, pair.Value, transformMap);

            return cloneRoot;
        }

        public static void AlignCloneRootToSourceReference(
            Transform sourceRoot,
            Transform sourceReferenceRoot,
            Transform cloneRoot)
        {
            if (sourceRoot == null || sourceReferenceRoot == null || cloneRoot == null)
                return;

            cloneRoot.localPosition = sourceReferenceRoot.InverseTransformPoint(sourceRoot.position);
            cloneRoot.localRotation = Quaternion.Inverse(sourceReferenceRoot.rotation) * sourceRoot.rotation;
            cloneRoot.localScale = sourceRoot.localScale;
        }

        public static void PlaceCloneAtPuppetRoot(Transform sourceRoot, Transform cloneRoot)
        {
            if (cloneRoot == null)
                return;

            cloneRoot.localPosition = Vector3.zero;
            cloneRoot.localRotation = Quaternion.identity;
            cloneRoot.localScale = sourceRoot != null ? sourceRoot.localScale : Vector3.one;
        }

        public static int CountRenderers(Transform root)
        {
            if (root == null)
                return 0;

            SkinnedMeshRenderer[] skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            int skinnedCount = skinnedRenderers != null ? skinnedRenderers.Length : 0;
            int meshCount = meshRenderers != null ? meshRenderers.Length : 0;
            return skinnedCount + meshCount;
        }

        // ── Sanitize — strips gameplay scripts but EXPLICITLY preserves Animator ──────
        public static void SanitizeVisualClone(Transform root, Transform animatorSource = null)
        {
            if (root == null)
                return;

            // Save all Animators and their settings BEFORE the MonoBehaviour sweep
            // (In IL2CPP the sweep may inadvertently hit Animator — this guarantees restoration)
            AnimatorSnapshot[] snapshots = SaveAnimators(root);

            RemoveComponents<Camera>(root);
            RemoveComponents<AudioListener>(root);
            RemoveComponents<AudioSource>(root);
            RemoveComponents<Rigidbody>(root);
            RemoveComponents<Rigidbody2D>(root);
            RemoveComponents<Collider>(root);
            RemoveComponents<Collider2D>(root);
            RemoveComponents<CharacterController>(root);
            RemoveComponents<MonoBehaviour>(root);

            // Restore any Animator that was destroyed by the sweep above
            RestoreAnimators(root, snapshots, animatorSource);

            DiagnosticLog.Info(
                "SanitizeVisualClone: swept " + root.name +
                ", animator snapshots: " + snapshots.Length);
        }

        public static void ForceRenderersVisible(Transform root)
        {
            if (root == null)
                return;

            SetActiveRecursive(root, true);

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                renderer.enabled = true;

                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null)
                    skinned.updateWhenOffscreen = true;
            }
        }

        // ── Animator preservation helpers ──────────────────────────────────────────────

        private struct AnimatorSnapshot
        {
            public Transform Target;
            public RuntimeAnimatorController Controller;
            public Avatar Avatar;
            public AnimatorUpdateMode UpdateMode;
            public AnimatorCullingMode CullingMode;
        }

        private static AnimatorSnapshot[] SaveAnimators(Transform root)
        {
            Animator[] animators = root.GetComponentsInChildren<Animator>(true);
            if (animators == null || animators.Length == 0)
                return new AnimatorSnapshot[0];

            AnimatorSnapshot[] snapshots = new AnimatorSnapshot[animators.Length];
            for (int i = 0; i < animators.Length; i++)
            {
                Animator a = animators[i];
                if (a == null) continue;
                snapshots[i].Target      = a.transform;
                snapshots[i].Controller  = a.runtimeAnimatorController;
                snapshots[i].Avatar      = a.avatar;
                snapshots[i].UpdateMode  = a.updateMode;
                snapshots[i].CullingMode = a.cullingMode;
            }
            return snapshots;
        }

        private static void RestoreAnimators(Transform root, AnimatorSnapshot[] snapshots, Transform source)
        {
            for (int i = 0; i < snapshots.Length; i++)
            {
                Transform target = snapshots[i].Target;
                if (target == null) continue;

                Animator existing = target.GetComponent<Animator>();
                if (existing != null)
                {
                    // Animator survived — just make sure it's configured correctly.
                    existing.applyRootMotion = false;
                    continue;
                }

                // Animator was destroyed — re-add it.
                Animator restored = target.gameObject.AddComponent<Animator>();
                restored.runtimeAnimatorController = snapshots[i].Controller;
                restored.avatar                    = snapshots[i].Avatar;
                restored.applyRootMotion           = false;
                restored.updateMode                = snapshots[i].UpdateMode;
                restored.cullingMode               = snapshots[i].CullingMode;
                restored.enabled                   = true;

                DiagnosticLog.Warning(
                    "Animator on '" + target.name + "' was removed by MonoBehaviour sweep " +
                    "and has been restored.");
            }
        }

        // ── MaterialPropertyBlock copy ─────────────────────────────────────────────────
        // Copies per-renderer property blocks from source to clone (matched by index).
        // Must run before SanitizeVisualClone so both hierarchies are structurally identical.
        // Fixes magenta skin: Material_ColorVariables sets skin tone via SetPropertyBlock();
        // once that MonoBehaviour is removed, the clone shows the shader's default magenta.
        private static void CopyMaterialPropertyBlocks(Transform source, Transform clone)
        {
            if (source == null || clone == null) return;
            try
            {
                Renderer[] srcR = source.GetComponentsInChildren<Renderer>(true);
                Renderer[] dstR = clone.GetComponentsInChildren<Renderer>(true);
                if (srcR == null || dstR == null) return;
                int count = srcR.Length < dstR.Length ? srcR.Length : dstR.Length;
                MaterialPropertyBlock block = new MaterialPropertyBlock();
                for (int i = 0; i < count; i++)
                {
                    if (srcR[i] == null || dstR[i] == null) continue;
                    try
                    {
                        srcR[i].GetPropertyBlock(block);
                        dstR[i].SetPropertyBlock(block);
                    }
                    catch (Exception) { }
                }
                DiagnosticLog.Info(
                    "CopyMaterialPropertyBlocks: " + count +
                    " renderer(s) copied from '" + source.name + "'.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("CopyMaterialPropertyBlocks failed: " + ex.Message);
            }
        }

        // ── Component removal helper ───────────────────────────────────────────────────

        private static void RemoveComponents<T>(Transform root) where T : Component
        {
            T[] components = root.GetComponentsInChildren<T>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                    continue;

                // Never destroy Animator — it's the core of puppet animation.
                if (component is Animator)
                    continue;

                Behaviour behaviour = component as Behaviour;
                if (behaviour != null)
                    behaviour.enabled = false;

                Collider collider = component as Collider;
                if (collider != null)
                    collider.enabled = false;

                Collider2D collider2D = component as Collider2D;
                if (collider2D != null)
                    collider2D.enabled = false;

                UnityEngine.Object.DestroyImmediate(component);
            }
        }

        private static void SetActiveRecursive(Transform root, bool active)
        {
            if (root == null)
                return;

            root.gameObject.SetActive(active);
            for (int i = 0; i < root.childCount; i++)
                SetActiveRecursive(root.GetChild(i), active);
        }

        // ── Manual clone path (CloneVisualOnlyHierarchy) ──────────────────────────────

        private static Transform CloneTransformTree(
            Transform source,
            Transform parent,
            Dictionary<Transform, Transform> transformMap)
        {
            GameObject clone = new GameObject(source.name);
            Transform cloneTransform = clone.transform;
            cloneTransform.SetParent(parent, false);
            cloneTransform.localPosition = source.localPosition;
            cloneTransform.localRotation = source.localRotation;
            cloneTransform.localScale = source.localScale;
            clone.layer = source.gameObject.layer;
            clone.SetActive(source.gameObject.activeSelf);

            transformMap[source] = cloneTransform;

            for (int i = 0; i < source.childCount; i++)
                CloneTransformTree(source.GetChild(i), cloneTransform, transformMap);

            return cloneTransform;
        }

        private static void CopyWhitelistedVisualComponents(
            Transform source,
            Transform destination,
            Dictionary<Transform, Transform> transformMap)
        {
            CopyAnimator(source, destination);
            CopyMeshFilter(source, destination);
            CopyMeshRenderer(source, destination);
            CopySkinnedMeshRenderer(source, destination, transformMap);
        }

        private static void CopyAnimator(Transform source, Transform destination)
        {
            Animator sourceAnimator = source.GetComponent<Animator>();
            if (sourceAnimator == null)
                return;

            Animator animator = destination.gameObject.AddComponent<Animator>();
            animator.runtimeAnimatorController = sourceAnimator.runtimeAnimatorController;
            animator.avatar                    = sourceAnimator.avatar;
            animator.applyRootMotion           = false;
            animator.updateMode                = sourceAnimator.updateMode;
            animator.cullingMode               = sourceAnimator.cullingMode;
            animator.enabled                   = sourceAnimator.enabled;
        }

        private static void CopyMeshFilter(Transform source, Transform destination)
        {
            MeshFilter sourceMeshFilter = source.GetComponent<MeshFilter>();
            if (sourceMeshFilter == null)
                return;

            MeshFilter meshFilter = destination.gameObject.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = sourceMeshFilter.sharedMesh;
        }

        private static void CopyMeshRenderer(Transform source, Transform destination)
        {
            MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();
            if (sourceRenderer == null)
                return;

            MeshRenderer renderer = destination.gameObject.AddComponent<MeshRenderer>();
            CopyRendererSettings(sourceRenderer, renderer);
        }

        private static void CopySkinnedMeshRenderer(
            Transform source,
            Transform destination,
            Dictionary<Transform, Transform> transformMap)
        {
            SkinnedMeshRenderer sourceRenderer = source.GetComponent<SkinnedMeshRenderer>();
            if (sourceRenderer == null)
                return;

            SkinnedMeshRenderer renderer = destination.gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh           = sourceRenderer.sharedMesh;
            renderer.rootBone             = MapTransform(sourceRenderer.rootBone, transformMap);
            renderer.bones                = MapBones(sourceRenderer.bones, transformMap);
            renderer.localBounds          = sourceRenderer.localBounds;
            renderer.quality              = sourceRenderer.quality;
            renderer.updateWhenOffscreen  = true;
            CopyRendererSettings(sourceRenderer, renderer);
        }

        private static void CopyRendererSettings(Renderer source, Renderer destination)
        {
            destination.enabled                  = source.enabled;
            destination.sharedMaterials          = source.sharedMaterials;
            destination.shadowCastingMode        = source.shadowCastingMode;
            destination.receiveShadows           = source.receiveShadows;
            destination.lightProbeUsage          = source.lightProbeUsage;
            destination.reflectionProbeUsage     = source.reflectionProbeUsage;
            destination.probeAnchor              = null;
            destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
        }

        private static Transform[] MapBones(
            Transform[] sourceBones,
            Dictionary<Transform, Transform> transformMap)
        {
            if (sourceBones == null)
                return new Transform[0];

            Transform[] bones = new Transform[sourceBones.Length];
            for (int i = 0; i < sourceBones.Length; i++)
                bones[i] = MapTransform(sourceBones[i], transformMap);

            return bones;
        }

        private static Transform MapTransform(
            Transform source,
            Dictionary<Transform, Transform> transformMap)
        {
            if (source == null)
                return null;

            Transform mapped;
            if (transformMap.TryGetValue(source, out mapped))
                return mapped;

            return null;
        }
    }
}
