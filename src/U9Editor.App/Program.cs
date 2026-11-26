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
        /// <summary>--window-shot PATH: save the whole window (panels and the 3D frame) as a PNG, then quit.</summary>
        public static string WindowShotPath;
        /// <summary>--theme Light|Dark: the theme for this run (not remembered).</summary>
        public static string ThemeAtStart;
        /// <summary>--look X,ROW --distance D --pitch P --yaw Y: where the camera starts (point coordinates, world units, degrees).</summary>
        public static float LookX = float.NaN, LookRow = float.NaN, Distance = float.NaN, Pitch = float.NaN, Yaw = float.NaN;
        /// <summary>--scene N --scene-time S: start with the view of the map's scene N (as the Scenes tab lists them, from 0) at S seconds.</summary>
        public static int SceneAtStart = -1;
        /// <summary>--scripts N: open the trigger script editor at script N (0: no script); --scripts-text PATH: then put that text in
        /// it as if typed; --scripts-side globals|symbols: open a side list; --scripts-shot PATH: save the editor window as a PNG and quit.</summary>
        public static int ScriptsAtStart = -1;
        public static string ScriptsTextPath, ScriptsSide, ScriptsShotPath;
        /// <summary>--combat TAB: open the combat balance window on a tab (balance, balance-you, weapons, armour, creatures, natural,
        /// multipliers); --combat-shot PATH: save it as a PNG, then quit; --combat-armour N: wear armour set N (0 none .. 6 blackrock).</summary>
        public static string CombatAtStart, CombatShotPath;
        public static int CombatArmourAtStart = -1;
        /// <summary>--dialogue: open the dialogue editor; --dialogue-line N: on that line; --dialogue-find TEXT: with that search;
        /// --dialogue-shot PATH: save it as a PNG, then quit.</summary>
        public static bool DialogueAtStart, DialogueVowelAtStart;
        public static string DialogueShotPath, DialogueFindAtStart;
        public static int DialogueLineAtStart = -1;
        public static float SceneTimeAtStart;
        /// <summary>--prefab X0,ROW0,X1,ROW1: copy every object in that rectangle of points (with its ground); --paste X,ROW[,TURNS]: paste
        /// it with its first point there (quarter turns counter-clockwise); --paste-preview X,ROW[,TURNS]: only show its outline there.</summary>
        public static int[] PrefabAtStart, PasteAtStart, PastePreviewAtStart;
        /// <summary>--prefab-file PATH: a saved prefab to paste with --paste or --paste-preview (instead of --prefab).</summary>
        public static string PrefabFileAtStart;
        /// <summary>--tab NAME: the side tab to show (its header, e.g. Prefabs).</summary>
        public static string TabAtStart;
        /// <summary>--select X,ROW: select the object nearest that point (points, fractions allowed).</summary>
        public static float SelectX = float.NaN, SelectRow = float.NaN;
        /// <summary>--kit N: the Kit tab with map N's pieces; --kit-piece T picks one; --kit-height Z sets the build
        /// height; --kit-wall X,Y;X,Y;...: builds a wall of the pattern through those points (object units) at the start.</summary>
        public static int KitAtStart = -1, KitPieceAtStart = -1, KitHeightAtStart = -1, KitCornerAtStart = -1;
        public static string KitWallAtStart;
        /// <summary>--kit-place X,Y[;X,Y...]: places the picked piece as clicks there would (snapped), the last one only
        /// hovered (its ghost shown), so a window shot shows both (object units; the build height or the ground).</summary>
        public static string KitPlaceAtStart;
        /// <summary>--kit-floor X,Y;X,Y;X,Y...: fills that area with the picked piece at the start (before --kit-place).</summary>
        public static string KitFloorAtStart;
        /// <summary>--kit-pattern A,B: the wall pattern (after --kit-piece has filled in its own).</summary>
        public static string KitPatternAtStart;
        /// <summary>--kit-room AX,AY;BX,BY;CX,CY: builds a room at the start (walls from the pattern, --kit-corner, --kit-floor-type).</summary>
        public static string KitRoomAtStart;
        public static int KitFloorTypeAtStart = -1;

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
                else if (a == "--window-shot" && i + 1 < args.Length) WindowShotPath = args[++i];
                else if (a == "--theme" && i + 1 < args.Length) ThemeAtStart = args[++i];
                else if (a == "--look" && i + 1 < args.Length)
                {
                    var xy = args[++i].Split(',');
                    if (xy.Length == 2) { float.TryParse(xy[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out LookX); float.TryParse(xy[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out LookRow); }
                }
                else if (a == "--distance" && i + 1 < args.Length) float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out Distance);
                else if (a == "--pitch" && i + 1 < args.Length) float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out Pitch);
                else if (a == "--scene" && i + 1 < args.Length) int.TryParse(args[++i], out SceneAtStart);
                else if (a == "--scene-time" && i + 1 < args.Length) float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out SceneTimeAtStart);
                else if (a == "--scripts" && i + 1 < args.Length) int.TryParse(args[++i], out ScriptsAtStart);
                else if (a == "--scripts-text" && i + 1 < args.Length) ScriptsTextPath = args[++i];
                else if (a == "--scripts-side" && i + 1 < args.Length) ScriptsSide = args[++i];
                else if (a == "--scripts-shot" && i + 1 < args.Length) ScriptsShotPath = args[++i];
                else if (a == "--combat" && i + 1 < args.Length) CombatAtStart = args[++i];
                else if (a == "--combat-shot" && i + 1 < args.Length) CombatShotPath = args[++i];
                else if (a == "--combat-armour" && i + 1 < args.Length) int.TryParse(args[++i], out CombatArmourAtStart);
                else if (a == "--dialogue") DialogueAtStart = true;
                else if (a == "--dialogue-vowel") { DialogueAtStart = true; DialogueVowelAtStart = true; }
                else if (a == "--dialogue-line" && i + 1 < args.Length) { DialogueAtStart = true; int.TryParse(args[++i], out DialogueLineAtStart); }
                else if (a == "--dialogue-find" && i + 1 < args.Length) { DialogueAtStart = true; DialogueFindAtStart = args[++i]; }
                else if (a == "--dialogue-shot" && i + 1 < args.Length) { DialogueAtStart = true; DialogueShotPath = args[++i]; }
                else if (a == "--prefab" && i + 1 < args.Length) PrefabAtStart = Ints(args[++i]);
                else if (a == "--prefab-file" && i + 1 < args.Length) PrefabFileAtStart = args[++i];
                else if (a == "--paste" && i + 1 < args.Length) PasteAtStart = Ints(args[++i]);
                else if (a == "--paste-preview" && i + 1 < args.Length) PastePreviewAtStart = Ints(args[++i]);
                else if (a == "--tab" && i + 1 < args.Length) TabAtStart = args[++i];
                else if (a == "--kit" && i + 1 < args.Length) int.TryParse(args[++i], out KitAtStart);
                else if (a == "--kit-piece" && i + 1 < args.Length) int.TryParse(args[++i], out KitPieceAtStart);
                else if (a == "--kit-height" && i + 1 < args.Length) int.TryParse(args[++i], out KitHeightAtStart);
                else if (a == "--kit-wall" && i + 1 < args.Length) KitWallAtStart = args[++i];
                else if (a == "--kit-corner" && i + 1 < args.Length) int.TryParse(args[++i], out KitCornerAtStart);
                else if (a == "--kit-place" && i + 1 < args.Length) KitPlaceAtStart = args[++i];
                else if (a == "--kit-floor" && i + 1 < args.Length) KitFloorAtStart = args[++i];
                else if (a == "--kit-pattern" && i + 1 < args.Length) KitPatternAtStart = args[++i];
                else if (a == "--kit-room" && i + 1 < args.Length) KitRoomAtStart = args[++i];
                else if (a == "--kit-floor-type" && i + 1 < args.Length) int.TryParse(args[++i], out KitFloorTypeAtStart);
                else if (a == "--select" && i + 1 < args.Length)
                {
                    var xy = args[++i].Split(',');
                    if (xy.Length == 2) { float.TryParse(xy[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out SelectX); float.TryParse(xy[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out SelectRow); }
                }
                else if (a == "--yaw" && i + 1 < args.Length) float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out Yaw);
                else if (a == "--size" && i + 1 < args.Length)
                {
                    var wh = args[++i].Split('x');
                    if (wh.Length == 2) { int.TryParse(wh[0], out Width); int.TryParse(wh[1], out Height); }
                }
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        static int[] Ints(string s)
        {
            var parts = s.Split(',');
            var r = new int[parts.Length];
            for (int k = 0; k < parts.Length; k++) if (!int.TryParse(parts[k].Trim(), out r[k])) return null;
            return r;
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace(Avalonia.Logging.LogEventLevel.Warning);
    }
}
