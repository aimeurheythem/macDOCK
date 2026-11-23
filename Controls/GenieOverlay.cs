using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shell;
using MacDock.Native;

namespace MacDock.Controls;

/// <summary>
/// Plays the macOS "Genie" warp on a window snapshot.
///
/// Performance: this is NOT a layered (AllowsTransparency) window - those force WPF to render 3D in
/// software and copy every frame through system memory, which is what made the effect laggy.
/// Instead it is a normal GPU-rendered window made transparent through DWM glass
/// (DwmExtendFrameIntoClientArea), covering the whole virtual screen once, so it never resizes.
///
/// Rendering: the snapshot is a texture on a 3D grid mesh seen through an orthographic camera
/// (1 unit = 1 DIP); every grid vertex is moved by the "lamp" curve each frame.
///
/// The lamp curve is ported from the MIT-licensed Windhawk mod "MacOS Minimize Animation"
/// (Abdullah Masood), which ported it from Potassiumuncher's genie engine.
/// </summary>
public sealed class GenieOverlay : Window
{
    /// <summary>
    /// Full graphics acceleration (tier 2). Without it (remote desktop, virtual machines, old or busy
    /// graphics) WPF draws in software: the Genie then uses a coarser grid and no anti-aliasing, so it
    /// still runs at full frame rate.
    /// </summary>
    private static readonly bool FullGpu = (RenderCapability.Tier >> 16) >= 2;
    private static readonly int Columns = FullGpu ? 48 : 24, Rows = FullGpu ? 48 : 24;
    private const double OffScreen = -32000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Pixel-exact copy of the window, shown instead of the 3D mesh at progress 0 (first frame of a
    // minimize, last frame of a restore): the swap with the real window is then truly invisible.
    private readonly System.Windows.Controls.Image _exact = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    private readonly System.Windows.Controls.Canvas _exactLayer = new() { IsHitTestVisible = false };

    private readonly System.Windows.Controls.Viewport3D _viewport = new();
    private readonly MeshGeometry3D _mesh = new();
    private readonly ImageBrush _brush = new() { Stretch = Stretch.Fill };
    private readonly OrthographicCamera _camera = new();

    private Rect _screen = Rect.Empty;
    private Rect _window, _icon; // overlay coordinates (DIPs)
    private int _framesToWait;
    private int _animationId;
    private TaskCompletionSource? _framesDone;

    public GenieOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;          // GPU path (see class remarks)
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1),
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        RenderOptions.SetBitmapScalingMode(_exact, BitmapScalingMode.NearestNeighbor);
        if (!FullGpu) RenderOptions.SetEdgeMode(_viewport, EdgeMode.Aliased);
        _exactLayer.Children.Add(_exact);
        Content = new System.Windows.Controls.Grid
        {
            UseLayoutRounding = true,
            Children = { _viewport, _exactLayer },
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(hwnd) is { } source)
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                // Windows 11 would round the overlay's corners and outline it: neither belongs on an animation.
                int square = 1, none = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(hwnd, 33, ref square, sizeof(int));
                DwmSetWindowAttribute(hwnd, 34, ref none, sizeof(int));
            }
            WindowEffects.AddExStyle(hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        };

        // Texture coordinates and triangles never change; only vertex positions animate.
        var uv = new PointCollection();
        var triangles = new Int32Collection();
        for (int r = 0; r <= Rows; r++)
            for (int c = 0; c <= Columns; c++)
                uv.Add(new Point(c / (double)Columns, r / (double)Rows));
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
            {
                int a = r * (Columns + 1) + c, b = a + 1, d = a + Columns + 1, e = d + 1;
                triangles.Add(a); triangles.Add(d); triangles.Add(b);
                triangles.Add(b); triangles.Add(d); triangles.Add(e);
            }
        _mesh.TextureCoordinates = uv;
        _mesh.TriangleIndices = triangles;

        RenderOptions.SetBitmapScalingMode(_brush, BitmapScalingMode.HighQuality);
        var material = new DiffuseMaterial(_brush);
        var model = new Model3DGroup();
        model.Children.Add(new AmbientLight(Colors.White));
        model.Children.Add(new GeometryModel3D(_mesh, material) { BackMaterial = material });
        _viewport.Children.Add(new ModelVisual3D { Content = model });
        _viewport.Camera = _camera;
    }

    /// <summary>Creates the GPU surface ahead of time so the first animation starts instantly.</summary>
    /// <summary>
    /// Shows the window once, parked off screen, and keeps it shown. Moving it on screen later is
    /// instant, whereas showing a hidden window takes a frame or two (that delay was the blink).
    /// </summary>
    /// <summary>Shows the (new) overlay parked off screen, ready to be moved on screen for an animation.</summary>
    /// <summary>Back to a few pixels, off screen, between animations (its large surfaces are released).</summary>
    public void Shrink()
    {
        if (!_parked) return;
        _mesh.Positions = new Point3DCollection();
        _brush.ImageSource = null;
        _exact.Source = null;
        FitTo(new Rect(0, 0, 64, 64));
    }

    public void Park()
    {
        FitTo(new Rect(0, 0, 64, 64)); // tiny until an animation gives it its real size
        _mesh.Positions = new Point3DCollection();
        _parked = true;
        Left = OffScreen;
        Top = OffScreen;
        Show();
    }

    public async void Warm()
    {
        FitToScreens();
        _mesh.Positions = new Point3DCollection();
        Show();

        // Play one real animation ON SCREEN with a fully transparent picture (invisible): compiles the
        // code, sets up the GPU 3D pipeline and the window's on-screen surface now. Otherwise the first
        // real Genie stalled (looked like nothing played) and its first frame came late (a blink).
        // The overlay swallows clicks meanwhile, so this is kept short and done once at startup.
        const int w = 1280, h = 800;
        var dummy = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, new byte[w * h * 4], w * 4);
        dummy.Freeze();
        _brush.ImageSource = dummy;
        _exact.Source = dummy;
        _window = new Rect(100, 100, w, h);
        _icon = new Rect(400, 700, 48, 48);
        await AnimateAsync(0, 1, 150);
        if (ReferenceEquals(_brush.ImageSource, dummy)) // a real Genie may have started meanwhile: leave it alone
            await ClearAsync();
    }

    /// <summary>Shows the snapshot at progress <paramref name="t"/> (0 = window, 1 = inside the icon). Rects in screen DIPs.</summary>
    public async Task PrepareAsync(BitmapSource snapshot, Rect windowRect, Rect iconRect, double t)
    {
        // Only as big as the animation: the window, the dock icon and the curve between them (at the
        // size of every screen its GPU surfaces took several hundred MB).
        var area = Rect.Union(windowRect, iconRect);
        area.Inflate(24, 24);
        area = new Rect(Math.Floor(area.X), Math.Floor(area.Y), Math.Ceiling(area.Width), Math.Ceiling(area.Height));
        if (!_presented || area != _screen)
        {
            // Resized while parked off screen, with two frames to set up its surface, so the first
            // frame on screen is already a real one.
            _presented = true;
            _parked = true;
            Left = OffScreen;
            Top = OffScreen;
            _mesh.Positions = new Point3DCollection();
            _exact.Visibility = Visibility.Collapsed;
            FitTo(area);
            await WaitFramesAsync(2);
        }
        _parked = false;
        _window = Offset(windowRect);
        _icon = Offset(iconRect);
        _brush.ImageSource = snapshot;
        _exact.Source = snapshot;
        SetProgress(t);
        if (!IsVisible) Show();
        if (Left != _screen.Left || Top != _screen.Top)
        {
            Left = _screen.Left; // from the parking spot: a move, never a resize or a re-show
            Top = _screen.Top;
        }
    }

    /// <summary>
    /// Waits until the current frame is really on screen: WPF has rendered it (frames) AND the
    /// desktop compositor has presented it (DwmFlush). Only then may the real window disappear.
    /// </summary>
    public async Task WaitUntilOnScreenAsync()
    {
        await WaitFramesAsync(3);
        DwmFlush();
    }

    /// <summary>Completes after <paramref name="frames"/> frames have been rendered.</summary>
    public Task WaitFramesAsync(int frames)
    {
        _framesToWait = frames;
        _framesDone = new TaskCompletionSource();
        // Hooked only while waiting: a permanent Rendering handler would redraw 60x/s even when idle.
        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendering += OnRendering;
        return _framesDone.Task;
    }

    /// <summary>Animates from <paramref name="from"/> to <paramref name="to"/> over <paramref name="ms"/> milliseconds.</summary>
    /// <summary>
    /// Runs inside WPF's per-frame callback and times progress with the frame clock (RenderingTime),
    /// so every displayed frame gets its exact position: no skipped or doubled steps.
    /// </summary>
    /// <param name="startWhen">Optional: progress holds at <paramref name="from"/> until this returns true (max ~200 ms).</param>
    public Task AnimateAsync(double from, double to, double ms, Func<bool>? startWhen = null)
    {
        var done = new TaskCompletionSource();
        int id = ++_animationId; // a newer animation takes over the mesh; this one then just ends
        TimeSpan last = TimeSpan.MinValue;
        double elapsed = 0;
        int held = 0;
        void OnFrame(object? sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == last) return;
            if (id != _animationId)
            {
                CompositionTarget.Rendering -= OnFrame;
                done.TrySetResult();
                return;
            }
            if (startWhen != null)
            {
                if (!startWhen() && ++held < 12) return;
                startWhen = null; // the clock starts on this frame
            }
            // A frame that took long (first use, busy system) counts as one normal frame at most:
            // the animation is never skipped or cut short by a stall.
            // Real time, so a slow or busy PC drops frames but the animation keeps its speed and length.
            // Only a long stall (the first frame on a new surface) counts as one frame, so nothing is skipped.
            if (last != TimeSpan.MinValue) { double dt = (now - last).TotalMilliseconds; elapsed += dt > 120 ? 16.7 : dt; }
            last = now;
            double p = Math.Clamp(elapsed / ms, 0, 1);
            SetProgress(from + (to - from) * p);
            if (p < 1) return;
            CompositionTarget.Rendering -= OnFrame;
            done.TrySetResult();
        }
        SetProgress(from);
        CompositionTarget.Rendering += OnFrame;
        return done.Task;
    }

    /// <summary>
    /// Empties the overlay, lets that empty frame reach the screen, THEN hides it. Hiding a window
    /// that still shows the last frame makes that stale frame flash the next time it is shown.
    /// </summary>
    public async Task ClearAsync()
    {
        _mesh.Positions = new Point3DCollection();
        _exact.Visibility = Visibility.Collapsed;
        await WaitFramesAsync(2);
        _brush.ImageSource = null;
        _exact.Source = null;
        _parked = true;
        Left = OffScreen; // park, stay shown
        Top = OffScreen;
    }

    private bool _parked, _presented;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_framesDone == null || --_framesToWait > 0) return;
        CompositionTarget.Rendering -= OnRendering;
        var done = _framesDone;
        _framesDone = null;
        done.TrySetResult();
    }

    private void FitToScreens() =>
        FitTo(new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                       SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight), move: true);

    private void FitTo(Rect screen, bool move = false)
    {
        if (screen == _screen) return;
        _screen = screen;
        if (move)
        {
            Left = screen.Left;
            Top = screen.Top;
        }
        Width = screen.Width;
        Height = screen.Height;
        _viewport.Width = screen.Width;
        _viewport.Height = screen.Height;
        _camera.Width = screen.Width;
        _camera.Position = new Point3D(screen.Width / 2, -screen.Height / 2, 100);
        _camera.LookDirection = new Vector3D(0, 0, -1);
        _camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private Rect Offset(Rect r) => new(r.X - _screen.X, r.Y - _screen.Y, r.Width, r.Height);

    /// <summary>
    /// Genie at progress t (0 = normal window, 1 = collapsed into the icon). Overall easing is
    /// smoothstep (as in the reference); restore simply runs t backwards.
    /// </summary>
    private void SetProgress(double t)
    {
        float p = (float)Smoothstep(Math.Clamp(t, 0, 1));

        // Target, like the reference: a narrow neck centered on the icon, ending at the icon's top edge.
        double neckWidth = _icon.Width * 0.7; // reference: 22 px neck on a 33 px taskbar icon
        var w = (X: (float)_window.X, Y: (float)_window.Y, W: (float)_window.Width, H: (float)_window.Height);
        var i = (X: (float)(_icon.X + (_icon.Width - neckWidth) / 2), Y: (float)_icon.Y,
                 W: (float)neckWidth, H: (float)_icon.Height);

        var points = new Point3DCollection((Rows + 1) * (Columns + 1));
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int r = 0; r <= Rows; r++)
        {
            float ty = r / (float)Rows;
            for (int c = 0; c <= Columns; c++)
            {
                float tx = c / (float)Columns;
                LampVertex(tx, ty, p, w, i, out float x, out float y);
                points.Add(new Point3D(x, -y, 0));
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        // A fully collapsed mesh would flash as a thin line: show nothing instead (reference behavior).
        bool degenerate = maxX - minX < 2 || maxY - minY < 2;
        // At rest (progress 0) show the pixel-exact image; in motion, the warped mesh.
        bool atRest = t <= 0.0005;
        if (atRest)
        {
            System.Windows.Controls.Canvas.SetLeft(_exact, _window.X);
            System.Windows.Controls.Canvas.SetTop(_exact, _window.Y);
            _exact.Width = _window.Width;
            _exact.Height = _window.Height;
        }
        _exact.Visibility = atRest ? Visibility.Visible : Visibility.Collapsed;
        // The mesh is drawn at rest too, hidden under the exact copy: the snapshot texture gets uploaded
        // to the GPU while we wait, instead of stalling the first moving frame.
        _mesh.Positions = degenerate ? new Point3DCollection() : points;
    }

    /// <summary>
    /// The macOS "lamp" curve, ported verbatim (MIT) from Potassiumuncher's genie engine via the Windhawk
    /// mod "MacOS Minimize Animation". Maps mesh vertex (tx, ty in 0..1) at progress p to a screen point,
    /// given window rect w and target rect i:
    ///  - first 30% (k): the window stretches down to the target and its bottom narrows into a neck;
    ///  - remaining 70% (j): everything slides down into the neck and collapses to the target's top;
    ///  - effectX: a sine sway proportional to the window-to-target offset gives the genie ripple.
    /// </summary>
    private static void LampVertex(float tx, float ty, float p,
        (float X, float Y, float W, float H) w, (float X, float Y, float W, float H) i,
        out float outX, out float outY)
    {
        const float split = 0.3f;
        float k = p <= split ? p / split : 1f;
        float j = p > split ? (p - split) / (1f - split) : 0f;

        float expandHeight = i.Y - w.Y - w.H;
        float fullHeight = (i.Y - w.Y) - expandHeight * (1f - k);
        float height = fullHeight - j * fullHeight;

        float y = ty * height;
        float x = tx * i.W
                + tx * (w.W - i.W) * (1f - j) * (1f - ty)
                + tx * (w.W - i.W) * (1f - k) * ty;

        float offsetX = (i.X - w.X) * (y / (fullHeight + 0.1f)) * k + (i.X - w.X) * j;
        float offsetY = i.Y - w.Y - height - expandHeight * (1f - k);

        float effectX = MathF.Sin(((height - y) / fullHeight) * 2f * MathF.PI + MathF.PI)
                      * (w.X + w.W * tx - (i.X + i.W * tx)) / 7f * k;

        outX = w.X + x + offsetX + effectX;
        outY = w.Y + y + offsetY;
    }

    private static double Smoothstep(double x) => x * x * (3 - 2 * x);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
