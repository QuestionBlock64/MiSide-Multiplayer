using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using MiSideMultiplayer.Runtime.Loading;

namespace MiSideMultiplayer.Runtime
{
    internal sealed class MultiplayerLoadingScene : MonoBehaviour
    {
        public MultiplayerLoadingScene(IntPtr ptr) : base(ptr) { }

        internal enum LoadState { Idle, Requested, Loading }

        internal static LoadState State = LoadState.Idle;
        internal static string TargetSceneName;
        internal static bool IsVisible
        {
            get { return _instance != null && State == LoadState.Loading; }
        }

        internal static bool HasRendered
        {
            get { return _instance != null && State == LoadState.Loading && _instance._hasRendered; }
        }


        private static MultiplayerLoadingScene _instance;
        private static PendingPhase _pendingPhase;
        private static bool _hasPendingPhase;
        private static bool _nativeLoadFired;

        private const int RingSize = 96;
        private const float RingThickness = 8f;
        private const float SpinSpeed = 5.2f;
        private const int MarginX = 40;
        private const int MarginY = 40;
        private const int GapToRing = 24;
        private const int TitleFontSize = 42;
        private const int SubtitleFontSize = 20;

        private int _stageIndex;
        private int _minimumVisibleStageIndex;
        private string _friendlyName = "Preparing...";
        private string _statusName = "Loaded Players";
        private int _readyCount;
        private int _expectedCount = 1;
        private float _progress = -1f;
        private float _spin;
        private bool _hasRendered;

        private Texture2D _ringTexture;
        private Color32[] _ringPixels;
        private Texture2D _ringTrackTexture;
        private Color32[] _ringTrackPixels;

        private sealed class PendingPhase
        {
            public int StageIndex;
            public float Progress;
            public int ReadyCount;
            public int ExpectedCount;
            public string FriendlyName;
            public string StatusName;
        }

        public static void Begin(string targetScene)
        {
            if (string.IsNullOrEmpty(targetScene))
            {
                DiagnosticLog.Warning("MultiplayerLoadingScene.Begin: empty target scene; ignoring.");
                return;
            }

            SceneLoadingHook.BeginMultiplayerLoad();
            SceneManagerHook.ResetForNewLoad();
            State = LoadState.Requested;
            TargetSceneName = targetScene;
            _pendingPhase = null;
            _hasPendingPhase = false;
            _nativeLoadFired = false;

            DiagnosticLog.Info("MultiplayerLoadingScene: armed for '" + targetScene +
                               "' (state=Requested).");
        }

        public static void StartNativeLoad()
        {
            if (_nativeLoadFired) return;
            if (string.IsNullOrEmpty(TargetSceneName)) return;
            if (State == LoadState.Idle) return;

            _nativeLoadFired = true;
            TriggerNativeLoad(TargetSceneName);
        }

        public static void Show()
        {
            if (_instance == null)
            {
                var go = new GameObject("MiSideMPLoadingScene");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _instance = go.AddComponent<MultiplayerLoadingScene>();
            }

            _instance.ResetForSession();

            if (_hasPendingPhase)
            {
                _instance.SetPhase(_pendingPhase.StageIndex,
                                   _pendingPhase.Progress,
                                   _pendingPhase.ReadyCount,
                                   _pendingPhase.ExpectedCount,
                                   _pendingPhase.FriendlyName,
                                   _pendingPhase.StatusName);
                _hasPendingPhase = false;
            }

            State = LoadState.Loading;
            DiagnosticLog.Info("MultiplayerLoadingScene: overlay shown (state=Loading, target='" +
                               (TargetSceneName ?? "<null>") + "').");
        }

        public static void ApplyPhase(int stageIndex,
                                      string phaseId,
                                      float progress,
                                      int readyCount,
                                      int expectedCount,
                                      string friendlyName,
                                      string statusName)
        {
            if (_instance == null)
            {
                _pendingPhase = new PendingPhase
                {
                    StageIndex = stageIndex,
                    Progress = progress,
                    ReadyCount = readyCount,
                    ExpectedCount = expectedCount,
                    FriendlyName = friendlyName,
                    StatusName = statusName
                };
                _hasPendingPhase = true;
                return;
            }

            _instance.SetPhase(stageIndex, progress, readyCount, expectedCount,
                               friendlyName, statusName);
        }

