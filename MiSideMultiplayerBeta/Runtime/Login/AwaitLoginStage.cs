using UnityEngine;

namespace MiSideMultiplayer.Runtime.Login
{
    /// <summary>
    /// Middle-layer stage: does not do the work itself. Delegates to a
    /// "counterpart" (relay transport, host, whatever) and waits for the
    /// counterpart to signal done.
    ///
    /// Translates BP_ServerAwaitLoadingStageComponent_C.
    /// </summary>
    public abstract class AwaitLoginStage : LoginStage, ILoginStagePollable
    {
        private bool _awaitingCounterpart;
        private float _awaitStartTime;

        protected AwaitLoginStage(int stageIndex) : base(stageIndex) { }

        /// <summary>
        /// How long we'll wait for the counterpart before giving up.
        /// Override in concrete stages if needed.
        /// </summary>
        protected virtual float AwaitTimeoutSeconds => 10f;

        /// <summary>
        /// Concrete stages call this from OnExecute after setting up
        /// whatever request they need to send to the counterpart.
        /// </summary>
        protected void BeginAwait()
        {
            _awaitingCounterpart = true;
            _awaitStartTime = Time.realtimeSinceStartup;
            DiagnosticLog.Info("AwaitLoginStage[" + StageIndex + "]: awaiting counterpart.");
        }

        /// <summary>
        /// Concrete stages override this to check the counterpart's status.
        /// Return true when the counterpart has signalled finished.
        /// </summary>
        protected abstract bool IsCounterpartFinished();

        /// <summary>
        /// Called if the timeout elapses without the counterpart signalling.
        /// Default behaviour is to treat it as a failure and finish anyway.
        /// </summary>
        protected virtual void OnCounterpartTimeout()
        {
            DiagnosticLog.Warning("AwaitLoginStage[" + StageIndex + "]: counterpart timed out after " +
                                  AwaitTimeoutSeconds + "s.");
            ReportFinished();
        }

        // ILoginStagePollable
        public void Poll(float dt)
        {
            if (!_awaitingCounterpart) return;
            if (Finished) return;

            if (IsCounterpartFinished())
            {
                _awaitingCounterpart = false;
                ReportFinished();
                return;
            }

            float elapsed = Time.realtimeSinceStartup - _awaitStartTime;
            if (elapsed >= AwaitTimeoutSeconds)
            {
                _awaitingCounterpart = false;
                OnCounterpartTimeout();
            }
        }
    }
}