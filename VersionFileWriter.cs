using System;
using System.IO;
using BepInEx;

namespace MiSideMultiplayer
{
    internal static class VersionFileWriter
    {
        public static void Write(
            string pluginName,
            string pluginVersion,
            bool networkingEnabled,
            string serverHost,
            int serverPort,
            string roomName)
        {
            string gameRoot = Paths.GameRootPath;
            if (string.IsNullOrEmpty(gameRoot))
                return;

            string dataDirectory = Path.Combine(gameRoot, "Data");
            Directory.CreateDirectory(dataDirectory);

            string versionFile = Path.Combine(dataDirectory, "Version.txt");
            string text =
                "MiSide Multiplayer DEV Test" + Environment.NewLine +
                pluginName + Environment.NewLine +
                "Version: " + pluginVersion + Environment.NewLine +
                "Runtime: BepInEx 6 IL2CPP" + Environment.NewLine +
                "Networking: " + (networkingEnabled ? "Enabled" : "Disabled") + Environment.NewLine +
                "Relay: " + serverHost + ":" + serverPort + Environment.NewLine +
                "Room: " + roomName + Environment.NewLine;

            File.WriteAllText(versionFile, text);
        }
    }
}
