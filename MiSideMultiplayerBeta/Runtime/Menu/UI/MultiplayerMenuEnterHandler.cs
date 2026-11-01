using MiSideMultiplayer.API;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu.UI
{
    public sealed class MultiplayerMenuEnterHandler : MonoBehaviour
    {
        private Component _parentLocation;
        private bool _loggedFirstUpdate;

        private void Awake()
        {
            Transform t = transform;
            while (t != null && _parentLocation == null)
            {
                _parentLocation = t.GetComponent("MenuLocation");
                if (_parentLocation == null) t = t.parent;
            }

            if (_parentLocation == null)
                DiagnosticLog.Warning("MultiplayerMenuEnterHandler: no MenuLocation found up the chain from '" + gameObject.name + "'.");
        }

        private void Update()
        {
            if (!_loggedFirstUpdate)
            {
                _loggedFirstUpdate = true;
                DiagnosticLog.Info("MultiplayerMenuEnterHandler: first Update tick. activeInHierarchy=" + gameObject.activeInHierarchy);
            }

            MultiplayerMenu.TickLogin(Time.deltaTime);

            if (MultiplayerMenu.CurrentState != MultiplayerMenu.LoginState.NotLoggedIn) return;
            if (!gameObject.activeInHierarchy) return;

            if (_parentLocation != null)
            {
                bool active;
                try { active = QBLib.Fields.ReadBool(_parentLocation, "active", false); }
                catch { active = true; }
                if (!active) active = gameObject.activeInHierarchy;
                if (!active) return;
            }

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                DiagnosticLog.Info("MultiplayerMenuEnterHandler: Enter pressed, firing login fade.");
                MultiplayerMenu.StartLoginFade(this);
            }
        }
    }
}