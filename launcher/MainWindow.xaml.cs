using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AlienShooterLauncher;

public partial class MainWindow : Window
{
    readonly GameConfig _cfg = new();
    readonly Dictionary<string, SoundPlayer> _sounds = new();
    bool _loading;

    public MainWindow()
    {
        InitializeComponent();
        LoadGameCursorAndSounds();
        LoadGameArt();
        StartRedFlicker();

        _loading = true;
        EnabledSwitch.IsChecked = _cfg.Enabled;
        ClipSwitch.IsChecked = _cfg.ClipCursor;
        BuildDisplays();
        BuildOutputs();
        BuildZoom();
        BuildFilter();
        Refresh();          // only updates the view; nothing is written until the user changes something
        _loading = false;
    }

    // ------------------------------------------------------------------ option chips

    RadioButton Choice(string group, string title, string subtitle, bool isChecked, bool enabled, Action onChecked)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, HorizontalAlignment = HorizontalAlignment.Center });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontFamily = (FontFamily)FindResource("Body"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0xA8, 0xA4)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var rb = new RadioButton
        {
            GroupName = group,
            Content = text,
            IsChecked = isChecked,
            IsEnabled = enabled,
            Style = (Style)FindResource("GameChoice"),
        };
        rb.Checked += (_, _) =>
        {
            if (_loading) return;
            Play("mouse_click");
            onChecked();
            Refresh();
        };
        return rb;
    }

    void BuildDisplays()
    {
        DisplayPanel.Children.Clear();
        foreach (var d in _cfg.Displays)
        {
            string sub = $"{d.Width}×{d.Height}" + (d.Primary ? " · PRINCIPAL" : "");
            var rb = Choice("display", $"PANTALLA {d.Number}", sub, d == _cfg.Display, true, () =>
            {
                _cfg.SelectDisplay(d);
                Rebuild(BuildOutputs, BuildZoom);
            });
            rb.ToolTip = d.Name;
            rb.Width = 170;
            DisplayPanel.Children.Add(rb);
        }
    }

    void BuildOutputs()
    {
        OutputPanel.Children.Clear();
        foreach (var o in _cfg.Outputs)
        {
            string sub = o.Available ? $"{o.Width}×{o.Height}" : "no disponible";
            var rb = Choice("output", o.Label, sub, o == _cfg.Output, o.Available, () =>
            {
                _cfg.Output = o;
                Rebuild(BuildZoom);
            });
            if (!o.Available)
            {
                rb.ToolTip = "Esta pantalla no tiene el modo 3840×2160. Elige la TV 4K en PANTALLA, " +
                             "o activa DSR en el Panel de control de NVIDIA.";
                ToolTipService.SetShowOnDisabled(rb, true);
            }
            OutputPanel.Children.Add(rb);
        }
    }

    void BuildZoom()
    {
        ZoomPanel.Children.Clear();
        foreach (var z in GameConfig.Presets)
        {
            var (w, h) = _cfg.RenderSize(z);
            string sub = $"{w}×{h}" + (z.Recommended ? "  ★" : "");
            var rb = Choice("zoom", z.Name, sub, z == _cfg.Zoom, true, () => _cfg.Zoom = z);
            rb.Width = 130;
            ZoomPanel.Children.Add(rb);
        }
    }

    void BuildFilter()
    {
        FilterPanel.Children.Clear();
        var sharp = Choice("filter", "NÍTIDA", "píxeles definidos", !_cfg.LinearFilter, true,
            () => _cfg.LinearFilter = false);
        var smooth = Choice("filter", "SUAVE", "suavizada", _cfg.LinearFilter, true,
            () => _cfg.LinearFilter = true);
        sharp.Width = smooth.Width = 120;
        FilterPanel.Children.Add(sharp);
        FilterPanel.Children.Add(smooth);
    }

    void Rebuild(params Action[] builders)
    {
        _loading = true;
        foreach (var b in builders) b();
        _loading = false;
    }

    // ------------------------------------------------------------------ state

    void Refresh()
    {
        bool on = _cfg.Enabled;
        Options.IsEnabled = on;
        Options.Opacity = on ? 1 : 0.35;
        EnabledHint.Text = on
            ? "El juego se dibuja a baja resolución y se amplía a toda la pantalla, sin franjas negras."
            : "Desactivado: el juego funciona como el original.";

        var (rw, rh) = _cfg.Render;
        double scale = (double)_cfg.Output.Height / rh;
        RenderInfo.Text = on
            ? $"Juego {rw}×{rh}  →  pantalla {_cfg.Output.Width}×{_cfg.Output.Height}  (x{scale:0.##})"
            : "";

        if (!_cfg.GameFound)
        {
            StatusText.Text = $"No se encontró Alien Shooter en {_cfg.GameDir}";
            PlayButton.IsEnabled = false;
            return;
        }
        if (_loading)
        {
            StatusText.Text = $"Juego: {_cfg.GameDir}";
            return;
        }
        Save();
    }

    void Save()
    {
        if (GameConfig.GameRunning)
        {
            StatusText.Text = "El juego está abierto: los cambios se aplicarán al cerrarlo y volver a jugar.";
            return;
        }
        try
        {
            _cfg.Save();
            StatusText.Text = $"Ajustes guardados  ·  {_cfg.GameDir}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "No se pudieron guardar los ajustes: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ events

    void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        _cfg.Enabled = EnabledSwitch.IsChecked == true;
        Play(_cfg.Enabled ? "energy_on" : "energy_off");
        Refresh();
    }

    void OnClipChanged(object sender, RoutedEventArgs e)
    {
        _cfg.ClipCursor = ClipSwitch.IsChecked == true;
        Play("mouse_click");
        Refresh();
    }

    async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (GameConfig.GameRunning)
        {
            Play("mouse_error");
            StatusText.Text = "Alien Shooter ya está abierto.";
            return;
        }
        Save();
        Play("shotgun");
        PlayButton.IsEnabled = false;
        try
        {
            _cfg.Launch();
            StatusText.Text = "Iniciando Alien Shooter desde Steam...";
            await Task.Delay(1800);
            Close();
        }
        catch (Exception ex)
        {
            PlayButton.IsEnabled = true;
            StatusText.Text = "No se pudo abrir Steam: " + ex.Message;
        }
    }

    void OnBackup(object sender, RoutedEventArgs e)
    {
        Play("mouse_click");
        try
        {
            StatusText.Text = "Partidas respaldadas en " + _cfg.BackupSaves();
        }
        catch (Exception ex)
        {
            StatusText.Text = "No se pudo respaldar: " + ex.Message;
        }
    }

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Play("mouse_click");
        _cfg.OpenGameFolder();
    }

    void OnHover(object sender, MouseEventArgs e)
    {
        if (sender is UIElement { IsEnabled: true }) Play("mouse_over");
    }

    void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnClose(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ game look & sound

    void LoadGameCursorAndSounds()
    {
        string dir = _cfg.GameDir;
        try
        {
            string ani = Path.Combine(dir, "cursores", "arrow.ani");
            if (File.Exists(ani)) Cursor = new Cursor(ani);
        }
        catch (Exception)
        {
            // Keep the Windows cursor if the game's cursor cannot be loaded.
        }
        foreach (string name in new[] { "mouse_over", "mouse_click", "mouse_error", "energy_on", "energy_off", "shotgun" })
        {
            string wav = Path.Combine(dir, "Wav", name + ".wav");
            if (!File.Exists(wav)) continue;
            try
            {
                var p = new SoundPlayer(wav);
                p.Load();
                _sounds[name] = p;
            }
            catch (Exception)
            {
                // A broken sound file just means no sound for that action.
            }
        }
    }

    void Play(string name)
    {
        if (_sounds.TryGetValue(name, out var p)) p.Play();
    }

    // ------------------------------------------------------------------ security camera

    IReadOnlyList<string> _scenes = Array.Empty<string>();
    int _scene;
    bool _camAOnTop = true;
    readonly System.Windows.Threading.DispatcherTimer _camTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };

    void LoadGameArt()
    {
        var art = new GameArt(_cfg.GameDir);
        BackgroundImage.Source = art.Background;
        LogoImage.Source = art.Logo;
        _scenes = art.Scenes;
        if (_scenes.Count == 0)
        {
            NoSignal.Visibility = Visibility.Visible;
            CamLabel.Text = "CAM --";
            return;
        }
        _scene = new Random().Next(_scenes.Count);
        CamA.Source = GameArt.LoadFile(_scenes[_scene]);
        UpdateCamLabel();
        _camTimer.Tick += (_, _) => NextScene();
        _camTimer.Start();

        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => CamClock.Text = DateTime.Now.ToString("dd-MM-yyyy  HH:mm:ss");
        CamClock.Text = DateTime.Now.ToString("dd-MM-yyyy  HH:mm:ss");
        clock.Start();

        var blink = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.15, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.7))));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4))));
        RecDot.BeginAnimation(OpacityProperty, blink);
    }

    void UpdateCamLabel()
    {
        char sector = (char)('A' + _scene % 6);
        CamLabel.Text = $"CAM {_scene + 1:00} · SECTOR {sector}";
    }

    void NextScene()
    {
        _scene = (_scene + 1) % _scenes.Count;
        var front = _camAOnTop ? CamA : CamB;
        var back = _camAOnTop ? CamB : CamA;
        back.Source = GameArt.LoadFile(_scenes[_scene]);
        var fade = TimeSpan.FromSeconds(1.1);
        back.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, fade));
        front.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, fade));
        _camAOnTop = !_camAOnTop;
        UpdateCamLabel();
    }

    // Faulty red emergency light, like the game's corridors.
    void StartRedFlicker()
    {
        var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        (double t, double v)[] keys =
        {
            (0, 0.5), (1.6, 0.62), (2.0, 0.5), (3.1, 0.58), (3.15, 0.2), (3.22, 0.6),
            (3.3, 0.25), (3.4, 0.55), (5.2, 0.48), (6.0, 0.5),
        };
        foreach (var (t, v) in keys)
            a.KeyFrames.Add(new LinearDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        RedGlow.BeginAnimation(OpacityProperty, a);
    }

    /// <summary>Renders the window to a PNG without showing it (used to check the layout).</summary>
    public void RenderSnapshot(string path)
    {
        var root = (FrameworkElement)Content;
        var size = new Size(Width, Height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        root.UpdateLayout();   // second pass: button shapes bind to the arranged size
        var bmp = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
