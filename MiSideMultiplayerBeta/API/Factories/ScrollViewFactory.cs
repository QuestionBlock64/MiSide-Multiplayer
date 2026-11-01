using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.API.UI
{
    /// <summary>
    /// Builds a scroll view for a menu.
    ///
    /// Preferred path: instantiate the cached template cloned from
    /// Location Author/Scroll View at MenuManager init. That gives us the
    /// game's own Viewport + MenuScrolac + Scrollbar Vertical + Frame, with
    /// all internal references wired correctly.
    ///
    /// Fallback path: build one from scratch if the template wasn't
    /// available (Location Author missing, or caching failed).
    /// </summary>
    public static class ScrollViewFactory
    {
        public const float DefaultViewportWidth  = 700f;
        public const float DefaultViewportHeight = 500f;

        public static RectTransform Build(
            RectTransform menuRoot,
            float width = DefaultViewportWidth,
            float height = DefaultViewportHeight,
            float yOffsetFromTop = -150f)
        {
            if (menuRoot == null) return null;

            // Prefer the cached template.
            GameObject clone = TryBuildFromTemplate(menuRoot, width, height, yOffsetFromTop);
            if (clone != null)
            {
                RectTransform contentFromTemplate = FindContentUnderTemplate(clone);
                if (contentFromTemplate != null)
                {
                    DiagnosticLog.Info("ScrollViewFactory: using cached template. " +
                                       "content='" + contentFromTemplate.name + "'.");
                    return contentFromTemplate;
                }

                DiagnosticLog.Warning("ScrollViewFactory: template instantiated but content not found; " +
                                      "destroying clone and falling back.");
                try { Object.Destroy(clone); } catch { }
            }

            return BuildFromScratch(menuRoot, width, height, yOffsetFromTop);
        }

        // ── Template path ──────────────────────────────────────────────────

        private static GameObject TryBuildFromTemplate(
            RectTransform menuRoot,
            float width,
            float height,
            float yOffsetFromTop)
        {
            var mgr = MenuManager.Instance;
            if (mgr == null) return null;

            GameObject clone = mgr.CreateScrollViewFromTemplate(menuRoot);
            if (clone == null) return null;

            try
            {
                RectTransform rt = clone.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0.5f, 1f);
                    rt.anchorMax = new Vector2(0.5f, 1f);
                    rt.pivot     = new Vector2(0.5f, 1f);
                    rt.anchoredPosition = new Vector2(0f, yOffsetFromTop);
                    rt.sizeDelta = new Vector2(width, height);
                }
            }
            catch { }

            return clone;
        }

        /// <summary>
        /// Walk the cloned subtree looking for the content RectTransform
        /// managed by MenuScrolac. That's the rect under Viewport that
        /// contains the credits text.
        /// </summary>
        private static RectTransform FindContentUnderTemplate(GameObject templateRoot)
        {
            if (templateRoot == null) return null;

            // Prefer the RectTransform that has a MenuScrolac component.
            try
            {
                Transform viewport = templateRoot.transform.Find("Viewport");
                if (viewport != null)
                {
                    // Direct children first.
                    for (int i = 0; i < viewport.childCount; i++)
                    {
                        Transform child = viewport.GetChild(i);
                        if (child == null) continue;

                        Component scrolac = child.GetComponent("MenuScrolac");
                        if (scrolac != null)
                        {
                            RectTransform rt = child.GetComponent<RectTransform>();
                            if (rt != null) return rt;
                        }
                    }

                    // Deeper search as a backup.
                    RectTransform deep = FindDescendantWithComponent<RectTransform>(viewport, "MenuScrolac");
                    if (deep != null) return deep;
                }

                // Absolute fallback: any RectTransform with MenuScrolac.
                RectTransform anywhere = FindDescendantWithComponent<RectTransform>(templateRoot.transform, "MenuScrolac");
                if (anywhere != null) return anywhere;
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("ScrollViewFactory: FindContentUnderTemplate failed: " + ex.Message);
            }

            return null;
        }

        private static T FindDescendantWithComponent<T>(Transform root, string componentTypeName)
            where T : Component
        {
            if (root == null || string.IsNullOrEmpty(componentTypeName)) return null;

            var stack = new System.Collections.Generic.Stack<Transform>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                if (t == null) continue;

                if (t.GetComponent(componentTypeName) != null)
                {
                    T c = t.GetComponent<T>();
                    if (c != null) return c;
                }

                for (int i = 0; i < t.childCount; i++)
                {
                    Transform child = t.GetChild(i);
                    if (child != null) stack.Push(child);
                }
            }

            return null;
        }

        // ── Fallback path ──────────────────────────────────────────────────

        private static RectTransform BuildFromScratch(
            RectTransform menuRoot,
            float width,
            float height,
            float yOffsetFromTop)
        {
            DiagnosticLog.Info("ScrollViewFactory: template unavailable, building from scratch.");

            GameObject scrollGo = new GameObject("Scroll View",
                Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
            RectTransform scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.SetParent(menuRoot, false);
            scrollRt.anchorMin = new Vector2(0.5f, 1f);
            scrollRt.anchorMax = new Vector2(0.5f, 1f);
            scrollRt.pivot     = new Vector2(0.5f, 1f);
            scrollRt.anchoredPosition = new Vector2(0f, yOffsetFromTop);
            scrollRt.sizeDelta = new Vector2(width, height);

            ScrollRect scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical   = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            GameObject viewportGo = new GameObject("Viewport",
                Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
            RectTransform viewportRt = viewportGo.GetComponent<RectTransform>();
            viewportRt.SetParent(scrollRt, false);
            viewportRt.anchorMin = Vector2.zero;
            viewportRt.anchorMax = Vector2.one;
            viewportRt.offsetMin = Vector2.zero;
            viewportRt.offsetMax = Vector2.zero;
            viewportRt.pivot = new Vector2(0f, 1f);

            Image viewportImg = viewportGo.AddComponent<Image>();
            viewportImg.color = Color.white;
            viewportImg.raycastTarget = true;

            Mask mask = viewportGo.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            GameObject contentGo = new GameObject("Content",
                Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
            RectTransform contentRt = contentGo.GetComponent<RectTransform>();
            contentRt.SetParent(viewportRt, false);
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot     = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0f, 0f);

            VerticalLayoutGroup vlg = contentGo.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 0f;

            RectOffset pad = new RectOffset();
            pad.left   = 20;
            pad.right  = 20;
            pad.top    = 20;
            pad.bottom = 40;
            vlg.padding = pad;

            ContentSizeFitter fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewportRt;
            scroll.content  = contentRt;

            return contentRt;
        }
    }
}