using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

namespace MiSideMultiplayer.Runtime.Menu.Music
{
    internal static class MultiplayerMenuMusic
    {
        private const string ResourceName = "MiSideMultiplayer.Runtime.Menu.Music.MultiplayerPiano.ogg";
        private const string CacheFileName = "MultiplayerPiano.ogg";
        private const string AudioObjectPath = "MenuGame/Sounds/Music";
        private const string SoundsObjectPath = "MenuGame/Sounds";
        private const string MultiplayerSourceName = "MultiplayerMusic";
        private const float FadeDuration = 1.5f;

        private static AudioSource _source;
        private static AudioSource _multiplayerSource;
        private static AudioClip _originalClip;
        private static float _originalVolume = 1f;
        private static bool _originalLoop;
        private static bool _originalMute;
        private static bool _capturedOriginal;
        private static bool _inMultiplayer;

        private static AudioClip _multiplayerClip;
        private static bool _multiplayerLoadStarted;

        private static MonoBehaviour _runnerBehaviour;
        private static MusicRunner _runner;

        public static bool IsInMultiplayer { get { return _inMultiplayer; } }

        public static void EnterMultiplayer()
        {
            bool wasInMultiplayer = _inMultiplayer;
            _inMultiplayer = true;

            EnsureSource();
            EnsureMultiplayerSource();
            CaptureOriginalIfNeeded();
            SilenceOriginalForMultiplayer();

            if (_multiplayerClip == null)
            {
                StartLoadMultiplayerClip();
                return;
            }

            if (_multiplayerSource == null)
            {
                DiagnosticLog.Warning("MultiplayerMenuMusic: dedicated MultiplayerMusic AudioSource is unavailable.");
                return;
            }

            if (!wasInMultiplayer || _multiplayerSource.clip != _multiplayerClip ||
                !IsPlaying(_multiplayerSource))
                ApplyMultiplayerClip();
            else
                FadeSource(_multiplayerSource, 1f, false, null);
        }

        public static void ExitMultiplayer()
        {
            if (!_inMultiplayer) return;
            _inMultiplayer = false;

            EnsureSource();
            if (_source != null)
            {
                if (!_capturedOriginal) CaptureOriginalIfNeeded();
                CancelFade(_source);

                // The vanilla source kept its clip and playback state while
                // muted. If the game has not started it yet, restart its
                // existing clip when returning to a normal menu.
                try
                {
                    if (_source.clip == null && _originalClip != null)
                    {
                        _source.clip = _originalClip;
                        _source.loop = _originalLoop;
                    }
                    if (_source.clip != null && !_source.isPlaying)
                        _source.Play();
                }
                catch { }

                if (_originalMute)
                {
                    _source.mute = true;
                    _source.volume = _originalVolume;
                    OnOriginalMusicRestored();
                }
                else
                {
                    _source.mute = false;
                    _source.volume = 0f;
                    FadeSource(_source, _originalVolume, false, OnOriginalMusicRestored);
                }
            }
            else
            {
                _capturedOriginal = false;
            }

            EnsureMultiplayerSource();
            if (_multiplayerSource != null)
                FadeSource(_multiplayerSource, 0f, true, null);
        }

        private static void EnsureSource()
        {
            if (_source != null) return;

            try
            {
                GameObject go = GameObject.Find(AudioObjectPath);
                if (go == null)
                {
                    DiagnosticLog.Warning("MultiplayerMenuMusic: '" + AudioObjectPath + "' not found.");
                    return;
                }
                _source = go.GetComponent<AudioSource>();
                if (_source == null)
                    DiagnosticLog.Warning("MultiplayerMenuMusic: AudioSource missing on '" + AudioObjectPath + "'.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerMenuMusic: EnsureSource failed: " + ex.Message);
            }
        }