        public static void Abort()
        {
            if (State == LoadState.Idle && _instance == null && !_nativeLoadFired)
            {
                SceneManagerHook.CancelPendingLoad();
                TargetSceneName = null;
                return;
            }

            DiagnosticLog.Info("MultiplayerLoadingScene: Abort() from state=" + State + ".");
            SceneManagerHook.CancelPendingLoad();

            TargetSceneName = null;
            _pendingPhase = null;
            _hasPendingPhase = false;
            _nativeLoadFired = false;
            State = LoadState.Idle;

            if (_instance != null)
            {
                UnityEngine.Object.Destroy(_instance.gameObject);
                _instance = null;
            }
        }

        internal static void ShowWaitingForPlayers(int expectedCount)
        {
            LoadingPhaseDef phase = LoadingPhases.Get(1);
            if (phase == null) return;

            ApplyPhase(1, phase.Id, -1f, 0, Mathf.Max(1, expectedCount),
                       phase.FriendlyName, phase.StatusName);
        }

        private static void TriggerNativeLoad(string target)
        {
            try
            {
                BlackScreen blackScreen = UnityEngine.Object.FindObjectOfType<BlackScreen>();
                if (blackScreen == null)
                {
                    DiagnosticLog.Warning("MultiplayerLoadingScene: no BlackScreen in scene.");
                    return;
                }

                DiagnosticLog.Info("MultiplayerLoadingScene: requesting native load of '" +
                                   target + "' via BlackScreen.NextLevel.");
                blackScreen.NextLevel(target);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLoadingScene: BlackScreen.NextLevel threw: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_ringTexture != null)
                UnityEngine.Object.Destroy(_ringTexture);
            if (_ringTrackTexture != null)
                UnityEngine.Object.Destroy(_ringTrackTexture);
            if (_instance == this) _instance = null;
        }

        private void ResetForSession()
        {
            _minimumVisibleStageIndex = 0;
            _stageIndex = 0;
            _friendlyName = "Loading...";
            _statusName = "Loaded Players";
            _readyCount = 0;
            _expectedCount = 1;
            _progress = 0f;
            _spin = 0f;
            _hasRendered = false;
            UpdateRingTexture();
        }

        private void SetPhase(int stageIndex,
                              float progress,
                              int readyCount,
                              int expectedCount,
                              string friendlyName,
                              string statusName)
        {
            int normalizedStageIndex = Mathf.Max(0, stageIndex);
            if (normalizedStageIndex < _minimumVisibleStageIndex) return;

            _minimumVisibleStageIndex = normalizedStageIndex;
            _stageIndex = normalizedStageIndex;
            _progress = _stageIndex == 0 ? Mathf.Clamp01(progress) : -1f;
            _readyCount = Mathf.Max(0, readyCount);
            _expectedCount = Mathf.Max(1, expectedCount);
            _friendlyName = string.IsNullOrEmpty(friendlyName) ? "Preparing..." : friendlyName;
            _statusName = string.IsNullOrEmpty(statusName) ? "Loaded Players" : statusName;
            UpdateRingTexture();
        }

        private void Update()
        {
            if (_progress < 0f)
            {
                _spin += Time.unscaledDeltaTime * SpinSpeed;
                if (_spin > Mathf.PI * 2f) _spin -= Mathf.PI * 2f;
            }

            if (State == LoadState.Idle) return;
            if (string.IsNullOrEmpty(TargetSceneName)) return;

            try
            {
                bool worldReady = SceneManager.GetActiveScene().name == TargetSceneName;
                bool pipelineReady = LoadingStageManager.PipelineCompleted;

                if (worldReady && pipelineReady)
                    DestroySelf();
            }
            catch { }
        }

        private void DestroySelf()
        {
            DiagnosticLog.Info("MultiplayerLoadingScene: DestroySelf (state=" + State +
                               ", target='" + (TargetSceneName ?? "<null>") +
                               "', pipelineCompleted=" + LoadingStageManager.PipelineCompleted + ").");

            TargetSceneName = null;
            _nativeLoadFired = false;
            State = LoadState.Idle;

            if (_instance == this)
            {
                UnityEngine.Object.Destroy(gameObject);
                _instance = null;
            }
        }

        private void OnGUI()
        {
            if (State != LoadState.Loading) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            _hasRendered = true;
            int width = Screen.width;
            int height = Screen.height;
            if (width <= 0 || height <= 0) return;

            int oldDepth = GUI.depth;
            Color oldColor = GUI.color;
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.depth = -32000;
            GUI.color = Color.white;

            string title = _friendlyName;
            var definition = LoadingPhases.Get(_stageIndex);
            if (definition != null && _stageIndex <= 1 &&
                LoadingStageManagerLocalFlags.LocalFinishedCurrentPhase)
            {
                title = definition.PostFinishName;
            }

            var titleStyle = new GUIStyle
            {
                alignment = TextAnchor.MiddleRight,
                fontSize = TitleFontSize,
                fontStyle = FontStyle.Bold,
                wordWrap = false
            };
            titleStyle.normal.textColor = Color.white;

            var subtitleStyle = new GUIStyle
            {
                alignment = TextAnchor.MiddleRight,
                fontSize = SubtitleFontSize,
                fontStyle = FontStyle.Bold,
                wordWrap = false
            };
            subtitleStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f, 1f);

