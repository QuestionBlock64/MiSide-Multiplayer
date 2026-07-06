using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MiSideMultiplayer
{
    public static class VisualCloneUtility
    {
        // ── Physics component type-names to PRESERVE during sanitisation ───────
        // These are MonoBehaviours that drive cloth / spring / hair physics.
        // Stripping them makes custom models look stiff (no hair/cloth bounce).
        // We match by runtime type-name so we don't need compile-time references
        // to DynamicBone or MS_CustomModels.SpringBones assemblies.
        private static readonly string[] PhysicsKeepNames =
        {
            // MiSide / Unity ecosystem bone-physics
            "DynamicBone",
            "DynamicBoneCollider",
            "DynamicBonePlaneCollider",
            // MS_CustomModels spring bones (from DLL reflection)
            "SpringBone",
            "SpringManager",
            "VRMSpringBone",
            "VRMSpringBoneColliderGroup",
            // VRM secondary components
            "VRMBlendShapeProxy",
            "BlendShapeProxy",
            // Magica Cloth (used by MiSide — MagicaPhysicsManager is scene root).
            // MagicaBoneCloth = cloth sim (skirts etc). MagicaBoneSpring is the
            // LIGHTER hair-sway-only component MagicaCloth2 typically uses for
            // hair specifically — this was missing before, likely why hair on
            // cloned puppets went rigid even though skirt/cloth still moved.
            "MagicaCloth",
            "MagicaBoneCloth",
            "MagicaBoneSpring",
            "MagicaMeshCloth",
            "MagicaMeshSpring",
        };

        // ── Public entry points ───────────────────────────────────────────────

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
            int stage0 = CountRenderers(clone.transform);

            Transform cloneTransform = clone.transform;
            cloneTransform.SetParent(parent, false);

            if (preserveSourceOffset)
                AlignCloneRootToSourceReference(sourceRoot, sourceReferenceRoot, cloneTransform);
            else
                PlaceCloneAtPuppetRoot(sourceRoot, cloneTransform);

            // Copy MaterialPropertyBlocks BEFORE sanitisation.
            // Material_ColorVariables (a MonoBehaviour) sets skin/hair colour per-renderer
            // via SetPropertyBlock(). After SanitizeVisualClone removes MonoBehaviours,
            // those blocks are gone and the clone's skin renders magenta (default shader
            // colour). Copying them here preserves skin tone on the puppet.
            CopyMaterialPropertyBlocks(sourceRoot, cloneTransform);
            int stage1 = CountRenderers(cloneTransform);

            SanitizeVisualClone(cloneTransform, sourceRoot);
            int stage2 = CountRenderers(cloneTransform);

            ForceRenderersVisible(cloneTransform);
            int stage3 = CountRenderers(cloneTransform);

            // Staged diagnostic — if renderers drop to 0 at any stage, this
            // pinpoints exactly which step is responsible instead of guessing.
            DiagnosticLog.Info(
                "Clone pipeline for '" + sourceRoot.name + "': " +
                "afterInstantiate=" + stage0 +
                "  afterAlign+PropBlocks=" + stage1 +
                "  afterSanitize=" + stage2 +
                "  afterForceVisible=" + stage3 + " renderer(s).");

            // HeadMirror / HairMirror presence check — both should survive since
            // they are direct children of Person, which is inside this clone.
            Transform hm = FindByNameRecursiveStatic(cloneTransform, "HeadMirror");
            Transform hr = FindByNameRecursiveStatic(cloneTransform, "HairMirror");
            DiagnosticLog.Info(
                "  HeadMirror=" + (hm != null ? "present(" + CountRenderers(hm) + " r)" : "MISSING") +
                "  HairMirror=" + (hr != null ? "present(" + CountRenderers(hr) + " r)" : "MISSING"));

            // Magenta-material safety net + diagnostics. Magenta in Unity
            // specifically means "shader failed to compile / not included in
            // build" (Hidden/InternalErrorShader) or a null material — it is
            // never a legitimate authored colour. We hide any renderer in that
            // state (better an armless puppet than a glowing error-colour
            // artifact) and log full material/shader detail for every renderer
            // whose path contains "Arm" so the exact object responsible is
            // identifiable from the log even if this heuristic doesn't catch it.
            LogArmAndMaterialDiagnostics(cloneTransform);

            return cloneTransform;
        }

        private static void LogArmAndMaterialDiagnostics(Transform cloneTransform)
        {
            try
            {
                Renderer[] renderers = cloneTransform.GetComponentsInChildren<Renderer>(true);
                int hiddenBroken = 0;

                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer r = renderers[i];
                    if (r == null) continue;

                    bool isArmPath = r.gameObject.name.IndexOf("Arm",
                        StringComparison.OrdinalIgnoreCase) >= 0;

                    Material[] mats = r.sharedMaterials;
                    bool broken = (mats == null || mats.Length == 0);
                    string matSummary = "none";
                    if (!broken)
                    {
                        System.Text.StringBuilder ms = new System.Text.StringBuilder();
                        for (int m = 0; m < mats.Length; m++)
                        {
                            if (m > 0) ms.Append('|');
                            if (mats[m] == null || mats[m].shader == null)
                            {
                                broken = true;
                                ms.Append("NULL");
                                continue;
                            }
                            string shaderName = mats[m].shader.name;
                            ms.Append(mats[m].name).Append('/').Append(shaderName);
                            if (shaderName.IndexOf("InternalError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                shaderName.IndexOf("Hidden/", StringComparison.OrdinalIgnoreCase) >= 0)
                                broken = true;
                        }
                        matSummary = ms.ToString();
                    }

                    if (isArmPath)
                        DiagnosticLog.Info(
                            "  [arm-path renderer] " + LocalPlayerLocator.GetPath(r.transform) +
                            "  bounds=" + r.bounds.size.ToString("F2") +
                            "  materials=" + matSummary +
                            "  broken=" + broken);

                    if (broken && r.enabled)
                    {
                        r.enabled = false;
                        hiddenBroken++;
                        DiagnosticLog.Warning(
                            "  Hid renderer with broken/missing material: " +
                            LocalPlayerLocator.GetPath(r.transform) + " (materials=" + matSummary + ")");
                    }
                }

                if (hiddenBroken > 0)
                    DiagnosticLog.Info("  Hid " + hiddenBroken + " renderer(s) with broken materials.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("LogArmAndMaterialDiagnostics failed: " + ex.Message);
            }
        }

        private static Transform FindByNameRecursiveStatic(Transform root, string targetName)
        {
            if (root == null) return null;
            if (string.Equals(root.name, targetName, StringComparison.OrdinalIgnoreCase))
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform r = FindByNameRecursiveStatic(root.GetChild(i), targetName);
                if (r != null) return r;
            }
            return null;
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

            cloneRoot.localPosition =
                sourceReferenceRoot.InverseTransformPoint(sourceRoot.position);
            cloneRoot.localRotation =
                Quaternion.Inverse(sourceReferenceRoot.rotation) * sourceRoot.rotation;
            cloneRoot.localScale = sourceRoot.localScale;
        }

        public static void PlaceCloneAtPuppetRoot(Transform sourceRoot, Transform cloneRoot)
        {
            if (cloneRoot == null) return;
            cloneRoot.localPosition = Vector3.zero;
            cloneRoot.localRotation = Quaternion.identity;
            cloneRoot.localScale    = sourceRoot != null ? sourceRoot.localScale : Vector3.one;
        }

        public static int CountRenderers(Transform root)
        {
            if (root == null) return 0;
            SkinnedMeshRenderer[] skinned  = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            MeshRenderer[]        mesh     = root.GetComponentsInChildren<MeshRenderer>(true);
            int s = skinned != null ? skinned.Length : 0;
            int m = mesh    != null ? mesh.Length    : 0;
            return s + m;
        }

        // ── Sanitise — strips gameplay scripts, KEEPS Animator + physics ──────
        public static void SanitizeVisualClone(Transform root, Transform animatorSource = null)
        {
            if (root == null) return;

            // Snapshot all Animators before the MonoBehaviour sweep.
            // In IL2CPP the sweep can inadvertently destroy Animator;
            // we restore them afterwards.
            AnimatorSnapshot[] snapshots = SaveAnimators(root);

            RemoveComponents<Camera>(root);
            RemoveComponents<AudioListener>(root);
            RemoveComponents<AudioSource>(root);
            RemoveComponents<Rigidbody>(root);
            RemoveComponents<Rigidbody2D>(root);
            RemoveComponents<Collider>(root);
            RemoveComponents<Collider2D>(root);
            RemoveComponents<CharacterController>(root);
            // Physics MonoBehaviours (DynamicBone, SpringBone …) are preserved by
            // IsPhysicsComponent() inside RemoveComponents.
            RemoveComponents<MonoBehaviour>(root);

            // Ensure Animator survived (or restore it from snapshot)
            RestoreAnimators(root, snapshots);

            DiagnosticLog.Info(
                "SanitizeVisualClone: swept '" + root.name +
                "'  animator snapshots=" + snapshots.Length);
        }

        // Confirmed in IL2CPP dump: PlayerMove.HideBody(bool) exists — MiSide
        // hides the local player's own body from their own first-person camera
        // by some combination of layer exclusion and/or shadow-only rendering
        // (the game also uses PIDI_PlanarReflection for mirrors, which reads
        // meshes via a LayerMask — "v_reflectLayers" — confirming a layer-based
        // visibility scheme exists in this project). Whichever technique the
        // original body used, Object.Instantiate() copies it onto our clone
        // too. A puppet is always viewed from a THIRD-PERSON external camera,
        // so we force every cloned renderer back to fully-visible defaults:
        // Default layer (0) and normal (non-shadow-only) rendering.
        public static void ForceRenderersVisible(Transform root)
        {
            if (root == null) return;

            SetActiveRecursive(root, true);

            int defaultLayer = LayerMask.NameToLayer("Default");
            if (defaultLayer < 0) defaultLayer = 0;

            int layersReset = 0;
            int shadowModesReset = 0;

            Transform[] allTransforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < allTransforms.Length; i++)
            {
                GameObject go = allTransforms[i] != null ? allTransforms[i].gameObject : null;
                if (go != null && go.layer != defaultLayer)
                {
                    go.layer = defaultLayer;
                    layersReset++;
                }
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                r.enabled = true;

                if (r.shadowCastingMode != ShadowCastingMode.On)
                {
                    r.shadowCastingMode = ShadowCastingMode.On;
                    shadowModesReset++;
                }

                SkinnedMeshRenderer skinned = r as SkinnedMeshRenderer;
                if (skinned != null)
                    skinned.updateWhenOffscreen = true;
            }

            if (layersReset > 0 || shadowModesReset > 0)
                DiagnosticLog.Info(
                    "ForceRenderersVisible: reset " + layersReset +
                    " object layer(s) to Default and " + shadowModesReset +
                    " renderer shadow-mode(s) to On (undoing first-person body hiding).");
        }

        // ── Animator snapshot / restore ───────────────────────────────────────

        private struct AnimatorSnapshot
        {
            public Transform               Target;
            public RuntimeAnimatorController Controller;
            public Avatar                  Avatar;
            public AnimatorUpdateMode      UpdateMode;
            public AnimatorCullingMode     CullingMode;
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

        private static void RestoreAnimators(Transform root, AnimatorSnapshot[] snapshots)
        {
            for (int i = 0; i < snapshots.Length; i++)
            {
                Transform target = snapshots[i].Target;
                if (target == null) continue;

                Animator existing = target.GetComponent<Animator>();
                if (existing != null)
                {
                    // Survived — normalise settings for puppet use.
                    existing.applyRootMotion = false;
                    existing.cullingMode     = AnimatorCullingMode.AlwaysAnimate;
                    existing.updateMode      = AnimatorUpdateMode.Normal;
                    existing.enabled         = true;
                    continue;
                }

                // Was destroyed by the MonoBehaviour sweep — re-add it.
                Animator restored = target.gameObject.AddComponent<Animator>();
                restored.runtimeAnimatorController = snapshots[i].Controller;
                restored.avatar                    = snapshots[i].Avatar;
                restored.applyRootMotion           = false;
                restored.cullingMode               = AnimatorCullingMode.AlwaysAnimate;
                restored.updateMode                = AnimatorUpdateMode.Normal;
                restored.enabled                   = true;

                DiagnosticLog.Warning(
                    "Animator on '" + target.name +
                    "' removed by MonoBehaviour sweep — restored.");
            }
        }

        // ── MaterialPropertyBlock copy ─────────────────────────────────────────
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

        // ── Component removal helper ──────────────────────────────────────────
        private static void RemoveComponents<T>(Transform root) where T : Component
        {
            T[] components = root.GetComponentsInChildren<T>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null) continue;

                // Always keep Animator.
                if (component is Animator)
                    continue;

                // Keep bone-physics MonoBehaviours (DynamicBone, SpringBone …).
                if (IsPhysicsComponent(component))
                    continue;

                Behaviour b = component as Behaviour;
                if (b != null) b.enabled = false;

                Collider   col  = component as Collider;
                Collider2D col2 = component as Collider2D;
                if (col  != null) col.enabled  = false;
                if (col2 != null) col2.enabled = false;

                UnityEngine.Object.DestroyImmediate(component);
            }
        }

        // ── Physics-component guard ───────────────────────────────────────────
        private static bool IsPhysicsComponent(Component c)
        {
            if (c == null) return false;
            string typeName = c.GetType().Name;
            for (int i = 0; i < PhysicsKeepNames.Length; i++)
                if (string.Equals(typeName, PhysicsKeepNames[i],
                                  StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // ── Utility ───────────────────────────────────────────────────────────
        private static void SetActiveRecursive(Transform root, bool active)
        {
            if (root == null) return;
            root.gameObject.SetActive(active);
            for (int i = 0; i < root.childCount; i++)
                SetActiveRecursive(root.GetChild(i), active);
        }

        // ── Manual-clone path ─────────────────────────────────────────────────
        private static Transform CloneTransformTree(
            Transform source,
            Transform parent,
            Dictionary<Transform, Transform> transformMap)
        {
            GameObject clone = new GameObject(source.name);
            Transform  ct    = clone.transform;
            ct.SetParent(parent, false);
            ct.localPosition = source.localPosition;
            ct.localRotation = source.localRotation;
            ct.localScale    = source.localScale;
            clone.layer      = source.gameObject.layer;
            clone.SetActive(source.gameObject.activeSelf);
            transformMap[source] = ct;
            for (int i = 0; i < source.childCount; i++)
                CloneTransformTree(source.GetChild(i), ct, transformMap);
            return ct;
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

        private static void CopyAnimator(Transform src, Transform dst)
        {
            Animator sa = src.GetComponent<Animator>();
            if (sa == null) return;
            Animator a = dst.gameObject.AddComponent<Animator>();
            a.runtimeAnimatorController = sa.runtimeAnimatorController;
            a.avatar          = sa.avatar;
            a.applyRootMotion = false;
            a.updateMode      = AnimatorUpdateMode.Normal;
            a.cullingMode     = AnimatorCullingMode.AlwaysAnimate;
            a.enabled         = sa.enabled;
        }

        private static void CopyMeshFilter(Transform src, Transform dst)
        {
            MeshFilter sf = src.GetComponent<MeshFilter>();
            if (sf == null) return;
            dst.gameObject.AddComponent<MeshFilter>().sharedMesh = sf.sharedMesh;
        }

        private static void CopyMeshRenderer(Transform src, Transform dst)
        {
            MeshRenderer sr = src.GetComponent<MeshRenderer>();
            if (sr == null) return;
            CopyRendererSettings(sr, dst.gameObject.AddComponent<MeshRenderer>());
        }

        private static void CopySkinnedMeshRenderer(
            Transform src, Transform dst,
            Dictionary<Transform, Transform> transformMap)
        {
            SkinnedMeshRenderer sr = src.GetComponent<SkinnedMeshRenderer>();
            if (sr == null) return;
            SkinnedMeshRenderer dr = dst.gameObject.AddComponent<SkinnedMeshRenderer>();
            dr.sharedMesh          = sr.sharedMesh;
            dr.rootBone            = MapTransform(sr.rootBone, transformMap);
            dr.bones               = MapBones(sr.bones, transformMap);
            dr.localBounds         = sr.localBounds;
            dr.quality             = sr.quality;
            dr.updateWhenOffscreen = true;
            CopyRendererSettings(sr, dr);
        }

        private static void CopyRendererSettings(Renderer src, Renderer dst)
        {
            dst.enabled                   = src.enabled;
            dst.sharedMaterials           = src.sharedMaterials;
            dst.shadowCastingMode         = src.shadowCastingMode;
            dst.receiveShadows            = src.receiveShadows;
            dst.lightProbeUsage           = src.lightProbeUsage;
            dst.reflectionProbeUsage      = src.reflectionProbeUsage;
            dst.probeAnchor               = null;
            dst.allowOcclusionWhenDynamic = src.allowOcclusionWhenDynamic;
        }

        private static Transform[] MapBones(
            Transform[] sourceBones,
            Dictionary<Transform, Transform> transformMap)
        {
            if (sourceBones == null) return new Transform[0];
            Transform[] bones = new Transform[sourceBones.Length];
            for (int i = 0; i < sourceBones.Length; i++)
                bones[i] = MapTransform(sourceBones[i], transformMap);
            return bones;
        }

        private static Transform MapTransform(
            Transform source,
            Dictionary<Transform, Transform> transformMap)
        {
            if (source == null) return null;
            Transform mapped;
            return transformMap.TryGetValue(source, out mapped) ? mapped : null;
        }
    }
}
