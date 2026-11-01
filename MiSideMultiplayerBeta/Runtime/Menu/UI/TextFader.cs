using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.Runtime.Menu.UI
{
    /// <summary>
    /// Fades a Text component's alpha toward a target over a duration.
    /// Tick-driven (no coroutines) so it works cleanly under Il2CppInterop.
    /// </summary>
    public sealed class TextFader : MonoBehaviour
    {
        private Text  _text;
        private float _startAlpha;
        private float _targetAlpha;
        private float _duration;
        private float _elapsed;
        private bool  _fading;
        private Color _baseColor;

        public bool IsFading { get { return _fading; } }

        public void Initialize(Text textComponent)
        {
            _text = textComponent;
            if (_text != null) _baseColor = _text.color;
        }

        public void SetAlpha(float alpha)
        {
            if (_text == null) return;
            _startAlpha = alpha;
            _targetAlpha = alpha;
            _fading = false;
            _baseColor = _text.color;
            _baseColor.a = alpha;
            _text.color = _baseColor;
        }

        public void FadeTo(float targetAlpha, float duration)
        {
            if (_text == null) return;
            _baseColor   = _text.color;
            _startAlpha  = _baseColor.a;
            _targetAlpha = targetAlpha;
            _duration    = Mathf.Max(0.01f, duration);
            _elapsed     = 0f;
            _fading      = true;
        }

        private void Update()
        {
            if (!_fading || _text == null) return;

            _elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_elapsed / _duration);
            float a = Mathf.Lerp(_startAlpha, _targetAlpha, t);

            Color c = _baseColor;
            c.a = a;
            _text.color = c;

            if (t >= 1f)
            {
                _fading = false;
                c.a = _targetAlpha;
                _text.color = c;
            }
        }
    }
}