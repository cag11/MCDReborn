using MCDSaveEdit.Logic;
using PakReader.Pak;
using MCDSaveEdit.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Putting a different model into one of the game's meshes.
    ///
    /// Serves two tabs. Which meshes are offered and how they are written back comes from a
    /// <see cref="MeshCatalogue"/> - weapons in one, creatures in the other - and everything else
    /// on screen is the same work either way, so it is the same control rather than two of it.
    ///
    /// The same arrangement as the recolouring tab and for the same reason: nothing here touches a
    /// save file, and nothing here modifies the game. A mod pak sits beside the game's own holding
    /// a file at the same asset path, the engine loads that instead, and deleting the pak is the
    /// whole of undo.
    ///
    /// What is different is that this edits geometry rather than pixels, which brings a problem
    /// recolouring does not have: the thing being replaced is already in the right place and the
    /// model arriving is not. A weapon is held by its handle, and moving the mesh moves the handle
    /// out of the hand; a creature stands on the ground, and a model centred differently sinks
    /// into it. So the original is drawn behind the edit, in outline, and that ghost is the
    /// alignment reference - it is the shape the game already knows how to hold or how to walk,
    /// and sitting the new shape on it is the whole of getting it right. A modelled arm or a
    /// modelled floor would say less: what is being replaced *is* the position, exactly, with
    /// nothing approximated.
    ///
    /// The preview and the file are computed by the same code, deliberately. A preview with its
    /// own copy of the arithmetic can disagree with what gets written, and it would disagree in
    /// the way nobody checks - where the picture looks right and the thing in game does not.
    /// </summary>
    public partial class MeshWorkshopTab : UserControl
    {
        private MeshCatalogue _catalogue = WeaponMeshes.catalogue;

        private MeshEntry? _selected;
        private MeshShape? _shape;

        //The model somebody brought, if they brought one. When this is set the tab is replacing
        //the weapon rather than reshaping it, and almost everything else behaves the same way.
        private GlbModel? _imported;

        //The artwork the preview is painted with: the weapon's own, or the imported model's when
        //one has been brought.
        private System.Windows.Media.Imaging.BitmapSource? _texture;

        //Remembered per weapon, so flicking through the list to compare does not throw away the
        //alignment somebody has just spent a minute getting right.
        private readonly Dictionary<string, MeshEdit.Transform> _transforms =
            new Dictionary<string, MeshEdit.Transform>(StringComparer.OrdinalIgnoreCase);

        private bool _filling;
        private double _spinX = -20;
        private double _spinY = 30;
        private Point _dragFrom;
        private bool _dragging;

        //What the view turns around and looks at. A weapon's own origin is not its middle - the
        //Claymore's sits fifty units down the blade - so spinning about the origin would swing the
        //mesh around the screen instead of turning it on the spot.
        private Point3D _centre = new Point3D(0, 0, 0);

        public MeshWorkshopTab()
        {
            InitializeComponent();
            hookSpin();
            translateStaticStrings();

            //Filled the first time somebody looks at it rather than at startup. Building the
            //creature list means opening several hundred packages to find out which of them can
            //be rewritten, which is a second nobody should pay for a tab they did not open.
            IsVisibleChanged += (s, e) => { if (IsVisible) { fillOnce(); } };
        }

        /// <summary>
        /// Which set of meshes this instance offers, set from the markup.
        ///
        /// A string rather than the catalogue itself because XAML cannot hand over an object
        /// without a great deal of ceremony, and there are two of these.
        /// </summary>
        public string Catalogue
        {
            get => _catalogue == MobMeshes.catalogue ? MOBS : WEAPONS;
            set
            {
                _catalogue = string.Equals(value, MOBS, StringComparison.OrdinalIgnoreCase)
                    ? MobMeshes.catalogue
                    : WeaponMeshes.catalogue;
                translateStaticStrings();
                _filled = false;
                if (IsVisible) { fillOnce(); }
            }
        }

        private const string WEAPONS = "Weapons";
        private const string MOBS = "Mobs";

        private bool _filled;

        private void fillOnce()
        {
            if (_filled) { return; }
            _filled = true;
            updateUI();
        }

        private void translateStaticStrings()
        {
            meshLabel.Content = _catalogue.subjectLabel;
            previewLabel.Content = R.WEAPON_SKINS_PREVIEW;
            shapeLabel.Content = R.WEAPON_SKINS_SHAPE;
            sizeHeader.Text = R.WEAPON_SKINS_SIZE;
            moveHeader.Text = R.WEAPON_SKINS_MOVE;
            turnHeader.Text = R.WEAPON_SKINS_TURN;
            scaleCaption.Text = R.WEAPON_SKINS_SCALE;
            applyButton.Content = R.WEAPON_SKINS_APPLY;
            resetButton.Content = R.WEAPON_SKINS_RESET;
            ghostCheckBox.Content = R.WEAPON_SKINS_GHOST;
            ghostHint.Text = _catalogue.ghostHint;
            spinHint.Text = R.WEAPON_SKINS_SPIN;
            modsNoteLabel.Text = R.WEAPON_SKINS_MODS_NOTE;
            searchBox.ToolTip = R.WEAPON_SKINS_SEARCH;
            modelHeader.Text = R.WEAPON_SKINS_MODEL;
            importButton.Content = R.WEAPON_SKINS_IMPORT;
            clearModelButton.Content = R.WEAPON_SKINS_CLEAR_MODEL;
            importHint.Text = _catalogue.importHint;
            texturedCheckBox.Content = R.WEAPON_SKINS_TEXTURED;
            glowHeader.Text = R.WEAPON_SKINS_GLOW;
            glowCheckBox.Content = R.WEAPON_SKINS_GLOW_ON;
            glowColourCaption.Text = R.WEAPON_SKINS_GLOW_COLOUR;
            glowPowerCaption.Text = R.WEAPON_SKINS_GLOW_POWER;
            glowHint.Text = R.WEAPON_SKINS_GLOW_HINT;
            fillGlowColours();
            showGlow();
        }

        public void updateUI()
        {
            fillCategories();
            fillMeshList();
            updateSelection();
        }

        #region Choosing

        /// <summary>
        /// The headings the list can be narrowed to, when the catalogue has more than one.
        ///
        /// A list of one is not a choice, so it is not shown as one.
        /// </summary>
        private void fillCategories()
        {
            var groups = _catalogue.groups();

            categoryCombo.Items.Clear();
            foreach (var group in groups)
            {
                categoryCombo.Items.Add(new ComboBoxItem { Content = group, Tag = group });
            }
            if (categoryCombo.Items.Count > 0) { categoryCombo.SelectedIndex = 0; }
            categoryCombo.Visibility = groups.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private string? selectedGroup => (categoryCombo.SelectedItem as ComboBoxItem)?.Tag as string;

        private void categoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => fillMeshList();

        private void searchBox_TextChanged(object sender, TextChangedEventArgs e) => fillMeshList();

        private void fillMeshList()
        {
            if (!IsInitialized) { return; }

            _filling = true;
            meshList.Items.Clear();

            if (!_catalogue.ready)
            {
                countLabel.Text = R.WEAPON_SKINS_NO_CONTENT;
                _filling = false;
                updateSelection();
                return;
            }

            var group = selectedGroup;
            var search = searchBox.Text?.Trim() ?? string.Empty;
            var matching = _catalogue.all()
                .Where(mesh => group == null || mesh.Group == group)
                .Where(mesh => search.Length == 0
                    || mesh.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || mesh.Variant.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .ToList();

            foreach (var mesh in matching)
            {
                //The folder is shown under the name because it is often the only thing telling two
                //apart - three different meshes are all called some variety of "Claymore", and the
                //game has a baby version of half its animals.
                var row = new StackPanel();
                row.Children.Add(new TextBlock { Text = mesh.Name });
                row.Children.Add(new TextBlock {
                    Text = mesh.Variant,
                    Foreground = Brushes.Gray,
                    FontSize = 10,
                });

                //Marked in the list rather than only once it is picked, so it can be avoided
                //before anybody spends an evening modelling something for it.
                if (mesh.Caution.Length > 0)
                {
                    row.Children.Add(new TextBlock {
                        Text = R.WEAPON_SKINS_WRONG_MASTER_TAG,
                        Foreground = new SolidColorBrush(Color.FromRgb(216, 160, 64)),
                        FontSize = 10,
                    });
                }

                meshList.Items.Add(new ListBoxItem { Content = row, Tag = mesh, ToolTip = mesh.Caution.Length > 0 ? mesh.Caution : null });
            }

            //Advice about the chosen heading goes here rather than against every entry, where it
            //would wear the mark that means an import onto this one comes out wrong.
            var note = _catalogue.noteFor(group);
            countLabel.Text = string.Format(_catalogue.countFormat, matching.Count)
                + "\n" + _catalogue.scopeNote
                + (note.Length > 0 ? "\n" + note : string.Empty);
            _filling = false;

            if (meshList.Items.Count > 0) { meshList.SelectedIndex = 0; }
            else { updateSelection(); }
        }

        private void meshList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) { return; }
            updateSelection();
        }

        private void updateSelection()
        {
            _selected = (meshList.SelectedItem as ListBoxItem)?.Tag as MeshEntry;
            _shape = _selected == null ? null : _catalogue.read(_selected.AssetPath);
            if (_imported == null)
            {
                _texture = _selected == null ? null : _catalogue.textureFor(_selected.AssetPath);
            }

            //And said in full when it is picked, because the mark in the list is four words.
            statusLabel.Text = _selected?.Caution ?? string.Empty;

            showTransform(_selected == null
                ? MeshEdit.Transform.none
                : _transforms.TryGetValue(_selected.AssetPath, out var saved) ? saved : MeshEdit.Transform.none);

            //The offsets are bounded by the weapon's own size rather than by a fixed number. A
            //dagger and a claymore need very different ranges, and a slider whose useful travel is
            //the first two percent is a slider nobody can aim with.
            var reach = Math.Max(20.0, (_shape?.LongestSide ?? 100f) * 1.5);
            foreach (var slider in new[] { offsetXSlider, offsetYSlider, offsetZSlider })
            {
                slider.Minimum = -reach;
                slider.Maximum = reach;
            }

            redraw();
        }

        #endregion

        #region Shaping

        private MeshEdit.Transform currentTransform => new MeshEdit.Transform(
            (float)scaleSlider.Value,
            new MeshGeometry.Position((float)offsetXSlider.Value, (float)offsetYSlider.Value, (float)offsetZSlider.Value),
            new MeshGeometry.Position((float)rotateXSlider.Value, (float)rotateYSlider.Value, (float)rotateZSlider.Value));

        private void showTransform(MeshEdit.Transform transform)
        {
            _filling = true;
            scaleSlider.Value = transform.Scale;
            offsetXSlider.Value = transform.Offset.X;
            offsetYSlider.Value = transform.Offset.Y;
            offsetZSlider.Value = transform.Offset.Z;
            rotateXSlider.Value = transform.RotationDegrees.X;
            rotateYSlider.Value = transform.RotationDegrees.Y;
            rotateZSlider.Value = transform.RotationDegrees.Z;
            _filling = false;
            showNumbers();
        }

        private void showNumbers()
        {
            scaleValue.Text = scaleSlider.Value.ToString("0.00") + "x";
            offsetXValue.Text = offsetXSlider.Value.ToString("0.#");
            offsetYValue.Text = offsetYSlider.Value.ToString("0.#");
            offsetZValue.Text = offsetZSlider.Value.ToString("0.#");
            rotateXValue.Text = rotateXSlider.Value.ToString("0") + "°";
            rotateYValue.Text = rotateYSlider.Value.ToString("0") + "°";
            rotateZValue.Text = rotateZSlider.Value.ToString("0") + "°";
        }

        private void transform_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_filling || !IsInitialized) { return; }
            showNumbers();
            if (_selected != null) { _transforms[_selected.AssetPath] = currentTransform; }
            redraw();
        }

        private void resetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selected != null) { _transforms.Remove(_selected.AssetPath); }
            showTransform(MeshEdit.Transform.none);
            redraw();
        }

        private void ghostCheckBox_Changed(object sender, RoutedEventArgs e) => redraw();

        #endregion

        #region Drawing

        /// <summary>
        /// Rebuilds the picture: the edited shape solid, the original behind it in outline.
        ///
        /// Everything is rebuilt rather than transformed by the viewport, because the point of the
        /// preview is to show the vertices where the file will have them. Letting the renderer
        /// apply the rotation would show what the renderer does with it, which is not the same
        /// question.
        /// </summary>
        private void redraw()
        {
            scene.Children.Clear();

            var showing = previewShape();
            if (showing == null || showing.Indices.Count == 0)
            {
                previewMessage.Text = _selected == null
                    ? R.WEAPON_SKINS_PICK_ONE
                    : R.WEAPON_SKINS_NO_GEOMETRY;
                previewMessage.Visibility = Visibility.Visible;
                measurementLabel.Text = string.Empty;
                applyButton.IsEnabled = false;
                return;
            }

            previewMessage.Visibility = Visibility.Collapsed;
            applyButton.IsEnabled = true;

            var transform = currentTransform;

            //The ghost is always the weapon as the game knows it, never the imported model. It is
            //there to say where the hand is, and an outline of the thing being positioned would
            //say nothing.
            if (ghostCheckBox.IsChecked == true && _shape != null && (_imported != null || !transform.isNothing))
            {
                scene.Children.Add(new GeometryModel3D {
                    Geometry = build(_shape, MeshEdit.Transform.none),
                    Material = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(60, 120, 170, 255))),
                    BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(40, 120, 170, 255))),
                });
            }

            var edited = build(showing, transform);
            var painted = texturedCheckBox.IsChecked == true
                && _texture != null
                && showing.TexCoords.Count > 0;

            //An ImageBrush stretches over the whole coordinate space by default, which is what a
            //texture wants. Tiling is left off: a coordinate outside nought to one is a fault in
            //the model, and showing it repeated would hide that.
            Brush front = painted
                ? new ImageBrush(_texture) { ViewportUnits = BrushMappingMode.Absolute }
                : new SolidColorBrush(Color.FromRgb(214, 220, 228));
            Brush back = painted
                ? new ImageBrush(_texture) { ViewportUnits = BrushMappingMode.Absolute, Opacity = 0.6 }
                : new SolidColorBrush(Color.FromRgb(120, 128, 140));

            scene.Children.Add(new GeometryModel3D {
                Geometry = edited,
                Material = new DiffuseMaterial(front),
                BackMaterial = new DiffuseMaterial(back),
            });

            //Brighter and flatter when textured, so the artwork is seen rather than the lighting;
            //more directional when plain, because with no texture the shading is the only thing
            //describing the shape.
            scene.Children.Add(new AmbientLight(painted ? Color.FromRgb(170, 170, 176) : Color.FromRgb(90, 90, 96)));
            scene.Children.Add(new DirectionalLight(
                painted ? Color.FromRgb(140, 140, 146) : Colors.White, new Vector3D(-0.4, -0.7, -1)));
            scene.Children.Add(new DirectionalLight(Color.FromRgb(70, 80, 100), new Vector3D(0.6, 0.4, 1)));

            scene.Transform = spin();
            frameCamera(edited.Bounds);
            describeSize(transform);
        }

        private static MeshGeometry3D build(MeshShape shape, MeshEdit.Transform transform)
        {
            var mesh = new MeshGeometry3D();
            var points = new Point3DCollection(shape.Positions.Count);

            foreach (var position in shape.Positions)
            {
                var moved = transform.isNothing ? position : transform.move(position);
                points.Add(new Point3D(moved.X, moved.Y, moved.Z));
            }

            mesh.Positions = points;
            mesh.TriangleIndices = new Int32Collection(shape.Indices);

            //Both the engine and WPF put the origin of a texture at its top left, so the
            //coordinates go across unchanged. There has to be one per vertex or WPF ignores the
            //lot, which is why a short list is padded rather than handed over as it is.
            if (shape.TexCoords.Count > 0)
            {
                var uvs = new PointCollection(shape.Positions.Count);
                for (int i = 0; i < shape.Positions.Count; i++)
                {
                    var pair = i < shape.TexCoords.Count ? shape.TexCoords[i] : (u: 0f, v: 0f);
                    uvs.Add(new Point(pair.u, pair.v));
                }
                mesh.TextureCoordinates = uvs;
            }
            //Normals are left for WPF to work out. The packed tangents in the file are exactly the
            //part that has not been decoded, so inventing them here would be inventing them.
            return mesh;
        }

        private Transform3D spin()
        {
            var turn = new Transform3DGroup();
            turn.Children.Add(new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(1, 0, 0), _spinX), _centre));
            turn.Children.Add(new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(0, 1, 0), _spinY), _centre));
            return turn;
        }

        /// <summary>
        /// Puts the camera far enough back to hold whatever is on screen.
        ///
        /// Framed on the edited shape rather than the original, so growing a weapon does not push
        /// it out of view - which would look like the edit having broken something.
        /// </summary>
        private void frameCamera(Rect3D bounds)
        {
            if (bounds.IsEmpty) { return; }

            _centre = new Point3D(
                bounds.X + bounds.SizeX / 2,
                bounds.Y + bounds.SizeY / 2,
                bounds.Z + bounds.SizeZ / 2);

            var reach = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
            if (reach <= 0) { reach = 100; }

            var distance = reach * 1.6;
            camera.Position = new Point3D(_centre.X, _centre.Y, _centre.Z + distance);
            camera.LookDirection = new Vector3D(0, 0, -1);
        }

        private void describeSize(MeshEdit.Transform transform)
        {
            var showing = previewShape();
            if (showing == null) { return; }

            float lowX = float.MaxValue, lowY = float.MaxValue, lowZ = float.MaxValue;
            float highX = float.MinValue, highY = float.MinValue, highZ = float.MinValue;
            foreach (var position in showing.Positions)
            {
                var moved = transform.move(position);
                lowX = Math.Min(lowX, moved.X); highX = Math.Max(highX, moved.X);
                lowY = Math.Min(lowY, moved.Y); highY = Math.Max(highY, moved.Y);
                lowZ = Math.Min(lowZ, moved.Z); highZ = Math.Max(highZ, moved.Z);
            }

            measurementLabel.Text = string.Format(R.WEAPON_SKINS_MEASUREMENTS,
                showing.Positions.Count, showing.TriangleCount,
                (highX - lowX).ToString("0.#"), (highY - lowY).ToString("0.#"), (highZ - lowZ).ToString("0.#"));
        }

        /// <summary>Dragging turns the model, because a weapon seen from one side is half a look.</summary>
        private void hookSpin()
        {
            viewport.MouseLeftButtonDown += (s, e) => {
                _dragging = true;
                _dragFrom = e.GetPosition(viewport);
                viewport.CaptureMouse();
            };
            viewport.MouseLeftButtonUp += (s, e) => {
                _dragging = false;
                viewport.ReleaseMouseCapture();
            };
            viewport.MouseMove += (s, e) => {
                if (!_dragging) { return; }
                var now = e.GetPosition(viewport);
                _spinY += (now.X - _dragFrom.X) * 0.5;
                _spinX += (now.Y - _dragFrom.Y) * 0.5;
                _dragFrom = now;
                scene.Transform = spin();
            };
            viewport.MouseWheel += (s, e) => {
                //Zoom by stepping the camera a fraction of the way towards what it is looking at,
                //so the step stays proportional and a dagger does not take twenty turns of the
                //wheel to approach while a claymore overshoots in one.
                var towards = _centre - camera.Position;
                camera.Position += towards * (e.Delta > 0 ? 0.12 : -0.12);
            };
        }

        #endregion


        #region Importing

        /// <summary>What the preview is showing: the imported model when there is one.</summary>
        private MeshShape? previewShape()
        {
            if (_imported == null) { return _shape; }

            var (origin, extent, radius) = CookedMesh.measure(_imported.Positions);
            return new MeshShape(_imported.Positions, _imported.Indices, origin, extent, radius);
        }

        private void importButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || _shape == null)
            {
                statusLabel.Text = R.WEAPON_SKINS_PICK_ONE;
                return;
            }

            var picker = new OpenFileDialog {
                Filter = R.WEAPON_SKINS_MODEL_FILTER,
                CheckFileExists = true,
            };
            if (picker.ShowDialog() != true) { return; }

            try
            {
                var model = GlbModel.read(picker.FileName);
                _imported = model;

                //Its own artwork, which is the only thing that makes the preview worth looking at:
                //an imported model wearing the weapon's texture would be painted with somebody
                //else's layout, so the picture would be misleading rather than merely plain.
                _texture = model.BaseColourPng == null ? null : safeImage(model.BaseColourPng);

                //Fitted on arrival rather than dropped at its own scale. A model is usually built
                //a few units long where a weapon here is a couple of hundred, so without this the
                //first sight of it is either a speck or a wall.
                var fit = ModelFitting.autoFit(model, _shape);

                //And the slider has to be able to hold that number. The sword this was built
                //against needs sixteen times its own size to match a claymore, against a slider
                //that stopped at four - so the fitted scale would have been silently clamped and
                //the model would have arrived a quarter of the size it was fitted to.
                widenScaleAround(fit.Scale);
                showTransform(fit);
                if (_selected != null) { _transforms[_selected.AssetPath] = currentTransform; }

                modelLabel.Text = string.Format(R.WEAPON_SKINS_MODEL_LOADED,
                    model.Name, model.VertexCount, model.TriangleCount,
                    model.BaseColourPng != null ? R.WEAPON_SKINS_WITH_TEXTURE : R.WEAPON_SKINS_NO_TEXTURE);

                //Said here rather than left to be discovered in game. A model whose artwork was
                //lost on the way to .glb imports correctly and comes out blank, which looks like
                //this having gone wrong.
                if (model.Note.Length > 0) { modelLabel.Text += "\n" + model.Note; }

                //And the one thing a model can be missing that looks exactly like a broken
                //import: no colour at all. It imports perfectly and comes out wearing whatever
                //the weapon it replaced was painted with.
                if (model.BaseColourPng == null) { modelLabel.Text += "\n" + R.WEAPON_SKINS_NO_COLOUR; }

                clearModelButton.Visibility = Visibility.Visible;
                statusLabel.Text = string.Empty;
                redraw();
            }
            catch (Exception problem)
            {
                //Said plainly. A model this cannot read is a fact about the file, and a dialog box
                //would not make it any more actionable.
                _imported = null;
                modelLabel.Text = problem.Message;
                clearModelButton.Visibility = Visibility.Collapsed;
                redraw();
            }
        }

        /// <summary>Lets the scale slider reach a fitted value, and stay usable around it.</summary>
        private void widenScaleAround(double fitted)
        {
            var top = Math.Max(BIGGEST_PLAIN_SCALE, fitted * 4.0);
            scaleSlider.Minimum = Math.Min(0.1, fitted / 8.0);
            scaleSlider.Maximum = top;
            scaleSlider.TickFrequency = Math.Max(0.01, top / 400.0);
        }

        //Eight rather than four, which is past anything sensible on purpose. A weapon at twice
        //the size it should be is a mistake; at eight times it is obviously deliberate, and people
        //will want that. Nothing downstream cares - the bounds are measured from wherever the
        //vertices end up - so the only thing the old limit protected was somebody's taste.
        private const double BIGGEST_PLAIN_SCALE = 8.0;

        private void resetScaleRange()
        {
            scaleSlider.Minimum = 0.1;
            scaleSlider.Maximum = BIGGEST_PLAIN_SCALE;
            scaleSlider.TickFrequency = 0.05;
        }

        private static System.Windows.Media.Imaging.BitmapSource? safeImage(byte[] png)
        {
            //A model can carry an image in a format the decoder does not know. That costs the
            //preview its texture and nothing else, so it is caught rather than thrown.
            try { return CustomSkins.imageFromPng(png); }
            catch (Exception) { return null; }
        }

        private void clearModelButton_Click(object sender, RoutedEventArgs e)
        {
            _imported = null;
            _texture = _selected == null ? null : _catalogue.textureFor(_selected.AssetPath);
            resetScaleRange();
            modelLabel.Text = string.Empty;
            clearModelButton.Visibility = Visibility.Collapsed;
            showTransform(MeshEdit.Transform.none);
            if (_selected != null) { _transforms.Remove(_selected.AssetPath); }
            redraw();
        }

        #endregion

        #region Applying


        #region Glow

        /// <summary>
        /// The colours offered, and what each is worth as light.
        ///
        /// Brighter than one, because these are multiplied by the strength below and a material
        /// lit to exactly white washes out to a flat shape with no edges. The game's own glowing
        /// arrows sit around seven on their strongest channel.
        /// </summary>
        private static readonly (string key, float r, float g, float b)[] GLOW_COLOURS = {
            ("RED", 7f, 0.3f, 0f),
            ("GREEN", 0.3f, 7f, 0.5f),
            ("BLUE", 0f, 2f, 7f),
            ("PURPLE", 5f, 0f, 7f),
            ("ORANGE", 7f, 2f, 0f),
            ("WHITE", 5f, 5f, 5f),
        };

        private void fillGlowColours()
        {
            if (glowColourCombo.Items.Count > 0) { return; }

            foreach (var (key, _, _, _) in GLOW_COLOURS)
            {
                glowColourCombo.Items.Add(new ComboBoxItem { Content = colourName(key), Tag = key });
            }
            glowColourCombo.SelectedIndex = 0;
        }

        private static string colourName(string key) => key switch {
            "RED" => R.WEAPON_SKINS_GLOW_RED,
            "GREEN" => R.WEAPON_SKINS_GLOW_GREEN,
            "BLUE" => R.WEAPON_SKINS_GLOW_BLUE,
            "PURPLE" => R.WEAPON_SKINS_GLOW_PURPLE,
            "ORANGE" => R.WEAPON_SKINS_GLOW_ORANGE,
            _ => R.WEAPON_SKINS_GLOW_WHITE,
        };

        private void glow_Changed(object sender, RoutedEventArgs e) => showGlow();

        private void glow_Changed(object sender, SelectionChangedEventArgs e) => showGlow();

        private void glowPower_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => showGlow();

        private void showGlow()
        {
            if (!IsInitialized) { return; }

            var on = glowCheckBox.IsChecked == true;
            glowColourRow.IsEnabled = on;
            glowPowerRow.IsEnabled = on;
            glowPowerValue.Text = ((int)glowPowerSlider.Value).ToString();
        }

        /// <summary>
        /// The material files that make this one glow, or nothing when it is not asked for.
        ///
        /// Nothing is also the answer when the materials have no emissive entry to write into.
        /// That is a real outcome rather than a failure - a value that was never in the file
        /// cannot be changed in place - and it is said in the status line afterwards.
        /// </summary>
        private IEnumerable<PakWriter.Entry>? glowEntries(out List<string> lit)
        {
            lit = new List<string>();
            if (_selected == null || glowCheckBox.IsChecked != true) { return null; }

            var key = (glowColourCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "RED";
            var colour = GLOW_COLOURS[0];
            foreach (var one in GLOW_COLOURS)
            {
                if (one.key == key) { colour = one; break; }
            }

            var entries = Glow.ignite(_selected.AssetPath, colour.r, colour.g, colour.b,
                (float)glowPowerSlider.Value, out lit).ToList();

            return entries.Count > 0 ? entries : null;
        }

        #endregion

        private void applyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) { return; }

            var transform = currentTransform;
            var glow = glowEntries(out var lit);

            //A glow on its own is a change worth writing, even where the shape is untouched.
            if (_imported == null && glow == null && (transform.isNothing || !_catalogue.canReshape))
            {
                statusLabel.Text = _catalogue.nothingToDo;
                return;
            }

            try
            {
                var mod = _imported != null
                    ? _catalogue.replace(_selected.AssetPath, _imported, transform,
                        _selected.Name + " " + _imported.Name, glow)
                    : _catalogue.reshape(_selected.AssetPath, transform, _selected.Name, glow);
                statusLabel.Text = string.Format(R.WEAPON_SKINS_APPLIED, System.IO.Path.GetFileName(mod.Path))
                    + (lit.Count > 0 ? " " + string.Format(R.WEAPON_SKINS_GLOW_LIT, lit.Count) : string.Empty);
            }
            catch (Exception problem)
            {
                //Said plainly rather than thrown at a dialog. Some meshes simply cannot be
                //reshaped, and that is information rather than a fault.
                statusLabel.Text = problem.Message;
            }
        }

        #endregion
    }
}
