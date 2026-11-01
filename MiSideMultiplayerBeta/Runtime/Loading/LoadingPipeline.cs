using System;
using System.Collections.Generic;

namespace MiSideMultiplayer.Runtime.Loading
{
    public sealed class LoadingPipeline
    {
        private readonly List<LoadingStage> _stages = new List<LoadingStage>();
        private int _currentIndex = -1;

        public bool IsRunning { get; private set; }
        public bool IsComplete { get; private set; }
        public int CurrentIndex { get { return _currentIndex; } }

        public event Action OnComplete;

        public void Add(LoadingStage stage)
        {
            if (stage == null) return;
            stage.StageIndex = _stages.Count;
            stage.Pipeline   = this;
            _stages.Add(stage);
        }

        public LoadingStage GetStageAt(int index)
        {
            if (index < 0 || index >= _stages.Count) return null;
            return _stages[index];
        }

        public void Start()
        {
            if (IsRunning) return;
            if (_stages.Count == 0)
            {
                IsComplete = true;
                RaiseComplete();
                return;
            }

            IsRunning = true;
            IsComplete = false;
            _currentIndex = 0;
            InitializeAndExecute(_stages[0]);
        }

        public void Tick(float dt)
        {
            if (!IsRunning || IsComplete) return;
            if (_currentIndex < 0 || _currentIndex >= _stages.Count) return;

            var stage = _stages[_currentIndex];
            if (stage == null) { AdvanceToNext(); return; }

            try { stage.Tick(dt); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("LoadingPipeline: stage " + _currentIndex +
                                      " Tick threw: " + ex.Message);
            }
        }

        public void NotifyClientReady(int stageIndex, string playerId)
        {
            if (stageIndex != _currentIndex) return;
            var stage = _stages[_currentIndex] as ServerAwaitLoadingStage;
            if (stage == null) return;
            stage.NotifyClientReady(playerId);
        }

        public void NotifyStageFinished(int stageIndex)
        {
            if (!IsRunning || IsComplete) return;
            if (stageIndex != _currentIndex) return;

            var stage = _stages[_currentIndex];
            if (stage == null) { AdvanceToNext(); return; }

            // If this stage has a local minimum display time, RequestServerFinish
            // will hold the finish until the minimum elapses on this peer.
            // Otherwise it finishes immediately. This is what stops a fast
            // host from making the joiner's overlay flash through all six
            // phases in a single frame.
            var awaitStage = stage as ServerAwaitLoadingStage;
            if (awaitStage != null)
            {
                awaitStage.RequestServerFinish();
                return;
            }

            stage.FinishFromServer();
        }

        public void AdvanceToHostStage(int hostStageIndex)
        {
            if (!IsRunning || IsComplete || hostStageIndex <= _currentIndex) return;

            while (IsRunning && !IsComplete && _currentIndex < hostStageIndex)
            {
                LoadingStage stage = GetStageAt(_currentIndex);
                if (stage == null) { AdvanceToNext(); continue; }

                // A host phase broadcast is a status update, not permission
                // to skip a joiner's own loading work. Ordinary local stages
                // must complete themselves. A shared stage may complete from
                // the host only after this peer has acknowledged it.
                var awaitStage = stage as ServerAwaitLoadingStage;
                if (awaitStage == null)
                {
                    if (!stage.IsFinished) return;
                    AdvanceToNext();
                    continue;
                }

                if (!awaitStage.HasLocalClientAcked) return;
                awaitStage.RequestServerFinish();
                if (_currentIndex < hostStageIndex) return;

                stage.FinishFromServer();
            }
        }

        private void InitializeAndExecute(LoadingStage stage)
        {
            try
            {
                stage.OnLoadingStageFinished += OnStageFinished;
                stage.InitializeLoadingStage();
                stage.ExecuteLoadingStage();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("LoadingPipeline: stage " + _currentIndex +
                                      " init/execute threw: " + ex.Message);
                AdvanceToNext();
            }
        }

        private void OnStageFinished()
        {
            AdvanceToNext();
        }

        private void AdvanceToNext()
        {
            if (!IsRunning || IsComplete) return;

            if (_currentIndex >= 0 && _currentIndex < _stages.Count)
            {
                var prev = _stages[_currentIndex];
                if (prev != null)
                {
                    try { prev.OnLoadingStageFinished -= OnStageFinished; } catch { }
                    try { prev.DestroyLoadingStage(); } catch { }
                }
            }

            _currentIndex++;
            if (_currentIndex >= _stages.Count)
            {
                IsRunning  = false;
                IsComplete = true;
                RaiseComplete();
                return;
            }

            InitializeAndExecute(_stages[_currentIndex]);
        }

        private void RaiseComplete()
        {
            try
            {
                var h = OnComplete;
                if (h != null) h();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("LoadingPipeline.OnComplete threw: " + ex.Message);
            }
        }
    }
}