using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.API.Credits
{
    internal static class CreditsBuilder
    {
        internal sealed class DetectedStyle
        {
            public int   titleSize  = 0;
            public int   headerSize = 0;
            public int   bodySize   = 0;
            public Color titleColor  = Color.white;
            public Color headerColor = Color.white;
            public Color bodyColor   = Color.white;
            public Color lineColor   = new Color(1f, 1f, 1f, 0.8f);
            public float lineHeight  = 4f;
        }

        internal static RectTransform BuildSection(
            RectTransform parent,
            List<CreditEntry> entries,
            CreditsStyle style)
        {
            if (parent == null) return null;
            if (style == null) style = new CreditsStyle();
            if (entries == null) entries = new List<CreditEntry>();

            DetectedStyle det = DetectStyle(parent);
            Font font = GetGameFont();

            int titleSize  = style.TitleSize  > 0 ? style.TitleSize  : (det.titleSize  > 0 ? det.titleSize  : 64);
            int headerSize = style.HeaderSize > 0 ? style.HeaderSize : (det.headerSize > 0 ? det.headerSize : 44);
            int bodySize   = style.BodySize   > 0 ? style.BodySize   : (det.bodySize   > 0 ? det.bodySize   : 32);

            Color titleColor  = ResolveColor(style.TitleColor,  det.titleColor);
            Color headerColor = ResolveColor(style.HeaderColor, det.headerColor);
            Color bodyColor   = ResolveColor(style.TextColor,   det.bodyColor);

            Color gradTop = ResolveColor(style.GradientTop, titleColor);
            Color gradBottom = ResolveColor(style.GradientBottom,
                new Color(titleColor.r * 0.55f, titleColor.g * 0.65f, titleColor.b * 0.95f, titleColor.a));

            Color lineColor = ResolveColor(style.DividerColor, det.lineColor);
            float lineHeight = style.DividerHeight > 0.5f ? style.DividerHeight : det.lineHeight;

            Color outlineColor = ResolveColor(style.OutlineColor, new Color(0.596f, 0.141f, 0.345f, 1f));
            float outlineSize = style.OutlineSize > 0.1f ? style.OutlineSize : 3f;

            float baseSpacing = style.BaseSpacing;
            float titleSpacing   = style.TitleSpacing   >= 0f ? style.TitleSpacing   : baseSpacing;
            float headerSpacing  = style.HeaderSpacing  >= 0f ? style.HeaderSpacing  : baseSpacing;
            float dividerSpacing = style.DividerSpacing >= 0f ? style.DividerSpacing : baseSpacing;
            float lineGap        = style.LineGap        >= 0f ? style.LineGap        : 0f;

            // Compute total height ourselves. Layout groups and ContentSizeFitter
            // have been unreliable in this IL2CPP menu context, so we place
            // each entry at an explicit anchoredPosition.y.
            float totalHeight = 0f;
            if (style.TopGap > 0.25f) totalHeight += style.TopGap;
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                {
                    float gap = Mathf.Max(
                        GetEntrySpacing(entries[i - 1], titleSpacing, headerSpacing, dividerSpacing, lineGap),
                        GetEntrySpacing(entries[i],     titleSpacing, headerSpacing, dividerSpacing, lineGap));
                    if (gap > 0.25f) totalHeight += gap;
                }
                totalHeight += EntryHeight(entries[i], titleSize, headerSize, bodySize, lineHeight);
            }
            if (style.BottomGap > 0.25f) totalHeight += style.BottomGap;
            if (totalHeight < 10f) totalHeight = 10f;

            GameObject section = CreateUIGameObject("CreditsSection");
            section.layer = parent.gameObject.layer;

            RectTransform rt = section.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, totalHeight);

            float cursorY = -(style.TopGap > 0.25f ? style.TopGap : 0f);

            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                {
                    float gap = Mathf.Max(
                        GetEntrySpacing(entries[i - 1], titleSpacing, headerSpacing, dividerSpacing, lineGap),
                        GetEntrySpacing(entries[i],     titleSpacing, headerSpacing, dividerSpacing, lineGap));
                    if (gap > 0.25f) cursorY -= gap;
                }

                float h = EntryHeight(entries[i], titleSize, headerSize, bodySize, lineHeight);
                PlaceEntry(rt, entries[i], cursorY, h, font,
                    titleSize, headerSize, bodySize,
                    titleColor, headerColor, bodyColor,
                    style.UseGradient, gradTop, gradBottom,
                    style.UseOutline, style.OutlineLines, outlineColor, outlineSize,
                    lineHeight, lineColor);
                cursorY -= h;
            }

            DiagnosticLog.Info("CreditsBuilder: section built with " + entries.Count +
                               " entries, total height=" + totalHeight.ToString("F1") +
                               ", font='" + (font != null ? font.name : "<null>") + "'.");

            return rt;
        }

        private static float EntryHeight(CreditEntry e,
            int titleSize, int headerSize, int bodySize, float lineHeight)
        {
            switch (e.Type)
            {
                case CreditEntryType.Title:   return titleSize;
                case CreditEntryType.Header:  return headerSize;
                case CreditEntryType.Line:    return bodySize;
                case CreditEntryType.Divider: return lineHeight;
                case CreditEntryType.Space:   return e.Value;
                default: return 0f;
            }
        }

        private static float GetEntrySpacing(CreditEntry e,
            float titleSpacing, float headerSpacing, float dividerSpacing, float lineGap)
        {
            switch (e.Type)
            {
                case CreditEntryType.Title:   return titleSpacing;
                case CreditEntryType.Header:  return headerSpacing;
                case CreditEntryType.Divider: return dividerSpacing;
                case CreditEntryType.Line:    return lineGap;
                case CreditEntryType.Space:   return e.Value;
                default: return 0f;
            }
        }

        private static void PlaceEntry(
            RectTransform parent, CreditEntry e, float y, float height, Font font,
            int titleSize, int headerSize, int bodySize,
            Color titleColor, Color headerColor, Color bodyColor,
            bool useGradient, Color gradTop, Color gradBottom,
            bool useOutline, bool outlineLines, Color outlineColor, float outlineSize,
            float lineHeight, Color lineColor)
        {
            switch (e.Type)
            {
                case CreditEntryType.Title:
                    PlaceText(parent, e.Text, y, height, titleSize, FontStyle.Bold, titleColor,
                        font, false, useGradient, gradTop, gradBottom, outlineColor, outlineSize);
                    break;

                case CreditEntryType.Header:
                    PlaceText(parent, e.Text, y, height, headerSize, FontStyle.Bold, headerColor,
                        font, useOutline, false, gradTop, gradBottom, outlineColor, outlineSize);
                    break;

                case CreditEntryType.Line:
                    PlaceText(parent, e.Text, y, height, bodySize, FontStyle.Normal, bodyColor,
                        font, outlineLines && useOutline, false, gradTop, gradBottom, outlineColor, outlineSize);
                    break;

                case CreditEntryType.Divider:
                    PlaceDivider(parent, y, height, lineColor);
                    break;

                case CreditEntryType.Space:
                    break;
            }
        }

        private static void PlaceText(
            RectTransform parent, string text, float y, float height,
            int fontSize, FontStyle style, Color color,
            Font font, bool outline, bool gradient, Color gradTop, Color gradBottom,
            Color outlineColor, float outlineSize)
        {
            GameObject go = CreateUIGameObject("CreditsLine");
            go.layer = parent.gameObject.layer;

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(0f, height);

            Text txt = go.AddComponent<Text>();
            txt.font = font;
            txt.text  = text ?? "";
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.color = color;
            txt.alignment = TextAnchor.UpperCenter;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow   = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;
            txt.supportRichText = true;

            if (gradient)
            {
                try
                {
                    CreditsGradient g = go.AddComponent<CreditsGradient>();
                    g.topColor    = gradTop;
                    g.bottomColor = gradBottom;
                }
                catch { }
            }

            if (outline)
            {
                try
                {
                    Outline o = go.AddComponent<Outline>();
                    o.effectColor    = outlineColor;
                    o.effectDistance = new Vector2(outlineSize, -outlineSize);
                }
                catch { }
            }
        }

        private static void PlaceDivider(RectTransform parent, float y, float height, Color color)
        {
            GameObject go = CreateUIGameObject("CreditsDivider");
            go.layer = parent.gameObject.layer;

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(0f, height);

            Image img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        private static GameObject CreateUIGameObject(string name)
        {
            Il2CppReferenceArray<Il2CppSystem.Type> types =
                new Il2CppReferenceArray<Il2CppSystem.Type>(1);
            types[0] = Il2CppType.Of<RectTransform>();
            return new GameObject(name, types);
        }

        private static Font GetGameFont()
        {
            try
            {
                if (GlobalGame.fontUse != null)
                    return GlobalGame.fontUse;
            }
            catch { }

            try { return Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            try { return Font.CreateDynamicFontFromOSFont("Arial", 32); } catch { }
            return null;
        }

        private static Color ResolveColor(string cfgVal, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(cfgVal)) return fallback;
            if (ColorUtility.TryParseHtmlString(cfgVal, out Color c)) return c;
            return fallback;
        }

        internal static DetectedStyle DetectStyle(RectTransform content)
        {
            DetectedStyle d = new DetectedStyle();
            if (content == null) return d;

            Dictionary<int, int> bodySizes = new Dictionary<int, int>();
            Dictionary<int, int> headerSizes = new Dictionary<int, int>();
            Dictionary<int, Color> bodyColors = new Dictionary<int, Color>();
            Dictionary<int, Color> headerColors = new Dictionary<int, Color>();

            int maxSize = 0;
            Color maxColor = Color.white;

            try
            {
                for (int i = 0; i < content.childCount; i++)
                {
                    Transform child = content.GetChild(i);
                    if (child == null) continue;

                    Text text = null;
                    Image image = null;
                    bool hasShadow = false;

                    Component[] comps = child.GetComponents<Component>();
                    foreach (Component c in comps)
                    {
                        if (c == null) continue;

                        if (text == null)
                        {
                            Text t = c.TryCast<Text>();
                            if (t != null) text = t;
                        }
                        if (image == null)
                        {
                            Image im = c.TryCast<Image>();
                            if (im != null) image = im;
                        }
                        if (c.TryCast<Shadow>() != null) hasShadow = true;
                    }

                    if (text != null)
                    {
                        int fs = text.fontSize;
                        Color col = text.color;

                        if (fs > maxSize) { maxSize = fs; maxColor = col; }

                        if (hasShadow)
                        {
                            headerSizes[fs] = headerSizes.ContainsKey(fs) ? headerSizes[fs] + 1 : 1;
                            headerColors[fs] = col;
                        }
                        else
                        {
                            bodySizes[fs] = bodySizes.ContainsKey(fs) ? bodySizes[fs] + 1 : 1;
                            bodyColors[fs] = col;
                        }
                    }
                    else if (image != null && child.name == "Line")
                    {
                        d.lineColor = image.color;
                        RectTransform crt = child.TryCast<RectTransform>();
                        if (crt != null && crt.rect.height > 0.5f)
                            d.lineHeight = crt.rect.height;
                    }
                }
            }
            catch { }

            d.titleSize  = maxSize;
            d.titleColor = maxColor;

            int bestB = 0;
            foreach (var kv in bodySizes)
            {
                if (kv.Value > bestB)
                {
                    bestB = kv.Value;
                    d.bodySize  = kv.Key;
                    d.bodyColor = bodyColors[kv.Key];
                }
            }

            int bestH = 0;
            foreach (var kv in headerSizes)
            {
                if (kv.Value > bestH)
                {
                    bestH = kv.Value;
                    d.headerSize  = kv.Key;
                    d.headerColor = headerColors[kv.Key];
                }
            }

            if (d.bodySize == 0)
                d.bodySize = maxSize > 0 ? Mathf.Max(16, (int)(maxSize * 0.5f)) : 32;
            if (d.headerSize == 0)
                d.headerSize = (int)(d.bodySize * 1.4f);
            if (d.titleSize == 0)
                d.titleSize = (int)(d.bodySize * 2f);

            return d;
        }
    }
}