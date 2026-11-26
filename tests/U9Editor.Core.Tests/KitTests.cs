using System;
using System.IO;
using System.Linq;
using System.Numerics;
using U9.Formats.Flx;
using U9.Formats.Models;
using U9.Formats.Objects;
using U9.Mods;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class KitTests
    {
        static string Root()
        {
            string env = Environment.GetEnvironmentVariable("U9_GAMEDATA");
            string src = !string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "static")) ? env : GameFolder.FindNear(AppContext.BaseDirectory)?.Root;
            Assert.SkipWhen(src == null, "Ultima IX game data not found");
            return src;
        }

        static KitCatalog Kit(string root, int map)
        {
            var objects = ObjectMap.Load(GamePaths.Find(root, $"static/fixed.{map}"), ObjectFileKind.Fixed);
            var types = TypeTable.Load(GamePaths.Find(root, "static/types.dat"));
            using var sap = FlxArchive.Open(GamePaths.Find(root, "static/sappear.flx"));
            return KitCatalog.Build(map, objects.Objects, types, m => m < sap.Count ? sap.Read(m) : null, null);
        }

        [Fact]
        public void Finds_shames_wall_kit_and_its_joins()
        {
            var kit = Kit(Root(), 10);
            Assert.Equal(65, kit.Pieces.Count);
            var wall = kit.Pieces[666];
            Assert.Equal(142, wall.Count);
            Assert.Equal(new Vector3(256, 128, 320), wall.Size);
            // wall after wall: 256 along its own x, not turned
            var step = kit.StraightStep(666, 666);
            Assert.NotNull(step);
            Assert.Equal(256, step.Value.Reach, 2f);
            Assert.True(Math.Abs(step.Value.Offset.Y) < 2 && Math.Abs(step.Value.Offset.Z) < 2);
            // a pillar (664, 128 square) 192 along a wall's axis: 128 / 2 + 128
            Assert.Contains(kit.Between(666, 664), j => j.Turn == 0 && Math.Abs(j.Offset.X - 191) <= 2 && Math.Abs(j.Offset.Y) <= 2);
            Assert.Equal(64, kit.JoinStep);
        }

        [Fact]
        public void Strings_walls_along_a_line_and_snaps_to_a_lone_wall()
        {
            string root = Root();
            var kit = Kit(root, 10);
            // a straight leg of 1024 units east: walls 256 apart, the last at the end point
            var wall = KitBuilding.Wall(kit, new[] { 666 }, new[] { new Vector2(1000, 1000), new Vector2(2024, 1000) }, 1073);
            Assert.Equal(new[] { 1000f, 1256f, 1512f, 1768f, 2024f }, wall.Select(p => MathF.Round(p.Position.X)).ToArray());
            Assert.All(wall, p => { Assert.Equal(1000, p.Position.Y, 1f); Assert.Equal(0, p.Yaw, 0.5f); Assert.Equal(1073, p.Z); });
            // a corner: the second leg runs north-south (+y), its pieces turned 90, none doubled at the corner
            var corner = KitBuilding.Wall(kit, new[] { 666 }, new[] { new Vector2(1000, 1000), new Vector2(1512, 1000), new Vector2(1512, 1512) }, 0);
            Assert.Equal(5, corner.Count);
            Assert.Equal(90, corner[^1].Yaw, 0.5f);
            Assert.Equal(1512, corner[^1].Position.Y, 1f);
            // a corner piece: a pillar one step past the first leg's last wall (192 = 128 + 64), the next leg from it
            var pillared = KitBuilding.Wall(kit, new[] { 666 }, new[] { new Vector2(0, 0), new Vector2(512, 0), new Vector2(512, 600) }, 0, snapDegrees: 90, corner: 664);
            // second leg: 600 long from the pillar; walls at 192, 448 and 704 (within half a step of its end)
            Assert.Equal(new[] { 666, 666, 666, 664, 666, 666, 666 }, pillared.Select(p => p.Type).ToArray());
            Assert.Equal(new Vector2(704, 0), new Vector2(MathF.Round(pillared[3].Position.X), MathF.Round(pillared[3].Position.Y)));
            Assert.Equal(new Vector2(704, 192), new Vector2(MathF.Round(pillared[4].Position.X), MathF.Round(pillared[4].Position.Y)));
            Assert.Equal(90, pillared[4].Yaw, 0.5f);
            // a pattern: wall, pillar, wall ... uses the learned steps (256 wall to wall, 191 wall to pillar)
            var mixed = KitBuilding.Wall(kit, new[] { 666, 664 }, new[] { new Vector2(0, 0), new Vector2(1000, 0) }, 0);
            Assert.Equal(new[] { 666, 664, 666, 664, 666, 664 }, mixed.Select(p => p.Type).ToArray());
            Assert.Equal(191, mixed[1].Position.X - mixed[0].Position.X, 2f);

            // a floor: Shame's 256-square tile (663) filling a 1024 x 512 rectangle: 4 x 2 tiles, middles half a step in
            var floor = KitBuilding.Fill(kit, 663, new[] { new Vector2(0, 0), new Vector2(1024, 0), new Vector2(1024, 512), new Vector2(0, 512) }, 1073);
            Assert.Equal(8, floor.Count);
            Assert.Contains(floor, p => Math.Abs(p.Position.X - 128) < 1 && Math.Abs(p.Position.Y - 128) < 1);
            Assert.Contains(floor, p => Math.Abs(p.Position.X - 896) < 1 && Math.Abs(p.Position.Y - 384) < 1);
            Assert.All(floor, p => Assert.Equal(1073, p.Z));
            // an L (snapped square): the missing quarter stays empty
            var ell = KitBuilding.Fill(kit, 663, new[] { new Vector2(0, 0), new Vector2(512, 0), new Vector2(512, 256), new Vector2(256, 256), new Vector2(256, 512), new Vector2(0, 512) }, 0, 90);
            Assert.Equal(3, ell.Count);

            // a room, 1100 x 700 asked: sides of 128 + 256n, so 1152 x 640 (4 and 2 walls); pillars on the corners, walls
            // 192 from them, then 256 apart; the inside 1024 x 512 (half a wall's 128 in all round) is 4 x 2 tiles
            var room = KitBuilding.Room(kit, 663, new[] { 666 }, 664, new Vector2(0, 0), new Vector2(1100, 0), new Vector2(0, 700), 1073);
            Assert.Equal(4, room.Count(p => p.Type == 664));
            Assert.Equal(12, room.Count(p => p.Type == 666));
            Assert.Equal(8, room.Count(p => p.Type == 663));
            Vector2 At(KitPlacement p) => new Vector2(MathF.Round(p.Position.X), MathF.Round(p.Position.Y));
            Assert.Contains(room, p => p.Type == 664 && At(p) == new Vector2(1152, 640));
            Assert.Contains(room, p => p.Type == 666 && At(p) == new Vector2(192, 0));
            Assert.Contains(room, p => p.Type == 666 && At(p) == new Vector2(1152, 448));
            Assert.Contains(room, p => p.Type == 663 && At(p) == new Vector2(192, 192));
            Assert.Contains(room, p => p.Type == 663 && At(p) == new Vector2(960, 448));
            // the game draws faces from the front only: walls (666 faces +y) and corners (664 faces -x and +y) face the
            // room's middle, as checked in the game (a corner turned otherwise left the corners open)
            Assert.Equal(new Vector2(0, 1), kit.Pieces[666].Facing);
            Assert.True(Vector2.Distance(Vector2.Normalize(new Vector2(-1, 1)), kit.Pieces[664].Facing) < 0.01f);
            var middle = new Vector2(576, 320);
            foreach (var p in room.Where(p => p.Type != 663))
            {
                var f = KitCatalog.ToMap(new Vector3(kit.Pieces[p.Type].Facing, 0), p.Yaw);
                var inward = middle - new Vector2(p.Position.X, p.Position.Y);
                Assert.True(Vector2.Dot(new Vector2(f.X, f.Y), inward) > 0, $"type {p.Type} at {At(p)} turned {p.Yaw} faces away");
            }
            // depth towards C: C on the other side makes the room go that way
            var other = KitBuilding.Room(kit, -1, new[] { 666 }, 664, new Vector2(0, 0), new Vector2(1100, 0), new Vector2(0, -700), 0);
            Assert.Contains(other, p => p.Type == 664 && At(p) == new Vector2(1152, -640));

            // a lone wall turned 30 degrees in an empty corner of a copy of Shame: a click near its end snaps there
            var map = ObjectMap.Read(File.ReadAllBytes(GamePaths.Find(root, "static/fixed.10")), ObjectFileKind.Fixed);
            byte[] before = map.Data.ToArray();
            var at = new Vector3(60000, 60000, 1000);
            KitBuilding.Apply(map, null, new[] { new KitPlacement(666, at, 30) });
            var end = at + KitCatalog.ToMap(kit.StraightStep(666, 666).Value.Offset, 30);
            var snap = KitBuilding.Snap(kit, kit.Pieces[666], map.Objects, end + new Vector3(20, -15, 0), 0, 128);
            Assert.NotNull(snap);
            Assert.True(Vector3.Distance(end, snap.Value.Placement.Position) < 2, $"{snap.Value.Placement.Position} vs {end}");
            Assert.Equal(30, snap.Value.Placement.Yaw, 0.5f);
            Assert.StartsWith("join", snap.Value.How);
            // far from any join but near the wall: its own turned grid (64 units)
            var free = KitBuilding.Snap(kit, kit.Pieces[664], map.Objects.Where(o => o.MapX == 60000), at + KitCatalog.ToMap(new Vector3(70, 300, 0), 30), 0, 40);
            Assert.NotNull(free);
            var local = KitCatalog.ToLocal(free.Value.Placement.Position - at, 30);
            Assert.Equal(64, local.X, 1f);
            Assert.Equal(320, local.Y, 1f);
            Assert.Contains("grid", free.Value.How);
            // the whole build is one edit: undone, the file is as before
            map = ObjectMap.Read(before, ObjectFileKind.Fixed);
            map.BeginEdit("build");
            KitBuilding.AddAll(map, null, wall.Select(p => p with { Position = p.Position + new Vector3(58000, 58000, 0) }));
            var e = map.EndEdit();
            map.Undo(e);
            Assert.Equal(before, map.Data);
        }

        [Fact]
        public void Places_kit_pieces_in_another_map_as_one_undo_step()
        {
            string root = Root();
            var kit = Kit(root, 10);
            // Shame's kit, built on Britannia (which has no type 666): the records come from Shame's file
            var g = GameFolder.TryOpen(root);
            var t = U9.Formats.Terrain.TerrainMap.Load(GamePaths.Find(root, "static/terrain.9"));
            var space = new MapSpace(t.Width, t.Height);
            var fx = ObjectMap.Load(GamePaths.Find(root, "static/fixed.9"), ObjectFileKind.Fixed);
            var nf = ObjectMap.Load(GamePaths.Find(root, "runtime/nonfixed.9"), ObjectFileKind.Nonfixed);
            var ed = new MapEditor(9, fx, nf, SceneObjects.Build(fx, nf, space, null), space, new TerrainRaster(t)) { Terrain = t };
            var shame = ObjectMap.Load(GamePaths.Find(root, "static/fixed.10"), ObjectFileKind.Fixed);
            byte[] before = fx.Data.ToArray();
            int count = fx.Objects.Count;
            var plan = KitBuilding.Wall(kit, new[] { 666 }, new[] { new Vector2(60000, 60000), new Vector2(61000, 60000) }, 2400);
            var made = ed.PlaceKit(plan, shame);
            Assert.Equal(plan.Count, made.Count);
            Assert.Equal(count + plan.Count, fx.Objects.Count);
            Assert.All(made, o => Assert.Equal(666, o.Source.Type));
            Assert.Equal(plan[1].MapX, made[1].Source.MapX);
            Assert.Equal(0, MapEditor.Yaw(made[0].Source.Rotation), 0.5f);
            Assert.True(ed.FixedChanged);
            // one step: undone, the file is as it was; a temporary step forgotten for redo does not come back
            Assert.NotNull(ed.Undo());
            Assert.Equal(before, fx.Data);
            ed.ForgetRedo();
            Assert.Null(ed.Redo());
            Assert.False(ed.FixedChanged);
        }

        [Fact]
        public void Draws_pictures_of_pieces()
        {
            string root = Root();
            var kit = Kit(root, 10);
            using var sap = FlxArchive.Open(GamePaths.Find(root, "static/sappear.flx"));
            using var bmp = FlxArchive.Open(GamePaths.Find(root, "static/bitmap16.flx"));
            var px = ModelThumbnail.Render(sap.Read(kit.Pieces[666].Model), b => bmp.Read(b), 48);
            Assert.NotNull(px);
            Assert.Equal(48 * 48 * 4, px.Length);
            int covered = 0, coloured = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] > 0) covered++;
                if (px[i + 3] > 0 && px[i] + px[i + 1] + px[i + 2] > 30) coloured++;
            }
            Assert.InRange(covered, 48 * 48 / 5, 48 * 48);        // the wall fills a good part of the picture
            Assert.True(coloured > covered / 2);                   // textured and lit, not black
            Assert.Null(ModelThumbnail.Render(new byte[] { 169, 0, 0, 0, 0, 0, 0, 0 }, null, 48));
        }

        [Fact]
        public void Frames_and_local_grids_round_trip()
        {
            var d = new Vector3(100, -40, 7);
            foreach (float yaw in new[] { 0f, 35f, -10f, 90f, 187f })
                Assert.True(Vector3.Distance(d, KitCatalog.ToMap(KitCatalog.ToLocal(d, yaw), yaw)) < 1e-3f);
            // a piece's own x axis: (256, 0) in a piece turned 90 degrees is (0, 256) on the map
            var m = KitCatalog.ToMap(new Vector3(256, 0, 0), 90);
            Assert.True(Math.Abs(m.X) < 1e-3f && Math.Abs(m.Y - 256) < 1e-3f);

            var g = new KitGrid(-10, new Vector2(1000, 2000), 64);
            var off = KitCatalog.ToMap(new Vector3(3 * 64 + 9, -2 * 64 - 20, 0), -10);
            var snapped = g.Snap(new Vector2(1000 + off.X, 2000 + off.Y));
            var back = KitCatalog.ToLocal(new Vector3(snapped - g.Origin, 0), -10);
            Assert.Equal(192, back.X, 1f);
            Assert.Equal(-128, back.Y, 1f);
            Assert.Equal(80, g.SnapYaw(84));
            Assert.Equal(-10, g.SnapYaw(-3));
        }
    }
}
