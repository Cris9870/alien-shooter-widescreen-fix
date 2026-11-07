using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AlienShooterLauncher;

/// <summary>
/// Pictures for the launcher, read from the player's own copy of the game at runtime.
/// Nothing from the game is shipped with the launcher.
/// </summary>
public sealed class GameArt
{
    readonly string _vid;

    public GameArt(string gameDir) => _vid = Path.Combine(gameDir, "Vid");

    /// <summary>Red-lit corridor (one of the game's loading screens).</summary>
    public BitmapSource? Background => Load("957_0.BMP");

    /// <summary>The ALIEN SHOOTER logo with its black background made transparent.</summary>
    public BitmapSource? Logo
    {
        get
        {
            var src = Load("game_logo.bmp");
            return src == null ? null : BlackToAlpha(src);
        }
    }

    /// <summary>The game's loading screens (800x600), shown on the launcher's "security camera".</summary>
    public IReadOnlyList<string> Scenes =>
        Directory.Exists(_vid)
            ? Directory.GetFiles(_vid, "95?_?.bmp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

    public static BitmapSource? LoadFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // read now, do not keep the file open
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    BitmapSource? Load(string name)
    {
        string path = Path.Combine(_vid, name);
        return File.Exists(path) ? LoadFile(path) : null;
    }

    // Alpha from brightness, then colour un-premultiplied, so the metal letters keep their shading.
    static BitmapSource BlackToAlpha(BitmapSource src)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            int b = px[i], g = px[i + 1], r = px[i + 2];
            int a = Math.Min(255, Math.Max(r, Math.Max(g, b)) * 22 / 10);
            if (a < 10)
            {
                px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0;
                continue;
            }
            px[i] = (byte)Math.Min(255, b * 255 / a);
            px[i + 1] = (byte)Math.Min(255, g * 255 / a);
            px[i + 2] = (byte)Math.Min(255, r * 255 / a);
            px[i + 3] = (byte)a;
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }
}
