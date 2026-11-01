using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using MiSideMultiplayer.Runtime.Menu.UI;
using MiSideMultiplayer.Runtime.Lobby;

namespace MiSideMultiplayer
{
    public sealed class PlayerDisplayName
    {
        private const float MaxLookDistance = 15f;
        private const float FadeInSeconds   = 0.15f;
        private const float FadeOutSeconds  = 0.25f;
        private const int   FontSize        = 18;
        private const int   CanvasSortOrder = 32000;

        // NEW — color pool indexed by joinOrder % 4. Lobby caps at 4, so no wraparound.
        private static readonly Color[] ColorPool =
        {
            new Color32(0xe8, 0x00, 0x03, 0xff), // 0  Red     #e80003
            new Color32(0xe4, 0x95, 0xec, 0xff), // 1  Violet  #e495ec
            new Color32(0x18, 0x9e, 0xf6, 0xff), // 2  Cyan    #189ef6
            new Color32(0x7f, 0x09, 0x5e, 0xff), // 3  Purple  #7f095e
        };

        private GameObject  canvasRoot;
        private Canvas      canvas;
        private Text        label;
        private TextFader   fader;
        private bool        built;
        private bool        buildFailed;

        private PuppetController currentTarget;
        private string           currentName;

        private static FieldInfo displayNameField;
        private static FieldInfo playerIdField;              // NEW — reflection handle for playerId
        private static bool      displayNameFieldResolved;
        private static bool      playerIdFieldResolved;      // NEW

        private readonly List<PuppetController> lookupScratch = new List<PuppetController>(8);

        public void Tick()
        {
            if (buildFailed)
                return;

            if (!built && !TryBuild())
                return;

            Camera cam = Camera.main;
            if (cam == null)
            {
                Dismiss();
                return;
            }

            lookupScratch.Clear();
            foreach (PuppetController p in PuppetRegistry.AllPuppets)
            {
                if (p != null && p.GameObject != null)
                    lookupScratch.Add(p);
            }
            if (lookupScratch.Count == 0)
            {
                Dismiss();
                return;
            }

            PuppetController hit = RaycastPuppet(cam);
            if (hit == null)
            {
                Dismiss();
                return;
            }

            if (hit == currentTarget)
                return;

            string name = GetDisplayName(hit);
            if (string.IsNullOrEmpty(name))
                name = "Player";

            // NEW — resolve the color for this puppet's owner and apply it.
            // Alpha is preserved from the current label color so the fade-in
            // still starts from invisible.
            Color color = GetAssignedColor(hit);
            color.a = label.color.a;
            label.color = color;

            currentTarget = hit;
            currentName   = name;
            label.text    = name;
            fader.FadeTo(1f, FadeInSeconds);
        }

        public void Dispose()
        {
            if (canvasRoot != null)
            {
                try { UnityEngine.Object.Destroy(canvasRoot); }
                catch (Exception) { }
            }
            canvasRoot = null;
            canvas     = null;
            label      = null;
            fader      = null;
            built      = false;
            currentTarget = null;
            currentName   = null;
        }

        private void Dismiss()
        {
            if (currentTarget == null)
                return;

            currentTarget = null;
            currentName   = null;
            if (fader != null)
                fader.FadeTo(0f, FadeOutSeconds);
        }

        private PuppetController RaycastPuppet(Camera cam)
        {
            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            RaycastHit hitInfo;
            if (!Physics.Raycast(ray, out hitInfo, MaxLookDistance,
                                 ~0, QueryTriggerInteraction.Ignore))
                return null;

            Transform t = hitInfo.transform;
            if (t == null)
                return null;

            for (int i = 0; i < lookupScratch.Count; i++)
            {
                PuppetController p = lookupScratch[i];
                if (p == null || p.GameObject == null)
                    continue;

                Transform root = p.GameObject.transform;
                Transform node = t;
                while (node != null)
                {
                    if (node == root)
                        return p;
                    node = node.parent;
                }
            }

            return null;
        }

