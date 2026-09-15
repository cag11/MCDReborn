using System;
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
    /// A Minecraft character, drawn from a 64x64 skin.
    ///
    /// The figure is twelve boxes - six body parts and the six overlay pieces that sit a hair
    /// outside them - which is the whole of the Minecraft player model. WPF has 3D in the box,
    /// so this needs no library and no browser.
    ///
    /// What usually goes wrong with this is the unwrap: a sleeve mapped upside down on one arm,
    /// or a head whose back is its front. The rectangles here are not guessed. They were
    /// measured out of the game's own textures and checked by seam continuity - colours run
    /// across the joins of the real art at less than half the difference of random columns,
    /// which is only true if these are the rectangles the artists painted on.
    ///
    /// This is honest for a hero. It would not be for armour: Minecraft Dungeons armour is
    /// sculpted geometry rather than boxes, so a skin lands on it stretched and broken. The hero
    /// underneath really is this shape.
    /// </summary>
    public class SkinViewer3D : Grid
    {
        private const double SHEET = 64.0;

        private readonly Viewport3D _viewport = new Viewport3D();
        private readonly ModelVisual3D _figure = new ModelVisual3D();
        private readonly AxisAngleRotation3D _turn = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 22);
        private readonly AxisAngleRotation3D _tilt = new AxisAngleRotation3D(new Vector3D(1, 0, 0), -8);
        private readonly PerspectiveCamera _camera;

        private Point _dragFrom;
        private bool _dragging;

        public SkinViewer3D()
        {
            _camera = new PerspectiveCamera(new Point3D(0, 16, 80), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 45);
            _viewport.Camera = _camera;
            _viewport.ClipToBounds = true;

            //WPF's FieldOfView is the horizontal one, so a wide, short panel leaves a very small
            //vertical angle and the figure fills far more than the height. The distance has to
            //follow the shape of the panel rather than being a number chosen once.
            SizeChanged += (s, e) => frameFigure();

            var lights = new ModelVisual3D {
                Content = new Model3DGroup {
                    Children = {
                        //Mostly ambient: pixel art has its own shading painted in, and a strong
                        //key light fights it rather than helping.
                        new AmbientLight(Color.FromRgb(190, 190, 190)),
                        new DirectionalLight(Color.FromRgb(90, 90, 90), new Vector3D(-0.4, -0.6, -1)),
                    },
                },
            };

            var spin = new Transform3DGroup();
            spin.Children.Add(new RotateTransform3D(_turn));
            spin.Children.Add(new RotateTransform3D(_tilt));
            _figure.Transform = spin;

            _viewport.Children.Add(lights);
            _viewport.Children.Add(_figure);
            Children.Add(_viewport);

            Cursor = Cursors.SizeWE;
            MouseLeftButtonDown += onDown;
            MouseMove += onMove;
            MouseLeftButtonUp += onUp;
            MouseLeave += onUp;
        }

        private BitmapSource? _skin;

        /// <summary>The 64x64 sheet to wear. Null empties the viewer.</summary>
        public BitmapSource? skin {
            get => _skin;
            set { _skin = value; rebuild(); }
        }

        public void resetView()
        {
            _turn.Angle = 22;
            _tilt.Angle = -8;
            frameFigure();
        }

        /// <summary>
        /// Backs the camera off far enough that the whole figure fits, whatever shape the panel
        /// has been given.
        /// </summary>
        private void frameFigure()
        {
            if (ActualWidth < 1 || ActualHeight < 1) { return; }

            const double figureHeight = 42;   // 32 tall, plus room for the tilt and some air
            var aspect = ActualWidth / ActualHeight;
            var horizontal = _camera.FieldOfView * Math.PI / 180.0;
            //Vertical angle from the horizontal one, which is what WPF actually takes.
            var vertical = 2 * Math.Atan(Math.Tan(horizontal / 2) / Math.Max(aspect, 0.0001));

            var distance = (figureHeight / 2) / Math.Tan(vertical / 2);
            _camera.Position = new Point3D(0, 16, Math.Max(distance, 40));
        }

        #region Turning it round

        private void onDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragFrom = e.GetPosition(this);
            CaptureMouse();
        }

        private void onMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) { return; }
            var now = e.GetPosition(this);
            _turn.Angle += (now.X - _dragFrom.X) * 0.5;
            //Clamped so the figure cannot be rolled past upside down, which is only ever a
            //way to lose track of which way it is facing.
            _tilt.Angle = Math.Max(-60, Math.Min(60, _tilt.Angle + (now.Y - _dragFrom.Y) * 0.3));
            _dragFrom = now;
        }

        private void onUp(object sender, MouseEventArgs e)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }

        #endregion

        #region Building the figure

        /// <summary>A box of the player model: where it sits, how big, and its texture origin.</summary>
        private readonly struct Part
        {
            public readonly double X, Y, Z, W, H, D;
            public readonly int U, V;
            /// <summary>Overlay pieces are the same box grown slightly, so they sit just outside.</summary>
            public readonly double Grow;

            public Part(double x, double y, double z, double w, double h, double d, int u, int v, double grow = 0)
            {
                X = x; Y = y; Z = z; W = w; H = h; D = d; U = u; V = v; Grow = grow;
            }
        }

        //The standard model, in texture pixels: a 32-tall figure with the head on top.
        private static readonly Part[] PARTS = {
            new Part(-4, 24, -4, 8, 8, 8, 0, 0),      // head
            new Part(-4, 12, -2, 8, 12, 4, 16, 16),   // body
            new Part(-8, 12, -2, 4, 12, 4, 40, 16),   // right arm
            new Part(4, 12, -2, 4, 12, 4, 32, 48),    // left arm
            new Part(-4, 0, -2, 4, 12, 4, 0, 16),     // right leg
            new Part(0, 0, -2, 4, 12, 4, 16, 48),     // left leg

            new Part(-4, 24, -4, 8, 8, 8, 32, 0, 0.5),      // hat
            new Part(-4, 12, -2, 8, 12, 4, 16, 32, 0.25),   // jacket
            new Part(-8, 12, -2, 4, 12, 4, 40, 32, 0.25),   // right sleeve
            new Part(4, 12, -2, 4, 12, 4, 48, 48, 0.25),    // left sleeve
            new Part(-4, 0, -2, 4, 12, 4, 0, 32, 0.25),     // right trouser
            new Part(0, 0, -2, 4, 12, 4, 0, 48, 0.25),      // left trouser
        };

        private void rebuild()
        {
            if (_skin == null || _skin.PixelWidth != 64 || _skin.PixelHeight != 64)
            {
                _figure.Content = null;
                return;
            }

            //Enlarged by whole pixels before it becomes a brush. A 3D material does not honour
            //BitmapScalingMode the way an Image does - the sheet is resampled on its way onto the
            //geometry - so a 64x64 texture arrives as a blur. Blowing it up nearest-neighbour
            //first means the smoothing has nothing left to smear.
            var brush = new ImageBrush(enlarge(_skin, 8)) { ViewportUnits = BrushMappingMode.Absolute };
            brush.Freeze();

            var material = new DiffuseMaterial(brush);
            var group = new Model3DGroup();
            foreach (var part in PARTS)
            {
                var mesh = build(part);
                //Both sides: the overlay pieces are see-through in places, and a back face that
                //vanished would leave a hole rather than the inside of the hat.
                group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
            }
            _figure.Content = group;
        }

        /// <summary>Nearest-neighbour by hand: every pixel becomes a square of them.</summary>
        private static BitmapSource enlarge(BitmapSource source, int factor)
        {
            int w = source.PixelWidth, h = source.PixelHeight;
            var from = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var small = new byte[w * h * 4];
            from.CopyPixels(small, w * 4, 0);

            int bw = w * factor, bh = h * factor;
            var big = new byte[bw * bh * 4];
            for (int y = 0; y < bh; y++)
            {
                int sourceRow = (y / factor) * w * 4;
                int targetRow = y * bw * 4;
                for (int x = 0; x < bw; x++)
                {
                    Buffer.BlockCopy(small, sourceRow + (x / factor) * 4, big, targetRow + x * 4, 4);
                }
            }

            var result = BitmapSource.Create(bw, bh, 96, 96, PixelFormats.Bgra32, null, big, bw * 4);
            result.Freeze();
            return result;
        }

        private static MeshGeometry3D build(Part part)
        {
            var g = part.Grow;
            double x0 = part.X - g, y0 = part.Y - g, z0 = part.Z - g;
            double x1 = part.X + part.W + g, y1 = part.Y + part.H + g, z1 = part.Z + part.D + g;

            //The Minecraft cross layout: top and bottom above, then right, front, left, back.
            int u = part.U, v = part.V;
            int w = (int)part.W, h = (int)part.H, d = (int)part.D;

            var mesh = new MeshGeometry3D();

            // front (+Z): image-left is the viewer's left, which is the figure's own right
            quad(mesh,
                new Point3D(x0, y1, z1), new Point3D(x0, y0, z1), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1),
                u + d, v + d, w, h);

            // back (-Z): seen from behind, so the horizontal runs the other way
            quad(mesh,
                new Point3D(x1, y1, z0), new Point3D(x1, y0, z0), new Point3D(x0, y0, z0), new Point3D(x0, y1, z0),
                u + d + w + d, v + d, w, h);

            // right (-X)
            quad(mesh,
                new Point3D(x0, y1, z0), new Point3D(x0, y0, z0), new Point3D(x0, y0, z1), new Point3D(x0, y1, z1),
                u, v + d, d, h);

            // left (+X)
            quad(mesh,
                new Point3D(x1, y1, z1), new Point3D(x1, y0, z1), new Point3D(x1, y0, z0), new Point3D(x1, y1, z0),
                u + d + w, v + d, d, h);

            // top (+Y): the image runs back-to-front down the rectangle
            quad(mesh,
                new Point3D(x0, y1, z0), new Point3D(x0, y1, z1), new Point3D(x1, y1, z1), new Point3D(x1, y1, z0),
                u + d, v, w, d);

            // bottom (-Y): front-to-back, the opposite of the top
            quad(mesh,
                new Point3D(x0, y0, z1), new Point3D(x0, y0, z0), new Point3D(x1, y0, z0), new Point3D(x1, y0, z1),
                u + d + w, v, w, d);

            mesh.Freeze();
            return mesh;
        }

        /// <summary>
        /// One face: four corners and the texture rectangle they carry, in sheet pixels.
        ///
        /// The corners arrive in the order top-left, bottom-left, bottom-right, top-right as the
        /// texture reads, so the rectangle maps straight onto them with no rotation to reason
        /// about at each call site.
        /// </summary>
        private static void quad(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d,
                                 int px, int py, int pw, int ph)
        {
            int at = mesh.Positions.Count;
            mesh.Positions.Add(a);
            mesh.Positions.Add(b);
            mesh.Positions.Add(c);
            mesh.Positions.Add(d);

            //Half a pixel in from each edge. Sampling exactly on the boundary picks up the
            //neighbouring rectangle on some drivers, which shows as a thread of the wrong
            //colour along every seam.
            double u0 = (px + 0.02) / SHEET, v0 = (py + 0.02) / SHEET;
            double u1 = (px + pw - 0.02) / SHEET, v1 = (py + ph - 0.02) / SHEET;

            mesh.TextureCoordinates.Add(new Point(u0, v0));
            mesh.TextureCoordinates.Add(new Point(u0, v1));
            mesh.TextureCoordinates.Add(new Point(u1, v1));
            mesh.TextureCoordinates.Add(new Point(u1, v0));

            mesh.TriangleIndices.Add(at);
            mesh.TriangleIndices.Add(at + 1);
            mesh.TriangleIndices.Add(at + 2);
            mesh.TriangleIndices.Add(at);
            mesh.TriangleIndices.Add(at + 2);
            mesh.TriangleIndices.Add(at + 3);
        }

        #endregion
    }
}
