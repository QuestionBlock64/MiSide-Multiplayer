using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using MiSideMultiplayer.API.Credits;

namespace MiSideMultiplayer.Runtime.Credits
{
    internal sealed class CreditsInjectionHost : MonoBehaviour
    {
        public static List<CreditEntry> PendingEntries;
        public static CreditsStyle      PendingStyle;

        private const string TargetScene = "SceneMenu";

        // The real credits content lives here. Its owning "Location Author"
        // node is off by default; we turn it on ourselves when we inject.
        private const string CreditsRootPath =
            "MenuGame/Canvas/Location Author";

        // Diagnostics — flip to false once credits render correctly.
        private const bool VerboseCanvasScan = true;

        private static bool _injectRequested;
        private static bool _injectedThisVisit;
        private static bool _scannedThisVisit;

        public static void Request(List<CreditEntry> entries, CreditsStyle style = null)
        {
            PendingEntries     = entries;
            PendingStyle       = style;
            _injectRequested   = true;
            _injectedThisVisit = false;
            _scannedThisVisit  = false;
        }

        private void Update()
        {
            if (!_injectRequested) return;
            if (SceneManager.GetActiveScene().name != TargetScene) return;
            if (_injectedThisVisit) return;

            try
            {
                if (!_scannedThisVisit)
                {
                    _scannedThisVisit = true;
                    if (VerboseCanvasScan)
                        DumpCanvasScrollRects();
                }

                RectTransform content = FindCreditsContent();
                if (content == null)
                {
                    DiagnosticLog.Warning("CreditsInjectionHost: could not locate " +
                                          "credits content at '" + CreditsRootPath +
                                          "/Scroll View/Viewport/Content'.");
                    _injectRequested = false;
                    return;
                }

                DiagnosticLog.Info("CreditsInjectionHost: using content '" + GetPath(content) + "'.");

                HideVanillaChildren(content);

                RectTransform section = CreditsBuilder.BuildSection(content, PendingEntries, PendingStyle);
                if (section == null)
                {
                    DiagnosticLog.Warning("CreditsInjectionHost: CreditsBuilder.BuildSection returned null.");
                    _injectRequested = false;
                    return;
                }

                float sectionHeight = section.sizeDelta.y;
                if (sectionHeight > content.sizeDelta.y)
                    content.sizeDelta = new Vector2(content.sizeDelta.x, sectionHeight);

                DiagnosticLog.Info("CreditsInjectionHost: injected " +
                                   (PendingEntries != null ? PendingEntries.Count : 0) +
                                   " entries, height=" + sectionHeight.ToString("F1") + ".");

                _injectedThisVisit = true;
                _injectRequested   = false;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("CreditsInjectionHost: Update threw: " + ex.Message);
                _injectRequested = false;
            }
        }

        // ── Content discovery + activation ──────────────────────────────────

        /// <summary>
        /// Locates the credits content rect and makes sure its owning
        /// "Location Author" node is active so the player can see it.
        /// The vanilla game leaves 'Canvas/Location Author' off; we turn it
        /// on ourselves because the vanilla Author button routes to a
        /// different, stub menu ('FrameMenu/Location Author').
        /// </summary>
        private static RectTransform FindCreditsContent()
        {
            GameObject root = GameObject.Find(CreditsRootPath);
            if (root == null)
            {
                DiagnosticLog.Warning("CreditsInjectionHost: '" + CreditsRootPath + "' not found.");
                return null;
            }

            // Activate the credits menu so its scroll view is visible.
            if (!root.activeSelf)
            {
                root.SetActive(true);
                DiagnosticLog.Info("CreditsInjectionHost: activated '" + CreditsRootPath + "'.");
            }

            // Now walk to Scroll View/Viewport/Content under it.
            Transform sv = FindChildRecursive(root.transform, "Scroll View");
            if (sv == null)
            {
                DiagnosticLog.Warning("CreditsInjectionHost: no 'Scroll View' under '" +
                                      CreditsRootPath + "'.");
                return null;
            }

            Transform vp = FindChildRecursive(sv, "Viewport");
            if (vp == null)
            {
                DiagnosticLog.Warning("CreditsInjectionHost: no 'Viewport' under Scroll View.");
                return null;
            }

            Transform c = FindChildRecursive(vp, "Content");
            if (c == null)
            {
                DiagnosticLog.Warning("CreditsInjectionHost: no 'Content' under Viewport.");
                return null;
            }

            RectTransform rt = c.TryCast<RectTransform>();
            if (rt == null)
            {
                DiagnosticLog.Warning("CreditsInjectionHost: Content is not a RectTransform.");
                return null;
            }

            return rt;
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

        // ── Diagnostics ─────────────────────────────────────────────────────

        private static void DumpCanvasScrollRects()
        {
            try
            {
                GameObject canvasGo = GameObject.Find("MenuGame/Canvas");
                if (canvasGo == null)
                {
                    DiagnosticLog.Warning("CreditsInjectionHost SCAN: MenuGame/Canvas not found.");
                    return;
                }

                var all = canvasGo.GetComponentsInChildren<ScrollRect>(true);
                DiagnosticLog.Info("CreditsInjectionHost SCAN: canvas-wide ScrollRect count=" +
                                   (all == null ? -1 : all.Length));

                if (all == null) return;

                for (int i = 0; i < all.Length; i++)
                {
                    var sr = all[i];
                    if (sr == null)
                    {
                        DiagnosticLog.Info("  [" + i + "] <null>");
                        continue;
                    }

                    string srPath = GetPath(sr.transform);
                    string contentPath;
                    try
                    {
                        contentPath = sr.content == null ? "<NULL>" : GetPath(sr.content);
                    }
                    catch (Exception ex)
                    {
                        contentPath = "<threw: " + ex.Message + ">";
                    }

                    string ownerState = "";
                    try
                    {
                        if (sr.content != null)
                        {
                            Transform owner = FindAncestorNamed(sr.content, "Location Author");
                            if (owner != null)
                            {
                                ownerState = "  ownerActiveSelf=" + owner.gameObject.activeSelf +
                                             " ownerActiveInHierarchy=" + owner.gameObject.activeInHierarchy;
                            }
                        }
                    }
                    catch { }

                    DiagnosticLog.Info("  [" + i + "] on='" + srPath +
                                       "' content='" + contentPath + "'" + ownerState);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("CreditsInjectionHost SCAN failed: " + ex.Message);
            }
        }

        private static Transform FindAncestorNamed(Transform t, string name)
        {
            Transform p = t;
            int guard = 0;
            while (p != null && guard++ < 64)
            {
                if (p.name == name) return p;
                p = p.parent;
            }
            return null;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

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
            catch (Exception ex)
            {
                DiagnosticLog.Warning("CreditsInjectionHost: HideVanillaChildren failed: " + ex.Message);
            }
        }

        private static string GetPath(Transform t)
        {
            if (t == null) return "";
            string path = t.name;
            Transform p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 64)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }
    }
}