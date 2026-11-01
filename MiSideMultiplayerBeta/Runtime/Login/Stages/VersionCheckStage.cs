namespace MiSideMultiplayer.Runtime.Login.Stages
{
    /// <summary>
    /// Stage 0: local version check. Inherits LoginStage directly, like
    /// BP_ThirdStage inherits BP_BaseLoadingStageComponent directly.
    /// Does its work synchronously; no counterpart, no await.
    /// </summary>
    public sealed class VersionCheckStage : LoginStage
    {
        public VersionCheckStage() : base(0) { }

        protected override void OnExecute()
        {
            DiagnosticLog.Info("LoginStage[0]: version check.");
            // TODO: real HTTP call. For now, finish immediately.
            ReportFinished();
        }
    }
}