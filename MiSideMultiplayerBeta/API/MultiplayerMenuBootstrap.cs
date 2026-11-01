using MiSideMultiplayer.API.Events;
using MiSideMultiplayer.API.Factories;

namespace MiSideMultiplayer.API
{
    /// <summary>
    /// Builds the "Location Multiplayer Settings" page and its button on
    /// Location MainOptions. Called once from the Menu.Start postfix.
    ///
    /// BACK is created AFTER the page is populated (in MultiplayerSettingsRuntime),
    /// so it ends up at the bottom of the list.
    /// </summary>
    internal static class MultiplayerMenuBootstrap
    {
        internal const string PageName = "Location Multiplayer Settings";
        internal const string PageTitle = "MULTIPLAYER SETTINGS";
        internal const string ButtonLabel = "MULTIPLAYER SETTINGS";

        internal static void Build()
        {
            if (MenuManager.Instance == null) return;

            GameMenu mainOptions = MenuManager.Instance.Find("Location MainOptions");
            if (mainOptions == null)
            {
                DiagnosticLog.Warning("MultiplayerMenuBootstrap: Location MainOptions not found.");
                return;
            }

            GameMenu page = MenuManager.Instance.Find(PageName);
            if (page == null)
            {
                // No back menu passed here on purpose. BACK is added by the
                // runtime bridge after all our rows exist, so it ends up last.
                page = new MenuFactory()
                    .SetObjectName(PageName)
                    .SetTitle(PageTitle)
                    .Build();

                DiagnosticLog.Info("MultiplayerMenuBootstrap: built '" + PageName + "'.");
            }

            if (mainOptions.Find(ButtonLabel) == null)
            {
                new MenuOptionFactory()
                    .SetName(ButtonLabel)
                    .SetParent(mainOptions)
                    .SetNextLocation(page)
                    .PlaceOptionBefore(4)
                    .Build();

                DiagnosticLog.Info("MultiplayerMenuBootstrap: added '" + ButtonLabel + "' to Location MainOptions.");
            }

            InitializedEvent.Invoke();
        }
    }
}