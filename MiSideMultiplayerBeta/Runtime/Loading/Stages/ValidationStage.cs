namespace MiSideMultiplayer.Runtime.Loading
{
    internal sealed class ValidationStage : ServerAwaitLoadingStage
    {
        private float _startedAt;

        public override float MinimumStageSeconds { get { return 1.0f; } }

        public override void InitializeLoadingStage()
        {
            base.InitializeLoadingStage();
            _startedAt = UnityEngine.Time.unscaledTime;
        }

        protected override void OnClientExecute()
        {
            DiagnosticLog.Info("ValidationStage: validating synchronized world (client).");
        }

        protected override void OnServerExecute()
        {
            DiagnosticLog.Info("ValidationStage: validating synchronized world (host).");
        }

        protected override void OnClientTick(float dt)
        {
            if (UnityEngine.Time.unscaledTime - _startedAt >= MinimumStageSeconds) SendSyncDoneAck();
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
