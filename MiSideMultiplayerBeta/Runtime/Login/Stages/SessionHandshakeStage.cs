using System;

namespace MiSideMultiplayer.Runtime.Login.Stages
{
    /// <summary>
    /// Stage 2: send the join message, wait for server ack. Same shape as
    /// BP_SecondStage.
    /// </summary>
    public sealed class SessionHandshakeStage : AwaitLoginStage
    {
        private readonly Func<bool> _isHandshakeComplete;

        public SessionHandshakeStage(Func<bool> isHandshakeComplete) : base(2)
        {
            _isHandshakeComplete = isHandshakeComplete;
        }

        protected override void OnExecute()
        {
            DiagnosticLog.Info("LoginStage[2]: session handshake.");
            // TODO: actually send the join message here.
            BeginAwait();
        }

        protected override bool IsCounterpartFinished()
        {
            return _isHandshakeComplete != null && _isHandshakeComplete();
        }
    }
}