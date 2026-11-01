namespace MiSideMultiplayer.API.Credits
{
    public sealed class CreditsStyle
    {
        public int TitleSize  = 0;
        public int HeaderSize = 0;
        public int BodySize   = 0;

        public string TitleColor  = "";
        public string HeaderColor = "";
        public string TextColor   = "";

        public bool UseGradient = true;
        public string GradientTop    = "";
        public string GradientBottom = "";

        public string DividerColor = "";
        public float  DividerHeight = 0f;

        public float TopGap    = 0f;
        public float BottomGap = 0f;

        public float BaseSpacing = 20f;

        public float TitleSpacing   = -1f;
        public float HeaderSpacing  = -1f;
        public float DividerSpacing = -1f;
        public float LineGap        = -1f;

        public bool UseOutline   = true;
        public bool OutlineLines = false;
        public string OutlineColor = "#982458";
        public float  OutlineSize  = 3f;
    }
}