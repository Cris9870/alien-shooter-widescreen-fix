using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AlienShooterLauncher;

/// <summary>A zoom choice: the game renders at output size / Scale and the DLL scales it back up.</summary>
public sealed record ZoomPreset(string Name, double Scale, bool Recommended);

/// <summary>A screen resolution for the game image. Native = the desktop size, no mode change.</summary>
public sealed record OutputMode(int Width, int Height, string Label, bool Native, bool Available);

/// <summary>A monitor or TV attached to the desktop.</summary>
public sealed record Display(string Device, int Number, string Name, int Width, int Height, bool Primary,
                             HashSet<(int W, int H)> Modes);

/// <summary>Game folder, upscaler settings (d3d9_upscale.ini) and installation of the proxy DLL.</summary>
public sealed class GameConfig
{
    public const int SteamAppId = 33100;

    // Levels 1 and 11 are 2000 px wide; a wider render shows a black strip.
    public const int MaxRenderWidth = 2000;

    // The game's menus need roughly 600 px of height.
    public const int MinRenderHeight = 600;

    public static readonly ZoomPreset[] Presets =
    {
        new("CERCA", 2.5, false),
        new("NORMAL", 2.0, true),
        new("LEJOS", 1.75, false),
    };

    public string GameDir { get; }
    public IReadOnlyList<Display> Displays { get; }

    public Display Display { get; private set; }
    public IReadOnlyList<OutputMode> Outputs { get; private set; }
    public OutputMode Output { get; set; }

    public bool Enabled { get; set; } = true;
    public ZoomPreset Zoom { get; set; } = Presets[1];
    public bool LinearFilter { get; set; }
    public bool ClipCursor { get; set; } = true;

    public GameConfig()
    {
        GameDir = FindGameDir();
        Displays = DisplayInfo.All();
        Display = Displays.First(d => d.Primary);
        Outputs = BuildOutputs(Display);
        Output = Outputs[0];
        Load();
    }

    public bool GameFound => File.Exists(Path.Combine(GameDir, "AlienShooter.exe"));
    public bool DllInstalled => File.Exists(DllPath);
    public static bool GameRunning => Process.GetProcessesByName("AlienShooter").Length > 0;

    string DllPath => Path.Combine(GameDir, "d3d9.dll");
    string IniPath => Path.Combine(GameDir, "d3d9_upscale.ini");
    string OptionsPath => Path.Combine(GameDir, "saves", "options.ini");

    public void SelectDisplay(Display d)
    {
        Display = d;
        Outputs = BuildOutputs(d);
        Output = Outputs[0];
    }

    /// <summary>Game resolution for a zoom preset at the current output size.</summary>
    public (int W, int H) RenderSize(ZoomPreset p)
    {
        int w = Output.Width, h = Output.Height;
        double scale = Math.Min(p.Scale, (double)h / MinRenderHeight);
        scale = Math.Max(scale, (double)w / MaxRenderWidth);   // never wider than the narrowest map
        scale = Math.Max(scale, 1.0);
        return ((int)Math.Round(w / scale), (int)Math.Round(h / scale));
    }

    public (int W, int H) Render => RenderSize(Zoom);

    // Native resolution first, then common resolutions the display offers. 4K is always listed;
    // without a 4K screen (or NVIDIA DSR) it shows as unavailable.
    static IReadOnlyList<OutputMode> BuildOutputs(Display d)
    {
        var list = new List<OutputMode> { new(d.Width, d.Height, "NATIVA", true, true) };
        (int W, int H, string Label)[] common =
        {
            (5160, 2160, "5K ULTRAWIDE"), (3840, 2160, "4K UHD"), (3440, 1440, "ULTRAWIDE"),
            (2560, 1440, "QHD"), (2560, 1080, "ULTRAWIDE"), (1920, 1080, "FULL HD"),
        };
        foreach (var c in common)
        {
            if (c.W == d.Width && c.H == d.Height) continue;
            bool available = d.Modes.Contains((c.W, c.H));
            if (available || c.W == 3840)
                list.Add(new OutputMode(c.W, c.H, c.Label, false, available));
        }
        return list;
    }

    void Load()
    {
        var ini = ReadIni(IniPath);
        Enabled = DllInstalled && Get(ini, "Enabled", 1) == 1;
        LinearFilter = Get(ini, "LinearFilter", 0) == 1;
        ClipCursor = Get(ini, "ClipCursor", 1) == 1;

        if (ini.TryGetValue("Monitor", out var dev) && Displays.FirstOrDefault(x => x.Device == dev) is { } d)
            SelectDisplay(d);
        int ow = Get(ini, "OutputWidth", 0), oh = Get(ini, "OutputHeight", 0);
        Output = Outputs.FirstOrDefault(o => !o.Native && o.Available && o.Width == ow && o.Height == oh) ?? Outputs[0];

        int rw = Get(ini, "RenderWidth", 0), rh = Get(ini, "RenderHeight", 0);
        Zoom = Presets.FirstOrDefault(p => RenderSize(p) == (rw, rh)) ?? Presets[1];
    }

    /// <summary>Writes the settings and installs or removes the DLL. The game must be closed.</summary>
    public void Save()
    {
        File.WriteAllText(IniPath, BuildIni(), Encoding.ASCII);
        if (Enabled)
        {
            InstallDll();
            SetFullScreenOption();
        }
        else if (DllInstalled)
        {
            File.Delete(DllPath);
        }
    }

