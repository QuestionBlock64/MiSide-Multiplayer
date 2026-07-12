using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class MitaController
    {
        private string localPlayerId;

        private Transform mitaRoot;
        private Transform mitaPerson;
        private Transform mitaHeadBone;
        private Animator  mitaAnimator;
        private float     nextFindTime;
        private string    lastSceneName;
        private const float FindInterval = 1f;

        private Vector3    targetPosition;
        private Quaternion targetRotation = Quaternion.identity;
        private Vector3    smoothVelocity;
        private bool       hasState;
        private const float SmoothTime    = 0.05f;
        private const float RotationSpeed = 20f;
        private const float TeleportDist  = 2f;

        private const float HeadLookSpeed = 8f;

        public void Configure(string playerId)
        {
            localPlayerId = playerId;
        }

        public void OnRemoteMitaStateReceived(MitaState state)
        {
            if (state == null || string.IsNullOrEmpty(state.senderId))
                return;

            if (state.senderId == localPlayerId)
                return;

            if (!string.IsNullOrEmpty(localPlayerId) &&
                string.Compare(localPlayerId, state.senderId, StringComparison.Ordinal) < 0)
                return;

            if (!string.IsNullOrEmpty(state.sceneName) &&
                state.sceneName != SceneManager.GetActiveScene().name)
                return;

            if (mitaRoot == null)
                TryFindMita();

            if (mitaRoot == null)
                return;

            targetPosition = state.position.ToUnity();
            targetRotation = state.rotation.ToUnity();

            if (!hasState)
            {
                mitaRoot.position = targetPosition;
                mitaRoot.rotation = targetRotation;
                smoothVelocity = Vector3.zero;
                hasState = true;

                if (mitaAnimator != null)
                    mitaAnimator.speed = 0f;

                DiagnosticLog.Info(
                    "MitaController: authority is '" + state.senderId + "'.");
            }

            if (state.Bones != null && state.Bones.Count > 0 && mitaPerson != null)
                ApplyBones(state.Bones);
        }

        public void Tick()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != lastSceneName)
            {
                lastSceneName = sceneName;
                mitaRoot      = null;
                mitaPerson    = null;
                mitaHeadBone  = null;
                mitaAnimator  = null;
                hasState      = false;
                nextFindTime  = 0f;
            }

            if (mitaRoot == null && Time.unscaledTime >= nextFindTime)
            {
                nextFindTime = Time.unscaledTime + FindInterval;
                TryFindMita();
            }

            if (mitaRoot != null && mitaHeadBone == null)
                mitaHeadBone = FindHeadBone(mitaPerson);

            if (mitaRoot == null || !hasState)
                return;

            float dist = Vector3.Distance(mitaRoot.position, targetPosition);
            if (dist > TeleportDist)
            {
                mitaRoot.position = targetPosition;
                smoothVelocity    = Vector3.zero;
            }
            else
            {
                mitaRoot.position = Vector3.SmoothDamp(
                    mitaRoot.position, targetPosition, ref smoothVelocity, SmoothTime);
            }

            float rotT = 1f - Mathf.Exp(-RotationSpeed * Time.deltaTime);
            mitaRoot.rotation = Quaternion.Slerp(mitaRoot.rotation, targetRotation, rotT);

            UpdateHeadLook();
        }

        private void UpdateHeadLook()
        {
            if (mitaHeadBone == null) return;

            Transform lookTarget = GetNearestPlayer();
            if (lookTarget == null) return;

            Vector3 direction = lookTarget.position - mitaHeadBone.position;
            if (direction.sqrMagnitude < 0.01f) return;

            Quaternion targetLook = Quaternion.LookRotation(direction, Vector3.up);
            Quaternion localTarget = Quaternion.Inverse(mitaRoot.rotation) * targetLook;

            float t = 1f - Mathf.Exp(-HeadLookSpeed * Time.deltaTime);
            mitaHeadBone.rotation = Quaternion.Slerp(mitaHeadBone.rotation, mitaRoot.rotation * localTarget, t);
        }

        private Transform GetNearestPlayer()
        {
            Transform nearest = null;
            float nearestDist = float.MaxValue;

            // Check local player
            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (localPlayer != null)
            {
                float d = Vector3.Distance(mitaRoot.position, localPlayer.position);
                if (d < nearestDist)
                {
                    nearestDist = d;
                    nearest = localPlayer;
                }
            }

            // Check remote puppets
            foreach (PuppetController puppet in PuppetRegistry.AllPuppets)
            {
                if (puppet != null && puppet.GameObject != null)
                {
                    float d = Vector3.Distance(mitaRoot.position, puppet.GameObject.transform.position);
                    if (d < nearestDist)
                    {
                        nearestDist = d;
                        nearest = puppet.GameObject.transform;
                    }
                }
            }

            return nearest;
        }

        private void TryFindMita()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return;

            lastSceneName = scene.name;

            GameObject worldGO = GameObject.Find("World");
            if (worldGO != null)
            {
                Transform quests = worldGO.transform.Find("Quests");
                if (quests != null)
                {
                    Transform mita = FindByNameRecursive(quests, "Mita");
                    if (mita != null && mita.gameObject.activeSelf)
                    {
                        BindMita(mita.gameObject);
                        return;
                    }
                }
            }

            GameObject direct = GameObject.Find("Mita");
            if (direct != null && direct.activeSelf)
            {
                BindMita(direct);
                return;
            }
        }

        private void BindMita(GameObject mita)
        {
            mitaRoot     = mita.transform;
            mitaPerson   = FindPersonInChildren(mita.transform);
            mitaHeadBone = FindHeadBone(mitaPerson);
            mitaAnimator = mita.GetComponentInChildren<Animator>(true);
            DiagnosticLog.Info(
                "MitaController: bound at '" + LocalPlayerLocator.GetPath(mita.transform) + "'" +
                (mitaPerson != null ? " (Person found)" : " (Person NOT found)") +
                (mitaHeadBone != null ? " (Head found)" : " (Head NOT found)"));
        }

        private static Transform FindHeadBone(Transform personRoot)
        {
            if (personRoot == null) return null;
            Transform armature = personRoot.Find("Armature");
            if (armature == null) return null;

            string[] paths =
            {
                "Hips/Spine/Chest/Neck2/Neck1/Head",
                "Hips/Spine/Chest/Neck1/Head",
                "Hips/Spine/Chest/Neck/Head",
            };

            for (int i = 0; i < paths.Length; i++)
            {
                Transform t = armature.Find(paths[i]);
                if (t != null) return t;
            }

            return FindByNameRecursive(armature, "Head");
        }

        private static Transform FindByNameRecursive(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform result = FindByNameRecursive(root.GetChild(i), name);
                if (result != null) return result;
            }
            return null;
        }

        private static Transform FindPersonInChildren(Transform root)
        {
            if (root == null) return null;
            if (root.name == "Person") return root;
            Transform person = root.Find("Person");
            if (person != null) return person;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform result = FindPersonInChildren(root.GetChild(i));
                if (result != null) return result;
            }
            return null;
        }

        private void ApplyBones(Dictionary<string, BoneTransformData> boneData)
        {
            if (mitaPerson == null || boneData == null) return;

            float originalSpeed = 1f;
            bool hasAnim = mitaAnimator != null;
            if (hasAnim)
            {
                originalSpeed = mitaAnimator.speed;
                mitaAnimator.speed = 0f;
            }

            for (int i = 0; i < BoneSyncData.Count; i++)
            {
                string path = BoneSyncData.BonePaths[i];
                if (string.IsNullOrEmpty(path)) continue;
                Transform bone = mitaPerson.Find(path);
                if (bone == null) continue;
                BoneTransformData data;
                if (!boneData.TryGetValue(path, out data)) continue;
                bone.localPosition = new Vector3(data.PosX, data.PosY, data.PosZ);
                bone.localRotation = new Quaternion(data.RotX, data.RotY, data.RotZ, data.RotW);
            }

            if (hasAnim)
                mitaAnimator.speed = originalSpeed;
        }
    }
}