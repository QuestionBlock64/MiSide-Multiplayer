namespace MiSideMultiplayer.Runtime.Loading
{
    internal sealed class LevelStreamingStage : ServerAwaitLoadingStage
    {
        private float _startedAt;

        public override float MinimumStageSeconds { get { return 2.0f; } }

        public override void InitializeLoadingStage()
        {
            base.InitializeLoadingStage();
            _startedAt = UnityEngine.Time.unscaledTime;
        }

        protected override void OnClientExecute()
        {
            DiagnosticLog.Info("LevelStreamingStage: client entered the shared black loading stage.");
        }

        protected override void OnServerExecute()
        {
            DiagnosticLog.Info("LevelStreamingStage: host entered the shared black loading stage.");
        }

        protected override void OnClientTick(float dt)
        {
            if (UnityEngine.Time.unscaledTime - _startedAt >= MinimumStageSeconds) SendReady();
        }

        protected override void OnServerTick(float dt)
        {
            if (UnityEngine.Time.unscaledTime - _startedAt >= MinimumStageSeconds) SendReady();
        }

        public override float GetProgress()
        {
            return -1f;
        }
    }
}