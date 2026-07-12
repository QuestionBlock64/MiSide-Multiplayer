using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiSideMultiplayer
{
    public static class BoneSyncData
    {
        public static readonly string[] BonePaths =
        {
            "Armature/Hips",
            "Armature/Hips/Spine",
            "Armature/Hips/Spine/Chest/Right shoulder",
            "Armature/Hips/Spine/Chest/Right shoulder/Right arm",
            "Armature/Hips/Spine/Chest/Right shoulder/Right arm/Right elbow",
            "Armature/Hips/Spine/Chest/Right shoulder/Right arm/Right elbow/Right wrist",
            "Armature/Hips/Spine/Chest/Left shoulder",
            "Armature/Hips/Spine/Chest/Left shoulder/Left arm",
            "Armature/Hips/Spine/Chest/Left shoulder/Left arm/Left elbow",
            "Armature/Hips/Spine/Chest/Left shoulder/Left arm/Left elbow/Left wrist",
            "Armature/Hips/Right leg",
            "Armature/Hips/Right leg/Right knee",
            "Armature/Hips/Right leg/Right knee/Right ankle",
            "Armature/Hips/Left leg",
            "Armature/Hips/Left leg/Left knee",
            "Armature/Hips/Left leg/Left knee/Left ankle",
        };
        public const int Count = 16;
    }

    [Serializable]
    public struct BoneTransformData
    {
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
    }

    [Serializable]
    public class BoneSyncMessage
    {
        public string senderId;
        public Dictionary<string, BoneTransformData> Bones;
    }

    internal sealed class BoneSync : IDisposable
    {
        private const float SendInterval = 0.066f;

        private RpcDispatcher _dispatcher;
        private string        _localPlayerId;
        private float         _timer;
        private bool          _configured;

        private static readonly Dictionary<string, Dictionary<string, BoneTransformData>> _pendingBones = new Dictionary<string, Dictionary<string, BoneTransformData>>();

        public void Configure(RpcDispatcher dispatcher, string localPlayerId)
        {
            _dispatcher = dispatcher;
            _localPlayerId = localPlayerId;
            _dispatcher.RemoteBoneSyncReceived += OnBoneDataReceived;
            _configured = true;
        }

        public void Tick()
        {
            if (!_configured || _dispatcher == null) return;
            Transform localPlayerRoot = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (localPlayerRoot == null) return;
            Transform personRoot = localPlayerRoot.Find("Person");
            if (personRoot == null) return;
            _timer += Time.unscaledDeltaTime;
            if (_timer < SendInterval) return;
            _timer = 0f;
            var boneData = SampleBones(personRoot);
            if (boneData == null || boneData.Count == 0) return;
            var payload = new BoneSyncMessage();
            payload.senderId = _localPlayerId;
            payload.Bones = boneData;
            _dispatcher.SendBoneSync(payload);
        }

        public void LateTick() { }

        public void Dispose()
        {
            if (_dispatcher != null) _dispatcher.RemoteBoneSyncReceived -= OnBoneDataReceived;
            _pendingBones.Clear();
            _configured = false;
        }

        /// <summary>
        /// Returns pending bone data WITHOUT removing it. Data is only replaced
        /// when a new packet arrives. This matches the MelonLoader behavior
        /// where _pendingBones[actor] persists until overwritten.
        /// </summary>
        public static bool TryGetPendingBones(string playerId, out Dictionary<string, BoneTransformData> boneData)
        {
            return _pendingBones.TryGetValue(playerId, out boneData) && boneData != null && boneData.Count > 0;
        }

        public static void ClearPending(string playerId)
        {
            _pendingBones.Remove(playerId);
        }

        private static Dictionary<string, BoneTransformData> SampleBones(Transform personRoot)
        {
            if (personRoot == null) return null;
            var result = new Dictionary<string, BoneTransformData>(BoneSyncData.Count);
            for (int i = 0; i < BoneSyncData.Count; i++)
            {
                string path = BoneSyncData.BonePaths[i];
                if (string.IsNullOrEmpty(path)) continue;
                Transform bone = personRoot.Find(path);
                if (bone == null) continue;
                BoneTransformData data = new BoneTransformData();
                data.PosX = bone.localPosition.x;
                data.PosY = bone.localPosition.y;
                data.PosZ = bone.localPosition.z;
                data.RotX = bone.localRotation.x;
                data.RotY = bone.localRotation.y;
                data.RotZ = bone.localRotation.z;
                data.RotW = bone.localRotation.w;
                result[path] = data;
            }
            return result;
        }

        private void OnBoneDataReceived(BoneSyncMessage message)
        {
            if (message == null || message.Bones == null || message.Bones.Count == 0) return;
            if (string.IsNullOrEmpty(message.senderId)) return;
            _pendingBones[message.senderId] = message.Bones;
        }
    }
}