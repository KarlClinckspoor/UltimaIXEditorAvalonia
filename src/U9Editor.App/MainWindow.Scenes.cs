using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using U9.Formats.Objects;
using U9.Formats.Triggers.Language;
using U9.Formats.Triggers;
using U9Editor.Core;

namespace U9Editor.App
{
    // The Scenes tab: in-engine scenes (camera moves chained by trigger scripts), played in the view and edited.
    public partial class MainWindow
    {
        sealed class SceneItem
        {
            public CameraPath Path;
            public override string ToString() => Path.ToString();
        }

        sealed class ShotItem
        {
            public CameraShot Shot;
            public override string ToString() => Shot.ToString();
        }

        List<CameraPath> scenes = new List<CameraPath>();
        CameraPath scene;
        DispatcherTimer sceneTimer;
        readonly Stopwatch sceneClock = new Stopwatch();
        double sceneFromMs;
        CameraPose? viewBeforeScene;
        List<SceneObject> hiddenPoints;
        bool settingSceneTime;

        /// <summary>Finds the current map's scenes again, keeping the selected scene and move where they still are.</summary>
        void ShowScenes()
        {
            var keep = scene;
            int keepShot = ShotsList.SelectedIndex;
            scenes = editor?.Nonfixed != null && triggerScripts != null ? Cutscenes.Find(editor.Nonfixed, triggerScripts, View.Space) : new List<CameraPath>();
            var items = scenes.Select(p => new SceneItem { Path = p }).ToList();
            ScenesList.ItemsSource = items;
            int points = editor?.Nonfixed?.Objects.Count(o => !o.Removed && o.Type == CameraPoint.Type) ?? 0;
            ScenesHeader.Text = $"Scenes ({scenes.Count}; {points} camera points on this map)";
            scene = null;
            ShotsList.ItemsSource = null;
            ShotEdit.IsEnabled = false;
            if (keep != null && items.FirstOrDefault(i => i.Path.Starter == keep.Starter && i.Path.StarterScript == keep.StarterScript) is SceneItem again)
            {
                ScenesList.SelectedItem = again;     // fills the moves (OnSceneSelected)
                if (keepShot >= 0 && keepShot < again.Path.Shots.Count) ShotsList.SelectedIndex = keepShot;
            }
        }

        void OnScriptsEdited() => Dispatcher.UIThread.Post(() => { ShowScenes(); scriptWindow?.ScriptsChanged(); });

        void OnSceneSelected(object sender, SelectionChangedEventArgs e)
        {
            if (ScenesList.SelectedItem is not SceneItem si) return;
            if (sceneTimer != null || viewBeforeScene != null) StopScene(restore: true);
            scene = si.Path;
            ShotsList.ItemsSource = scene.Shots.Select(s => new ShotItem { Shot = s }).ToList();
            settingSceneTime = true;
            SceneTime.Maximum = Math.Max(0.1, scene.TotalMs / 1000.0);
            SceneTime.Value = 0;
            settingSceneTime = false;
            SceneClock.Text = $"0.0 / {scene.TotalMs / 1000.0:0.0} s";
            var starter = editor?.Objects.Of(scene.Starter);
            if (starter != null) View.SelectObject(starter);
            ShowScript(scene.StarterSlot, $"Script {scene.StarterSlot}: the scene's start ({CameraPath.ScriptName(scene.StarterScript)} of type {scene.Starter.Type} link {scene.Starter.Link})");
            StatusText.Text = $"Starts from {CameraPath.ScriptName(scene.StarterScript)} (script {scene.StarterSlot}: {string.Join(" ", triggerScripts[scene.StarterSlot].Select(o => o.ToString()))}) of the selected object.";
        }

