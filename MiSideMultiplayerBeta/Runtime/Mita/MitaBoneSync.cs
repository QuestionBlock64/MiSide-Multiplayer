using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Mita uses the same per-bone pose transport as player puppets. Quest
    /// transitions can reparent the rendered model away from its AI object,
    /// so resolve and synchronize the active visual rig as well as its bones.
    /// </summary>
    internal static class MitaBoneSync
    {
        private const string RigRootPosePath = "$armature";
        private const string RigWorldPosePath = "$armature-world";
        private const string ModelPosePathPrefix = "$model:";
        private const string HeadPosePathPrefix = "$head:";
        private const float BindingValidationInterval = 0.25f;

        private static readonly string[] ExplicitHeadBoneNames =
        {
            "head", "headbone", "neck", "neck1", "neck2", "jaw", "eye", "eyes"
        };

        private sealed class PoseBinding
        {
            public Transform PersonRoot;
            public Transform RigRoot;
            public string[] Paths;
            public Transform[] Targets;
            public BoneTransformData[] Values;
            public Dictionary<string, BoneTransformData> Source;
            public int MissingTargets;
            public int HierarchyFingerprint;
            public float NextValidationTime;
        }

        private static readonly Dictionary<int, PoseBinding> SampleBindings =
            new Dictionary<int, PoseBinding>();
        private static readonly Dictionary<int, PoseBinding> ApplyBindings =
            new Dictionary<int, PoseBinding>();
        private static float _nextSampleFailureLogTime;
        private static float _nextApplyFailureLogTime;

        internal static void ClearBindings()
        {
            SampleBindings.Clear();
            ApplyBindings.Clear();
        }

        internal static int CountExplicitHeadPoseEntries(
            Dictionary<string, BoneTransformData> boneData)
        {
            if (boneData == null) return 0;
            int count = 0;
            foreach (string path in boneData.Keys)
                if (IsHeadPosePath(path)) count++;
            return count;
        }

        internal static bool TryCaptureHeadPose(Transform personRoot, out string path,
                                                 out Vector3 localPosition,
                                                 out Quaternion localRotation)
        {
            path = null;
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            if (personRoot == null) return false;

            try
            {
                Transform rigRoot = GetSkeletonRoot(personRoot);
                Transform searchRoot = rigRoot != null ? rigRoot : personRoot;
                Transform best = null;
                int bestScore = -1;
                int bestDepth = -1;
                List<Transform> pending = new List<Transform>();
                pending.Add(searchRoot);
                while (pending.Count > 0)
                {
                    int last = pending.Count - 1;
                    Transform current = pending[last];
                    pending.RemoveAt(last);
                    if (current == null) continue;

                    int score = GetHeadBoneScore(current.name);
                    int depth = GetDepthFromRoot(searchRoot, current);
                    if (score > bestScore || (score == bestScore && depth > bestDepth))
                    {
                        best = current;
                        bestScore = score;
                        bestDepth = depth;
                    }
                    for (int i = current.childCount - 1; i >= 0; i--)
                    {
                        Transform child = current.GetChild(i);
                        if (child != null) pending.Add(child);
                    }
                }

                if (best == null || best == personRoot) return false;
                path = BuildRelativePath(personRoot, best);
                if (string.IsNullOrEmpty(path)) return false;
                localPosition = best.localPosition;
                localRotation = best.localRotation;
                return IsFinite(localPosition) && IsFinite(localRotation) &&
                       Quaternion.Dot(localRotation, localRotation) > 0.0001f;
            }
            catch { return false; }
        }

        internal static bool ApplyHeadPose(Transform personRoot, string path,
                                            Vector3 localPosition,
                                            Quaternion localRotation)
        {
            if (personRoot == null || string.IsNullOrEmpty(path) ||
                !IsFinite(localPosition) || !IsFinite(localRotation) ||
                Quaternion.Dot(localRotation, localRotation) <= 0.0001f)
                return false;
            try
            {
                Transform head = FindRelativePath(personRoot, path, true);
                if (head == null || head == personRoot || !head.IsChildOf(personRoot))
                    return false;
                localRotation = localRotation.normalized;
                head.localPosition = localPosition;
                head.localRotation = localRotation;
                return true;
            }
            catch { return false; }
        }

        internal static bool TryGetHeadPose(Dictionary<string, BoneTransformData> pose,
                                             string path, out Vector3 localPosition,
                                             out Quaternion localRotation)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            if (pose == null || string.IsNullOrEmpty(path)) return false;
            BoneTransformData data;
            if (!pose.TryGetValue(HeadPosePathPrefix + path, out data)) return false;
            localPosition = new Vector3(data.PosX, data.PosY, data.PosZ);
            localRotation = new Quaternion(data.RotX, data.RotY, data.RotZ, data.RotW);
            return IsFinite(localPosition) && IsFinite(localRotation) &&
                   Quaternion.Dot(localRotation, localRotation) > 0.0001f;
        }

        private static int GetHeadBoneScore(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            string normalized = name.Replace(" ", string.Empty).Replace("_", string.Empty)
                .Replace("-", string.Empty).ToLowerInvariant();
            if (normalized == "head") return 100;
            if (normalized == "headbone" || normalized == "head01" ||
                normalized == "mixamorig:head") return 95;
            if (normalized.StartsWith("head", StringComparison.Ordinal)) return 90;
            if (normalized.EndsWith("head", StringComparison.Ordinal)) return 85;
            if (normalized == "neck" || normalized == "neck1" || normalized == "neck2") return 50;
            return -1;
        }

        private static int GetDepthFromRoot(Transform root, Transform target)
        {
            int depth = 0;
            Transform current = target;
            while (current != null && current != root)
            {
                depth++;
                current = current.parent;
            }
            return current == root ? depth : -1;
        }

        internal static Dictionary<string, BoneTransformData> Interpolate(
            Dictionary<string, BoneTransformData> previous,
            Dictionary<string, BoneTransformData> latest,
            float amount,
            Dictionary<string, BoneTransformData> destination)
        {
            if (latest == null || previous == null || ReferenceEquals(previous, latest))
                return latest;
            if (amount <= 0f) return previous;
            if (amount >= 1f) return latest;
            if (destination == null)
                destination = new Dictionary<string, BoneTransformData>(latest.Count);

            destination.Clear();
            foreach (KeyValuePair<string, BoneTransformData> pair in latest)
            {
                BoneTransformData oldValue;
                if (!previous.TryGetValue(pair.Key, out oldValue))
                {
                    destination[pair.Key] = pair.Value;
                    continue;
                }

                BoneTransformData newValue = pair.Value;
                Vector3 oldPosition = new Vector3(oldValue.PosX, oldValue.PosY, oldValue.PosZ);
                Vector3 newPosition = new Vector3(newValue.PosX, newValue.PosY, newValue.PosZ);
                Quaternion oldRotation = new Quaternion(
                    oldValue.RotX, oldValue.RotY, oldValue.RotZ, oldValue.RotW);
                Quaternion newRotation = new Quaternion(
                    newValue.RotX, newValue.RotY, newValue.RotZ, newValue.RotW);
                Vector3 position = Vector3.LerpUnclamped(oldPosition, newPosition, amount);
                Quaternion rotation = Quaternion.SlerpUnclamped(oldRotation, newRotation, amount);
                newValue.PosX = position.x;
                newValue.PosY = position.y;
                newValue.PosZ = position.z;
                newValue.RotX = rotation.x;
                newValue.RotY = rotation.y;
                newValue.RotZ = rotation.z;
                newValue.RotW = rotation.w;
                destination[pair.Key] = newValue;
            }

            return destination;
        }

        internal static string GetNetworkPath(Transform transform)
        {
            if (transform == null) return string.Empty;

            List<string> segments = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                string segment = current.name;
                if (current.parent != null)
                {
                    if (CountSameNameChildren(current.parent, current.name) > 1)
                        segment += "~" + current.GetSiblingIndex();
                }
                else
                {
                    Scene scene = current.gameObject.scene;
                    GameObject[] roots = scene.IsValid() ? scene.GetRootGameObjects() : null;
                    if (roots != null && CountSameNameRoots(roots, current.name) > 1)
                        segment += "~" + current.GetSiblingIndex();
                }

                segments.Add(segment);
                current = current.parent;
            }

            segments.Reverse();
            return string.Join("/", segments.ToArray());
        }

        internal static GameObject FindGameObjectByNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split('/');
            if (parts.Length == 0) return null;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return null;
            GameObject[] roots = scene.GetRootGameObjects();

            string rootName;
            int rootIndex;
            SplitSegment(parts[0], out rootName, out rootIndex);
            Transform current = null;
            if (rootIndex >= 0 && rootIndex < roots.Length && roots[rootIndex] != null &&
                roots[rootIndex].name == rootName)
                current = roots[rootIndex].transform;
            else
            {
                for (int i = 0; i < roots.Length; i++)
                {
                    if (roots[i] != null && roots[i].name == rootName)
                    {
                        current = roots[i].transform;
                        break;
                    }
                }
            }

            for (int i = 1; i < parts.Length && current != null; i++)
            {
                string childName;
                int childIndex;
                SplitSegment(parts[i], out childName, out childIndex);
                if (childIndex >= 0 && childIndex < current.childCount)
                {
                    Transform indexed = current.GetChild(childIndex);
                    if (indexed != null && indexed.name == childName)
                    {
                        current = indexed;
                        continue;
                    }
                }
                current = current.Find(childName);
            }

            return current != null ? current.gameObject : null;
        }

        private static int CountSameNameChildren(Transform parent, string name)
        {
            if (parent == null) return 0;
            int count = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child != null && child.name == name) count++;
            }
            return count;
        }

        private static int CountSameNameRoots(GameObject[] roots, string name)
        {
            if (roots == null) return 0;
            int count = 0;
            for (int i = 0; i < roots.Length; i++)
                if (roots[i] != null && roots[i].name == name) count++;
            return count;
        }

        private static void SplitSegment(string segment, out string name, out int index)
        {
            name = segment ?? string.Empty;
            index = -1;
            int marker = name.LastIndexOf('~');
            int parsed;
            if (marker <= 0 || !int.TryParse(name.Substring(marker + 1), out parsed)) return;
            index = parsed;
            name = name.Substring(0, marker);
        }

        internal static Transform FindPersonRoot(Transform root)
        {
            if (root == null) return null;

            List<Transform> armatures = new List<Transform>();
            CollectArmatures(root, armatures);
            for (int i = 0; i < armatures.Count; i++)
                if (armatures[i] != null && armatures[i].gameObject.activeInHierarchy &&
                    CountDescendants(armatures[i]) >= 32)
                    return root;

            // MitaPerson can outlive or reparent its visible model during
            // quest transitions. In that case the network path still resolves
            // to the logic object, while the rendered Mita (and its Armature)
            // now lives under a scene-level Act object. Bind to that visual
            // root so snapshots continue sampling/applying the character.
            Transform externalVisual = FindExternalMitaVisualRoot(root);
            return externalVisual != null ? externalVisual : root;
        }

        private static Transform FindExternalMitaVisualRoot(Transform sourceRoot)
        {
            if (sourceRoot == null) return null;
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return null;

            Transform best = null;
            float bestDistance = float.MaxValue;
            int bestModelNameScore = -1;
            List<Transform> stack = new List<Transform>(256);
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = roots.Length - 1; i >= 0; i--)
                if (roots[i] != null) stack.Add(roots[i].transform);

            while (stack.Count > 0)
            {
                int last = stack.Count - 1;
                Transform current = stack[last];
                stack.RemoveAt(last);
                if (current == null || !current.gameObject.activeInHierarchy)
                    continue;

                if (current != sourceRoot && !current.IsChildOf(sourceRoot) &&
                    current.name.IndexOf("Mita", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    List<Transform> candidates = new List<Transform>();
                    CollectArmatures(current, candidates);
                    int activeArmatures = 0;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        Transform armature = candidates[i];
                        if (armature == null || !armature.gameObject.activeInHierarchy ||
                            CountDescendants(armature) < 32)
                            continue;
                        activeArmatures++;
                    }

                    if (activeArmatures > 0 &&
                        current.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                    {
                        float distance = Vector3.Distance(sourceRoot.position, current.position);
                        int modelNameScore = current.name.IndexOf(
                            "MitaPerson", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
                        if (best == null || modelNameScore > bestModelNameScore ||
                            (modelNameScore == bestModelNameScore && distance < bestDistance))
                        {
                            best = current;
                            bestDistance = distance;
                            bestModelNameScore = modelNameScore;
                        }
                    }
                }

                for (int i = current.childCount - 1; i >= 0; i--)
                {
                    Transform child = current.GetChild(i);
                    if (child != null) stack.Add(child);
                }
            }

            return best;
        }

        internal static int CountMatchingBones(Transform personRoot)
        {
            if (personRoot == null) return 0;
            int count = 0;
            for (int i = 0; i < BoneSyncData.Count; i++)
            {
                string path = BoneSyncData.BonePaths[i];
                if (!string.IsNullOrEmpty(path) && personRoot.Find(path) != null)
                    count++;
            }
            return count;
        }

        internal static int CountSyncTransforms(Transform personRoot)
        {
            if (personRoot == null) return 0;
            Transform rigRoot = GetSkeletonRoot(personRoot);
            return CountDescendants(rigRoot);
        }

        internal static string DescribeRig(Transform personRoot)
        {
            if (personRoot == null) return "personRoot=<null>";

            try
            {
                List<Transform> candidates = new List<Transform>();
                CollectArmatures(personRoot, candidates);
                StringBuilder description = new StringBuilder();
                description.Append("personRoot='").Append(personRoot.name)
                    .Append("' active=").Append(personRoot.gameObject.activeInHierarchy)
                    .Append("' armatures=").Append(candidates.Count);
                int shown = 0;
                for (int i = 0; i < candidates.Count && shown < 5; i++)
                {
                    Transform candidate = candidates[i];
                    if (candidate == null) continue;
                    description.Append(" [").Append(GetNetworkPath(candidate))
                        .Append(" active=").Append(candidate.gameObject.activeInHierarchy)
                        .Append(" descendants=").Append(CountDescendants(candidate)).Append("]");
                    shown++;
                }
                return description.ToString();
            }
            catch (Exception ex)
            {
                return "personRoot='" + personRoot.name + "' rig-description-failed=" +
                       ex.GetType().Name + ": " + ex.Message;
            }
        }

        internal static string DescribeApplyCandidates(
            Transform personRoot, Dictionary<string, BoneTransformData> boneData)
        {
            if (personRoot == null) return "personRoot=<null>";
            if (boneData == null) return "boneData=<null>";

            try
            {
                List<Transform> candidates = new List<Transform>();
                CollectArmatures(personRoot, candidates);
                StringBuilder description = new StringBuilder();
                description.Append("root='").Append(personRoot.name).Append("' candidates=");
                if (candidates.Count == 0) description.Append("<none>");
                for (int i = 0; i < candidates.Count && i < 5; i++)
                {
                    Transform candidate = candidates[i];
                    if (candidate == null) continue;
                    description.Append(i == 0 ? "" : "; ")
                        .Append(GetNetworkPath(candidate))
                        .Append(" active=").Append(candidate.gameObject.activeInHierarchy)
                        .Append(" exact=").Append(CountMatchingPaths(candidate, boneData, false))
                        .Append(" relaxed=").Append(CountMatchingPaths(candidate, boneData, true))
                        .Append(" descendants=").Append(CountDescendants(candidate));
                }

                Transform best = FindBestSkeletonRoot(personRoot, boneData);
                description.Append(" selected='").Append(best != null ? GetNetworkPath(best) : "<none>").Append("'");
                if (best != null)
                {
                    description.Append(" unmapped=");
                    int shown = 0;
                    foreach (string path in boneData.Keys)
                    {
                        if (string.IsNullOrEmpty(path) || path == RigRootPosePath ||
                            path == RigWorldPosePath ||
                            IsModelPosePath(path) || IsHeadPosePath(path)) continue;
                        if (FindRelativePath(best, path, true) != null) continue;
                        if (shown > 0) description.Append(",");
                        description.Append(path);
                        shown++;
                        if (shown >= 6) break;
                    }
                    if (shown == 0) description.Append("<none>");
                }
                return description.ToString();
            }
            catch (Exception ex)
            {
                return "apply-diagnostics-failed=" + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static int CountDescendants(Transform root)
        {
            if (root == null) return 0;
            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null) continue;
                count++;
                count += CountDescendants(child);
            }
            return count;
        }

        private static Transform GetSkeletonRoot(Transform personRoot)
        {
            if (personRoot == null) return null;

            Transform bestArmature = null;
            int bestCount = -1;
            bool bestArmatureActive = false;
            FindArmatures(personRoot, ref bestArmature, ref bestCount, ref bestArmatureActive);
            return bestArmature != null ? bestArmature : personRoot;
        }

        private static void FindArmatures(Transform current,
                                         ref Transform bestArmature, ref int bestCount,
                                         ref bool bestArmatureActive)
        {
            if (current == null) return;
            if (current.name == "Armature")
            {
                bool candidateActive = current.gameObject.activeInHierarchy;
                int count = CountDescendants(current);
                if (bestArmature == null ||
                    (candidateActive && !bestArmatureActive) ||
                    (candidateActive == bestArmatureActive && count > bestCount))
                {
                    bestArmature = current;
                    bestCount = count;
                    bestArmatureActive = candidateActive;
                }
            }

            for (int i = 0; i < current.childCount; i++)
                FindArmatures(current.GetChild(i), ref bestArmature, ref bestCount,
                              ref bestArmatureActive);
        }

        internal static Dictionary<string, BoneTransformData> Sample(Transform personRoot)
        {
            if (personRoot == null) return null;
            try
            {
                PoseBinding binding = GetSampleBinding(personRoot);
                if (binding == null || binding.Paths == null || binding.Targets == null)
                    return null;

                Dictionary<string, BoneTransformData> result =
                    new Dictionary<string, BoneTransformData>(binding.Paths.Length);
                int missingTargets = 0;
                for (int i = 0; i < binding.Paths.Length; i++)
                {
                    Transform child = binding.Targets[i];
                    if (child == null)
                    {
                        missingTargets++;
                        continue;
                    }

                    bool worldPose = binding.Paths[i] == RigWorldPosePath;
                    Vector3 localPosition = worldPose ? child.position : child.localPosition;
                    Quaternion localRotation = worldPose ? child.rotation : child.localRotation;
                    if (!IsFinite(localPosition) || !IsFinite(localRotation)) continue;
                    if (!worldPose && IsHeadPosePath(binding.Paths[i]))
                        localRotation = Quaternion.identity;
                    else if (!worldPose && IsInsideHeadChain(child, binding.RigRoot))
                        localRotation = Quaternion.identity;

                    BoneTransformData data = new BoneTransformData();
                    data.PosX = localPosition.x;
                    data.PosY = localPosition.y;
                    data.PosZ = localPosition.z;
                    data.RotX = localRotation.x;
                    data.RotY = localRotation.y;
                    data.RotZ = localRotation.z;
                    data.RotW = localRotation.w;
                    result[binding.Paths[i]] = data;
                }

                binding.MissingTargets = missingTargets;
                if (missingTargets > 0)
                {
                    PoseBinding refreshed = BuildSampleBinding(personRoot);
                    if (refreshed != null)
                    {
                        SampleBindings[personRoot.GetInstanceID()] = refreshed;
                        if (refreshed.Paths != null && refreshed.Paths.Length > 0 &&
                            refreshed.MissingTargets == 0)
                            return SampleBinding(refreshed);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= _nextSampleFailureLogTime)
                {
                    _nextSampleFailureLogTime = Time.unscaledTime + 5f;
                    DiagnosticLog.Warning("MitaBoneSync.Sample failed for '" + personRoot.name +
                                          "': " + ex.GetType().Name + ": " + ex.Message + ". " +
                                          DescribeRig(personRoot));
                }
                return null;
            }
        }

        private static Dictionary<string, BoneTransformData> SampleBinding(PoseBinding binding)
        {
            Dictionary<string, BoneTransformData> result =
                new Dictionary<string, BoneTransformData>(binding.Paths.Length);
            int missingTargets = 0;
            for (int i = 0; i < binding.Paths.Length; i++)
            {
                Transform target = binding.Targets[i];
                if (target == null)
                {
                    missingTargets++;
                    continue;
                }

                bool worldPose = binding.Paths[i] == RigWorldPosePath;
                Vector3 localPosition = worldPose ? target.position : target.localPosition;
                Quaternion localRotation = worldPose ? target.rotation : target.localRotation;
                if (!IsFinite(localPosition) || !IsFinite(localRotation)) continue;
                if (!worldPose && IsHeadPosePath(binding.Paths[i]))
                    localRotation = Quaternion.identity;
                else if (!worldPose && IsInsideHeadChain(target, binding.RigRoot))
                    localRotation = Quaternion.identity;

                BoneTransformData data = new BoneTransformData();
                data.PosX = localPosition.x;
                data.PosY = localPosition.y;
                data.PosZ = localPosition.z;
                data.RotX = localRotation.x;
                data.RotY = localRotation.y;
                data.RotZ = localRotation.z;
                data.RotW = localRotation.w;
                result[binding.Paths[i]] = data;
            }

            binding.MissingTargets = missingTargets;
            return result;
        }

        private static PoseBinding GetSampleBinding(Transform personRoot)
        {
            int id = personRoot.GetInstanceID();
            PoseBinding binding;
            bool rebuild = !SampleBindings.TryGetValue(id, out binding) || binding == null ||
                           binding.PersonRoot != personRoot || binding.RigRoot == null ||
                           binding.Paths == null || binding.Paths.Length == 0;
            if (!rebuild && Time.unscaledTime >= binding.NextValidationTime)
            {
                binding.NextValidationTime = Time.unscaledTime + BindingValidationInterval;
                if (!IsSameOrChild(binding.RigRoot, personRoot) ||
                    !binding.RigRoot.gameObject.activeInHierarchy ||
                    ComputeHierarchyFingerprint(personRoot) != binding.HierarchyFingerprint ||
                    binding.MissingTargets > 0)
                    rebuild = true;

                if (!rebuild)
                {
                    for (int i = 0; i < binding.Targets.Length; i++)
                    {
                        Transform target = binding.Targets[i];
                        if (!IsValidPoseTarget(binding, binding.Paths[i], target))
                        {
                            rebuild = true;
                            break;
                        }
                    }
                }
            }

            if (rebuild)
            {
                binding = BuildSampleBinding(personRoot);
                if (binding != null) SampleBindings[id] = binding;
                PruneBindings(SampleBindings);
            }

            return binding;
        }

        private static PoseBinding BuildSampleBinding(Transform personRoot)
        {
            if (personRoot == null) return null;
            Transform rigRoot = GetSkeletonRoot(personRoot);
            if (rigRoot == null) return null;

            int descendantCount = CountDescendants(rigRoot);
            int wrapperCount = CountModelWrappers(personRoot, rigRoot);
            List<string> paths = new List<string>(descendantCount + wrapperCount + 1);
            List<Transform> targets = new List<Transform>(descendantCount + wrapperCount + 1);
            if (descendantCount > 0)
            {
                // The game moves some Mita models on a visual child between
                // the MitaPerson object and Armature. Include that chain so
                // root motion is not lost when the outer object stays at 0,0,0.
                CollectModelWrappers(personRoot, rigRoot, paths, targets);

                // The Armature transform itself can carry root offset/rotation
                // during quest animations, so ship it with the child bones.
                paths.Add(RigRootPosePath);
                targets.Add(rigRoot);
                // Sync the world pose too. A model may be reparented beneath
                // a scene Act on one peer, so matching only local transforms
                // would leave it at the wrong world location.
                paths.Add(RigWorldPosePath);
                targets.Add(rigRoot);
                CollectSampleChildren(rigRoot, string.Empty, paths, targets);

            }

            PoseBinding binding = new PoseBinding();
            binding.PersonRoot = personRoot;
            binding.RigRoot = rigRoot;
            binding.Paths = paths.ToArray();
            binding.Targets = targets.ToArray();
            binding.HierarchyFingerprint = ComputeHierarchyFingerprint(personRoot);
            binding.NextValidationTime = Time.unscaledTime + BindingValidationInterval;
            return binding;
        }

        private static int CountModelWrappers(Transform personRoot, Transform rigRoot)
        {
            if (personRoot == null || rigRoot == null || rigRoot == personRoot) return 0;
            int count = 0;
            Transform current = rigRoot.parent;
            while (current != null && current != personRoot)
            {
                count++;
                current = current.parent;
            }
            return current == personRoot ? count : 0;
        }

        private static void CollectModelWrappers(Transform personRoot, Transform rigRoot,
                                                  List<string> paths, List<Transform> targets)
        {
            if (personRoot == null || rigRoot == null || paths == null || targets == null) return;
            List<Transform> wrappers = new List<Transform>();
            Transform current = rigRoot.parent;
            while (current != null && current != personRoot)
            {
                wrappers.Add(current);
                current = current.parent;
            }
            if (current != personRoot) return;

            wrappers.Reverse();
            for (int i = 0; i < wrappers.Count; i++)
            {
                string relativePath = BuildRelativePath(personRoot, wrappers[i]);
                if (string.IsNullOrEmpty(relativePath)) continue;
                paths.Add(ModelPosePathPrefix + relativePath);
                targets.Add(wrappers[i]);
            }
        }

        private static string BuildRelativePath(Transform root, Transform target)
        {
            if (root == null || target == null || target == root || !target.IsChildOf(root))
                return string.Empty;

            List<string> segments = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                string segment = current.name;
                if (current.parent != null && CountSameNameChildren(current.parent, current.name) > 1)
                    segment += "~" + current.GetSiblingIndex();
                segments.Add(segment);
                current = current.parent;
            }

            if (current != root) return string.Empty;
            segments.Reverse();
            return string.Join("/", segments.ToArray());
        }

        private static bool IsModelPosePath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith(ModelPosePathPrefix, StringComparison.Ordinal);
        }

        private static bool IsHeadPosePath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith(HeadPosePathPrefix, StringComparison.Ordinal);
        }

        private static bool IsExplicitHeadBoneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string normalized = name.Replace(" ", string.Empty).Replace("_", string.Empty)
                .ToLowerInvariant();
            if (normalized.StartsWith("head", StringComparison.Ordinal) ||
                normalized.StartsWith("neck", StringComparison.Ordinal) ||
                normalized.StartsWith("jaw", StringComparison.Ordinal) ||
                normalized.StartsWith("eye", StringComparison.Ordinal))
                return true;
            for (int i = 0; i < ExplicitHeadBoneNames.Length; i++)
                if (normalized == ExplicitHeadBoneNames[i]) return true;
            return false;
        }

        private static bool IsInsideHeadChain(Transform target, Transform rigRoot)
        {
            for (Transform current = target; current != null && current != rigRoot;
                 current = current.parent)
                if (IsExplicitHeadBoneName(current.name)) return true;
            return false;
        }

        private static void CollectExplicitHeadTargets(
            Transform root, Transform current, bool insideHeadChain,
            List<string> paths, List<Transform> targets)
        {
            if (root == null || current == null || paths == null || targets == null) return;
            bool headChain = insideHeadChain || IsExplicitHeadBoneName(current.name);
            if (current != root && headChain)
            {
                string relativePath = BuildRelativePath(root, current);
                if (!string.IsNullOrEmpty(relativePath))
                {
                    paths.Add(HeadPosePathPrefix + relativePath);
                    targets.Add(current);
                }
            }

            for (int i = 0; i < current.childCount; i++)
                CollectExplicitHeadTargets(root, current.GetChild(i), headChain, paths, targets);
        }

        private static bool IsValidPoseTarget(PoseBinding binding, string path, Transform target)
        {
            if (binding == null || target == null) return false;
            if (path == RigRootPosePath || path == RigWorldPosePath)
                return target == binding.RigRoot;
            if (IsModelPosePath(path))
                return IsSameOrChild(target, binding.PersonRoot) &&
                       (target == binding.PersonRoot || binding.RigRoot.IsChildOf(target));
            if (IsHeadPosePath(path))
                return IsSameOrChild(target, binding.PersonRoot);
            return target == binding.RigRoot || target.IsChildOf(binding.RigRoot);
        }

        private static void CollectSampleChildren(Transform parent, string parentPath,
                                                  List<string> paths, List<Transform> targets)
        {
            if (parent == null) return;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child == null) continue;

                string segment = child.name;
                if (CountSameNameChildren(parent, child.name) > 1)
                    segment += "~" + child.GetSiblingIndex();
                string path = string.IsNullOrEmpty(parentPath)
                    ? segment
                    : parentPath + "/" + segment;

                paths.Add(path);
                targets.Add(child);
                CollectSampleChildren(child, path, paths, targets);
            }
        }

        internal static int Apply(Transform personRoot,
                                  Dictionary<string, BoneTransformData> boneData)
        {
            if (personRoot == null || boneData == null) return 0;
            try
            {
                PoseBinding binding = GetApplyBinding(personRoot, boneData);
                if (binding == null || binding.Targets == null || binding.Values == null)
                    return 0;

                int availableTargets = 0;
                for (int i = 0; i < binding.Targets.Length; i++)
                    if (binding.Targets[i] != null) availableTargets++;

                int minimumMapped = Mathf.Max(1, Mathf.CeilToInt(binding.Targets.Length * 0.9f));
                if (availableTargets < minimumMapped)
                {
                    binding.MissingTargets = binding.Targets.Length - availableTargets;
                    return 0;
                }

                int applied = 0;
                int missingTargets = 0;
                for (int i = 0; i < binding.Targets.Length; i++)
                {
                    if (binding.Paths[i] == RigWorldPosePath ||
                        IsHeadPosePath(binding.Paths[i])) continue;
                    Transform bone = binding.Targets[i];
                    if (bone == null)
                    {
                        missingTargets++;
                        continue;
                    }
                    BoneTransformData data = binding.Values[i];

                    Vector3 localPosition = new Vector3(data.PosX, data.PosY, data.PosZ);
                    Quaternion localRotation = new Quaternion(data.RotX, data.RotY, data.RotZ, data.RotW);
                    if (!IsFinite(localPosition) || !IsFinite(localRotation)) continue;

                    Vector3 currentPosition = bone.localPosition;
                    Quaternion currentRotation = bone.localRotation;
                    if ((currentPosition - localPosition).sqrMagnitude > 0.00000001f)
                        bone.localPosition = localPosition;
                    if (!IsInsideHeadChain(bone, binding.RigRoot) &&
                        1f - Mathf.Abs(Quaternion.Dot(currentRotation, localRotation)) > 0.000001f)
                        bone.localRotation = localRotation;
                    applied++;
                }
                // Apply the world pose after local values so the armature
                // ends the frame at the host's visual root transform.
                for (int i = 0; i < binding.Targets.Length; i++)
                {
                    if (binding.Paths[i] != RigWorldPosePath) continue;
                    Transform rig = binding.Targets[i];
                    if (rig == null)
                    {
                        missingTargets++;
                        continue;
                    }
                    BoneTransformData data = binding.Values[i];
                    Vector3 worldPosition = new Vector3(data.PosX, data.PosY, data.PosZ);
                    Quaternion worldRotation = new Quaternion(data.RotX, data.RotY, data.RotZ, data.RotW);
                    if (!IsFinite(worldPosition) || !IsFinite(worldRotation)) continue;
                    rig.SetPositionAndRotation(worldPosition, worldRotation);
                    applied++;
                }
                binding.MissingTargets = missingTargets;
                return applied;
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= _nextApplyFailureLogTime)
                {
                    _nextApplyFailureLogTime = Time.unscaledTime + 5f;
                    DiagnosticLog.Warning("MitaBoneSync.Apply failed for '" + personRoot.name +
                                          "': " + ex.GetType().Name + ": " + ex.Message + ". " +
                                          DescribeRig(personRoot));
                }
                return 0;
            }
        }

        private static PoseBinding GetApplyBinding(
            Transform personRoot, Dictionary<string, BoneTransformData> boneData)
        {
            int id = personRoot.GetInstanceID();
            PoseBinding binding;
            bool rebuild = !ApplyBindings.TryGetValue(id, out binding) || binding == null ||
                           binding.PersonRoot != personRoot || binding.RigRoot == null ||
                           binding.Paths == null || binding.Paths.Length != boneData.Count;

            if (!rebuild && Time.unscaledTime >= binding.NextValidationTime)
            {
                binding.NextValidationTime = Time.unscaledTime + BindingValidationInterval;
                if (!IsSameOrChild(binding.RigRoot, personRoot) ||
                    !binding.RigRoot.gameObject.activeInHierarchy ||
                    ComputeHierarchyFingerprint(personRoot) != binding.HierarchyFingerprint ||
                    binding.MissingTargets > 0)
                    rebuild = true;
                for (int i = 0; i < binding.Targets.Length; i++)
                {
                    Transform target = binding.Targets[i];
                    if (!IsValidPoseTarget(binding, binding.Paths[i], target))
                    {
                        rebuild = true;
                        break;
                    }
                }
            }

            if (rebuild)
            {
                binding = BuildApplyBinding(personRoot, boneData);
                if (binding == null) return null;
                ApplyBindings[id] = binding;
                PruneBindings(ApplyBindings);
            }

            if (!ReferenceEquals(binding.Source, boneData))
            {
                if (!ContainsAllPaths(binding.Paths, boneData))
                {
                    binding = BuildApplyBinding(personRoot, boneData);
                    if (binding == null) return null;
                    ApplyBindings[id] = binding;
                }
            }

            // Interpolated snapshots are updated in place every LateUpdate.
            // Refresh these cached values even when the source dictionary
            // reference is unchanged, or the rig would keep applying the
            // first interpolated frame and still look stepped.
            for (int i = 0; i < binding.Paths.Length; i++)
            {
                BoneTransformData value;
                binding.Values[i] = boneData.TryGetValue(binding.Paths[i], out value)
                    ? value
                    : default(BoneTransformData);
            }
            binding.Source = boneData;

            return binding;
        }

        private static PoseBinding BuildApplyBinding(
            Transform personRoot, Dictionary<string, BoneTransformData> boneData)
        {
            if (personRoot == null || boneData == null) return null;
            Transform rigRoot = FindBestSkeletonRoot(personRoot, boneData);
            if (rigRoot == null) return null;

            string[] paths = new string[boneData.Count];
            Transform[] targets = new Transform[boneData.Count];
            BoneTransformData[] values = new BoneTransformData[boneData.Count];
            int index = 0;
            int missingTargets = 0;
            foreach (KeyValuePair<string, BoneTransformData> pair in boneData)
            {
                paths[index] = pair.Key;
                Transform target = string.IsNullOrEmpty(pair.Key)
                    ? null
                    : pair.Key == RigRootPosePath || pair.Key == RigWorldPosePath
                        ? rigRoot
                        : IsModelPosePath(pair.Key)
                            ? FindRelativePath(personRoot, pair.Key.Substring(ModelPosePathPrefix.Length))
                            : IsHeadPosePath(pair.Key)
                                ? FindRelativePath(personRoot, pair.Key.Substring(HeadPosePathPrefix.Length))
                                : FindRelativePath(rigRoot, pair.Key);
                if (target == null && IsModelPosePath(pair.Key))
                {
                    string relativeModelPath = pair.Key.Substring(ModelPosePathPrefix.Length);
                    int slash = relativeModelPath.LastIndexOf('/');
                    string modelName = slash >= 0
                        ? relativeModelPath.Substring(slash + 1)
                        : relativeModelPath;
                    string parsedName;
                    int ignoredIndex;
                    SplitSegment(modelName, out parsedName, out ignoredIndex);
                    if (personRoot.name == parsedName) target = personRoot;
                }
                if (target == null && !string.IsNullOrEmpty(pair.Key) &&
                    pair.Key != RigRootPosePath && pair.Key != RigWorldPosePath &&
                    !IsModelPosePath(pair.Key) && !IsHeadPosePath(pair.Key))
                    target = FindRelativePath(rigRoot, pair.Key, true);
                targets[index] = target;
                if (target == null) missingTargets++;
                values[index] = pair.Value;
                index++;
            }

            PoseBinding binding = new PoseBinding();
            binding.PersonRoot = personRoot;
            binding.RigRoot = rigRoot;
            binding.Paths = paths;
            binding.Targets = targets;
            binding.Values = values;
            binding.Source = boneData;
            binding.MissingTargets = missingTargets;
            binding.HierarchyFingerprint = ComputeHierarchyFingerprint(personRoot);
            binding.NextValidationTime = Time.unscaledTime + BindingValidationInterval;
            return binding;
        }

        private static bool ContainsAllPaths(string[] paths,
                                             Dictionary<string, BoneTransformData> boneData)
        {
            if (paths == null || boneData == null || paths.Length != boneData.Count)
                return false;
            for (int i = 0; i < paths.Length; i++)
                if (string.IsNullOrEmpty(paths[i]) || !boneData.ContainsKey(paths[i]))
                    return false;
            return true;
        }

        private static void PruneBindings(Dictionary<int, PoseBinding> bindings)
        {
            if (bindings.Count <= 32) return;
            List<int> dead = null;
            foreach (KeyValuePair<int, PoseBinding> pair in bindings)
            {
                if (pair.Value != null && pair.Value.PersonRoot != null) continue;
                if (dead == null) dead = new List<int>();
                dead.Add(pair.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) bindings.Remove(dead[i]);
        }

        private static Transform FindBestSkeletonRoot(
            Transform personRoot, Dictionary<string, BoneTransformData> boneData)
        {
            if (personRoot == null || boneData == null) return null;
            List<Transform> candidates = new List<Transform>();
            CollectArmatures(personRoot, candidates);

            bool hasRigRootPose = boneData.ContainsKey(RigRootPosePath);
            if (!hasRigRootPose)
                candidates.Insert(0, personRoot);
            if (candidates.Count == 0) return null;

            Transform bestRoot = null;
            int bestExactMatches = 0;
            int bestRelaxedMatches = 0;
            int bestDescendantCount = -1;

            for (int i = 0; i < candidates.Count; i++)
            {
                Transform candidate = candidates[i];
                if (candidate == null) continue;
                if (candidate != personRoot && !candidate.gameObject.activeInHierarchy) continue;

                int exactMatches = CountMatchingPaths(candidate, boneData, false);
                int pathCount = CountNonRigRootPaths(boneData);
                if (pathCount > 0 && exactMatches == pathCount)
                    return candidate;

                int relaxedMatches = CountMatchingPaths(candidate, boneData, true);
                int descendantCount = CountDescendants(candidate);

                if (pathCount > 0 && exactMatches == 0 && relaxedMatches == 0)
                    continue;

                if (bestRoot == null || exactMatches > bestExactMatches ||
                    (exactMatches == bestExactMatches && relaxedMatches > bestRelaxedMatches) ||
                    (exactMatches == bestExactMatches && relaxedMatches == bestRelaxedMatches &&
                     descendantCount > bestDescendantCount))
                {
                    bestRoot = candidate;
                    bestExactMatches = exactMatches;
                    bestRelaxedMatches = relaxedMatches;
                    bestDescendantCount = descendantCount;
                }
            }

            return bestRoot;
        }

        private static int ComputeHierarchyFingerprint(Transform root)
        {
            if (root == null) return 0;

            unchecked
            {
                int hash = 17;
                Stack<Transform> stack = new Stack<Transform>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    Transform current = stack.Pop();
                    if (current == null)
                    {
                        hash = (hash * 31) + 1;
                        continue;
                    }

                    string name = current.name ?? string.Empty;
                    for (int i = 0; i < name.Length; i++)
                        hash = (hash * 31) + name[i];
                    hash = (hash * 31) + current.GetInstanceID();
                    hash = (hash * 31) + current.childCount;
                    hash = (hash * 31) + (current.gameObject.activeInHierarchy ? 1 : 0);
                    for (int i = current.childCount - 1; i >= 0; i--)
                        stack.Push(current.GetChild(i));
                }
                return hash;
            }
        }

        private static void CollectArmatures(Transform current, List<Transform> candidates)
        {
            if (current == null || candidates == null) return;
            if (current.name == "Armature")
                candidates.Add(current);

            for (int i = 0; i < current.childCount; i++)
                CollectArmatures(current.GetChild(i), candidates);
        }

        private static int CountMatchingPaths(Transform root,
                                              Dictionary<string, BoneTransformData> boneData,
                                              bool allowSiblingFallback)
        {
            if (root == null || boneData == null) return 0;
            int count = 0;
            foreach (KeyValuePair<string, BoneTransformData> pair in boneData)
            {
                if (!string.IsNullOrEmpty(pair.Key) && pair.Key != RigRootPosePath &&
                    pair.Key != RigWorldPosePath &&
                    !IsModelPosePath(pair.Key) && !IsHeadPosePath(pair.Key) &&
                    FindRelativePath(root, pair.Key, allowSiblingFallback) != null)
                    count++;
            }
            return count;
        }

        private static int CountNonRigRootPaths(Dictionary<string, BoneTransformData> boneData)
        {
            if (boneData == null) return 0;
            int count = 0;
            foreach (string path in boneData.Keys)
                if (!string.IsNullOrEmpty(path) && path != RigRootPosePath &&
                    path != RigWorldPosePath &&
                    !IsModelPosePath(path) && !IsHeadPosePath(path)) count++;
            return count;
        }

        private static bool IsSameOrChild(Transform candidate, Transform parent)
        {
            return candidate != null && parent != null &&
                   (candidate == parent || candidate.IsChildOf(parent));
        }

        private static Transform FindRelativePath(Transform root, string path)
        {
            return FindRelativePath(root, path, false);
        }

        private static Transform FindRelativePath(Transform root, string path,
                                                  bool allowSiblingFallback)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split('/');
            Transform current = root;
            for (int i = 0; i < parts.Length && current != null; i++)
            {
                string childName;
                int childIndex;
                SplitSegment(parts[i], out childName, out childIndex);
                Transform next = null;
                if (childIndex >= 0)
                {
                    if (childIndex < current.childCount)
                    {
                        Transform indexed = current.GetChild(childIndex);
                        if (indexed != null && indexed.name == childName)
                            next = indexed;
                    }

                    if (next == null && allowSiblingFallback)
                    {
                        for (int child = 0; child < current.childCount; child++)
                        {
                            Transform candidate = current.GetChild(child);
                            if (candidate != null && candidate.name == childName)
                            {
                                next = candidate;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    for (int child = 0; child < current.childCount; child++)
                    {
                        Transform candidate = current.GetChild(child);
                        if (candidate != null && candidate.name == childName)
                        {
                            next = candidate;
                            break;
                        }
                    }
                }
                current = next;
            }
            return current;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) &&
                   IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
