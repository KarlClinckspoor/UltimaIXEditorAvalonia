using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Interactivity;
using U9.Formats.Triggers.Language;
using U9Editor.Core;

namespace U9Editor.App
{
    // The trigger script editor's link to the main window: the scripts, the names, the open map's undo steps.
    public partial class MainWindow : IScriptHost
    {
        TriggerNames triggerNames, baseTriggerNames;
        ScriptEditorWindow scriptWindow;

        /// <summary>The editor's own names (shipped next to the program) and the user's, which win.</summary>
        static string ShippedNamesFile => Path.Combine(AppContext.BaseDirectory, "names", "trigger-names.txt");
        static string UserNamesFile => Path.Combine(Settings.Folder, "trigger-names.txt");

        void LoadTriggerNames()
        {
            baseTriggerNames = TriggerNames.Build(typeNames, typeTable, ShippedNamesFile);
            triggerNames = TriggerNames.Build(typeNames, typeTable, ShippedNamesFile, UserNamesFile);
            if (triggerNames.Problems.Count > 0) StatusText.Text = "Names file: " + string.Join("; ", triggerNames.Problems.Take(3));
        }

        U9.Formats.Triggers.TriggerScripts IScriptHost.Scripts => triggerScripts;
        TriggerNames IScriptHost.Names => triggerNames ??= new TriggerNames();
        MapEditor IScriptHost.Editor => editor;

        int[] IScriptHost.SelectedObjectSlots() => selected?.Source is U9.Formats.Objects.MapObject o && !o.Removed ? Cutscenes.ScriptSlots(o) : null;

        Dictionary<int, int> IScriptHost.ScriptUsers()
        {
            var d = new Dictionary<int, int>();
            if (editor?.Nonfixed == null) return d;
            foreach (var o in editor.Nonfixed.Objects)
            {
                if (o.Removed) continue;
                foreach (int s in Cutscenes.ScriptSlots(o).Where(x => x > 0).Distinct())
                    d[s] = d.TryGetValue(s, out int n) ? n + 1 : 1;
            }
            return d;
        }

        bool IScriptHost.RunScriptEdit(Action a)
        {
            if (editor == null) return false;
            string before = StatusText.Text;
            bool ok = true;
            try { a(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is InvalidDataException)
            {
                StatusText.Text = "Not done: " + ex.Message;
                ok = false;
            }
            UpdateTitle();
            return ok;
        }

        void IScriptHost.SaveNames()
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var lines = new List<string> { "# The user's names for the U9 Editor's trigger language (wins over the shipped names file)." };
                lines.AddRange(triggerNames.Lines(baseTriggerNames));
                File.WriteAllLines(UserNamesFile, lines);
            }
            catch (IOException ex) { StatusText.Text = "Names not saved: " + ex.Message; }
            catch (UnauthorizedAccessException ex) { StatusText.Text = "Names not saved: " + ex.Message; }
        }

        /// <summary>Opens the script editor (one window), at a script when given one.</summary>
        void OpenScriptEditor(int slot = 0)
        {
            if (triggerScripts == null) { StatusText.Text = "This game folder has no TRIGGERS.FLX."; return; }
            if (scriptWindow == null)
            {
                scriptWindow = new ScriptEditorWindow(this);
                scriptWindow.Closed += (o, e) => scriptWindow = null;
                scriptWindow.Show(this);
                if (slot > 0) scriptWindow.Load(slot);
            }
            else if (slot > 0) scriptWindow.Open(slot);
            else scriptWindow.Activate();
        }

        void OnOpenScriptEditor(object sender, RoutedEventArgs e) => OpenScriptEditor(0);

        bool scriptsAtStartDone;

        /// <summary>The --scripts start options: open the editor, type text, show a side list, save a picture and quit.</summary>
        void ScriptsAtStart()
        {
            OpenScriptEditor(Program.ScriptsAtStart);
            if (scriptWindow == null) return;
            if (Program.ScriptsTextPath != null && File.Exists(Program.ScriptsTextPath)) scriptWindow.SetText(File.ReadAllText(Program.ScriptsTextPath));
            if (Program.ScriptsSide != null) scriptWindow.ShowSide(Program.ScriptsSide);
            if (Program.ScriptsShotPath != null)
            {
                var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                t.Tick += (o, e) => { t.Stop(); scriptWindow.SaveShot(Program.ScriptsShotPath); closeAnyway = true; Close(); };
                t.Start();
            }
        }

        void OnScriptOpenEditor(object sender, RoutedEventArgs e) => OpenScriptEditor(scriptSlot);
    }
}
