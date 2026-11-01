using System;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu.UI
{
    public sealed class CanvasGroupFader : MonoBehaviour
    {
        public float FadeDuration = 0.25f;

        private CanvasGroup _group;
        private float       _currentAlpha;
        private float       _targetAlpha;
        private bool        _initialized;

        private Transform _watchTransform;
        private Vector3   _hiddenPosition;
        private float     _visibleThreshold = 5f;

        // Diagnostic.
        private float   _diagLastLogTime;
        private Vector3 _diagLastPos;
        private const float DiagInterval = 1.0f;
        private const float DiagDelta = 1.0f;

        public void Initialize(CanvasGroup group, bool startVisible)
        {
            _group        = group;
            _currentAlpha = startVisible ? 1f : 0f;
            _targetAlpha  = _currentAlpha;
            _initialized  = true;
            Apply();
        }

        public void SetVisible(bool visible)
        {
            if (_watchTransform != null) return;
            _targetAlpha = visible ? 1f : 0f;
        }

        public void SnapTo(bool visible)
        {
            if (_watchTransform != null) return;
            _targetAlpha  = visible ? 1f : 0f;
            _currentAlpha = _targetAlpha;
            Apply();
        }

        public void SetWatchPosition(Transform target, Vector3 hiddenPosition, float threshold = 5f)
        {
            _watchTransform   = target;
            _hiddenPosition   = hiddenPosition;
            _visibleThreshold = threshold;
            if (target != null) _diagLastPos = target.localPosition;
        }

        private void Update()
        {
            if (!_initialized || _group == null) return;

            if (_watchTransform != null)
            {
                Vector3 current = _watchTransform.localPosition;

                // Diagnostic: log position changes at most once per second.
                if (Time.unscaledTime - _diagLastLogTime > DiagInterval &&
                    Vector3.Distance(current, _diagLastPos) > DiagDelta)
                {
                    _diagLastLogTime = Time.unscaledTime;
                    _diagLastPos = current;
                    float d = Vector3.Distance(current, _hiddenPosition);
                    DiagnosticLog.Info("CanvasGroupFader['" + _watchTransform.name +
                                       "']: localPos=" + current.ToString("F1") +
                                       " hiddenPos=" + _hiddenPosition.ToString("F1") +
                                       " dist=" + d.ToString("F1") +
                                       " threshold=" + _visibleThreshold.ToString("F1"));
                }

                float distance = Vector3.Distance(current, _hiddenPosition);
                _targetAlpha = (distance > _visibleThreshold) ? 1f : 0f;
            }

            if (Mathf.Approximately(_currentAlpha, _targetAlpha)) return;

            float step = Time.unscaledDeltaTime / Mathf.Max(0.01f, FadeDuration);
            _currentAlpha = Mathf.MoveTowards(_currentAlpha, _targetAlpha, step);
            Apply();
        }

        private void Apply()
        {
            if (_group == null) return;
            _group.alpha          = _currentAlpha;
            _group.interactable   = _currentAlpha > 0.99f;
            _group.blocksRaycasts = _currentAlpha > 0.01f;
        }
    }
}