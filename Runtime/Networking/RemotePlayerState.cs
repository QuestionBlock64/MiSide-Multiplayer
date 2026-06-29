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

        // Body
        public NetVector3    position;
        public NetQuaternion rotation;
        public NetVector3    velocity;
        public float         speed;         // forward horizontal magnitude
        public float         lateralSpeed;  // strafe -> InertionRight animator param
        public bool          isGrounded;
        public bool          isVisible = true;

        // Head (Person/HeadMirror world rotation, confirmed in dump string literals)
        public NetQuaternion headRotation;

        // Animator
        public string               action;
        public float[]              blendWeights;
        public AnimatorFloatParam[] floatParameters;
        public AnimatorBoolParam[]  boolParameters;
        public AnimatorIntParam[]   intParameters;

        public int tick;
    }

    [Serializable]
    public struct NetVector3
    {
        public float x, y, z;
        public static NetVector3 FromUnity(Vector3 v)
        { NetVector3 r = new NetVector3(); r.x=v.x; r.y=v.y; r.z=v.z; return r; }
        public Vector3 ToUnity() { return new Vector3(x, y, z); }
    }

    [Serializable]
    public struct NetQuaternion
    {
        public float x, y, z, w;
        public static NetQuaternion FromUnity(Quaternion q)
        { NetQuaternion r = new NetQuaternion(); r.x=q.x; r.y=q.y; r.z=q.z; r.w=q.w; return r; }
        public Quaternion ToUnity() { return new Quaternion(x, y, z, w); }
        public static readonly NetQuaternion Identity = FromUnity(Quaternion.identity);
    }

    [Serializable] public struct AnimatorFloatParam { public string name; public float value; }
    [Serializable] public struct AnimatorBoolParam  { public string name; public bool  value; }
    [Serializable] public struct AnimatorIntParam   { public string name; public int   value; }
}
