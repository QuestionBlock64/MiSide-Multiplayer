using System;
using System.Collections.Generic;

namespace MiSideMultiplayer
{
    [Serializable]
    public sealed class InventoryClaimRequest
    {
        public string sceneName;
        public string itemPath;
    }

    [Serializable]
    public sealed class InventoryClaimResult
    {
        public string sceneName;
        public string itemPath;
        public string ownerId;
        public bool approved;
    }

    [Serializable]
    public sealed class InventoryKeyChange
    {
        public string sceneName;
        public string itemPath;
        public string ownerId;
    }

    [Serializable]
    public sealed class InventoryConsumeRequest
    {
        public string sceneName;
        public string itemPath;
    }

    [Serializable]
    public sealed class InventoryConsumeResult
    {
        public string sceneName;
        public string itemPath;
        public bool approved;
    }

    [Serializable]
    public sealed class InventorySnapshot
    {
        public string sceneName;
        public List<InventorySnapshotItem> items;
    }

    [Serializable]
    public sealed class InventorySnapshotItem
    {
        public string itemPath;
        public string ownerId;
        public bool hasKey;
        public bool consumed;
    }
}