            float titleWidth = Mathf.Min(1200f,
                Mathf.Max(480f, titleStyle.CalcSize(new GUIContent(title)).x + 8f));
            float right = width - MarginX - RingSize - GapToRing;
            float centerY = height - MarginY - RingSize * 0.5f;

            GUI.Label(new Rect(right - titleWidth, centerY - TitleFontSize * 0.72f,
                               titleWidth, TitleFontSize * 1.45f), title, titleStyle);

            string subtitle = "(" + _statusName + ": " + _readyCount + "/" + _expectedCount + ")";
            GUI.Label(new Rect(right - titleWidth, centerY + TitleFontSize * 0.34f,
                               titleWidth, SubtitleFontSize * 1.55f), subtitle, subtitleStyle);

            EnsureRingTexture();
            if (_ringTexture != null && _ringTrackTexture != null)
            {
                float ringX = width - MarginX - RingSize;
                float ringY = height - MarginY - RingSize;
                var ringRect = new Rect(ringX, ringY, RingSize, RingSize);

                // Keep the dark track in screen space. Only the white arc
                // rotates during indeterminate loading.
                GUI.DrawTexture(ringRect, _ringTrackTexture);
                if (_progress < 0f)
                {
                    GUIUtility.RotateAroundPivot(-_spin * Mathf.Rad2Deg,
                                                 new Vector2(ringX + RingSize * 0.5f,
                                                             ringY + RingSize * 0.5f));
                }
                GUI.DrawTexture(ringRect, _ringTexture);
            }

            GUI.matrix = oldMatrix;
            GUI.color = oldColor;
            GUI.depth = oldDepth;
        }

        private void EnsureRingTexture()
        {
            if (_ringTexture != null) return;

            _ringTexture = new Texture2D(RingSize, RingSize, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            _ringPixels = new Color32[RingSize * RingSize];
            _ringTrackTexture = new Texture2D(RingSize, RingSize, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            _ringTrackPixels = new Color32[RingSize * RingSize];
            UpdateRingTexture();
        }

        private void UpdateRingTexture()
        {
            if (_ringTexture == null || _ringPixels == null ||
                _ringTrackTexture == null || _ringTrackPixels == null) return;

            float center = RingSize * 0.5f;
            float outer = RingSize * 0.5f - 2f;
            float inner = outer - RingThickness;
            float completedArc = _progress < 0f ? Mathf.PI * 0.5f
                : Mathf.PI * 2f * Mathf.Clamp01(_progress);

            Color32 gray = new Color32(120, 120, 120, 255);
            Color32 white = new Color32(255, 255, 255, 255);
            Color32 clear = new Color32(0, 0, 0, 0);

            for (int y = 0; y < RingSize; y++)
            {
                for (int x = 0; x < RingSize; x++)
                {
                    float dx = x + 0.5f - center;
                    float dy = y + 0.5f - center;
                    float radius = Mathf.Sqrt(dx * dx + dy * dy);
                    int index = y * RingSize + x;

                    if (radius < inner - 1f || radius > outer + 1f)
                    {
                        _ringPixels[index] = clear;
                        _ringTrackPixels[index] = clear;
                        continue;
                    }

                    float alpha = Mathf.Min(Mathf.Clamp01(outer - radius + 0.5f),
                                              Mathf.Clamp01(radius - inner + 0.5f));
                    if (alpha <= 0f)
                    {
                        _ringPixels[index] = clear;
                        _ringTrackPixels[index] = clear;
                        continue;
                    }

                    float angle = Mathf.Atan2(dy, dx);
                    if (angle < 0f) angle += Mathf.PI * 2f;
                    _ringTrackPixels[index] = new Color32(gray.r, gray.g, gray.b, (byte)(gray.a * alpha));
                    _ringPixels[index] = angle < completedArc
                        ? new Color32(white.r, white.g, white.b, (byte)(white.a * alpha))
                        : clear;
                }
            }

            _ringTexture.SetPixels32(_ringPixels);
            _ringTexture.Apply(false, false);
            _ringTrackTexture.SetPixels32(_ringTrackPixels);
            _ringTrackTexture.Apply(false, false);
        }
    }

    internal static class LoadingStageManagerLocalFlags
    {
        public static bool LocalFinishedCurrentPhase = false;
    }
}
