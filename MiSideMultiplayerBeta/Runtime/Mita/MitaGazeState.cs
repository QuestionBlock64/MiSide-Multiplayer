using System.Collections.Generic;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Per-Mita gaze target tracking. Keyed by scene path so multiple
    /// MitaPerson instances in the same scene each carry their own target.
    /// </summary>
    public static class MitaGazeState
    {
        private static readonly Dictionary<string, string> _currentByPath =
            new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _remoteByPath =
            new Dictionary<string, string>();

        public static string GetCurrent(string mitaPath)
        {
            if (string.IsNullOrEmpty(mitaPath)) return "";
            string v;
            return _currentByPath.TryGetValue(mitaPath, out v) ? v : "";
        }

        public static void SetCurrent(string mitaPath, string playerId)
        {
            if (string.IsNullOrEmpty(mitaPath)) return;
            _currentByPath[mitaPath] = playerId ?? "";
        }

        public static void ClearCurrent(string mitaPath)
        {
            if (string.IsNullOrEmpty(mitaPath)) return;
            _currentByPath.Remove(mitaPath);
        }

        public static string GetRemote(string mitaPath)
        {
            if (string.IsNullOrEmpty(mitaPath)) return "";
            string v;
            return _remoteByPath.TryGetValue(mitaPath, out v) ? v : "";
        }

        public static void SetRemote(string mitaPath, string playerId)
        {
            if (string.IsNullOrEmpty(mitaPath)) return;
            _remoteByPath[mitaPath] = playerId ?? "";
        }

        public static void ClearAll()
        {
            _currentByPath.Clear();
            _remoteByPath.Clear();
        }
    }
}