using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class ErrorWindowIcon
    {
        private const string ResourceName = "MiSideMultiplayer.Runtime.UI.warning.png";

        private static Sprite _sprite;
        private static bool   _attempted;

        public static Sprite Load()
        {
            if (_attempted) return _sprite;
            _attempted = true;

            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        DiagnosticLog.Warning("ErrorWindowIcon: embedded resource '" +
                                              ResourceName + "' not found. " +
                                              "Check the EmbeddedResource entry in the .csproj.");
                        return null;
                    }

                    byte[] bytes = new byte[stream.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    bool ok = false;
                    try
                    {
                        ok = UnityEngine.ImageConversion.LoadImage(tex, bytes);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Warning("ErrorWindowIcon: ImageConversion.LoadImage failed: " +
                                              ex.Message);
                        return null;
                    }

                    if (!ok)
                    {
                        DiagnosticLog.Warning("ErrorWindowIcon: failed to decode embedded warning.png.");
                        return null;
                    }

                    _sprite = Sprite.Create(tex,
                        new Rect(0, 0, tex.width, tex.height),
                        new Vector2(0.5f, 0.5f));
                    return _sprite;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("ErrorWindowIcon: load failed: " + ex.Message);
                return null;
            }
        }
    }
}