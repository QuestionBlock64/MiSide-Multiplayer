using System;
using System.Collections.Generic;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Credits;
using MiSideMultiplayer.API.Factories;
using MiSideMultiplayer.Runtime.Menu.Patches;
using MiSideMultiplayer.Runtime.Menu.UI;
using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MultiplayerAuthorMenu
    {
        public const string PageName  = "Location MPAuthor";
        public const string PageTitle = "CREDITS";

        private const string SourceScrollViewPath =
            "MenuGame/Canvas/Location Author/Scroll View";

        private const float BottomPadding = 40f;

        private const float BackY = -150f;

        private const float ScrollViewYOffset = -220f;
        private const float ScrollViewWidth   = 700f;
        private const float ScrollViewHeight  = 530f;

        private const float FadeDuration = 0.25f;

        public static GameMenu Build(GameMenu backTarget)
        {
            if (MenuManager.Instance == null)
            {
                DiagnosticLog.Warning("MultiplayerAuthorMenu: MenuManager not initialized.");
                return null;
            }

            GameMenu existing = MenuManager.Instance.Find(PageName);
            if (existing != null)
                return existing;

            GameMenu menu = new MenuFactory()
                .SetObjectName(PageName)
                .SetTitle(PageTitle)
                .Build();

            if (menu == null)
            {
                DiagnosticLog.Warning("MultiplayerAuthorMenu: MenuFactory.Build returned null.");
                return null;
            }

            try
            {
                RectTransform menuRoot = menu.MenuObject.GetComponent<RectTransform>();
                if (menuRoot == null)
                {
                    DiagnosticLog.Warning("MultiplayerAuthorMenu: menu has no RectTransform.");
                    return menu;
                }

                RectTransform scrollView = CloneScrollViewInto(menuRoot);
                if (scrollView == null)
                {
                    DiagnosticLog.Warning("MultiplayerAuthorMenu: could not clone scroll view.");
                    return menu;
                }

                AttachFader(scrollView.gameObject, menu.MenuObject);

                RectTransform content = FindContentInClone(scrollView);
                if (content == null)
                {
                    DiagnosticLog.Warning("MultiplayerAuthorMenu: clone has no Content.");
                    return menu;
                }

                HideVanillaChildren(content);

                List<CreditEntry> entries = BuildCreditsEntries();
                CreditsStyle style = BuildCreditsStyle();

                RectTransform section = CreditsBuilder.BuildSection(content, entries, style);
                if (section == null)
                {
                    DiagnosticLog.Warning("MultiplayerAuthorMenu: CreditsBuilder.BuildSection returned null.");
                    return menu;
                }

                float sectionHeight = MeasureSectionHeight(section);
                float contentHeight = sectionHeight + BottomPadding;

                section.sizeDelta = new Vector2(section.sizeDelta.x, sectionHeight);
                content.sizeDelta = new Vector2(content.sizeDelta.x, contentHeight);

                try { Canvas.ForceUpdateCanvases(); } catch { }
                try { LayoutRebuilder.ForceRebuildLayoutImmediate(content); } catch { }

                DiagnosticLog.Info("MultiplayerAuthorMenu: built '" + PageName +
                                   "' with " + entries.Count + " entries, sectionHeight=" +
                                   sectionHeight.ToString("F1") + ", contentHeight=" +
                                   contentHeight.ToString("F1") + ".");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerAuthorMenu: build threw: " + ex);
            }

            if (backTarget != null)
            {
                try
                {
                    MenuOption back = new MenuOptionFactory()
                        .SetName("BACK")
                        .SetParent(menu)
                        .SetNextLocation(backTarget)
                        .Build();

                    if (back != null && back.OptionObject != null)
                    {
                        RectTransform rt = back.OptionObject.GetComponent<RectTransform>();
                        if (rt != null)
                        {
                            Vector2 pos = rt.anchoredPosition;
                            pos.y = BackY;
                            rt.anchoredPosition = pos;
                        }
                    }

                    MenuLocation ml = menu.MenuObject.GetComponent<MenuLocation>();
                    if (ml != null && back != null)
                        ml.buttonBack = back.OptionObject;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MultiplayerAuthorMenu: BACK option failed: " + ex.Message);
                }
            }

            return menu;
        }


        private static void AttachFader(GameObject scrollView, GameObject menuObject)
        {
            if (scrollView == null || menuObject == null) return;

            try
            {
                MenuLocation ml = menuObject.GetComponent<MenuLocation>();
                if (ml == null)
                {
                    DiagnosticLog.Warning("MultiplayerAuthorMenu: menu has no MenuLocation; " +
                                          "fader will stay at alpha 0.");
                    return;
                }

                CanvasGroup group = scrollView.GetComponent<CanvasGroup>();
                if (group == null)
                    group = scrollView.AddComponent<CanvasGroup>();

                CanvasGroupFader fader = scrollView.GetComponent<CanvasGroupFader>();
                if (fader == null)
                    fader = scrollView.AddComponent<CanvasGroupFader>();

                fader.FadeDuration = FadeDuration;
                fader.Initialize(group, false);

                MenuLocationActiveFadePatch.Register(ml, fader);

                DiagnosticLog.Info("MultiplayerAuthorMenu: attached CanvasGroupFader, " +
                                   "registered with MenuLocationActiveFadePatch.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerAuthorMenu: AttachFader failed: " + ex.Message);
            }
        }


        private static RectTransform CloneScrollViewInto(RectTransform menuRoot)
        {
            GameObject source = GameObject.Find(SourceScrollViewPath);
            if (source == null)
            {
                DiagnosticLog.Warning("MultiplayerAuthorMenu: source '" +
                                      SourceScrollViewPath + "' not found.");
                return null;
            }

            GameObject clone = UnityEngine.Object.Instantiate(source, menuRoot);
            clone.name = "Scroll View";
            clone.SetActive(true);

            RectTransform cloneRt = clone.GetComponent<RectTransform>();
            if (cloneRt != null)
            {
                cloneRt.anchorMin = new Vector2(0.5f, 1f);
                cloneRt.anchorMax = new Vector2(0.5f, 1f);
                cloneRt.pivot     = new Vector2(0.5f, 1f);
                cloneRt.anchoredPosition = new Vector2(0f, ScrollViewYOffset);
                cloneRt.sizeDelta = new Vector2(ScrollViewWidth, ScrollViewHeight);
            }

            DiagnosticLog.Info("MultiplayerAuthorMenu: cloned '" + SourceScrollViewPath + "'.");
            return cloneRt;
        }

        private static RectTransform FindContentInClone(Transform cloneRoot)
        {
            try
            {
                ScrollRect[] scrollRects = cloneRoot.GetComponentsInChildren<ScrollRect>(true);
                if (scrollRects != null)
                {
                    for (int i = 0; i < scrollRects.Length; i++)
                    {
                        ScrollRect sr = scrollRects[i];
                        if (sr == null) continue;
                        if (sr.content == null) continue;
                        return sr.content;
                    }
                }
            }
            catch { }

            Transform vp = FindChildRecursive(cloneRoot, "Viewport");
            if (vp != null)
            {
                Transform c = FindChildRecursive(vp, "Content");
                if (c != null)
                {
                    RectTransform rt = c.TryCast<RectTransform>();
                    if (rt != null) return rt;
                }
            }

            return null;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent == null || string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c == null) continue;
                if (c.name == name) return c;
                Transform hit = FindChildRecursive(c, name);
                if (hit != null) return hit;
            }
            return null;
        }

        private static void HideVanillaChildren(RectTransform content)
        {
            try
            {
                for (int i = 0; i < content.childCount; i++)
                {
                    Transform child = content.GetChild(i);
                    if (child == null) continue;
                    if (child.gameObject.activeSelf)
                        child.gameObject.SetActive(false);
                }
            }
            catch { }
        }

        private static float MeasureSectionHeight(RectTransform section)
        {
            if (section == null) return 0f;

            float lowest = 0f;
            try
            {
                for (int i = 0; i < section.childCount; i++)
                {
                    Transform child = section.GetChild(i);
                    if (child == null) continue;

                    RectTransform crt = child.TryCast<RectTransform>();
                    if (crt == null) continue;

                    float bottom = crt.anchoredPosition.y + crt.rect.yMin;
                    if (bottom < lowest) lowest = bottom;
                }
            }
            catch { }

            float measured = Mathf.Abs(lowest);

            if (measured < 10f)
                measured = section.sizeDelta.y;

            return measured;
        }


        private static List<CreditEntry> BuildCreditsEntries()
        {
            return new List<CreditEntry>
            {
                CreditEntry.Title("MiSide Multiplayer"),
                CreditEntry.Line("A cooperative multiplayer mod"),
                CreditEntry.Space(30f),

                CreditEntry.Divider(),

                CreditEntry.Header("Lead Developer"),
                CreditEntry.Line("QB"),
                CreditEntry.Space(20f),

                CreditEntry.Header("Contributors"),
                CreditEntry.Line("MaximWeTrO - Helped with some mod features."),
                CreditEntry.Space(10f),
                CreditEntry.Line("Danvol - The MiSide Main Menu Piano Cover artist."),
                CreditEntry.Space(20f),

                CreditEntry.Header("Special Thanks"),
                CreditEntry.Line("The MiSide community"),
                CreditEntry.Space(10f),
                CreditEntry.Line("Umeerai and MakenCat - AIHASTO"),
                CreditEntry.Space(10f),
                CreditEntry.Line("Everyone who tested the beta"),
                CreditEntry.Space(20f),

                CreditEntry.Header("Built With"),
                CreditEntry.Line("BepInEx 6 (IL2CPP)"),
                CreditEntry.Line("HarmonyX"),
                CreditEntry.Line("Unity Engine"),
                CreditEntry.Space(20f),

                CreditEntry.Divider(),
                CreditEntry.Line("Thank you for playing!"),
            };
        }

        private static CreditsStyle BuildCreditsStyle()
        {
            return new CreditsStyle
            {
                TitleSize  = 72,
                HeaderSize = 36,
                BodySize   = 28,
                UseGradient = true,
                GradientTop    = "#FF66CC",
                GradientBottom = "#882299",
                OutlineColor = "#982458",
                OutlineSize  = 3f,
                UseOutline   = true,
            };
        }
    }
}