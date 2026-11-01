using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Coffee.UIEffects;
using UnityEngine;
using UnityEngine.UI;
using MiSideMultiplayer.Runtime.Menu.UI;

namespace MiSideMultiplayer.Runtime.Menu
{
    /// <summary>
    /// Adds the multiplayer wordmark to the game's existing NameGame logo
    /// canvas beside the game's NameGame logo, so it never overlaps or becomes
    /// part of the original logo hierarchy.
    /// </summary>
    internal static class MultiplayerMenuBranding
    {
        private const string ResourceName = "MiSideMultiplayer.Runtime.UI.multiplayer.png";
        private const string BadgeObjectName = "Multiplayer";
        private const string ModeSelectName = "Location Mode Select";

        // Position the wordmark relative to the rendered logo letters.
        private const float LogoWidthFraction = 0.92f;
        private const float CenterRightFraction = 0.325f;
        private const float VerticalOffsetPixels = -4f;
        private const float FallbackLogoLeftFraction = 0.10f;
        private const float FallbackLogoBottomFraction = 0.765f;
        private const float FallbackLogoWidthFraction = 0.26f;
        private const float FallbackLogoHeightFraction = 0.11f;
        private const float LogoGapFraction = 0.025f;
        private const float SourceAspect = 3f;
        private const float BadgeFadeDuration = 0.35f;
        private const float BadgeShineOpacity = 0.5f;
        private const float BadgeMotionLegDuration = 2.4f;
        private const float BadgeMotionRestDuration = 0.5f;
        private const float BadgeMotionX = 7f;
        private const float BadgeMotionY = 1.5f;
        private const float LogoMotionQuietWindow = 0.18f;
        private const float LogoMotionPositionThreshold = 0.08f;
        private const float LogoMotionRotationThreshold = 0.08f;
        private const float MenuItemArrivalTolerance = 2f;
        private static readonly bool UseMeasuredScreenPlacement = true;

        private static Texture2D _texture;
        private static Sprite _sprite;
        private static GameObject _badgeObject;
        private static Image _badgeImage;
        private static CanvasGroup _badgeCanvasGroup;
        private static CanvasGroupFader _badgeFader;
        private static UIShiny _badgeUIShiny;
        private static bool _badgeShinyConfigured;
        private static RectTransform _badgeRect;
        private static Vector2 _badgeBasePosition;
        private static float _badgeMotionElapsed;
        private static GameObject _overlayCanvasObject;
        private static Canvas _overlayCanvas;
        private static bool _loadAttempted;
        private static bool _menuIntroPending;
        private static MenuLocation _introMenuLocation;
        private static Transform _introNameGame;
        private static RectTransform _introLetterE;
        private static bool _logoMotionSampled;
        private static bool _logoMotionObserved;
        private static Vector3 _lastLogoLetterPosition;
        private static float _lastLogoLetterRotation;
        private static float _lastLogoMotionAt;
        private static bool _isMultiplayerMenuActive;
        private static bool _badgeVisibilityRequested;
        private static bool _faderInitialized;
        private static bool _deactivateAfterFadeOut;

        public static bool IsMultiplayerMenuName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                   (name == ModeSelectName ||
                    name.StartsWith("Location Multiplayer", StringComparison.Ordinal) ||
                    name.StartsWith("Location MP", StringComparison.Ordinal));
        }

        public static void OnSceneLoaded(string sceneName)
        {
            if (string.Equals(sceneName, "SceneMenu", StringComparison.Ordinal))
            {
                _menuIntroPending = true;
                ResetMenuIntroWatch();
                _introNameGame = FindNameGameInScene();
                CacheIntroLetter();
                _isMultiplayerMenuActive = false;
                _badgeVisibilityRequested = false;
                ForceHideBadge();
                DiagnosticLog.Info("[MENU-BRANDING] SceneMenu loaded; waiting for the initial menu/logo animation to settle.");
                return;
            }

            _menuIntroPending = false;
            ResetMenuIntroWatch();
            _isMultiplayerMenuActive = false;
            _badgeVisibilityRequested = false;
            Hide();
        }