    public void Launch() =>
        Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamAppId}") { UseShellExecute = true });

    public void OpenGameFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{GameDir}\"") { UseShellExecute = true });

    /// <summary>Copies the game's saves folder next to the launcher. Returns the backup folder.</summary>
    public string BackupSaves()
    {
        string src = Path.Combine(GameDir, "saves");
        string dst = Path.Combine(AppContext.BaseDirectory, "respaldos", $"saves_{DateTime.Now:yyyyMMdd_HHmmss}");
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dst, file.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
        return dst;
    }

    /// <summary>After a launcher update: replace an installed DLL with the one this launcher carries.</summary>
    public void RefreshInstalledDll()
    {
        if (Enabled && DllInstalled && !GameRunning) InstallDll();
    }

    void InstallDll()
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("d3d9.dll")
            ?? throw new InvalidOperationException("d3d9.dll no esta incluida en el launcher.");
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        byte[] dll = ms.ToArray();
        if (DllInstalled && Sha256(File.ReadAllBytes(DllPath)).SequenceEqual(Sha256(dll)))
            return;
        File.WriteAllBytes(DllPath, dll);
    }

    static byte[] Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    // The DLL turns the game's fullscreen request into its own window or mode, so keep FullScreen=1.
    void SetFullScreenOption()
    {
        if (!File.Exists(OptionsPath)) return;
        var lines = File.ReadAllLines(OptionsPath, Latin1);
        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("FullScreen=") && lines[i] != "FullScreen=1")
            {
                lines[i] = "FullScreen=1";
                changed = true;
            }
        }
        if (changed) File.WriteAllLines(OptionsPath, lines, Latin1);
    }

    string BuildIni()
    {
        var (rw, rh) = Render;
        return string.Join("\r\n", new[]
        {
            "; Ajustes de la DLL de Alien Shooter (los escribe el launcher)",
            "[upscale]",
            "; 1 = activado, 0 = el juego funciona como el original",
            $"Enabled={(Enabled ? 1 : 0)}",
            "; Pantalla donde se abre el juego (vacio = la principal)",
            $"Monitor={Display.Device}",
            "; Resolucion de pantalla. 0 = la del escritorio (ventana sin bordes)",
            $"OutputWidth={(Output.Native ? 0 : Output.Width)}",
            $"OutputHeight={(Output.Native ? 0 : Output.Height)}",
            "; Resolucion interna del juego (maximo recomendado de ancho: 2000)",
            $"RenderWidth={rw}",
            $"RenderHeight={rh}",
            "; 0 = pixeles nitidos, 1 = suavizado",
            $"LinearFilter={(LinearFilter ? 1 : 0)}",
            "IntegerScale=0",
            "; 1 = el raton no sale de la pantalla del juego mientras juegas",
            $"ClipCursor={(ClipCursor ? 1 : 0)}",
            "TestMode=0",
            "Log=1",
            "",
        });
    }

    static Dictionary<string, string> ReadIni(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return d;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (line.StartsWith(";") || line.StartsWith("[") || eq <= 0) continue;
            d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return d;
    }

    static int Get(Dictionary<string, string> ini, string key, int fallback) =>
        ini.TryGetValue(key, out var v) && int.TryParse(v, out int n) ? n : fallback;

    static string FindGameDir()
    {
        const string fallback = @"C:\Program Files (x86)\Steam\steamapps\common\Alien Shooter";
        if (File.Exists(Path.Combine(fallback, "AlienShooter.exe"))) return fallback;
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam)
            {
                var libraries = new List<string> { steam };
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                foreach (string lib in libraries)
                {
                    string dir = Path.Combine(lib, "steamapps", "common", "Alien Shooter");
                    if (File.Exists(Path.Combine(dir, "AlienShooter.exe"))) return dir;
                }
            }
        }
        catch (Exception)
        {
            // Registry or Steam files unreadable: fall back to the default path.
        }
        return fallback;
    }
}

/// <summary>Monitors attached to the desktop, with their current and supported resolutions.</summary>
static class DisplayInfo
{
    const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4, CurrentSettings = -1;

    public static IReadOnlyList<Display> All()
    {
        var list = new List<Display>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (int i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & AttachedToDesktop) == 0) continue;
            string device = dd.DeviceName;
            bool primary = (dd.StateFlags & PrimaryDevice) != 0;

            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string name = EnumDisplayDevices(device, 0, ref mon, 0) ? mon.DeviceString : "Pantalla";

            var dm = NewDevMode();
            if (!EnumDisplaySettings(device, CurrentSettings, ref dm)) continue;
            int w = dm.dmPelsWidth, h = dm.dmPelsHeight;

            var modes = new HashSet<(int, int)>();
            var m = NewDevMode();
            for (int k = 0; EnumDisplaySettings(device, k, ref m); k++)
                if (m.dmBitsPerPel == 32) modes.Add((m.dmPelsWidth, m.dmPelsHeight));

            int number = int.TryParse(Regex.Match(device, @"\d+$").Value, out int n) ? n : list.Count + 1;
            list.Add(new Display(device, number, name, w, h, primary, modes));
        }
        if (list.Count == 0 || !list.Any(d => d.Primary))
            list.Insert(0, new Display("", 1, "Pantalla", 1920, 1080, true, new HashSet<(int, int)>()));
        return list.OrderByDescending(d => d.Primary).ThenBy(d => d.Number).ToList();
    }

    static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, int index, ref DISPLAY_DEVICE dd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);
}
