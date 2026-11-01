namespace MiSideMultiplayer.Runtime.Loading
{
    internal sealed class ActorSetupStage : ServerAwaitLoadingStage
    {
        private float _startedAt;

        public override float MinimumStageSeconds { get { return 1.5f; } }

        public override void InitializeLoadingStage()
        {
            base.InitializeLoadingStage();
            _startedAt = UnityEngine.Time.unscaledTime;
            MitaPlayerRosterSync.PrepareForLoading();
        }

        protected override void OnClientExecute()
        {
            DiagnosticLog.Info("ActorSetupStage: syncing player states (client).");
            MitaController.SuppressLocalAIForAll();
            LocalPlayerSampler.SendSnapshotNow();
        }

        protected override void OnServerExecute()
        {
            DiagnosticLog.Info("ActorSetupStage: collecting and multicasting actor state.");
            MitaPlayerRosterSync.PublishHostRosterIfDue(true);
            LocalPlayerSampler.SendSnapshotNow();
        }

        protected override void OnClientTick(float dt)
        {
            if (UnityEngine.Time.unscaledTime - _startedAt >= MinimumStageSeconds &&
                MitaPlayerRosterSync.HasSnapshotForLoading) SendReady();
        }

        protected override void OnServerTick(float dt)
        {
            MitaPlayerRosterSync.PublishHostRosterIfDue(false);
            if (UnityEngine.Time.unscaledTime - _startedAt >= MinimumStageSeconds &&
                MitaPlayerRosterSync.HasSnapshotForLoading) SendReady();
        }

        public override float GetProgress()
        {
            return -1f;
        }
    }
}