        public static void OnLocationActivated(MenuLocation location)
        {
            if (location == null) return;

            string name = null;
            try { name = location.gameObject != null ? location.gameObject.name : null; }
            catch { }

            bool isMultiplayerMenu = IsMultiplayerMenuName(name);
            bool wasRequested = _badgeVisibilityRequested;
            _isMultiplayerMenuActive = isMultiplayerMenu;
            DiagnosticLog.Info("[MENU-BRANDING] location activated: name='" + name +
                               "' multiplayer=" + isMultiplayerMenu + ".");

            if (!isMultiplayerMenu)
            {
                _badgeVisibilityRequested = false;
                Hide();
                return;
            }

            if (_menuIntroPending)
            {
                if (_introMenuLocation == null)
                    _introMenuLocation = location;
                if (_introNameGame == null)
                    _introNameGame = FindNameGame(location);
                CacheIntroLetter();
                ForceHideBadge();
            }

            try
            {
                Transform nameGame = FindNameGame(location);
                if (nameGame == null)
                {
                    DiagnosticLog.Warning("[MENU-BRANDING] NameGame not found for '" + name + "'.");
                    _badgeVisibilityRequested = false;
                    Hide();
                    return;
                }

                if (!EnsureBadge(nameGame))
                {
                    _badgeVisibilityRequested = false;
                    Hide();
                    return;
                }

                RectTransform nameRect = nameGame as RectTransform;
                Transform canvasTransform = nameGame.parent;
                RectTransform canvasRect = canvasTransform as RectTransform;

                // Use an isolated overlay and measured screen bounds so the
                // badge sits beside the logo without changing game UI objects.
                if (UseMeasuredScreenPlacement)
                {
                    if (_overlayCanvasObject == null || _overlayCanvas == null)
                    {
                        _overlayCanvasObject = new GameObject("MiSideMultiplayer.BrandingOverlay");
                        Transform menuRoot = nameGame.root;
                        if (menuRoot != null)
                            _overlayCanvasObject.transform.SetParent(menuRoot, false);

                        _overlayCanvas = _overlayCanvasObject.AddComponent<Canvas>();
                        _overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                        _overlayCanvas.overrideSorting = true;
                        _overlayCanvas.sortingOrder = 1000;

                        CanvasScaler scaler = _overlayCanvasObject.AddComponent<CanvasScaler>();
                        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                        scaler.scaleFactor = 1f;
                    }

                    if (_badgeObject.transform.parent != _overlayCanvasObject.transform)
                        _badgeObject.transform.SetParent(_overlayCanvasObject.transform, false);

                    RectTransform probeRect = _badgeObject.GetComponent<RectTransform>();
                    if (probeRect == null)
                    {
                        DiagnosticLog.Warning("[MENU-BRANDING] probe object has no RectTransform.");
                        Hide();
                        return;
                    }

                    bool measuredLogo = TryGetLogoScreenBounds(nameGame, canvasTransform, out Rect logoBounds);
                    if (!measuredLogo)
                    {
                        float left = Screen.width * FallbackLogoLeftFraction;
                        float bottom = Screen.height * FallbackLogoBottomFraction;
                        logoBounds = Rect.MinMaxRect(
                            left,
                            bottom,
                            left + Screen.width * FallbackLogoWidthFraction,
                            bottom + Screen.height * FallbackLogoHeightFraction);
                        DiagnosticLog.Warning("[MENU-BRANDING] logo has no measurable Graphic/Renderer; " +
                                              "using reference-screen bounds=" + logoBounds +
                                              " for screen=" + Screen.width + "x" + Screen.height + ".");
                    }

                    float probeWidth = Mathf.Min(logoBounds.width * LogoWidthFraction, Screen.width * 0.60f);
                    float probeHeight = probeWidth / SourceAspect;
                    Vector2 badgeScreenCenter = new Vector2(
                        logoBounds.xMin + logoBounds.width * (0.5f + CenterRightFraction),
                        logoBounds.yMin - VerticalOffsetPixels);
                    Vector2 badgeCanvasPosition = badgeScreenCenter -
                        new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                    probeRect.anchorMin = new Vector2(0.5f, 0.5f);
                    probeRect.anchorMax = probeRect.anchorMin;
                    probeRect.pivot = new Vector2(0.5f, 0.5f);
                    probeRect.localScale = Vector3.one;
                    probeRect.localRotation = Quaternion.identity;
                    probeRect.sizeDelta = new Vector2(probeWidth, probeHeight);
                    probeRect.anchoredPosition = badgeCanvasPosition;
                    _badgeRect = probeRect;
                    _badgeBasePosition = badgeCanvasPosition;
                    _badgeObject.transform.SetAsLastSibling();
                    RequestBadgeVisibility(wasRequested);

                    DiagnosticLog.Info("[MENU-BRANDING] logo placement: location='" + name +
                                       "' measuredLogo=" + measuredLogo +
                                       " nameGameType='" + nameGame.GetType().FullName +
                                       "' nameGameParent='" + (canvasTransform != null
                                           ? canvasTransform.name
                                           : "<none>") + "' parentType='" +
                                       (canvasTransform != null
                                           ? canvasTransform.GetType().FullName
                                           : "<none>") + "' parentIsRect=" +
                                       (canvasRect != null) + " canvas=" +
                                       _overlayCanvas.name + " sortingOrder=" +
                                       _overlayCanvas.sortingOrder + " renderMode=" +
                                       _overlayCanvas.renderMode + " canvasScale=1.000" +
                                       " screen=" + Screen.width + "x" + Screen.height +
                                       " logoBounds=" + logoBounds +
                                       " badgeScreenCenter=" + badgeScreenCenter +
                                       " badgeCanvasPosition=" + badgeCanvasPosition +
                                       " image=" + (_badgeImage != null && _badgeImage.sprite != null
                                           ? _badgeImage.sprite.rect.size.ToString()
                                           : "<null>") + " object='" + _badgeObject.name +
                                       "' activeSelf=" + _badgeObject.activeSelf +
                                       " activeInHierarchy=" + _badgeObject.activeInHierarchy +
                                       " imageEnabled=" + (_badgeImage != null && _badgeImage.enabled) +
                                       " anchoredPosition=" + probeRect.anchoredPosition +
                                       " size=" + probeRect.rect.size + ".");
                    return;
                }

                if (nameRect == null || canvasRect == null)
                {
                    DiagnosticLog.Warning("[MENU-BRANDING] NameGame or its parent Canvas has no RectTransform. " +
                                          "NameGame='" + nameGame.name + "' nameGameType='" +
                                          nameGame.GetType().FullName + "' parent='" + canvasTransform.name +
                                          "' parentType='" + canvasTransform.GetType().FullName + "'.");
                    Hide();
                    return;
                }

                // Keep the badge outside NameGame: it is a sibling on Canvas,
                // positioned next to the visible logo letters.
                if (_badgeObject.transform.parent != canvasTransform)
                    _badgeObject.transform.SetParent(canvasTransform, false);

                RectTransform badgeRect = _badgeObject.GetComponent<RectTransform>();
                if (badgeRect != null)
                {
                    if (!TryGetLogoLetterBounds(nameRect, canvasRect, out Rect logoBounds))
                    {
                        DiagnosticLog.Warning("[MENU-BRANDING] Logo letter bounds not found under '" +
                                              nameGame.name + "'.");
                        Hide();
                        return;
                    }

                    float gap = Mathf.Max(logoBounds.width * LogoGapFraction, 6f);
                    float availableRight = canvasRect.rect.xMax - logoBounds.xMax - gap;
                    float width = Mathf.Min(logoBounds.width * LogoWidthFraction, availableRight);
                    if (width <= 0f)
                    {
                        DiagnosticLog.Warning("[MENU-BRANDING] no Canvas space beside logo. canvas=" +
                                              canvasRect.rect + " logo=" + logoBounds +
                                              " availableRight=" + availableRight.ToString("F1") + ".");
                        Hide();
                        return;
                    }
                    float height = width / SourceAspect;
                    Vector2 badgeCenter = new Vector2(
                        logoBounds.xMax + gap + width * 0.5f,
                        logoBounds.center.y);

                    DiagnosticLog.Info("[MENU-BRANDING] layout: logo='" + nameGame.name +
                                       "' canvas='" + canvasRect.name + "' canvasRect=" + canvasRect.rect +
                                       " logoBounds=" + logoBounds + " availableRight=" +
                                       availableRight.ToString("F1") + " desiredCenter=" + badgeCenter +
                                       " desiredSize=" + new Vector2(width, height) + ".");

                    badgeRect.anchorMin = new Vector2(0.5f, 0.5f);
                    badgeRect.anchorMax = badgeRect.anchorMin;
                    badgeRect.pivot = new Vector2(0.5f, 0.5f);
                    // A previously created badge may carry the tiny scale from
                    // the old screen-space placement. Reset it after parenting
                    // so the measured logo-relative dimensions render as set.
                    badgeRect.localScale = Vector3.one;
                    badgeRect.localRotation = Quaternion.identity;
                    badgeRect.anchoredPosition = UseMeasuredScreenPlacement
                        ? Vector2.zero
                        : badgeCenter - canvasRect.rect.center;
                    badgeRect.sizeDelta = new Vector2(width, height);
                }

                _badgeObject.transform.SetAsLastSibling();
                _badgeRect = badgeRect;
                _badgeBasePosition = badgeRect != null ? badgeRect.anchoredPosition : Vector2.zero;
                RequestBadgeVisibility(wasRequested);
                Vector3[] badgeCorners = new Vector3[4];
                badgeRect.GetWorldCorners(badgeCorners);
                DiagnosticLog.Info("[MENU-BRANDING] measuredPlacement=" + UseMeasuredScreenPlacement +
                                   " object='" + _badgeObject.name + "' parent='" +
                                   _badgeObject.transform.parent.name + "' activeSelf=" +
                                   _badgeObject.activeSelf + " activeInHierarchy=" +
                                   _badgeObject.activeInHierarchy + " imageEnabled=" +
                                   (_badgeImage != null && _badgeImage.enabled) + " sprite='" +
                                   (_badgeImage != null && _badgeImage.sprite != null
                                       ? _badgeImage.sprite.name
                                       : "<null>") + "' anchoredPosition=" + badgeRect.anchoredPosition +
                                   " size=" + badgeRect.rect.size + " worldCorners=[" +
                                   badgeCorners[0] + "; " + badgeCorners[1] + "; " +
                                   badgeCorners[2] + "; " + badgeCorners[3] + "].");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[MENU-BRANDING] could not show multiplayer badge: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryGetLogoScreenBounds(Transform logo, Transform sourceCanvas, out Rect bounds)
        {
            bounds = default;
            if (logo == null) return false;

            Camera camera = null;
            try
            {
                Canvas canvas = sourceCanvas != null ? sourceCanvas.GetComponent<Canvas>() : null;
                if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    camera = canvas.worldCamera;
            }
            catch { }

            bool found = false;
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            int visualComponents = 0;
            Vector3[] corners = new Vector3[4];
            Stack<Transform> pending = new Stack<Transform>();

            for (int i = 0; i < logo.childCount; i++)
            {
                Transform letter = logo.GetChild(i);
                if (letter == null || !IsLogoLetter(letter.name)) continue;

                bool letterHadVisual = false;
                pending.Clear();
                pending.Push(letter);
                while (pending.Count > 0)
                {
                    Transform node = pending.Pop();
                    if (node == null || !node.gameObject.activeInHierarchy) continue;

                    Graphic graphic = node.GetComponent<Graphic>();
                    if (graphic != null && graphic.enabled && graphic.rectTransform != null)
                    {
                        graphic.rectTransform.GetWorldCorners(corners);
                        for (int corner = 0; corner < corners.Length; corner++)
                            IncludeWorldPoint(corners[corner], camera, ref found,
                                ref minX, ref minY, ref maxX, ref maxY);
                        letterHadVisual = true;
                        visualComponents++;
                    }

                    Renderer renderer = node.GetComponent<Renderer>();
                    if (renderer != null && renderer.enabled)
                    {
                        Bounds rendererBounds = renderer.bounds;
                        Vector3 min = rendererBounds.min;
                        Vector3 max = rendererBounds.max;
                        for (int corner = 0; corner < 8; corner++)
                        {
                            Vector3 world = new Vector3(
                                (corner & 1) == 0 ? min.x : max.x,
                                (corner & 2) == 0 ? min.y : max.y,
                                (corner & 4) == 0 ? min.z : max.z);
                            IncludeWorldPoint(world, camera, ref found,
                                ref minX, ref minY, ref maxX, ref maxY);
                        }
                        letterHadVisual = true;
                        visualComponents++;
                    }

                    for (int childIndex = 0; childIndex < node.childCount; childIndex++)
                        pending.Push(node.GetChild(childIndex));
                }

                if (!letterHadVisual)
                {
                    RectTransform fallbackRect = letter.GetComponent<RectTransform>();
                    if (fallbackRect != null)
                    {
                        fallbackRect.GetWorldCorners(corners);
                        for (int corner = 0; corner < corners.Length; corner++)
                            IncludeWorldPoint(corners[corner], camera, ref found,
                                ref minX, ref minY, ref maxX, ref maxY);
                    }
                    else
                    {
                        IncludeWorldPoint(letter.position, camera, ref found,
                            ref minX, ref minY, ref maxX, ref maxY);
                    }
                }
            }

            if (!found || maxX <= minX || maxY <= minY)
            {
                DiagnosticLog.Warning("[MENU-BRANDING] invalid logo screen bounds. logo='" +
                                      logo.name + "' children=" + logo.childCount +
                                      " visualComponents=" + visualComponents +
                                      " screen=" + Screen.width + "x" + Screen.height + ".");
                return false;
            }

            bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        private static void IncludeWorldPoint(
            Vector3 world,
            Camera camera,
            ref bool found,
            ref float minX,
            ref float minY,
            ref float maxX,
            ref float maxY)
        {
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, world);
            if (float.IsNaN(screen.x) || float.IsNaN(screen.y) ||
                float.IsInfinity(screen.x) || float.IsInfinity(screen.y)) return;

            minX = Mathf.Min(minX, screen.x);
            minY = Mathf.Min(minY, screen.y);
            maxX = Mathf.Max(maxX, screen.x);
            maxY = Mathf.Max(maxY, screen.y);
            found = true;
        }

        private static Transform FindNameGame(MenuLocation location)
        {
            try
            {
                GameObject nameGame = GameObject.Find("MenuGame/Canvas/NameGame");
                if (nameGame != null) return nameGame.transform;
            }
            catch { }

            try
            {
                Transform canvas = location.transform.root.Find("MenuGame/Canvas/NameGame");
                if (canvas != null) return canvas;
            }
            catch { }

            return null;
        }

        private static bool EnsureBadge(Transform nameGame)
        {
            if (!LoadSprite()) return false;

            try
            {
                bool hadBadge = _badgeObject != null;
                GameObject existing = null;
                Transform existingChild = nameGame != null ? nameGame.Find(BadgeObjectName) : null;
                if (existingChild == null && nameGame != null)
                {
                    // Reuse the earlier development badge if a plugin hot
                    // reload left it attached to the logo.
                    existingChild = nameGame.Find("MiSideMultiplayer.MenuBadge");
                    if (existingChild != null) existingChild.name = BadgeObjectName;
                }

                if (existingChild != null) existing = existingChild.gameObject;
                if (existing != null)
                {
                    _badgeObject = existing;
                    _badgeImage = existing.GetComponent<Image>();
                }

                if (_badgeObject == null)
                {
                    _badgeObject = new GameObject(BadgeObjectName);
                    _badgeObject.AddComponent<RectTransform>();
                    _badgeObject.AddComponent<CanvasRenderer>();
                    _badgeObject.AddComponent<Image>();
                    _badgeImage = _badgeObject.GetComponent<Image>();
                }

                if (_badgeObject.GetComponent<RectTransform>() == null)
                {
                    UnityEngine.Object.Destroy(_badgeObject);
                    _badgeObject = new GameObject(BadgeObjectName);
                    hadBadge = false;
                    _badgeObject.AddComponent<RectTransform>();
                    _badgeObject.AddComponent<CanvasRenderer>();
                    _badgeObject.AddComponent<Image>();
                    _badgeImage = _badgeObject.GetComponent<Image>();
                }

                _badgeRect = _badgeObject.GetComponent<RectTransform>();
                if (_badgeObject.GetComponent<CanvasRenderer>() == null)
                    _badgeObject.AddComponent<CanvasRenderer>();

                if (_badgeImage == null) _badgeImage = _badgeObject.AddComponent<Image>();
                _badgeImage.sprite = _sprite;
                _badgeImage.preserveAspect = true;
                _badgeImage.raycastTarget = false;
                _badgeImage.maskable = false;
                _badgeImage.color = Color.white;

                _badgeCanvasGroup = _badgeObject.GetComponent<CanvasGroup>();
                if (_badgeCanvasGroup == null)
                    _badgeCanvasGroup = _badgeObject.AddComponent<CanvasGroup>();

                _badgeFader = _badgeObject.GetComponent<CanvasGroupFader>();
                if (_badgeFader == null)
                    _badgeFader = _badgeObject.AddComponent<CanvasGroupFader>();

                _badgeUIShiny = _badgeObject.GetComponent<UIShiny>();
                if (_badgeUIShiny == null)
                {
                    _badgeUIShiny = _badgeObject.AddComponent<UIShiny>();
                    _badgeShinyConfigured = false;
                }
                if (!hadBadge)
                {
                    _faderInitialized = false;
                    _badgeShinyConfigured = false;
                }
                ConfigureBadgeShiny(nameGame);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerMenuBranding: badge creation failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        public static void Tick()
        {
            if (!_menuIntroPending &&
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "SceneMenu" &&
                _introMenuLocation == null)
            {
                _menuIntroPending = true;
                ResetMenuIntroWatch();
                _introNameGame = FindNameGameInScene();
                CacheIntroLetter();
                ForceHideBadge();
            }

            if (_menuIntroPending)
            {
                if (_introNameGame == null)
                    _introNameGame = FindNameGameInScene();
                CacheIntroLetter();

                bool logoVisible = IsIntroLetterVisible();
                if (logoVisible)
                    ObserveLogoMotion();

                bool menuItemsSettled = IsIntroLocationAnimationSettled();
                bool logoResting = !_logoMotionObserved ||
                                   Time.unscaledTime - _lastLogoMotionAt >= LogoMotionQuietWindow;
                if (_isMultiplayerMenuActive && _badgeVisibilityRequested &&
                    logoVisible && menuItemsSettled && logoResting)
                {
                    _menuIntroPending = false;
                    DiagnosticLog.Info("[MENU-BRANDING] initial menu animation settled; fading in multiplayer logo.");
                    FadeInBadge();
                }
            }

            if (_deactivateAfterFadeOut && _badgeCanvasGroup != null &&
                _badgeCanvasGroup.alpha <= 0.001f && _badgeObject != null)
            {
                _badgeObject.SetActive(false);
                _deactivateAfterFadeOut = false;
            }

            UpdateBadgeMotion();
        }

        private static void ResetMenuIntroWatch()
        {
            _introMenuLocation = null;
            _introNameGame = null;
            _introLetterE = null;
            _logoMotionSampled = false;
            _logoMotionObserved = false;
            _lastLogoLetterPosition = Vector3.zero;
            _lastLogoLetterRotation = 0f;
            _lastLogoMotionAt = 0f;
        }

        private static Transform FindNameGameInScene()
        {
            try
            {
                GameObject nameGame = GameObject.Find("MenuGame/Canvas/NameGame");
                return nameGame != null ? nameGame.transform : null;
            }
            catch
            {
                return null;
            }
        }

        private static void CacheIntroLetter()
        {
            if (_introLetterE != null || _introNameGame == null) return;
            try
            {
                _introLetterE = _introNameGame.Find("E") as RectTransform;
            }
            catch
            {
                _introLetterE = null;
            }
        }

        private static bool IsIntroLetterVisible()
        {
            if (_introLetterE == null || !_introLetterE.gameObject.activeInHierarchy)
                return false;

            try
            {
                Image image = _introLetterE.GetComponent<Image>();
                if (image == null)
                    image = _introLetterE.GetComponentInChildren<Image>(true);
                return image == null || (image.enabled && image.color.a > 0.01f);
            }
            catch
            {
                return false;
            }
        }

        private static void ObserveLogoMotion()
        {
            Vector3 position = _introLetterE.anchoredPosition3D;
            float rotation = _introLetterE.localEulerAngles.z;
            float now = Time.unscaledTime;

            if (_logoMotionSampled &&
                ((position - _lastLogoLetterPosition).sqrMagnitude >=
                 LogoMotionPositionThreshold * LogoMotionPositionThreshold ||
                 Mathf.Abs(Mathf.DeltaAngle(_lastLogoLetterRotation, rotation)) >=
                 LogoMotionRotationThreshold))
            {
                _logoMotionObserved = true;
                _lastLogoMotionAt = now;
            }
            else if (!_logoMotionSampled)
            {
                _lastLogoMotionAt = now;
            }

            _logoMotionSampled = true;
            _lastLogoLetterPosition = position;
            _lastLogoLetterRotation = rotation;
        }

        private static bool IsIntroLocationAnimationSettled()
        {
            if (_introMenuLocation == null) return false;

            try
            {
                if (_introMenuLocation.gameObject == null ||
                    !_introMenuLocation.gameObject.activeInHierarchy)
                    return true;

                var items = _introMenuLocation.objects;
                if (items == null || items.Count == 0)
                    return true;

                for (int i = 0; i < items.Count; i++)
                {
                    RectTransform item = items[i];
                    if (item != null &&
                        Mathf.Abs(item.anchoredPosition.x) > MenuItemArrivalTolerance)
                        return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[MENU-BRANDING] could not inspect menu entrance animation: " +
                                      ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static void RequestBadgeVisibility(bool wasRequested)
        {
            _badgeVisibilityRequested = true;
            if (_menuIntroPending)
            {
                ForceHideBadge();
                return;
            }

            if (!wasRequested || _deactivateAfterFadeOut ||
                _badgeObject == null || !_badgeObject.activeSelf)
            {
                FadeInBadge();
                return;
            }

            _deactivateAfterFadeOut = false;
            if (_badgeFader != null) _badgeFader.SetVisible(true);
        }

        private static void ForceHideBadge()
        {
            if (_badgeObject == null || _badgeCanvasGroup == null || _badgeFader == null) return;
            if (!_badgeObject.activeSelf) _badgeObject.SetActive(true);
            _badgeCanvasGroup.alpha = 0f;
            _badgeCanvasGroup.interactable = false;
            _badgeCanvasGroup.blocksRaycasts = false;
            _badgeFader.FadeDuration = BadgeFadeDuration;
            _badgeFader.Initialize(_badgeCanvasGroup, false);
            _faderInitialized = true;
            _deactivateAfterFadeOut = false;
        }

        private static void FadeInBadge()
        {
            if (_badgeObject == null || _badgeCanvasGroup == null || _badgeFader == null) return;
            if (!_badgeObject.activeSelf) _badgeObject.SetActive(true);

            _badgeFader.FadeDuration = BadgeFadeDuration;
            if (!_faderInitialized)
            {
                _badgeFader.Initialize(_badgeCanvasGroup, false);
                _faderInitialized = true;
            }
            _deactivateAfterFadeOut = false;
            _badgeFader.SetVisible(true);
            _badgeMotionElapsed = 0f;
            if (_badgeRect != null)
            {
                _badgeRect.anchoredPosition = _badgeBasePosition;
                _badgeRect.localRotation = Quaternion.identity;
            }
            PlayBadgeShine();
        }

        private static void FadeOutBadge()
        {
            if (_badgeObject == null || _badgeCanvasGroup == null || _badgeFader == null) return;
            if (!_badgeObject.activeSelf) return;

            _badgeFader.FadeDuration = BadgeFadeDuration;
            if (!_faderInitialized)
            {
                _badgeFader.Initialize(_badgeCanvasGroup, false);
                _faderInitialized = true;
                _badgeObject.SetActive(false);
                return;
            }

            _deactivateAfterFadeOut = true;
            _badgeFader.SetVisible(false);
        }

        private static void ConfigureBadgeShiny(Transform nameGame)
        {
            if (_badgeUIShiny == null || _badgeShinyConfigured) return;

            try
            {
                Transform eLetter = nameGame != null ? nameGame.Find("E") : null;
                UIShiny source = eLetter != null ? eLetter.GetComponent<UIShiny>() : null;
                if (source != null)
                {
                    _badgeUIShiny.width = source.width;
                    _badgeUIShiny.softness = source.softness;
                    _badgeUIShiny.rotation = source.rotation;
                    _badgeUIShiny.brightness = BadgeShineOpacity;
                    _badgeUIShiny.gloss = source.gloss;
                    _badgeUIShiny.effectArea = source.effectArea;
                }
                else
                {
                    _badgeUIShiny.brightness = BadgeShineOpacity;
                }

                EffectPlayer sourcePlayer = source != null ? source.effectPlayer : null;
                EffectPlayer badgePlayer = _badgeUIShiny.effectPlayer;
                badgePlayer.duration = sourcePlayer != null && sourcePlayer.duration > 0.01f
                    ? sourcePlayer.duration
                    : 0.75f;
                badgePlayer.loopDelay = 2f;
                badgePlayer.loop = true;
                badgePlayer.updateMode = AnimatorUpdateMode.UnscaledTime;
                badgePlayer.Stop(true);
                _badgeUIShiny.effectFactor = 0f;
                _badgeShinyConfigured = true;
                DiagnosticLog.Info("[MENU-BRANDING] copied NameGame/E UIShiny settings to the badge image.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[MENU-BRANDING] could not copy NameGame/E UIShiny settings: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PlayBadgeShine()
        {
            if (_badgeUIShiny == null) return;
            try
            {
                _badgeUIShiny.enabled = true;
                _badgeUIShiny.brightness = BadgeShineOpacity;
                _badgeUIShiny.effectFactor = 0f;
                _badgeUIShiny.effectPlayer.Play(true);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[MENU-BRANDING] UIShiny.Play failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void UpdateBadgeMotion()
        {
            if (_badgeRect == null || _badgeObject == null || !_badgeObject.activeInHierarchy ||
                _badgeCanvasGroup == null || _badgeCanvasGroup.alpha <= 0.001f) return;

            float cycleDuration = BadgeMotionLegDuration * 2f + BadgeMotionRestDuration;
            _badgeMotionElapsed = Mathf.Repeat(
                _badgeMotionElapsed + Time.unscaledDeltaTime, cycleDuration);

            float movement;
            if (_badgeMotionElapsed < BadgeMotionLegDuration)
            {
                movement = Mathf.SmoothStep(0f, 1f, _badgeMotionElapsed / BadgeMotionLegDuration);
            }
            else if (_badgeMotionElapsed < BadgeMotionLegDuration * 2f)
            {
                float returnProgress =
                    (_badgeMotionElapsed - BadgeMotionLegDuration) / BadgeMotionLegDuration;
                movement = 1f - Mathf.SmoothStep(0f, 1f, returnProgress);
            }
            else
            {
                movement = 0f;
            }

            _badgeRect.anchoredPosition = _badgeBasePosition +
                new Vector2(BadgeMotionX, BadgeMotionY) * movement;
            _badgeRect.localRotation = Quaternion.identity;
        }

        private static bool TryGetLogoLetterBounds(
            RectTransform logo,
            RectTransform coordinateSpace,
            out Rect bounds)
        {
            bounds = default;
            if (logo == null || coordinateSpace == null) return false;

            bool found = false;
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            Vector3[] corners = new Vector3[4];

            for (int i = 0; i < logo.childCount; i++)
            {
                Transform child = logo.GetChild(i);
                if (child == null || !IsLogoLetter(child.name)) continue;

                RectTransform letter = child as RectTransform;
                if (letter == null) continue;
                letter.GetWorldCorners(corners);
                for (int corner = 0; corner < corners.Length; corner++)
                {
                    Vector3 local = coordinateSpace.InverseTransformPoint(corners[corner]);
                    minX = Mathf.Min(minX, local.x);
                    minY = Mathf.Min(minY, local.y);
                    maxX = Mathf.Max(maxX, local.x);
                    maxY = Mathf.Max(maxY, local.y);
                }
                found = true;
            }

            if (!found || maxX <= minX || maxY <= minY) return false;
            bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        private static bool IsLogoLetter(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return string.Equals(name, "M", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "I1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "S", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "I2", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "D", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "E", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LoadSprite()
        {
            if (_sprite != null) return true;
            if (_loadAttempted) return false;
            _loadAttempted = true;

            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        DiagnosticLog.Warning("MultiplayerMenuBranding: embedded resource '" +
                                              ResourceName + "' was not found.");
                        return false;
                    }

                    byte[] bytes = new byte[stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) break;
                        offset += read;
                    }

                    _texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    bool loaded = UnityEngine.ImageConversion.LoadImage(_texture, bytes);
                    if (!loaded)
                    {
                        DiagnosticLog.Warning("MultiplayerMenuBranding: Multiplayer.png could not be decoded.");
                        return false;
                    }

                    _sprite = Sprite.Create(_texture,
                        new Rect(0f, 0f, _texture.width, _texture.height),
                        new Vector2(0.5f, 0.5f));
                    DiagnosticLog.Info("MultiplayerMenuBranding: loaded Multiplayer.png (" +
                        _texture.width + "x" + _texture.height + ").");
                    return _sprite != null;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerMenuBranding: image load failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static void Hide()
        {
            try { FadeOutBadge(); }
            catch { }
        }
    }
}
