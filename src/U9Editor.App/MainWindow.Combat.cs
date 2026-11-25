using System;
using System.IO;
using Avalonia.Interactivity;
using Avalonia.Threading;
using U9Editor.Core;

namespace U9Editor.App
{
    /// <summary>Edit > Combat balance: the combat window over the open game folder (one at a time).</summary>
    public partial class MainWindow
    {
        CombatWindow combatWindow;
        bool combatAtStartDone;

        void OnOpenCombat(object sender, RoutedEventArgs e) => OpenCombat();

        void OpenCombat(string tab = null)
        {
            if (game == null) { StatusText.Text = "Open a game folder first."; return; }
            if (combatWindow != null) { combatWindow.Activate(); return; }
            CombatEditing editing;
            try { editing = CombatEditing.Open(game.Root); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                StatusText.Text = "No combat balance: " + ex.Message;
                return;
            }
            combatWindow = new CombatWindow(editing, t => typeNames?[t], tab);
            combatWindow.Closed += (s, a) => combatWindow = null;
            combatWindow.Show(this);
        }

        void CombatAtStart()
        {
            CombatWindow.ShotPath = Program.CombatShotPath;
            CombatWindow.ArmourAtStart = Program.CombatArmourAtStart;
            OpenCombat(Program.CombatAtStart);
            if (combatWindow != null && Program.CombatShotPath != null)
                combatWindow.Closed += (s, a) => Dispatcher.UIThread.Post(() => { closeAnyway = true; Close(); });
        }

        /// <summary>Whether the combat window holds unsaved changes (the main window asks before closing).</summary>
        bool CombatUnsaved => combatWindow?.HasChanges == true;
    }
}
