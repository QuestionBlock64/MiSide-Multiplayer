using System;
using System.Collections.Generic;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.API.Credits
{
    public static class QBLibCredits
    {
        public static GameMenu BuildMenu(
            string menuName,
            string menuTitle,
            GameMenu backTarget,
            List<CreditEntry> entries,
            CreditsStyle style = null)
        {
            if (MenuManager.Instance == null)
            {
                DiagnosticLog.Warning("QBLibCredits: MenuManager not initialized.");
                return null;
            }

            GameMenu existing = MenuManager.Instance.Find(menuName);
            if (existing != null)
            {
                DiagnosticLog.Info("QBLibCredits: menu '" + menuName + "' already exists; returning it.");
                return existing;
            }

            MenuFactory factory = new MenuFactory()
                .SetObjectName(menuName)
                .SetTitle(menuTitle);

            if (backTarget != null)
                factory = factory.SetBackButton(backTarget);

            GameMenu menu = factory.Build();
            if (menu == null)
            {
                DiagnosticLog.Warning("QBLibCredits: MenuFactory.Build returned null.");
                return null;
            }

            try
            {
                RectTransform menuRoot = menu.MenuObject.GetComponent<RectTransform>();
                if (menuRoot == null)
                {
                    DiagnosticLog.Warning("QBLibCredits: menu root has no RectTransform.");
                    return menu;
                }

                RectTransform content = CloneAuthorScrollView(menuRoot);
                if (content == null)
                {
                    DiagnosticLog.Warning("QBLibCredits: could not clone Location Author scroll view.");
                    return menu;
                }

                HideVanillaChildren(content);

                RectTransform section = BuildSection(content, entries, style);

                if (section != null)
                {
                    float sectionHeight = section.sizeDelta.y;
                    content.sizeDelta = new Vector2(content.sizeDelta.x, sectionHeight);
                    DiagnosticLog.Info("QBLibCredits: content height set to " +
                                       sectionHeight.ToString("F1") + ".");
                }

                DiagnosticLog.Info("QBLibCredits: built credits menu '" + menuName +
                                   "' with " + (entries != null ? entries.Count : 0) + " entries.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("QBLibCredits: BuildMenu threw: " + ex.Message);
            }

            return menu;
        }

        public static RectTransform BuildSection(
            RectTransform parent,
            List<CreditEntry> entries,
            CreditsStyle style = null)
        {
            return CreditsBuilder.BuildSection(parent, entries, style);
        }

        /// <summary>
        /// There are two GameObjects named "Location Author" in the scene.
        /// Only one has a Scroll View child. Walk FrameMenu's children,
        /// find each "Location Author", and pick the one whose Scroll View
        /// actually exists. Clone that one.
        /// </summary>
        private static RectTransform CloneAuthorScrollView(RectTransform menuRoot)
        {
            try
            {
                GameObject frameMenu = GameObject.Find("MenuGame/Canvas/FrameMenu");
                if (frameMenu == null)
                {
                    DiagnosticLog.Warning("QBLibCredits: FrameMenu not found.");
                    return null;
                }

                Transform chosenScrollView = null;

                for (int i = 0; i < frameMenu.transform.childCount; i++)
                {
                    Transform child = frameMenu.transform.GetChild(i);
                    if (child == null) continue;
                    if (child.name != "Location Author") continue;

                    Transform sv = child.Find("Scroll View");
                    if (sv != null)
                    {
                        chosenScrollView = sv;
                        DiagnosticLog.Info("QBLibCredits: found Scroll View under FrameMenu/" +
                                           child.name + " (index " + i + ").");
                        break;
                    }
                }

                if (chosenScrollView == null)
                {
                    DiagnosticLog.Warning("QBLibCredits: no Location Author with a Scroll View child.");
                    return null;
                }

                GameObject clone = UnityEngine.Object.Instantiate(chosenScrollView.gameObject, menuRoot);
                clone.name = "Scroll View";

                RectTransform cloneRt = clone.GetComponent<RectTransform>();
                if (cloneRt != null)
                {
                    cloneRt.anchorMin = new Vector2(0.5f, 1f);
                    cloneRt.anchorMax = new Vector2(0.5f, 1f);
                    cloneRt.pivot     = new Vector2(0.5f, 1f);
                    cloneRt.anchoredPosition = new Vector2(0f, -220f);
                    cloneRt.sizeDelta = new Vector2(700f, 530f);
                }

                clone.SetActive(true);

                RectTransform content = FindContentInClone(clone.transform);
                if (content == null)
                {
                    DiagnosticLog.Warning("QBLibCredits: clone has no content rect.");
                    return null;
                }

                DiagnosticLog.Info("QBLibCredits: cloned Location Author scroll view, " +
                                   "content='" + content.name + "'.");
                return content;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("QBLibCredits: CloneAuthorScrollView failed: " + ex.Message);
                return null;
            }
        }

        private static RectTransform FindContentInClone(Transform cloneRoot)
        {
            try
            {
                // Find the ScrollRect and return its content.
                var scrollRects = cloneRoot.GetComponentsInChildren<ScrollRect>(true);
                if (scrollRects != null)
                {
                    for (int i = 0; i < scrollRects.Length; i++)
                    {
                        var sr = scrollRects[i];
                        if (sr == null) continue;
                        if (sr.content == null) continue;
                        return sr.content;
                    }
                }

                // Fallback: look for a child named "Content".
                Transform t = cloneRoot.Find("Viewport/Content");
                if (t != null)
                {
                    RectTransform rt = t.GetComponent<RectTransform>();
                    if (rt != null) return rt;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("QBLibCredits: FindContentInClone failed: " + ex.Message);
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
    }
}