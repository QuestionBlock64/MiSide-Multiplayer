using System;

namespace MiSideMultiplayer
{
    [Serializable]
    public sealed class WorldStoryObjectState
    {
        public string senderId;
        public string sceneName;
        public string path;
        public bool   isActive;
        public bool   isTerminalDisabled;
        public bool   isSnapshot;
        public string snapshotId;
        public int    snapshotIndex;
        public int    snapshotCount;
        public WorldAnimatorState[] animators;
        public WorldTransformState[] transforms;
        public WorldAudioSourceState[] audioSources;
        public long scene5Sequence;
        public NetVector3 senderPosition;
    }

    [Serializable]
    public sealed class WorldTransformState
    {
        public string transformPath;
        public NetVector3 position;
        public NetQuaternion rotation;
        public NetVector3 localScale;
    }

    [Serializable]
    public sealed class WorldAudioSourceState
    {
        public string audioSourcePath;
        public string clipName;
        public bool enabled;
        public bool isPlaying;
        public bool loop;
        public bool mute;
        public float time;
        public float volume;
        public float pitch;
    }

    [Serializable]
    public sealed class WorldAnimatorState
    {
        public string animatorPath;
        public bool enabled;
        public float speed;
        public WorldAnimatorLayerState[] layers;
        public WorldAnimatorParameterState[] parameters;
    }

    [Serializable]
    public sealed class WorldAnimatorLayerState
    {
        public int layerIndex;
        public int stateHash;
        public float normalizedTime;
        public float weight;
    }

    [Serializable]
    public sealed class WorldAnimatorParameterState
    {
        public int nameHash;
        public int type;
        public bool boolValue;
        public int intValue;
        public float floatValue;
    }
}
