using System;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MenuNavigation
    {
        public static void GoTo(GameMenu from, GameMenu to)
        {
            if (from == null || from.MenuObject == null)
            {
                DiagnosticLog.Warning("MenuNavigation.GoTo: source menu is null.");
                return;
            }
            if (to == null || to.MenuObject == null)
            {
                DiagnosticLog.Warning("MenuNavigation.GoTo: destination menu is null.");
                return;
            }

            try
            {
                MenuOption hop = new MenuOptionFactory()
                    .SetName("__nav_hop")
                    .SetParent(from)
                    .SetNextLocation(to)
                    .Build();

                if (hop == null || hop.OptionObject == null)
                {
                    DiagnosticLog.Warning("MenuNavigation.GoTo: failed to build hop option.");
                    return;
                }

                // Park off-screen so it doesn't flash.
                RectTransform rt = hop.OptionObject.GetComponent<RectTransform>();
                if (rt != null)
                {
                    Vector2 pos = rt.anchoredPosition;
                    pos.y = -9000f;
                    rt.anchoredPosition = pos;
                }

                Component nl = hop.OptionObject.GetComponent("MenuNextLocation");
                if (nl != null)
                    QBLib.Fields.WriteRef(nl, "changeCase", hop.OptionObject);

                // Fire the navigation.
                var next = hop.OptionObject.GetComponent<MenuNextLocation>();
                if (next != null)
                {
                    next.Click();
                    DiagnosticLog.Info("MenuNavigation.GoTo: '" + from.MenuObject.name +
                                       "' → '" + to.MenuObject.name + "'.");
                }
                else
                {
                    DiagnosticLog.Warning("MenuNavigation.GoTo: hop has no MenuNextLocation.");
                }

                HopCleanup.Attach(hop.OptionObject);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MenuNavigation.GoTo failed:\n" + ex.ToString());
            }
        }
    }
}