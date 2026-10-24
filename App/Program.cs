using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace PenCalibration.App
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args) => AppBuilder.Configure<CalibrationApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .StartWithClassicDesktopLifetime(args);
    }

    public class CalibrationApp : Application
    {
        public override void Initialize()
        {
            Name = "Pen Calibration";
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new CalibrationWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }
}