        private static void EnsureMultiplayerSource()
        {
            if (_multiplayerSource != null) return;

            try
            {
                GameObject go = GameObject.Find(SoundsObjectPath + "/" + MultiplayerSourceName);
                Transform soundsRoot = null;
                try
                {
                    GameObject sounds = GameObject.Find(SoundsObjectPath);
                    if (sounds != null) soundsRoot = sounds.transform;
                }
                catch { }

                if (go == null)
                {
                    if (soundsRoot == null && _source != null)
                        soundsRoot = _source.transform.parent;
                    if (soundsRoot == null)
                    {
                        DiagnosticLog.Warning("MultiplayerMenuMusic: MenuGame/Sounds is unavailable; cannot create dedicated source.");
                        return;
                    }

                    go = new GameObject(MultiplayerSourceName);
                    go.transform.SetParent(soundsRoot, false);
                }

                _multiplayerSource = go.GetComponent<AudioSource>();
                if (_multiplayerSource == null)
                    _multiplayerSource = go.AddComponent<AudioSource>();

                _multiplayerSource.playOnAwake = false;
                _multiplayerSource.loop = true;
                _multiplayerSource.spatialBlend = 0f;
                _multiplayerSource.volume = 0f;

                if (_source != null)
                {
                    try { _multiplayerSource.outputAudioMixerGroup = _source.outputAudioMixerGroup; } catch { }
                    try { _multiplayerSource.pitch = _source.pitch; } catch { }
                    try { _multiplayerSource.priority = _source.priority; } catch { }
                    try { _multiplayerSource.ignoreListenerPause = _source.ignoreListenerPause; } catch { }
                    try { _multiplayerSource.ignoreListenerVolume = _source.ignoreListenerVolume; } catch { }
                    try { _multiplayerSource.bypassEffects = _source.bypassEffects; } catch { }
                    try { _multiplayerSource.bypassListenerEffects = _source.bypassListenerEffects; } catch { }
                    try { _multiplayerSource.bypassReverbZones = _source.bypassReverbZones; } catch { }
                }

                DiagnosticLog.Info("MultiplayerMenuMusic: dedicated AudioSource '" +
                                   MultiplayerSourceName + "' is ready beside the vanilla Music source.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerMenuMusic: dedicated source creation failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void CaptureOriginalIfNeeded()
        {
            if (_capturedOriginal || _source == null) return;
            _capturedOriginal = true;
            _originalClip = _source.clip;
            _originalVolume = _source.volume;
            _originalLoop = _source.loop;
            _originalMute = _source.mute;
            DiagnosticLog.Info("MultiplayerMenuMusic: preserved vanilla clip '" +
                               (_originalClip != null ? _originalClip.name : "<null>") +
                               "' at volume " + _originalVolume + ".");
        }

        private static void SilenceOriginalForMultiplayer(bool cancelFade = true)
        {
            if (_source == null) return;
            if (cancelFade) CancelFade(_source);
            try
            {
                _source.volume = 0f;
                _source.mute = true;
            }
            catch { }
        }

        public static void LateTick()
        {
            // Other menu scripts can reset the native source after a location
            // transition. Reassert silence late each frame so it cannot leak
            // beneath the dedicated multiplayer track, including while that
            // track is still being loaded.
            if (_inMultiplayer)
                SilenceOriginalForMultiplayer(cancelFade: false);
        }

        private static void CancelFade(AudioSource source)
        {
            if (source == null || _runner == null) return;
            try { _runner.Cancel(source); } catch { }
        }

        private static void ApplyMultiplayerClip()
        {
            if (!_inMultiplayer || _multiplayerSource == null || _multiplayerClip == null) return;

            float carry = CurrentPhase(_source);
            if (carry <= 0f && IsPlaying(_multiplayerSource))
                carry = CurrentPhase(_multiplayerSource);

            bool alreadyPlayingClip = _multiplayerSource.clip == _multiplayerClip &&
                                      IsPlaying(_multiplayerSource);
            if (!alreadyPlayingClip)
            {
                try
                {
                    _multiplayerSource.Stop();
                    _multiplayerSource.clip = _multiplayerClip;
                    _multiplayerSource.loop = true;
                    _multiplayerSource.volume = 0f;
                    _multiplayerSource.time = WrapToClipLength(carry, _multiplayerClip);
                    _multiplayerSource.Play();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MultiplayerMenuMusic: could not start dedicated track: " + ex.Message);
                    return;
                }
            }

            SilenceOriginalForMultiplayer();
            FadeSource(_multiplayerSource, 1f, false, null);

            DiagnosticLog.Info("MultiplayerMenuMusic: playing MultiplayerPiano.ogg through dedicated '" +
                               MultiplayerSourceName + "' AudioSource.");
        }

        private static void FadeSource(AudioSource source, float targetVolume,
                                       bool stopAtEnd, Action onComplete)
        {
            if (source == null) return;

            EnsureRunner();
            if (_runner == null)
            {
                source.volume = targetVolume;
                if (stopAtEnd) source.Stop();
                if (onComplete != null) onComplete();
                return;
            }

            _runner.Begin(source, targetVolume, FadeDuration, stopAtEnd, onComplete);
        }

        private static void EnsureRunner()
        {
            if (_runner != null && _runnerBehaviour != null) return;

            try
            {
                GameObject runnerGo = new GameObject("MultiplayerMenuMusicRunner");
                UnityEngine.Object.DontDestroyOnLoad(runnerGo);
                _runner = runnerGo.AddComponent<MusicRunner>();
                _runnerBehaviour = _runner;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerMenuMusic: cannot create runner: " + ex.Message);
            }
        }

        private static bool IsPlaying(AudioSource source)
        {
            try { return source != null && source.isPlaying; }
            catch { return false; }
        }

        private static float CurrentPhase(AudioSource source)
        {
            if (source == null) return 0f;
            try { return source.isPlaying ? source.time : 0f; }
            catch { return 0f; }
        }

        private static float WrapToClipLength(float time, AudioClip clip)
        {
            if (clip == null || clip.length <= 0.01f) return 0f;
            float wrapped = time % clip.length;
            return wrapped < 0f ? wrapped + clip.length : wrapped;
        }

        private static void OnOriginalMusicRestored()
        {
            if (_inMultiplayer) return;
            _capturedOriginal = false;
        }

        private static void StartLoadMultiplayerClip()
        {
            if (_multiplayerLoadStarted) return;
            _multiplayerLoadStarted = true;

            EnsureRunner();
            if (_runner == null)
            {
                DiagnosticLog.Warning("MultiplayerMenuMusic: no runner available to load the ogg.");
                _multiplayerLoadStarted = false;
                return;
            }

            _runner.BeginLoad();
        }

        internal static void OnClipLoaded(AudioClip clip)
        {
            _multiplayerLoadStarted = false;
            if (clip == null)
            {
                DiagnosticLog.Warning("MultiplayerMenuMusic: loaded clip was null.");
                return;
            }

            clip.name = "MultiplayerPiano";
            _multiplayerClip = clip;
            DiagnosticLog.Info("MultiplayerMenuMusic: loaded MultiplayerPiano.ogg (" +
                               clip.length.ToString("F1") + "s).");

            if (_inMultiplayer)
            {
                EnsureSource();
                EnsureMultiplayerSource();
                CaptureOriginalIfNeeded();
                ApplyMultiplayerClip();
            }
        }

        internal static string PrepareFilePath()
        {
            try { return ExtractResourceToDisk(); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerMenuMusic: extracting ogg failed: " + ex.Message);
                return null;
            }
        }

        private static string ExtractResourceToDisk()
        {
            string dir = Path.Combine(BepInEx.Paths.PluginPath, "MiSideMultiplayer", "Cache");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string outputPath = Path.Combine(dir, CacheFileName);
            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                return outputPath;

            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) return null;

                using (FileStream file = File.Create(outputPath))
                {
                    byte[] buffer = new byte[8192];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        file.Write(buffer, 0, read);
                }
            }
            return outputPath;
        }
    }

