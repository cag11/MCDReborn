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

            foreach (var one in spawns)
            {
                pillar(mesh, one.x + 0.5, one.y, one.z + 0.5, 0.9, 4.0);
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
                _turning = true;
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
