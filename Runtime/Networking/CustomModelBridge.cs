using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using BepInEx;
using Il2CppInterop.Runtime;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Soft-dependency bridge to the MS_CustomModels mod.
    ///
    /// DESIGN GOALS
    /// ─────────────
    /// 1. If MS_CustomModels is NOT installed → every call is a no-op (no errors).
    /// 2. If MS_CustomModels IS installed → we read which model is selected on the
    ///    LOCAL player and broadcast its name in RemotePlayerState.customModelName.
    /// 3. The RECEIVER reads customModelName and:
    ///      • "None" / empty → use default MiSide body (existing PuppetFactory flow).
    ///      • Anything else  → try to load that model on the puppet via ModelPuppet,
    ///        then fall back to the existing clone flow if ModelPuppet isn't available.
    ///
    /// HOW WE READ THE SELECTED MODEL
    /// ───────────────────────────────
    /// MS_CustomModels stores per-character model selections in BepInEx config entries
    /// (ConfigEntry&lt;Model&gt;).  The simplest and most reliable way to read these without
    /// a compile-time reference is to parse the config file on disk (INI format, updated
    /// by BepInEx whenever a config value changes).  We cache the result and re-read
    /// every RefreshInterval seconds so in-game model changes are picked up.
    ///
    /// PLUGIN GUID / CONFIG FILE NAME
    /// ───────────────────────────────
    /// Reflected from the loaded assembly's Plugin class's PLUGIN_GUID constant or
    /// alternatively from the BepInPlugin attribute on the main Plugin type.
    ///
    /// HOW WE APPLY THE MODEL ON PUPPETS
    /// ───────────────────────────────────
    /// MS_CustomModels exposes a ModelPuppet class (confirmed by DLL reflection).
    /// When the receiver creates a puppet, we:
    ///   1. Add a ModelPuppet component to the puppet root GameObject.
    ///   2. Call ModelPuppet.ChangeModel(modelName) via System.Reflection.
    /// If reflection fails, we fall back to the standard PuppetFactory clone flow
    /// (which already clones model(Clone) if a custom model is loaded on the source).
    ///
    /// PLAYER MODEL DIRECTORY
    /// ───────────────────────
    /// BepInEx/plugins/models/player/   — contains one sub-folder per model.
    /// Each sub-folder holds a *.vrmmod file.  We scan this directory on startup and
    /// log every found model name to the BepInEx console.
    /// </summary>
    public static class CustomModelBridge
    {
        // ── State ─────────────────────────────────────────────────────────────
        private static bool   initialized;
        private static bool   modPresent;
        private static string configFilePath;    // full path to MS_CustomModels.cfg
        private static string pluginGuid;        // e.g. "MS_CustomModels"

        // Reflection handles for MS_CustomModels types
        private static Type   modelPuppetType;
        private static MethodInfo changeModelMethod;

        // Cached model name (re-read every RefreshInterval seconds)
        private static string cachedModelName    = "None";
        private static float  nextModelReadTime  = 0f;
        private const  float  RefreshInterval    = 3f;

        // ── Initialise (call once from RuntimeServices.ctor) ──────────────────
        public static void TryInitialize()
        {
            if (initialized) return;
            initialized = true;

            try
            {
                // Find the managed assembly for MS_CustomModels
                Assembly msAsm = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(a.GetName().Name, "MS_CustomModels",
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        msAsm = a;
                        break;
                    }
                }

                if (msAsm == null)
                {
                    DiagnosticLog.Info("[CustomModelBridge] MS_CustomModels not loaded — " +
                                       "custom model sync disabled.");
                    return;
                }

                modPresent = true;
                DiagnosticLog.Info("[CustomModelBridge] MS_CustomModels v" +
                                   msAsm.GetName().Version + " detected.");

                // Resolve plugin GUID from MyPluginInfo or BepInPlugin attribute
                pluginGuid = ResolvePluginGuid(msAsm);
                DiagnosticLog.Info("[CustomModelBridge] Plugin GUID: " + pluginGuid);

                // Locate config file
                configFilePath = Path.Combine(Paths.ConfigPath, pluginGuid + ".cfg");
                DiagnosticLog.Info("[CustomModelBridge] Config path: " + configFilePath);

                // Cache reflection handles for ModelPuppet.ChangeModel
                modelPuppetType  = msAsm.GetType("MS_CustomModels.ModelPuppet");
                changeModelMethod = modelPuppetType?.GetMethod(
                    "ChangeModel",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);

                if (changeModelMethod != null)
                    DiagnosticLog.Info("[CustomModelBridge] ModelPuppet.ChangeModel found — " +
                                       "puppet model loading available.");
                else
                    DiagnosticLog.Warning("[CustomModelBridge] ModelPuppet.ChangeModel not found. " +
                                          "Will fall back to visual-clone flow.");

                // Scan player models directory and log all found models
                ScanPlayerModels();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[CustomModelBridge] Init error: " + ex.Message);
            }
        }

        // ── Read selected model for the local player ──────────────────────────
        /// <summary>
        /// Returns the currently selected player model name.
        /// "None" means the default MiSide body is in use.
        /// Result is cached and refreshed every RefreshInterval seconds.
        /// </summary>
        public static string GetLocalPlayerModelName(Transform localPlayerRoot)
        {
            if (!modPresent)
                return "None";

            if (Time.unscaledTime >= nextModelReadTime)
            {
                nextModelReadTime = Time.unscaledTime + RefreshInterval;
                cachedModelName   = ReadModelNameFromConfig();

                // Double-check: if we can't read config, fall back to presence of model(Clone)
                if (string.IsNullOrEmpty(cachedModelName) || cachedModelName == "None")
                    cachedModelName = DetectFromHierarchy(localPlayerRoot);

                DiagnosticLog.Info("[CustomModelBridge] Local player model: '" +
                                   cachedModelName + "'");
            }

            return cachedModelName;
        }

        // ── Apply model to a puppet ───────────────────────────────────────────
        /// <summary>
        /// Attempts to apply modelName to the puppetRoot via ModelPuppet.ChangeModel.
        /// Returns true if the model was applied, false if the caller should fall
        /// back to the standard visual-clone flow.
        /// </summary>
        public static bool TryApplyModelToPuppet(GameObject puppetRoot, string modelName)
        {
            if (!modPresent || string.IsNullOrEmpty(modelName) ||
                modelName == "None" || modelPuppetType == null || changeModelMethod == null)
                return false;

            try
            {
                // Add a ModelPuppet component to the puppet via non-generic AddComponent(Type).
                // This is a real Unity API that works for any registered MonoBehaviour type.
                Il2CppSystem.Type il2CppModelPuppetType = Il2CppType.From(modelPuppetType);
                Component puppet = puppetRoot.AddComponent(il2CppModelPuppetType);
                if (puppet == null)
                {
                    DiagnosticLog.Warning("[CustomModelBridge] AddComponent(ModelPuppet) returned null.");
                    return false;
                }

                // Call ChangeModel(modelName)
                try
                {
                    changeModelMethod.Invoke(puppet, new object[] { modelName });
                }
                catch (Exception invokeEx)
                {
                    // ChangeModel failed (bad/unknown model name). Remove the
                    // half-initialised ModelPuppet component immediately so it
                    // cannot linger on puppetRoot and interfere with the
                    // fallback native-clone visual that PuppetFactory builds next.
                    DiagnosticLog.Warning(
                        "[CustomModelBridge] ChangeModel('" + modelName +
                        "') failed — removing ModelPuppet component: " + invokeEx.Message);
                    try { UnityEngine.Object.Destroy(puppet); } catch (Exception) { }
                    return false;
                }

                DiagnosticLog.Info("[CustomModelBridge] Applied model '" + modelName +
                                   "' to puppet '" + puppetRoot.name + "' via ModelPuppet.");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[CustomModelBridge] TryApplyModelToPuppet failed: " +
                                      ex.Message);
                return false;
            }
        }

        // ── Config file reader ────────────────────────────────────────────────
        private static string ReadModelNameFromConfig()
        {
            if (string.IsNullOrEmpty(configFilePath) || !File.Exists(configFilePath))
                return "None";

            try
            {
                bool  inPlayerSection = false;
                string[] lines = File.ReadAllLines(configFilePath);

                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#") ||
                        line.StartsWith(";"))
                        continue;

                    // Section header [Player] or [player]
                    if (line.StartsWith("["))
                    {
                        inPlayerSection = line.IndexOf("Player",
                            StringComparison.OrdinalIgnoreCase) >= 0;
                        continue;
                    }

                    // Key = Value within the Player section
                    if (inPlayerSection && line.IndexOf('=') >= 0)
                    {
                        int eq   = line.IndexOf('=');
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();

                        // The config key is typically "Model" or the character type name
                        if (key.IndexOf("Model", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            key.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                            return string.IsNullOrEmpty(val) ? "None" : val;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[CustomModelBridge] Config read error: " + ex.Message);
            }

            return "None";
        }

        // ── Hierarchy fallback: detect presence of custom model ───────────────
        // IMPORTANT: only trust names that actually LOOK like a custom-model
        // wrapper. MS_CustomModels/VRM loaders name their instantiated root
        // "<something>(Clone)" or "model"/"Model"/"PlayerModel". A bare
        // "does it have any renderer" scan is WRONG — Player-level utility
        // objects (e.g. "Hand Left Door", a doorknob prop) can carry a tiny
        // MeshRenderer and would be misidentified as a model name, which then
        // makes TryApplyModelToPuppet attempt (and fail) a ChangeModel call
        // with garbage input — and can leave a broken ModelPuppet component
        // sitting on the puppet root that blocks the fallback clone visual.
        private static readonly string[] KnownModelMarkers =
        {
            "(clone)", "model", "playermodel",
        };

        private static string DetectFromHierarchy(Transform localPlayerRoot)
        {
            if (localPlayerRoot == null || localPlayerRoot.parent == null)
                return "None";

            Transform playerLevel = localPlayerRoot.parent;

            for (int i = 0; i < playerLevel.childCount; i++)
            {
                Transform child = playerLevel.GetChild(i);
                if (child == null || child == localPlayerRoot) continue;
                if (child.name.IndexOf("Head",   StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (child.name.IndexOf("Camera", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                string lower = child.name.ToLowerInvariant();
                bool looksLikeModel = false;
                for (int m = 0; m < KnownModelMarkers.Length; m++)
                {
                    if (lower.Contains(KnownModelMarkers[m])) { looksLikeModel = true; break; }
                }
                if (!looksLikeModel) continue;

                if (VisualCloneUtility.CountRenderers(child) > 0)
                    return child.name;
            }

            return "None";
        }

        // ── Model directory scanner ───────────────────────────────────────────
        private static void ScanPlayerModels()
        {
            try
            {
                // MS_CustomModels reads models from:
                //   BepInEx/plugins/models/player/
                // (confirmed by ModelManager.SEARCH_DIR and the user's description)
                string modelDir = Path.Combine(Paths.PluginPath, "models", "player");

                DiagnosticLog.Info("[CustomModelBridge] Scanning player model directory: " +
                                   modelDir);

                if (!Directory.Exists(modelDir))
                {
                    DiagnosticLog.Warning("[CustomModelBridge] Player model dir not found: " +
                                          modelDir +
                                          " — no .vrmmod files loaded yet.");
                    return;
                }

                // Each model lives in its own sub-folder containing a .vrmmod file.
                string[] vrmFiles = Directory.GetFiles(modelDir, "*.vrmmod",
                                                       SearchOption.AllDirectories);

                if (vrmFiles.Length == 0)
                {
                    DiagnosticLog.Info("[CustomModelBridge] No .vrmmod files found in: " +
                                       modelDir);
                    return;
                }

                DiagnosticLog.Info("[CustomModelBridge] Found " + vrmFiles.Length +
                                   " player model(s):");
                for (int i = 0; i < vrmFiles.Length; i++)
                {
                    // Model name = the folder that directly contains the .vrmmod file
                    string folder    = Path.GetDirectoryName(vrmFiles[i]);
                    string modelName = Path.GetFileName(folder);
                    DiagnosticLog.Info("[CustomModelBridge]   [" + i + "] " +
                                       modelName + "  (" + vrmFiles[i] + ")");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[CustomModelBridge] ScanPlayerModels error: " + ex.Message);
            }
        }

        // ── GUID resolver ─────────────────────────────────────────────────────
        private static string ResolvePluginGuid(Assembly msAsm)
        {
            // 1. Look for a MyPluginInfo class with a PLUGIN_GUID field
            try
            {
                Type info = msAsm.GetType("MS_CustomModels.MyPluginInfo");
                if (info != null)
                {
                    FieldInfo f = info.GetField("PLUGIN_GUID",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    if (f != null)
                    {
                        string guid = f.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(guid)) return guid;
                    }
                }
            }
            catch (Exception) { }

            // 2. Find the Plugin class and read its BepInPlugin attribute
            try
            {
                foreach (Type t in msAsm.GetTypes())
                {
                    foreach (object attr in t.GetCustomAttributes(false))
                    {
                        if (attr.GetType().Name == "BepInPlugin")
                        {
                            // BepInPlugin has .GUID or constructor arg 0
                            PropertyInfo guidProp = attr.GetType().GetProperty("GUID");
                            if (guidProp != null)
                            {
                                string g = guidProp.GetValue(attr) as string;
                                if (!string.IsNullOrEmpty(g)) return g;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }

            // 3. Fall back to the assembly name
            return msAsm.GetName().Name;
        }
    }
}