        void OnShotSelected(object sender, SelectionChangedEventArgs e)
        {
            if (ShotsList.SelectedItem is not ShotItem it) { ShotEdit.IsEnabled = false; return; }
            var s = it.Shot;
            ShotEdit.IsEnabled = editor != null;
            ShotSeconds.Text = (s.Move.HalfSeconds / 2.0).ToString("0.#", CultureInfo.InvariantCulture);
            ShotGlide.IsChecked = s.Move.Glides;
            ShotStraight.IsChecked = s.Move.IsLinear;
            ShotBack.IsChecked = s.Move.Returns;
            ShotThen1.IsChecked = s.Move.RunsFirst;
            ShotThen2.IsChecked = s.Move.RunsSecond;
            int users = editor?.Nonfixed?.Objects.Count(o => !o.Removed && Cutscenes.ScriptSlots(o).Contains(s.Slot)) ?? 0;
            ShotInfo.Text = $"Camera point link {s.Point.Link}: {s.Point.Pose}. Sent by op {s.OpIndex} of script {s.Slot} " +
                            $"(of type {s.From.Type} link {s.From.Link}; {users} object{(users == 1 ? "" : "s")} run{(users == 1 ? "s" : "")} that script)" +
                            (s.Also.Length > 0 ? $"; the script also does {s.Also}" : "") + ".";
            ShowScript(s.Slot, $"Script {s.Slot}: sends the camera to point {s.Point.Link} (op {s.OpIndex}; run by type {s.From.Type} link {s.From.Link})");
            if (sceneTimer == null)
            {
                s.Point.Pose.ApplyTo(View.Camera);
                View.Redraw();
                var so = editor?.Objects.Of(s.Point.Object);
                if (so != null) View.SelectObject(so);
            }
        }

        void OnShotLook(object sender, RoutedEventArgs e)
        {
            if (ShotsList.SelectedItem is not ShotItem it) return;
            it.Shot.Point.Pose.ApplyTo(View.Camera);
            View.Redraw();
        }

        void OnShotKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { OnShotSet(sender, null); e.Handled = true; }
        }

        void OnShotSet(object sender, RoutedEventArgs e)
        {
            if (ShotsList.SelectedItem is not ShotItem it || editor == null) return;
            if (!double.TryParse(ShotSeconds.Text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) || sec < 0 || sec > 1023.5)
            {
                StatusText.Text = "Seconds: 0 to 1023.5, in half seconds.";
                return;
            }
            var move = CameraMove.Make((int)Math.Round(sec * 2), ShotGlide.IsChecked == true, ShotStraight.IsChecked == true, ShotBack.IsChecked == true,
                                       ShotThen1.IsChecked == true, ShotThen2.IsChecked == true);
            Edit(() => editor.SetCameraMove(it.Shot, move));
            UpdateTitle();
            StatusText.Text = $"Move to point {it.Shot.Point.Link}: {move} (script {it.Shot.Slot}). Ctrl+S saves TRIGGERS.FLX.";
        }

        void OnShotFromView(object sender, RoutedEventArgs e)
        {
            if (ShotsList.SelectedItem is not ShotItem it || editor == null) return;
            var so = editor.Objects.Of(it.Shot.Point.Object);
            if (so == null) return;
            var c = View.Camera;
            Edit(() => editor.SetCameraPoint(so, new CameraPose(c.Target, c.Yaw, c.Pitch, c.Distance)));
            ShowScenes();
            StatusText.Text = $"Camera point {it.Shot.Point.Link} put where the view is. Ctrl+Z takes it back.";
        }

        // ---- scripts and new scenes

        int scriptSlot;

        void ShowScript(int slot, string header)
        {
            scriptSlot = slot;
            ScriptHeader.Text = header;
            ScriptText.Text = triggerScripts != null && slot > 0 ? TriggerPrinter.Print(triggerScripts[slot], triggerNames, slot) : "";
        }

        void OnScriptReload(object sender, RoutedEventArgs e) { if (scriptSlot > 0) ShowScript(scriptSlot, ScriptHeader.Text); }

