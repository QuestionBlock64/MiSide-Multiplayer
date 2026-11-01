using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.Runtime.Menu.UI
{
    /// <summary>
    /// Re-applies a target color to a Text component every LateUpdate, so
    /// any UI_Colors or similar palette driver on the parent chain can't
    /// override it.
    /// </summary>
    public sealed class TextColorLock : MonoBehaviour
    {
        private Text  _text;
        private Color _target;

        public void Initialize(Text text, Color target)
        {
            _text   = text;
            _target = target;
            Apply();
        }

        public void SetColor(Color c)
        {
            _target = c;
            Apply();
        }

        private void LateUpdate()
        {
            Apply();
        }

        private void Apply()
        {
            if (_text == null) return;
            Color current = _text.color;
            if (current.r != _target.r || current.g != _target.g ||
                current.b != _target.b || current.a != _target.a)
            {
                _text.color = _target;
            }
        }
    }
}