    /// <summary>
    /// Persistent loader and unscaled cross-fade driver. Independent fades
    /// let the native menu track and dedicated multiplayer track cross-fade
    /// without replacing the native clip.
    /// </summary>
    internal sealed class MusicRunner : MonoBehaviour
    {
        private sealed class Fade
        {
            public AudioSource Target;
            public float StartVolume;
            public float TargetVolume;
            public float Duration;
            public float Elapsed;
            public bool StopAtEnd;
            public Action OnComplete;
        }

        private readonly List<Fade> _fades = new List<Fade>();
        private UnityWebRequest _webRequest;
        private bool _loading;

        public void Begin(AudioSource target, float targetVolume, float duration,
                          bool stopAtEnd, Action onComplete)
        {
            if (target == null) return;

            for (int i = _fades.Count - 1; i >= 0; i--)
            {
                if (_fades[i].Target == target)
                    _fades.RemoveAt(i);
            }

            _fades.Add(new Fade
            {
                Target = target,
                StartVolume = target.volume,
                TargetVolume = targetVolume,
                Duration = duration > 0.01f ? duration : 0.01f,
                Elapsed = 0f,
                StopAtEnd = stopAtEnd,
                OnComplete = onComplete
            });
        }

        public void Cancel(AudioSource target)
        {
            if (target == null) return;
            for (int i = _fades.Count - 1; i >= 0; i--)
            {
                if (_fades[i].Target == target)
                    _fades.RemoveAt(i);
            }
        }

