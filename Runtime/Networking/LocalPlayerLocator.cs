using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public static class LocalPlayerLocator
    {
        // ── Hardcoded target ────────────────────────────────────────────────────
        /// <summary>
        /// The one and only path we look for.  Backslash is the display form;
        /// forward-slash is what Unity's Transform.Find / GameObject.Find want.
        /// </summary>
        public const string HardcodedPlayerPathDisplay = "GameController\\Player\\Person";
        public const string HardcodedPlayerPathUnity   = "GameController/Player/Person";

        // ── Legacy candidate arrays kept for visual-root helpers ────────────────
        private static readonly string[] FallbackVisualRootCandidates =
        {
            "CameraMita/Mita",
            "Mita/MitaPerson Mita",
            "MitaPerson Mita",
            "MitaPerson Mita/Body",
            "Mita",
            "Model",
            "Visuals",
            "PlayerModel",
            "Character",
            "Body",
            "Armature",
            "Mesh",
            "Avatar"
        };

        private static readonly string[] PlayerKeywords =
        {
            "player", "tamagotchi", "mita", "person", "personage",
            "character", "controller", "firstperson", "first_person",
            "fps", "move", "movement", "walk", "body", "camera_root", "cameraroot"
        };

        private static readonly string[] ContainerKeywords =
        {
            "scene", "world", "level", "environment", "room", "rooms",
            "manager", "managers", "system", "systems", "ui", "canvas",
            "eventsystem", "remote", "puppet", "misidemultiplayer"
        };

        private static readonly string[] VisualBlockKeywords =
        {
            "bathroom", "toilet", "vent", "vents", "cockroach", "prop", "props",
            "furniture", "decor", "wall", "floor", "ceiling", "door", "window",
            "light", "lamp", "trigger", "collision"
        };

        // ── Primary hardcoded lookup ────────────────────────────────────────────

        /// <summary>
        /// Looks for the single hard-coded path GameController/Player/Person.
        /// Works even if the target object is inactive.
        /// </summary>
        public static Transform FindHardcodedPlayerPath()
        {
            // Fast path: Unity's built-in find (active objects only).
            GameObject direct = GameObject.Find(HardcodedPlayerPathUnity);
            if (direct != null)
                return direct.transform;

            // Slow path: manual traversal so we catch inactive objects too.
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                return null;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] == null)
                    continue;

                if (string.Equals(roots[i].name, "GameController", StringComparison.OrdinalIgnoreCase))
                {
                    Transform found = roots[i].transform.Find("Player/Person");
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        // ── skinnedfind simulation ──────────────────────────────────────────────

        /// <summary>
        /// Simulates the in-game 'skinnedfind' debug command.
        /// Lists every SkinnedMeshRenderer in the active scene with its full path.
        /// </summary>
        public static string RunSkinnedFind()
        {
            SkinnedMeshRenderer[] renderers = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>();
            StringBuilder sb = new StringBuilder();
            sb.Append("[skinnedfind] Found ");
            sb.Append(renderers.Length);
            sb.AppendLine(" SkinnedMeshRenderer(s) in scene:");

            for (int i = 0; i < renderers.Length; i++)
            {
                SkinnedMeshRenderer r = renderers[i];
                if (r == null)
                    continue;

                sb.Append("  [");
                sb.Append(i);
                sb.Append("] ");
                sb.Append(GetPath(r.transform));
                if (r.sharedMesh != null)
                {
                    sb.Append("  mesh:");
                    sb.Append(r.sharedMesh.name);
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        // ── find * simulation ───────────────────────────────────────────────────

        /// <summary>
        /// Simulates the in-game 'find *' debug command.
        /// Lists all scene root objects and expands GameController's subtree
        /// so the developer can verify whether GameController/Player/Person exists.
        /// </summary>
        public static string RunFindAll()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                return "[find *] No valid scene is currently loaded.";

            StringBuilder sb = new StringBuilder();
            GameObject[] roots = scene.GetRootGameObjects();
            sb.Append("[find *] Scene '");
            sb.Append(scene.name);
            sb.Append("' — ");
            sb.Append(roots.Length);
            sb.AppendLine(" root object(s):");

            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] == null)
                    continue;

                sb.Append("  ");
                sb.Append(roots[i].name);
                sb.Append(" (children:");
                sb.Append(roots[i].transform.childCount);
                sb.AppendLine(")");

                // Fully expand GameController so the user can see Player/Person.
                if (string.Equals(roots[i].name, "GameController", StringComparison.OrdinalIgnoreCase))
                {
                    DumpTransformRecursive(roots[i].transform, sb, "    ", 0, 5);
                }
            }

            return sb.ToString().TrimEnd();
        }

        // ── Visual root helpers (unchanged) ─────────────────────────────────────

        public static Transform FindVisualRoot(Transform playerRoot, string[] visualRootCandidates)
        {
            if (playerRoot == null)
                return null;

            string[] candidates = MergeCandidates(visualRootCandidates, FallbackVisualRootCandidates);

            for (int i = 0; i < candidates.Length; i++)
            {
                Transform found = FindDeepChild(playerRoot, candidates[i]);
                Transform promoted = PromoteVisualRoot(found, playerRoot);
                if (IsUsableCharacterVisualRoot(promoted))
                    return promoted;
            }

            for (int i = 0; i < playerRoot.childCount; i++)
            {
                Transform child = playerRoot.GetChild(i);
                Transform promoted = PromoteVisualRoot(child, playerRoot);
                if (IsUsableCharacterVisualRoot(promoted))
                    return promoted;
            }

            if (IsUsableCharacterVisualRoot(playerRoot))
                return playerRoot;

            return null;
        }

        public static Transform FindBestSceneVisualRoot(string[] visualRootCandidates)
        {
            string[] candidates = MergeCandidates(visualRootCandidates, FallbackVisualRootCandidates);
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded)
                return null;

            VisualCandidate best = new VisualCandidate();
            GameObject[] roots = activeScene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                Transform root = roots[i].transform;
                for (int j = 0; j < candidates.Length; j++)
                {
                    Transform found = FindDeepChild(root, candidates[j]);
                    ConsiderVisualCandidate(PromoteVisualRoot(found, root), ref best);
                }
            }

            Animator[] animators = UnityEngine.Object.FindObjectsOfType<Animator>();
            for (int i = 0; i < animators.Length; i++)
            {
                Animator animator = animators[i];
                if (animator != null && IsInLoadedScene(animator.gameObject))
                    ConsiderVisualCandidate(PromoteVisualRoot(animator.transform, null), ref best);
            }

            SkinnedMeshRenderer[] skinnedRenderers = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>();
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinnedRenderers[i];
                if (renderer != null && IsInLoadedScene(renderer.gameObject))
                    ConsiderVisualCandidate(PromoteVisualRoot(renderer.transform, null), ref best);
            }

            if (best.Transform != null)
            {
                DiagnosticLog.Info(
                    "Selected fallback scene visual source score=" +
                    best.Score +
                    " path=" +
                    GetPath(best.Transform) +
                    " reasons=" +
                    best.Reasons);
            }

            return best.Transform;
        }

        // ── Grounded helper ─────────────────────────────────────────────────────

        public static bool TryReadGrounded(Transform root, out bool isGrounded)
        {
            isGrounded = false;

            if (root == null)
                return false;

            CharacterController controller = root.GetComponentInChildren<CharacterController>();
            if (controller != null)
            {
                isGrounded = controller.isGrounded;
                return true;
            }

            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null)
                    continue;

                if (TryReadBoolMember(behaviour, "isGrounded", out isGrounded) ||
                    TryReadBoolMember(behaviour, "IsGrounded", out isGrounded) ||
                    TryReadBoolMember(behaviour, "grounded",   out isGrounded) ||
                    TryReadBoolMember(behaviour, "Grounded",   out isGrounded))
                {
                    return true;
                }
            }

            return false;
        }

        // ── Path utilities (public) ──────────────────────────────────────────────

        public static string GetPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string path = transform.name;
            Transform parent = transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        // ── Internal helpers ────────────────────────────────────────────────────

        private static void DumpTransformRecursive(
            Transform t,
            StringBuilder sb,
            string indent,
            int depth,
            int maxDepth)
        {
            if (t == null || depth > maxDepth)
                return;

            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);
                if (child == null)
                    continue;

                sb.Append(indent);
                sb.Append(depth == 0 ? "└─ " : "   ");
                sb.Append(child.name);
                sb.Append(" (children:");
                sb.Append(child.childCount);
                sb.AppendLine(")");

                DumpTransformRecursive(child, sb, indent + "   ", depth + 1, maxDepth);
            }
        }

        private static Transform PromoteVisualRoot(Transform found, Transform playerRoot)
        {
            if (found == null)
                return null;

            Transform best = found;
            Transform current = found.parent;

            while (current != null)
            {
                if (current != found && ContainsCamera(current))
                    break;

                string loweredName = current.name.ToLowerInvariant();
                if (LooksLikeVisualRootName(loweredName) || current.GetComponent<Animator>() != null)
                    best = current;

                if (current == playerRoot)
                    break;

                current = current.parent;
            }

            return best;
        }

        private static bool ContainsCamera(Transform transform)
        {
            if (transform == null)
                return false;

            if (transform.GetComponent<Camera>() != null)
                return true;

            string loweredName = transform.name.ToLowerInvariant();
            return loweredName.IndexOf("camera", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeVisualRootName(string loweredName)
        {
            if (string.IsNullOrEmpty(loweredName))
                return false;

            return loweredName.IndexOf("model",      StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("visual",     StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("player",     StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("body",       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("character",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("person",     StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("avatar",     StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("armature",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("tamagotchi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("mita",       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsUsableCharacterVisualRoot(Transform root)
        {
            if (root == null || root.gameObject == null || !IsInLoadedScene(root.gameObject))
                return false;

            string path        = GetPath(root);
            string loweredPath = path.ToLowerInvariant();
            string loweredName = root.name.ToLowerInvariant();

            if (IsHardEnvironmentName(loweredName) || ContainsAny(loweredPath, VisualBlockKeywords))
                return false;

            bool hasSkinnedMesh = root.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
            bool hasAnimator    = root.GetComponentInChildren<Animator>(true) != null;
            if (!hasSkinnedMesh && !hasAnimator)
                return false;

            if (LooksLikeVisualRootName(loweredName) || ContainsAny(loweredPath, PlayerKeywords))
                return true;

            SkinnedMeshRenderer[] skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (skinnedRenderers != null)
            {
                for (int i = 0; i < skinnedRenderers.Length; i++)
                {
                    SkinnedMeshRenderer renderer = skinnedRenderers[i];
                    if (renderer != null &&
                        renderer.sharedMesh != null &&
                        LooksLikeVisualRootName(renderer.name.ToLowerInvariant()))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void ConsiderVisualCandidate(Transform candidate, ref VisualCandidate best)
        {
            if (!IsUsableCharacterVisualRoot(candidate))
                return;

            string reasons;
            int score = ScoreVisualCandidate(candidate, out reasons);
            if (score <= best.Score)
                return;

            best.Transform = candidate;
            best.Score     = score;
            best.Reasons   = reasons;
        }

        private static int ScoreVisualCandidate(Transform candidate, out string reasons)
        {
            int score = 0;
            reasons = string.Empty;

            if (candidate == null)
                return score;

            string path        = GetPath(candidate);
            string loweredPath = path.ToLowerInvariant();
            string loweredName = candidate.name.ToLowerInvariant();

            if (LooksLikeVisualRootName(loweredName))
                AddVisualScore(ref score, ref reasons, 180, "visual-name");
            if (ContainsAny(loweredPath, PlayerKeywords))
                AddVisualScore(ref score, ref reasons, 140, "character-path");
            if (candidate.GetComponentInChildren<Animator>(true) != null)
                AddVisualScore(ref score, ref reasons, 120, "animator");

            int skinnedCount = CountSkinnedRenderersUnder(candidate, 8);
            if (skinnedCount > 0)
                AddVisualScore(ref score, ref reasons, 120 + skinnedCount * 20, "skinned-mesh");

            int meshCount = CountMeshRenderersUnder(candidate, 12);
            if (meshCount > 0)
                AddVisualScore(ref score, ref reasons, Mathf.Min(meshCount * 5, 35), "mesh-renderer");

            return score;
        }

        private static void AddVisualScore(ref int score, ref string reasons, int points, string reason)
        {
            score += points;
            if (string.IsNullOrEmpty(reasons))
                reasons = reason;
            else
                reasons += "," + reason;
        }

        private static Transform FindDeepChild(Transform root, string nameOrPath)
        {
            if (root == null || string.IsNullOrEmpty(nameOrPath))
                return null;

            Transform directPath = root.Find(nameOrPath);
            if (directPath != null)
                return directPath;

            if (string.Equals(root.name, nameOrPath, StringComparison.OrdinalIgnoreCase))
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeepChild(root.GetChild(i), nameOrPath);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static bool IsHardEnvironmentName(string loweredName)
        {
            if (string.IsNullOrEmpty(loweredName))
                return false;

            return loweredName.IndexOf("housegame",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("bathroom",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("toilet",     StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.IndexOf("cockroach",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                   loweredName.Equals("vent",               StringComparison.OrdinalIgnoreCase) ||
                   loweredName.Equals("vents",              StringComparison.OrdinalIgnoreCase) ||
                   loweredName.Equals("bathroom vents",     StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadBoolMember(MonoBehaviour behaviour, string memberName, out bool value)
        {
            value = false;

            try
            {
                Type type = behaviour.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                FieldInfo field = type.GetField(memberName, flags);
                if (field != null && field.FieldType == typeof(bool))
                {
                    value = (bool)field.GetValue(behaviour);
                    return true;
                }

                PropertyInfo property = type.GetProperty(memberName, flags);
                if (property != null &&
                    property.PropertyType == typeof(bool) &&
                    property.GetIndexParameters().Length == 0)
                {
                    value = (bool)property.GetValue(behaviour, null);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        private static bool IsInLoadedScene(GameObject gameObject)
        {
            return gameObject != null && gameObject.scene.IsValid() && gameObject.scene.isLoaded;
        }

        private static bool ContainsAny(string value, string[] needles)
        {
            if (string.IsNullOrEmpty(value) || needles == null)
                return false;

            for (int i = 0; i < needles.Length; i++)
            {
                string needle = needles[i];
                if (!string.IsNullOrEmpty(needle) &&
                    value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string[] MergeCandidates(string[] configured, string[] fallback)
        {
            List<string> merged = new List<string>();
            AddCandidates(merged, configured);
            AddCandidates(merged, fallback);
            return merged.ToArray();
        }

        private static void AddCandidates(List<string> merged, string[] candidates)
        {
            if (candidates == null)
                return;

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (string.IsNullOrEmpty(candidate))
                    continue;

                bool exists = false;
                for (int j = 0; j < merged.Count; j++)
                {
                    if (string.Equals(merged[j], candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                    merged.Add(candidate);
            }
        }

        private static int CountSkinnedRenderersUnder(Transform root, int maxCount)
        {
            if (root == null)
                return 0;

            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers == null)
                return 0;

            return Mathf.Min(renderers.Length, maxCount);
        }

        private static int CountMeshRenderersUnder(Transform root, int maxCount)
        {
            if (root == null)
                return 0;

            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers == null)
                return 0;

            return Mathf.Min(renderers.Length, maxCount);
        }

        private struct VisualCandidate
        {
            public Transform Transform;
            public int Score;
            public string Reasons;
        }
    }
}
