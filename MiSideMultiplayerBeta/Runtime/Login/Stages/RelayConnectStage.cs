using System;

namespace MiSideMultiplayer.Runtime.Login.Stages
{
    /// <summary>
    /// Stage 1: wait for the relay transport to connect. Inherits the await
    /// middle layer, like BP_FirstStage inherits BP_ServerAwaitLoadingStageComponent.
    /// </summary>
    public sealed class RelayConnectStage : AwaitLoginStage
    {
        private readonly Func<bool> _isConnected;

        public RelayConnectStage(Func<bool> isConnected) : base(1)
        {
            _isConnected = isConnected;
        }

        protected override void OnExecute()
        {
            DiagnosticLog.Info("LoginStage[1]: waiting for relay connection.");
            BeginAwait();
        }

        protected override bool IsCounterpartFinished()
        {
            return _isConnected != null && _isConnected();
        }
    }
}