using System;

namespace MiSideMultiplayer.Runtime.Loading
{
    [Serializable]
    public sealed class LoadingPhaseBroadcast
    {
        public int    stageIndex;      // which phase (0..N)
        public string phaseId;         // "Initializing" etc.
        public float  progress;        // 0..1 within this phase
        public int    readyCount;      // X
        public int    expectedCount;   // Y
        public string friendlyName;    // what the client should show now
        public string statusName;      // "Loaded Players" etc.
    }
}