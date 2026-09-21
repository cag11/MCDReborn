using MCDSaveEdit.Logic;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// A mission you can walk around, rather than a shape you have to take on trust.
    ///
    /// The flat plan was accurate and unusable. You could see that Creeper Woods had corridors
    /// somewhere in it, and nothing about which corridor was the one on the bridge, or how far
    /// down the ravine went, or whether the thing you were about to drop four skeletons into was
    /// a courtyard or a roof. A spawn point is placed in three dimensions and was being chosen in
    /// two.
    ///
    /// This is stock WPF 3D, deliberately. Everything the view needs - drag to turn, wheel to
    /// come closer, click to say where - is a few hundred lines against a Viewport3D that the app
    /// already ships and that costs nothing to publish. The two things Viewport3D genuinely
    /// cannot do are worked around rather than paid for: it has no per-vertex colour, so the
    /// mission's colours live in one texture and every quad carries a window onto it; and its own
    /// hit testing walks every triangle in software, so clicks are answered by marching the
    /// height field instead, which is both faster and gives back a block rather than a triangle.
    /// </summary>
    public class MapView3D : Grid
    {
        private readonly Viewport3D _viewport = new Viewport3D();
        private readonly PerspectiveCamera _camera;
        private readonly ModelVisual3D _scene = new ModelVisual3D();
        private readonly ModelVisual3D _lights = new ModelVisual3D();

        private MapRelief.Relief? _relief;
        private GeometryModel3D? _ground;
        private GeometryModel3D? _markers;
        private GeometryModel3D? _ways;

        //A group rather than one model: the entry door is the same pink at a different strength,
        //and two materials cannot share one mesh.
        private Model3DGroup? _doorGroup;

        private Model3DGroup? _startGroup;
        private GeometryModel3D? _exits;
        private GeometryModel3D? _gates;
        private GeometryModel3D? _steps;
        private GeometryModel3D? _wires;
        private GeometryModel3D? _cursor;

        //Where the camera is looking and from how far. Yaw and pitch are degrees because every
        //number a person might want to reason about here is in degrees.
        private Point3D _target = new Point3D(0, 0, 0);
        private double _yaw = 35;
        private double _pitch = 48;
        private double _distance = 400;

        //Which movement keys are down. Held rather than acted on as they arrive, because key
        //repeat is a stutter set by the keyboard rather than a speed, and holding W should glide.
        private readonly HashSet<Key> _held = new HashSet<Key>();
        private readonly System.Windows.Threading.DispatcherTimer _walk;

        private Point _dragFrom;
        private bool _turning;
        private bool _panning;
        private bool _moved;

        //Where the pins are, so a press can tell "take hold of that one" from "turn the camera".
        //The view is handed them by mark(), markDoors() and markStarts() anyway, so hit testing
        //them here costs nothing and keeps the two answers from drifting apart.
        private readonly List<(int x, int y, int z)> _marks = new List<(int x, int y, int z)>();
        private readonly List<(int x, int y, int z)> _doorPins = new List<(int x, int y, int z)>();
        private readonly List<(int x, int y, int z)> _startPins = new List<(int x, int y, int z)>();
        private readonly List<(int x, int y, int z)> _exitPins = new List<(int x, int y, int z)>();
        private readonly List<(int x, int y, int z)> _gatePins = new List<(int x, int y, int z)>();
        private readonly List<(int x, int y, int z)> _stepPins = new List<(int x, int y, int z)>();

        /// <summary>The kinds of thing standing on the map that can be taken hold of.</summary>
        public enum Pin { Spawn, Door, Start, Exit, Gate, Step }

        private bool _dragging;
        private Pin _dragKind;
        private (int x, int y, int z) _dragAt;

        /// <summary>
        /// How close a press has to land to take hold of a point, in blocks.
        ///
        /// Deliberately tighter than the eight blocks a click uses to SELECT one. Selecting the
        /// wrong point is a glance at the status line; dragging the wrong one moves somebody's
        /// work, and a camera that grabs a spawn point every time you orbit near one would be
        /// worse than having no dragging at all.
        /// </summary>
        private const double GRAB = 3.0;

        /// <summary>Where a click landed, in the room's own block coordinates.</summary>
        public event Action<int, int, int>? Picked;

        /// <summary>What the pointer is over, for a readout that says where you are.</summary>
        public event Action<int, int, int>? Hovered;

        /// <summary>
        /// A double click: put them here, now.
        ///
        /// Aiming and then reaching for a button is right when the radius and the count matter,
        /// and it is three motions when they do not. Somebody dropping twenty spawn points along
        /// a corridor should be able to just keep clicking.
        /// </summary>
        public event Action<int, int, int>? Confirmed;

        /// <summary>
        /// A pin has been taken hold of: which kind, and where that pin actually is.
        ///
        /// The position is the PIN's, not the floor cell the ray hit - those are up to GRAB
        /// blocks apart, which is the whole point of GRAB.
        /// </summary>
        public event Action<Pin, int, int, int>? Grabbed;

        /// <summary>A held pin has been dragged over a new block. Fires as it travels.</summary>
        public event Action<int, int, int>? Dragged;

        /// <summary>The button came up and the point is where it was left.</summary>
        public event Action<int, int, int>? Dropped;

        /// <summary>Escape while dragging: put it back where it started.</summary>
        public event Action? DragCancelled;

        public MapView3D()
        {
            ClipToBounds = true;
            Background = Brushes.Transparent;
            Focusable = true;

            _camera = new PerspectiveCamera
            {
                FieldOfView = 55,
                NearPlaneDistance = 0.5,
                FarPlaneDistance = 20000,
            };

            _viewport.Camera = _camera;
            _viewport.Children.Add(_lights);
            _viewport.Children.Add(_scene);

            //Mostly ambient. The texture already carries the lie of the land baked into it, and a
            //strong directional light on top of that shades the same hill twice.
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(178, 178, 178)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(120, 120, 120),
                new Vector3D(-0.4, -1, -0.55)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(52, 52, 68),
                new Vector3D(0.6, 0.35, 0.7)));
            _lights.Content = lights;

            Children.Add(_viewport);

            _walk = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _walk.Tick += (_, _) => step();

            MouseDown += onDown;
            MouseUp += onUp;
            MouseMove += onMove;
            MouseWheel += onWheel;
            MouseLeave += (_, _) => { _turning = false; _panning = false; };
            KeyDown += onKey;
            KeyUp += onKeyUp;

            //A key held while the view loses focus would otherwise stay held forever, and the map
            //would drift away on its own.
            LostKeyboardFocus += (_, _) => { _held.Clear(); _walk.Stop(); };
        }

        /// <summary>Holds a key and runs one frame, so a probe can check the camera moves.</summary>
        internal void probeWalk(Key key, int frames = 10)
        {
            _held.Add(key);
            for (var i = 0; i < frames; i++) { step(); }
            _held.Remove(key);
        }

        /// <summary>Where the camera is, as one value a probe can compare.</summary>
        internal (double yaw, double pitch, double distance, double x, double z) cameraNow
            => (_yaw, _pitch, _distance, _target.X, _target.Z);

        /// <summary>Is there anything to look at?</summary>
        public bool Ready => _relief != null;

        /// <summary>The ground under a column, for whoever wants to know how high to place.</summary>
        public int heightAt(int x, int z) => _relief?.heightAt(x, z) ?? 0;

        /// <summary>Whether the game will let something stand on a column.</summary>
        public bool walkableAt(int x, int z) => _relief?.walkableAt(x, z) ?? true;

        /// <summary>
        /// Hands the view a room to draw.
        ///
        /// The relief is built elsewhere and on another thread: it decompresses the whole block
        /// array to find out what the top of every column is made of, and doing that on the UI
        /// thread freezes the window for as long as it takes.
        /// </summary>
        public void show(MapRelief.Relief relief, bool keepCamera = false)
        {
            _relief = relief;

            var group = new Model3DGroup();

            var mesh = new MeshGeometry3D
            {
                Positions = points(relief.Points),
                TextureCoordinates = texels(relief.Uvs),
                TriangleIndices = new Int32Collection(relief.Indices),
            };
            mesh.Freeze();

            var brush = new ImageBrush(picture(relief))
            {
                //Stretch is what maps texture coordinates onto the image. Anything else and the
                //whole map slides.
                Stretch = Stretch.Fill,
            };
            brush.Freeze();

            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(brush));
            material.Freeze();

            //Both sides. Looking up at a floor from inside a ravine should show the floor, not
            //the inside of the world.
            _ground = new GeometryModel3D(mesh, material) { BackMaterial = material };
            group.Children.Add(_ground);

            if (_ways != null) { group.Children.Add(_ways); }
            if (_doorGroup != null) { group.Children.Add(_doorGroup); }
            if (_startGroup != null) { group.Children.Add(_startGroup); }
            if (_exits != null) { group.Children.Add(_exits); }
            if (_gates != null) { group.Children.Add(_gates); }
            if (_steps != null) { group.Children.Add(_steps); }
            if (_wires != null) { group.Children.Add(_wires); }
            if (_markers != null) { group.Children.Add(_markers); }
            if (_cursor != null) { group.Children.Add(_cursor); }

            _scene.Content = group;

            if (!keepCamera) { frame(); }
            else { place(); }
        }

        /// <summary>Puts the whole room in view, looking down at it from the south-east.</summary>
        public void frame()
        {
            if (_relief == null) { return; }

            _target = new Point3D(_relief.Sx / 2.0, _relief.Lowest, _relief.Sz / 2.0);
            _yaw = 35;
            _pitch = 48;

            //Far enough back that the long side fits across the view, with a little air.
            var across = Math.Max(_relief.Sx, _relief.Sz);
            _distance = across / (2 * Math.Tan(_camera.FieldOfView * Math.PI / 360)) * 1.25;

            place();
        }

        /// <summary>Looking straight down, which is how the game itself frames a mission.</summary>
        public void overhead()
        {
            _pitch = 89;
            _yaw = 0;
            place();
        }

        private void place()
        {
            var pitch = _pitch * Math.PI / 180;
            var yaw = _yaw * Math.PI / 180;

            var away = new Vector3D(
                Math.Cos(pitch) * Math.Sin(yaw),
                Math.Sin(pitch),
                Math.Cos(pitch) * Math.Cos(yaw));

            _camera.Position = _target + away * _distance;
            _camera.LookDirection = -away;

            //Straight down has no natural up, so it is chosen rather than computed - otherwise the
            //view flips over at the exact moment somebody tilts past vertical.
            _camera.UpDirection = _pitch > 89.5
                ? new Vector3D(Math.Sin(yaw), 0, Math.Cos(yaw))
                : new Vector3D(0, 1, 0);
        }

        private static Point3DCollection points(List<double> from)
        {
            var made = new Point3DCollection(from.Count / 3);
            for (var i = 0; i < from.Count; i += 3)
            {
                made.Add(new Point3D(from[i], from[i + 1], from[i + 2]));
            }
            made.Freeze();
            return made;
        }

        private static PointCollection texels(List<double> from)
        {
            var made = new PointCollection(from.Count / 2);
            for (var i = 0; i < from.Count; i += 2)
            {
                made.Add(new Point(from[i], from[i + 1]));
            }
            made.Freeze();
            return made;
        }

        /// <summary>
        /// The map as a texture, blown up before WPF can soften it.
        ///
        /// A 3D material does not honour BitmapScalingMode the way an Image does - the picture is
        /// resampled on its way onto the geometry, so one pixel per block arrives as a smear and
        /// the whole mission looks out of focus. Enlarging by a whole number first, nearest
        /// neighbour, leaves the smoothing nothing to smear: each block becomes a block of
        /// identical pixels and the edges survive.
        ///
        /// The factor is whatever fits under four thousand pixels, which is about where the cost
        /// of the bitmap stops being worth the sharpness.
        /// </summary>
        private static BitmapSource picture(MapRelief.Relief relief)
        {
            var sx = relief.Sx;
            var sz = relief.TextureHeight;

            var blow = Math.Max(1, Math.Min(6, 4000 / Math.Max(sx, sz)));

            var wide = sx * blow;
            var tall = sz * blow;
            var stride = wide * 4;
            var pixels = new byte[stride * tall];

            for (var z = 0; z < sz; z++)
            {
                //One source row painted once, then copied to the rest of the rows it covers.
                var row = z * blow * stride;

                for (var x = 0; x < sx; x++)
                {
                    var colour = relief.Texture[x + z * sx];
                    var b = (byte)(colour & 0xFF);
                    var g = (byte)((colour >> 8) & 0xFF);
                    var r = (byte)((colour >> 16) & 0xFF);
                    var a = (byte)((colour >> 24) & 0xFF);

                    for (var dx = 0; dx < blow; dx++)
                    {
                        var at = row + (x * blow + dx) * 4;
                        pixels[at + 0] = b;
                        pixels[at + 1] = g;
                        pixels[at + 2] = r;
                        pixels[at + 3] = a;
                    }
                }

                for (var dz = 1; dz < blow; dz++)
                {
                    Buffer.BlockCopy(pixels, row, pixels, row + dz * stride, stride);
                }
            }

            var made = BitmapSource.Create(wide, tall, 96, 96,
                PixelFormats.Bgra32, null, pixels, stride);
            made.Freeze();
            return made;
        }

        /// <summary>
        /// Where the spawn points are.
        ///
        /// Drawn as little pillars standing on the ground rather than dots lying on it, because a
        /// dot painted onto a hillside is invisible the moment the hillside turns away from you.
        /// </summary>
        public void mark(IEnumerable<(int x, int y, int z)> spawns, Color colour)
        {
            var mesh = new MeshGeometry3D();
            var count = 0;

            _marks.Clear();

            foreach (var one in spawns)
            {
                pillar(mesh, one.x + 0.5, one.y, one.z + 0.5, 0.9, 4.0);
                _marks.Add(one);
                count++;
            }

            if (count == 0)
            {
                _markers = null;
                redraw();
                return;
            }

            mesh.Freeze();

            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(colour)));
            //A marker has to stay readable against a dark forest floor and a bright stone path
            //alike, so it carries its own light rather than taking the scene's.
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                Color.FromRgb((byte)(colour.R / 2), (byte)(colour.G / 2), (byte)(colour.B / 2)))));
            material.Freeze();

            _markers = new GeometryModel3D(mesh, material) { BackMaterial = material };
            redraw();
        }

        /// <summary>
        /// The ways in and out of the room.
        ///
        /// Drawn taller and thinner than a spawn point, and in their own colour, because they are
        /// a different kind of thing entirely - a spawn point is where mobs appear, a teleport is
        /// a door to another place. The ones that take you somewhere are drawn taller again than
        /// the ones you arrive at, since a room usually has both and they are easy to confuse.
        /// </summary>
        public void markWays(IEnumerable<(int x, int y, int z, bool leaves)> ways)
        {
            var mesh = new MeshGeometry3D();
            var count = 0;

            foreach (var one in ways)
            {
                if (one.x < 0) { continue; }
                pillar(mesh, one.x + 0.5, one.y, one.z + 0.5,
                    one.leaves ? 1.6 : 1.2, one.leaves ? 26.0 : 18.0);
                count++;
            }

            if (count == 0)
            {
                _ways = null;
                redraw();
                return;
            }

            mesh.Freeze();

            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(
                Color.FromRgb(120, 230, 255))));
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                Color.FromRgb(40, 130, 170))));
            material.Freeze();

            _ways = new GeometryModel3D(mesh, material) { BackMaterial = material };
            redraw();
        }

        /// <summary>
        /// Lines between things that are wired together.
        ///
        /// A gate and the step that opens it are the same relationship a node graph draws with a
        /// wire, and it has the same problem: the two ends are usually nowhere near each other,
        /// and a list cannot show you that the gate at one end of the map is held by the villager
        /// at the other. So it is drawn.
        ///
        /// Thin square beams rather than lines, because WPF's 3D has no line primitive - a line
        /// has no thickness and so no triangles. A beam four hundred blocks long and a third of a
        /// block across reads as a wire from any distance that matters.
        /// </summary>
        public void wire(IEnumerable<(int ax, int ay, int az, int bx, int by, int bz)> pairs)
        {
            var mesh = new MeshGeometry3D();
            var count = 0;

            foreach (var one in pairs)
            {
                beam(mesh,
                    one.ax + 0.5, one.ay + 9.0, one.az + 0.5,
                    one.bx + 0.5, one.by + 9.0, one.bz + 0.5,
                    0.55);
                count++;
            }

            if (count == 0)
            {
                _wires = null;
                redraw();
                return;
            }

            mesh.Freeze();

            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(
                Color.FromRgb(235, 235, 120))));
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                Color.FromRgb(140, 140, 60))));
            material.Freeze();

            _wires = new GeometryModel3D(mesh, material) { BackMaterial = material };
            redraw();
        }

        /// <summary>
        /// A square beam from one point to another.
        ///
        /// Built by finding any two directions across the line and walking a square along it, so
        /// it works for a wire going straight up as readily as one along the ground - which the
        /// obvious "cross with up" version does not.
        /// </summary>
        private static void beam(MeshGeometry3D mesh, double ax, double ay, double az,
                                 double bx, double by, double bz, double thick)
        {
            var along = new Vector3D(bx - ax, by - ay, bz - az);
            if (along.Length < 1e-6) { return; }
            along.Normalize();

            //Any vector not parallel to the beam will do to start the cross products off.
            var other = Math.Abs(along.Y) > 0.9
                ? new Vector3D(1, 0, 0)
                : new Vector3D(0, 1, 0);

            var side = Vector3D.CrossProduct(along, other);
            side.Normalize();
            var up = Vector3D.CrossProduct(along, side);

            var at = mesh.Positions.Count;

            foreach (var end in new[] { (ax, ay, az), (bx, by, bz) })
            {
                foreach (var corner in new[] { (1, 1), (1, -1), (-1, -1), (-1, 1) })
                {
                    var offset = side * (corner.Item1 * thick) + up * (corner.Item2 * thick);
                    mesh.Positions.Add(new Point3D(
                        end.Item1 + offset.X, end.Item2 + offset.Y, end.Item3 + offset.Z));
                }
            }

            //Four sides, two triangles each. The ends are left open - nothing ever sees them.
            for (var face = 0; face < 4; face++)
            {
                var next = (face + 1) % 4;

                foreach (var index in new[] { face, face + 4, next + 4, face, next + 4, next })
                {
                    mesh.TriangleIndices.Add(at + index);
                }
            }
        }

        /// <summary>
        /// The gates an objective holds shut.
        ///
        /// Purple, and drawn as a row of posts along the gate's own width rather than as one
        /// marker at its corner - a gate is a wall five or nine cells long, and a single spike at
        /// one end says nothing about which way it lies or what it blocks. Getting that wrong
        /// leaves a gate lying along the corridor instead of across it, which looks fine on the
        /// map and lets everybody walk straight past in game.
        /// </summary>
        public void markGates(IEnumerable<(int x, int y, int z, int sx, int sz)> gates)
        {
            var mesh = new MeshGeometry3D();
            var count = 0;

            _gatePins.Clear();

            foreach (var one in gates)
            {
                //The anchor, which is what a drag moves and what the list reports.
                _gatePins.Add((one.x, one.y, one.z));

                var along = Math.Max(1, Math.Max(one.sx, one.sz));

                for (var step = 0; step < along; step++)
                {
                    var x = one.x + (one.sx >= one.sz ? step : 0);
                    var z = one.z + (one.sx >= one.sz ? 0 : step);

                    //The first post is taller, so which end the gate is anchored at is visible -
                    //that is the cell its position names and the one a drag moves.
                    pillar(mesh, x + 0.5, one.y, z + 0.5,
                        step == 0 ? 2.0 : 1.3, step == 0 ? 18.0 : 12.0);
                }

                count++;
            }

            if (count == 0)
            {
                _gates = null;
                redraw();
                return;
            }

            mesh.Freeze();

            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(
                Color.FromRgb(178, 120, 255))));
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                Color.FromRgb(88, 40, 150))));
            material.Freeze();

            _gates = new GeometryModel3D(mesh, material) { BackMaterial = material };
            redraw();
        }

        /// <summary>
        /// What the mission asks of you, other than leaving.
        ///
        /// Amber, because the five colours already on the map were taken. A step you have to
        /// click stands taller and thinner than one you only have to walk into - the first is a
        /// thing in the world and the second is a patch of floor, and at map scale the shape is
        /// the only part of that anybody can see.
        /// </summary>
        public void markSteps(IEnumerable<(int x, int y, int z, bool click)> steps)
        {
            var mesh = new MeshGeometry3D();
            var count = 0;

            _stepPins.Clear();

            foreach (var one in steps)
            {
                //A step whose region is not in this room has nowhere to stand. It is still
                //listed, and still worth shouting about, but there is nothing to draw.
                if (one.x < 0) { continue; }

                _stepPins.Add((one.x, one.y, one.z));

                pillar(mesh, one.x + 0.5, one.y, one.z + 0.5,
                    one.click ? 1.8 : 3.4, one.click ? 22.0 : 10.0);

                count++;
            }

            if (count == 0)
            {
                _steps = null;
                redraw();
                return;
            }

            mesh.Freeze();

            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(
                Color.FromRgb(255, 176, 46))));
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                Color.FromRgb(150, 96, 12))));
            material.Freeze();

            _steps = new GeometryModel3D(mesh, material) { BackMaterial = material };
            redraw();
        }

        /// <summary>
        /// The way out - the glowing gate that finishes the mission.
        ///
        /// Red, and nothing else on the map is red. There are five kinds of marker standing on
        /// one map by now and the only one that reliably tells them apart at a glance is hue, so
        /// each gets its own rather than a shade of somebody else's.
        /// </summary>
        public void markExits(IEnumerable<(int x, int y, int z)> exits)
        {
            var mesh = new MeshGeometry3D();
            var count = 0;

            _exitPins.Clear();

            foreach (var one in exits)
            {
                pillar(mesh, one.x + 0.5, one.y, one.z + 0.5, 2.8, 24.0);
                _exitPins.Add((one.x, one.y, one.z));
                count++;
            }

            if (count == 0)
            {
                _exits = null;
                redraw();
                return;
            }

            mesh.Freeze();

            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(
                Color.FromRgb(255, 72, 72))));
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                Color.FromRgb(150, 25, 25))));
            material.Freeze();

            _exits = new GeometryModel3D(mesh, material) { BackMaterial = material };
            redraw();
        }

        /// <summary>
        /// Where the mission puts you when it starts.
        ///
        /// Green, and the broadest marker of the lot, because it is an area rather than a point -
        /// the game's own are three to six cells across and you materialise somewhere inside.
        ///
        /// It gets its own colour because it is not any of the other three. A door is how tiles
        /// join, a teleport is a way to another dungeon, a spawn point is where mobs appear, and
        /// none of them is where YOU arrive - which is the single thing a hand-built mission most
        /// needs and the easiest to leave out, because nothing about the map looks wrong without it.
        /// </summary>
        public void markStarts(IEnumerable<(int x, int y, int z, bool main)> starts)
        {
            var mesh = new MeshGeometry3D();
            var bright = new MeshGeometry3D();
            var count = 0;
            var mains = 0;

            _startPins.Clear();

            foreach (var one in starts)
            {
                //The main way in stands taller. A mission can have several arrival areas and only
                //one of them is where the mission BEGINS - the rest are where teleports drop you -
                //and nothing about the regions themselves tells them apart.
                if (one.main)
                {
                    pillar(bright, one.x + 0.5, one.y, one.z + 0.5, 3.4, 26.0);
                    mains++;
                }
                else
                {
                    pillar(mesh, one.x + 0.5, one.y, one.z + 0.5, 2.6, 15.0);
                }

                _startPins.Add((one.x, one.y, one.z));
                count++;
            }

            if (count == 0)
            {
                _startGroup = null;
                redraw();
                return;
            }

            var group = new Model3DGroup();

            if (count > mains)
            {
                mesh.Freeze();
                group.Children.Add(new GeometryModel3D(mesh, arrival(false))
                {
                    BackMaterial = arrival(false),
                });
            }

            if (mains > 0)
            {
                bright.Freeze();
                group.Children.Add(new GeometryModel3D(bright, arrival(true))
                {
                    BackMaterial = arrival(true),
                });
            }

            _startGroup = group;
            redraw();
        }

        /// <summary>
        /// The arrival markers: yellow for the way the mission begins, green for the rest.
        ///
        /// A different HUE rather than a brighter green, because a shade is only legible next to
        /// the thing it is a shade of - and these two are usually at opposite ends of a mission a
        /// thousand blocks long, never in the same view. Yellow has to be read on its own.
        ///
        /// It survives the company it keeps: the spawn points are orange but a twentieth the
        /// size, the doors are pink, the teleports pale blue. Nothing else on the map is yellow.
        /// </summary>
        private static Material arrival(bool main)
        {
            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(
                main ? Color.FromRgb(255, 226, 64) : Color.FromRgb(80, 195, 105))));
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                main ? Color.FromRgb(160, 130, 18) : Color.FromRgb(26, 92, 40))));
            material.Freeze();
            return material;
        }

        /// <summary>
        /// The doors in the room's wall.
        ///
        /// Pink, and a different shape again: spawn points are short orange spikes, teleports are
        /// tall blue ones, and a door is a broad flat slab standing in the wall. Three kinds of
        /// thing in one view need to be told apart at a glance and from any angle, and colour
        /// alone stops working the moment two of them are behind each other.
        ///
        /// The one the level starts you at is drawn taller and brighter. It is the single most
        /// consequential thing on the map - a mission with no way in crashes on the loading
        /// screen - so it should not take a click to find out which one it is.
        /// </summary>
        public void markDoors(IEnumerable<(int x, int y, int z, bool entry)> doors)
        {
            var mesh = new MeshGeometry3D();
            var bright = new MeshGeometry3D();
            var count = 0;
            var entries = 0;

            _doorPins.Clear();

            foreach (var one in doors)
            {
                _doorPins.Add((one.x, one.y, one.z));
                if (one.entry)
                {
                    pillar(bright, one.x + 0.5, one.y, one.z + 0.5, 2.2, 22.0);
                    entries++;
                }
                else
                {
                    pillar(mesh, one.x + 0.5, one.y, one.z + 0.5, 1.8, 14.0);
                }
                count++;
            }

            if (count == 0)
            {
                _doorGroup = null;
                redraw();
                return;
            }

            var group = new Model3DGroup();

            if (count > entries)
            {
                mesh.Freeze();
                group.Children.Add(new GeometryModel3D(mesh, pink(false))
                {
                    BackMaterial = pink(false),
                });
            }

            if (entries > 0)
            {
                bright.Freeze();
                group.Children.Add(new GeometryModel3D(bright, pink(true))
                {
                    BackMaterial = pink(true),
                });
            }

            _doorGroup = group;
            redraw();
        }

        private static Material pink(bool entry)
        {
            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(
                entry ? Color.FromRgb(255, 120, 220) : Color.FromRgb(225, 90, 185))));
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                entry ? Color.FromRgb(150, 40, 120) : Color.FromRgb(90, 25, 70))));
            material.Freeze();
            return material;
        }

        /// <summary>
        /// The spot a click chose, so it is obvious what is about to happen.
        ///
        /// <paramref name="onExisting"/> when the click landed on a spawn point that is already
        /// there rather than on bare ground - a different colour, because one means "add here"
        /// and the other means "this one", and confusing them deletes the wrong thing.
        /// </summary>
        public void aim(int x, int y, int z, bool onExisting = false)
        {
            var mesh = new MeshGeometry3D();
            pillar(mesh, x + 0.5, y, z + 0.5, onExisting ? 2.0 : 1.4, onExisting ? 13.0 : 10.0);
            mesh.Freeze();

            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(
                onExisting ? Brushes.Gold : Brushes.White));
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                onExisting ? Color.FromRgb(200, 150, 30) : Color.FromRgb(120, 200, 255))));
            material.Freeze();

            _cursor = new GeometryModel3D(mesh, material) { BackMaterial = material };
            redraw();
        }

        private void redraw()
        {
            if (_scene.Content is not Model3DGroup group) { return; }

            var made = new Model3DGroup();
            if (_ground != null) { made.Children.Add(_ground); }
            if (_ways != null) { made.Children.Add(_ways); }
            if (_doorGroup != null) { made.Children.Add(_doorGroup); }
            if (_startGroup != null) { made.Children.Add(_startGroup); }
            if (_exits != null) { made.Children.Add(_exits); }
            if (_gates != null) { made.Children.Add(_gates); }
            if (_steps != null) { made.Children.Add(_steps); }
            if (_wires != null) { made.Children.Add(_wires); }
            if (_markers != null) { made.Children.Add(_markers); }
            if (_cursor != null) { made.Children.Add(_cursor); }
            _scene.Content = made;
        }

        private static void pillar(MeshGeometry3D mesh, double x, double y, double z,
                                   double wide, double tall)
        {
            var first = mesh.Positions.Count;
            var half = wide / 2;

            //A four-sided spike: cheap, and unlike a cube it reads as a marker rather than as
            //something somebody built out of blocks.
            mesh.Positions.Add(new Point3D(x, y + tall, z));
            mesh.Positions.Add(new Point3D(x - half, y, z - half));
            mesh.Positions.Add(new Point3D(x + half, y, z - half));
            mesh.Positions.Add(new Point3D(x + half, y, z + half));
            mesh.Positions.Add(new Point3D(x - half, y, z + half));

            for (var side = 0; side < 4; side++)
            {
                mesh.TriangleIndices.Add(first);
                mesh.TriangleIndices.Add(first + 1 + side);
                mesh.TriangleIndices.Add(first + 1 + (side + 1) % 4);
            }
        }

        private void onDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            _dragFrom = e.GetPosition(this);
            _moved = false;

            if (e.ChangedButton == MouseButton.Left
                && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                //A press that lands on a spawn point takes hold of it. Everything else turns the
                //camera, which is what the whole surface did before and still does.
                if (!grab(_dragFrom)) { _turning = true; }
            }
            else
            {
                _panning = true;
            }

            CaptureMouse();
        }

        private void onUp(object sender, MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();

            if (_dragging) { drop(); return; }

            var wasTurning = _turning;
            _turning = false;
            _panning = false;

            //A drag that turned the camera is not a click. Placing a spawn point every time
            //somebody looks around would be unusable.
            if (_moved || !wasTurning || e.ChangedButton != MouseButton.Left) { return; }

            var hit = look(e.GetPosition(this));
            if (hit == null) { return; }

            //No marker drawn here. Whoever listens decides whether this is bare ground or an
            //existing point and calls aim itself, so the colour is right the first time rather
            //than flickering white and then gold.
            Picked?.Invoke(hit.Value.x, hit.Value.y, hit.Value.z);

            if (e.ClickCount >= 2)
            {
                Confirmed?.Invoke(hit.Value.x, hit.Value.y, hit.Value.z);
            }
        }

        private void onMove(object sender, MouseEventArgs e)
        {
            var now = e.GetPosition(this);

            if (_dragging) { dragTo(now); return; }

            if (!_turning && !_panning)
            {
                var over = look(now);
                if (over != null) { Hovered?.Invoke(over.Value.x, over.Value.y, over.Value.z); }
                return;
            }

            var byX = now.X - _dragFrom.X;
            var byY = now.Y - _dragFrom.Y;
            if (Math.Abs(byX) > 2 || Math.Abs(byY) > 2) { _moved = true; }

            if (_turning)
            {
                _yaw -= byX * 0.4;
                _pitch = Math.Max(2, Math.Min(90, _pitch + byY * 0.3));
            }
            else
            {
                //Panning moves the point being looked at, across the ground rather than across
                //the screen, so dragging feels like sliding the map about.
                var yaw = _yaw * Math.PI / 180;
                var speed = _distance * 0.0016;

                var right = new Vector3D(Math.Cos(yaw), 0, -Math.Sin(yaw));
                var into = new Vector3D(Math.Sin(yaw), 0, Math.Cos(yaw));

                _target -= right * (byX * speed);
                _target -= into * (byY * speed);
            }

            _dragFrom = now;
            place();
        }

        private void onWheel(object sender, MouseWheelEventArgs e)
        {
            //Geometric, so one notch feels the same close up and far away.
            _distance *= Math.Pow(0.88, e.Delta / 120.0);
            _distance = Math.Max(6, Math.Min(12000, _distance));
            place();
            e.Handled = true;
        }

        //Laid out under the left hand, the way every game that moves a camera does it. Q and E
        //go down and up, because a mission is a building as much as a floor.
        private static bool moves(Key key) =>
            key == Key.W || key == Key.A || key == Key.S || key == Key.D
            || key == Key.Q || key == Key.E
            || key == Key.Up || key == Key.Down || key == Key.Left || key == Key.Right;

        private void onKey(object sender, KeyEventArgs e)
        {
            //Escape lets go of a point mid-drag. Dragging is the one gesture here that changes
            //the map while it is still happening, so it is the one that needs a way out that is
            //not "undo it afterwards and hope".
            if (e.Key == Key.Escape && _dragging)
            {
                _dragging = false;
                ReleaseMouseCapture();
                DragCancelled?.Invoke();
                e.Handled = true;
                return;
            }

            //F was framing before there was anything to hold down; it stays, but not as a letter
            //next to the movement keys - R is the reframe now and F is left alone for anyone with
            //the habit.
            if (e.Key == Key.R || e.Key == Key.F) { frame(); e.Handled = true; }
            if (e.Key == Key.T) { overhead(); e.Handled = true; }

            if (!moves(e.Key)) { return; }

            _held.Add(e.Key);
            if (!_walk.IsEnabled) { _walk.Start(); }
            e.Handled = true;
        }

        private void onKeyUp(object sender, KeyEventArgs e)
        {
            if (!_held.Remove(e.Key)) { return; }
            if (_held.Count == 0) { _walk.Stop(); }
            e.Handled = true;
        }

        /// <summary>
        /// One frame of holding a movement key.
        ///
        /// Moves the point being looked at rather than the camera, which is what right-dragging
        /// does too - so the two feel like the same gesture and end up in the same place. Speed
        /// comes from how far out you are, because a step that reads well across a whole mission
        /// is a leap when you are down among the trees.
        /// </summary>
        private void step()
        {
            if (_held.Count == 0) { _walk.Stop(); return; }

            var speed = Math.Max(0.35, _distance * 0.012);
            if (Keyboard.Modifiers == ModifierKeys.Shift) { speed *= 3; }

            var yaw = _yaw * Math.PI / 180;

            //Matches the panning drag: away from the camera is +into, so forward is the other way.
            var into = new Vector3D(Math.Sin(yaw), 0, Math.Cos(yaw));
            var right = new Vector3D(Math.Cos(yaw), 0, -Math.Sin(yaw));

            var moved = new Vector3D(0, 0, 0);

            if (_held.Contains(Key.W) || _held.Contains(Key.Up)) { moved -= into; }
            if (_held.Contains(Key.S) || _held.Contains(Key.Down)) { moved += into; }
            if (_held.Contains(Key.A) || _held.Contains(Key.Left)) { moved -= right; }
            if (_held.Contains(Key.D) || _held.Contains(Key.Right)) { moved += right; }
            if (_held.Contains(Key.E)) { moved += new Vector3D(0, 1, 0); }
            if (_held.Contains(Key.Q)) { moved -= new Vector3D(0, 1, 0); }

            if (moved.Length < 1e-9) { return; }

            //Normalised so W and D together is not faster than either alone.
            moved.Normalize();
            _target += moved * speed;
            place();
        }

        /// <summary>
        /// What is under a point on screen.
        ///
        /// The ray is built by hand rather than asked of WPF, because WPF's own hit test walks
        /// every triangle in software and its own guidance is to switch that off on anything
        /// large. Marching the height field costs one step per column crossed and answers with a
        /// block coordinate, which is what a spawn point wanted anyway.
        /// </summary>
        /// <summary>
        /// The same look a click makes, for a probe to check.
        ///
        /// The ray and the pick were both right and the answer still came out wrong, because the
        /// two spoke different tuples. Only the whole path catches that.
        /// </summary>
        internal (int x, int y, int z)? probeLook(Point at) => look(at);

        /// <summary>
        /// Takes hold of the spawn point under a press, if there is one.
        /// </summary>
        /// <returns>Whether a drag started, which is the same as "do not turn the camera".</returns>
        private bool grab(Point at)
        {
            _dragFrom = at;
            _moved = false;

            var on = look(at);
            if (on == null) { return false; }

            var held = holding(on.Value);
            if (held == null) { return false; }

            _dragging = true;
            _dragKind = held.Value.kind;
            _dragAt = held.Value.at;

            //Selected on the way down rather than on the way up, because a drag has no way up
            //until it is over and the pin being moved has to be chosen before it can move.
            Grabbed?.Invoke(held.Value.kind, held.Value.at.x, held.Value.at.y, held.Value.at.z);
            return true;
        }

        private void dragTo(Point at)
        {
            //Measured from where the press landed, not from the last frame, so a slow drag still
            //counts as one. That is why _dragFrom stays put for the whole drag.
            if (Math.Abs(at.X - _dragFrom.X) > 2 || Math.Abs(at.Y - _dragFrom.Y) > 2)
            {
                _moved = true;
            }

            var to = look(at);
            if (to == null || to.Value == _dragAt) { return; }

            _dragAt = to.Value;
            Dragged?.Invoke(to.Value.x, to.Value.y, to.Value.z);
        }

        private void drop()
        {
            _dragging = false;

            //A press that never travelled was somebody selecting a point, and Picked already said
            //so on the way down. Reporting a drop as well would write an edit for a click that
            //moved nothing.
            if (_moved) { Dropped?.Invoke(_dragAt.x, _dragAt.y, _dragAt.z); }
        }

        /// <summary>Which kind of pin is being dragged, for whoever has to move it.</summary>
        public Pin heldKind => _dragKind;

        /// <summary>
        /// The same press, drag and release the mouse makes, for a probe to run.
        ///
        /// These call the handlers' own methods rather than repeating what they do. A probe that
        /// reimplements the gesture passes while the gesture is broken - which has happened here
        /// before, when a probe read a tuple by name off a call that returned it by position.
        /// </summary>
        internal bool probeGrab(Point at) => grab(at);

        internal void probeDragTo(Point at) => dragTo(at);

        internal void probeDrop() => drop();

        internal bool probeDragging => _dragging;

        /// <summary>The spawn points as the view has them, for a probe to aim at.</summary>
        internal IReadOnlyList<(int x, int y, int z)> probeMarks => _marks;

        /// <summary>The doors and arrival areas as the view has them.</summary>
        internal IReadOnlyList<(int x, int y, int z)> probeDoorPins => _doorPins;

        internal IReadOnlyList<(int x, int y, int z)> probeStartPins => _startPins;

        internal IReadOnlyList<(int x, int y, int z)> probeExitPins => _exitPins;

        internal IReadOnlyList<(int x, int y, int z)> probeGatePins => _gatePins;

        internal IReadOnlyList<(int x, int y, int z)> probeStepPins => _stepPins;

        /// <summary>
        /// Which pin a spot is close enough to have meant, if any.
        ///
        /// All three kinds are searched and the nearest wins rather than the first kind that
        /// matches, because a door and an arrival area often stand within a few blocks of each
        /// other - that is what a mission entrance looks like - and taking hold of whichever was
        /// checked first would move the wrong one about half the time.
        /// </summary>
        private (Pin kind, (int x, int y, int z) at)? holding((int x, int y, int z) at)
        {
            (Pin kind, (int x, int y, int z) at)? best = null;
            var bestGap = GRAB * GRAB;

            void search(List<(int x, int y, int z)> pins, Pin kind)
            {
                foreach (var one in pins)
                {
                    //The same lopsided measure the click uses: height counts for a quarter,
                    //because two pins stacked vertically are rare and a few blocks out across the
                    //floor is the normal cost of aiming at a hillside.
                    var dx = (double)(one.x - at.x);
                    var dy = (double)(one.y - at.y);
                    var dz = (double)(one.z - at.z);

                    var gap = dx * dx + dz * dz + dy * dy * 0.25;
                    if (gap > bestGap) { continue; }

                    bestGap = gap;
                    best = (kind, one);
                }
            }

            search(_marks, Pin.Spawn);
            search(_doorPins, Pin.Door);
            search(_startPins, Pin.Start);
            search(_exitPins, Pin.Exit);
            search(_gatePins, Pin.Gate);
            search(_stepPins, Pin.Step);

            return best;
        }

        private (int x, int y, int z)? look(Point at)
        {
            if (_relief == null || ActualWidth <= 0 || ActualHeight <= 0) { return null; }

            var forward = _camera.LookDirection;
            forward.Normalize();

            var right = Vector3D.CrossProduct(forward, _camera.UpDirection);
            if (right.Length < 1e-9) { return null; }
            right.Normalize();

            var up = Vector3D.CrossProduct(right, forward);

            //WPF states the field of view across the WIDER side, which for a wide window is the
            //horizontal one. Assuming horizontal outright skews every pick on a tall window.
            var wide = ActualWidth >= ActualHeight;
            var half = Math.Tan(_camera.FieldOfView * Math.PI / 360);
            var aspect = ActualWidth / ActualHeight;

            var halfX = wide ? half : half * aspect;
            var halfY = wide ? half / aspect : half;

            var nx = (2 * at.X / ActualWidth - 1) * halfX;
            var ny = (1 - 2 * at.Y / ActualHeight) * halfY;

            var ray = forward + right * nx + up * ny;

            return MapRelief.pick(_relief,
                _camera.Position.X, _camera.Position.Y, _camera.Position.Z,
                ray.X, ray.Y, ray.Z,
                _distance + Math.Max(_relief.Sx, _relief.Sz) * 2.0);
        }
    }
}
