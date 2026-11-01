using System;
using UnityEngine;

namespace MiSideMultiplayer
{
    /// <summary>
    /// One-shot diagnostic. On the first frame where a puppet exists, dumps
    /// every renderer and every material on that puppet, including the shader
    /// name and the full property list the shader exposes.
    ///
    /// Purpose: identify MiSide's puppet body shader so PuppetProximityFade
    /// can apply alpha correctly (the current generic Standard-shader setup
    /// only fades the outline, not the body).
    ///
    /// Remove this file once the fade works.
    /// </summary>
    public static class PuppetShaderProbe
    {
        private static bool _ran;

        public static void Tick()
        {
            if (_ran) return;

            try
            {
                foreach (PuppetController p in PuppetRegistry.AllPuppets)
                {
                    if (p == null || p.GameObject == null) continue;

                    _ran = true; // only log once per session

                    Renderer[] rs = p.GameObject.GetComponentsInChildren<Renderer>(true);
                    DiagnosticLog.Info("[SHADER-PROBE] " + rs.Length +
                                       " renderer(s) on puppet '" + p.GameObject.name + "':");

                    for (int i = 0; i < rs.Length; i++)
                    {
                        Renderer r = rs[i];
                        if (r == null) continue;

                        string path = LocalPlayerLocator.GetPath(r.transform);
                        DiagnosticLog.Info("  [R] " + path +
                                           "  enabled=" + r.enabled +
                                           "  type=" + r.GetType().Name);

                        Material[] mats = null;
                        try { mats = r.sharedMaterials; } catch (Exception) { }
                        if (mats == null)
                        {
                            DiagnosticLog.Info("    <no sharedMaterials>");
                            continue;
                        }

                        for (int m = 0; m < mats.Length; m++)
                        {
                            Material mat = mats[m];
                            if (mat == null)
                            {
                                DiagnosticLog.Info("    [M" + m + "] <null>");
                                continue;
                            }

                            string shaderName = "?";
                            try
                            {
                                if (mat.shader != null) shaderName = mat.shader.name;
                            }
                            catch (Exception) { }

                            int rq = 0;
                            try { rq = mat.renderQueue; } catch (Exception) { }

                            DiagnosticLog.Info("    [M" + m + "] '" + mat.name + "'" +
                                               "  shader='" + shaderName + "'" +
                                               "  renderQueue=" + rq);

                            // Dump the shader's property names so we can see what
                            // the shader actually exposes for color/alpha control.
                            try
                            {
                                int propCount = mat.shader != null ? mat.shader.GetPropertyCount() : 0;
                                if (propCount > 0)
                                {
                                    string props = "";
                                    for (int pi = 0; pi < propCount; pi++)
                                    {
                                        if (pi > 0) props += ", ";
                                        try { props += mat.shader.GetPropertyName(pi); }
                                        catch (Exception) { props += "?"; }
                                    }
                                    DiagnosticLog.Info("        props: " + props);
                                }
                            }
                            catch (Exception) { }

                            // Also dump the current _Color / _BaseColor values if present,
                            // so we can tell which one the shader actually uses.
                            try
                            {
                                if (mat.HasProperty("_Color"))
                                {
                                    Color c = mat.GetColor("_Color");
                                    DiagnosticLog.Info("        _Color    = " + c);
                                }
                            }
                            catch (Exception) { }

                            try
                            {
                                if (mat.HasProperty("_BaseColor"))
                                {
                                    Color c = mat.GetColor("_BaseColor");
                                    DiagnosticLog.Info("        _BaseColor= " + c);
                                }
                            }
                            catch (Exception) { }
                        }
                    }
                    return; // only probe the first puppet
                }
            }
            catch (Exception ex)
            {
                _ran = true;
                DiagnosticLog.Warning("PuppetShaderProbe failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}