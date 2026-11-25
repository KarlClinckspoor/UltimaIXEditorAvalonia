using System;
using System.IO;
using Avalonia.Interactivity;
using Avalonia.Threading;
using U9Editor.Core;

namespace U9Editor.App
{
    /// <summary>Edit > Dialogue: the dialogue editor over the open game folder (one at a time).</summary>
    public partial class MainWindow
    {
        DialogueWindow dialogueWindow;
        bool dialogueAtStartDone;

        void OnOpenDialogue(object sender, RoutedEventArgs e) => OpenDialogue();

        void OpenDialogue()
        {
            if (game == null) { StatusText.Text = "Open a game folder first."; return; }
            if (dialogueWindow != null) { dialogueWindow.Activate(); return; }
            DialogueEditing editing;
            try { editing = DialogueEditing.Open(game.Root); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                StatusText.Text = "No dialogue: " + ex.Message;
                return;
            }
            dialogueWindow = new DialogueWindow(editing);
            dialogueWindow.Closed += (s, a) => dialogueWindow = null;
            dialogueWindow.Show(this);
        }

        void DialogueAtStart()
        {
            DialogueWindow.ShotPath = Program.DialogueShotPath;
            DialogueWindow.FindAtStart = Program.DialogueFindAtStart;
            DialogueWindow.LineAtStart = Program.DialogueLineAtStart;
            DialogueWindow.VowelAtStart = Program.DialogueVowelAtStart;
            OpenDialogue();
            if (dialogueWindow != null && Program.DialogueShotPath != null)
                dialogueWindow.Closed += (s, a) => Dispatcher.UIThread.Post(() => { closeAnyway = true; Close(); });
        }

        bool DialogueUnsaved => dialogueWindow?.HasChanges == true;
    }
}
