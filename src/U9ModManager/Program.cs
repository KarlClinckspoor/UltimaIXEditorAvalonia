using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace U9ModManager
{
    static class Program
    {
        /// <summary>--game PATH: the game folder to show first; --home PATH: the manager's folder (instead of %APPDATA%\U9ModManager);
        /// --shot PATH: save the window as a PNG once shown, then quit (for scripted checks).</summary>
        public static string GameAtStart, HomeAtStart, ShotPath;

        [STAThread]
        public static void Main(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--game") GameAtStart = args[++i];
                else if (args[i] == "--home") HomeAtStart = args[++i];
                else if (args[i] == "--shot") ShotPath = args[++i];
            }
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
        }
    }

    public sealed class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }
}
