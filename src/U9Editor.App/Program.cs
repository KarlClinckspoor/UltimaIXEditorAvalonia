using System;
using Avalonia;

namespace U9Editor.App
{
    static class Program
    {
        /// <summary>--gamedata PATH: the install to open (the folder holding static/).</summary>
        public static string GameDataAtStart;
        /// <summary>--map N: the map to show first (default 9, Britannia).</summary>
        public static int MapAtStart = 9;
        /// <summary>--size WxH: the window's size.</summary>
        public static int Width, Height;
        /// <summary>--top-down: start in the straight-down map view.</summary>
        public static bool TopDownAtStart;
        /// <summary>--screenshot PATH: save the first drawn frame as a PNG, then quit (for scripted checks).</summary>
        public static string ScreenshotPath;
        /// <summary>--look X,ROW --distance D --pitch P --yaw Y: where the camera starts (point coordinates, world units, degrees).</summary>
        public static float LookX = float.NaN, LookRow = float.NaN, Distance = float.NaN, Pitch = float.NaN, Yaw = float.NaN;

        [STAThread]
        public static void Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--gamedata" && i + 1 < args.Length) GameDataAtStart = args[++i];
                else if (a == "--map" && i + 1 < args.Length) int.TryParse(args[++i], out MapAtStart);
                else if (a == "--top-down") TopDownAtStart = true;
                else if (a == "--screenshot" && i + 1 < args.Length) ScreenshotPath = args[++i];
                else if (a == "--look" && i + 1 < args.Length)
                {
                    var xy = args[++i].Split(',');
                    if (xy.Length == 2) { float.TryParse(xy[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out LookX); float.TryParse(xy[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out LookRow); }
                }
                else if (a == "--distance" && i + 1 < args.Length) float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out Distance);
                else if (a == "--pitch" && i + 1 < args.Length) float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out Pitch);
                else if (a == "--yaw" && i + 1 < args.Length) float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out Yaw);
                else if (a == "--size" && i + 1 < args.Length)
                {
                    var wh = args[++i].Split('x');
                    if (wh.Length == 2) { int.TryParse(wh[0], out Width); int.TryParse(wh[1], out Height); }
                }
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace(Avalonia.Logging.LogEventLevel.Warning);
    }
}
