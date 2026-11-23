using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MacDock;

/// <summary>
/// The small motions that make macOS feel smooth: windows open by fading in from slightly smaller,
/// close by fading out, minimize by shrinking away, content changes cross-fade, and scrolling glides
/// instead of jumping by lines.
/// </summary>
public static class MacMotion
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    /// <summary>
    /// Open / close / minimize animations for a borderless (AllowsTransparency) window whose visible
    /// content is <paramref name="root"/>. <paramref name="shown"/> runs once the window has finished
    /// opening (or coming back from the dock); <paramref name="hiding"/> runs as it starts to go away.
    /// </summary>
    public static void AttachWindow(Window window, FrameworkElement root, Action? shown = null, Action? hiding = null)
    {
        var scale = new ScaleTransform(1, 1);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = scale;
        bool closingAnimated = false, minimizing = false;

        void Animate(double fromOpacity, double toOpacity, double fromScale, double toScale, int ms, IEasingFunction ease, Action? done)
        {
            var time = TimeSpan.FromMilliseconds(ms);
            var fade = new DoubleAnimation(fromOpacity, toOpacity, time) { EasingFunction = ease };
            if (done != null) fade.Completed += (_, _) => done();
            root.BeginAnimation(UIElement.OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, toScale, time) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, toScale, time) { EasingFunction = ease });
        }

        void Open() => Animate(0, 1, 0.94, 1, 240, EaseOut, () =>
        {
            // Hand back to the plain values so later layout changes don't fight the animation.
            root.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            root.Opacity = 1;
            scale.ScaleX = scale.ScaleY = 1;
            shown?.Invoke();
        });

        root.Opacity = 0;
        window.Loaded += (_, _) => Open();
        window.Closing += (_, e) =>
        {
            if (closingAnimated || e.Cancel) return;
            e.Cancel = true;
            closingAnimated = true;
            hiding?.Invoke();
            Animate(root.Opacity, 0, 1, 0.96, 170, EaseIn, () => window.Dispatcher.BeginInvoke(window.Close));
        };
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Normal || window.WindowState == WindowState.Maximized)
            {
                if (!minimizing) return;
                minimizing = false;
                Open();
            }
        };

        // Minimize: shrink and fade away first (macOS "Scale" effect), then really minimize.
        window.Resources["MacMotion.Minimize"] = new Action(() =>
        {
            if (minimizing) return;
            minimizing = true;
            hiding?.Invoke();
            Animate(root.Opacity, 0, 1, 0.85, 200, EaseIn, () => window.WindowState = WindowState.Minimized);
        });
    }

    /// <summary>Minimizes a window attached with <see cref="AttachWindow"/> with its animation (plain minimize otherwise).</summary>
    public static void Minimize(Window window)
    {
        if (window.Resources["MacMotion.Minimize"] is Action animated) animated();
        else window.WindowState = WindowState.Minimized;
    }

    /// <summary>New content fades in while sliding up a few pixels (a pane change, a page of results).</summary>
    public static void Reveal(FrameworkElement element, double rise = 8, int ms = 180)
    {
        var move = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = move;
        var time = TimeSpan.FromMilliseconds(ms);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = EaseOut });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, time) { EasingFunction = EaseOut });
    }

    /// <summary>Fades a whole window out (used when MacDock quits) and then runs <paramref name="done"/>.</summary>
    public static void FadeOut(Window window, int ms, Action? done = null)
    {
        var fade = new DoubleAnimation(window.Opacity, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseIn };
        if (done != null) fade.Completed += (_, _) => done();
        window.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ---------------- Smooth scrolling ----------------

    private sealed class ScrollState
    {
        public double Target;
        public double Current;
        public bool Running;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("SmoothScrollState", typeof(ScrollState), typeof(MacMotion));

    /// <summary>
    /// Mouse-wheel scrolling glides to its destination (each notch adds to where it's heading, so fast
    /// spins accelerate) instead of jumping three lines at a time.
    /// </summary>
    public static void SmoothScroll(ScrollViewer viewer)
    {
        if (viewer.GetValue(StateProperty) != null) return;
        var state = new ScrollState();
        viewer.SetValue(StateProperty, state);
        viewer.PreviewMouseWheel += (_, e) =>
        {
            if (viewer.ScrollableHeight <= 0) return; // let an outer list scroll
            e.Handled = true;
            if (!state.Running) state.Target = state.Current = viewer.VerticalOffset;
            state.Target = Math.Clamp(state.Target - e.Delta * 0.9, 0, viewer.ScrollableHeight);
            if (state.Running) return;
            state.Running = true;
            TimeSpan last = TimeSpan.Zero;
            void Step(object? s, EventArgs args)
            {
                var now = ((RenderingEventArgs)args).RenderingTime;
                double dt = last == TimeSpan.Zero ? 16 : Math.Clamp((now - last).TotalMilliseconds, 1, 50);
                last = now;
                // Ease toward the target: about 90 % of the way in 150 ms, whatever the frame rate.
                double k = 1 - Math.Pow(0.02, dt / 150);
                state.Current += (state.Target - state.Current) * k;
                if (Math.Abs(state.Target - state.Current) < 0.5) state.Current = state.Target;
                viewer.ScrollToVerticalOffset(state.Current);
                if (state.Current == state.Target)
                {
                    CompositionTarget.Rendering -= Step;
                    state.Running = false;
                }
            }
            CompositionTarget.Rendering += Step;
        };
        // Scrollbar drags and keyboard scrolling move the offset directly: follow them.
        viewer.ScrollChanged += (_, _) => { if (!state.Running) state.Target = state.Current = viewer.VerticalOffset; };
    }
}
