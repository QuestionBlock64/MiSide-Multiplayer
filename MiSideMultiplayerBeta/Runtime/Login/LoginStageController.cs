using System;
using System.Collections.Generic;

namespace MiSideMultiplayer.Runtime.Login
{
    public sealed class LoginStageController
    {
        private readonly List<LoginStage> _stages = new List<LoginStage>();
        private int _currentIndex = -1;
        private bool _started;
        private bool _completed;
        private bool _failed;

        public event Action<LoginStage> OnStageStarted;
        public event Action OnAllStagesCompleted;
        public event Action<LoginStage, Exception> OnStageFailed;

        public LoginStage CurrentStage =>
            (_currentIndex >= 0 && _currentIndex < _stages.Count) ? _stages[_currentIndex] : null;

        public float OverallProgress
        {
            get
            {
                if (_stages.Count == 0) return 1f;
                float total = 0f;
                for (int i = 0; i < _stages.Count; i++) total += _stages[i].GetProgress();
                return total / _stages.Count;
            }
        }

        public bool IsComplete => _completed;
        public bool IsFailed   => _failed;

        public void Add(LoginStage stage)
        {
            if (stage == null) return;
            _stages.Add(stage);
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _currentIndex = -1;
            _completed = false;
            _failed = false;
            Advance();
        }

        public void Tick(float dt)
        {
            if (!_started || _completed || _failed) return;
            var stage = CurrentStage;
            if (stage == null) { Complete(); return; }

            var poll = stage as ILoginStagePollable;
            if (poll != null)
            {
                try { poll.Poll(dt); }
                catch (Exception ex) { Fail(stage, ex); }
            }
        }

        private void Advance()
        {
            if (_currentIndex >= 0 && _currentIndex < _stages.Count)
            {
                var prev = _stages[_currentIndex];
                try { prev.Finish(); }
                catch (Exception ex) { Fail(prev, ex); return; }

                try { prev.Destroy(); }
                catch (Exception ex) { Fail(prev, ex); return; }
            }

            _currentIndex++;
            if (_currentIndex >= _stages.Count) { Complete(); return; }

            var next = _stages[_currentIndex];
            try
            {
                next.Initialize();
                next.OnStageFinished = HandleStageFinished;

                var started = OnStageStarted;
                if (started != null) started(next);

                next.Execute();
            }
            catch (Exception ex) { Fail(next, ex); return; }
        }

        private void HandleStageFinished(LoginStage stage)
        {
            if (stage != CurrentStage) return;
            Advance();
        }

        private void Complete()
        {
            _completed = true;
            var done = OnAllStagesCompleted;
            if (done != null) done();
        }

        private void Fail(LoginStage stage, Exception ex)
        {
            _failed = true;
            var fail = OnStageFailed;
            if (fail != null) fail(stage, ex);
        }
    }
}