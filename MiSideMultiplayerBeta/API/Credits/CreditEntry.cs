namespace MiSideMultiplayer.API.Credits
{
    public enum CreditEntryType
    {
        Title,
        Header,
        Line,
        Divider,
        Space
    }

    public sealed class CreditEntry
    {
        public CreditEntryType Type;
        public string Text = "";
        public float Value = 0f;

        public CreditEntry(CreditEntryType type, string text = "", float value = 0f)
        {
            Type  = type;
            Text  = text ?? "";
            Value = value;
        }

        public static CreditEntry Title(string text)   { return new CreditEntry(CreditEntryType.Title, text); }
        public static CreditEntry Header(string text)  { return new CreditEntry(CreditEntryType.Header, text); }
        public static CreditEntry Line(string text)    { return new CreditEntry(CreditEntryType.Line, text); }
        public static CreditEntry Divider()            { return new CreditEntry(CreditEntryType.Divider); }
        public static CreditEntry Space(float height)  { return new CreditEntry(CreditEntryType.Space, "", height); }
    }
}