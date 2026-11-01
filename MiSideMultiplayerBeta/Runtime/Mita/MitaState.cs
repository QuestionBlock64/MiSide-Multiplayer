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

        // A full snapshot is sent during the loading "Downloading Mita State"
        // phase. The host numbers each Mita record so a joiner can acknowledge
        // a complete baseline instead of treating one received Mita as ready.
        public bool isFullSnapshot;
        public string snapshotId;
        public int snapshotIndex;
        public int snapshotCount;
        public long lastEventSequence;

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

        // The ordinary Mita puppet disables local game AI. Preserve the
        // visible/behavioral state held by MitaLifeEmotion alongside the pose.
        public MitaLifeEmotionState lifeEmotion;

        // Host's local unscaled time when this state was sampled. Used by the
        // receiver to place the state on its own timeline for interpolation.
        public float hostSendTime;

        public MitaEvent[] events;
    }

    [Serializable]
    public sealed class MitaLifeEmotionState
    {
        public bool active;
        public float timeDontActive;
        public float timeShy;
        public int levelShy;
        public int indexDialogueShy;
        public float timeIgnoryActivation;
        public int typeEmotionLongSee;
        public string emotionStart;
        public string emotionStop;
        public bool randomEmotion;
        public float timeRanomEmotion;
        public bool lookFullPlayer;
        public float timeLookFullPlayer;
        public int orderLookFullPlayer;
        public int dialogueFarAwayIndex;
        public int dialogueCloselyIndex;
        public float dialogueFarAwayTime;
        public float dialogueCloselyTime;
        public float speedTimeOtherDiaolgue;
        public bool lookHead;
    }

    [Serializable]
    public sealed class MitaEvent
    {
        public string senderId;
        public string sceneName;
        public string mitaPath;
        public string kind;
        public string stringValue;
        public string targetPlayerId;
        public int    intValue;
        public float  floatValue;
        public bool   boolValue;
        public long   sequence;
        public NetVector3 targetPosition;
    }
}
