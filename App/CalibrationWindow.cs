using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace PenCalibration.App
{
    /// <summary>
    /// Fullscreen calibration screen: shows one crosshair at a time, records each pen tap,
    /// then fits and saves the correction. Mirrors the flow of Wacom's "Calibrate" tab.
    /// </summary>
    public class CalibrationWindow : Window
    {
        private enum Stage { Connecting, Error, Intro, Measuring, Review, Verify }

        // A tap is matched with the driver's latest position; anything older didn't come from the pen.
        private const double MaxSampleAgeMs = 250;

        private readonly DriverClient driver = new();
        private readonly TargetCanvas canvas = new();
        private readonly TextBlock title = new() { FontSize = 28, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock body = new() { FontSize = 16, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 720, LineHeight = 26 };
        private readonly TextBlock footer = new() { FontSize = 13, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 24) };

        private readonly List<Sample> samples = new();
        private Stage stage = Stage.Connecting;
        private int targetCount = 9;
        private FitMode fitMode = FitMode.Perspective;
        private int screenIndex;
        private string? tablet;
        private CalibrationResult? result;
        private bool busy;
        // Set once the user picks a screen with N, so following the pen doesn't fight them.
        private bool screenChosenByUser;
        private DateTime activatedAt = DateTime.MinValue;
        private readonly DispatcherTimer penFollower = new() { Interval = TimeSpan.FromMilliseconds(400) };

        public CalibrationWindow()
        {
            Title = "Pen Calibration";
            Background = Brushes.Black;
            SystemDecorations = SystemDecorations.None;
            Topmost = true;
            FontFamily = new FontFamily("fonts:Inter#Inter");

            var text = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Children = { title, body } };
            var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,2*,Auto") };
            Grid.SetRow(text, 1);
            Grid.SetRow(footer, 3);
            Grid.SetRowSpan(canvas, 4);
            grid.Children.Add(canvas);
            grid.Children.Add(text);
            grid.Children.Add(footer);
            text.IsHitTestVisible = false;
            Content = grid;

            canvas.PointerPressed += OnPointerPressed;
            Activated += (_, _) => activatedAt = DateTime.Now;
            Opened += async (_, _) =>
            {
                // Find the pen display before going fullscreen, so the window doesn't hop screens.
                await ConnectAsync();
                MoveToScreen(await PenScreenAsync(maxAgeMs: double.MaxValue) ?? 0);
                Activate();
                penFollower.Start();
            };
            penFollower.Tick += async (_, _) => await FollowPenAsync();
            Closing += (_, _) =>
            {
                penFollower.Stop();
                driver.Dispose();
            };
        }

        // ---- Flow --------------------------------------------------------------------------

        private async Task ConnectAsync()
        {
            Present(Stage.Connecting, "Connecting…", "Looking for the Pen Calibration filter in OpenTabletDriver.", "Esc  quit");
            try
            {
                await driver.ConnectAsync();
                ShowIntro();
            }
            catch (Exception ex)
            {
                ShowError("Can't reach OpenTabletDriver",
                    "Install the Pen Calibration plugin, add it under Filters, click Apply, and use Absolute Mode.\n\n" + ex.Message,
                    "R  try again     Esc  quit");
            }
        }

        /// <summary>
        /// Index (into <see cref="SortedScreens"/>) of the screen the pen was last seen on, if OTD
        /// reported a pen position within <paramref name="maxAgeMs"/> and sees the same number of screens.
        /// </summary>
        private async Task<int?> PenScreenAsync(double maxAgeMs)
        {
            if (!driver.Connected)
                return null;
            try
            {
                var (index, count, ageMs) = await driver.GetScreenAsync();
                return ageMs <= maxAgeMs && count == SortedScreens().Count ? index : null;
            }
            catch (DriverException)
            {
                return null;
            }
        }

        /// <summary>On the start screen, move to whichever screen the pen is hovering over.</summary>
        private async Task FollowPenAsync()
        {
            if (stage != Stage.Intro || busy || screenChosenByUser)
                return;
            try
            {
                if (await PenScreenAsync(maxAgeMs: 500) is int index && index != screenIndex && stage == Stage.Intro)
                    MoveToScreen(index);
            }
            catch (Exception)
            {
                // Connection problems surface on the next real action; following the pen is best effort.
            }
        }

        private void ShowIntro()
        {
            samples.Clear();
            result = null;
            canvas.Clear();
            Cursor = Cursor.Default;
            Present(Stage.Intro, "Pen Calibration",
                $"Tap the centre of each crosshair with the pen tip, holding the pen the way you normally draw.\n" +
                $"Targets: {targetCount}   ·   Correction: {fitMode}\n\nTap anywhere to begin.\n" +
                "Wrong screen? Hover the pen over your drawing display, or press N.",
                "T  number of targets     P  correction type     N  next screen     C  clear saved calibration     Esc  quit");
        }

        private async Task BeginMeasuringAsync()
        {
            if (targetCount < CalibrationSolver.MinimumPoints(fitMode))
                fitMode = FitMode.Affine;

            await driver.BeginAsync();
            samples.Clear();
            canvas.Clear();
            // Hide the cursor: the eye should be on the crosshair, not on where the cursor thinks the pen is.
            Cursor = new Cursor(StandardCursorType.None);
            stage = Stage.Measuring;
            ShowNextTarget();
        }

        private void ShowNextTarget()
        {
            var layout = CalibrationSolver.TargetLayout(targetCount);
            var size = canvas.Bounds.Size;
            canvas.Targets = layout.Select(p => new Point(p.X * size.Width, p.Y * size.Height)).ToList();
            canvas.Current = samples.Count;
            Present(Stage.Measuring, "", $"Target {samples.Count + 1} of {targetCount}", "Backspace  undo last tap     Esc  cancel");
        }

        private async Task RecordTapAsync(Point click)
        {
            var target = canvas.Targets[samples.Count];
            var raw = await driver.GetPositionAsync();
            if (raw.AgeMs > MaxSampleAgeMs)
            {
                body.Text = $"That tap didn't come from the pen (no pen report for {raw.AgeMs:F0} ms). Tap the target with the pen.";
                return;
            }

            tablet = raw.Tablet;
            samples.Add(new Sample((target.X, target.Y), (click.X, click.Y), (raw.X, raw.Y)));
            canvas.Taps.Add(click);

            if (samples.Count < targetCount)
            {
                ShowNextTarget();
                return;
            }

            await driver.EndAsync();
            ShowReview();
        }

        private void ShowReview()
        {
            Cursor = Cursor.Default;
            canvas.Current = -1;
            try
            {
                var size = canvas.Bounds.Size;
                result = CalibrationSolver.Solve(samples, fitMode, size.Width, size.Height);
            }
            catch (Exception ex)
            {
                ShowError("Calibration failed", ex.Message, "R  try again     Esc  quit");
                return;
            }

            string stats = $"Average error  {result.ErrorBefore.Average():F1} px  →  {result.ErrorAfter.Average():F1} px\n" +
                           $"Worst error  {result.ErrorBefore.Max():F1} px  →  {result.ErrorAfter.Max():F1} px\n" +
                           $"Tablet: {tablet}";
            if (result.Problem is string problem)
            {
                ShowError("Measurement looks wrong", problem + "\n\n" + stats, "R  try again     Esc  quit");
                return;
            }

            Present(Stage.Review, "Calibration measured", stats, "Enter  save and apply     R  redo     Esc  quit without saving");
        }

        private async Task SaveAsync()
        {
            if (result == null || tablet == null)
                return;

            await driver.ApplyAsync(new CalibrationRecord
            {
                Tablet = tablet,
                Matrix = result.Correction.Values,
                Targets = samples.Count,
                Mode = fitMode.ToString(),
                AverageErrorBefore = result.ErrorBefore.Average(),
                WorstErrorBefore = result.ErrorBefore.Max(),
                AverageErrorAfter = result.ErrorAfter.Average(),
                WorstErrorAfter = result.ErrorAfter.Max(),
                CentreShift = new[] { result.CentreShift.X, result.CentreShift.Y },
            });
            canvas.Taps.Clear();
            canvas.Current = -1;
            // Same as while measuring: judge the result by where the dots land, not by the cursor.
            Cursor = new Cursor(StandardCursorType.None);
            Present(Stage.Verify, "Saved",
                "The correction is active. Tap the crosshairs again to check — your taps are drawn as dots.",
                "R  recalibrate     Esc / Enter  done");
        }

        private async Task ClearAsync()
        {
            // The tablet name comes from the driver, so a pen report is needed first.
            var raw = await driver.GetPositionAsync();
            await driver.ClearAsync(raw.Tablet);
            body.Text = $"Saved calibration for {raw.Tablet} removed.\n\nTap anywhere to begin a new calibration.";
        }

        // ---- Input -------------------------------------------------------------------------

        private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(canvas);
            // Pen tip (and touch) arrive as the left button; ignore barrel buttons and right clicks.
            if (!point.Properties.IsLeftButtonPressed || busy)
                return;

            // The click that brings the window to the front shouldn't also start the calibration.
            if (stage == Stage.Intro && (!IsActive || (DateTime.Now - activatedAt).TotalMilliseconds < 400))
            {
                Activate();
                return;
            }

            await Run(async () =>
            {
                switch (stage)
                {
                    case Stage.Intro:
                        await BeginMeasuringAsync();
                        break;
                    case Stage.Measuring:
                        await RecordTapAsync(point.Position);
                        break;
                    case Stage.Verify:
                        canvas.Taps.Add(point.Position);
                        canvas.InvalidateVisual();
                        break;
                }
            });
        }

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (busy)
                return;

            await Run(async () =>
            {
                switch (stage, e.Key)
                {
                    case (Stage.Verify, Key.Enter):
                    case (_, Key.Escape) when stage != Stage.Measuring:
                        Close();
                        break;

                    case (Stage.Measuring, Key.Escape):
                        await driver.EndAsync();
                        ShowIntro();
                        break;

                    case (Stage.Measuring, Key.Back) when samples.Count > 0:
                        samples.RemoveAt(samples.Count - 1);
                        canvas.Taps.RemoveAt(canvas.Taps.Count - 1);
                        ShowNextTarget();
                        break;

                    case (Stage.Error, Key.R) when !driver.Connected:
                        await ConnectAsync();
                        break;

                    case (Stage.Error or Stage.Review or Stage.Verify, Key.R):
                        ShowIntro();
                        break;

                    case (Stage.Review, Key.Enter):
                        await SaveAsync();
                        break;

                    case (Stage.Intro, Key.T):
                    {
                        var counts = CalibrationSolver.TargetCounts.ToList();
                        targetCount = counts[(counts.IndexOf(targetCount) + 1) % counts.Count];
                        ShowIntro();
                        break;
                    }

                    case (Stage.Intro, Key.D4 or Key.NumPad4):
                        targetCount = 4;
                        ShowIntro();
                        break;
                    case (Stage.Intro, Key.D5 or Key.NumPad5):
                        targetCount = 5;
                        ShowIntro();
                        break;
                    case (Stage.Intro, Key.D9 or Key.NumPad9):
                        targetCount = 9;
                        ShowIntro();
                        break;

                    case (Stage.Intro, Key.P):
                        fitMode = fitMode == FitMode.Perspective || targetCount < 4 ? FitMode.Affine : FitMode.Perspective;
                        ShowIntro();
                        break;

                    case (Stage.Intro, Key.N):
                        screenChosenByUser = true;
                        MoveToScreen(screenIndex + 1);
                        break;

                    case (Stage.Intro, Key.C):
                        await ClearAsync();
                        break;
                }
            });
        }

        private async Task Run(Func<Task> action)
        {
            busy = true;
            try
            {
                await action();
            }
            catch (DriverException ex) when (stage != Stage.Measuring)
            {
                body.Text = ex.Message;
            }
            catch (Exception ex)
            {
                // Don't leave the driver's correction switched off behind an error screen.
                if (driver.Connected)
                    _ = driver.EndAsync().ContinueWith(_ => { });
                Cursor = Cursor.Default;
                ShowError("Something went wrong", ex.Message, "R  try again     Esc  quit");
            }
            finally
            {
                busy = false;
            }
        }

        // ---- Helpers -----------------------------------------------------------------------

        private void Present(Stage next, string heading, string text, string keys)
        {
            stage = next;
            title.Text = heading;
            title.IsVisible = heading.Length > 0;
            body.Text = text;
            footer.Text = keys;
            canvas.InvalidateVisual();
        }

        private void ShowError(string heading, string text, string keys)
        {
            canvas.Current = -1;
            Present(Stage.Error, heading, text, keys);
        }

        /// <summary>Screens left to right, then top to bottom: the same order the plugin counts OTD's displays in.</summary>
        private List<Screen> SortedScreens() => Screens.All.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();

        private void MoveToScreen(int index)
        {
            var screens = SortedScreens();
            if (screens.Count == 0)
                return;

            screenIndex = index % screens.Count;
            var screen = screens[screenIndex];
            WindowState = WindowState.Normal;
            Position = screen.Bounds.Position;
            // Give the window manager a moment to move the window before going fullscreen on that screen.
            Dispatcher.UIThread.Post(() => WindowState = WindowState.FullScreen, DispatcherPriority.Background);
        }
    }

    /// <summary>Draws the crosshairs and the recorded taps.</summary>
    public class TargetCanvas : Control
    {
        private static readonly IPen Bright = new Pen(Brushes.White, 2);
        private static readonly IPen Dim = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1.5);
        private static readonly IBrush TapBrush = new SolidColorBrush(Color.FromRgb(255, 120, 60));

        public List<Point> Targets { get; set; } = new();
        public List<Point> Taps { get; } = new();

        /// <summary>Index of the target to tap, or -1 to show all targets equally.</summary>
        public int Current { get; set; } = -1;

        public void Clear()
        {
            Targets = new List<Point>();
            Taps.Clear();
            Current = -1;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            // Transparent fill so the whole window receives pointer events.
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

            for (int i = 0; i < Targets.Count; i++)
            {
                bool active = Current < 0 || i == Current;
                // While measuring, show only the current target and the ones already done.
                if (Current >= 0 && i > Current)
                    continue;
                DrawCrosshair(context, Targets[i], active ? Bright : Dim, active && Current >= 0 ? 36 : 22);
            }

            foreach (var tap in Taps)
                context.DrawEllipse(TapBrush, null, tap, 3.5, 3.5);
        }

        private static void DrawCrosshair(DrawingContext context, Point c, IPen pen, double arm)
        {
            context.DrawLine(pen, new Point(c.X - arm, c.Y), new Point(c.X - 4, c.Y));
            context.DrawLine(pen, new Point(c.X + 4, c.Y), new Point(c.X + arm, c.Y));
            context.DrawLine(pen, new Point(c.X, c.Y - arm), new Point(c.X, c.Y - 4));
            context.DrawLine(pen, new Point(c.X, c.Y + 4), new Point(c.X, c.Y + arm));
            context.DrawEllipse(null, pen, c, arm * 0.45, arm * 0.45);
        }
    }
}
