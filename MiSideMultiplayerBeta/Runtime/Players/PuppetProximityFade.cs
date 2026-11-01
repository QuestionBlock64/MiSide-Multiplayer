using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MiSideMultiplayer
{
    public sealed class PuppetProximityFade
    {
        // ── Tunables ────────────────────────────────────────────────────────
        private const float FullOpacityDistance = 0.2f;
        private const float MinOpacityDistance  = 0.00f;
        private const float MinOpacity          = 0.10f;
        private const float LerpSpeed           = 8f;

        // ── Per-puppet state ────────────────────────────────────────────────
        private sealed class PuppetFadeState
        {
            public GameObject RenderersOwner;
            public float CurrentAlpha = 1f;
            public float TargetAlpha  = 1f;
            public readonly List<RendererState> Renderers = new List<RendererState>();
            public bool MaterialsSwapped;
        }

        private sealed class RendererState
        {
            public Renderer               Renderer;
            public Material[]             OriginalShared;
            public Material[]             FadedShared;
            public bool                   CanFade;
        }

        private readonly Dictionary<PuppetController, PuppetFadeState> states =
            new Dictionary<PuppetController, PuppetFadeState>();

        // ── Public API ──────────────────────────────────────────────────────

        public void Tick()
        {
            Transform localRoot = FindLocalPlayerTransform();
            if (localRoot == null)
            {
                RestoreAll();
                return;
            }

            Vector3 localPos = localRoot.position;

            List<PuppetController> puppets = new List<PuppetController>();
            foreach (PuppetController p in PuppetRegistry.AllPuppets)
            {
                if (p != null && p.GameObject != null)
                    puppets.Add(p);
            }

            for (int i = 0; i < puppets.Count; i++)
            {
                PuppetController puppet = puppets[i];
                if (puppet == null || puppet.GameObject == null)
                    continue;

                PuppetFadeState state = GetOrCreateState(puppet);
                if (state == null)
                    continue;

                Vector3 pPos = puppet.GameObject.transform.position;
                float dx = pPos.x - localPos.x;
                float dz = pPos.z - localPos.z;
                float distance = Mathf.Sqrt(dx * dx + dz * dz);

                state.TargetAlpha = ComputeTargetAlpha(distance);

                state.CurrentAlpha = Mathf.MoveTowards(
                    state.CurrentAlpha,
                    state.TargetAlpha,
                    LerpSpeed * Time.deltaTime);

                ApplyAlpha(puppet, state);
            }

            PruneDestroyedPuppets(puppets);
        }

        public void Dispose()
        {
            RestoreAll();
            states.Clear();
        }

        // ── Alpha math ──────────────────────────────────────────────────────

        private static float ComputeTargetAlpha(float distance)
        {
            if (distance >= FullOpacityDistance)
                return 1f;
            if (distance <= MinOpacityDistance)
                return MinOpacity;

            float t = (distance - MinOpacityDistance) /
                      (FullOpacityDistance - MinOpacityDistance);
            return Mathf.Lerp(MinOpacity, 1f, t);
        }

        // ── Material application ────────────────────────────────────────────

        private void ApplyAlpha(PuppetController puppet, PuppetFadeState state)
        {
            bool wantOriginal = state.CurrentAlpha >= 0.999f;

            if (wantOriginal)
            {
                if (state.MaterialsSwapped)
                {
                    RestoreOriginalMaterials(state);
                    state.MaterialsSwapped = false;
                }
                return;
            }

            if (!state.MaterialsSwapped)
            {
                if (!BuildFadedMaterials(state))
                    return;
                SwapToFadedMaterials(state);
                state.MaterialsSwapped = true;
            }

            float alpha = state.CurrentAlpha;
            for (int i = 0; i < state.Renderers.Count; i++)
            {
                RendererState rs = state.Renderers[i];
                if (rs == null || !rs.CanFade || rs.FadedShared == null)
                    continue;

                for (int m = 0; m < rs.FadedShared.Length; m++)
                {
                    Material mat = rs.FadedShared[m];
                    if (mat == null) continue;

                    // Standard-family: _Color.
                    try
                    {
                        if (mat.HasProperty("_Color"))
                        {
                            Color c = mat.GetColor("_Color");
                            c.a = alpha;
                            mat.SetColor("_Color", c);
                        }
                    }
                    catch (Exception) { }

                    // RealToon: _MainColor is the base tint. Alpha lives here.
                    try
                    {
                        if (mat.HasProperty("_MainColor"))
                        {
                            Color c = mat.GetColor("_MainColor");
                            c.a = alpha;
                            mat.SetColor("_MainColor", c);
                        }
                    }
                    catch (Exception) { }

                    // Universal Render Pipeline / generic fallback.
                    try
                    {
                        if (mat.HasProperty("_BaseColor"))
                        {
                            Color c = mat.GetColor("_BaseColor");
                            c.a = alpha;
                            mat.SetColor("_BaseColor", c);
                        }
                    }
                    catch (Exception) { }
                }
            }
        }

        private static bool BuildFadedMaterials(PuppetFadeState state)
        {
            bool anyFadeable = false;
            state.Renderers.Clear();

            Renderer[] renderers = state.RenderersOwner
                .GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;

                RendererState rs = new RendererState();
                rs.Renderer       = r;
                rs.OriginalShared = r.sharedMaterials;

                if (rs.OriginalShared == null || rs.OriginalShared.Length == 0)
                {
                    rs.CanFade = false;
                    state.Renderers.Add(rs);
                    continue;
                }

                Material[] faded = new Material[rs.OriginalShared.Length];
                bool rendererFadeable = true;

                for (int m = 0; m < rs.OriginalShared.Length; m++)
                {
                    Material src = rs.OriginalShared[m];
                    if (src == null || src.shader == null)
                    {
                        rendererFadeable = false;
                        break;
                    }

                    Material clone;
                    if (!TryCreateTransparentClone(src, out clone))
                    {
                        rendererFadeable = false;
                        break;
                    }

                    faded[m] = clone;
                }

                if (rendererFadeable)
                {
                    rs.FadedShared = faded;
                    rs.CanFade     = true;
                    anyFadeable    = true;
                }
                else
                {
                    if (faded != null)
                    {
                        for (int m = 0; m < faded.Length; m++)
                        {
                            if (faded[m] != null)
                            {
                                try { UnityEngine.Object.Destroy(faded[m]); }
                                catch (Exception) { }
                            }
                        }
                    }
                    rs.FadedShared = null;
                    rs.CanFade     = false;
                }

                state.Renderers.Add(rs);
            }

            return anyFadeable;
        }

        private static bool TryCreateTransparentClone(Material src, out Material clone)
        {
            clone = null;
            try
            {
                clone = new Material(src);
                clone.name = src.name + " (FadeClone)";

                string shaderName = src.shader != null ? src.shader.name : "";

                // RealToon 5: it exposes _MainColor, not _Color, and its own
                // transparency pathway is controlled by its own keywords.
                // Rather than fighting RealToon's internals, override the
                // clone's shader to Standard and copy the main texture / color.
                // At point-blank fade range the cel-shading difference is
                // not visible.
                if (shaderName.StartsWith("RealToon", StringComparison.OrdinalIgnoreCase))
                {
                    Shader standardShader = Shader.Find("Standard");
                    if (standardShader == null)
                    {
                        // No Standard shader available — fall back to leaving
                        // this renderer unfaded rather than producing a broken
                        // visual.
                        UnityEngine.Object.Destroy(clone);
                        clone = null;
                        return false;
                    }

                    clone.shader = standardShader;

                    // Copy the main texture.
                    try
                    {
                        Texture mainTex = null;
                        if (src.HasProperty("_MainTex"))
                            mainTex = src.GetTexture("_MainTex");
                        if (mainTex != null)
                            clone.SetTexture("_MainTex", mainTex);
                    }
                    catch (Exception) { }

                    // Copy base color from RealToon's _MainColor.
                    try
                    {
                        if (src.HasProperty("_MainColor"))
                        {
                            Color c = src.GetColor("_MainColor");
                            c.a = 1f;
                            clone.SetColor("_Color", c);
                        }
                    }
                    catch (Exception) { }

                    // Copy normal map if present.
                    try
                    {
                        if (src.HasProperty("_NormalMap"))
                        {
                            Texture nm = src.GetTexture("_NormalMap");
                            if (nm != null)
                            {
                                clone.SetTexture("_BumpMap", nm);
                                clone.EnableKeyword("_NORMALMAP");
                            }
                        }
                    }
                    catch (Exception) { }
                }

                // Standard-shader transparency setup. Works for the Standard
                // shader we just switched to, and for materials that were
                // already using Standard (the Ring, Tetris, water effects, etc).
                try
                {
                    clone.SetOverrideTag("RenderType", "Transparent");
                    clone.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    clone.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    clone.SetInt("_ZWrite", 0);
                    clone.DisableKeyword("_ALPHATEST_ON");
                    clone.EnableKeyword("_ALPHABLEND_ON");
                    clone.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    clone.renderQueue = (int)RenderQueue.Transparent;
                }
                catch (Exception) { }

                try { clone.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); } catch (Exception) { }
                try { clone.EnableKeyword("_ALPHABLEND_ON"); }            catch (Exception) { }
                try { clone.DisableKeyword("_ALPHATEST_ON"); }            catch (Exception) { }

                return true;
            }
            catch (Exception)
            {
                if (clone != null)
                {
                    try { UnityEngine.Object.Destroy(clone); } catch (Exception) { }
                }
                clone = null;
                return false;
            }
        }

        private static void SwapToFadedMaterials(PuppetFadeState state)
        {
            for (int i = 0; i < state.Renderers.Count; i++)
            {
                RendererState rs = state.Renderers[i];
                if (rs == null || !rs.CanFade || rs.FadedShared == null) continue;
                try { rs.Renderer.sharedMaterials = rs.FadedShared; }
                catch (Exception) { }
            }
        }

        private static void RestoreOriginalMaterials(PuppetFadeState state)
        {
            for (int i = 0; i < state.Renderers.Count; i++)
            {
                RendererState rs = state.Renderers[i];
                if (rs == null || rs.Renderer == null || rs.OriginalShared == null)
                    continue;
                try { rs.Renderer.sharedMaterials = rs.OriginalShared; }
                catch (Exception) { }
            }
        }

        // ── State helpers ───────────────────────────────────────────────────

        private PuppetFadeState GetOrCreateState(PuppetController puppet)
        {
            PuppetFadeState state;
            if (states.TryGetValue(puppet, out state) && state != null)
                return state;

            state = new PuppetFadeState();
            state.RenderersOwner = puppet.GameObject;
            state.CurrentAlpha   = 1f;
            state.TargetAlpha    = 1f;
            states[puppet]       = state;
            return state;
        }

        private void PruneDestroyedPuppets(List<PuppetController> livePuppets)
        {
            List<PuppetController> toRemove = null;
            foreach (var kvp in states)
            {
                PuppetController p = kvp.Key;
                bool stillAlive = false;
                for (int i = 0; i < livePuppets.Count; i++)
                {
                    if (livePuppets[i] == p) { stillAlive = true; break; }
                }
                if (stillAlive) continue;

                if (toRemove == null) toRemove = new List<PuppetController>();
                toRemove.Add(p);
            }

            if (toRemove == null) return;

            for (int i = 0; i < toRemove.Count; i++)
            {
                PuppetFadeState s;
                if (states.TryGetValue(toRemove[i], out s) && s != null)
                {
                    if (s.MaterialsSwapped)
                        RestoreOriginalMaterials(s);
                    DestroyFadeClones(s);
                }
                states.Remove(toRemove[i]);
            }
        }

        private static void DestroyFadeClones(PuppetFadeState state)
        {
            for (int i = 0; i < state.Renderers.Count; i++)
            {
                RendererState rs = state.Renderers[i];
                if (rs == null || rs.FadedShared == null) continue;
                for (int m = 0; m < rs.FadedShared.Length; m++)
                {
                    if (rs.FadedShared[m] == null) continue;
                    try { UnityEngine.Object.Destroy(rs.FadedShared[m]); }
                    catch (Exception) { }
                }
                rs.FadedShared = null;
            }
        }

        private void RestoreAll()
        {
            foreach (var kvp in states)
            {
                PuppetFadeState s = kvp.Value;
                if (s == null) continue;
                if (s.MaterialsSwapped)
                    RestoreOriginalMaterials(s);
                s.CurrentAlpha     = 1f;
                s.TargetAlpha      = 1f;
                s.MaterialsSwapped = false;
            }
        }

        private static Transform FindLocalPlayerTransform()
        {
            try { return LocalPlayerLocator.FindHardcodedPlayerPath(); }
            catch (Exception) { return null; }
        }
    }
}