        public void BeginLoad()
        {
            if (_loading || _webRequest != null) return;

            string path = MultiplayerMenuMusic.PrepareFilePath();
            if (string.IsNullOrEmpty(path))
            {
                DiagnosticLog.Warning("MusicRunner: no path to load from.");
                return;
            }

            string url = "file:///" + path.Replace("\\", "/");
            try
            {
                _webRequest = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.OGGVORBIS);
                _webRequest.SendWebRequest();
                _loading = true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MusicRunner: UnityWebRequest construction failed: " + ex.Message);
                _webRequest = null;
                _loading = false;
            }
        }

        private void Update()
        {
            TickLoad();
            TickFades();
        }

        private void TickLoad()
        {
            if (!_loading || _webRequest == null) return;

            try
            {
                if (!_webRequest.isDone) return;

                _loading = false;
                bool success = false;
                try { success = _webRequest.result == UnityWebRequest.Result.Success; }
                catch { success = string.IsNullOrEmpty(_webRequest.error); }

                if (!success)
                {
                    DiagnosticLog.Warning("MusicRunner: UnityWebRequest error: " + _webRequest.error);
                }
                else
                {
                    AudioClip clip = null;
                    try { clip = DownloadHandlerAudioClip.GetContent(_webRequest); }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Warning("MusicRunner: GetContent threw: " + ex.Message);
                    }
                    MultiplayerMenuMusic.OnClipLoaded(clip);
                }

                try { _webRequest.Dispose(); } catch { }
                _webRequest = null;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MusicRunner: UnityWebRequest poll threw: " + ex.Message);
                _loading = false;
                try { _webRequest.Dispose(); } catch { }
                _webRequest = null;
            }
        }

        private void TickFades()
        {
            for (int i = _fades.Count - 1; i >= 0; i--)
            {
                Fade fade = _fades[i];
                if (fade.Target == null)
                {
                    _fades.RemoveAt(i);
                    continue;
                }

                fade.Elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(fade.Elapsed / fade.Duration);
                fade.Target.volume = Mathf.Lerp(fade.StartVolume, fade.TargetVolume, t);
                if (t < 1f) continue;

                fade.Target.volume = fade.TargetVolume;
                if (fade.StopAtEnd && fade.TargetVolume <= 0f)
                {
                    try { fade.Target.Stop(); } catch { }
                }

                Action onComplete = fade.OnComplete;
                _fades.RemoveAt(i);
                if (onComplete != null)
                {
                    try { onComplete(); } catch { }
                }
            }
        }
    }
}
