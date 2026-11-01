namespace MiSideMultiplayer.Runtime.Loading
{
    /// <summary>
    /// Static definition of a single loading phase. Mirrors one row in the
    /// UE project's DT_CoopLoadingTable.
    /// </summary>
    public sealed class LoadingPhaseDef
    {
        public readonly string Id;
        public readonly string FriendlyName;
        public readonly string PostFinishName;
        public readonly string StatusName;

        public LoadingPhaseDef(string id, string friendlyName,
                               string postFinishName, string statusName)
        {
            Id             = id;
            FriendlyName   = friendlyName;
            PostFinishName = postFinishName;
            StatusName     = statusName;
        }
    }

    /// <summary>
    /// The ordered phase table. Index matches StageIndex everywhere.
    /// </summary>
    public static class LoadingPhases
    {
        public static readonly LoadingPhaseDef[] All = new LoadingPhaseDef[]
        {
            new LoadingPhaseDef(
                "Loading",
                "Loading...",
                "Loading...",
                "Loaded Players"),

            new LoadingPhaseDef(
                "LoadingLevels",
                "Waiting For Players...",
                "Waiting For Players...",
                "Loaded Players"),

            new LoadingPhaseDef(
                "SyncingStates",
                "Syncing States...",
                "Syncing States...",
                "Loaded Players"),

            new LoadingPhaseDef(
                "SyncingWorld",
                "Syncing World...",
                "Syncing World...",
                "Loaded Players"),

            new LoadingPhaseDef(
                "SettingUpMitas",
                "Downloading Mita State...",
                "Downloading Mita State...",
                "Loaded Players"),

            new LoadingPhaseDef(
                "Validating",
                "Validating...",
                "Validating...",
                "Loaded Players"),

            new LoadingPhaseDef(
                "FinishingUp",
                "Finishing up...",
                "Finishing up...",
                "Loaded Players"),
        };

        public static int Count { get { return All.Length; } }

        public static LoadingPhaseDef Get(int index)
        {
            if (index < 0 || index >= All.Length) return null;
            return All[index];
        }
    }
}