        void OnScriptApply(object sender, RoutedEventArgs e)
        {
            if (editor == null || scriptSlot <= 0) { StatusText.Text = "Pick a scene or a move first."; return; }
            TriggerOp[] ops;
            var compiled = TriggerCompiler.Compile(ScriptText.Text, triggerNames, scriptSlot);
            if (!compiled.Ok) { StatusText.Text = "Script not applied: " + string.Join("; ", compiled.Diagnostics.Where(d => !d.Warning).Take(3)); return; }
            ops = compiled.Ops;
            int slot = scriptSlot;
            string header = ScriptHeader.Text;
            Edit(() => editor.SetScriptOps(slot, ops));
            UpdateTitle();
            ShowScript(slot, header);
            StatusText.Text = $"Script {slot}: {ops.Length - 1} ops. Ctrl+Z takes it back, Ctrl+S saves TRIGGERS.FLX.";
        }

        CameraPose ViewPose() { var c = View.Camera; return new CameraPose(c.Target, c.Yaw, c.Pitch, Math.Max(c.Distance, 0.07f)); }

        /// <summary>A trigger zone to copy (record and property segment): one on this map, else on Britannia.</summary>
        bool ZoneTemplate(out byte[] record, out byte[] segment)
        {
            record = segment = null;
            bool ok(MapObject z) => z.Type == 1111 && z.Map.PropertiesOf(z).Any(p => p.Id == PropertySegment.OoTrigger) && z.Map.PropertiesOf(z).Any(p => p.Id == PropertySegment.TriggerArea);
            var z = editor?.Nonfixed?.Objects.FirstOrDefault(o => !o.Removed && ok(o))
                    ?? mapLinks?.From(9).Select(l => l.Zone).FirstOrDefault(ok);
            if (z == null) { StatusText.Text = "Not done: no trigger zone to copy."; return false; }
            record = z.Raw;
            segment = z.Map.Data.AsSpan(z.Map.DataStart + z.PropertyOffset, 16).ToArray();
            return true;
        }

        void AfterSceneMade(SceneObject zone, string what)
        {
            UpdateTitle();
            ShowScenes();
            if (zone != null && ScenesList.ItemsSource is List<SceneItem> items && items.FirstOrDefault(i => i.Path.Starter == zone.Source) is SceneItem it)
                ScenesList.SelectedItem = it;
            StatusText.Text = what + " Ctrl+Z takes it back; Ctrl+S saves the objects and TRIGGERS.FLX.";
        }

        void OnSceneNew(object sender, RoutedEventArgs e)
        {
            if (editor == null || !SelectedPoint(out int mx, out int my) || !ZoneTemplate(out var rec, out var seg)) return;
            double.TryParse(ShotSeconds.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec);
            int half = sec > 0 ? (int)Math.Round(sec * 2) : 4;
            SceneObject zone = null;
            var pose = ViewPose();
            Edit(() => zone = editor.NewScene(rec, seg, mx, my, editor.GroundZ(mx, my), pose, half));
            if (zone != null) AfterSceneMade(zone, $"New scene: walking into the zone at ({mx / 128.0:F1}, {my / 128.0:F1}) shows this view for {half / 2.0:0.#} s.");
        }

        void OnSceneAddMove(object sender, RoutedEventArgs e)
        {
            if (editor == null || scene == null) { StatusText.Text = "Pick a scene first."; return; }
            if (!double.TryParse(ShotSeconds.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) || sec <= 0) sec = 3;
            var move = CameraMove.Make((int)Math.Round(sec * 2), ShotGlide.IsChecked != false, ShotStraight.IsChecked == true, false, false, false);
            var keep = scene;
            var pose = ViewPose();
            Edit(() => editor.AddSceneMove(keep, pose, move));
            AfterSceneMade(editor.Objects.Of(keep.Starter), $"Move added: {move} to a new camera point where the view is.");
        }

