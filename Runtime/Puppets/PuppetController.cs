using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiSideMultiplayer
{
    public sealed class PuppetController
    {
        private const float PositionSmoothTime = 0.075f;
        private const float RotationLerpSpeed  = 18f;
        private const float TeleportDistance   = 7f;

        private string     playerId;
        private string     displayName;
        private GameObject gameObjectRef;
        private Transform  transformRef;
        private Transform  visualRoot;
        private Animator   animator;

        private Transform  nameTagRoot;
        private TextMesh   nameTagMesh;

        private Vector3           targetPosition;
        private Quaternion        targetRotation  = Quaternion.identity;
        private Vector3           smoothVelocity;
        private RemotePlayerState latestState;
        private bool              hasSnapshot;

        private float nextCoordLogTime;
        private const float CoordLogInterval = 3f;

        public GameObject GameObject { get { return gameObjectRef; } }

        public PuppetController(GameObject root)
        {
            gameObjectRef = root;
            transformRef  = root.transform;
        }

        public void Bind(string remotePlayerId, string remoteDisplayName,
                         Transform clonedVisualRoot, bool enableFallbackMarker)
        {
            playerId    = remotePlayerId;
            displayName = remoteDisplayName;
            visualRoot  = clonedVisualRoot;

            animator = visualRoot != null
                ? visualRoot.GetComponentInChildren<Animator>(true)
                : null;

            if (animator != null)
            {
                animator.speed = 0f;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false;
            }

            EnsureNameTag();
        }

        public void SetVisible(bool visible)
        {
            if (visualRoot != null && visualRoot.gameObject.activeSelf != visible)
                visualRoot.gameObject.SetActive(visible);
            if (nameTagRoot != null && nameTagRoot.gameObject.activeSelf != visible)
                nameTagRoot.gameObject.SetActive(visible);
        }

        public void ApplySnapshot(RemotePlayerState state)
        {
            if (state == null) return;
            latestState      = state;
            targetPosition   = state.position.ToUnity();
            targetRotation   = Norm(state.rotation.ToUnity());

            if (!hasSnapshot)
            {
                transformRef.SetPositionAndRotation(targetPosition, targetRotation);
                smoothVelocity = Vector3.zero;
                hasSnapshot    = true;
            }
        }

        public void Tick()
        {
            if (!hasSnapshot) return;

            float dist = Vector3.Distance(transformRef.position, targetPosition);
            if (dist > TeleportDistance)
            {
                transformRef.position = targetPosition;
                smoothVelocity        = Vector3.zero;
            }
            else
            {
                transformRef.position = Vector3.SmoothDamp(
                    transformRef.position, targetPosition,
                    ref smoothVelocity, PositionSmoothTime);
            }

            float rotT = 1f - Mathf.Exp(-RotationLerpSpeed * Time.deltaTime);
            transformRef.rotation = Quaternion.Slerp(
                transformRef.rotation, targetRotation, rotT);

            UpdateNameTagFacing();

            if (Time.unscaledTime >= nextCoordLogTime)
            {
                nextCoordLogTime = Time.unscaledTime + CoordLogInterval;
                LogCoordinates();
            }
        }

        public void LateTick()
        {
            ApplyBoneSync();
            UpdateNameTagFacing();
        }

        private void ApplyBoneSync()
        {
            if (visualRoot == null || string.IsNullOrEmpty(playerId))
                return;

            Dictionary<string, BoneTransformData> boneData;
            if (!BoneSync.TryGetPendingBones(playerId, out boneData) || boneData == null)
                return;

            Transform personRoot = visualRoot.Find("Person");
            if (personRoot == null)
                return;

            if (animator != null)
                animator.speed = 0f;

            for (int i = 0; i < BoneSyncData.Count; i++)
            {
                string path = BoneSyncData.BonePaths[i];
                Transform bone = personRoot.Find(path);
                if (bone == null)
                    continue;

                BoneTransformData data;
                if (!boneData.TryGetValue(path, out data))
                    continue;

                bone.localPosition = new Vector3(data.PosX, data.PosY, data.PosZ);
                bone.localRotation = new Quaternion(data.RotX, data.RotY, data.RotZ, data.RotW);
            }
        }

        private void EnsureNameTag()
        {
            if (nameTagRoot != null) return;
            string label = string.IsNullOrEmpty(displayName) ? playerId : displayName;
            GameObject tag = new GameObject("NameTag_" + playerId);
            tag.transform.SetParent(transformRef, false);
            tag.transform.localPosition = new Vector3(0f, 2.95f, 0f);
            tag.transform.localScale    = Vector3.one;
            nameTagRoot = tag.transform;
            try
            {
                nameTagMesh               = tag.AddComponent<TextMesh>();
                nameTagMesh.text          = label;
                nameTagMesh.fontSize      = 56;
                nameTagMesh.characterSize = 0.048f;
                nameTagMesh.anchor        = TextAnchor.MiddleCenter;
                nameTagMesh.alignment     = TextAlignment.Center;
                nameTagMesh.color         = Color.white;
            }
            catch (Exception) { }
        }

        private void UpdateNameTagFacing()
        {
            if (nameTagRoot == null || Camera.main == null) return;
            nameTagRoot.rotation = Camera.main.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
        }

        private void LogCoordinates()
        {
            Vector3 p = transformRef.position;
            DiagnosticLog.Info(
                "Player2 [" + playerId + "]" +
                "  x=" + p.x.ToString("F3") +
                "  y=" + p.y.ToString("F3") +
                "  z=" + p.z.ToString("F3") +
                (latestState != null ? "  scene=" + latestState.sceneName : ""));
        }

        private static Quaternion Norm(Quaternion q)
        {
            if (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f)
                return Quaternion.identity;
            return q;
        }
    }
}