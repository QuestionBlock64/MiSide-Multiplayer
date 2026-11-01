using System;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Loading
{
    public abstract class ServerAwaitLoadingStage : LoadingStage
    {
        protected ServerAwaitLoadingStage() { }

        private readonly System.Collections.Generic.HashSet<string> _ready =
            new System.Collections.Generic.HashSet<string>();

        protected bool ReportedReady;
        private float _stageStartedAt;
        private bool _timeoutForced;
        private bool _serverFinishDeferred;
        private const float ServerReadyTimeoutSeconds = 45f;

        public virtual float MinimumStageSeconds { get { return 0.5f; } }

        public bool HasLocalClientAcked { get { return ReportedReady; } }

        public float StageStartedAt { get { return _stageStartedAt; } }

        public override void InitializeLoadingStage()
        {
            base.InitializeLoadingStage();
            _ready.Clear();
            ReportedReady = false;
            _timeoutForced = false;
            _serverFinishDeferred = false;
            _stageStartedAt = Time.unscaledTime;
        }

        public override void ExecuteLoadingStage()
        {
            if (IsHost()) OnServerExecute();
            else OnClientExecute();
        }

        public override void Tick(float dt)
        {
            if (IsHost())
            {
                OnServerTick(dt);

                bool allReady = _ready.Count >= ExpectedClientCount();
                bool minElapsed = Time.unscaledTime - _stageStartedAt >= MinimumStageSeconds;

                if (allReady && minElapsed)
                {
                    OnServerAllReady();
                    FinishLoadingStage();
                    return;
                }

                if (!_timeoutForced && Time.unscaledTime - _stageStartedAt >= ServerReadyTimeoutSeconds)
                {
                    _timeoutForced = true;
                    DiagnosticLog.Warning("ServerAwaitLoadingStage[" + StageIndex +
                                          "]: timed out waiting for players; forcing advance.");
                    int needed = ExpectedClientCount();
                    while (_ready.Count < needed) _ready.Add("__timeout_" + _ready.Count);
                    OnServerAllReady();
                    FinishLoadingStage();
                    return;
                }
            }
            else
            {
                OnClientTick(dt);

                if (_serverFinishDeferred &&
                    Time.unscaledTime - _stageStartedAt >= MinimumStageSeconds)
                {
                    _serverFinishDeferred = false;
                    FinishLoadingStage();
                }
            }
        }

        [HideFromIl2Cpp]
        public void RequestServerFinish()
        {
            float elapsed = Time.unscaledTime - _stageStartedAt;
            if (!ReportedReady)
            {
                DiagnosticLog.Warning("ServerAwaitLoadingStage[" + StageIndex +
                                      "]: ignored host phase advance before local ready.");
                return;
            }

            if (elapsed >= MinimumStageSeconds)
            {
                FinishFromServer();
                return;
            }

            _serverFinishDeferred = true;
        }

        [HideFromIl2Cpp]
        internal void NotifyClientReady(string playerId)
        {
            if (!IsHost()) return;
            if (string.IsNullOrEmpty(playerId)) return;
            _ready.Add(playerId);
        }

        protected abstract void OnClientExecute();
        protected virtual void OnServerExecute() { }
        protected virtual void OnServerTick(float dt) { }
        protected virtual void OnServerAllReady() { }
        protected virtual void OnClientTick(float dt) { }

        protected void SendReady()
        {
            if (ReportedReady) return;
            ReportedReady = true;
            LoadingStageManagerLocalFlags.LocalFinishedCurrentPhase = true;

            if (IsHost()) _ready.Add(MiSideMultiplayerPlugin.GetLocalPlayerId());

            LoadingNetwork.SendClientReady(StageIndex);
        }

        protected void SendSyncDoneAck()
        {
            if (ReportedReady) return;
            ReportedReady = true;
            LoadingStageManagerLocalFlags.LocalFinishedCurrentPhase = true;

            if (IsHost())
                _ready.Add(MiSideMultiplayerPlugin.GetLocalPlayerId());
            else
                LoadingNetwork.SendSyncDoneAck(StageIndex);
        }

        public int ReadyCount { get { return _ready.Count; } }

        protected int ExpectedClientCount() { return LoadingNetwork.ExpectedClientCount(); }
        protected static bool IsHost() { return LoadingNetwork.IsHost(); }
    }
}
