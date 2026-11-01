namespace MiSideMultiplayer.Runtime.Loading
{
    internal sealed class BotSetupStage : ServerAwaitLoadingStage
    {
        private float _startedAt;
        private float _nextSnapshotRequestAt;

        public override float MinimumStageSeconds { get { return 1.5f; } }

        public override void InitializeLoadingStage()
        {
            base.InitializeLoadingStage();
            _startedAt = UnityEngine.Time.unscaledTime;
        }

        protected override void OnClientExecute()
        {
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            MitaSnapshotSync.Reset(sceneName);
            _nextSnapshotRequestAt = UnityEngine.Time.unscaledTime;
            DiagnosticLog.Info("BotSetupStage: downloading host Mita state (client).");
            MitaController.SuppressLocalAIForAll();
            MitaSnapshotSync.RequestSnapshot();
            _nextSnapshotRequestAt = UnityEngine.Time.unscaledTime + 1f;
        }

        protected override void OnServerExecute()
        {
            DiagnosticLog.Info("BotSetupStage: hosting authoritative Mita state (host).");
        }

        protected override void OnClientTick(float dt)
        {
            float elapsed = UnityEngine.Time.unscaledTime - _startedAt;
            if (!MitaSnapshotSync.HasCompletedSnapshot &&
                UnityEngine.Time.unscaledTime >= _nextSnapshotRequestAt)
            {
                MitaSnapshotSync.RequestSnapshot();
                _nextSnapshotRequestAt = UnityEngine.Time.unscaledTime + 1f;
            }

            if (elapsed >= MinimumStageSeconds && MitaSnapshotSync.HasCompletedSnapshot)
                SendReady();
        }

        protected override void OnServerTick(float dt)
        {
            if (UnityEngine.Time.unscaledTime - _startedAt >= MinimumStageSeconds) SendReady();
        }

        public override float GetProgress()
        {
            return MitaSnapshotSync.Progress;
        }
    }
}
