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

        // ── Body ──────────────────────────────────────────────────────────────
        public NetVector3    position;
        public NetQuaternion rotation;
        public NetVector3    velocity;
        public float         speed;         // forward horizontal magnitude → SpeedForward
        public float         lateralSpeed;  // strafe                       → InertionRight
        public bool          isGrounded;
        public bool          isVisible = true;

        // ── Head (HeadPlayer world rotation sampled from source player) ───────
        public NetQuaternion headRotation;

        // ── Animator state (state-machine level) ──────────────────────────────
        // shortNameHash from Animator.GetCurrentAnimatorStateInfo(layer).
        // Sent every tick so the puppet can call Play(hash) when it drifts.
        public int   animatorStateHash;          // current state (layer 0, short name)
        public int   animatorFullPathHash;       // current state (layer 0, full path — preferred for Play())
        public float animatorNormalizedTime;     // position within that state
        public string action;                    // cross-fade clip name (legacy path)

        // ── Animator parameters ───────────────────────────────────────────────
        public float[]              blendWeights;
        public AnimatorFloatParam[] floatParameters;
        public AnimatorBoolParam[]  boolParameters;
        public AnimatorIntParam[]   intParameters;

        // ── MS_CustomModels integration ───────────────────────────────────────
        // "None" (or empty) = default MiSide body.
        // Any other value = the .vrmmod model name the remote player has loaded.
        public string customModelName;

        public NetQuaternion[] boneRotations;   // see BoneSync.BonePaths for order
        public bool            hasBoneData;

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
