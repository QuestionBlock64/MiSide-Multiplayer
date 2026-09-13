using System;
using System.Collections.Generic;

namespace MiSideMultiplayer
{
    [Serializable]
    public sealed class MitaState
    {
        public string senderId;
        public string sceneName;
        public string mitaPath;

        public NetVector3 position;
        public NetQuaternion rotation;

        public Dictionary<string, BoneTransformData> Bones;

        public bool navEnabled;
        public NetVector3 navDestination;

        public bool magnetActive;
        public NetVector3 magnetTargetPos;
        public NetQuaternion magnetTargetRot;

        // Host-authoritative gaze: id of the player this Mita is aiming at.
        // Empty string means "no target" — non-host will leave the head IK
        // weight to naturally fall off.
        public string gazeTargetPlayerId;

        // Host's local unscaled time when this state was sampled. Used by the
        // receiver to place the state on its own timeline for interpolation.
        public float hostSendTime;

        public MitaEvent[] events;
    }

    [Serializable]
    public sealed class MitaEvent
    {
        public string mitaPath;
        public string kind;
        public string stringValue;
        public int    intValue;
        public float  floatValue;
        public bool   boolValue;
    }
}