        private static string GetDisplayName(PuppetController puppet)
        {
            if (puppet == null)
                return null;

            if (!displayNameFieldResolved)
            {
                displayNameFieldResolved = true;
                try
                {
                    displayNameField = typeof(PuppetController).GetField(
                        "displayName",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning(
                        "PlayerDisplayName: failed to resolve PuppetController.displayName: " +
                        ex.Message);
                    displayNameField = null;
                }
            }

            if (displayNameField == null)
                return null;

            try
            {
                return displayNameField.GetValue(puppet) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // NEW — resolve a puppet's owner playerId via reflection, then look up
        // joinOrder in the live session roster. Falls back to white if anything
        // along the chain is missing.
        private static string GetPlayerId(PuppetController puppet)
        {
            if (puppet == null)
                return null;

            if (!playerIdFieldResolved)
            {
                playerIdFieldResolved = true;
                try
                {
                    playerIdField = typeof(PuppetController).GetField(
                        "playerId",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning(
                        "PlayerDisplayName: failed to resolve PuppetController.playerId: " +
                        ex.Message);
                    playerIdField = null;
                }
            }

            if (playerIdField == null)
                return null;

            try
            {
                return playerIdField.GetValue(puppet) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // NEW — color for the given puppet, or white if we can't resolve it.
        private static Color GetAssignedColor(PuppetController puppet)
        {
            string playerId = GetPlayerId(puppet);
            if (string.IsNullOrEmpty(playerId))
                return Color.white;

            SessionController session = SessionController.Active;
            if (session == null)
                return Color.white;

            SessionPlayer sp;
            if (!session.Players.TryGetValue(playerId, out sp) || sp == null)
                return Color.white;

            int idx = sp.joinOrder;
            if (idx < 0 || idx >= ColorPool.Length)
                return Color.white;

            return ColorPool[idx];
        }

        private bool TryBuild()
        {
            built = true;

            try
            {
                canvasRoot = new GameObject("MiSideMultiplayer.PlayerDisplayName");
                UnityEngine.Object.DontDestroyOnLoad(canvasRoot);
                canvasRoot.hideFlags = HideFlags.HideAndDontSave;

                canvas = canvasRoot.AddComponent<Canvas>();
                canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = CanvasSortOrder;

                CanvasScaler scaler = canvasRoot.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1f;

                GameObject labelGO = new GameObject("Label");
                labelGO.transform.SetParent(canvasRoot.transform, false);

                RectTransform rt = labelGO.AddComponent<RectTransform>();
                rt.anchorMin        = new Vector2(0.5f, 0.5f);
                rt.anchorMax        = new Vector2(0.5f, 0.5f);
                rt.pivot            = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(0f, -28f);
                rt.sizeDelta        = new Vector2(400f, 32f);

                label = labelGO.AddComponent<Text>();
                label.alignment     = TextAnchor.MiddleCenter;
                label.color         = Color.white;
                label.fontSize      = FontSize;
                label.raycastTarget = false;
                label.text          = string.Empty;
                label.font          = ResolveFont();
                if (label.font == null)
                {
                    DiagnosticLog.Warning(
                        "PlayerDisplayName: no usable Font found; label will not render.");
                    buildFailed = true;
                    return false;
                }

                fader = labelGO.AddComponent<TextFader>();
                fader.Initialize(label);
                fader.SetAlpha(0f);

                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "PlayerDisplayName: failed to build canvas: " +
                    ex.GetType().Name + ": " + ex.Message);
                buildFailed = true;
                return false;
            }
        }

        private static Font ResolveFont()
        {
            try
            {
                Font f = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (f != null) return f;
            }
            catch (Exception) { }

            try            {
                Text[] texts = UnityEngine.Object.FindObjectsOfType<Text>();
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i] != null && texts[i].font != null)
                        return texts[i].font;
                }
            }
            catch (Exception) { }

            try
            {
                TextMesh[] meshes = UnityEngine.Object.FindObjectsOfType<TextMesh>();
                for (int i = 0; i < meshes.Length; i++)
                {
                    if (meshes[i] != null && meshes[i].font != null)
                        return meshes[i].font;
                }
            }
            catch (Exception) { }

            try
            {
                Font f = Font.CreateDynamicFontFromOSFont("Arial", FontSize);
                if (f != null) return f;
            }
            catch (Exception) { }

            return null;
        }
    }
}