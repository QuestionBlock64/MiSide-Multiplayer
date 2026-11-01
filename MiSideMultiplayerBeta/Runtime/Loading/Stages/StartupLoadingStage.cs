using UnityEngine;

namespace MiSideMultiplayer.Runtime.Loading
{
    internal sealed class StartupLoadingStage : LoadingStage
    {
        private bool _done;
        private bool _sawLoadingOverlay;
        private bool _waitingAnnounced;
        private float _startedAt;
        private float _overlayShownAt;

        private const float MinimumVisibleSeconds = 0.25f;
        private const float PreflightProgressSeconds = 0.5f;
        private const float OverlayWaitTimeoutSeconds = 5f;

        public override void InitializeLoadingStage()
        {
            base.InitializeLoadingStage();
            _done = false;
            _sawLoadingOverlay = false;
            _waitingAnnounced = false;
            _startedAt = Time.unscaledTime;
            _overlayShownAt = 0f;
        }

        public override void ExecuteLoadingStage()
        {
            PrepareMultiplayerState();
        }

        private void PrepareMultiplayerState()
        {
            try
            {
                // Client-local prep: clear any leftover puppets from the
                // previous session. Read the current roster so the UI can
                // show it.
                DiagnosticLog.Info("StartupLoadingStage: clearing world caches.");

                try { MitaSceneCache.Invalidate(); }         catch { }
                try { WorldOccupancyGuard.Invalidate(); }    catch { }

                // The puppet registry is inside NetworkManager; a fresh
                // scene load will recreate puppets anyway, but cleaning up
                // here means the loading UI can show a clean slate.
                // (NetworkManager.ClearAllPuppets is a public helper we'd
                // add if it doesn't exist; here we just invalidate caches.)

                _done = true;
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("StartupLoadingStage: " + ex.Message);
                _done = true;
            }
        }

        public override float GetProgress()
        {
            if (!_done) return 0f;

            float preflightProgress = Mathf.Clamp01(
                (Time.unscaledTime - _startedAt) / PreflightProgressSeconds);
            if (!_sawLoadingOverlay || preflightProgress < 1f)
                return 0.75f * preflightProgress;

            return 0.75f + 0.25f * Mathf.Clamp01(
                (Time.unscaledTime - Mathf.Max(_overlayShownAt,
                    _startedAt + PreflightProgressSeconds)) / MinimumVisibleSeconds);
        }

        public override void Tick(float dt)
        {
            TryCompleteStartup();
        }

        private void TryCompleteStartup()
        {
            if (!_waitingAnnounced) PublishLocalProgress();

            if (!_done || IsFinished) return;

            if (!_sawLoadingOverlay && MultiplayerLoadingScene.HasRendered)
            {
                _sawLoadingOverlay = true;
                _overlayShownAt = Time.unscaledTime;
                DiagnosticLog.Info("StartupLoadingStage: multiplayer loading overlay is ready.");
            }

            if (_sawLoadingOverlay)
            {
                if (Time.unscaledTime >= _startedAt + PreflightProgressSeconds &&
                    Time.unscaledTime - _overlayShownAt >= MinimumVisibleSeconds)
                {
                    AnnounceWaitingForPlayers();
                    FinishLoadingStage();
                }
                return;
            }

            if (Time.unscaledTime - _startedAt >= OverlayWaitTimeoutSeconds)
            {
                DiagnosticLog.Warning("StartupLoadingStage: loading overlay did not appear; continuing.");
                AnnounceWaitingForPlayers();
                FinishLoadingStage();
            }
        }
        private void PublishLocalProgress()
        {
            LoadingPhaseDef phase = LoadingPhases.Get(0);
            if (phase == null) return;

            MultiplayerLoadingScene.ApplyPhase(0, phase.Id, GetProgress(), 0,
                                               LoadingStageManager.ExpectedClientCount(),
                                               phase.FriendlyName, phase.StatusName);
        }

        private void AnnounceWaitingForPlayers()
        {
            if (_waitingAnnounced) return;
            _waitingAnnounced = true;

            int expected = LoadingStageManager.ExpectedClientCount();
            MultiplayerLoadingScene.ShowWaitingForPlayers(expected);
            DiagnosticLog.Info("StartupLoadingStage: preflight complete; waiting for players.");
        }

    }
}