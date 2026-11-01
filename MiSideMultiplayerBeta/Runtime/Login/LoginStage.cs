using System;

namespace MiSideMultiplayer.Runtime.Login
{
    /// <summary>
    /// Base class for a single step of the login flow. Mirrors the Unreal
    /// BP_BaseLoadingStageComponent pattern: a passive state holder that a
    /// controller drives through Initialize → Execute → Finish → Destroy.
    /// Does NOT tick itself; the controller polls it.
    /// </summary>
    public abstract class LoginStage
    {
        public int StageIndex { get; private set; }
        public bool Finished { get; private set; }

        /// <summary>
        /// Raised when Execute() has done its job. Controller subscribes.
        /// Plain Action (not event) so the controller can subscribe from outside.
        /// </summary>
        public Action<LoginStage> OnStageFinished;

        protected LoginStage(int stageIndex)
        {
            StageIndex = stageIndex;
        }

        // ── Lifecycle ───────────────────────────────────────────────────────
        // Same four methods as the Unreal base. Controller calls them in order.

        public void Initialize()
        {
            Finished = false;
            OnInitialize();
        }

        public void Execute()
        {
            OnExecute();
        }

        public void Finish()
        {
            Finished = true;
            OnFinish();
        }

        public void Destroy()
        {
            OnDestroy();
            OnStageFinished = null;
        }

        // ── Overridable hooks for concrete stages ───────────────────────────
        // Concrete stages normally only override OnExecute.

        protected virtual void OnInitialize() { }
        protected abstract void OnExecute();
        protected virtual void OnFinish() { }
        protected virtual void OnDestroy() { }

        // ── Progress query ──────────────────────────────────────────────────

        /// <summary>0..1. Override if the stage has measurable progress.</summary>
        public virtual float GetProgress() => Finished ? 1f : 0f;

        // ── Called by the concrete stage to signal completion ───────────────

        protected void ReportFinished()
        {
            if (Finished) return;
            var handler = OnStageFinished;
            if (handler != null) handler(this);
        }
    }
}