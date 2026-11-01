using MiSideMultiplayer.API;
using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MenuTweaks
    {
        // ── Vanilla Location Menu: Exit → Back ─────────────────────────────

        public static void ConvertExitToBack(GameMenu locationMenu, GameMenu backTarget)
        {
            if (locationMenu == null || locationMenu.MenuObject == null) return;
            if (backTarget == null || backTarget.MenuObject == null) return;

            MenuOption exitOption = locationMenu.Find("Button Exit");
            if (exitOption == null)
            {
                DiagnosticLog.Warning("MenuTweaks: 'Button Exit' not found in Location Menu.");
                return;
            }

            GameObject optionGO = exitOption.OptionObject;

            bool stripped = false;
            Transform buttonChild = optionGO.transform.Find("Button");
            if (buttonChild != null)
            {
                Component runner = buttonChild.GetComponent("Menu_RunMouse");
                if (runner != null)
                {
                    Object.Destroy(runner);
                    stripped = true;
                    DiagnosticLog.Info("MenuTweaks: removed Menu_RunMouse from Button Exit\\Button.");
                }
            }
            if (!stripped)
            {
                Component runner = optionGO.GetComponent("Menu_RunMouse");
                if (runner != null)
                {
                    Object.Destroy(runner);
                    DiagnosticLog.Info("MenuTweaks: removed Menu_RunMouse from Button Exit.");
                }
            }

            try { exitOption.Text = "BACK"; }
            catch (System.Exception ex) { DiagnosticLog.Warning("MenuTweaks: label change failed: " + ex.Message); }

            try
            {
                Component next = optionGO.GetComponent("MenuNextLocation");
                if (next != null)
                {
                    QBLib.Fields.WriteRef(next, "nextLocation", backTarget.MenuObject);
                }
                else
                {
                    DiagnosticLog.Warning("MenuTweaks: Button Exit has no MenuNextLocation.");
                }
            }
            catch (System.Exception ex) { DiagnosticLog.Warning("MenuTweaks: nextLocation write failed: " + ex.Message); }
        }

        // ── Location ExitGame: Button No → Mode Select ─────────────────────

        public static void PointExitGameNoButtonTo(GameMenu backTarget)
        {
            if (backTarget == null || backTarget.MenuObject == null) return;

            if (MenuManager.Instance == null)
            {
                DiagnosticLog.Warning("MenuTweaks: MenuManager not initialized.");
                return;
            }

            GameMenu exitGameMenu = MenuManager.Instance.Find("Location ExitGame");
            if (exitGameMenu == null)
            {
                DiagnosticLog.Warning("MenuTweaks: 'Location ExitGame' not found.");
                return;
            }

            MenuOption noOption = exitGameMenu.Find("Button No");
            if (noOption == null)
            {
                DiagnosticLog.Warning("MenuTweaks: 'Button No' not found in Location ExitGame.");
                return;
            }

            GameObject optionGO = noOption.OptionObject;

            GameObject interactive = optionGO;
            Transform buttonChild = optionGO.transform.Find("Button");
            if (buttonChild != null) interactive = buttonChild.gameObject;

            Component next = interactive.GetComponent("MenuNextLocation");
            if (next == null)
            {
                Transform p = interactive.transform.parent;
                while (p != null && next == null)
                {
                    next = p.GetComponent("MenuNextLocation");
                    p = p.parent;
                }
            }

            if (next == null)
            {
                DiagnosticLog.Warning("MenuTweaks: 'Button No' has no MenuNextLocation.");
                return;
            }

            QBLib.Fields.WriteRef(next, "nextLocation", backTarget.MenuObject);
            DiagnosticLog.Info("MenuTweaks: pointed 'Button No' nextLocation to '" +
                               backTarget.MenuObject.name + "'.");
        }
    }
}