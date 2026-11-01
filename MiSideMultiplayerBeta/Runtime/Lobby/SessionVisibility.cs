namespace MiSideMultiplayer.Runtime.Lobby
{
    public enum SessionVisibility
    {
        Public        = 0,
        Private       = 1,
        DirectConnect = 2
    }

    public static class SessionVisibilityExtensions
    {
        public static string DisplayName(this SessionVisibility v)
        {
            switch (v)
            {
                case SessionVisibility.Public:        return "PUBLIC";
                case SessionVisibility.Private:       return "PRIVATE";
                case SessionVisibility.DirectConnect: return "DIRECT CONNECT";
            }
            return "UNKNOWN";
        }
    }
}