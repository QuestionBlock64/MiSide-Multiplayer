using System;

namespace MiSideMultiplayer
{
    /// <summary>
    /// State of a single door, identified by its stable scene path
    /// (see WorldObjectRegistry).
    ///
    /// localRotation is the door's ACTUAL, real, physically-simulated
    /// rotation as observed on the sender's client — sent directly rather
    /// than an "isOpen" bool alone, because HingeJoint.axis/anchor are
    /// stripped from this IL2CPP build (only .angle survived stripping —
    /// confirmed via dump), so the receiver has no safe way to compute the
    /// correct open rotation itself. Transmitting the real rotation and
    /// just matching it directly sidesteps that entirely — same approach
    /// already used for player position/rotation sync.
    /// </summary>
    [Serializable]
    public sealed class WorldDoorState
    {
        public string        senderId;
        public string        sceneName;
        public string        path;
        public bool          isOpen;
        public NetQuaternion localRotation;
    }
}
