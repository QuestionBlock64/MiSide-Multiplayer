using System;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Loading
{
    public sealed class LoadingStageManager : MonoBehaviour
    {
        public LoadingStageManager(IntPtr ptr) : base(ptr) { }

        private static LoadingStageManager _instance;
        private LoadingPipeline _pipeline;

        private static bool _localIsHost;
        private static int  _expectedClients;

        public static bool PipelineCompleted { get; private set; }

        private float _nextBroadcastTime;
        private const float BroadcastInterval = 0.1f;

        private bool _doneBroadcast;
        private int _doneResendsLeft;
        private float _nextDoneResendTime;
        private const float DoneResendInterval = 0.5f;
        private const int DoneResendCount = 4;

        private int _lastSeenStageIndex = -1;


        public static bool IsLocalHost { get { return _localIsHost; } }

        public static int ExpectedClientCount()
        {
            if (_expectedClients <= 0) return 1;
            return _expectedClients;
        }

        public static void Begin(bool isHost, int expectedClients)
        {
            if (_instance == null)
            {
                DiagnosticLog.Warning("LoadingStageManager.Begin: no instance. " +
                                      "Spawn the manager before calling Begin.");
                return;
            }

            LoadingNetwork.EnsureBound();

            _localIsHost     = isHost;
            _expectedClients = Mathf.Max(1, expectedClients);
            WorldStoryObjectSync.ArmPostLoadSnapshotGate();

            PipelineCompleted = false;

            LoadingStageManagerLocalFlags.LocalFinishedCurrentPhase = false;
            _instance._lastSeenStageIndex = -1;

            MultiplayerLoadingScene.StartNativeLoad();

            _instance._pipeline = new LoadingPipeline();

            _instance._pipeline.Add(new StartupLoadingStage());
            _instance._pipeline.Add(new LevelStreamingStage());
            _instance._pipeline.Add(new ActorSetupStage());
            _instance._pipeline.Add(new ActorHandshakeStage());
            _instance._pipeline.Add(new BotSetupStage());
            _instance._pipeline.Add(new ValidationStage());
            _instance._pipeline.Add(new FinishingUpStage());

            _instance._pipeline.OnComplete += _instance.OnPipelineComplete;
            _instance._doneBroadcast = false;
            _instance._doneResendsLeft = 0;
            _instance._nextDoneResendTime = 0f;

            LoadingPhaseDef firstPhase = LoadingPhases.Get(0);
            if (firstPhase != null)
            {
                MultiplayerLoadingScene.ApplyPhase(
                    0, firstPhase.Id, 0f, 0, _expectedClients,
                    firstPhase.FriendlyName, firstPhase.StatusName);
            }

            DiagnosticLog.Info("LoadingStageManager: pipeline started (host=" +
                               isHost + ", expected=" + _expectedClients + ").");
            _instance._pipeline.Start();
        }

        public void OnClientReady(string playerId, int stageIndex)
        {
            if (_pipeline == null) return;
            _pipeline.NotifyClientReady(stageIndex, playerId);
        }

        public void OnServerStageFinished(int stageIndex)
        {
            if (_pipeline == null) return;
            _pipeline.NotifyStageFinished(stageIndex);
        }

        public void OnPhaseBroadcast(int    stageIndex,
                                     string phaseId,
                                     float  progress,
                                     int    readyCount,
                                     int    expectedCount,
                                     string friendlyName,
                                     string statusName)
        {
            _expectedClients = Mathf.Max(1, expectedCount);
            MultiplayerLoadingScene.ApplyPhase(
                stageIndex, phaseId, progress,
                readyCount, expectedCount,
                friendlyName, statusName);

            if (_pipeline != null)
                _pipeline.AdvanceToHostStage(stageIndex);
        }

        public void OnDone()
        {
            if (!PipelineCompleted)
            {
                PipelineCompleted = true;
                WorldStoryObjectSync.NotifyLoadingGoReceived();
                SceneManagerHook.ReleasePendingLoad();
                SceneLoadingHook.AllowLoad();
            }

            if (WorldStoryObjectSync.ShouldHoldLoadingOverlay)
            {
                DiagnosticLog.Info("LoadingStageManager: keeping multiplayer overlay up until the destination world snapshot is applied.");
                return;
            }

            MultiplayerLoadingScene.Abort();
        }

        public static void ReleaseWorldSyncOverlay()
        {
            MultiplayerLoadingScene.Abort();
        }

        /// <summary>
        /// Clears every multiplayer load hold during an explicit session exit.
        /// This is separate from OnDone: normal scene transitions must retain
        /// the native loading cleanup until the requested destination appears.
        /// </summary>
        public static void AbortForSessionExit()
        {
            try { WorldStoryObjectSync.CancelPostLoadSnapshotGate(); } catch { }
            try { SceneManagerHook.CancelPendingLoad(); } catch { }
            try { SceneLoadingHook.CancelNativeTeardown(); } catch { }

            if (_instance != null)
            {
                _instance._pipeline = null;
                _instance._doneBroadcast = false;
                _instance._doneResendsLeft = 0;
                _instance._nextDoneResendTime = 0f;
                _instance._lastSeenStageIndex = -1;
            }

            PipelineCompleted = false;
            _localIsHost = false;
            _expectedClients = 0;
            LoadingStageManagerLocalFlags.LocalFinishedCurrentPhase = false;
            try { MultiplayerLoadingScene.Abort(); } catch { }
        }

        public void OnParticipantDropped()
        {
            DiagnosticLog.Warning("LoadingStageManager: relay removed this client after a 25-second loading-stage timeout; releasing the multiplayer load.");
            _pipeline = null;
            OnDone();
        }

        private void Awake()
        {
            gameObject.name = "MiSideMultiplayer.Loading";
            DontDestroyOnLoad(gameObject);
            _instance = this;
            LoadingNetwork.Bind(this);
        }

        private void OnDestroy()
        {
            LoadingNetwork.Unbind();
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            if (_pipeline == null) return;

            _pipeline.Tick(Time.unscaledDeltaTime);

            int idx = _pipeline.CurrentIndex;
            if (idx != _lastSeenStageIndex)
            {
                _lastSeenStageIndex = idx;
                LoadingStageManagerLocalFlags.LocalFinishedCurrentPhase = false;
            }

            if (_localIsHost && _pipeline.IsRunning)
                BroadcastProgress();

            if (_localIsHost && _doneBroadcast && _doneResendsLeft > 0 &&
                Time.unscaledTime >= _nextDoneResendTime)
            {
                _doneResendsLeft--;
                _nextDoneResendTime = Time.unscaledTime + DoneResendInterval;
                LoadingNetwork.SendDone();
            }
        }

        private void BroadcastProgress()
        {
            if (Time.unscaledTime < _nextBroadcastTime) return;
            _nextBroadcastTime = Time.unscaledTime + BroadcastInterval;

            var def = LoadingPhases.Get(_pipeline.CurrentIndex);
            if (def == null) return;

            var current = _pipeline.GetStageAt(_pipeline.CurrentIndex);
            float progress = current != null ? current.GetProgress() : 0f;

            int ready = _expectedClients;
            var serverAwait = current as ServerAwaitLoadingStage;
            if (serverAwait != null)
                ready = serverAwait.ReadyCount;

            if (serverAwait != null && serverAwait.HasLocalClientAcked)
                LoadingStageManagerLocalFlags.LocalFinishedCurrentPhase = true;

            LoadingNetwork.SendPhase(
                _pipeline.CurrentIndex,
                def.Id,
                progress,
                ready,
                _expectedClients,
                def.FriendlyName,
                def.StatusName);
        }

        private void OnPipelineComplete()
        {
            if (PipelineCompleted)
            {
                // Second caller (e.g. client's local complete racing the
                // host's done). Nothing more to do.
                return;
            }

            PipelineCompleted = true;
            WorldStoryObjectSync.NotifyLoadingGoReceived();
            SceneManagerHook.ReleasePendingLoad();
            SceneLoadingHook.AllowLoad();

            if (!_localIsHost) return;
            if (_doneBroadcast) return;

            _doneBroadcast = true;
            LoadingNetwork.SendDone();
            _doneResendsLeft = DoneResendCount;
            _nextDoneResendTime = Time.unscaledTime + DoneResendInterval;
            DiagnosticLog.Info("LoadingStageManager: host pipeline complete; broadcast done; released deferred scene load.");
        }
    }
}
