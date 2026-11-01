using System;

namespace MiSideMultiplayer.Runtime.Loading
{
    public abstract class LoadingStage
    {
        protected LoadingStage() { }

        public int StageIndex { get; internal set; }

        public LoadingPipeline Pipeline { get; internal set; }

        public bool IsFinished { get; private set; }

        public event Action OnLoadingStageFinished;

        public virtual void InitializeLoadingStage()
        {
            IsFinished = false;
        }

        public abstract void ExecuteLoadingStage();

        public abstract void Tick(float dt);

        public abstract float GetProgress();

        public virtual void FinishFromServer()
        {
            FinishLoadingStage();
        }

        public virtual void DestroyLoadingStage() { }

        protected void FinishLoadingStage()
        {
            if (IsFinished) return;
            IsFinished = true;

            try
            {
                var handler = OnLoadingStageFinished;
                if (handler != null) handler();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("LoadingStage: OnLoadingStageFinished threw: " + ex.Message);
            }
        }
    }
}