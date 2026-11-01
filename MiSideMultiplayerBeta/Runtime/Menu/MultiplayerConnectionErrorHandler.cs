using MiSideMultiplayer.API;
using MiSideMultiplayer.Runtime.UI;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MultiplayerConnectionErrorHandler
    {
        private const string GeneralMessage =
            "Couldn't connect to the server. Please check your internet connection and try again.";
        private const string DirectMessage =
            "No server was found at that IP and port. Check the IP and port, confirm the Direct Connect relay is running, and allow both the relay server and MiSide Multiplayer through your firewall.";
        private const string TimeoutMessage = "Timed out.";
        private const string VersionMismatchMessage =
            "You're running an outdated version of MiSide Multiplayer. Please update to the latest version and try again.";
        private const string ConnectionResetMessage =
            "QBLib.Network.QBServException: connection reset";
        private const string UnknownErrorMessage =
            "Unknown Error. Contact Multiplayer Support if this persists.";
        private const string VersionCheckMessage =
            "Could not log in. Please check your internet connection and try again.";

        private static bool _showingFailure;
        private static PendingFailure _pendingFailure;

        private sealed class PendingFailure
        {
            public string Title;
            public string Message;
            public string ReturnPageName;
        }

        public static void HandleRelayFailure(RelayFailureKind failure, bool isDirectConnection)
        {
            if (_showingFailure) return;

            if (failure == RelayFailureKind.ConnectionReset)
            {
                Show(isDirectConnection ? "Direct Connect Failed" : "Internal Exception",
                    isDirectConnection ? DirectMessage : ConnectionResetMessage,
                    isDirectConnection ? MultiplayerDirectConnectMenu.PageName : MultiplayerHomeMenu.PageName);
                return;
            }

            bool timedOut = failure == RelayFailureKind.TimedOut;
            Show(
                isDirectConnection ? "Direct Connect Failed" : "Connection Error",
                isDirectConnection ? DirectMessage : (timedOut ? TimeoutMessage : GeneralMessage),
                isDirectConnection ? MultiplayerDirectConnectMenu.PageName :
                    (timedOut ? ModeSelectMenu.PageName : MultiplayerHomeMenu.PageName));
        }

        public static void ShowPrivateLobbyNotFound()
        {
            Show("Private Lobby Not Found",
                "No active private lobby uses that code. Check the code shown by the host and try again.",
                MultiplayerPrivateLobbyMenu.PageName);
        }

        public static void ShowInvalidDirectEndpoint(string message)
        {
            Show("Invalid Direct Connect Address", message ?? "Enter a valid IP address and port.",
                MultiplayerDirectConnectMenu.PageName);
        }

        public static void ShowRegistrationRejected(string reason, bool directConnection)
        {
            string normalized = string.IsNullOrWhiteSpace(reason)
                ? "unknown" : reason.Trim().ToLowerInvariant();
            if (normalized == "server_mode_mismatch")
            {
                Show(directConnection ? "Direct Connect Server Required" : "Lobby Registration Failed",
                    directConnection
                        ? "This server is not in Direct Connect mode. Start the relay with --directconnect, or use a Public/Private server."
                        : "This relay is reserved for Direct Connect lobbies.",
                    directConnection ? MultiplayerHostSetupMenu.PageName : MultiplayerHostSetupMenu.PageName);
                return;
            }

            string message = normalized == "already_reserved"
                ? "That lobby code or room is already in use. Choose a different private code and try again."
                : "The relay rejected this lobby registration (" + normalized + ").";
            Show("Lobby Registration Failed", message, MultiplayerHostSetupMenu.PageName);
        }

        public static void ShowJoinRejected(string reason)
        {
            string normalized = string.IsNullOrWhiteSpace(reason)
                ? "unknown" : reason.Trim().ToLowerInvariant();
            string spaced = normalized.Replace('_', ' ').Replace('-', ' ');

            // The relay currently sends "Locked_InGame" for an occupied game
            // and "lobby_full" when the player cap has been reached. Accept
            // the older and more readable aliases too, so protocol casing or
            // separator changes never fall through to the generic support page.
            if (normalized == "lobby_full" || spaced == "lobby full" ||
                spaced == "lobby is full" || spaced == "full")
            {
                Show("Unable to Join", "Lobby is full.", MultiplayerHomeMenu.PageName);
                return;
            }

            if (normalized == "locked_ingame" || normalized == "game_in_progress" ||
                normalized == "in_progress" || normalized == "in_game" ||
                spaced == "locked ingame" || spaced == "game in progress" ||
                spaced == "in progress" || spaced == "already playing")
            {
                Show("Unable to Join", "A game is in progress.", MultiplayerHomeMenu.PageName);
                return;
            }

            switch (normalized)
            {
                case "server_mode_mismatch":
                    Show("Wrong Server Type",
                        "This server is not hosting Direct Connect lobbies. Check the host's IP and port.",
                        MultiplayerDirectConnectMenu.PageName);
                    return;

                case "no_such_room":
                    Show("No Direct Connect Lobby",
                        "A relay answered at that address, but no Direct Connect lobby is open there. Check with the host.",
                        MultiplayerDirectConnectMenu.PageName);
                    return;

                default:
                    Show("Error", UnknownErrorMessage, MultiplayerHomeMenu.PageName);
                    return;
            }
        }

        public static void ShowVersionMismatch()
        {
            Show("Version Mismatch", VersionMismatchMessage, ModeSelectMenu.PageName);
        }

        public static void ShowVersionCheckConnectionError()
        {
            Show("Connection Error", VersionCheckMessage, ModeSelectMenu.PageName);
        }

        public static void ShowConnectionTimeout()
        {
            Show("Connection Error", TimeoutMessage, ModeSelectMenu.PageName);
        }

        public static void ShowHostLeft(string hostName)
        {
            string who = string.IsNullOrEmpty(hostName) ? "The host" : hostName;
            Show("Connection Error", who + " left the session.", MultiplayerHomeMenu.PageName);
        }

        internal static void Tick()
        {
            if (_pendingFailure == null || !IsSceneMenu() || MenuManager.Instance == null)
                return;

            PendingFailure pending = _pendingFailure;
            _pendingFailure = null;
            Present(pending.Title, pending.Message, pending.ReturnPageName);
        }

        private static void Show(string title, string message, string returnPageName)
        {
            if (_showingFailure) return;
            _showingFailure = true;

            DiagnosticLog.Warning("MultiplayerConnectionErrorHandler: " + title + " — " + message);
            MultiplayerConnectingOverlay.Hide();
            MultiplayerDirectConnectMenu.AbortPendingConnection();
            MultiplayerPrivateLobbyMenu.AbortPendingConnection();
            MultiplayerPublicLobbyMenu.AbortPendingConnection();
            MultiplayerHostSetupMenu.AbortPendingConnection();
            MultiplayerLobbyMenu.AbortForConnectionFailure();
            try { MiSideMultiplayer.Runtime.Loading.SceneLoadingHook.CancelNativeTeardown(); } catch { }
            try { MultiplayerLoadingScene.Abort(); } catch { }
            try { MiSideMultiplayerPlugin.DisconnectRelay(); } catch { }

            if (!IsSceneMenu())
            {
                _pendingFailure = new PendingFailure
                {
                    Title = title,
                    Message = message,
                    ReturnPageName = returnPageName
                };

                DiagnosticLog.Info("MultiplayerConnectionErrorHandler: returning to SceneMenu before showing error.");
                try
                {
                    ReturnToSceneMenu();
                }
                catch (System.Exception ex)
                {
                    _pendingFailure = null;
                    _showingFailure = false;
                    DiagnosticLog.Warning("MultiplayerConnectionErrorHandler: SceneMenu return failed: " + ex.Message);
                }
                return;
            }

            Present(title, message, returnPageName);
        }

        private static bool IsSceneMenu()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "SceneMenu"; }
            catch { return false; }
        }

        private static void ReturnToSceneMenu()
        {
            // Match GameController.ExitGame(). BlackScreen destroys the
            // persistent gameplay controller before it begins the normal
            // SceneLoading -> SceneMenu transition.
            GameObject game = GameObject.FindWithTag("Game");
            Transform blackScreen = game == null ? null :
                game.transform.Find("Interface/BlackScreen");
            BlackScreen transition = blackScreen == null ? null :
                blackScreen.GetComponent<BlackScreen>();

            if (transition != null)
            {
                transition.NextLevel("SceneMenu");
                return;
            }

            UnityEngine.SceneManagement.SceneManager.LoadScene("SceneMenu");
        }

        private static void Present(string title, string message, string returnPageName)
        {
            GameMenu returnTo = null;
            try
            {
                if (MenuManager.Instance != null)
                    returnTo = MenuManager.Instance.Find(returnPageName);
            }
            catch { }

            MultiplayerErrorWindowMenu.Show(
                returnTo,
                message,
                ErrorWindowButtons.OK,
                _ => { _showingFailure = false; },
                title);
        }
    }
}
