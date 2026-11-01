using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace MiSideMultiplayer.Runtime.UI
{
    internal static class MultiplayerConnectingOverlay
    {
        private enum Phase { Hidden, Opening, Closing }

        private static Phase  _phase = Phase.Hidden;
        private static string _title    = "CONNECTING";
        private static string _subtitle = "PLEASE WAIT";
        private static float  _progress = -1f;
        private static float  _spin     = 0f;
        private static float  _alpha    = 0f;

        private static float  _shownAt;
        private static float  _closingAt;
        private static bool   _holdReleased = true;
        private static float  _holdUntil;

        private static readonly List<Action> _pendingOnHidden = new List<Action>();
        private static readonly List<Action> _pendingOnFading = new List<Action>();

        private const int    RingSize      = 96;
        private const float  RingThickness = 8f;
        private const float  SpinSpeed     = 5.2f;

        private const int    MarginX       = 40;
        private const int    MarginY       = 40;
        private const int    GapToRing     = 24;

        // Minimum time the overlay is visible.
        private const float  MinimumDisplayTime = 0.8f;

        // Safety net.
        private const float  MaxDisplayTime = 15.0f;

        private const float  FadeInDuration  = 0.25f;
        private const float  FadeOutDuration = 0.4f;

        // When the fade-out has dropped below this alpha, deferred actions
        // queued via RunWhenFading fire.
        private const float  FadeEarlyTrigger = 0.35f;

        private const float  DofFocusDistance = 3.5f;
        private const float  DofAperture      = 8.0f;
        private const float  DofFocalLength   = 30f;

        private static Texture2D _ringTex;
        private static Color32[] _pixels;

        private const float LobbyScanSpinSpeed = 5.2f;
        private static float _lobbyScanSpin;
        private static float _lobbyScanAlpha;
        private static float _lobbyScanTargetAlpha;
        private static Texture2D _lobbyScanRingTex;
        private static Color32[] _lobbyScanRingPixels;
        private static Texture2D _lobbyScanTrackTex;
        private static Color32[] _lobbyScanTrackPixels;

        private static GameObject        _blurRoot;
        private static PostProcessVolume _blurVolume;
        private static bool              _blurInitFailed;

        // ── Public API ──────────────────────────────────────────────────────

        public static void Show(string title, string subtitle)
        {
            _title    = string.IsNullOrEmpty(title) ? "CONNECTING" : title.ToUpperInvariant();
            _subtitle = string.IsNullOrEmpty(subtitle) ? "PLEASE WAIT" : subtitle.ToUpperInvariant();
            _progress = -1f;
            _spin     = 0f;

            _phase        = Phase.Opening;
            _alpha        = 0f;
            _shownAt      = Time.unscaledTime;
            _holdUntil    = _shownAt + MinimumDisplayTime;
            _holdReleased = false;

            EnsureBlurVolume();
            SetBlurWeight(1f);
        }

        public static void SetProgress(float progress)
        {
            _progress = progress < 0f ? -1f : Mathf.Clamp01(progress);
        }

        /// <summary>Show a small, non-blocking spinner while the lobby directory is scanned.</summary>
        public static void SetLobbyScanVisible(bool visible)
        {
            _lobbyScanTargetAlpha = visible ? 1f : 0f;
            if (visible && _lobbyScanRingTex == null)
                EnsureLobbyScanRingTexture();
        }

        /// <summary>Signal that the underlying work is complete.</summary>
        public static void ReleaseHold()
        {
            _holdReleased = true;
        }

        /// <summary>Cancel immediately (error, back-out, host-left).</summary>
        public static void Hide()
        {
            _holdReleased = true;
            _holdUntil    = 0f;
        }

        public static bool IsVisible { get { return _phase != Phase.Hidden; } }

        /// <summary>
        /// Queue an action to run once the overlay has fully faded out.
        /// Use for cleanup that must happen after the overlay is gone.
        /// </summary>
        public static void RunWhenHidden(Action action)
        {
            if (action == null) return;
            if (_phase == Phase.Hidden)
            {
                try { action(); }
                catch (Exception ex) { DiagnosticLog.Warning("ConnectingOverlay.RunWhenHidden immediate: " + ex.Message); }
                return;
            }
            _pendingOnHidden.Add(action);
        }

        /// <summary>
        /// Queue an action to run when the fade-out has dropped below the
        /// early trigger threshold. Use for navigation so the menu transition
        /// happens under the (partially) visible overlay rather than after it.
        /// </summary>
        public static void RunWhenFading(Action action)
        {
            if (action == null) return;
            if (_phase == Phase.Hidden)
            {
                try { action(); }
                catch (Exception ex) { DiagnosticLog.Warning("ConnectingOverlay.RunWhenFading immediate: " + ex.Message); }
                return;
            }
            _pendingOnFading.Add(action);
        }

        public static void Tick()
        {
            float now = Time.unscaledTime;

            switch (_phase)
            {
                case Phase.Hidden:
                    break;

                case Phase.Opening:
                    if (_alpha < 1f)
                    {
                        _alpha += Time.unscaledDeltaTime / FadeInDuration;
                        if (_alpha > 1f) _alpha = 1f;
                    }

                    _spin += Time.unscaledDeltaTime * SpinSpeed;
                    if (_spin > Mathf.PI * 2f) _spin -= Mathf.PI * 2f;

                    if (now - _shownAt >= MaxDisplayTime)
                    {
                        _holdReleased = true;
                        _holdUntil    = 0f;
                        DiagnosticLog.Warning("ConnectingOverlay: max display time reached; " +
                                              "forcing close (ReleaseHold was never called?).");
                    }

                    if (_holdReleased && now >= _holdUntil)
                    {
                        _phase     = Phase.Closing;
                        _closingAt = now;
                    }
                    break;

                case Phase.Closing:
                    float t = (now - _closingAt) / FadeOutDuration;
                    float prevAlpha = _alpha;
                    _alpha = 1f - Mathf.Clamp01(t);

                    if (prevAlpha > FadeEarlyTrigger && _alpha <= FadeEarlyTrigger)
                    {
                        FlushQueue(_pendingOnFading, "RunWhenFading");
                    }

                    if (_alpha <= 0f)
                    {
                        _alpha = 0f;
                        _phase = Phase.Hidden;
                        SetBlurWeight(0f);
                        FlushQueue(_pendingOnHidden, "RunWhenHidden");
                    }
                    break;
            }

            _lobbyScanAlpha = Mathf.MoveTowards(
                _lobbyScanAlpha,
                _lobbyScanTargetAlpha,
                Time.unscaledDeltaTime / 0.2f);
            _lobbyScanSpin += Time.unscaledDeltaTime * LobbyScanSpinSpeed;
            if (_lobbyScanSpin > Mathf.PI * 2f) _lobbyScanSpin -= Mathf.PI * 2f;
        }

        private static void FlushQueue(List<Action> queue, string tag)
        {
            if (queue.Count == 0) return;

            var copy = queue.ToArray();
            queue.Clear();

            for (int i = 0; i < copy.Length; i++)
            {
                try { copy[i]?.Invoke(); }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("ConnectingOverlay." + tag + " deferred: " + ex.Message);
                }
            }
        }

        // ── Draw ────────────────────────────────────────────────────────────

        public static void Draw()
        {
            if (_phase != Phase.Hidden && _alpha > 0.001f)
            {
                int w = Screen.width;
                int h = Screen.height;

                EnsureRingTexture();
                UpdateRingTexture();

                const int titleFont    = 42;
                const int subtitleFont = 20;

                var titleStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleRight,
                    fontSize  = titleFont,
                    fontStyle = FontStyle.Bold
                };
                titleStyle.normal.textColor = new Color(1f, 1f, 1f, _alpha);

                var subtitleStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleRight,
                    fontSize  = subtitleFont,
                    fontStyle = FontStyle.Bold
                };
                subtitleStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f, _alpha);

                Vector2 titleSize = titleStyle.CalcSize(new GUIContent(_title));

                float blockWidth  = titleSize.x + GapToRing + RingSize;

                float blockRight  = w - MarginX;
                float blockBottom = h - MarginY;

                float blockLeft = blockRight - blockWidth;
                float blockTop  = blockBottom - RingSize;

                float ringCenterY = blockTop + RingSize * 0.5f;
                float titleY      = ringCenterY - (titleFont * 0.8f);

                float ringX = blockLeft + titleSize.x + GapToRing;
                float ringY = ringCenterY - RingSize * 0.5f;

                GUI.Label(
                    new Rect(blockLeft, titleY, titleSize.x + 2f, titleFont * 1.4f),
                    _title,
                    titleStyle);

                Color prevColor = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, _alpha);

                Matrix4x4 saved = GUI.matrix;
                GUIUtility.RotateAroundPivot(-_spin * Mathf.Rad2Deg,
                                             new Vector2(ringX + RingSize * 0.5f,
                                                         ringY + RingSize * 0.5f));
                GUI.DrawTexture(new Rect(ringX, ringY, RingSize, RingSize), _ringTex);
                GUI.matrix = saved;

                GUI.color = prevColor;

                GUI.Label(
                    new Rect(blockLeft, titleY + titleFont * 1.1f, titleSize.x + 2f, subtitleFont * 1.4f),
                    _subtitle,
                    subtitleStyle);
            }

            DrawLobbyScanIndicator();
        }

        private static void DrawLobbyScanIndicator()
        {
            if (_lobbyScanAlpha <= 0.001f) return;

            EnsureLobbyScanRingTexture();
            if (_lobbyScanRingTex == null || _lobbyScanTrackTex == null) return;

            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleRight,
                fontSize = 42,
                fontStyle = FontStyle.Bold
            };
            style.normal.textColor = new Color(1f, 1f, 1f, _lobbyScanAlpha);

            const string label = "FINDING LOBBIES...";
            Vector2 labelSize = style.CalcSize(new GUIContent(label));
            float ringX = Screen.width - MarginX - RingSize;
            float ringY = Screen.height - MarginY - RingSize;
            float labelX = ringX - GapToRing - labelSize.x;
            float labelY = ringY + RingSize * 0.5f - 42f * 0.8f;

            GUI.Label(
                new Rect(labelX, labelY, labelSize.x + 2f, 42f * 1.4f),
                label,
                style);

            Color previousColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, _lobbyScanAlpha);
            Matrix4x4 previousMatrix = GUI.matrix;
            var ringRect = new Rect(ringX, ringY, RingSize, RingSize);
            GUI.DrawTexture(ringRect, _lobbyScanTrackTex);
            GUIUtility.RotateAroundPivot(
                -_lobbyScanSpin * Mathf.Rad2Deg,
                new Vector2(ringX + RingSize * 0.5f, ringY + RingSize * 0.5f));
            GUI.DrawTexture(ringRect, _lobbyScanRingTex);
            GUI.matrix = previousMatrix;
            GUI.color = previousColor;
        }

        private static void EnsureLobbyScanRingTexture()
        {
            if (_lobbyScanRingTex != null && _lobbyScanTrackTex != null) return;
            try
            {
                if (_lobbyScanRingTex == null)
                {
                    _lobbyScanRingTex = new Texture2D(RingSize, RingSize, TextureFormat.RGBA32, false)
                    {
                        name = "MiSideLobbyScanArc",
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    _lobbyScanRingPixels = new Color32[RingSize * RingSize];
                }
                if (_lobbyScanTrackTex == null)
                {
                    _lobbyScanTrackTex = new Texture2D(RingSize, RingSize, TextureFormat.RGBA32, false)
                    {
                        name = "MiSideLobbyScanTrack",
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    _lobbyScanTrackPixels = new Color32[RingSize * RingSize];
                }

                // Match the native multiplayer loading indicator: a stationary
                // gray track with only its white quarter-arc rotating.
                float center = RingSize * 0.5f;
                float outer = RingSize * 0.5f - 2f;
                float inner = outer - RingThickness;
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
                            _lobbyScanRingPixels[index] = clear;
                            _lobbyScanTrackPixels[index] = clear;
                            continue;
                        }

                        float alpha = Mathf.Min(Mathf.Clamp01(outer - radius + 0.5f),
                                                Mathf.Clamp01(radius - inner + 0.5f));
                        if (alpha <= 0f)
                        {
                            _lobbyScanRingPixels[index] = clear;
                            _lobbyScanTrackPixels[index] = clear;
                            continue;
                        }

                        float angle = Mathf.Atan2(dy, dx);
                        if (angle < 0f) angle += Mathf.PI * 2f;
                        _lobbyScanTrackPixels[index] =
                            new Color32(gray.r, gray.g, gray.b, (byte)(gray.a * alpha));
                        _lobbyScanRingPixels[index] = angle < Mathf.PI * 0.5f
                            ? new Color32(white.r, white.g, white.b, (byte)(white.a * alpha))
                            : clear;
                    }
                }

                _lobbyScanRingTex.SetPixels32(_lobbyScanRingPixels);
                _lobbyScanRingTex.Apply(false, false);
                _lobbyScanTrackTex.SetPixels32(_lobbyScanTrackPixels);
                _lobbyScanTrackTex.Apply(false, false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("ConnectingOverlay: couldn't create the standard loading ring: " + ex.Message);
            }
        }

        // ── Blur volume ─────────────────────────────────────────────────────

        private static void EnsureBlurVolume()
        {
            if (_blurVolume != null) return;
            if (_blurInitFailed) return;

            try
            {
                _blurRoot = new GameObject("MiSideConnectingBlur");
                UnityEngine.Object.DontDestroyOnLoad(_blurRoot);
                _blurRoot.hideFlags = HideFlags.HideAndDontSave;

                TryMatchVolumeLayer(_blurRoot);

                _blurVolume = _blurRoot.AddComponent<PostProcessVolume>();
                if (_blurVolume == null)
                {
                    DiagnosticLog.Warning("ConnectingOverlay: PostProcessVolume AddComponent returned null.");
                    _blurInitFailed = true;
                    return;
                }

                _blurVolume.isGlobal = true;
                _blurVolume.priority = 1000f;
                _blurVolume.weight   = 0f;

                var profile = ScriptableObject.CreateInstance<PostProcessProfile>();
                if (profile == null)
                {
                    DiagnosticLog.Warning("ConnectingOverlay: PostProcessProfile CreateInstance returned null.");
                    _blurInitFailed = true;
                    return;
                }
                profile.name = "MiSideConnectingBlurProfile";
                profile.hideFlags = HideFlags.HideAndDontSave;

                var dof = profile.AddSettings<DepthOfField>();
                if (dof == null)
                {
                    DiagnosticLog.Warning("ConnectingOverlay: DepthOfField AddSettings returned null.");
                    _blurInitFailed = true;
                    return;
                }

                dof.enabled.overrideState = true;
                dof.enabled.value         = true;

                dof.focusDistance.overrideState = true;
                dof.focusDistance.value         = DofFocusDistance;

                dof.aperture.overrideState      = true;
                dof.aperture.value              = DofAperture;

                dof.focalLength.overrideState   = true;
                dof.focalLength.value           = DofFocalLength;

                dof.kernelSize.overrideState    = true;
                dof.kernelSize.value            = KernelSize.Small;

                _blurVolume.profile = profile;

                DiagnosticLog.Info("ConnectingOverlay: blur volume ready (focus=" +
                                   DofFocusDistance + ", aperture=" + DofAperture +
                                   ", focal=" + DofFocalLength + ", layer=" +
                                   _blurRoot.layer + ").");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("ConnectingOverlay: EnsureBlurVolume failed: " + ex.Message);
                _blurInitFailed = true;
                _blurVolume = null;
            }
        }

        private static void TryMatchVolumeLayer(GameObject root)
        {
            try
            {
                var cams = UnityEngine.Object.FindObjectsOfType<Camera>();
                if (cams == null) return;

                for (int i = 0; i < cams.Length; i++)
                {
                    var cam = cams[i];
                    if (cam == null) continue;

                    var layer = cam.GetComponent<PostProcessLayer>();
                    if (layer == null) continue;

                    int mask = layer.volumeLayer.value;

                    for (int bit = 0; bit < 32; bit++)
                    {
                        if ((mask & (1 << bit)) != 0)
                        {
                            root.layer = bit;
                            DiagnosticLog.Info("ConnectingOverlay: blur volume layer set to " +
                                               bit + " (cam '" + cam.gameObject.name +
                                               "', mask=0x" + mask.ToString("X8") + ").");
                            return;
                        }
                    }

                    DiagnosticLog.Warning("ConnectingOverlay: cam '" + cam.gameObject.name +
                                          "' has PostProcessLayer with empty volumeLayer mask " +
                                          "(0x" + mask.ToString("X8") + ").");
                }

                DiagnosticLog.Warning("ConnectingOverlay: no camera with PostProcessLayer found.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("ConnectingOverlay: TryMatchVolumeLayer failed: " + ex.Message);
            }
        }

        private static void SetBlurWeight(float weight)
        {
            if (_blurVolume == null) return;
            try { _blurVolume.weight = weight; } catch { }
        }

        // ── Ring texture generation ─────────────────────────────────────────

        private static void EnsureRingTexture()
        {
            if (_ringTex != null) return;

            _ringTex = new Texture2D(RingSize, RingSize, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp
            };
            _pixels = new Color32[RingSize * RingSize];
        }

        private static void UpdateRingTexture(bool forceIndeterminate = false)
        {
            if (_ringTex == null || _pixels == null) return;

            float cx     = RingSize * 0.5f;
            float cy     = RingSize * 0.5f;
            float outer  = RingSize * 0.5f - 2f;
            float inner  = outer - RingThickness;

            Color32 gray  = new Color32(120, 120, 120, 255);
            Color32 white = new Color32(255, 255, 255, 255);
            Color32 clear = new Color32(0, 0, 0, 0);

            bool  hasProgress = _progress >= 0f && !forceIndeterminate;
            float arcLen      = hasProgress ? Mathf.PI * 2f * _progress : 0f;

            for (int y = 0; y < RingSize; y++)
            {
                for (int x = 0; x < RingSize; x++)
                {
                    float dx = x + 0.5f - cx;
                    float dy = y + 0.5f - cy;
                    float r  = Mathf.Sqrt(dx * dx + dy * dy);

                    int idx = y * RingSize + x;

                    if (r < inner - 1f || r > outer + 1f)
                    {
                        _pixels[idx] = clear;
                        continue;
                    }

                    float edgeOuter = Mathf.Clamp01(outer - r + 0.5f);
                    float edgeInner = Mathf.Clamp01(r - inner + 0.5f);
                    float bandAlpha = Mathf.Min(edgeOuter, edgeInner);
                    if (bandAlpha <= 0f)
                    {
                        _pixels[idx] = clear;
                        continue;
                    }

                    float ang = Mathf.Atan2(dy, dx);
                    if (ang < 0f) ang += Mathf.PI * 2f;

                    Color32 c = gray;
                    if (!hasProgress)
                    {
                        const float IndeterminateArc = Mathf.PI * 0.5f;
                        if (ang < IndeterminateArc) c = white;
                    }
                    else
                    {
                        if (ang < arcLen) c = white;
                    }

                    byte a = (byte)(c.a * bandAlpha);
                    _pixels[idx] = new Color32(c.r, c.g, c.b, a);
                }
            }

            _ringTex.SetPixels32(_pixels);
            _ringTex.Apply(false, false);
        }
    }
}