        void OnSceneOrbit(object sender, RoutedEventArgs e)
        {
            if (editor == null || selected == null) { StatusText.Text = "Select the object to circle first (its click also sets the zone's ground point)."; return; }
            var ci = CultureInfo.InvariantCulture;
            if (!float.TryParse(OrbitRadius.Text?.Replace(',', '.'), NumberStyles.Float, ci, out float r) || r <= 0 ||
                !int.TryParse(OrbitHeight.Text, out int h) || !int.TryParse(OrbitCount.Text, out int n) ||
                !float.TryParse(OrbitSeconds.Text?.Replace(',', '.'), NumberStyles.Float, ci, out float lap) || lap <= 0)
            { StatusText.Text = "Orbit: radius (points), height (object units), points and lap seconds, as numbers."; return; }
            if (!SelectedPoint(out int mx, out int my) || !ZoneTemplate(out var rec, out var seg)) return;
            var o = selected;
            // the middle of its model, else where it stands
            int cz = o.Model >= 0 ? (int)Math.Round(o.BoundCentre.Y * MapSpace.HeightUnitsPerPoint * 4) : o.Source.Z;
            SceneObject zone = null;
            Edit(() => zone = editor.NewOrbitScene(rec, seg, mx, my, editor.GroundZ(mx, my), (float)o.Source.PointX, (float)o.Source.PointRow, cz, r, h, n, lap));
            if (zone != null) AfterSceneMade(zone, $"Orbit scene round {o.Name ?? "type " + o.Source.Type}: {n} points, {r:0.#} points out, a {lap:0.#} s lap, started by the zone at ({mx / 128.0:F1}, {my / 128.0:F1}).");
        }

        // ---- playing

        void OnScenePlay(object sender, RoutedEventArgs e)
        {
            if (sceneTimer != null) { StopScene(restore: true); return; }
            if (scene == null) { StatusText.Text = "Pick a scene first."; return; }
            var c = View.Camera;
            viewBeforeScene ??= new CameraPose(c.Target, c.Yaw, c.Pitch, c.Distance);
            HidePoints(true);
            sceneFromMs = SceneTime.Value * 1000 >= scene.TotalMs - 1 ? 0 : SceneTime.Value * 1000;
            sceneClock.Restart();
            sceneTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => SceneTick());
            sceneTimer.Start();
            ScenePlay.Content = "Stop";
        }

        void SceneTick()
        {
            if (scene == null) { StopScene(restore: true); return; }
            double ms = sceneFromMs + sceneClock.Elapsed.TotalMilliseconds;
            ShowSceneAt(Math.Min(ms, scene.TotalMs));
            if (ms > scene.TotalMs + 400) StopScene(restore: false);
        }

        void ShowSceneAt(double ms)
        {
            scene.Sample(ms).ApplyTo(View.Camera);
            View.Redraw();
            settingSceneTime = true;
            SceneTime.Value = ms / 1000;
            settingSceneTime = false;
            SceneClock.Text = $"{ms / 1000:0.0} / {scene.TotalMs / 1000.0:0.0} s";
            int at = scene.Shots.FindLastIndex(s => s.StartMs <= ms);
            if (at >= 0 && ShotsList.SelectedIndex != at && sceneTimer != null) ShotsList.SelectedIndex = at;
        }

        void OnSceneScrub(object sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (settingSceneTime || scene == null) return;
            if (sceneTimer != null) { sceneFromMs = SceneTime.Value * 1000; sceneClock.Restart(); return; }
            if (viewBeforeScene == null) { var c = View.Camera; viewBeforeScene = new CameraPose(c.Target, c.Yaw, c.Pitch, c.Distance); HidePoints(true); }
            ShowSceneAt(SceneTime.Value * 1000);
        }

        /// <summary>Stops playing; with <paramref name="restore"/> the view goes back to where it was before.</summary>
        void StopScene(bool restore)
        {
            sceneTimer?.Stop();
            sceneTimer = null;
            ScenePlay.Content = "Play";
            HidePoints(false);
            if (restore && viewBeforeScene is CameraPose v) { v.ApplyTo(View.Camera); View.Redraw(); }
            viewBeforeScene = null;
        }

        void HidePoints(bool hide)
        {
            if (hide)
            {
                if (hiddenPoints != null || View.Objects == null) return;
                hiddenPoints = View.Objects.All.Where(o => o.Kind == ObjectFileKind.Nonfixed && o.Source.Type == CameraPoint.Type).ToList();
                View.HideObjects(hiddenPoints, true);
            }
            else if (hiddenPoints != null)
            {
                View.HideObjects(hiddenPoints, false);
                hiddenPoints = null;
            }
        }
    }
}
