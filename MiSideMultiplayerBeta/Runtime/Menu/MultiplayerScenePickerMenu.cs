using System;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using MiSideMultiplayer.Runtime.Lobby;
using MiSideMultiplayer.Runtime.Menu.Patches;
using MiSideMultiplayer.Runtime.Menu.UI;
using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.Runtime.Menu
{
    /// <summary>
    /// Host-only menu that lets the host pick which scene everyone loads
    /// into when the session starts. Reached from the lobby's SCENE row.
    /// Scene buttons live inside a cloned vanilla Scroll View so they can
    /// scroll past the visible area.
    /// </summary>
    internal static class MultiplayerScenePickerMenu
    {
        public const string PageName  = "Location Multiplayer Scene Picker";
        public const string PageTitle = "SELECT SCENE";

        public const string DefaultSceneInternal = "Scene 1 - RealRoom";
        public const string DefaultSceneDisplay  = "PROLOGUE";

        private const string SourceScrollViewPath =
            "MenuGame/Canvas/Location Author/Scroll View";

        private const float ScrollViewYOffset = -250f;
        private const float ScrollViewWidth   = 700f;
        private const float ScrollViewHeight  = 550f;

        // Height of a single scene button.
        private const float RowHeight = 55f;

        // Left/right padding inside Content, in pixels.
        private const float ContentSidePadding = 10f;

        // Space above the first row, below the last row, and between rows.
        private const float TopPadding            = 40f;
        private const float BottomPadding         = 40f;
        private const float BetweenButtonPadding  = 25f;

        private const float BackY = -120f;

        private const float FadeDuration = 0.25f;

        public struct SceneEntry
        {
            public readonly string InternalName;
            public readonly string DisplayName;
            public SceneEntry(string internalName, string displayName)
            {
                InternalName = internalName;
                DisplayName  = displayName;
            }
        }

        public static readonly SceneEntry[] Scenes =
        {
            new SceneEntry("Scene 1 - RealRoom",          "PROLOGUE"),
            new SceneEntry("Scene 2 - InGame",            "I'M INSIDE A GAME?"),
            new SceneEntry("Scene 3 - WeTogether",        "TOGETHER AT LAST"),
            new SceneEntry("Scene 4 - StartSecret",       "THINGS GET WEIRD"),
            new SceneEntry("Scene 6 - BasementFirst",     "THE BASEMENT"),
            new SceneEntry("Scene 7 - Backrooms",         "BEYOND THE WORLD"),
            new SceneEntry("Scene 8 - ReRooms",           "THE LOOP"),
            new SceneEntry("Scene 9 - ChibiMita",         "CHIBI MITA"),
            new SceneEntry("Scene 11 - Backrooms",        "DUMMIES AND FORGOTTEN PUZZLES"),
            new SceneEntry("Scene 17 - Dreamer",          "SHE JUST WANTS TO SLEEP"),
            new SceneEntry("Scene 18 - 2D",               "NOVELS"),
            new SceneEntry("Scene 19 - Glasses",          "READING BOOKS, DESTROYING GLITCHES"),
            new SceneEntry("Scene 20 - FightMita",        "RUN AND HIDE!"),
            new SceneEntry("Scene 12 - Freak",            "OLD VERSION"),
            new SceneEntry("Scene 14 - MobilePlayer",     "BEING CANDID"),
            new SceneEntry("Scene 15 - BasementAndDeath", "REBOOT"),
        };

        private static GameMenu _menu;
        private static GameMenu _lobbyMenu;

        public static GameMenu Menu { get { return _menu; } }

        public static GameMenu Build(GameMenu lobbyMenu)
        {
            if (MenuManager.Instance == null)
            {
                DiagnosticLog.Warning("MultiplayerScenePickerMenu: MenuManager not initialized.");
                return null;
            }

            _lobbyMenu = lobbyMenu;

            GameMenu existing = MenuManager.Instance.Find(PageName);
            if (existing != null)
            {
                _menu = existing;
                return existing;
            }

            _menu = new MenuFactory()
                .SetObjectName(PageName)
                .SetTitle(PageTitle)
                .Build();

            if (_menu == null)
            {
                DiagnosticLog.Warning("MultiplayerScenePickerMenu: MenuFactory.Build returned null.");
                return null;
            }

            RectTransform content = null;

            try
            {
                RectTransform menuRoot = _menu.MenuObject.GetComponent<RectTransform>();
                if (menuRoot == null)
                {
                    DiagnosticLog.Warning("MultiplayerScenePickerMenu: menu has no RectTransform.");
                }
                else
                {
                    RectTransform scrollView = CloneScrollViewInto(menuRoot);
                    if (scrollView != null)
                    {
                        AttachFader(scrollView.gameObject, _menu.MenuObject);
                        content = FindContentInClone(scrollView);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerScenePickerMenu: scroll view setup threw: " + ex);
            }

            if (content == null)
            {
                DiagnosticLog.Warning("MultiplayerScenePickerMenu: no Content under scroll view; " +
                                      "falling back to plain menu buttons (some may be off-screen).");
                for (int i = 0; i < Scenes.Length; i++)
                {
                    AddSceneButton(Scenes[i], null, i);
                }
            }
            else
            {
                HideVanillaChildren(content);

                for (int i = 0; i < Scenes.Length; i++)
                {
                    AddSceneButton(Scenes[i], content, i);
                }

                float contentHeight = ComputeContentHeight(Scenes.Length);
                content.sizeDelta = new Vector2(content.sizeDelta.x, contentHeight);

                try { Canvas.ForceUpdateCanvases(); } catch { }
                try { LayoutRebuilder.ForceRebuildLayoutImmediate(content); } catch { }

                DiagnosticLog.Info("MultiplayerScenePickerMenu: content height set to " +
                                   contentHeight.ToString("F1") +
                                   " (rows=" + Scenes.Length +
                                   ", top=" + TopPadding +
                                   ", bottom=" + BottomPadding +
                                   ", between=" + BetweenButtonPadding + ").");
            }

            if (_lobbyMenu != null)
            {
                try
                {
                    MenuOption back = new MenuOptionFactory()
                        .SetName("BACK")
                        .SetParent(_menu)
                        .SetNextLocation(_lobbyMenu)
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

                    MenuLocation ml = _menu.MenuObject.GetComponent<MenuLocation>();
                    if (ml != null && back != null)
                        ml.buttonBack = back.OptionObject;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MultiplayerScenePickerMenu: BACK option failed: " + ex.Message);
                }
            }

            DiagnosticLog.Info("MultiplayerScenePickerMenu: built '" + PageName +
                               "' with " + Scenes.Length + " scene(s).");
            return _menu;
        }

        // ── Layout math ─────────────────────────────────────────────────────

        /// <summary>
        /// Total content height required to fit `count` rows with the current
        /// padding values.
        ///   top + (count * rowHeight) + ((count - 1) * betweenGap) + bottom
        /// </summary>
        private static float ComputeContentHeight(int count)
        {
            if (count <= 0)
                return TopPadding + BottomPadding;

            float between = (count - 1) * BetweenButtonPadding;
            return TopPadding + (count * RowHeight) + between + BottomPadding;
        }

        /// <summary>
        /// Y position (anchored, negative-down) for row `index`, given the
        /// padding constants. Row 0 sits just below TopPadding.
        /// </summary>
        private static float ComputeRowY(int index)
        {
            return -(TopPadding + index * (RowHeight + BetweenButtonPadding));
        }

        private static void AddSceneButton(SceneEntry entry, RectTransform content, int index)
        {
            try
            {
                MenuOption option = new MenuOptionFactory()
                    .SetName(entry.DisplayName)
                    .SetParent(_menu)
                    .SetNextLocation(_lobbyMenu != null ? _lobbyMenu : _menu)
                    .SetOnClick(() => OnSceneChosen(entry))
                    .Build();

                if (content != null && option != null && option.OptionObject != null)
                {
                    ReparentIntoContent(option.OptionObject, content, index);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerScenePickerMenu: button '" +
                                      entry.DisplayName + "' failed: " + ex.Message);
            }
        }

        private static void ReparentIntoContent(GameObject optionObject, RectTransform content, int index)
        {
            if (optionObject == null || content == null) return;

            try
            {
                RectTransform rt = optionObject.GetComponent<RectTransform>();
                if (rt == null) return;

                rt.SetParent(content, false);

                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot     = new Vector2(0.5f, 1f);

                rt.offsetMin = new Vector2( ContentSidePadding, rt.offsetMin.y);
                rt.offsetMax = new Vector2(-ContentSidePadding, rt.offsetMax.y);

                rt.sizeDelta = new Vector2(rt.sizeDelta.x, RowHeight);

                // Use the padding-aware row position.
                rt.anchoredPosition = new Vector2(0f, ComputeRowY(index));

                for (int i = 0; i < rt.childCount; i++)
                {
                    Transform c = rt.GetChild(i);
                    if (c == null) continue;
                    if (c.name == "Text") continue;

                    RectTransform crt = c.TryCast<RectTransform>();
                    if (crt == null) continue;

                    crt.anchorMin = Vector2.zero;
                    crt.anchorMax = Vector2.one;
                    crt.offsetMin = Vector2.zero;
                    crt.offsetMax = Vector2.zero;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerScenePickerMenu: reparent of '" +
                                      optionObject.name + "' failed: " + ex.Message);
            }
        }

        private static void OnSceneChosen(SceneEntry entry)
        {
            DiagnosticLog.Info("MultiplayerScenePickerMenu: host chose '" +
                               entry.InternalName + "' (" + entry.DisplayName + ").");

            var session = MultiplayerLobbyMenu.Session;
            if (session == null)
            {
                DiagnosticLog.Warning("MultiplayerScenePickerMenu: no active session; cannot set scene.");
                return;
            }

            if (!session.IsLocalHost)
            {
                DiagnosticLog.Warning("MultiplayerScenePickerMenu: non-host attempted to select a scene.");
                return;
            }

            session.SelectScene(entry.InternalName);
        }

        // ── Scroll view clone + fade ────────────────────────────────────────

        private static RectTransform CloneScrollViewInto(RectTransform menuRoot)
        {
            GameObject source = GameObject.Find(SourceScrollViewPath);
            if (source == null)
            {
                DiagnosticLog.Warning("MultiplayerScenePickerMenu: source '" +
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

            DiagnosticLog.Info("MultiplayerScenePickerMenu: cloned '" +
                               SourceScrollViewPath + "'.");
            return cloneRt;
        }

        private static void AttachFader(GameObject scrollView, GameObject menuObject)
        {
            if (scrollView == null || menuObject == null) return;

            try
            {
                MenuLocation ml = menuObject.GetComponent<MenuLocation>();
                if (ml == null)
                {
                    DiagnosticLog.Warning("MultiplayerScenePickerMenu: menu has no MenuLocation.");
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

                DiagnosticLog.Info("MultiplayerScenePickerMenu: attached CanvasGroupFader, " +
                                   "registered with MenuLocationActiveFadePatch.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerScenePickerMenu: AttachFader failed: " + ex.Message);
            }
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

        // ── Lookup helpers ──────────────────────────────────────────────────

        public static string GetDisplayName(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return DefaultSceneDisplay;
            for (int i = 0; i < Scenes.Length; i++)
            {
                if (Scenes[i].InternalName == internalName)
                    return Scenes[i].DisplayName;
            }
            return internalName;
        }

        public static string GetInternalName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return DefaultSceneInternal;
            for (int i = 0; i < Scenes.Length; i++)
            {
                if (Scenes[i].DisplayName == displayName)
                    return Scenes[i].InternalName;
            }
            return displayName;
        }
    }
}