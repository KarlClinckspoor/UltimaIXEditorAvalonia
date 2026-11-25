using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using U9.Formats.Models;
using U9.Formats.Objects;
using U9Editor.Core;

namespace U9Editor.App
{
    /// <summary>Adding and removing object properties, their help, and the type-wide values of TYPES.DAT.</summary>
    public partial class MainWindow
    {
        sealed class PropChoice
        {
            public int Id;
            public override string ToString() => $"{PropertyNames.NameOf(Id)} (0x{Id:X2})";
        }

        static readonly (int Bit, string Name, string Tip)[] typeFlags =
        {
            (0x01, "Never hidden", "TYPES.DAT flag 0x01: always drawn"),
            (0x02, "NPC-only collision", "TYPES.DAT flag 0x02: only NPCs collide with it"),
            (0x04, "Partial collision", "TYPES.DAT flag 0x04"),
            (0x08, "Camera passes", "TYPES.DAT flag 0x08: does not block the camera"),
            (0x10, "Portal block", "TYPES.DAT flag 0x10"),
            (0x20, "Unique model", "TYPES.DAT flag 0x20"),
            (0x100, "Mesh collision", "TYPES.DAT flag 0x100: collides with its model's faces"),
        };
        readonly Dictionary<int, CheckBox> typeFlagBoxes = new Dictionary<int, CheckBox>();
        int typeShown = -1;

        void ShowTypeAndAddRow(SceneObject o)
        {
            var target = o != null ? propTarget ?? o.Source : null;
            ShowType(target?.Type ?? -1);
            bool canAdd = o != null && editor != null && o.Kind == ObjectFileKind.Nonfixed && target != null;
            PropAddRow.IsEnabled = canAdd;
            if (!canAdd) { PropAddId.ItemsSource = null; return; }
            var have = editor.PropertiesOf(target).Select(p => p.Id).ToHashSet();
            PropAddId.ItemsSource = PropertyNames.All.Keys
                .Where(id => id != PropertyNames.Contents && id != PropertyNames.NextPropertyPointer && !have.Contains(id))
                .OrderBy(id => PropertyNames.NameOf(id), StringComparer.OrdinalIgnoreCase)
                .Select(id => new PropChoice { Id = id }).ToList();
        }

        void OnPropAddPicked(object sender, SelectionChangedEventArgs e)
        {
            if (PropAddId.SelectedItem is PropChoice c) PropHelpText.Text = PropertyHelp.Of(c.Id);
        }

        void OnPropAdd(object sender, RoutedEventArgs e)
        {
            if (propOwner == null || propTarget == null || editor == null) return;
            if (PropAddId.SelectedItem is not PropChoice c) { StatusText.Text = "Pick the property to add first."; return; }
            if (!ParseValue(PropAddA.Text, out int a) || !ParseValue(PropAddB.Text, out int b))
            {
                StatusText.Text = "Property values are whole numbers from 0 to 65535 (empty is 0).";
                return;
            }
            var owner = propOwner;
            var target = propTarget;
            Edit(() =>
            {
                editor.AddProperty(owner, target, c.Id, a, b);
                StatusText.Text = $"Added {PropertyNames.NameOf(c.Id)}: {a}, {b}. Ctrl+Z takes it back.";
            });
            editors[editor.MapNumber] = editor;
            ShowContents(owner, sameObject: true);
            UpdateTitle();
        }

        static bool ParseValue(string text, out int v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(text)) return true;
            return int.TryParse(text.Trim(), out v) && v >= 0 && v <= 0xFFFF;
        }

        void OnPropRemove(object sender, RoutedEventArgs e)
        {
            if (PropList.SelectedItem is not PropItem pi || propOwner == null || propTarget == null || editor == null) return;
            var owner = propOwner;
            var target = propTarget;
            Edit(() =>
            {
                editor.RemoveProperty(owner, target, pi.Property);
                StatusText.Text = $"Removed {pi.Property.Name}. Ctrl+Z puts it back.";
            });
            editors[editor.MapNumber] = editor;
            ShowContents(owner, sameObject: true);
            UpdateTitle();
        }

        // ---- the type's TYPES.DAT entry

        void ShowType(int type)
        {
            var types = editor?.Types;
            TypePanel.IsVisible = types != null && type >= 0 && type < types.Count;
            typeShown = TypePanel.IsVisible ? type : -1;
            if (!TypePanel.IsVisible) return;
            if (typeFlagBoxes.Count == 0)
                foreach (var (bit, name, tip) in typeFlags)
                {
                    var cb = new CheckBox { Content = name, FontSize = 11, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
                    ToolTip.SetTip(cb, tip);
                    typeFlagBoxes[bit] = cb;
                    TypeFlagPanel.Children.Add(cb);
                }
            var t = types[type];
            int count = editor.Objects.All.Count(o => !o.Source.Removed && o.Source.Type == type);
            TypeHeader.Text = $"Type {type} ({typeNames?[type] ?? "no name"}): {count} on this map";
            TypeHp.Text = t.HitPoints.ToString();
            TypeWeight.Text = t.Weight.ToString();
            TypeVolume.Text = t.Volume.ToString();
            TypeBook.Text = t.Book.ToString();
            TypeModel.Text = t.DefaultModel.ToString();
            TypeUsecode.Text = t.Usecode.ToString();
            foreach (var kv in typeFlagBoxes) kv.Value.IsChecked = (t.Flags & kv.Key) != 0;
        }

        void OnTypeSet(object sender, RoutedEventArgs e)
        {
            if (editor?.Types == null || typeShown < 0) return;
            var values = new List<(TypeField, int)>();
            bool Field(TextBox box, TypeField f)
            {
                if (!int.TryParse(box.Text?.Trim(), out int v) || v < 0 || v > TypeTable.MaxOf(f))
                {
                    StatusText.Text = $"{f} is a whole number from 0 to {TypeTable.MaxOf(f)}.";
                    return false;
                }
                values.Add((f, v));
                return true;
            }
            if (!Field(TypeHp, TypeField.HitPoints) || !Field(TypeWeight, TypeField.Weight) || !Field(TypeVolume, TypeField.Volume) ||
                !Field(TypeBook, TypeField.Book) || !Field(TypeModel, TypeField.DefaultModel) || !Field(TypeUsecode, TypeField.Usecode)) return;
            int flags = editor.Types[typeShown].Flags;
            foreach (var kv in typeFlagBoxes) flags = kv.Value.IsChecked == true ? flags | kv.Key : flags & ~kv.Key;
            values.Add((TypeField.Flags, flags));
            int type = typeShown;
            string before = editor.UndoDescription;
            Edit(() => editor.SetTypeFields(type, values));
            editors[editor.MapNumber] = editor;
            StatusText.Text = editor.UndoDescription != before
                ? $"Type {type} changed for every object of it (Ctrl+S writes TYPES.DAT, Ctrl+Z takes it back)." : "Nothing changed.";
            UpdateTitle();
        }

        void OnTypeEdited(int type)
        {
            if (editor != null) editors[editor.MapNumber] = editor;
            if (type == typeShown) ShowType(type);
            UpdateTitle();
        }
    }
}
