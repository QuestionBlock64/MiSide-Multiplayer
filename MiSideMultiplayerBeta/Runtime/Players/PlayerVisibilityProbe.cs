using System;
using System.Reflection;
using UnityEngine;

namespace MiSideMultiplayer
{
    public static class PlayerVisibilityProbe
    {
        private static readonly string[] VisibleMemberNames =
        {
            "isVisible",
            "IsVisible",
            "visible",
            "Visible",
            "showPlayer",
            "ShowPlayer",
            "showBody",
            "ShowBody",
            "bodyVisible",
            "BodyVisible",
            "playerVisible",
            "PlayerVisible",
            "renderBody",
            "RenderBody"
        };

        private static readonly string[] HiddenMemberNames =
        {
            "isHidden",
            "IsHidden",
            "hidden",
            "Hidden",
            "hidePlayer",
            "HidePlayer",
            "hideBody",
            "HideBody",
            "bodyHidden",
            "BodyHidden",
            "playerHidden",
            "PlayerHidden"
        };

        public static bool Evaluate(Transform playerRoot, string[] visualRootCandidates)
        {
            if (playerRoot == null || !playerRoot.gameObject.activeInHierarchy)
                return false;

            bool gameFlag;
            if (TryReadVisibilityFromBehaviours(playerRoot, out gameFlag))
                return gameFlag;

            Transform visualRoot = LocalPlayerLocator.FindVisualRoot(playerRoot, visualRootCandidates);
            if (visualRoot != null)
            {
                if (!visualRoot.gameObject.activeInHierarchy)
                    return false;

                if (visualRoot != playerRoot &&
                    TryReadVisibilityFromBehaviours(visualRoot, out gameFlag))
                {
                    return gameFlag;
                }

                if (HasEnabledRenderer(visualRoot))
                    return true;
            }

            return HasEnabledRenderer(playerRoot);
        }

        private static bool HasEnabledRenderer(Transform root)
        {
            if (root == null)
                return false;

            SkinnedMeshRenderer[] skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinnedRenderers[i];
                if (renderer == null ||
                    !renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy ||
                    renderer.sharedMesh == null)
                {
                    continue;
                }

                return true;
            }

            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < meshRenderers.Length; i++)
            {
                MeshRenderer renderer = meshRenderers[i];
                if (renderer == null ||
                    !renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                    return true;
            }

            return false;
        }

        private static bool TryReadVisibilityFromBehaviours(Transform root, out bool isVisible)
        {
            isVisible = true;

            if (root == null)
                return false;

            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            bool foundPositive = false;
            bool foundNegative = false;

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || !behaviour.enabled || !behaviour.gameObject.activeInHierarchy)
                    continue;

                bool value;
                if (TryReadBoolMember(behaviour, VisibleMemberNames, out value))
                {
                    foundPositive = true;
                    if (!value)
                    {
                        isVisible = false;
                        return true;
                    }

                    isVisible = true;
                }

                if (TryReadBoolMember(behaviour, HiddenMemberNames, out value))
                {
                    foundNegative = true;
                    if (value)
                    {
                        isVisible = false;
                        return true;
                    }

                    isVisible = true;
                }
            }

            return foundPositive || foundNegative;
        }

        private static bool TryReadBoolMember(MonoBehaviour behaviour, string[] memberNames, out bool value)
        {
            value = false;

            if (behaviour == null || memberNames == null)
                return false;

            Type type = behaviour.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            for (int i = 0; i < memberNames.Length; i++)
            {
                string memberName = memberNames[i];

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

            return false;
        }
    }
}
