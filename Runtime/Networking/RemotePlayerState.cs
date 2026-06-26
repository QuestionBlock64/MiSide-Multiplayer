using System;
using UnityEngine;

namespace MiSideMultiplayer
{
    [Serializable]
    public sealed class RemotePlayerState
    {
        public string playerId;
        public string displayName;
        public string sceneName;
        public NetVector3 position;
        public NetQuaternion rotation;
        public NetVector3 velocity;
        public float speed;
        public bool isGrounded;
        public bool isVisible = true;
        public string action;
        public float[] blendWeights;
        public AnimatorFloatParam[] floatParameters;
        public AnimatorBoolParam[] boolParameters;
        public AnimatorIntParam[] intParameters;
        public int tick;
    }

    [Serializable]
    public struct NetVector3
    {
        public float x;
        public float y;
        public float z;

        public static NetVector3 FromUnity(Vector3 value)
        {
            NetVector3 result = new NetVector3();
            result.x = value.x;
            result.y = value.y;
            result.z = value.z;
            return result;
        }

        public Vector3 ToUnity()
        {
            return new Vector3(x, y, z);
        }
    }

    [Serializable]
    public struct NetQuaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public static NetQuaternion FromUnity(Quaternion value)
        {
            NetQuaternion result = new NetQuaternion();
            result.x = value.x;
            result.y = value.y;
            result.z = value.z;
            result.w = value.w;
            return result;
        }

        public Quaternion ToUnity()
        {
            return new Quaternion(x, y, z, w);
        }
    }

    [Serializable]
    public struct AnimatorFloatParam
    {
        public string name;
        public float value;
    }

    [Serializable]
    public struct AnimatorBoolParam
    {
        public string name;
        public bool value;
    }

    [Serializable]
    public struct AnimatorIntParam
    {
        public string name;
        public int value;
    }
}
