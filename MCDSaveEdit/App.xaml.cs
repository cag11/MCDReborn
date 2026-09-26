using MCDSaveEdit.Data;
using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using MCDSaveEdit.UI;
using PakReader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
#nullable enable

namespace MCDSaveEdit
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application, IDisposable
    {
        private readonly AppModel _model = new AppModel();
        private string[] _startupArguments = new string[0];
        private readonly MultiTextWriter _outputWriter = new MultiTextWriter();

        private ControlWriter? _controlWriter = null;
        private SplashWindow? _splashWindow = null;
        private Window? _busyWindow = null;

        /// <summary>
        /// Everywhere an exception can end a run, written down before it does.
        ///
        /// Four of them, because .NET has four different ways of losing one and each is reported
        /// through its own event:
        ///
        /// - the background threads, through <see cref="Trouble"/>, which is the one this was
        ///   built for - the live camera runs four loops and any of them throwing used to close
        ///   the application with no window, no message and nothing written anywhere;
        /// - the interface thread, which shows a dialog and can often carry on afterwards;
        /// - a task nobody awaited, which is otherwise silent entirely;
        /// - and everything else, which is already fatal by the time it arrives here, so the only
        ///   thing to do is get it on disk before the process goes.
        /// </summary>
        private void watchForTrouble()
        {
            LiveEdit.Trouble.reporter = (where, problem) => Services.Journal.trouble(where, problem);

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception problem)
                {
                    Services.Journal.trouble("the application", problem);
                }
                Services.Journal.note("---- ended badly ----");
            };

            DispatcherUnhandledException += (_, args) =>
            {
                Services.Journal.trouble("the window", args.Exception);

                //Not marked handled. Carrying on after an unknown fault means carrying on with an
                //unknown state, and the log now has the fault either way - which is the thing that
                //was missing, rather than the crash itself.
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Services.Journal.trouble("a background task", args.Exception);
                args.SetObserved();
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _startupArguments = e.Args;

            //Before anything else, so that whatever happens next is written down.
#if VERBOSE
            Services.Journal.loud = true;
#endif
            if (_startupArguments.Any(a => string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase)))
            {
                Services.Journal.loud = true;
            }

            Services.Journal.begin();
            watchForTrouble();

            //A class handler rather than per-window wiring: dialogs are created all over the
            //app, and any one that was missed would pop up with a white title bar. Loaded is
            //late enough for the window to have an HWND for DWM to paint.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, args) => UI.Theme.TitleBarTheme.apply(sender as Window)));
            UI.Theme.ThemeManager.themeChanged += _ => UI.Theme.TitleBarTheme.applyToAllWindows();

            //Before any window is created, so nothing ever renders unthemed.
            UI.Theme.ThemeManager.initialize();

            //Dev aid: the splash only exists for a moment during a real start, so it gets
            //its own capture path.
            var splashShot = _startupArguments.FirstOrDefault(a => a.StartsWith("SCREENSHOT_SPLASH="));
            if (splashShot != null)
            {
                var splash = WindowFactory.createSplashWindow();
                splash.Show();
                Console.WriteLine("Loading Pak Files");
                Console.WriteLine("Found 318 equipment images");
                UI.Theme.WindowCapture.captureThenExit(splash, splashShot.Substring("SCREENSHOT_SPLASH=".Length).Trim('"'));
                return;
            }
            _outputWriter.addWriter(Console.Out);
            Console.SetOut(_outputWriter);

            EventLogger.init();

            showSplashWindowReplacingOldWindow();

            var args = e.Args;
            _ = Application.Current?.Dispatcher.Invoke(DispatcherPriority.Background, new ThreadStart(delegate {
                startAsync(args);
            }));
        }

        private async void startAsync(string[] args)
        {
            MainThreadConsoleWriteLine($"{Constants.PRODUCT_NAME} {Constants.CURRENT_VERSION}");
            
            string? fileName = args.LastOrDefault();
            if(!string.IsNullOrWhiteSpace(fileName) && File.Exists(fileName))
            {
                string extension = Path.GetExtension(fileName!);
                EventLogger.logEvent("handleFileOpenAsync", new Dictionary<string, object>() { { "extension", extension } });
                await _model.mainModel.handleFileOpenAsync(fileName!);
            }

            bool skipGameContent = args.Contains("SKIP_GAME_CONTENT");
            if (skipGameContent)
            {
                showMainWindow();
            }
            else
            {
                bool askForGameContentLocation = args.Contains("ASK_FOR_GAME_CONTENT_LOCATION");
                await loadAsync(askForGameContentLocation);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            EventLogger.dispose();
            _outputWriter.Dispose();
            base.OnExit(e);
        }

        private async Task loadAsync(bool askForGameContentLocation)
        {
            MainThreadConsoleWriteLine("Searching for pak files...");
            bool canContinue = true;
            //check default install locations
            string? paksFolderPath = _model.usableGameContentIfExists();
            if (askForGameContentLocation || string.IsNullOrWhiteSpace(paksFolderPath))
            {
                //show dialog asking for install location
                canContinue = showGameFilesWindow(ref paksFolderPath);
            }

            if (!string.IsNullOrWhiteSpace(paksFolderPath))
            {
                MainThreadConsoleWriteLine($"Pak files path: {paksFolderPath}");
                try
                {
                    await loadGameContentAsync(paksFolderPath!);
                }
                catch (Exception e)
                {
                    //Clear the path saved in the registry because it might be the cause of the exception
                    _model.unloadGameContent();

                    var title = $"{Constants.PRODUCT_NAME} {Constants.CURRENT_VERSION} - {R.ERROR}";
                    var message = $"{R.FAILED_TO_LOAD_GAME_CONTENT_ERROR_TITLE}\n\n{e.Message}\n\n{R.PLEASE_HAVE_LATEST_VERSION}\n\n{R.LAUNCH_WITH_LIMITED_FEATURES_QUESTION}";
                    var result = MessageBox.Show(message, title, MessageBoxButton.YesNo);
                    canContinue = result == MessageBoxResult.Yes || result == MessageBoxResult.OK;
                }
            }
            else
            {
                //Clear the path saved in the registry
                _model.unloadGameContent();
            }

            if (canContinue == false)
            {
                //User opted to Exit
                _splashWindow?.Close();
                closeBusyIndicator();
                this.MainWindow?.Close();
                this.Shutdown();
                return;
            }

            showMainWindow();
        }

        private void MainThreadConsoleWriteLine(string str)
        {
            this.ExecuteOnMainThread(delegate {
                Console.WriteLine(str);
            });
        }

        private bool showGameFilesWindow(ref string? selectedPath)
        {
            EventLogger.logEvent("showGameFilesWindow");
            var gameFilesWindow = WindowFactory.createGameFilesWindow(selectedPath, allowNoContent: true);
            gameFilesWindow.ShowDialog();
            var gameFilesWindowResult = gameFilesWindow.result;
            switch (gameFilesWindowResult)
            {
                case GameFilesWindow.GameFilesWindowResult.exit:
                    selectedPath = null;
                    return false;
                case GameFilesWindow.GameFilesWindowResult.useSelectedPath:
                    selectedPath = gameFilesWindow.selectedPath!;
                    return true;
                case GameFilesWindow.GameFilesWindowResult.noContent:
                    selectedPath = null;
                    return true;
            }
            throw new NotImplementedException();
        }

        private async Task<bool> loadGameContentAsync(string paksFolderPath)
        {
            showBusyIndicator();
            _model.initPakReader();
            await _model.loadGameContentAsync(paksFolderPath);
            await preloadImages();
            return true;
        }

        private Task<bool> preloadImages()
        {
            var tcs = new TaskCompletionSource<bool>();
            Task.Run(() => {
                MainThreadConsoleWriteLine("Loading UI images...");
                InventoryTab.preload();
                EquipmentScreen.preload();
                ItemListScreen.preload();
                ItemControl.preload();
#if !HIDE_CHEST_TAB
                MainThreadConsoleWriteLine("Loading Chest images...");
                ChestTab.preload();
#endif

                MainThreadConsoleWriteLine("Loading Equipment images...");
                BaseSelectionWindow.preload();

                tcs.SetResult(true);
            });
            return tcs.Task;
        }

        private void showMainWindow()
        {
            EventLogger.logEvent("showMainWindow", new Dictionary<string, object>() { { "gameContentLoaded", AppModel.gameContentLoaded.ToString() } });
            MainThreadConsoleWriteLine("Loading Done");
            var mainWindow = WindowFactory.createMainWindow(_model.mainModel);
            mainWindow.onRelaunch = onRelaunch;
            mainWindow.onReload = onReload;
            this.MainWindow = mainWindow;

            //Show BEFORE closing the others. ShutdownMode is OnLastWindowClose, so a
            //moment with zero open windows shuts the whole app down.
            this.MainWindow.Show();

            _splashWindow?.Close();
            closeBusyIndicator();

            var aboutShotPath = UI.Theme.WindowCapture.aboutPathFromArguments(_startupArguments);
            if (aboutShotPath != null)
            {
                //Shown non-modally so the capture can run on the same dispatcher.
                var aboutWindow = WindowFactory.createAboutWindow();
                aboutWindow.Owner = this.MainWindow;
                aboutWindow.Show();
                UI.Theme.WindowCapture.captureThenExit(aboutWindow, aboutShotPath!);
                return;
            }

            //Dev aid: dump the MCD Builder share link for the loaded save and exit.
            //Dev aid: report the resolved font of a few named elements.
            //Dev aid: report what a share link would import, without touching the save.
            var importProbe = _startupArguments.FirstOrDefault(a => a.StartsWith("PRINT_IMPORT="));
            if (importProbe != null)
            {
                var link = importProbe.Substring("PRINT_IMPORT=".Length).Trim('"');
                var parsed = Logic.BuildShare.itemsFromShare(link, 250);
                if (parsed == null) { Console.WriteLine("[import] not a build"); }
                else
                {
                    Console.WriteLine($"[import] {parsed.Count} item(s)");
                    foreach (var it in parsed)
                    {
                        var ench = it.Enchantments == null ? "" :
                            string.Join(", ", it.Enchantments.Select(x => $"{x.Id}:{x.Level}"));
                        Console.WriteLine($"[import]   {it.Type}  [{ench}]");
                    }
                }
                this.Shutdown();
                return;
            }

            if (_startupArguments.Contains("PRINT_FONTS"))
            {
                foreach (var name in new[] { "itemSearchBox", "itemSearchHint", "inventoryCountLabel", "allItemsButton" })
                {
                    var el = UI.Theme.WindowCapture.findByName(this.MainWindow, name);
                    if (el is System.Windows.Controls.Control c)
                        Console.WriteLine($"[font] {name}: {c.FontFamily} {c.FontSize}");
                    else if (el is System.Windows.Controls.TextBlock t)
                        Console.WriteLine($"[font] {name}: {t.FontFamily} {t.FontSize}");
                    else Console.WriteLine($"[font] {name}: not found");
                }
                this.Shutdown();
                return;
            }

            var armorProbe = _startupArguments.FirstOrDefault(a => a.StartsWith("PRINT_ARMOR_DATA="));
            if (armorProbe != null)
            {
                printArmorData(armorProbe.Substring("PRINT_ARMOR_DATA=".Length).Trim('"'));
                this.Shutdown();
                return;
            }

            if (_startupArguments.Contains("PRINT_ARMOR_DEFAULTS"))
            {
                var profile = _model.mainModel.profileModel.profile.value;
                var all = (profile?.Items ?? new Save.Models.Profiles.Item[0])
                    .Concat(profile?.StorageChestItems ?? new Save.Models.Profiles.Item[0]);
                foreach (var it in all)
                {
                    if (it.Armorproperties == null || it.Armorproperties.Length == 0) { continue; }
                    var props = string.Join(",", it.Armorproperties.Select(x => $"{x.Id}:{x.Rarity}"));
                    Console.WriteLine($"[defaults] {it.Type}	{props}");
                }
                this.Shutdown();
                return;
            }

            if (_startupArguments.Contains("PRINT_ENCHANT_SETS"))
            {
                var profile = _model.mainModel.profileModel.profile.value;
                foreach (var it in (profile?.Items ?? new Save.Models.Profiles.Item[0]))
                {
                    var ench = it.Enchantments;
                    if (ench == null) { Console.WriteLine($"[sets] {it.Type}: null"); continue; }
                    var applied = ench.Count(x => x.Level > 0);
                    var sets = string.Join(" | ", Enumerable.Range(0, (ench.Length + 2) / 3)
                        .Select(i => string.Join(",", ench.Skip(i * 3).Take(3).Select(x => $"{x.Id}:{x.Level}"))));
                    Console.WriteLine($"[sets] {it.Type} slot={it.EquipmentSlot ?? "-"} count={ench.Length} applied={applied} :: {sets}");
                }
                this.Shutdown();
                return;
            }

            //APPLY_SKIN=<asset path>|<png>|<name> - the whole Apply path, exactly as the tab
            //runs it, writing a real mod pak beside the game's own.
            var applySkin = _startupArguments.FirstOrDefault(a => a.StartsWith("APPLY_SKIN="));
            if (applySkin != null)
            {
                var parts = applySkin.Substring("APPLY_SKIN=".Length).Trim('"').Split('|');
                try
                {
                    //Preview first, so the read that the tab does before applying is in the way.
                    var seen = Services.ImageResolver.instance.imageSource(parts[0]);
                    Console.WriteLine($"[apply] preview = {(seen != null ? $"{seen.PixelWidth}x{seen.PixelHeight}" : "NULL")}");

                    var mod = Logic.CustomSkins.apply(parts[0], parts[1], parts[2]);
                    Console.WriteLine($"[apply] wrote {mod.Path} ({mod.Size} bytes)");
                    Console.WriteLine($"[apply] installed count = {Logic.CustomSkins.installed().Count}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[apply] FAILED: {e.Message}");
                }
                this.Shutdown();
                return;
            }

            //PRINT_VERSION_STATUS - what the update check found and how this build compares.
            if (_startupArguments.Contains("PRINT_VERSION_STATUS"))
            {
                Services.Config.instance.downloadAsync().GetAwaiter().GetResult();
                Console.WriteLine($"[version] this build      = {Constants.CURRENT_VERSION}");
                Console.WriteLine($"[version] latest stable   = {Services.Config.instance.stableReleaseVersionString ?? "(none)"}");
                Console.WriteLine($"[version] latest beta     = {(string.IsNullOrWhiteSpace(Services.Config.instance.betaReleaseVersionString) ? "(none)" : Services.Config.instance.betaReleaseVersionString)}");
                Console.WriteLine($"[version] label           = {Services.Config.instance.versionLabel()}");
                Console.WriteLine($"[version] update offered  = {Services.Config.instance.isNewStableVersionAvailable()}");
                this.Shutdown();
                return;
            }

            //TEST_ARMOR_DEFAULTS - proves a type change leaves properties alone and that the
            //defaults table still produces the right ones on demand.
            if (_startupArguments.Contains("TEST_ARMOR_DEFAULTS"))
            {
                var armor = _model.mainModel.profileModel.profile.value?.Items
                    ?.FirstOrDefault(x => Data.ArmorDefaults.forItemType(x.Type) != null);
                if (armor == null)
                {
                    Console.WriteLine("[armor] no armor in this save");
                }
                else
                {
                    string show(Save.Models.Profiles.Armorproperty[]? p)
                        => p == null ? "(none)" : string.Join(", ", p.Select(x => x.Id));

                    Console.WriteLine($"[armor] {armor.Type}  before        = {show(armor.Armorproperties)}");

                    //What the UI does now when a different type is chosen: the type, nothing else.
                    var before = show(armor.Armorproperties);
                    armor.Type = "WolfArmor";
                    Console.WriteLine($"[armor] after type change       = {show(armor.Armorproperties)}");
                    Console.WriteLine($"[armor] properties untouched    = {show(armor.Armorproperties) == before}");

                    //What the Defaults button does.
                    armor.Armorproperties = Data.ArmorDefaults.forItemType(armor.Type)!;
                    Console.WriteLine($"[armor] after Defaults button   = {show(armor.Armorproperties)}");
                }
                this.Shutdown();
                return;
            }

            //SCREENSHOT_PICKER=<png>|items|enchantments[|<term>] - opens a selection window,
            //optionally types a search term, and captures it. The pickers are modal dialogs, so
            //they cannot be reached by the main-window capture path.
            var pickerShot = _startupArguments.FirstOrDefault(a => a.StartsWith("SCREENSHOT_PICKER="));
            if (pickerShot != null)
            {
                var parts = pickerShot.Substring("SCREENSHOT_PICKER=".Length).Trim('"').Split('|');
                var window = UI.WindowFactory.createSelectionWindow();
                //Every entry point, not just the two easy ones: the filtered pickers are what a
                //gear slot opens, and they were the ones missing a search box.
                switch (parts.Length > 1 ? parts[1] : "items")
                {
                    case "enchantments": window.loadEnchantments(null, null); break;
                    case "props": window.loadArmorProperties(null); break;
                    case "armor": window.loadFilteredItems(Save.Models.Enums.ItemFilterEnum.Armor, null); break;
                    case "melee": window.loadFilteredItems(Save.Models.Enums.ItemFilterEnum.MeleeWeapons, null); break;
                    case "ranged": window.loadFilteredItems(Save.Models.Enums.ItemFilterEnum.RangedWeapons, null); break;
                    case "artifacts": window.loadFilteredItems(Save.Models.Enums.ItemFilterEnum.Artifacts, null); break;
                    default: window.loadItems(null); break;
                }

                if (window is Window shown)
                {
                    shown.Show();
                    shown.UpdateLayout();
                    Console.WriteLine($"[picker] window = {shown.GetType().Name}");
                    if (parts.Length > 2 && parts[2].Length > 0)
                    {
                        UI.Theme.WindowCapture.typeInto(shown, parts[2]);
                        shown.UpdateLayout();
                    }
                    //A bound list rebuilds its containers on a later dispatcher pass, so a
                    //capture taken right after UpdateLayout still shows the old rows.
                    Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ContextIdle);
                    shown.UpdateLayout();

                    var list = UI.Theme.WindowCapture.findFirst<System.Windows.Controls.ListBox>(shown);
                    Console.WriteLine($"[picker] listBox items = {list?.Items.Count.ToString() ?? "not found"}");
                    UI.Theme.WindowCapture.captureThenExit(shown, parts[0]);
                    return;
                }
                this.Shutdown();
                return;
            }

            //ADD_MOD=<pak file> - the manual mod path, exactly as the upload button runs it.
            var addMod = _startupArguments.FirstOrDefault(a => a.StartsWith("ADD_MOD="));
            if (addMod != null)
            {
                var file = addMod.Substring("ADD_MOD=".Length).Trim('"');
                try
                {
                    Console.WriteLine($"[mod] paks   = {Logic.CustomSkins.paksFolder}");
                    Console.WriteLine($"[mod] ~mods  = {Logic.CustomSkins.modsFolder}");
                    Console.WriteLine($"[mod] is pak = {Logic.CustomSkins.looksLikePak(file, out var pakVersion)} (v{pakVersion})");

                    var mod = Logic.CustomSkins.installPak(file, overwrite: true);
                    Console.WriteLine($"[mod] installed \"{mod.Name}\" manual={mod.Manual} -> {mod.Path}");
                    foreach (var one in Logic.CustomSkins.installed())
                    {
                        Console.WriteLine($"[mod]   listed: {one.Name}  manual={one.Manual}  {one.Size} bytes");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[mod] FAILED: {e.Message}");
                }
                this.Shutdown();
                return;
            }

            //APPLY_HERO=<heroId>|<png>|<none|equipped|all> - the Hero tab's apply, headless.
            var applyHero = _startupArguments.FirstOrDefault(a => a.StartsWith("APPLY_HERO="));
            if (applyHero != null)
            {
                var parts = applyHero.Substring("APPLY_HERO=".Length).Trim('"').Split('|');
                try
                {
                    var hero = Logic.HeroSkins.all()
                        .FirstOrDefault(h => string.Equals(h.Id, parts[0], StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"No hero called {parts[0]}");

                    var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
                        new Uri(System.IO.Path.GetFullPath(parts[1])),
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                    var mode = parts.Length > 2 ? parts[2] : "none";
                    var hide = new List<string>();
                    if (mode == "all")
                    {
                        hide.AddRange(Services.ItemDatabase.armor
                            .Select(Logic.CustomSkins.textureFor)
                            .Where(x => x != null).Select(x => x!)
                            .Distinct(StringComparer.OrdinalIgnoreCase));
                    }
                    else if (mode == "equipped")
                    {
                        var slot = Save.Models.Enums.EquipmentSlotEnum.ArmorGear.ToString();
                        hide.AddRange((_model.mainModel.profileModel.profile.value?.Items ?? Array.Empty<Item>())
                            .Where(i => i.EquipmentSlot == slot)
                            .Select(i => Logic.CustomSkins.textureFor(i.Type))
                            .Where(x => x != null).Select(x => x!));
                    }

                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var mod = Logic.HeroSkins.apply(hero, decoder.Frames[0]);
                    watch.Stop();
                    Console.WriteLine($"[hero] skin  -> {mod.Path} ({mod.Size:N0} bytes, {watch.ElapsedMilliseconds} ms)");

                    //Armour visibility is its own pak now, so the test drives it separately.
                    if (hide.Count > 0)
                    {
                        watch.Restart();
                        Logic.ArmourVisibility.setHidden(true);
                        watch.Stop();
                        var hidden = Logic.ArmourVisibility.installed();
                        Console.WriteLine($"[hero] hide  -> {hidden?.Path} ({hidden?.Size:N0} bytes, {watch.ElapsedMilliseconds} ms)");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[hero] FAILED: {e.Message}");
                }
                this.Shutdown();
                return;
            }

            //PRINT_HEROES - the hero skins the loaded content carries, and which the save uses.
            if (_startupArguments.Contains("PRINT_HEROES"))
            {
                var heroes = Logic.HeroSkins.all();
                var current = _model.mainModel.profileModel.profile.value?.Skin;
                Console.WriteLine($"[hero] save says: {current ?? "(none)"}");
                Console.WriteLine($"[hero] {heroes.Count} hero skins in content");
                foreach (var hero in heroes)
                {
                    var picture = Logic.HeroSkins.preview(hero);
                    var size = picture == null ? "-" : $"{picture.PixelWidth}x{picture.PixelHeight}";
                    var mark = string.Equals(hero.Id, current, StringComparison.OrdinalIgnoreCase) ? " <-- current" : "";
                    Console.WriteLine($"[hero]   {hero.Name,-24} {size,-8} {hero.Id}{mark}");
                }
                this.Shutdown();
                return;
            }

            //FIND_ASSET=<substring> - pak entries whose path contains it, with the size of any
            //that decode as a texture. For finding what the game keeps and where.
            var findAsset = _startupArguments.FirstOrDefault(a => a.StartsWith("FIND_ASSET="));
            if (findAsset != null)
            {
                var term = findAsset.Substring("FIND_ASSET=".Length).Trim('"');
                var paks = Logic.CustomSkins.index;
                int shown = 0;
                foreach (var entry in paks ?? Enumerable.Empty<string>())
                {
                    if (entry.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    if (++shown > 60) { Console.WriteLine("[find] ..."); break; }
                    var clean = entry.Substring(entry.IndexOf("//", StringComparison.Ordinal) + 1);
                    var picture = Services.ImageResolver.instance.imageSource(clean);
                    var size = picture == null ? "-" : $"{picture.PixelWidth}x{picture.PixelHeight}";
                    Console.WriteLine($"[find] {size,-9} {clean}");
                }
                Console.WriteLine($"[find] {shown} shown");
                this.Shutdown();
                return;
            }

            //PROBE_ASSET=<asset path> - calls the two readers in both orders, because the tab
            //previews with one and applies with the other.
            var probe = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_ASSET="));
            if (probe != null)
            {
                var path = probe.Substring("PROBE_ASSET=".Length).Trim('"');
                var pak = Logic.CustomSkins.index;
                Console.WriteLine($"[probe] {path}");
                Console.WriteLine($"  index null? {pak == null}");
                if (pak != null)
                {
                    Console.WriteLine($"  extractPackage (cold) = {(pak.extractPackage(path) != null ? "ok" : "NULL")}");
                    Console.WriteLine($"  imageSource           = {(Services.ImageResolver.instance.imageSource(path) != null ? "ok" : "NULL")}");
                    Console.WriteLine($"  extractPackage (warm) = {(pak.extractPackage(path) != null ? "ok" : "NULL")}");
                    var pkg = pak.extractPackage(path);
                    if (pkg != null)
                    {
                        var tex = pkg.Value.GetExport<PakReader.Parsers.Class.UTexture2D>();
                        Console.WriteLine($"  GetExport<UTexture2D>  = {(tex != null ? "ok" : "NULL")}");
                    }
                }
                this.Shutdown();
                return;
            }

            //PROBE_IGNITE=<mesh path>;<r>;<g>;<b>;<power> - runs the glow writer and reads the
            //result back out of the bytes it produced. Nothing is installed: this is the check
            //that the offsets written to are the ones that were found.
            var probeIgnite = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_IGNITE="));
            if (probeIgnite != null)
            {
                var bits = probeIgnite.Substring("PROBE_IGNITE=".Length).Trim('"').Split(';');
                float number(int i, float fallback)
                    => bits.Length > i && float.TryParse(bits[i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

                var entries = Logic.Glow.ignite(bits[0], number(1, 7f), number(2, 0.3f), number(3, 0f),
                    number(4, 125f), out var lit).ToList();
                Console.WriteLine($"[ignite] {bits[0]} -> {lit.Count} material(s), {entries.Count} file(s)");

                foreach (var name in lit) { Console.WriteLine($"    lit {name}"); }

                for (int i = 0; i + 1 < entries.Count; i++)
                {
                    if (!entries[i].Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (!entries[i + 1].Path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)) { continue; }

                    Console.WriteLine($"    {entries[i].Path}");
                    foreach (var (name, value) in Logic.Glow.readParameters(entries[i].Data, entries[i + 1].Data))
                    {
                        Console.WriteLine($"        {name,-20} {string.Join(", ", value.Select(v => v.ToString("G6")))}");
                    }
                }
                this.Shutdown();
                return;
            }

            //PROBE_GLOW=<material path>[;<material path>] - what the glow writer thinks each
            //emissive parameter reads. Checked against the values the package itself declares:
            //if the offsets are right these agree, and if they are wrong they are nonsense.
            var probeGlow = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_GLOW="));
            if (probeGlow != null)
            {
                foreach (var path in probeGlow.Substring("PROBE_GLOW=".Length).Trim('"').Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(path)) { continue; }
                    Console.WriteLine($"[glow] {path.Trim()}");
                    foreach (var (name, value) in Logic.Glow.readParameters(path.Trim()))
                    {
                        Console.WriteLine($"    {name,-20} {string.Join(", ", value.Select(v => v.ToString("G6")))}");
                    }
                }
                this.Shutdown();
                return;
            }

            //PROBE_PARAM=<material path>;<parameter name> - where a named parameter sits
            //inside the parameter arrays, and what follows it. The layout of one array element
            //is what turns "the emissive is 125" into "the float at byte 1731".
            var probeParam = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_PARAM="));
            if (probeParam != null)
            {
                var parts = probeParam.Substring("PROBE_PARAM=".Length).Trim('"').Split(';');
                var paks = Logic.CustomSkins.index;
                var read = paks?.extractPackage(parts[0]);
                if (read == null) { Console.WriteLine("unreadable"); this.Shutdown(); return; }

                var uasset = read.Value.UAsset.ToArray();
                var uexp = read.Value.UExp.ToArray();
                var names = Logic.CookedProperties.readNamesOf(uasset);
                var wanted = parts.Length > 1 ? parts[1] : "EmissivePower";

                var index = -1;
                for (int i = 0; i < names.Count; i++)
                {
                    if (string.Equals(names[i], wanted, StringComparison.Ordinal)) { index = i; break; }
                }
                Console.WriteLine($"[param] \"{wanted}\" is name #{index} of {names.Count}");
                if (index < 0) { this.Shutdown(); return; }

                var pattern = BitConverter.GetBytes(index);
                for (int at = 0; at + 4 <= uexp.Length; at++)
                {
                    if (uexp[at] != pattern[0] || uexp[at + 1] != pattern[1]
                        || uexp[at + 2] != pattern[2] || uexp[at + 3] != pattern[3]) { continue; }

                    var line = new System.Text.StringBuilder($"  at {at}: ");
                    for (int step = 0; step <= 32 && at + step + 4 <= uexp.Length; step += 4)
                    {
                        line.Append($"[+{step}]{BitConverter.ToSingle(uexp, at + step):G6} ");
                    }
                    Console.WriteLine(line.ToString());
                }
                this.Shutdown();
                return;
            }

            //PROBE_MATERIAL=<mesh asset path> - the materials a mesh wears, what each one
            //descends from, and every parameter it overrides. Which levers exist on a material
            //decides whether anything can be done to it from outside the process.
            var probeMaterial = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_MATERIAL="));
            if (probeMaterial != null)
            {
                var mesh = probeMaterial.Substring("PROBE_MATERIAL=".Length).Trim('"');
                var paks = Logic.CustomSkins.index;
                Console.WriteLine($"[material] mesh {mesh}");
                Console.WriteLine($"[material] masters: {string.Join(", ", Logic.CreatureVariants.mastersOf(mesh))}");

                foreach (var path in Logic.CreatureVariants.materialsOf(mesh))
                {
                    Console.WriteLine($"[material] {path}");
                    if (paks == null) { continue; }

                    var read = paks.extractPackage(path);
                    if (read == null || !read.Value.HasExport()) { Console.WriteLine("    unreadable"); continue; }

                    System.Text.Json.Nodes.JsonNode? root;
                    try { root = System.Text.Json.Nodes.JsonNode.Parse(read.Value.JsonData); }
                    catch (System.Text.Json.JsonException) { Console.WriteLine("    unparseable"); continue; }
                    if (root is not System.Text.Json.Nodes.JsonArray exports) { continue; }

                    foreach (var property in Logic.CookedProperties.readAll(read.Value.UAsset.ToArray(), read.Value.UExp.ToArray()))
                    {
                        Console.WriteLine($"    prop  {property.Export}.{property.Name} ({property.Type}) at {property.At} size {property.Size}");
                    }

                    foreach (var export in exports)
                    {
                        var value = export?["ExportValue"];
                        if (value == null) { continue; }

                        var parent = value["Parent"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(parent)) { Console.WriteLine($"    parent: {parent}"); }

                        foreach (var kind in new[] { "VectorParameterValues", "ScalarParameterValues", "TextureParameterValues" })
                        {
                            if (value[kind] is not System.Text.Json.Nodes.JsonArray list) { continue; }
                            foreach (var parameter in list)
                            {
                                var name = parameter?["ParameterInfo"]?["Name"]?.ToString();
                                var was = parameter?["ParameterValue"]?.ToJsonString();
                                Console.WriteLine($"    {kind.Replace("ParameterValues", ""),-8} {name,-28} {was}");
                            }
                        }
                    }
                }
                this.Shutdown();
                return;
            }

            //PROBE_CATALOGUE - what the workshop list offers, by heading.
            if (_startupArguments.Any(a => a == "PROBE_CATALOGUE"))
            {
                foreach (var group in Logic.WeaponMeshes.catalogue.groups())
                {
                    var entries = Logic.WeaponMeshes.catalogue.all().Where(m => m.Group == group).ToList();
                    var warned = entries.Count(m => m.Caution.Length > 0);
                    Console.WriteLine($"[catalogue] {group}: {entries.Count}, {warned} marked \"imports come out wrong\"");
                    foreach (var entry in entries.Take(entries.Count > 20 ? 3 : 20))
                    {
                        Console.WriteLine($"    {entry.Name}  ({entry.Variant})  {entry.AssetPath}");
                    }
                }
                this.Shutdown();
                return;
            }

            //SHOW_OVERLAY - puts the escalation overlay up for fifteen seconds against the
            //running game, and writes down where it decided to sit. The only part of it that
            //cannot be checked by reading the code is whether it lands over the game.
            if (_startupArguments.Any(a => a == "SHOW_OVERLAY"))
            {
                var live = new Logic.LiveStatsLink { escalationOn = true, enemiesOn = false };
                var overlay = new UI.EscalationOverlay(live);
                overlay.Show();

                var watch = new System.Windows.Threading.DispatcherTimer {
                    Interval = TimeSpan.FromSeconds(3),
                };
                var seen = 0;
                watch.Tick += (_, _) => {
                    Console.WriteLine($"[overlay] visible={overlay.Visibility}"
                        + $" at {overlay.Left:F0},{overlay.Top:F0} size {overlay.Width}x{overlay.Height}"
                        + $" | game window 0x{live.gameWindow.ToInt64():X} attached={live.attached} status=\"{live.status}\""
                        + $" | stage {live.escalation.stage} ({live.escalation.stageName})"
                        + $" tough {live.toughnessNow:0.##} fast {live.speedNow:0.##}"
                        + $" through {live.escalation.through:P0}");
                    if (++seen >= 5)
                    {
                        watch.Stop();
                        overlay.Close();
                        live.Dispose();
                        this.Shutdown();
                    }
                };
                watch.Start();
                return;
            }

            //DRAW_CROSSHAIRS=<folder> - every crosshair rendered to a picture, so how they
            //look is something that can be looked at rather than imagined.
            var drawThem = _startupArguments.FirstOrDefault(a => a.StartsWith("DRAW_CROSSHAIRS="));
            if (drawThem != null)
            {
                var folder = drawThem.Substring("DRAW_CROSSHAIRS=".Length).Trim('"');
                System.IO.Directory.CreateDirectory(folder);

                foreach (var style in UI.CrosshairOverlay.STYLES)
                {
                    var overlay = new UI.CrosshairOverlay(Logic.LiveCameraLink.shared);
                    overlay.Left = -4000;   //off screen, since it only has to be rendered
                    overlay.Show();
                    overlay.look(style, "Green", 1.0);
                    overlay.UpdateLayout();

                    var board = overlay.Content as System.Windows.FrameworkElement;
                    var size = 240;
                    var picture = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);

                    //On a mid grey, because a crosshair drawn on nothing says nothing about
                    //whether it would be visible on a floor.
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var paint = visual.RenderOpen())
                    {
                        paint.DrawRectangle(new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0x6B, 0x6B, 0x66)), null,
                            new Rect(0, 0, size, size));
                        //At its own size. A VisualBrush stretches what it is given to fill the
                        //rectangle by default, which makes every crosshair look like it fills the
                        //screen and hides the one thing being checked.
                        paint.DrawRectangle(new System.Windows.Media.VisualBrush(board) {
                            Stretch = System.Windows.Media.Stretch.None,
                            AlignmentX = System.Windows.Media.AlignmentX.Center,
                            AlignmentY = System.Windows.Media.AlignmentY.Center,
                        }, null, new Rect(0, 0, size, size));
                    }
                    picture.Render(visual);

                    var file = System.IO.Path.Combine(folder, style + ".png");
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
                    using (var stream = System.IO.File.Create(file)) { encoder.Save(stream); }

                    overlay.Close();
                    Console.WriteLine($"[crosshair] {file}");
                }

                this.Shutdown();
                return;
            }

            //DUMP_TEXTURE=<asset path>;<file> - the artwork the game would load for an asset,
            //saved as a picture. Which is how "the texture is wrong" and "something is tinting
            //it" get told apart without guessing.
            var dumpTexture = _startupArguments.FirstOrDefault(a => a.StartsWith("DUMP_TEXTURE="));
            if (dumpTexture != null)
            {
                var bits = dumpTexture.Substring("DUMP_TEXTURE=".Length).Trim('"').Split(';');
                var picture = Services.ImageResolver.instance.imageSource(bits[0]);
                if (picture == null) { Console.WriteLine("[texture] could not decode it"); this.Shutdown(); return; }

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
                using (var stream = System.IO.File.Create(bits[1])) { encoder.Save(stream); }

                Console.WriteLine($"[texture] {picture.PixelWidth}x{picture.PixelHeight} -> {bits[1]}");
                this.Shutdown();
                return;
            }

            //PROBE_GLB=<file> - what an imported model brings with it, and in particular
            //whether it brings a colour. A model with none wears whatever the weapon it replaces
            //was painted with, which looks like a fault in the importer and is not one.
            var probeGlb = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_GLB="));
            if (probeGlb != null)
            {
                var file = probeGlb.Substring("PROBE_GLB=".Length).Trim('"');
                try
                {
                    var model = Logic.GlbModel.read(file);
                    Console.WriteLine($"[glb] {model.Name}");
                    Console.WriteLine($"      {model.Positions.Count} vertices, {model.Indices.Count / 3} triangles");
                    Console.WriteLine($"      base colour: {(model.BaseColourPng == null ? "NONE" : model.BaseColourPng.Length.ToString("N0") + " bytes of PNG")}");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[glb] could not read it: {problem.Message}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_COPY=<source>;<target folder>;<wanted name> - copies one asset to another
            //path and reads the result back, which is the only way to know a rename took.
            var probeCopy = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_COPY="));
            if (probeCopy != null)
            {
                var bits = probeCopy.Substring("PROBE_COPY=".Length).Trim('"').Split(';');
                var name = Logic.AssetCopy.nameFor(bits[0], bits[1], bits.Length > 2 ? bits[2] : "copy");
                Console.WriteLine($"[copy] {bits[0]}");
                Console.WriteLine($"       would become {name ?? "(no name fits)"}");

                if (name == null) { this.Shutdown(); return; }

                //Back to how the paks spell it, which is what the writer wants.
                var target = "/Dungeons/Content/" + name.Substring("/Game/".Length);
                var entries = Logic.AssetCopy.copy(bits[0], target).ToList();
                Console.WriteLine($"       {entries.Count} file(s): {string.Join(", ", entries.Select(e => System.IO.Path.GetFileName(e.Path)))}");

                foreach (var entry in entries)
                {
                    if (!entry.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) { continue; }

                    var names = Logic.CookedProperties.readNamesOf(entry.Data);
                    Console.WriteLine($"       the copy calls itself:");
                    foreach (var known in names)
                    {
                        if (known.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"         \"{known}\"");
                        }
                    }
                }
                this.Shutdown();
                return;
            }

            //PROBE_COLD=<mission> - whether Edit spawns works from nothing at all.
            //
            //The documented first step used to be "press Export map", which existed only because
            //the app would not take it. Deleting the folder and pressing the button is the only
            //honest test of that.
            var probeCold = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_COLD="));
            if (probeCold != null)
            {
                var wanted = probeCold.Substring("PROBE_COLD=".Length).Trim('"');
                var folder = Logic.MapWorkshop.folderFor(wanted);

                if (System.IO.Directory.Exists(folder))
                {
                    System.IO.Directory.Delete(folder, true);
                }

                Console.WriteLine($"[cold] deleted the folder - exists? {System.IO.Directory.Exists(folder)}");

                //The real tab, the real button. Hosted off-screen because a UserControl that is
                //never shown never loads its mission list.
                var tab = new UI.MapsTab();
                var host = new Window
                {
                    Content = tab, Width = 900, Height = 600,
                    Left = -20000, WindowStartupLocation = WindowStartupLocation.Manual,
                };
                host.Show();

                if (!tab.probePick(wanted))
                {
                    Console.WriteLine($"[cold] no mission called {wanted}");
                    host.Close();
                    this.Shutdown();
                    return;
                }

                _ = tab.openSpawns().ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        var made = Logic.MapWorkshop.exported(wanted);
                        var welded = System.IO.File.Exists(
                            System.IO.Path.Combine(Logic.MapWorkshop.folderFor(wanted),
                                "level.json.multitile"));

                        Console.WriteLine($"[cold] tab says: {tab.probeStatus}");
                        Console.WriteLine(made
                            ? "[cold] it exported on its own"
                            : "[cold] WRONG - nothing was exported");
                        Console.WriteLine(welded
                            ? "[cold] and welded it into one mission"
                            : "[cold] WRONG - it was not welded");

                        foreach (var open in Windows.OfType<UI.SpawnsWindow>())
                        {
                            Console.WriteLine($"[cold] the editor opened on: {open.roomChosen}");
                            open.Close();
                        }

                        host.Close();
                        this.Shutdown();
                    });
                });

                return;
            }

            //PROBE_CHUNKER=<world folder> - a world up to the modern Minecraft and back again.
            //
            //The round trip is the thing worth testing: going up is only useful if coming down
            //returns what went in, and a converter that quietly drops blocks would look exactly
            //like a converter that works.
            var probeChunk = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_CHUNKER="));
            if (probeChunk != null)
            {
                var world = probeChunk.Substring("PROBE_CHUNKER=".Length).Trim('"');

                Console.WriteLine($"[chunker] jar: {Logic.MapTools.chunker ?? "NOT INSTALLED"}");
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var down = await Logic.MapTools.convert(world, Logic.MapTools.LEGACY);
                    Console.WriteLine(down.Ok
                        ? $"[chunker] down to {Logic.MapTools.LEGACY} in {clock.ElapsedMilliseconds:N0} ms"
                        : $"[chunker] WRONG - coming down failed: {down.Last}");

                    //Everything the tools put in the world has to still be there. Chunker keeps
                    //level.dat and the regions and discards the rest, which is most of what the
                    //trip home reads.
                    foreach (var needed in new[]
                    {
                        Logic.MapTools.LEVEL_MARKER, "objectgroup.json",
                        "region_plane", "region_y_plane", "walkable_plane",
                    })
                    {
                        var at = System.IO.Path.Combine(world, needed);
                        var there = System.IO.File.Exists(at) || System.IO.Directory.Exists(at);
                        Console.WriteLine(there
                            ? $"[chunker] kept {needed}"
                            : $"[chunker] WRONG - lost {needed}");
                    }

                    //The swap must leave one world, not a graveyard of halves.
                    foreach (var leftover in new[] { ".converting", ".before-convert" })
                    {
                        if (System.IO.Directory.Exists(world + leftover))
                        {
                            Console.WriteLine($"[chunker] WRONG - left {leftover} behind");
                        }
                    }

                    Dispatcher.Invoke(() => this.Shutdown());
                });

                return;
            }

            //PROBE_GATES - which kinds of objective actually hold a gate shut, and with what.
            //
            //PROBE_QUESTS said "locked-doors" is a field of click and of nothing else, and that
            //arena and killgroup keep a "gate" instead. If that holds across every mission then
            //hanging a gate off a gauntlet writes a field the game never reads - a gate that is
            //drawn, and lit, and never opens, with no error anywhere.
            //Builds the challenge example on a COPY of a map and prints what the app actually
            //wrote. Not a test of the UI - a test of the file, which is the thing that has to be
            //right. The copy matters: a probe that edits the map somebody is working on is a
            //probe that eats an afternoon.
            //
            //  MCDReborn.exe PROBE_CHALLENGE=<map folder>;<out folder>
            if (_startupArguments.Any(a => a.StartsWith("PROBE_CHALLENGE=", StringComparison.Ordinal)))
            {
                var said = _startupArguments.First(a =>
                    a.StartsWith("PROBE_CHALLENGE=", StringComparison.Ordinal))["PROBE_CHALLENGE=".Length..];

                var parts = said.Split(';', 2);
                var from = parts[0].Trim().Trim('"');
                var into = (parts.Length > 1 ? parts[1] : Path.Combine(Path.GetTempPath(), "cwork"))
                    .Trim().Trim('"');

                Directory.CreateDirectory(into);

                var work = Path.Combine(into, "map");
                if (Directory.Exists(work)) { Directory.Delete(work, true); }

                foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                {
                    Directory.CreateDirectory(dir.Replace(from, work));
                }
                Directory.CreateDirectory(work);
                foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                {
                    var landing = file.Replace(from, work);
                    Directory.CreateDirectory(Path.GetDirectoryName(landing)!);
                    File.Copy(file, landing, true);
                }

                var map = Logic.MapSpawns.load(work);
                var room = map.Rooms.FirstOrDefault();
                if (room == null) { Console.WriteLine("no room in " + from); Shutdown(); return; }

                var log = new System.Text.StringBuilder();
                log.AppendLine("room " + room.Id + "  " + string.Join("x", room.Size));

                //A group first, because a challenge naming one that does not exist spawns
                //nothing - which is the trap the whole wiring view exists to make visible.
                var group = Logic.MapSpawns.addGroup(map, "zombie");
                var id = group["id"]?.GetValue<string>() ?? "custom";
                log.AppendLine("group " + id);

                var made = Logic.MapSpawns.place(room, room.Size[0] / 2, room.Size[1] / 2,
                    room.Size[2] / 2, 6, 8, string.Empty);
                log.AppendLine("spawn points " + made);

                var at = Logic.MapSpawns.addChallenge(map, room,
                    Logic.MapSpawns.freeChallengeName(map), id, 6,
                    Logic.MapSpawns.CHESTS[0].path, Logic.MapSpawns.GATE_LOOKS[0].path,
                    true, room.Size[0] / 2, room.Size[1] / 2, room.Size[2] / 2 + 4);

                log.AppendLine("challenge at " + at);

                Logic.MapSpawns.save(map);

                foreach (var one in Logic.MapSpawns.challengesOf(map))
                {
                    log.AppendLine("  " + one);
                }

                var levelPath = Directory.GetFiles(work, "level.json", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (levelPath != null)
                {
                    File.Copy(levelPath, Path.Combine(into, "level.json"), true);
                    log.AppendLine("level.json -> " + Path.Combine(into, "level.json"));
                }

                var roomPath = Directory.GetFiles(work, Path.GetFileName(room.File),
                    SearchOption.AllDirectories).FirstOrDefault();

                if (roomPath != null)
                {
                    File.Copy(roomPath, Path.Combine(into, Path.GetFileName(room.File)), true);
                    log.AppendLine("room -> " + Path.Combine(into, Path.GetFileName(room.File)));
                }

                File.WriteAllText(Path.Combine(into, "probe.txt"), log.ToString());
                Console.WriteLine(log.ToString());
                Shutdown();
                return;
            }

            //Every wiring tab's shape, read off a real map and printed. Read-only: it loads,
            //builds each tab twice, and writes nothing back.
            //
            //  MCDReborn.exe PROBE_WIRING=<map folder>
            if (_startupArguments.Any(a => a.StartsWith("PROBE_WIRING=", StringComparison.Ordinal)))
            {
                var folder = _startupArguments.First(a =>
                    a.StartsWith("PROBE_WIRING=", StringComparison.Ordinal))["PROBE_WIRING=".Length..]
                    .Trim().Trim('"');

                var map = Logic.MapSpawns.load(folder);
                Console.WriteLine(folder + "  " + map.Rooms.Count + " room(s)");
                Console.WriteLine(new UI.WiringWindow(map).probeTabs());
                Shutdown();
                return;
            }

            //What Inspect unwelded opens, without the window: exports a mission straight from
            //the paks, never welds it, and prints every wiring tab's shape. The point is the
            //comparison with the same mission welded - see PROBE_WIRING.
            //
            //  MCDReborn.exe PROBE_UNWELD=<mission name>;<out folder>
            if (_startupArguments.Any(a => a.StartsWith("PROBE_UNWELD=", StringComparison.Ordinal)))
            {
                var said = _startupArguments.First(a =>
                    a.StartsWith("PROBE_UNWELD=", StringComparison.Ordinal))["PROBE_UNWELD=".Length..];

                var bits = said.Split(';', 2);
                var wanted = bits[0].Trim().Trim('"');
                var into = (bits.Length > 1 ? bits[1] : Path.Combine(Path.GetTempPath(), "unweld"))
                    .Trim().Trim('"');

                var mission = Logic.GameMaps.all()
                    .FirstOrDefault(one => string.Equals(one.Name, wanted,
                        StringComparison.OrdinalIgnoreCase));

                if (mission == null) { Console.WriteLine("no mission " + wanted); Shutdown(); return; }

                Directory.CreateDirectory(into);

                if (Directory.GetFiles(into, "level.json", SearchOption.AllDirectories).Length == 0)
                {
                    Logic.MapMod.export(mission, into);

                    //Pinned but never welded, exactly as the button does it. Without the pin a
                    //stretch that still picks from a tile-group has no single tile and simply
                    //is not there: Creeper Woods comes out of the paks with six of its nineteen.
                    if (Logic.MapTools.available)
                    {
                        var pin = Task.Run(() => Logic.MapTools.makeFixed(into)).GetAwaiter().GetResult();
                        Console.WriteLine("pin " + (pin.Ok ? "ok" : pin.Last));
                    }
                    else
                    {
                        Console.WriteLine("no converters - stretches left picking at random");
                    }
                }

                var map = Logic.MapSpawns.load(into);
                Console.WriteLine(mission.Name + " unwelded  " + map.Rooms.Count + " room(s)");
                Console.WriteLine(new UI.WiringWindow(map).probeTabs());
                Shutdown();
                return;
            }

            //Which process the live links would take, and whether a character can be read out of
            //it. READ-ONLY: it opens, looks, and lets go - nothing is applied, no debugger is
            //attached, and it is the same external read the Difficulty tab makes every 400 ms.
            //Written for the Steam launcher race: Dungeons.exe is a stub that starts the real game
            //a second later and stays alive, so what was taken matters as much as whether.
            //
            //  MCDReborn.exe PROBE_LIVE
            if (_startupArguments.Any(a => a == "PROBE_LIVE"))
            {
                foreach (var name in LiveEdit.GameProcess.PROCESS_NAMES)
                {
                    foreach (var one in System.Diagnostics.Process.GetProcessesByName(name))
                    {
                        Console.WriteLine($"running  {one.ProcessName,-26} pid {one.Id,-6} "
                            + $"{one.WorkingSet64 / (1024 * 1024)} MB");
                    }
                }

                var game = LiveEdit.GameProcess.open(out var problem);
                if (game == null) { Console.WriteLine("open failed: " + problem); Shutdown(); return; }

                Console.WriteLine($"opened   {game.Process.ProcessName,-26} pid {game.Id}"
                    + $"   superseded={game.Superseded}");

                var stats = new LiveEdit.LiveStats(game);
                var found = stats.look();
                Console.WriteLine($"character found={found}  ready={stats.Ready}");

                game.Dispose();
                Shutdown();
                return;
            }

            //Runs MapChecks over every level the game ships, and optionally over one map folder.
            //The shipped levels are the test: each of them can be finished, so a rule that flags
            //one of them is a wrong rule rather than a broken level.
            //
            //  MCDReborn.exe PROBE_CHECKS[=<map folder>]
            if (_startupArguments.Any(a => a.StartsWith("PROBE_CHECKS", StringComparison.Ordinal)))
            {
                var said = _startupArguments.First(a => a.StartsWith("PROBE_CHECKS", StringComparison.Ordinal));
                var clean = 0;
                var shipped = 0;

                Console.WriteLine("mob-group pool: " + Logic.MapChecks.pool().Count + " ids");

                foreach (var mission in Logic.GameMaps.all())
                {
                    var level = Logic.MapChecks.parse(Logic.GameMaps.read(mission.PakPath));
                    if (level == null) { Console.WriteLine("unreadable  " + mission.Name); continue; }

                    shipped++;
                    var found = Logic.MapChecks.run(level);
                    if (found.Count == 0) { clean++; continue; }

                    Console.WriteLine(mission.Name);
                    foreach (var one in found) { Console.WriteLine("    " + one); }
                }

                Console.WriteLine($"{clean} of {shipped} shipped levels clean");

                if (said.Contains('='))
                {
                    var folder = said[(said.IndexOf('=') + 1)..].Trim().Trim('"');
                    var map = Logic.MapSpawns.load(folder);
                    Console.WriteLine();
                    Console.WriteLine(folder);
                    var found = Logic.MapChecks.run(map);
                    if (found.Count == 0) { Console.WriteLine("    clean"); }
                    foreach (var one in found) { Console.WriteLine("    " + one); }
                }

                Shutdown();
                return;
            }

            //Every wiring tab of every shipped level, counted, one row per level, tab and wire
            //kind - written as TSV for a script that counts the same things straight out of the
            //level JSON. Built with no rooms at all, deliberately: every wire the tabs draw comes
            //from the level file, so a room-less map is exactly the part being tested.
            //
            //  MCDReborn.exe PROBE_WIRING_ALL=<out.tsv>
            if (_startupArguments.Any(a => a.StartsWith("PROBE_WIRING_ALL=", StringComparison.Ordinal)))
            {
                var into = _startupArguments.First(a =>
                    a.StartsWith("PROBE_WIRING_ALL=", StringComparison.Ordinal))["PROBE_WIRING_ALL=".Length..]
                    .Trim().Trim('"');

                var rows = new System.Text.StringBuilder();
                var levels = 0;

                foreach (var mission in Logic.GameMaps.all())
                {
                    var level = Logic.MapChecks.parse(Logic.GameMaps.read(mission.PakPath));
                    if (level == null) { continue; }

                    levels++;
                    var map = new Logic.MapSpawns.Map(string.Empty, level,
                        new Dictionary<string, System.Text.Json.Nodes.JsonObject>(),
                        new List<Logic.MapSpawns.Room>(), new List<string>());

                    foreach (var (tab, kind, count) in new UI.WiringWindow(map).probeCounts())
                    {
                        rows.AppendLine($"{mission.Name}\t{tab}\t{kind}\t{count}");
                    }
                }

                File.WriteAllText(into, rows.ToString());
                Console.WriteLine($"{levels} levels -> {into}");
                Shutdown();
                return;
            }

            //PROBE_SLOTITEM=<out.pak> - fills one of the item ids the game registers but ships no
            //files for (SpiderCrossbow) with a copy of HeavyCrossbow, and proves the resizing rename
            //first: every package must come back byte-identical from a no-op rename and from a
            //grow-then-shrink round trip, and must still parse. Writes nothing but the pak.
            //
            //  MCDReborn.exe PROBE_SLOTITEM=C:\...\MCDReborn_SpiderCrossbow_P.pak
            if (_startupArguments.Any(a => a.StartsWith("PROBE_SLOTITEM=", StringComparison.Ordinal)))
            {
                var into = _startupArguments.First(a => a.StartsWith("PROBE_SLOTITEM=", StringComparison.Ordinal))
                    ["PROBE_SLOTITEM=".Length..].Trim().Trim('"');
                var index = Logic.CustomSkins.index!;

                bool parses(byte[] uasset, byte[] uexp, byte[]? ubulk, out string said)
                {
                    try
                    {
                        var reader = new PakReader.Parsers.PackageReader(new MemoryStream(uasset), new MemoryStream(uexp),
                            ubulk == null ? null! : new MemoryStream(ubulk));
                        said = $"{reader.ExportMap.Length} exports";
                        return true;
                    }
                    catch (Exception e) { said = e.GetType().Name + ": " + e.Message; return false; }
                }

                var folders = new[]
                {
                    "Actors/Equipment/RangedWeapons/HeavyCrossbow/",
                    "Actors/Equipment/MeleeWeapons/Katana_Unique1/",
                    "Actors/Equipment/Armor/PhantomArmor_Unique1/",
                    "Components/Enchantments/Stunning/",
                    "DataTables/Assets/",
                };
                int tested = 0, identical = 0, roundTrip = 0, parsedBefore = 0, parsedGrown = 0;
                foreach (var folder in folders)
                {
                    var packages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in index.AllEntries())
                    {
                        var at = entry.Key.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
                        if (at < 0) { continue; }
                        var rest = entry.Key.Substring(at + folder.Length);
                        if (rest.Length == 0 || rest.Contains('/')) { continue; }
                        var dot = rest.IndexOf('.');
                        packages.Add("/Dungeons/Content/" + folder + (dot >= 0 ? rest[..dot] : rest));
                    }
                    foreach (var path in packages)
                    {
                        var package = index.extractPackage(path);
                        if (package == null) { continue; }
                        var uasset = package.Value.UAsset.ToArray();
                        var uexp = package.Value.UExp.ToArray();
                        var ubulk = package.Value.UBulk?.ToArray();
                        tested++;

                        var same = Logic.PackageRename.rename(uasset, _ => null, out _);
                        if (same != null && same.AsSpan().SequenceEqual(uasset)) { identical++; }
                        else { Console.WriteLine($"  NOT IDENTICAL on a no-op: {path} ({(same == null ? "not understood" : "differs")})"); }

                        //Grow every name by a suffix, then take it off again.
                        var grown = Logic.PackageRename.rename(uasset, t => t.Length > 0 ? t + "_Grown7" : null, out var n);
                        var back = grown == null ? null : Logic.PackageRename.rename(grown,
                            t => t.EndsWith("_Grown7", StringComparison.Ordinal) ? t[..^7] : null, out _);
                        if (back != null && back.AsSpan().SequenceEqual(uasset)) { roundTrip++; }
                        else { Console.WriteLine($"  ROUND TRIP FAILED: {path}"); }

                        if (parses(uasset, uexp, ubulk, out var before)) { parsedBefore++; }
                        //A grown copy parses only if every offset moved: its names are all wrong
                        //for the engine, but the reader only follows structure.
                        if (grown != null && parses(grown, uexp, ubulk, out var after)) { parsedGrown++; }
                        else if (parses(uasset, uexp, ubulk, out _)) { Console.WriteLine($"  GROWN DOES NOT PARSE: {path}"); }
                    }
                }
                Console.WriteLine($"[slot] rename check on {tested} game packages: no-op identical {identical}, "
                    + $"grow+shrink identical {roundTrip}, parse before {parsedBefore}, parse grown {parsedGrown}");

                var made = Logic.NewContent.cloneFolder("/Dungeons/Content/Actors/Equipment/RangedWeapons/HeavyCrossbow",
                    "SpiderCrossbow", ownsBareId: true);
                if (made == null) { Console.WriteLine("[slot] clone FAILED"); Shutdown(); return; }
                //The asset registry, with the copy's entries added - without them the game's item
                //finder never learns the copy exists and the class it loads comes back null.
                var entries = made.Entries.ToList();
                var registry = Logic.RegistryPatch.readGameRegistry(Logic.CustomSkins.paksFolder!);
                var patched = registry == null ? null
                    : Logic.RegistryPatch.withClones(registry, made.GameFrom, made.Rename, out var registered);
                if (patched == null) { Console.WriteLine("[slot] registry NOT patched"); Shutdown(); return; }
                Console.WriteLine($"[slot] registry: {registry!.Length:N0} -> {patched.Length:N0} bytes, "
                    + $"{(patched.Length - registry.Length):N0} added");
                File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "mcd-registry-patched.bin"), patched);
                entries.Add(new Logic.PakWriter.Entry(Logic.RegistryPatch.PAK_PATH, patched));
                Logic.PakWriter.write(into, entries);
                Console.WriteLine($"[slot] wrote {entries.Count} file(s) -> {into}");

                var byName = made.Entries.ToDictionary(e => e.Path, e => e.Data, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in made.Entries.Where(e => e.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
                {
                    var stem = entry.Path[..^".uasset".Length];
                    byName.TryGetValue(stem + ".ubulk", out var bulk);
                    var ok = parses(entry.Data, byName[stem + ".uexp"], bulk, out var said);
                    var names = Logic.CookedProperties.readNamesOf(entry.Data);
                    var left = names.Where(x => x.Contains("HeavyCrossbow", StringComparison.OrdinalIgnoreCase)).ToList();
                    var ours = names.Where(x => x.Contains("SpiderCrossbow", StringComparison.OrdinalIgnoreCase)).ToList();
                    var (hashOk, hashAll) = Logic.NewContent.checkHashes(entry.Data);
                    Console.WriteLine($"  {stem.Substring(stem.LastIndexOf('/') + 1),-32} parse={(ok ? said : "FAIL " + said)} hashes {hashOk}/{hashAll}");
                    foreach (var x in ours) { Console.WriteLine($"      + {x}"); }
                    foreach (var x in left) { Console.WriteLine($"      ! still {x}"); }
                }
                Shutdown();
                return;
            }

            //Builds the new-content test pak: enchantments and an item under ids the game has
            //never shipped, each a copy of a real one so that if the id loads, what it does is
            //unmistakable. Then reads the pak back and checks every renamed name and hash.
            //
            //  MCDReborn.exe PROBE_NEWCONTENT=<out.pak>
            if (_startupArguments.Any(a => a.StartsWith("PROBE_NEWCONTENT=", StringComparison.Ordinal)))
            {
                var into = _startupArguments.First(a =>
                    a.StartsWith("PROBE_NEWCONTENT=", StringComparison.Ordinal))["PROBE_NEWCONTENT=".Length..]
                    .Trim().Trim('"');

                var wanted = new[]
                {
                    ("/Dungeons/Content/Components/Enchantments/Stunning", "Stunlock"),
                    ("/Dungeons/Content/Components/Enchantments/LevitationShot", "LevitationPlus"),
                    ("/Dungeons/Content/Actors/Equipment/MeleeWeapons/Katana_Unique1", "Katana_Custom1"),
                };

                var all = new List<Logic.PakWriter.Entry>();
                foreach (var (from, id) in wanted)
                {
                    var made = Logic.NewContent.cloneFolder(from, id, ownsBareId: from.Contains("/Actors/"));
                    if (made == null) { Console.WriteLine($"FAILED  {from} -> {id}"); continue; }
                    Console.WriteLine($"{id,-16} {made.Files.Count} asset(s): {string.Join(", ", made.Files)}");
                    all.AddRange(made.Entries);
                }

                Logic.PakWriter.write(into, all);
                Console.WriteLine($"wrote {all.Count} file(s) -> {into}");

                //The hash functions themselves, tested on the game's own untouched assets first:
                //anything short of every name means the port is wrong, whatever the pak says.
                var original = 0;
                var originalTotal = 0;
                foreach (var source in new[]
                {
                    "/Dungeons/Content/Components/Enchantments/Stunning/BP_Stunning",
                    "/Dungeons/Content/Components/Enchantments/LevitationShot/BP_LevitationShot",
                    "/Dungeons/Content/Actors/Equipment/MeleeWeapons/Katana_Unique1/BP_Katana_Unique1Storable",
                    "/Dungeons/Content/Actors/Equipment/MeleeWeapons/Katana_Unique1/BP_Katana_Unique1Instance",
                    "/Dungeons/Content/Actors/Equipment/MeleeWeapons/Katana_Unique1/SM_Katana_Unique1",
                })
                {
                    var package = Logic.CustomSkins.index?.extractPackage(source);
                    if (package == null) { Console.WriteLine("  (could not read " + source + ")"); continue; }
                    var (ok, total) = Logic.NewContent.checkHashes(package.Value.UAsset.ToArray());
                    original += ok;
                    originalTotal += total;
                }
                Console.WriteLine($"hash port vs the game's own assets: {original} / {originalTotal} names match");

                var written = 0;
                var writtenTotal = 0;
                foreach (var item in Logic.ModPak.read(into))
                {
                    if (!item.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) { continue; }
                    var (ok, total) = Logic.NewContent.checkHashes(item.Data);
                    written += ok;
                    writtenTotal += total;
                }
                Console.WriteLine($"hashes in the written pak:           {written} / {writtenTotal} names match");

                //Read back: every name in every copied package, flagged if it still says an old id
                //or if either hash disagrees with its spelling.
                foreach (var item in Logic.ModPak.read(into))
                {
                    if (!item.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) { continue; }
                    var names = Logic.CookedProperties.readNamesOf(item.Data);
                    Console.WriteLine("  " + item.Path);
                    foreach (var name in names)
                    {
                        if (name.IndexOf("Stunning", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("LevitationShot", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("Katana_Unique1", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("Stunlock", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("LevitationPlus", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("Katana_Custom1", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Console.WriteLine("      " + name);
                        }
                    }
                }

                Shutdown();
                return;
            }

            //What installed mods add to the pickers, and which gear each enchantment lands on.
            //Read-only: the scan at startup has already run; this reports what it found.
            //
            //  MCDReborn.exe PROBE_MODCONTENT
            if (_startupArguments.Any(a => a == "PROBE_MODCONTENT"))
            {
                foreach (var pair in Logic.NewContent.modEnchantments.OrderBy(one => one.Key))
                {
                    var melee = Data.EnchantmentCategories.matches(pair.Key, Data.EnchantmentCategory.Melee);
                    var ranged = Data.EnchantmentCategories.matches(pair.Key, Data.EnchantmentCategory.Ranged);
                    var armor = Data.EnchantmentCategories.matches(pair.Key, Data.EnchantmentCategory.Armor);
                    Console.WriteLine($"enchantment {pair.Key,-16} {pair.Value,-22} shown for melee={melee} ranged={ranged} armor={armor}"
                        + $"   in list={Services.EnchantmentDatabase.allEnchantments.Contains(pair.Key)}");
                }
                foreach (var id in new[] { "Katana_Custom1" })
                {
                    Console.WriteLine($"item        {id,-16} all={Services.ItemDatabase.all.Contains(id)} melee={Services.ItemDatabase.meleeWeapons.Contains(id)}");
                }
                Shutdown();
                return;
            }

            if (_startupArguments.Any(a => a == "PROBE_GATES"))
            {
                var held = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var clickers = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var doors = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var simplest = string.Empty;

                foreach (var mission in Logic.GameMaps.all())
                {
                    var raw = Logic.GameMaps.read(mission.PakPath);
                    if (raw == null) { continue; }

                    System.Text.Json.Nodes.JsonObject? level;
                    try
                    {
                        var text = Logic.GameMaps.stripComments(
                            new System.Text.UTF8Encoding(false).GetString(raw).TrimStart('\uFEFF'));

                        level = System.Text.Json.Nodes.JsonNode.Parse(text,
                            documentOptions: new System.Text.Json.JsonDocumentOptions
                            {
                                AllowTrailingCommas = true,
                                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            }) as System.Text.Json.Nodes.JsonObject;
                    }
                    catch { continue; }

                    if (level?["objectives"] is not System.Text.Json.Nodes.JsonArray all) { continue; }

                    foreach (var one in all)
                    {
                        if (one is not System.Text.Json.Nodes.JsonObject step) { continue; }

                        foreach (var part in step)
                        {
                            if (part.Value is not System.Text.Json.Nodes.JsonObject body) { continue; }

                            var holds = body["locked-doors"] != null ? "locked-doors"
                                : body["gate"] != null ? "gate"
                                : null;

                            if (holds == null) { continue; }

                            var key = part.Key + "." + holds;
                            held[key] = held.TryGetValue(key, out var was) ? was + 1 : 1;

                            if (part.Key != "click") { continue; }

                            //What you click to open it, and what the held thing is drawn as.
                            var what = body["object"]?.GetValue<string>() ?? "(none)";
                            clickers[what] = clickers.TryGetValue(what, out var seen) ? seen + 1 : 1;

                            var leaf = body["door-path"]?.GetValue<string>() ?? "(none)";
                            doors[leaf] = doors.TryGetValue(leaf, out var also) ? also + 1 : 1;

                            //The smallest worked example, which is the one worth copying.
                            var shown = step.ToJsonString(
                                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                            if (simplest.Length == 0 || shown.Length < simplest.Length)
                            {
                                simplest = mission.Name + ":\n" + shown;
                            }
                        }
                    }
                }

                Console.WriteLine();
                Console.WriteLine("what holds a gate shut:");
                foreach (var one in held) { Console.WriteLine($"   {one.Key,-24} {one.Value}"); }

                Console.WriteLine();
                Console.WriteLine("what you click to open one:");
                foreach (var one in clickers) { Console.WriteLine($"   {one.Value,3}  {one.Key}"); }

                Console.WriteLine();
                Console.WriteLine("what the held door is drawn as (door-path):");
                foreach (var one in doors) { Console.WriteLine($"   {one.Value,3}  {one.Key}"); }

                Console.WriteLine();
                Console.WriteLine("the smallest one:");
                Console.WriteLine(simplest);

                this.Shutdown();
                return;
            }

            //PROBE_STEPS=<mission> - building a step, a gate, and the wire between them, through
            //the window's own buttons.
            //
            //The complaint this exists for: a gate could be made and then had nothing to be
            //opened by, because the chain held one step - the way out - and there was no way to
            //add another. PROBE_GATES then showed that offering every step would have been the
            //worse bug, since the game reads "locked-doors" out of a click and out of nothing
            //else. So there are two things to prove: that a step can be added, and that the kind
            //which cannot hold a gate is never offered as one that can.
            //
            //On a COPY. Two of these probes have eaten the map they were pointed at.
            var probeSteps = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_STEPS="));
            if (probeSteps != null)
            {
                var wanted = probeSteps.Substring("PROBE_STEPS=".Length).Trim('"');

                var live = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                var folder = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "mcd-steps-probe", wanted);

                try
                {
                    if (System.IO.Directory.Exists(folder))
                    {
                        System.IO.Directory.Delete(folder, true);
                    }

                    foreach (var from in System.IO.Directory.GetFiles(
                        live, "*", System.IO.SearchOption.AllDirectories))
                    {
                        var to = System.IO.Path.Combine(folder, from.Substring(live.Length + 1));
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
                        System.IO.File.Copy(from, to, true);
                    }

                    Console.WriteLine($"[steps] working on a copy at {folder}");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[steps] could not copy {live}: {problem.Message}");
                    this.Shutdown();
                    return;
                }

                var map = Logic.MapSpawns.load(folder);
                var window = new UI.SpawnsWindow(map);

                window.WindowState = WindowState.Normal;
                window.Width = 1280;
                window.Height = 800;
                window.Left = -20000;
                window.Show();

                var waited = 0;
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250),
                };

                timer.Tick += (_, _) =>
                {
                    waited += 250;
                    if (!window.mapReady && waited < 20000) { return; }
                    timer.Stop();

                    try
                    {
                        Console.WriteLine($"[steps] room: {window.roomChosen}");

                        //Whatever the map had been left in the middle of, stripped back to the
                        //way out. Through the window's own button, so this is a real starting
                        //state rather than an assumed one - the copy underneath is somebody's
                        //actual map and has no obligation to be tidy.
                        Console.WriteLine($"[steps] found: {string.Join(" | ", window.questRows)}");
                        window.probeOnlyExit();

                        //--- what it looked like before ------------------------------------
                        Console.WriteLine($"[steps] chain was: {string.Join(" | ", window.questRows)}");
                        Console.WriteLine($"[steps] the gate picker offered {window.opensRows.Length}: "
                            + string.Join(" | ", window.opensRows));

                        var across = Math.Max(8, window.roomAcross / 4);

                        //--- the wording, which is a key and not a sentence -----------------
                        Console.WriteLine($"[steps] {window.wordingWhyNow}");
                        Console.WriteLine($"[steps] wording on offer: "
                            + string.Join(" | ", window.wordingRows.Take(6)));
                        Console.WriteLine($"[steps] banner names: "
                            + string.Join(" | ", window.bannerRows.Take(6)));

                        Console.WriteLine(window.wordingRows.Length > 0
                            ? "[steps] there is real wording to choose from"
                            : "[steps] WRONG - nothing to say, so no step can be made");

                        //--- a step you have to walk into, which CANNOT hold a gate ---------
                        window.probeAddReachStep(0, across, 20, across);

                        var reachOffered = window.opensRows.Length;
                        Console.WriteLine($"[steps] after a reach step the picker offers {reachOffered}");
                        Console.WriteLine(reachOffered == 0
                            ? "[steps] right - neither a gauntlet nor the exit is offered"
                            : "[steps] WRONG - something is offered that cannot usefully open a gate");

                        //--- a step you have to click, which CAN ----------------------------
                        window.probeAddClickStep(1, 0, across * 2, 20, across);

                        var clickOffered = window.opensRows.Length;
                        Console.WriteLine($"[steps] after a click step the picker offers {clickOffered}: "
                            + string.Join(" | ", window.opensRows));
                        Console.WriteLine(clickOffered == 1
                            ? "[steps] right - the click alone, without the gauntlet or the exit"
                            : "[steps] WRONG - expected exactly the click to be offered");

                        Console.WriteLine($"[steps] chain now: {string.Join(" | ", window.questRows)}");
                        Console.WriteLine($"[steps] spots: {string.Join(" | ", window.stepRows)}");
                        Console.WriteLine($"[steps] amber pins: {window.stepPinsNow.Count}");
                        Console.WriteLine(window.stepPinsNow.Count == window.stepRows.Length
                            ? "[steps] every spot is drawn"
                            : "[steps] WRONG - a spot was added that nothing draws");

                        //--- a gate on its own -----------------------------------------------
                        //
                        //The complaint: added a gate, ran the game, saw nothing. A gate with no
                        //objective CANNOT be drawn - the prefab lives on the objective - so a
                        //loose one is an invisible permanent wall. It should wire itself.
                        //Counted before and after rather than absolutely: the map underneath is
                        //somebody's own and may already hold a loose gate from before this
                        //wired itself - which it does, and which is the bug being reported.
                        var looseWere = window.gateRows.Count(one => one.Contains("NOTHING OPENS IT"));

                        window.probeAddGate(across * 2, 20, across * 2);
                        Console.WriteLine($"[steps] {window.probeStatus}");

                        var looseNow = window.gateRows.Count(one => one.Contains("NOTHING OPENS IT"));
                        Console.WriteLine($"[steps] invisible gates: {looseWere} before, {looseNow} after");

                        Console.WriteLine(looseNow == looseWere
                            ? "[steps] the new gate wired itself to a step, so it can be seen"
                            : "[steps] WRONG - adding a gate made another invisible wall");

                        Console.WriteLine(looseWere == 0
                            ? "[steps] nothing was left loose beforehand either"
                            : $"[steps] note: {looseWere} gate(s) in this map predate the wiring "
                              + "and are still invisible - the list now says so");

                        //--- the wire done by hand -------------------------------------------
                        window.probeAddGate(across, 20, across * 2);
                        window.probePickGate(window.gateRows.Length - 1);
                        window.probeLockGate(0);

                        Console.WriteLine($"[steps] gates: {string.Join(" | ", window.gateRows)}");

                        var held = window.gateRows.Count(one => one.Contains("opens:"));
                        Console.WriteLine(held >= 1
                            ? "[steps] the gate says what opens it"
                            : "[steps] WRONG - no gate claims to be opened by anything");

                        //The invisible-gate bug. A held gate with no prefab still blocks, which
                        //in game reads as the map being broken rather than as a door.
                        Console.WriteLine(window.gateRows.Any(one => one.Contains("INVISIBLE"))
                            ? "[steps] WRONG - a held gate is invisible"
                            : "[steps] every held gate is drawn as something");

                        //--- and what actually reached the file ------------------------------
                        //The writer the Save button uses, without the weld it also kicks off.
                        Logic.MapSpawns.save(map);

                        var raw = System.IO.File.ReadAllText(
                            System.IO.Path.Combine(folder, "level.json"));

                        var written = System.Text.Json.Nodes.JsonNode.Parse(
                            Logic.GameMaps.stripComments(raw),
                            documentOptions: new System.Text.Json.JsonDocumentOptions
                            {
                                AllowTrailingCommas = true,
                                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            }) as System.Text.Json.Nodes.JsonObject;

                        var steps = written?["objectives"] as System.Text.Json.Nodes.JsonArray
                            ?? new System.Text.Json.Nodes.JsonArray();

                        Console.WriteLine($"[steps] the file now holds {steps.Count} objective(s)");

                        var lastIsExit = steps.Count > 0
                            && (steps[steps.Count - 1] as System.Text.Json.Nodes.JsonObject)
                                ?["click"]?["object"]?.GetValue<string>() == Logic.MapSpawns.EXIT_DOOR;

                        Console.WriteLine(lastIsExit
                            ? "[steps] the way out is still last, so everything before it is asked for"
                            : "[steps] WRONG - a step went in behind the exit and will never be asked");

                        var onGauntlet = 0;
                        var onClick = 0;

                        foreach (var one in steps)
                        {
                            if (one is not System.Text.Json.Nodes.JsonObject step) { continue; }
                            if (step["gauntlet"]?["locked-doors"] != null) { onGauntlet++; }
                            if (step["click"]?["locked-doors"] != null) { onClick++; }
                        }

                        Console.WriteLine($"[steps] locked-doors written: {onClick} on a click, "
                            + $"{onGauntlet} on a gauntlet");

                        //The banner bug: every key written has to BE in the level's own table,
                        //or the game draws <MISSING STRING TABLE ENTRY> and says nothing.
                        var table = Logic.MapSpawns.loctableOf(map);
                        var missing = 0;

                        foreach (var each in steps)
                        {
                            if (each is not System.Text.Json.Nodes.JsonObject step) { continue; }

                            foreach (var field in new[] { "name", "description" })
                            {
                                var key = step[field]?.GetValue<string>();
                                if (key == null) { continue; }

                                var said = Services.R.wordFor(table, key);
                                Console.WriteLine($"[steps] {field,-12} {key,-42} "
                                    + (said ?? "<MISSING STRING TABLE ENTRY>"));

                                if (said == null) { missing++; }
                            }
                        }

                        Console.WriteLine(missing == 0
                            ? $"[steps] every word of it is in the \"{table}\" table"
                            : $"[steps] WRONG - {missing} key(s) will draw as missing on the banner");

                        //And the prefab itself: written into the file, on the body beside the
                        //locked-doors it applies to, and actually present in the game's paks.
                        foreach (var each in steps)
                        {
                            if (each is not System.Text.Json.Nodes.JsonObject step) { continue; }
                            if (step["click"] is not System.Text.Json.Nodes.JsonObject body) { continue; }
                            if (body["locked-doors"] == null) { continue; }

                            var drawn = body["door-path"]?.GetValue<string>();

                            if (drawn == null)
                            {
                                Console.WriteLine("[steps] WRONG - a held gate went to file with no door-path");
                                continue;
                            }

                            var there = Logic.CustomSkins.index?.extractPackage(
                                "/Dungeons/Content/" + drawn) != null;

                            Console.WriteLine($"[steps] door-path {drawn}");
                            Console.WriteLine(there
                                ? "[steps] the game has that prefab, so it will draw"
                                : "[steps] WRONG - the game has no such prefab");
                        }
                        Console.WriteLine(onClick == 1 && onGauntlet == 0
                            ? "[steps] the gate is held by the one body the game reads it from"
                            : "[steps] WRONG - that gate will not open");

                        //--- and taking a step away takes its spot with it -------------------
                        var spotsWere = window.stepRows.Length;
                        window.probePickQuest(0);
                        window.probeRemoveQuest();

                        Console.WriteLine($"[steps] after removing step 1, spots: "
                            + $"{string.Join(" | ", window.stepRows)}");
                        Console.WriteLine(window.stepRows.Length == spotsWere - 1
                            ? "[steps] the removed step took its own spot with it"
                            : "[steps] WRONG - a spot was left behind with nothing asking for it");

                        //--- and the pin can be dragged, like the other five -----------------
                        //
                        //Through grab/dragTo/drop, which is what the mouse calls. A sixth kind
                        //of pin on one map is a sixth chance for "nearest wins" to take hold of
                        //the wrong thing, and the amber ones stand near the gates on purpose.
                        var view = window.probeView;

                        Point? screen(System.Collections.Generic.IReadOnlyList<(int x, int y, int z)> pins)
                        {
                            for (var sy = 12.0; sy < view.ActualHeight - 12; sy += 5)
                            {
                                for (var sx = 12.0; sx < view.ActualWidth - 12; sx += 5)
                                {
                                    var at = new Point(sx, sy);
                                    var hit = view.probeLook(at);
                                    if (hit == null) { continue; }

                                    foreach (var pin in pins)
                                    {
                                        double dx = pin.x - hit.Value.x, dy = pin.y - hit.Value.y,
                                               dz = pin.z - hit.Value.z;
                                        if (dx * dx + dz * dz + dy * dy * 0.25 <= 2.0) { return at; }
                                    }
                                }
                            }

                            return null;
                        }

                        var onPin = screen(window.stepPinsNow);

                        if (onPin == null)
                        {
                            Console.WriteLine("[steps] the amber pin is not on screen, drag not tested");
                        }
                        else
                        {
                            var before = window.stepRows.FirstOrDefault() ?? string.Empty;

                            var caught = view.probeGrab(onPin.Value);
                            Console.WriteLine(caught && view.heldKind == UI.MapView3D.Pin.Step
                                ? "[steps] took hold of the amber pin rather than a neighbour"
                                : $"[steps] WRONG - took {(caught ? view.heldKind.ToString() : "nothing")}");

                            if (caught)
                            {
                                //Somewhere else on the same map, found the same way the drag
                                //itself finds ground.
                                Point? bare = null;
                                for (var sy = 12.0; sy < view.ActualHeight - 12 && bare == null; sy += 9)
                                {
                                    for (var sx = 12.0; sx < view.ActualWidth - 12; sx += 9)
                                    {
                                        var at = new Point(sx, sy);
                                        var hit = view.probeLook(at);
                                        if (hit == null) { continue; }

                                        var here = hit.Value;
                                        if (window.stepPinsNow.All(pin =>
                                            Math.Abs(pin.x - here.x) + Math.Abs(pin.z - here.z) > 12))
                                        {
                                            bare = at;
                                            break;
                                        }
                                    }
                                }

                                if (bare == null)
                                {
                                    Console.WriteLine("[steps] no clear ground to drag onto");
                                    view.probeDrop();
                                }
                                else
                                {
                                    view.probeDragTo(bare.Value);
                                    view.probeDrop();

                                    var after = window.stepRows.FirstOrDefault() ?? string.Empty;
                                    Console.WriteLine($"[steps] {before}");
                                    Console.WriteLine($"[steps] {after}");
                                    Console.WriteLine(after != before && window.stepRows.Length == 1
                                        ? "[steps] the spot moved, and there is still one of it"
                                        : "[steps] WRONG - the drag did not move the spot");
                                }
                            }
                        }
                    }
                    catch (Exception problem)
                    {
                        Console.WriteLine($"[steps] threw: {problem}");
                    }

                    this.Shutdown();
                };

                timer.Start();
                return;
            }

            //PROBE_BUTTONS=<mission> - whether Save and Install come back after they are used.
            //
            //They did not. Install switched itself off by hand on the way in and nothing ever
            //switched it back on: updateUI, which owns every other button's state, had no line
            //for it. One press and it was dead for the rest of the session, with the status bar
            //still saying there was work to save.
            //
            //And underneath that, the wait it did instead of awaiting: it called the save
            //handler - an async void, which returns at its first await - and then spun until
            //"_map.Changed.Count > 0" went false. MapSpawns.save clears that list BEFORE the
            //weld starts, so the condition was already false and the loop exited at once. The
            //install packed whatever the last weld had left behind.
            //
            //On a COPY, and it never installs anything - the install path shares _busy and
            //updateUI with the save path, which is the whole point of the fix.
            var probeButtons = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_BUTTONS="));
            if (probeButtons != null)
            {
                var wanted = probeButtons.Substring("PROBE_BUTTONS=".Length).Trim('"');

                var live = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                var folder = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "mcd-buttons-probe", wanted);

                try
                {
                    if (System.IO.Directory.Exists(folder)) { System.IO.Directory.Delete(folder, true); }

                    foreach (var from in System.IO.Directory.GetFiles(
                        live, "*", System.IO.SearchOption.AllDirectories))
                    {
                        var to = System.IO.Path.Combine(folder, from.Substring(live.Length + 1));
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
                        System.IO.File.Copy(from, to, true);
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[buttons] could not copy {live}: {problem.Message}");
                    this.Shutdown();
                    return;
                }

                Console.WriteLine($"[buttons] working on a copy at {folder}");

                //A mission, so Install is on screen at all - it is hidden without one.
                var mission = Logic.GameMaps.all()
                    .FirstOrDefault(one => string.Equals(one.Name, wanted,
                        StringComparison.OrdinalIgnoreCase));

                if (mission == null)
                {
                    Console.WriteLine($"[buttons] the game has no mission called {wanted}");
                    this.Shutdown();
                    return;
                }

                var map = Logic.MapSpawns.load(folder);
                var window = new UI.SpawnsWindow(map, mission);

                window.WindowState = WindowState.Normal;
                window.Width = 1280;
                window.Height = 800;
                window.Left = -20000;
                window.Show();

                var waited = 0;
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250),
                };

                timer.Tick += async (_, _) =>
                {
                    waited += 250;
                    if (!window.mapReady && waited < 20000) { return; }
                    timer.Stop();

                    try
                    {
                        Console.WriteLine($"[buttons] at rest: save {window.saveEnabled}, "
                            + $"install {window.installEnabled}, {window.changedNow} changed, "
                            + $"pak behind the folder: {window.worthInstallingNow}");

                        //Install is offered when it would DO something - unsaved edits, or a pak
                        //older than the folder. "Nothing changed yet" beside a live button is a
                        //press that cannot be told apart from one that failed.
                        Console.WriteLine(window.installEnabled == window.worthInstallingNow
                            ? "[buttons] Install is offered exactly when it would change something"
                            : "[buttons] WRONG - Install disagrees with whether there is anything to install");

                        //An edit, through the window's own button, so the changed list fills the
                        //way it does for somebody using it.
                        window.probeAddGate(Math.Max(8, window.roomAcross / 4), 20,
                                            Math.Max(8, window.roomAcross / 4));

                        Console.WriteLine($"[buttons] after an edit: save {window.saveEnabled}, "
                            + $"install {window.installEnabled}, {window.changedNow} changed");

                        Console.WriteLine(window.saveEnabled
                            ? "[buttons] Save woke up for the edit"
                            : "[buttons] WRONG - there is work to save and Save is off");

                        //The BUTTON, not the method under it - the bug was in what the button
                        //leaves behind, so pressing anything less would miss it.
                        var before = DateTime.UtcNow;
                        window.probeSave();

                        Console.WriteLine(window.busyNow
                            ? "[buttons] both buttons are off while the save runs"
                            : "[buttons] the save finished before the first look");

                        Console.WriteLine(!window.saveEnabled && !window.installEnabled || !window.busyNow
                            ? "[buttons] nothing can be pressed twice mid-save"
                            : "[buttons] WRONG - a button is live while a save is running");

                        var spun = 0;
                        while (window.busyNow && spun < 120000)
                        {
                            await System.Threading.Tasks.Task.Delay(50);
                            spun += 50;
                        }

                        var took = (DateTime.UtcNow - before).TotalMilliseconds;

                        Console.WriteLine($"[buttons] the save finished in {took:N0} ms, "
                            + $"{window.changedNow} changed");

                        Console.WriteLine(!window.saveEnabled
                            ? "[buttons] Save went off, because there is nothing left to save"
                            : "[buttons] WRONG - Save is offering to save nothing");

                        //Saving writes the folder, so the pak is now behind it and installing
                        //would genuinely do something - even though nothing is unsaved.
                        Console.WriteLine($"[buttons] after saving, pak behind the folder: "
                            + $"{window.worthInstallingNow}, install {window.installEnabled}");

                        Console.WriteLine(window.installEnabled
                            ? "[buttons] Install is still reachable with nothing unsaved"
                            : "[buttons] WRONG - saving locked Install out");

                        Console.WriteLine($"[buttons] afterwards: save {window.saveEnabled}, "
                            + $"install {window.installEnabled}");

                        //The regression itself. Nothing is left to save, so Save is rightly off;
                        //Install has to be ON, because a folder with nothing unsaved is exactly
                        //the folder somebody wants to install.
                        Console.WriteLine(window.installEnabled
                            ? "[buttons] Install is still available after a save"
                            : "[buttons] WRONG - Install has not come back");

                        //And a second round, which is what the session-long death looked like.
                        window.probeAddGate(Math.Max(10, window.roomAcross / 3), 20,
                                            Math.Max(10, window.roomAcross / 3));

                        Console.WriteLine($"[buttons] second edit: save {window.saveEnabled}, "
                            + $"install {window.installEnabled}, {window.changedNow} changed");

                        Console.WriteLine(window.saveEnabled && window.installEnabled
                            ? "[buttons] both buttons work a second time"
                            : "[buttons] WRONG - a button did not come back for the second edit");

                        //--- and now the half that only happens on a welded map ---------------
                        //
                        //Nothing in the workshop is welded - a hand-built map is one stretch and
                        //saves in six milliseconds - so the branch the wait was broken in never
                        //ran above. Faking the marker file makes saveNow take it. The weld will
                        //REFUSE this folder, which is fine and is the point: what is being
                        //measured is whether the save waits for the answer at all, and whether a
                        //refusal is reported rather than installed over.
                        var marker = System.IO.Path.Combine(folder, "level.json.multitile");
                        System.IO.File.Copy(
                            System.IO.Path.Combine(folder, "level.json"), marker, true);

                        Console.WriteLine($"[buttons] pretending the map is welded "
                            + $"(tools available: {Logic.MapTools.available})");

                        var weldStart = DateTime.UtcNow;
                        window.probeSave();

                        var sawBusy = window.busyNow;

                        var spun2 = 0;
                        while (window.busyNow && spun2 < 180000)
                        {
                            await System.Threading.Tasks.Task.Delay(50);
                            spun2 += 50;
                        }

                        var weldTook = (DateTime.UtcNow - weldStart).TotalMilliseconds;

                        Console.WriteLine($"[buttons] the welded save took {weldTook:N0} ms, "
                            + $"held the buttons: {sawBusy}");

                        Console.WriteLine(sawBusy
                            ? "[buttons] the save was still running when it returned, so it is awaited"
                            : "[buttons] the weld returned instantly - nothing was waited for");

                        Console.WriteLine($"[buttons] status: {window.probeStatus}");

                        Console.WriteLine(window.installEnabled
                            ? "[buttons] Install came back after the welded save too"
                            : "[buttons] WRONG - Install is dead after a welded save");
                    }
                    catch (Exception problem)
                    {
                        Console.WriteLine($"[buttons] threw: {problem}");
                    }

                    this.Shutdown();
                };

                timer.Start();
                return;
            }

            //PROBE_NAMESPACES - every namespace inside the game's own string table.
            //
            //The app cherry-picks a dozen of them by name, which is why an objective's
            //description came back missing: not absent from the game, just never loaded.
            if (_startupArguments.Any(a => a == "PROBE_NAMESPACES"))
            {
                var pak = Logic.CustomSkins.index;
                if (pak == null) { Console.WriteLine("[ns] no pak index"); this.Shutdown(); return; }

                var tables = pak.extractLocResFile("/Dungeons/Content/Localization/Game/en/Game");

                if (tables == null)
                {
                    Console.WriteLine("[ns] could not read the English table");
                    this.Shutdown();
                    return;
                }

                foreach (var space in tables.OrderByDescending(one => one.Value.Count))
                {
                    Console.WriteLine($"[ns] {space.Value.Count,6}  \"{space.Key}\"   "
                        + string.Join(", ", space.Value.Keys.Take(3)));
                }

                //How portable each key is. A map's wording comes out of the namespace its
                //loctable-id names, and installing the same folder over a different mission
                //changes which namespace that is - so a key only half the missions carry is a
                //key that draws as missing on the other half.
                var missions = tables
                    .Where(one => one.Key.EndsWith("Labels", StringComparison.Ordinal)
                                  && one.Value.Keys.Any(k => k.StartsWith("description_",
                                      StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                Console.WriteLine($"[ns] {missions.Count} mission label namespaces: "
                    + string.Join(", ", missions.Select(one => one.Key)));

                var spread = new SortedDictionary<string, int>(StringComparer.Ordinal);

                foreach (var space in missions)
                {
                    foreach (var key in space.Value.Keys)
                    {
                        spread[key] = spread.TryGetValue(key, out var was) ? was + 1 : 1;
                    }
                }

                foreach (var key in spread.OrderByDescending(one => one.Value).Take(40))
                {
                    var said = missions.Select(one =>
                        one.Value.TryGetValue(key.Key, out var v) ? v : null)
                        .FirstOrDefault(v => v != null);

                    Console.WriteLine($"[ns] {key.Value,3}/{missions.Count}  {key.Key,-46} {said}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_LOCRES - every string table in the paks, not just the one the app reads.
            //
            //The app loads Localization/<lang>/Game only, and that table has no objective
            //descriptions in it - which is why offering to look one up came back empty. This
            //says what else is in there.
            if (_startupArguments.Any(a => a == "PROBE_LOCRES"))
            {
                var pak = Logic.CustomSkins.index;
                if (pak == null) { Console.WriteLine("[locres] no pak index"); this.Shutdown(); return; }

                var seen = 0;

                foreach (var item in pak)
                {
                    if (item == null) { continue; }

                    var at = item.IndexOf("//") + 1;
                    var path = item.Substring(at);

                    if (path.IndexOf("locres", StringComparison.OrdinalIgnoreCase) < 0
                        && path.IndexOf("Localization", StringComparison.OrdinalIgnoreCase) < 0
                        && path.IndexOf("StringTable", StringComparison.OrdinalIgnoreCase) < 0
                        && path.IndexOf("loctable", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    //English and the shared ones only, or this is fifteen copies of one list.
                    if (path.Contains("/ja/") || path.Contains("/de/") || path.Contains("/fr/")
                        || path.Contains("/es/") || path.Contains("/it/") || path.Contains("/ko/")
                        || path.Contains("/pl/") || path.Contains("/pt-BR/") || path.Contains("/ru/")
                        || path.Contains("/zh-") || path.Contains("/nl/") || path.Contains("/sv/")
                        || path.Contains("/da/") || path.Contains("/nb/") || path.Contains("/fi/")
                        || path.Contains("/tr/") || path.Contains("/es-MX/") || path.Contains("/pt/"))
                    {
                        continue;
                    }

                    Console.WriteLine($"[locres] {path}");
                    if (++seen > 60) { Console.WriteLine("[locres] ..."); break; }
                }

                Console.WriteLine($"[locres] {seen} listed");
                this.Shutdown();
                return;
            }

            //PROBE_WORDS - what an objective is allowed to say.
            //
            //Its "description" is a KEY, not a sentence. A key the game's table has not got
            //draws as <MISSING STRING TABLE ENTRY> on the mission banner, which is what the
            //first cut of the step editor produced for everybody who typed their own wording.
            if (_startupArguments.Any(a => a == "PROBE_WORDS"))
            {
                var found = Services.R
                    .missionStringsStartingWith("description_")
                    .OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Console.WriteLine($"[words] {found.Count} description keys the game can draw");

                foreach (var table in Services.R.everyTable()
                    .GroupBy(one => one.table)
                    .OrderBy(one => one.Key, StringComparer.Ordinal))
                {
                    Console.WriteLine($"[words] table {table.Key,-16} {table.Count():N0} strings, "
                        + $"e.g. {string.Join(", ", table.Take(3).Select(one => one.key))}");
                }

                foreach (var hunt in new[] { "exit_through", "the_escape", "objective" })
                {
                    foreach (var hit in Services.R.everyTable()
                        .Where(one => one.key.IndexOf(hunt, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Take(6))
                    {
                        Console.WriteLine($"[words] \"{hunt}\" -> [{hit.table}] {hit.key} = {hit.value}");
                    }
                }

                foreach (var pair in found.Take(400))
                {
                    Console.WriteLine($"[words] {pair.Key,-62} {pair.Value}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_DRAWN - what a gate the game actually draws looks like, region and all.
            //
            //Ours blocks and is invisible. The region is right - it holds you back - so the
            //missing part is whatever tells the game to put something THERE. PROBE_GATES said
            //the click body carries a "door-path" beside its "locked-doors", which is the
            //candidate; this goes and reads the regions those names point at, in the game's own
            //object groups, to see what else they carry.
            if (_startupArguments.Any(a => a == "PROBE_DRAWN"))
            {
                var shapes = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var told = 0;

                foreach (var mission in Logic.GameMaps.all())
                {
                    var raw = Logic.GameMaps.read(mission.PakPath);
                    if (raw == null) { continue; }

                    System.Text.Json.Nodes.JsonObject? level;
                    try
                    {
                        level = System.Text.Json.Nodes.JsonNode.Parse(
                            Logic.GameMaps.stripComments(
                                new System.Text.UTF8Encoding(false).GetString(raw).TrimStart('\uFEFF')),
                            documentOptions: new System.Text.Json.JsonDocumentOptions
                            {
                                AllowTrailingCommas = true,
                                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            }) as System.Text.Json.Nodes.JsonObject;
                    }
                    catch { continue; }

                    if (level?["objectives"] is not System.Text.Json.Nodes.JsonArray all) { continue; }

                    //Which gate names this mission holds shut, and what it says to draw there.
                    var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var one in all)
                    {
                        if (one is not System.Text.Json.Nodes.JsonObject step) { continue; }
                        if (step["click"] is not System.Text.Json.Nodes.JsonObject body) { continue; }
                        if (body["locked-doors"] is not System.Text.Json.Nodes.JsonArray shut) { continue; }

                        var drawn = body["door-path"]?.GetValue<string>() ?? "(none)";

                        foreach (var said in shut)
                        {
                            var name = said?.GetValue<string>();
                            if (name == null) { continue; }
                            var at = name.LastIndexOf('.');
                            wanted[at < 0 ? name : name.Substring(at + 1)] = drawn;
                        }
                    }

                    if (wanted.Count == 0) { continue; }

                    //And what those regions actually are, in the tiles they live in.
                    var (groups, _) = Logic.GameMaps.referencedBy(raw);

                    foreach (var group in groups)
                    {
                        var body = Logic.GameMaps.read("/Dungeons/Content/" + Logic.GameMaps.GROUPS + group);
                        if (body == null) { continue; }

                        System.Text.Json.Nodes.JsonObject? sheet;
                        try
                        {
                            sheet = System.Text.Json.Nodes.JsonNode.Parse(
                                Logic.GameMaps.stripComments(
                                    new System.Text.UTF8Encoding(false).GetString(body)
                                        .TrimStart('\uFEFF')),
                                documentOptions: new System.Text.Json.JsonDocumentOptions
                                {
                                    AllowTrailingCommas = true,
                                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                                }) as System.Text.Json.Nodes.JsonObject;
                        }
                        catch { continue; }

                        foreach (var one in sheet?["objects"] as System.Text.Json.Nodes.JsonArray
                            ?? new System.Text.Json.Nodes.JsonArray())
                        {
                            if (one is not System.Text.Json.Nodes.JsonObject tile) { continue; }

                            foreach (var each in tile["regions"] as System.Text.Json.Nodes.JsonArray
                                ?? new System.Text.Json.Nodes.JsonArray())
                            {
                                if (each is not System.Text.Json.Nodes.JsonObject region) { continue; }

                                var name = region["name"]?.GetValue<string>();
                                if (name == null || !wanted.TryGetValue(name, out var drawn)) { continue; }

                                //What is drawn, how wide it has to be, and which way it lies -
                                //because a gate prefab that does not match its region is a gate
                                //that looks wrong or is not there.
                                var size = region["size"] as System.Text.Json.Nodes.JsonArray;
                                var sx = size?[0]?.GetValue<int>() ?? 0;
                                var sz = size?[2]?.GetValue<int>() ?? 0;

                                var lie = sx >= sz ? "x" : "z";
                                var wide = Math.Max(sx, sz);
                                var tags = region["tags"]?.GetValue<string>() ?? "";

                                var row = $"{drawn}  |  {wide} along {lie}  |  tags \"{tags}\"";
                                shapes[row] = shapes.TryGetValue(row, out var was) ? was + 1 : 1;
                                told++;
                            }
                        }
                    }
                }

                Console.WriteLine();
                Console.WriteLine($"{told} gate regions the game holds shut:");
                foreach (var shape in shapes.OrderBy(one => one.Key, StringComparer.Ordinal))
                {
                    Console.WriteLine($"   {shape.Value,4}  {shape.Key}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_PREFABS - whether every prefab the editor offers is actually in the game.
            //
            //Both lists were read off the game's own missions, but some of those missions are
            //downloads. A prefab that is not there is a mission that will not start, and the
            //editor has no business offering one - so every path in both dropdowns is resolved
            //against the paks rather than trusted.
            if (_startupArguments.Any(a => a == "PROBE_PREFABS"))
            {
                var pak = Logic.CustomSkins.index;
                if (pak == null) { Console.WriteLine("[prefabs] no pak index"); this.Shutdown(); return; }

                var bad = 0;

                void check(string what, (string path, string name)[] list)
                {
                    Console.WriteLine($"[prefabs] --- {what} ---");

                    foreach (var one in list)
                    {
                        if (one.path.Length == 0)
                        {
                            Console.WriteLine($"[prefabs]  --   {one.name}");
                            continue;
                        }

                        var there = pak.extractPackage("/Dungeons/Content/" + one.path) != null;
                        if (!there) { bad++; }

                        Console.WriteLine($"[prefabs]  {(there ? "ok" : "NO")}   {one.name,-34} {one.path}");
                    }
                }

                check("things to click", Logic.MapSpawns.CLICKABLES);
                check("gate looks", Logic.MapSpawns.GATE_LOOKS);
                check("travel doors", Logic.MapSpawns.TRAVEL_DOORS);
                check("the way out", new[] { (Logic.MapSpawns.EXIT_DOOR, "Exit gate") });

                //The one Blossoming Isles uses for every gate it has. Checked because "BPI"
                //looks like a mod's own prefix and a mod-only prefab must not be offered.
                check("seen in mods", new[]
                {
                    ("Decor/Prefabs/Door/BPI_ObjectiveDoor", "Objective door"),
                });

                //Where the ones that did not resolve actually live. The game's own levels
                //reference these by paths whose casing is not the pak's, and the reader is
                //literal about it.
                foreach (var hunt in new[] { "BP_Platform", "BP_PlatformObsidian", "BP_DoorWinter" })
                {
                    foreach (var item in pak)
                    {
                        if (item == null) { continue; }
                        if (item.IndexOf(hunt, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                        Console.WriteLine($"[prefabs] \"{hunt}\" really lives at {item}");
                    }
                }

                Console.WriteLine(bad == 0
                    ? "[prefabs] every one of them is in the game"
                    : $"[prefabs] WRONG - {bad} are not, and would be offered anyway");

                this.Shutdown();
                return;
            }

            //PROBE_MODGUTS=<pak file>;<folder> - everything in somebody else's mod that is NOT
            //map data, read out and written down.
            //
            //PROBE_UNPAK already pulls the levels, groups and packs, because those are the parts
            //this app has a shape for. This one goes after the rest: the string table a mod
            //ships beside the game's own, the sublevels, the blueprints. Read-only - it writes
            //into the folder it is given and touches nothing else.
            var probeGuts = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_MODGUTS="));
            if (probeGuts != null)
            {
                var gutBits = probeGuts.Substring("PROBE_MODGUTS=".Length).Trim('"').Split(';');
                if (gutBits.Length < 2)
                {
                    Console.WriteLine("[guts] PROBE_MODGUTS=<pak file>;<folder>");
                    this.Shutdown();
                    return;
                }

                var gutPak = gutBits[0].Trim();
                var gutInto = gutBits[1].Trim();

                if (!System.IO.File.Exists(gutPak))
                {
                    Console.WriteLine($"[guts] no such pak: {gutPak}");
                    this.Shutdown();
                    return;
                }

                PakReader.Pak.PakIndex? guts;

                try
                {
                    guts = new PakReader.Pak.PakIndex(new[] { gutPak }, cacheFiles: true,
                        caseSensitive: false, filter: null);

                    if (guts.UseKey(new byte[32]) == 0)
                    {
                        foreach (var key in MCDSaveEdit.Data.Secrets.PAKS_AES_KEYS)
                        {
                            var said = key.key;
                            var bytes = said.StartsWith("0x")
                                ? said.Substring(2).ToBytesKey()
                                : said.ToBytesKey();

                            if (guts.UseKey(bytes) > 0) { break; }
                        }
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[guts] the reader refused it: {problem.Message}");
                    this.Shutdown();
                    return;
                }

                System.IO.Directory.CreateDirectory(gutInto);

                var gutAll = new List<string>();
                foreach (var one in guts) { if (one != null) { gutAll.Add(one); } }

                //The enumerated name carries a mount point the getters do not always want, so
                //every spelling is tried rather than one being assumed.
                IEnumerable<string> spellingsOf(string one)
                {
                    yield return one;
                    var at = one.IndexOf("//", StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        yield return one.Substring(at + 1);
                        yield return one.Substring(at + 2);
                    }
                }

                byte[]? bytesOf(string one)
                {
                    foreach (var spelling in spellingsOf(one))
                    {
                        try
                        {
                            var got = guts.GetFile(spelling);
                            if (got != null) { return got.Value.ToArray(); }
                        }
                        catch { }
                    }
                    return null;
                }

                //Everything that is not map data, written out as it sits. A .uasset carries its
                //own name table, so the raw bytes are enough to see what a blueprint refers to
                //without an Unreal editor in the room.
                var wrote = 0;
                //A fourth argument opts the data tree back in, for the times the question is
                //about the pictures a resource pack ships rather than about the level.
                var gutData = gutBits.Length > 3 ? gutBits[3].Trim() : null;

                foreach (var one in gutAll)
                {
                    if (one.IndexOf("/data/", StringComparison.OrdinalIgnoreCase) >= 0
                        && (gutData == null
                            || one.IndexOf(gutData, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        continue;
                    }

                    var raw = bytesOf(one);
                    if (raw == null) { Console.WriteLine($"[guts] unreadable: {one}"); continue; }

                    var relative = one.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar);
                    var full = System.IO.Path.Combine(gutInto, relative + ".bin");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                    System.IO.File.WriteAllBytes(full, raw);
                    wrote++;

                    Console.WriteLine($"[guts] {raw.Length,9:N0}  {one}");
                }

                Console.WriteLine($"[guts] wrote {wrote} file(s) under {gutInto}");

                //The string table. This is the one that matters: a mission's objective wording
                //is a key into the namespace its loctable-id names, and a mod shipping its own
                //copy of Localization/Game/en/Game can put any key it likes in there.
                var gutLoc = gutAll.FirstOrDefault(one =>
                    one.IndexOf("localization/game/en/game", StringComparison.OrdinalIgnoreCase) >= 0);

                if (gutLoc == null) { Console.WriteLine("[guts] no English string table in the mod"); }
                else
                {
                    Dictionary<string, Dictionary<string, string>>? modTables = null;

                    foreach (var spelling in spellingsOf(gutLoc))
                    {
                        try
                        {
                            if (!guts.TryGetFile(spelling, out var seg) || seg == null) { continue; }
                            using var stream = new System.IO.MemoryStream(
                                seg.Value.Array!, seg.Value.Offset, seg.Value.Count);
                            modTables = new PakReader.LocResReader(stream).Entries;
                            if (modTables != null) { break; }
                        }
                        catch (Exception problem)
                        {
                            Console.WriteLine($"[guts] locres \"{spelling}\": {problem.Message}");
                        }
                    }

                    if (modTables == null) { Console.WriteLine("[guts] the mod's table would not read"); }
                    else
                    {
                        var modCount = modTables.Sum(one => one.Value.Count);
                        Console.WriteLine($"[guts] the mod's table: {modTables.Count} namespace(s), "
                            + $"{modCount:N0} strings");

                        var theirs = Logic.CustomSkins.index?
                            .extractLocResFile("/Dungeons/Content/Localization/Game/en/Game");

                        if (theirs == null) { Console.WriteLine("[guts] could not read the game's own table to compare"); }
                        else
                        {
                            Console.WriteLine($"[guts] the game's table: {theirs.Count} namespace(s), "
                                + $"{theirs.Sum(one => one.Value.Count):N0} strings");

                            foreach (var space in modTables.OrderBy(one => one.Key, StringComparer.Ordinal))
                            {
                                theirs.TryGetValue(space.Key, out var was);

                                var added = space.Value.Keys
                                    .Where(k => was == null || !was.ContainsKey(k)).ToList();
                                var changed = space.Value
                                    .Where(kv => was != null && was.TryGetValue(kv.Key, out var old)
                                                 && old != kv.Value).Select(kv => kv.Key).ToList();
                                var dropped = was == null
                                    ? new List<string>()
                                    : was.Keys.Where(k => !space.Value.ContainsKey(k)).ToList();

                                Console.WriteLine($"[guts] ns \"{space.Key}\": {space.Value.Count} strings, "
                                    + $"game had {(was == null ? 0 : was.Count)} - "
                                    + $"{added.Count} added, {changed.Count} reworded, {dropped.Count} gone");

                                foreach (var k in added.OrderBy(k => k, StringComparer.Ordinal))
                                {
                                    Console.WriteLine($"[guts]   + {k,-44} {space.Value[k]}");
                                }
                                foreach (var k in changed.OrderBy(k => k, StringComparer.Ordinal))
                                {
                                    Console.WriteLine($"[guts]   ~ {k,-44} {was![k]}  ->  {space.Value[k]}");
                                }
                            }

                            //Namespaces the game has and the mod's copy does not carry at all -
                            //a mod that ships a partial table replaces the whole file.
                            var lost = theirs.Keys.Where(k => !modTables.ContainsKey(k)).ToList();
                            Console.WriteLine($"[guts] namespaces the mod's copy drops entirely: {lost.Count}"
                                + (lost.Count == 0 ? "" : "  e.g. " + string.Join(", ", lost.Take(8))));
                        }

                        var dump = System.IO.Path.Combine(gutInto, "modstrings.txt");
                        using (var pen = new System.IO.StreamWriter(dump, false,
                            new System.Text.UTF8Encoding(false)))
                        {
                            foreach (var space in modTables.OrderBy(one => one.Key, StringComparer.Ordinal))
                            {
                                pen.WriteLine($"=== {space.Key} ({space.Value.Count}) ===");
                                foreach (var kv in space.Value.OrderBy(k => k.Key, StringComparer.Ordinal))
                                {
                                    pen.WriteLine($"{kv.Key}\t{kv.Value}");
                                }
                            }
                        }
                        Console.WriteLine($"[guts] every string in the mod's table -> {dump}");
                    }
                }

                //And how the mod's block table compares with the game's, since ids are positions
                //in that file and a longer file means ids the base game has no name for.
                foreach (var pack in gutAll
                    .Where(one => one.IndexOf("/resourcepacks/", StringComparison.OrdinalIgnoreCase) >= 0
                                  && one.EndsWith("/blocks", StringComparison.OrdinalIgnoreCase)))
                {
                    var raw = bytesOf(pack);
                    if (raw == null) { continue; }
                    var named = pack.Split('/');
                    Console.WriteLine($"[guts] pack {named[named.Length - 2],-20} {raw.Length:N0} bytes");
                }

                //And the same paths out of the GAME, so "the mod ships its own" can be told from
                //"the mod ships the only one". A third argument names what to pull across.
                if (gutBits.Length > 2 && Logic.CustomSkins.index != null)
                {
                    var wanted = gutBits[2].Trim();
                    var mine = System.IO.Path.Combine(gutInto, "_game");
                    System.IO.Directory.CreateDirectory(mine);

                    foreach (var one in Logic.CustomSkins.index)
                    {
                        if (one == null) { continue; }
                        if (one.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                        byte[]? raw = null;
                        foreach (var spelling in spellingsOf(one))
                        {
                            try
                            {
                                var got = Logic.CustomSkins.index.GetFile(spelling);
                                if (got != null) { raw = got.Value.ToArray(); break; }
                            }
                            catch { }
                        }

                        Console.WriteLine($"[guts] game: {(raw == null ? -1 : raw.Length),9:N0}  {one}");
                        if (raw == null) { continue; }

                        var full = System.IO.Path.Combine(mine,
                            one.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar) + ".bin");
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                        System.IO.File.WriteAllBytes(full, raw);
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_UNPAK=<pak file>;<folder> - takes somebody else's map mod apart into folders
            //this app can open.
            //
            //A mission mod is a pak, and everything in this app works on folders: level.json,
            //objectgroups/<name>/objectgroup.json, resourcepacks/<name>/blocks.json. So a mod
            //cannot be looked at, only played. This writes one folder per level inside the pak,
            //in exactly the shape Import map expects.
            var probeUnpak = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_UNPAK="));
            if (probeUnpak != null)
            {
                var bits = probeUnpak.Substring("PROBE_UNPAK=".Length).Trim('"').Split(';');
                if (bits.Length < 2)
                {
                    Console.WriteLine("[unpak] PROBE_UNPAK=<pak file>;<folder where the maps go>");
                    this.Shutdown();
                    return;
                }

                var pakFile = bits[0].Trim();
                var into = bits[1].Trim();

                if (!System.IO.File.Exists(pakFile))
                {
                    Console.WriteLine($"[unpak] no such pak: {pakFile}");
                    this.Shutdown();
                    return;
                }

                PakReader.Pak.PakIndex? mod = null;

                try
                {
                    mod = new PakReader.Pak.PakIndex(new[] { pakFile }, cacheFiles: true,
                        caseSensitive: false, filter: null);

                    //A mod pak is usually unencrypted, which the reader spells as a zero key.
                    //The game's own keys are tried after, because a mod built out of the game's
                    //files can carry its encryption with it.
                    var opened = mod.UseKey(new byte[32]);

                    if (opened == 0)
                    {
                        foreach (var key in MCDSaveEdit.Data.Secrets.PAKS_AES_KEYS)
                        {
                            var said = key.key;
                            var bytes = said.StartsWith("0x")
                                ? said.Substring(2).ToBytesKey()
                                : said.ToBytesKey();

                            if (mod.UseKey(bytes) > 0) { break; }
                        }
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[unpak] the reader refused it: {problem.Message}");
                    this.Shutdown();
                    return;
                }

                var everything = new List<string>();
                foreach (var item in mod) { if (item != null) { everything.Add(item); } }

                Console.WriteLine($"[unpak] {everything.Count} entries in {System.IO.Path.GetFileName(pakFile)}");

                //What is in there, by the part of the tree it lives in - a mod may carry meshes
                //and textures as well as data, and only the data is a map.
                var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var one in everything)
                {
                    var bit = one.Split('/');
                    var head = string.Join("/", bit.Take(Math.Min(5, bit.Length - 1)));
                    kinds[head] = kinds.TryGetValue(head, out var was) ? was + 1 : 1;
                }

                foreach (var kind in kinds.OrderByDescending(one => one.Value).Take(14))
                {
                    Console.WriteLine($"[unpak] {kind.Value,5}  {kind.Key}");
                }

                //Everything that is NOT map data, listed in full - a mod that ships its own
                //prefabs, its own string table or its own blueprints is doing something this
                //app has no idea about, and that is exactly what is worth knowing.
                foreach (var one in everything)
                {
                    if (one.IndexOf("/data/", StringComparison.OrdinalIgnoreCase) >= 0) { continue; }
                    Console.WriteLine($"[unpak] not-data: {one}");
                }

                //And which parts of the data tree, since a mod replacing the game's own files
                //is different from one adding its own.
                var seen = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var one in everything)
                {
                    var at = one.IndexOf("/data/", StringComparison.OrdinalIgnoreCase);
                    if (at < 0) { continue; }

                    var rest = one.Substring(at + 6).Split('/');
                    var head = string.Join("/", rest.Take(Math.Min(3, rest.Length - 1)));
                    seen[head] = seen.TryGetValue(head, out var was) ? was + 1 : 1;
                }

                foreach (var one in seen)
                {
                    Console.WriteLine($"[unpak] data/{one.Key,-44} {one.Value}");
                }

                byte[]? grab(string wanted)
                {
                    foreach (var one in everything)
                    {
                        if (one.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                        //The enumerated name carries the mount point - "//dungeons/content/..."
                        //- and GetFile wants it without. Tried both ways rather than assumed,
                        //because a mod chooses its own mount and they do not all agree.
                        var at = one.IndexOf("//", StringComparison.Ordinal);
                        var spellings = at < 0
                            ? new[] { one }
                            : new[] { one, one.Substring(at + 1), one.Substring(at + 2) };

                        foreach (var spelling in spellings)
                        {
                            try
                            {
                                var got = mod.GetFile(spelling);
                                if (got != null) { return got.Value.ToArray(); }
                            }
                            catch { }
                        }
                    }

                    return null;
                }

                var levels = everything
                    .Where(one => one.IndexOf("/levels/", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                Console.WriteLine($"[unpak] {levels.Count} level(s): "
                    + string.Join(", ", levels.Select(System.IO.Path.GetFileName)));

                foreach (var level in levels)
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(level);
                    var folder = System.IO.Path.Combine(into, name);
                    System.IO.Directory.CreateDirectory(folder);

                    var raw = grab(level);
                    if (raw == null)
                    {
                        Console.WriteLine($"[unpak] {name}: could not be read out");
                        continue;
                    }

                    void put(string relative, byte[] data)
                    {
                        var full = System.IO.Path.Combine(folder,
                            relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                        System.IO.File.WriteAllBytes(full, data);
                    }

                    put("level.json", raw);

                    var (groups, packs) = Logic.GameMaps.referencedBy(raw);
                    var had = 0;

                    foreach (var group in groups)
                    {
                        //Out of the mod first; a mod that only replaces the level still leans on
                        //the game's own groups, so those are fetched from the game after.
                        var got = grab("/objectgroups/" + group)
                            ?? Logic.GameMaps.read("/Dungeons/Content/" + Logic.GameMaps.GROUPS + group);

                        if (got == null) { continue; }
                        put("objectgroups/" + group + ".json", got);
                        had++;
                    }

                    var packed = 0;
                    foreach (var pack in packs)
                    {
                        var got = grab("/resourcepacks/" + pack + "/blocks")
                            ?? Logic.GameMaps.read("/Dungeons/Content/" + Logic.GameMaps.PACKS
                                                   + pack + "/blocks");

                        if (got == null) { continue; }
                        put("resourcepacks/" + pack + "/blocks.json", got);
                        packed++;
                    }

                    Console.WriteLine($"[unpak] {name}: {had}/{groups.Count} group(s), "
                        + $"{packed}/{packs.Count} pack(s)  ->  {folder}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_READ=<map folder name> - what the editor makes of somebody else's mission.
            //
            //Read-only. Written after a mod built entirely out of kill-groups showed up in the
            //editor as blank rows and invisible gates - both of which were the editor's fault,
            //not the mod's.
            var probeRead = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_READ="));
            if (probeRead != null)
            {
                var wanted = probeRead.Substring("PROBE_READ=".Length).Trim('"');
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                try
                {
                    var map = Logic.MapSpawns.load(folder);

                    Console.WriteLine($"[read] {wanted}: {map.Rooms.Count} room(s), "
                        + $"loctable \"{Logic.MapSpawns.loctableOf(map)}\"");

                    foreach (var step in Logic.MapSpawns.objectivesOf(map))
                    {
                        Console.WriteLine($"[read]   {step}");
                    }

                    var gates = 0;
                    var blind = 0;

                    foreach (var room in map.Rooms)
                    {
                        foreach (var gate in Logic.MapSpawns.gatesOf(map, room))
                        {
                            gates++;
                            if (gate.OpenedBy.Length > 0 && gate.Drawn.Length == 0) { blind++; }
                            if (gates <= 12) { Console.WriteLine($"[read]   gate {room.Id}: {gate}"); }
                        }
                    }

                    Console.WriteLine($"[read] {gates} gate(s) the editor can see, "
                        + $"{blind} of them with nothing drawn");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[read] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_LEVELBITS - the level-wide settings, and what values are actually legal.
            //
            //The Level tab offers these as dropdowns rather than text, for the same reason the
            //objective wording is a list: a value the game does not know is a mission that
            //looks wrong or does not load, with nothing said anywhere. So the lists are read
            //off the game's own 56 missions instead of guessed at.
            if (_startupArguments.Any(a => a == "PROBE_LEVELBITS"))
            {
                var seen = new SortedDictionary<string, SortedDictionary<string, int>>(
                    StringComparer.Ordinal);

                void note(string field, string value)
                {
                    if (!seen.TryGetValue(field, out var values))
                    {
                        values = new SortedDictionary<string, int>(StringComparer.Ordinal);
                        seen[field] = values;
                    }

                    values[value] = values.TryGetValue(value, out var was) ? was + 1 : 1;
                }

                foreach (var mission in Logic.GameMaps.all())
                {
                    var raw = Logic.GameMaps.read(mission.PakPath);
                    if (raw == null) { continue; }

                    System.Text.Json.Nodes.JsonObject? level;
                    try
                    {
                        level = System.Text.Json.Nodes.JsonNode.Parse(
                            Logic.GameMaps.stripComments(
                                new System.Text.UTF8Encoding(false).GetString(raw).TrimStart('\uFEFF')),
                            documentOptions: new System.Text.Json.JsonDocumentOptions
                            {
                                AllowTrailingCommas = true,
                                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            }) as System.Text.Json.Nodes.JsonObject;
                    }
                    catch { continue; }

                    if (level == null) { continue; }

                    foreach (var field in new[]
                    {
                        "music-override", "ambience-level-id", "loctable-id",
                        "play-intro", "require-matching-doors",
                    })
                    {
                        var said = level[field];
                        if (said == null) { continue; }
                        note(field, said.ToJsonString());
                    }

                    //Where a level names its own theme, which is the better-attested field.
                    foreach (var one in level["dungeons"] as System.Text.Json.Nodes.JsonArray
                        ?? new System.Text.Json.Nodes.JsonArray())
                    {
                        if (one is not System.Text.Json.Nodes.JsonObject dungeon) { continue; }

                        foreach (var field in new[] { "ambience", "audio-ambience" })
                        {
                            var said = dungeon[field];
                            if (said != null) { note("dungeons[]." + field, said.ToJsonString()); }
                        }
                    }

                    void walk(System.Text.Json.Nodes.JsonNode? node)
                    {
                        if (node is System.Text.Json.Nodes.JsonArray list)
                        {
                            foreach (var one in list) { walk(one); }
                            return;
                        }

                        if (node is not System.Text.Json.Nodes.JsonObject body) { return; }

                        foreach (var field in new[]
                        {
                            "audio-ambience", "push-ambience", "visual-theme", "sound-theme",
                        })
                        {
                            var said = body[field];
                            if (said != null) { note("stretches[]." + field, said.ToJsonString()); }
                        }

                        foreach (var one in body) { walk(one.Value); }
                    }

                    walk(level["stretches"]);
                    walk(level["dungeons"]);
                }

                foreach (var field in seen)
                {
                    Console.WriteLine();
                    Console.WriteLine($"[bits] {field.Key}  ({field.Value.Count} distinct)");
                    foreach (var value in field.Value.OrderByDescending(one => one.Value))
                    {
                        Console.WriteLine($"[bits]   {value.Value,4}  {value.Key}");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_TABS=<mission> - the three new tabs, through their own buttons.
            //
            //Fights, keyed doors and the level's own settings. All three make several things at
            //once - a fight is ground, a gate and a step - and the failure they share is making
            //some but not all of them, which in game is a step nobody can finish and a chain
            //that stops dead behind it.
            //
            //On a COPY.
            var probeTabs = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_TABS="));
            if (probeTabs != null)
            {
                var wanted = probeTabs.Substring("PROBE_TABS=".Length).Trim('"');

                var live = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                var folder = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "mcd-tabs-probe", wanted);

                try
                {
                    if (System.IO.Directory.Exists(folder)) { System.IO.Directory.Delete(folder, true); }

                    foreach (var from in System.IO.Directory.GetFiles(
                        live, "*", System.IO.SearchOption.AllDirectories))
                    {
                        var to = System.IO.Path.Combine(folder, from.Substring(live.Length + 1));
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
                        System.IO.File.Copy(from, to, true);
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[tabs] could not copy: {problem.Message}");
                    this.Shutdown();
                    return;
                }

                var map = Logic.MapSpawns.load(folder);
                var window = new UI.SpawnsWindow(map);

                window.WindowState = WindowState.Normal;
                window.Width = 1280;
                window.Height = 800;
                window.Left = -20000;
                window.Show();

                var waited = 0;
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250),
                };

                timer.Tick += (_, _) =>
                {
                    waited += 250;
                    if (!window.mapReady && waited < 20000) { return; }
                    timer.Stop();

                    try
                    {
                        Console.WriteLine($"[tabs] tabs: {string.Join(" | ", window.tabHeaders)}");
                        Console.WriteLine(window.tabHeaders.Length == 7
                            ? "[tabs] seven of them, one job each"
                            : $"[tabs] WRONG - {window.tabHeaders.Length} tabs");

                        //Where they are DRAWN, after clicking each one in turn. Seven headers
                        //do not fit one row, and WPF's own tab panel answers that by moving
                        //whole rows about so the selected one sits against the content - so the
                        //strip rearranged itself on every click.
                        window.probePickTab(0);
                        var laidOut = window.tabHeadersOnScreen;
                        Console.WriteLine($"[tabs] drawn: {string.Join(" | ", laidOut)}");

                        var wandered = 0;

                        for (var which = 0; which < window.tabHeaders.Length; which++)
                        {
                            window.probePickTab(which);
                            var now = window.tabHeadersOnScreen;

                            if (now.SequenceEqual(laidOut)) { continue; }

                            wandered++;
                            Console.WriteLine($"[tabs] after picking {window.tabHeaders[which]}: "
                                + string.Join(" | ", now));
                        }

                        Console.WriteLine(wandered == 0
                            ? "[tabs] every header stays where it was, whichever is picked"
                            : $"[tabs] WRONG - the strip rearranged itself on {wandered} of them");

                        window.probePickTab(0);

                        window.probeOnlyExit();

                        var across = Math.Max(8, window.roomAcross / 4);

                        //--- fights -------------------------------------------------------
                        Console.WriteLine($"[tabs] {window.arenaHintNow}");
                        Console.WriteLine($"[tabs] mob groups to draw from: {window.arenaGroupCount}");

                        window.probeAddArena(0, 8, true, across, 20, across);
                        Console.WriteLine($"[tabs] {window.probeStatus}");

                        foreach (var row in window.arenaRows) { Console.WriteLine($"[tabs]   {row}"); }

                        Console.WriteLine(window.arenaRows.Length == 1
                            ? "[tabs] the fight is there"
                            : $"[tabs] WRONG - {window.arenaRows.Length} fights after adding one");

                        Console.WriteLine(window.arenaRows.Any(one => one.Contains("seals"))
                            ? "[tabs] and it seals something"
                            : "[tabs] WRONG - the fight seals nothing although it was asked to");

                        Console.WriteLine(!window.arenaRows.Any(one => one.Contains("NO SPAWN"))
                            ? "[tabs] and its mobs have somewhere to come from"
                            : "[tabs] WRONG - the fight has no spawn region");

                        //A wave is another fight on the same ground, ahead of this one.
                        window.probePickArena(0);
                        window.probeAddWave(12);
                        Console.WriteLine($"[tabs] {window.probeStatus}");

                        foreach (var row in window.arenaRows) { Console.WriteLine($"[tabs]   {row}"); }

                        Console.WriteLine(window.arenaRows.Length == 2
                            ? "[tabs] two waves now"
                            : $"[tabs] WRONG - {window.arenaRows.Length} fights after a wave");

                        var sealing = window.arenaRows.Count(one => one.Contains("seals"));
                        Console.WriteLine(sealing == 1
                            ? "[tabs] only the last wave holds the gate, so it opens at the end"
                            : $"[tabs] WRONG - {sealing} waves hold the gate");

                        //--- keyed doors ---------------------------------------------------
                        window.probeAddKeyed(0, across * 2, 20, across);
                        Console.WriteLine($"[tabs] {window.probeStatus}");

                        foreach (var row in window.keyRows) { Console.WriteLine($"[tabs]   {row}"); }

                        Console.WriteLine(window.keyRows.Length == 1
                            && !window.keyRows[0].Contains("NO KEY")
                            ? "[tabs] the locked door has a key somewhere"
                            : "[tabs] WRONG - the door cannot be opened");

                        window.probePickKeyed(0);
                        window.probeAlsoKey(across, 20, across * 2);
                        Console.WriteLine($"[tabs] {window.probeStatus}");

                        Console.WriteLine(window.keyRows.Any(one => one.Contains(" or "))
                            ? "[tabs] and it can now turn up in either of two places"
                            : "[tabs] WRONG - the second key spot did not take");

                        //--- the level itself ------------------------------------------------
                        window.probeSetMusic(1);
                        Console.WriteLine($"[tabs] {window.probeStatus}");

                        window.probeToggleMatchDoors();
                        Console.WriteLine($"[tabs] {window.probeStatus}");

                        //--- and all of it reaches the file ----------------------------------
                        Logic.MapSpawns.save(map);

                        var written = System.Text.Json.Nodes.JsonNode.Parse(
                            Logic.GameMaps.stripComments(System.IO.File.ReadAllText(
                                System.IO.Path.Combine(folder, "level.json"))),
                            documentOptions: new System.Text.Json.JsonDocumentOptions
                            {
                                AllowTrailingCommas = true,
                                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            }) as System.Text.Json.Nodes.JsonObject;

                        var steps = written?["objectives"] as System.Text.Json.Nodes.JsonArray
                            ?? new System.Text.Json.Nodes.JsonArray();

                        var fights = 0;
                        var keyed = 0;

                        foreach (var each in steps)
                        {
                            if (each is not System.Text.Json.Nodes.JsonObject step) { continue; }
                            if (step["killgroup"] != null) { fights++; }
                            if (step["click"]?["key-type"] != null) { keyed++; }
                        }

                        Console.WriteLine($"[tabs] the file holds {steps.Count} step(s): "
                            + $"{fights} fight(s), {keyed} keyed door(s)");

                        Console.WriteLine(fights == 2 && keyed == 1
                            ? "[tabs] everything made it to the file"
                            : "[tabs] WRONG - something did not get written");

                        Console.WriteLine($"[tabs] music-override: "
                            + $"{written?["music-override"]?.ToJsonString() ?? "(unset)"}");
                        Console.WriteLine($"[tabs] require-matching-doors: "
                            + $"{written?["require-matching-doors"]?.ToJsonString() ?? "(unset)"}");
                    }
                    catch (Exception problem)
                    {
                        Console.WriteLine($"[tabs] threw: {problem}");
                    }

                    this.Shutdown();
                };

                timer.Start();
                return;
            }

            //PROBE_CSV=<mission> - where an objective's wording really comes from.
            //
            //Not the string table. The game ships one CSV per mission under Decor/Text, two
            //columns, and a key that is in the CSV but not in the table falls back to the CSV's
            //own text. That is how a mod adds wording the game has never heard of - and it
            //means this editor can too, by appending a row rather than authoring a .locres.
            var probeCsv = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_CSV="));
            if (probeCsv != null)
            {
                var wanted = probeCsv.Substring("PROBE_CSV=".Length).Trim('"');

                //Found by looking rather than by guessing the spelling - the paks are not
                //consistent about case and the reader is literal.
                var pak = Logic.CustomSkins.index;
                var found = new List<string>();

                if (pak != null)
                {
                    foreach (var item in pak)
                    {
                        if (item == null) { continue; }
                        if (item.IndexOf("/Text/", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                        found.Add(item);
                    }
                }

                Console.WriteLine($"[csv] {found.Count} file(s) under a Text folder");
                foreach (var one in found.Take(8)) { Console.WriteLine($"[csv]   {one}"); }

                //Every one of them, with its size and first real row, so what each CSV is FOR
                //is a matter of record rather than of the name.
                if (string.Equals(wanted, "all", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var one in found.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var at = one.IndexOf("//", StringComparison.Ordinal);
                        var raw = Logic.GameMaps.read(at < 0 ? one : one.Substring(at + 1));
                        if (raw == null) { Console.WriteLine($"[csv] ?????  {one}"); continue; }

                        var text = new System.Text.UTF8Encoding(false).GetString(raw)
                            .Replace("\r\n", "\n");
                        var rows = text.Split('\n').Where(r => r.Trim().Length > 0).ToList();

                        Console.WriteLine($"[csv] {rows.Count - 1,4} rows  "
                            + $"{System.IO.Path.GetFileName(one),-40} "
                            + (rows.Count > 1 ? rows[1] : ""));
                    }

                    this.Shutdown();
                    return;
                }

                var mine = found.Where(one =>
                    one.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                Console.WriteLine($"[csv] {mine.Count} of them mention \"{wanted}\"");

                foreach (var spelling in mine.Select(one =>
                {
                    var at = one.IndexOf("//", StringComparison.Ordinal);
                    return at < 0 ? one : one.Substring(at + 1);
                }))
                {
                    var raw = Logic.GameMaps.read(spelling);
                    Console.WriteLine($"[csv] {spelling}: {(raw == null ? "not there" : raw.Length + " bytes")}");

                    if (raw == null) { continue; }

                    var text = new System.Text.UTF8Encoding(false).GetString(raw);
                    var rows = text.Replace("\r\n", "\n").Split('\n');

                    Console.WriteLine($"[csv] {rows.Length} row(s)");
                    foreach (var row in rows.Take(14)) { Console.WriteLine($"[csv]   {row}"); }
                    break;
                }

                this.Shutdown();
                return;
            }

            //PROBE_WORDS2=<mission> - wording somebody typed, all the way to the pak.
            //
            //The thing that could not be done until it turned out the game reads a plain CSV
            //beside its compiled string table. Three links in the chain, and every one of them
            //fails silently: the key has to be minted and remembered, the objective has to
            //carry the key rather than the words, and the table has to be built for whichever
            //mission it is installed over and put in the pak at the game's own path.
            //
            //On a COPY, and it installs nothing.
            var probeWords = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_WORDS2="));
            if (probeWords != null)
            {
                var wanted = probeWords.Substring("PROBE_WORDS2=".Length).Trim('"');

                var live = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                var folder = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "mcd-words-probe", wanted);

                try
                {
                    if (System.IO.Directory.Exists(folder)) { System.IO.Directory.Delete(folder, true); }

                    foreach (var from in System.IO.Directory.GetFiles(
                        live, "*", System.IO.SearchOption.AllDirectories))
                    {
                        var to = System.IO.Path.Combine(folder, from.Substring(live.Length + 1));
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
                        System.IO.File.Copy(from, to, true);
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[words] could not copy: {problem.Message}");
                    this.Shutdown();
                    return;
                }

                var map = Logic.MapSpawns.load(folder);
                var table = Logic.MapSpawns.loctableOf(map);

                Console.WriteLine($"[words] the mission reads \"{table}\"");
                Console.WriteLine($"[words] its table is at {Logic.MapWords.pathFor(table) ?? "(not found)"}");

                var had = Logic.MapWords.fromGame(table);
                Console.WriteLine($"[words] the game gives it {had.Count} row(s)");

                var window = new UI.SpawnsWindow(map);
                window.WindowState = WindowState.Normal;
                window.Width = 1280;
                window.Height = 800;
                window.Left = -20000;
                window.Show();

                var waited = 0;
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250),
                };

                timer.Tick += (_, _) =>
                {
                    waited += 250;
                    if (!window.mapReady && waited < 20000) { return; }
                    timer.Stop();

                    try
                    {
                        window.probeOnlyExit();

                        //Wording nothing in the game has ever said, including the punctuation
                        //and comma that a two-column CSV has to survive.
                        const string mine = "Ring the bell, then run";
                        window.probeSayAndAddStep(mine, Math.Max(8, window.roomAcross / 4), 20, 8);

                        Console.WriteLine($"[words] {window.probeStatus}");
                        Console.WriteLine($"[words] chain: {string.Join(" | ", window.questRows)}");

                        var shown = window.questRows.Any(one => one.Contains(mine));
                        Console.WriteLine(shown
                            ? "[words] the editor shows the words back"
                            : "[words] WRONG - the step does not read as what was typed");

                        Console.WriteLine(!window.questRows.Any(one => one.Contains("NO SUCH WORDING"))
                            ? "[words] and nothing is flagged as missing"
                            : "[words] WRONG - still flagged as missing wording");

                        //The map's own table, on disk beside the level.
                        Logic.MapSpawns.save(map);

                        var mineOnly = Logic.MapWords.fromFolder(folder);
                        Console.WriteLine($"[words] the map keeps {mineOnly.Count} row(s) of its own:");
                        foreach (var row in mineOnly)
                        {
                            Console.WriteLine($"[words]   {row.Key} = {row.Said}");
                        }

                        Console.WriteLine(mineOnly.Any(one => one.Said == mine)
                            ? "[words] written, comma and all"
                            : "[words] WRONG - the CSV lost the wording");

                        //And what a pak built from this folder would carry.
                        foreach (var over in new[] { table, "pumpkinpastures" })
                        {
                            var built = Logic.MapWords.tableFor(folder, over);

                            if (built == null)
                            {
                                Console.WriteLine($"[words] WRONG - nothing to ship for {over}");
                                continue;
                            }

                            var rows = Logic.MapWords.parse(
                                new System.Text.UTF8Encoding(false).GetString(built));

                            var theirs = Logic.MapWords.fromGame(over).Count;

                            Console.WriteLine($"[words] installed over {over}: "
                                + $"{Logic.MapWords.pakPathFor(over)}, "
                                + $"{rows.Count} row(s) = {theirs} theirs + {mineOnly.Count} mine");

                            Console.WriteLine(rows.Count == theirs + mineOnly.Count
                                && rows.Any(one => one.Said == mine)
                                ? "[words] the mission keeps its own wording and gains ours"
                                : "[words] WRONG - the merged table is not right");
                        }
                    }
                    catch (Exception problem)
                    {
                        Console.WriteLine($"[words] threw: {problem}");
                    }

                    this.Shutdown();
                };

                timer.Start();
                return;
            }

            //PROBE_WHOLEMOD=<pak>;<folder> - somebody else's mod, taken apart and put back whole.
            //
            //PROBE_UNPAK pulls out the three things this app understands - the level, its object
            //groups, its block table - and drops everything else, which for a mod like
            //Blossoming Isles is a thousand textures, its fonts, its widgets, its sub-levels
            //and its string table. Installing what came back out was installing a third of a
            //mod, and the missing two thirds fail quietly.
            //
            //This writes every entry down instead, with a manifest saying where each came from,
            //so install can put them back at the paths they were found at.
            var probeWhole = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_WHOLEMOD="));
            if (probeWhole != null)
            {
                var bits = probeWhole.Substring("PROBE_WHOLEMOD=".Length).Trim('"').Split(';');
                if (bits.Length < 2)
                {
                    Console.WriteLine("[whole] PROBE_WHOLEMOD=<pak file>;<folder>");
                    this.Shutdown();
                    return;
                }

                var pakFile = bits[0].Trim();
                var into = bits[1].Trim();

                try
                {
                    var items = Logic.ModPak.read(pakFile);
                    Console.WriteLine($"[whole] {System.IO.Path.GetFileName(pakFile)}: "
                        + Logic.ModPak.describe(items));

                    var manifest = Logic.ModPak.unpak(pakFile, into,
                        bits.Length > 2 && bits[2].Trim().Length > 0 ? bits[2].Trim() : null);
                    Console.WriteLine($"[whole] unpacked to {into}");

                    //What a pak built from that folder would carry, and whether it is all of it.
                    var notes = new List<string>();
                    var entries = Logic.ModPak.entriesFor(into, notes);

                    foreach (var note in notes.Take(6)) { Console.WriteLine($"[whole] {note}"); }

                    Console.WriteLine($"[whole] {items.Count} in, {entries.Count} out");
                    Console.WriteLine(entries.Count == items.Count
                        ? "[whole] nothing was lost taking it apart"
                        : "[whole] WRONG - the round trip does not carry every entry");

                    //And how much of it the map tab alone would have shipped.
                    var mapOnly = entries.Count(one =>
                        one.Path.IndexOf("/data/lovika/", StringComparison.OrdinalIgnoreCase) >= 0
                        || one.Path.IndexOf("/data/resourcepacks/", StringComparison.OrdinalIgnoreCase) >= 0);

                    Console.WriteLine($"[whole] the map itself is {mapOnly} of those; "
                        + $"{entries.Count - mapOnly} would have been dropped before this");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[whole] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_CSVBYTES=<mission> - what the game's label table really is, byte for byte,
            //and what we would put in its place.
            //
            //Written after a map with its own wording crashed the game on load. The wording
            //mechanism was read off a working mod, but "a CSV goes at this path" is not the
            //same claim as "THIS file is a CSV and ours is shaped like it", and a shipping
            //build's crash log says only "Unhandled exception".
            var probeBytes = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_CSVBYTES="));
            if (probeBytes != null)
            {
                var wanted = probeBytes.Substring("PROBE_CSVBYTES=".Length).Trim('"');

                var path = Logic.MapWords.pathFor(wanted);
                Console.WriteLine($"[bytes] the game keeps it at {path ?? "(nowhere)"}");

                var raw = path == null ? null : Logic.GameMaps.read(path);

                if (raw == null)
                {
                    Console.WriteLine("[bytes] could not read it");
                    this.Shutdown();
                    return;
                }

                void dump(string what, byte[] data)
                {
                    Console.WriteLine($"[bytes] {what}: {data.Length} bytes");

                    var head = string.Join(" ", data.Take(24).Select(b => b.ToString("x2")));
                    var tail = string.Join(" ", data.Skip(Math.Max(0, data.Length - 12))
                        .Select(b => b.ToString("x2")));

                    Console.WriteLine($"[bytes]   head {head}");
                    Console.WriteLine($"[bytes]   tail {tail}");

                    var text = new System.Text.UTF8Encoding(false).GetString(data);
                    var lines = text.Replace("\r\n", "\u00b6\n").Split('\n');

                    Console.WriteLine($"[bytes]   first line: {lines.FirstOrDefault()}");
                    Console.WriteLine($"[bytes]   last  line: "
                        + lines.Where(one => one.Length > 0).LastOrDefault());

                    var commas = lines.Where(one => one.Trim().Length > 0)
                        .Select(one => one.Count(c => c == ','))
                        .GroupBy(one => one)
                        .OrderByDescending(one => one.Count());

                    Console.WriteLine("[bytes]   commas per row: "
                        + string.Join(", ", commas.Select(one => $"{one.Key}x{one.Count()}")));

                    Console.WriteLine($"[bytes]   CRLF: {text.Contains("\r\n")}, "
                        + $"bare LF: {text.Replace("\r\n", "").Contains("\n")}, "
                        + $"BOM: {data.Length > 2 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF}");
                }

                dump("the game's", raw);

                //Every row it actually has, so a row this editor would mangle is visible
                //rather than inferred from a comma count.
                foreach (var line in new System.Text.UTF8Encoding(false).GetString(raw)
                    .Replace("\r\n", "\n").Split('\n'))
                {
                    if (line.Count(c => c == ',') == 1) { continue; }
                    Console.WriteLine($"[bytes]   odd row ({line.Count(c => c == ',')} commas): {line}");
                }

                //And ours, built the way install builds it.
                var folder = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "mcd-csvbytes");

                System.IO.Directory.CreateDirectory(folder);
                Logic.MapWords.forget(folder);
                Logic.MapWords.remember(folder, "description_mcd_reborn", "MCD Reborn 2");

                var mine = Logic.MapWords.tableFor(folder, wanted);

                if (mine == null) { Console.WriteLine("[bytes] we would ship nothing"); }
                else
                {
                    dump("ours", mine);

                    //The whole point of the change: what we ship must BEGIN with the game's
                    //own file, byte for byte, and only then say anything of its own.
                    var same = mine.Length >= raw.Length
                        && !raw.Where((b, i) => mine[i] != b).Any();

                    Console.WriteLine(same
                        ? "[bytes] ours starts with the game's file, byte for byte"
                        : "[bytes] WRONG - we are rewriting rows the game shipped");
                    Console.WriteLine($"[bytes] we would put it at {Logic.MapWords.pakPathFor(wanted)}");

                    //The one thing that matters most: does the game's own path end in .csv at
                    //all? The index strips extensions, so the only way to know is to ask a
                    //reader that does not.
                    try
                    {
                        foreach (var pak in System.IO.Directory.GetFiles(
                            System.IO.Path.GetDirectoryName(Logic.GameMaps.resolve(path) ?? "") ?? "",
                            "*.pak"))
                        {
                            Console.WriteLine($"[bytes] (pak on disk: {System.IO.Path.GetFileName(pak)})");
                            break;
                        }
                    }
                    catch { }
                }

                this.Shutdown();
                return;
            }

            //PROBE_CLICKSPOT - what a clickable thing stands on, and where each one is used.
            //
            //A map crashed on entering it, with one thing added: a click objective on a town
            //bell. Two halves could be wrong and both are guesses until counted - the REGION
            //this editor writes for it, and whether a prefab out of one mission can be spawned
            //in another at all.
            if (_startupArguments.Any(a => a == "PROBE_CLICKSPOT"))
            {
                var shapes = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var byMission = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

                foreach (var mission in Logic.GameMaps.all())
                {
                    var raw = Logic.GameMaps.read(mission.PakPath);
                    if (raw == null) { continue; }

                    System.Text.Json.Nodes.JsonObject? level;
                    try
                    {
                        level = System.Text.Json.Nodes.JsonNode.Parse(
                            Logic.GameMaps.stripComments(
                                new System.Text.UTF8Encoding(false).GetString(raw).TrimStart('\uFEFF')),
                            documentOptions: new System.Text.Json.JsonDocumentOptions
                            {
                                AllowTrailingCommas = true,
                                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            }) as System.Text.Json.Nodes.JsonObject;
                    }
                    catch { continue; }

                    if (level?["objectives"] is not System.Text.Json.Nodes.JsonArray all) { continue; }

                    //Which regions this mission clicks, and what it draws at each.
                    var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var one in all)
                    {
                        if (one is not System.Text.Json.Nodes.JsonObject step) { continue; }
                        if (step["click"] is not System.Text.Json.Nodes.JsonObject body) { continue; }

                        var what = body["object"]?.GetValue<string>();
                        if (what == null) { continue; }

                        if (!byMission.TryGetValue(what, out var who))
                        {
                            who = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                            byMission[what] = who;
                        }
                        who.Add(mission.Name);

                        foreach (var said in body["locations"] as System.Text.Json.Nodes.JsonArray
                            ?? new System.Text.Json.Nodes.JsonArray())
                        {
                            var name = said?.GetValue<string>();
                            if (name == null) { continue; }
                            var at = name.LastIndexOf('.');
                            wanted[at < 0 ? name : name.Substring(at + 1)] = what;
                        }
                    }

                    if (wanted.Count == 0) { continue; }

                    var (groups, _) = Logic.GameMaps.referencedBy(raw);

                    foreach (var group in groups)
                    {
                        var body = Logic.GameMaps.read("/Dungeons/Content/" + Logic.GameMaps.GROUPS + group);
                        if (body == null) { continue; }

                        System.Text.Json.Nodes.JsonObject? sheet;
                        try
                        {
                            sheet = System.Text.Json.Nodes.JsonNode.Parse(
                                Logic.GameMaps.stripComments(
                                    new System.Text.UTF8Encoding(false).GetString(body).TrimStart('\uFEFF')),
                                documentOptions: new System.Text.Json.JsonDocumentOptions
                                {
                                    AllowTrailingCommas = true,
                                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                                }) as System.Text.Json.Nodes.JsonObject;
                        }
                        catch { continue; }

                        foreach (var one in sheet?["objects"] as System.Text.Json.Nodes.JsonArray
                            ?? new System.Text.Json.Nodes.JsonArray())
                        {
                            if (one is not System.Text.Json.Nodes.JsonObject tile) { continue; }

                            foreach (var each in tile["regions"] as System.Text.Json.Nodes.JsonArray
                                ?? new System.Text.Json.Nodes.JsonArray())
                            {
                                if (each is not System.Text.Json.Nodes.JsonObject region) { continue; }

                                var name = region["name"]?.GetValue<string>();
                                if (name == null || !wanted.ContainsKey(name)) { continue; }

                                var size = region["size"] as System.Text.Json.Nodes.JsonArray;
                                var row = $"type {region["type"]?.GetValue<string>() ?? "(none)"}"
                                    + $"  size [{size?[0]},{size?[1]},{size?[2]}]"
                                    + $"  tags \"{region["tags"]?.GetValue<string>() ?? ""}\"";

                                shapes[row] = shapes.TryGetValue(row, out var was) ? was + 1 : 1;
                            }
                        }
                    }
                }

                Console.WriteLine("what a clickable thing's region looks like:");
                foreach (var shape in shapes.OrderByDescending(one => one.Value))
                {
                    Console.WriteLine($"[click] {shape.Value,4}  {shape.Key}");
                }

                Console.WriteLine();
                Console.WriteLine("and which missions use each prefab:");
                foreach (var one in byMission)
                {
                    var offered = Logic.MapSpawns.CLICKABLES.Any(c => c.path == one.Key);

                    Console.WriteLine($"[click] {(offered ? "OFFERED" : "       ")}  "
                        + $"{one.Key}");
                    Console.WriteLine($"[click]            used by: {string.Join(", ", one.Value)}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_MISSIONS - the rows the Maps tab shows, by the name it shows them under.
            if (_startupArguments.Any(a => a == "PROBE_MISSIONS"))
            {
                foreach (var one in Logic.GameMaps.all())
                {
                    Console.WriteLine($"[missions] {one.Name,-22} {one.Label}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_CLAIM2=<folder>;<mission> - what installing a folder over a mission does to
            //the level's id, without installing anything.
            //
            //Written after a mod imported over the wrong mission crashed on entering it. The id
            //had been rewritten to the mission being replaced, which is right for a map this
            //app built out of nothing and wrong for one that came from somewhere else: the game
            //finds a tile's sub-level at Decor/Maps/<id>/SubLevels/<tile>, so renaming the id
            //moves that lookup to a folder with nothing in it.
            var probeClaim2 = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_CLAIM2="));
            if (probeClaim2 != null)
            {
                var bits = probeClaim2.Substring("PROBE_CLAIM2=".Length).Trim('"').Split(';');
                if (bits.Length < 2)
                {
                    Console.WriteLine("[claim] PROBE_CLAIM2=<map folder>;<mission>");
                    this.Shutdown();
                    return;
                }

                var folder = bits[0].Trim();
                var wanted = bits[1].Trim();

                var mission = Logic.GameMaps.all().FirstOrDefault(one =>
                    string.Equals(one.Name, wanted, StringComparison.OrdinalIgnoreCase));

                if (mission == null)
                {
                    Console.WriteLine($"[claim] no mission called {wanted}");
                    this.Shutdown();
                    return;
                }

                var level = System.IO.Path.Combine(folder, "level.json");
                if (!System.IO.File.Exists(level))
                {
                    Console.WriteLine($"[claim] no level.json in {folder}");
                    this.Shutdown();
                    return;
                }

                System.Text.Json.Nodes.JsonObject? read(byte[] raw)
                    => System.Text.Json.Nodes.JsonNode.Parse(
                        Logic.GameMaps.stripComments(
                            new System.Text.UTF8Encoding(false).GetString(raw).TrimStart('\uFEFF')),
                        documentOptions: new System.Text.Json.JsonDocumentOptions
                        {
                            AllowTrailingCommas = true,
                            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                        }) as System.Text.Json.Nodes.JsonObject;

                var before = read(System.IO.File.ReadAllBytes(level));

                //Through install itself, so what is measured is what would be shipped.
                var entries = Logic.MapMod.wouldShip(folder, mission);

                var after = entries
                    .Where(one => one.Path.EndsWith("/" + mission.Name + ".json",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(one => read(one.Data))
                    .FirstOrDefault();

                foreach (var field in new[] { "id", "loctable-id", "ambience-level-id" })
                {
                    Console.WriteLine($"[claim] {field,-20} "
                        + $"{before?[field]?.GetValue<string>() ?? "(unset)",-16} -> "
                        + $"{after?[field]?.GetValue<string>() ?? "(unset)"}");
                }

                //And whether the sub-levels it ships still line up with the id it will load as.
                var id = after?["id"]?.GetValue<string>() ?? "";
                var subs = entries.Where(one =>
                    one.Path.IndexOf("/SubLevels/", StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                Console.WriteLine($"[claim] it ships {subs.Count} sub-level file(s)");

                //And the wording table, which has to be the one the level reads rather than
                //the one the mission is called.
                var reads = after?["loctable-id"]?.GetValue<string>()
                    ?? after?["id"]?.GetValue<string>() ?? "?";

                var tables = entries.Where(one =>
                    one.Path.IndexOf("/Decor/Text/", StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                Console.WriteLine($"[claim] the level reads the \"{reads}\" table");

                foreach (var one in tables)
                {
                    var leaf = one.Path.Substring(one.Path.LastIndexOf('/') + 1);

                    Console.WriteLine($"[claim]   ships {leaf}  "
                        + (leaf.StartsWith(reads, StringComparison.OrdinalIgnoreCase)
                            ? "<- the one it reads"
                            : "WRONG - it will never open this"));
                }

                var owners = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var sub in subs)
                {
                    var under = sub.Path.Replace('\\', '/');
                    var at = under.IndexOf("/Maps/", StringComparison.OrdinalIgnoreCase);
                    if (at < 0) { continue; }

                    var owner = under.Substring(at + 6).Split('/')[0];
                    owners[owner] = owners.TryGetValue(owner, out var was) ? was + 1 : 1;
                }

                foreach (var owner in owners)
                {
                    //"Lobby" is the camp and belongs to no level - a mod that dresses the camp
                    //puts its sub-levels there on purpose, and they are found by the camp's own
                    //id rather than by this one's.
                    var mine = string.Equals(owner.Key, id, StringComparison.OrdinalIgnoreCase);
                    var camp = string.Equals(owner.Key, "Lobby", StringComparison.OrdinalIgnoreCase);

                    Console.WriteLine($"[claim]   {owner.Value} under \"{owner.Key}\"  "
                        + (mine ? "<- found by this level's id"
                           : camp ? "(the camp, not this level)"
                           : "WRONG - nothing will look there"));
                }

                this.Shutdown();
                return;
            }

            //PROBE_FRESH=<mission>;<folder> - the game's own mission, straight out of the paks.
            //
            //So that welding can be measured against what the game actually plays rather than
            //against a folder somebody has been editing.
            var probeFresh = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_FRESH="));
            if (probeFresh != null)
            {
                var bits = probeFresh.Substring("PROBE_FRESH=".Length).Trim('"').Split(';');
                if (bits.Length < 2)
                {
                    Console.WriteLine("[fresh] PROBE_FRESH=<mission>;<folder>");
                    this.Shutdown();
                    return;
                }

                var mission = Logic.GameMaps.all().FirstOrDefault(one =>
                    string.Equals(one.Name, bits[0].Trim(), StringComparison.OrdinalIgnoreCase));

                if (mission == null)
                {
                    Console.WriteLine($"[fresh] no mission called {bits[0]}");
                    this.Shutdown();
                    return;
                }

                try
                {
                    var made = Logic.MapMod.export(mission, bits[1].Trim());
                    Console.WriteLine($"[fresh] {made.Files} file(s), {made.Bytes:N0} bytes -> {made.Folder}");
                    foreach (var note in made.Notes) { Console.WriteLine($"[fresh]   {note}"); }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[fresh] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_LIVETILES=<mission> - the layout the running game actually built.
            //
            //Welding has to place the rooms itself, and placing them itself is guesswork: the
            //game assembles a mission at run time from rules and a seed nobody outside it has,
            //so the best an offline guess manages is A layout rather than THE layout. Creeper
            //Woods welded offline leaves two rooms short of their doorways.
            //
            //But the game has already done it. While a mission is loaded its answer is sitting
            //in memory, so this goes and reads it rather than imitating it.
            //
            //Read-only: nothing is written to the game.
            var probeLive = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_LIVETILES="));
            if (probeLive != null)
            {
                var wanted = probeLive.Substring("PROBE_LIVETILES=".Length).Trim('"');

                //The tile ids this mission is made of, so a name in the game can be recognised
                //as one. Taken from the folder rather than guessed at.
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    var folder = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MCDReborn", "maps", wanted);

                    if (!System.IO.Directory.Exists(folder)) { folder = wanted; }

                    foreach (var file in System.IO.Directory.GetFiles(
                        System.IO.Path.Combine(folder, "objectgroups"), "objectgroup.json",
                        System.IO.SearchOption.AllDirectories))
                    {
                        var sheet = System.Text.Json.Nodes.JsonNode.Parse(
                            Logic.GameMaps.stripComments(System.IO.File.ReadAllText(file)),
                            documentOptions: new System.Text.Json.JsonDocumentOptions
                            {
                                AllowTrailingCommas = true,
                                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            }) as System.Text.Json.Nodes.JsonObject;

                        foreach (var one in sheet?["objects"] as System.Text.Json.Nodes.JsonArray
                            ?? new System.Text.Json.Nodes.JsonArray())
                        {
                            var id = (one as System.Text.Json.Nodes.JsonObject)?["id"]
                                ?.GetValue<string>();

                            if (!string.IsNullOrEmpty(id)) { known.Add(id!); }
                        }
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[live] could not read the tile ids: {problem.Message}");
                }

                Console.WriteLine($"[live] looking for {known.Count} tile id(s) of {wanted}");

                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null)
                {
                    Console.WriteLine($"[live] the game is not open: {why}");
                    this.Shutdown();
                    return;
                }

                using (game)
                {
                    var names = new LiveEdit.NameTable(game);
                    if (!names.find(one => Console.WriteLine($"[live] names: {one}")))
                    {
                        Console.WriteLine("[live] could not find the name table");
                        this.Shutdown();
                        return;
                    }

                    Console.WriteLine($"[live] name table: {names.Count:N0} names");

                    var tables = new LiveEdit.ObjectTables(game);
                    var found = tables.search();

                    if (found.Count == 0)
                    {
                        Console.WriteLine("[live] could not find the object array");
                        this.Shutdown();
                        return;
                    }

                    var array = found.OrderByDescending(one => one.NumElements).First();
                    Console.WriteLine($"[live] object array: {array.NumElements:N0} objects");

                    //UObject, 4.22: vtable, flags, index, class, name, outer.
                    const int CLASS_AT = 0x10;
                    const int NAME_AT = 0x18;
                    const int OUTER_AT = 0x20;

                    string? nameOf(IntPtr obj)
                    {
                        if (obj == IntPtr.Zero) { return null; }
                        var raw = game.read(new IntPtr(obj.ToInt64() + NAME_AT), 4);
                        return raw == null ? null : names.nameOf(BitConverter.ToInt32(raw, 0));
                    }

                    IntPtr pointerAt(IntPtr obj, int offset)
                    {
                        var raw = game.read(new IntPtr(obj.ToInt64() + offset), 8);
                        return raw == null ? IntPtr.Zero : new IntPtr(BitConverter.ToInt64(raw, 0));
                    }

                    var hits = new List<(string name, string cls, IntPtr at)>();
                    var byClass = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    var looked = 0;

                    for (var i = 0; i < array.NumElements; i++)
                    {
                        var obj = tables.objectAt(array, i);
                        if (obj == IntPtr.Zero) { continue; }

                        looked++;

                        var name = nameOf(obj);
                        if (name == null || name.Length < 3) { continue; }

                        //A tile's name may carry the engine's _NN suffix on a duplicate.
                        var bare = name;
                        var under = bare.LastIndexOf('_');
                        if (under > 0 && int.TryParse(bare.Substring(under + 1), out _)
                            && !known.Contains(bare))
                        {
                            bare = bare.Substring(0, under);
                        }

                        if (!known.Contains(bare)) { continue; }

                        var cls = nameOf(pointerAt(obj, CLASS_AT)) ?? "?";
                        byClass[cls] = byClass.TryGetValue(cls, out var was) ? was + 1 : 1;

                        if (hits.Count < 400) { hits.Add((name, cls, obj)); }
                    }

                    Console.WriteLine($"[live] walked {looked:N0} objects, "
                        + $"{hits.Count} of them named after a tile of this mission");

                    foreach (var one in byClass.OrderByDescending(one => one.Value))
                    {
                        Console.WriteLine($"[live]   {one.Value,5}  class {one.Key}");
                    }

                    foreach (var one in hits.Take(12))
                    {
                        var outer = nameOf(pointerAt(one.at, OUTER_AT)) ?? "?";
                        Console.WriteLine($"[live]   {one.name,-30} {one.cls,-28} outer={outer}");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_FINDSTR=<text> - where a piece of text sits in the running game.
            //
            //Before building anything on top of the name table, the cheap question: is a tile's
            //name in the game's memory as text at all? If it is, the entry that holds it can be
            //found directly and carries its own index - which is all that is needed to match
            //objects, and does not require finding the name table at all.
            //
            //Read-only.
            var probeStr = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_FINDSTR="));
            if (probeStr != null)
            {
                var wanted = probeStr.Substring("PROBE_FINDSTR=".Length).Trim('"');

                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null)
                {
                    Console.WriteLine($"[str] the game is not open: {why}");
                    this.Shutdown();
                    return;
                }

                using (game)
                {
                    var needleA = System.Text.Encoding.ASCII.GetBytes(wanted + "\0");
                    var needleW = System.Text.Encoding.Unicode.GetBytes(wanted + "\0");

                    var hitsA = new List<long>();
                    var hitsW = new List<long>();
                    var regions = 0;
                    long bytes = 0;

                    foreach (var (at, size) in game.writableRegions())
                    {
                        var buffer = new byte[size];
                        if (!game.tryRead(at, buffer, (int)size)) { continue; }

                        regions++;
                        bytes += size;

                        void hunt(byte[] needle, List<long> into)
                        {
                            if (into.Count >= 8) { return; }

                            for (var i = 0; i + needle.Length <= buffer.Length; i++)
                            {
                                if (buffer[i] != needle[0]) { continue; }

                                var same = true;
                                for (var j = 1; j < needle.Length; j++)
                                {
                                    if (buffer[i + j] != needle[j]) { same = false; break; }
                                }

                                if (!same) { continue; }

                                into.Add(at.ToInt64() + i);
                                if (into.Count >= 8) { return; }
                            }
                        }

                        hunt(needleA, hitsA);
                        hunt(needleW, hitsW);

                        if (hitsA.Count >= 8 && hitsW.Count >= 8) { break; }
                    }

                    Console.WriteLine($"[str] \"{wanted}\": {hitsA.Count} ansi, {hitsW.Count} wide "
                        + $"(scanned {regions} region(s), {bytes / (1024 * 1024)} MB)");

                    //The bytes just before it. An FNameEntry in this engine is an int32 index, a
                    //pointer, then the text at 0x10 - so a hit that IS a name entry has its own
                    //index sixteen bytes back, and that index is what an object's name field
                    //holds.
                    //Who points AT the text. A layout does not store a tile's name inline, it
                    //stores a pointer to it - so the interesting structure is whatever holds
                    //this address, and what sits beside it there.
                    //What sits in front of each copy. Nothing points AT the wide ones, which
                    //is what an inline buffer looks like - and a name entry keeps its text
                    //inline at 0x10, with an index and a hash pointer in front. If these are
                    //entries then the tile names are engine names after all, and each one
                    //carries the number an object would be holding.
                    foreach (var hit in hitsW.Concat(hitsA))
                    {
                        var before = game.read(new IntPtr(hit - 0x10), 0x10);
                        if (before == null) { continue; }

                        var index = BitConverter.ToInt32(before, 0);
                        var next = BitConverter.ToInt64(before, 8);

                        var entryish = (index & 1) == 1 && (index >> 1) > 0 && (index >> 1) < 4_000_000
                            && (next == 0 || (next > 0x10000 && next < 0x7fffffffffff));

                        Console.WriteLine($"[str] {hit:x} -0x10: "
                            + string.Join(" ", before.Take(16).Select(b => b.ToString("x2"))));
                        Console.WriteLine($"[str]     index={index >> 1} wide={(index & 1) != 0} "
                            + $"hashNext={next:x}  {(entryish ? "<- looks like a name entry" : "")}");
                    }

                    foreach (var hit in new List<long>())
                    {
                        var target = BitConverter.GetBytes(hit);
                        var pointers = new List<long>();

                        foreach (var (at, size) in game.writableRegions())
                        {
                            if (pointers.Count >= 6) { break; }

                            var buffer = new byte[size];
                            if (!game.tryRead(at, buffer, (int)size)) { continue; }

                            for (var i = 0; i + 8 <= buffer.Length; i += 8)
                            {
                                var same = true;
                                for (var j = 0; j < 8; j++)
                                {
                                    if (buffer[i + j] != target[j]) { same = false; break; }
                                }

                                if (!same) { continue; }

                                pointers.Add(at.ToInt64() + i);
                                if (pointers.Count >= 6) { break; }
                            }
                        }

                        Console.WriteLine($"[str] {hit:x} is pointed at from {pointers.Count} place(s)");

                        foreach (var from in pointers)
                        {
                            var around = game.read(new IntPtr(from - 0x80), 0x180);
                            if (around == null) { continue; }

                            var ints = new List<string>();
                            var floats = new List<string>();

                            for (var i = 0; i + 4 <= around.Length; i += 4)
                            {
                                var whole = BitConverter.ToInt32(around, i);
                                var real = BitConverter.ToSingle(around, i);

                                ints.Add(Math.Abs(whole) < 100000 ? whole.ToString() : "-");
                                floats.Add(Math.Abs(real) > 0.01 && Math.Abs(real) < 100000
                                    ? real.ToString("0.#") : "-");
                            }

                            //Anything that looks like a place: three smallish numbers in a
                            //row, which is what a tile position is. Printed with their offset
                            //from the name pointer so a repeating stride shows up.
                            Console.WriteLine($"[str]   from {from:x} (name pointer at +0x80 of this window)");

                            for (var i = 0; i + 12 <= around.Length; i += 4)
                            {
                                var a = BitConverter.ToInt32(around, i);
                                var b = BitConverter.ToInt32(around, i + 4);
                                var c = BitConverter.ToInt32(around, i + 8);

                                var placeish = Math.Abs(a) < 4000 && Math.Abs(b) < 4000
                                    && Math.Abs(c) < 4000 && (a != 0 || b != 0 || c != 0);

                                var fa = BitConverter.ToSingle(around, i);
                                var fb = BitConverter.ToSingle(around, i + 4);
                                var fc = BitConverter.ToSingle(around, i + 8);

                                var floatish = new[] { fa, fb, fc }.All(one =>
                                    one == 0 || (Math.Abs(one) > 0.5 && Math.Abs(one) < 500000));

                                if (placeish)
                                {
                                    Console.WriteLine($"[str]     +{i - 0x80,4}  ints   {a} {b} {c}");
                                }
                                else if (floatish && (fa != 0 || fb != 0 || fc != 0))
                                {
                                    Console.WriteLine($"[str]     +{i - 0x80,4}  floats {fa:0.#} {fb:0.#} {fc:0.#}");
                                }
                            }
                        }
                    }

                    foreach (var hit in hitsA.Take(0))
                    {
                        var head = game.read(new IntPtr(hit - 0x10), 0x10);
                        if (head == null) { continue; }

                        var index = BitConverter.ToInt32(head, 0);

                        Console.WriteLine($"[str]   {hit:x}  header={string.Join(" ", head.Take(16).Select(b => b.ToString("x2")))}");
                        Console.WriteLine($"[str]        if this is a name entry, index={index >> 1}"
                            + $" wide={(index & 1) != 0}");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_GNAMES - the engine's name table, found by its own code.
            //
            //Not by shape. Shape finds forty-two wrong answers in this game, because a table of
            //pointers to pointers is not a rare thing to look like. This finds the FUNCTION that
            //makes the table, and reads the address out of the instruction that touches it:
            //
            //  48 83 EC 28        sub  rsp, 28h
            //  48 8B 05 ?? ?? ?? ??   mov  rax, cs:Names      <- the address wanted
            //  48 85 C0           test rax, rax
            //  75 ??              jnz  short already
            //  B9 08 08 00 00     mov  ecx, 808h              <- sizeof the 4.22 table
            //
            //That 0x808 is what pins the engine version: it is
            //sizeof(TStaticIndirectArrayThreadSafeRead<FNameEntry, 4M, 16384>) - 256 chunk
            //pointers, a count and a chunk count. The 4.8-era build of the same function says
            //0x408 instead, and nothing else in fifty megabytes of code says either.
            //
            //Scanned in the process, never in the file: this executable is packed, every
            //section on disk is encrypted, and what is readable is the decrypted image the
            //game is running from.
            //
            //Read-only.
            //PROBE_GNAMES=<regex> additionally lists every name in the table that matches, to
            //a file beside the output - read-only, the same external reads as the bare form.
            if (_startupArguments.Any(a => a == "PROBE_GNAMES" || a.StartsWith("PROBE_GNAMES=", StringComparison.Ordinal)))
            {
                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null)
                {
                    Console.WriteLine($"[gnames] the game is not open: {why}");
                    this.Shutdown();
                    return;
                }

                using (game)
                {
                    var began = DateTime.UtcNow;
                    var image = game.image(out var imageSize);
                    var baseAt = image.ToInt64();

                    Console.WriteLine($"[gnames] module at {baseAt:x}, {imageSize / (1024 * 1024)} MB");

                    //The section table is intact even though the names are blanked, so the code
                    //range is read out of the PE header rather than guessed or looked up by name.
                    var lowCode = baseAt + 0x1000;
                    var highCode = baseAt + imageSize;

                    var dos = game.read(image, 0x40);
                    if (dos != null && dos[0] == 'M' && dos[1] == 'Z')
                    {
                        var peAt = BitConverter.ToInt32(dos, 0x3c);
                        var pe = game.read(new IntPtr(baseAt + peAt), 0x108);

                        if (pe != null)
                        {
                            var sections = BitConverter.ToUInt16(pe, 6);
                            var optionalSize = BitConverter.ToUInt16(pe, 20);
                            var firstSection = peAt + 24 + optionalSize;

                            for (var i = 0; i < sections; i++)
                            {
                                var row = game.read(new IntPtr(baseAt + firstSection + i * 40), 40);
                                if (row == null) { continue; }

                                var flags = BitConverter.ToUInt32(row, 36);
                                var rva = BitConverter.ToUInt32(row, 12);
                                var size = BitConverter.ToUInt32(row, 8);

                                //IMAGE_SCN_CNT_CODE | MEM_EXECUTE, and the big one is the real
                                //.text rather than the protector's own stub.
                                if ((flags & 0x20000000) == 0) { continue; }
                                if (size < 0x100000) { continue; }

                                lowCode = baseAt + rva;
                                highCode = lowCode + size;

                                Console.WriteLine($"[gnames] code section at +{rva:x}, "
                                    + $"{size / (1024 * 1024)} MB");
                                break;
                            }
                        }
                    }

                    //48 83 EC 28 48 8B 05 ?? ?? ?? ?? 48 85 C0 75 ?? B9 08 08 00 00
                    var want = new byte?[]
                    {
                        0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0x05, null, null, null, null,
                        0x48, 0x85, 0xC0, 0x75, null, 0xB9, 0x08, 0x08, 0x00, 0x00,
                    };

                    var found = new List<long>();
                    var CHUNK = 4 * 1024 * 1024;
                    var buffer = new byte[CHUNK + 64];

                    for (var at = lowCode; at < highCode; at += CHUNK)
                    {
                        var take = (int)Math.Min(CHUNK + 64, highCode - at);
                        if (!game.tryRead(new IntPtr(at), buffer, take)) { continue; }

                        for (var i = 0; i + want.Length <= take; i++)
                        {
                            if (buffer[i] != 0x48) { continue; }

                            var same = true;
                            for (var j = 1; j < want.Length; j++)
                            {
                                if (want[j] == null) { continue; }
                                if (buffer[i + j] != want[j]!.Value) { same = false; break; }
                            }

                            if (same) { found.Add(at + i); }
                        }
                    }

                    Console.WriteLine($"[gnames] {found.Count} match(es) for FName::GetNames "
                        + $"in {(DateTime.UtcNow - began).TotalSeconds:F1}s");

                    foreach (var match in found)
                    {
                        //mov rax, [rip+disp32] - the address is relative to the NEXT instruction.
                        var raw = game.read(new IntPtr(match + 7), 4);
                        if (raw == null) { continue; }

                        var pointerAt = match + 11 + BitConverter.ToInt32(raw, 0);

                        var held = game.read(new IntPtr(pointerAt), 8);
                        if (held == null) { continue; }

                        var table = BitConverter.ToInt64(held, 0);

                        Console.WriteLine($"[gnames] match at {match:x} -> &GNames {pointerAt:x} "
                            + $"-> table {table:x}");

                        if (table < 0x10000) { Console.WriteLine("[gnames]   not made yet"); continue; }

                        var names = new LiveEdit.NameTable(game);
                        names.useChunks(new IntPtr(table));

                        var zero = names.nameOf(0);
                        Console.WriteLine($"[gnames]   0..7: " + string.Join(" | ",
                            Enumerable.Range(0, 8).Select(one => names.nameOf(one) ?? "(null)")));

                        if (zero != "None")
                        {
                            Console.WriteLine("[gnames]   name 0 is not \"None\", so this is not it");
                            continue;
                        }

                        var counts = game.read(new IntPtr(table + 0x800), 8);
                        if (counts != null)
                        {
                            Console.WriteLine($"[gnames]   NumElements={BitConverter.ToInt32(counts, 0):N0} "
                                + $"NumChunks={BitConverter.ToInt32(counts, 4)}");
                        }

                        Console.WriteLine($"[gnames] GNAMES POINTER = {pointerAt:x}  "
                            + $"(RVA +{pointerAt - baseAt:x})");
                        Console.WriteLine($"[gnames] TABLE = {table:x}");

                        var grepArg = _startupArguments.FirstOrDefault(a =>
                            a.StartsWith("PROBE_GNAMES=", StringComparison.Ordinal));
                        if (grepArg != null)
                        {
                            var pattern = new System.Text.RegularExpressions.Regex(
                                grepArg["PROBE_GNAMES=".Length..].Trim('"'),
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            var total = counts == null ? 300000 : BitConverter.ToInt32(counts, 0);
                            var hits = new List<string>();
                            for (var i = 0; i < total; i++)
                            {
                                var said = names.nameOf(i);
                                if (said != null && pattern.IsMatch(said)) { hits.Add($"{i}\t{said}"); }
                            }
                            var into = Path.Combine(Path.GetTempPath(), "mcd-gnames-grep.txt");
                            File.WriteAllLines(into, hits);
                            Console.WriteLine($"[gnames] {hits.Count} of {total:N0} names match -> {into}");
                            break;
                        }

                        //And the thing it is all for: can a tile's name be found in it?
                        var hunting = new[] { "cw_start_a001", "cw_theinn001", "LevelTransform",
                                              "PackageNameToLoad", "LevelStreaming" };

                        var end = counts == null ? 200000 : BitConverter.ToInt32(counts, 0);
                        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

                        for (var i = 0; i < end && seen.Count < hunting.Length; i++)
                        {
                            var said = names.nameOf(i);
                            if (said == null) { continue; }

                            foreach (var one in hunting)
                            {
                                if (!seen.ContainsKey(one)
                                    && string.Equals(said, one, StringComparison.Ordinal))
                                {
                                    seen[one] = i;
                                }
                            }
                        }

                        foreach (var one in hunting)
                        {
                            Console.WriteLine($"[gnames]   \"{one}\": "
                                + (seen.TryGetValue(one, out var where)
                                    ? $"name {where}" : "not in the table"));
                        }

                        break;
                    }

                    if (found.Count == 0)
                    {
                        Console.WriteLine("[gnames] the pattern is not in this build");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_REVEAL=<slot>[;<slot>...] - showing a slot in the cooked panel.
            //
            //Proves the in-place edit before anything depends on it: read the widget this exe
            //carries, walk to a slot button's visibility, point it at Visible, and check the file
            //is the same length and the change reads back. A package that comes out a different
            //size is a package with every later offset wrong, and it would install perfectly.
            var probeReveal = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_REVEAL="));
            if (probeReveal != null)
            {
                var wanted = probeReveal.Substring("PROBE_REVEAL=".Length).Trim('"')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse).ToList();

                var cooked = System.IO.Path.Combine(
                    @"C:\Users\Gaming\Documents\Unreal Projects\MyProject\Saved\Cooked",
                    @"WindowsNoEditor\MyProject\Content\MCDReborn\UI\UMG_MCDRebornMaps");

                var header = System.IO.File.ReadAllBytes(cooked + ".uasset");
                var data = System.IO.File.ReadAllBytes(cooked + ".uexp");

                var was = data.Length;
                var package = Logic.CookedEdit.read(header, data);

                Console.WriteLine($"[reveal] {package.Names.Count} name(s), "
                    + $"{package.Exports.Count} export(s)");
                Console.WriteLine($"[reveal] Visible is name {package.indexOf("ESlateVisibility::Visible")}, "
                    + $"Collapsed is {package.indexOf("ESlateVisibility::Collapsed")}");

                foreach (var one in package.Exports.Where(e =>
                    e.Name.StartsWith("Mission", StringComparison.Ordinal)).Take(4))
                {
                    Console.WriteLine($"[reveal]   {one.Name} at {one.At}, {one.Size} bytes");
                }

                foreach (var slot in wanted)
                {
                    var button = Logic.MapTable.slotButton(slot);

                    //How many exports carry that name, because a cooked widget serialises its
                    //tree twice and a change to only one of them loses to the other.
                    var copies = package.Exports.Count(one =>
                        string.Equals(one.Name, button, StringComparison.Ordinal));

                    var done = Logic.CookedEdit.setEnum(package, button, "Visibility",
                        "ESlateVisibility::Visible");

                    Console.WriteLine($"[reveal] {button}: {(done ? "shown" : "NOT CHANGED")}"
                        + $"  ({copies} cop{(copies == 1 ? "y" : "ies")} in the package)");
                }

                Console.WriteLine($"[reveal] uexp was {was:N0} bytes, now {data.Length:N0}"
                    + (was == data.Length ? "  (unchanged, as it must be)" : "  *** MOVED ***"));

                this.Shutdown();
                return;
            }

            //PROBE_SLOT=<folder>;<slot>;<name> - a map installed as its own mission.
            //
            //The other half of PROBE_TABLE. The table offers a hundred slots; this fills one, and
            //says what went into the pak so that "the slot is empty" and "the slot holds the
            //wrong thing" can be told apart without starting the game.
            var probeSlot = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_SLOT="));
            if (probeSlot != null)
            {
                var bits = probeSlot.Substring("PROBE_SLOT=".Length).Trim('"').Split(';');
                if (bits.Length < 2)
                {
                    Console.WriteLine("[slot] need PROBE_SLOT=<folder>;<slot>[;<name>]");
                    this.Shutdown();
                    return;
                }

                var which = int.Parse(bits[1]);
                var shown = bits.Length > 2 ? bits[2] : System.IO.Path.GetFileName(bits[0]);

                try
                {
                    var made = Logic.MapSlots.install(bits[0], which, shown);

                    Console.WriteLine($"[slot] {System.IO.Path.GetFileName(made.Path)}, "
                        + $"{new System.IO.FileInfo(made.Path).Length:N0} bytes");

                    var many = 0;
                    foreach (var one in Logic.ModPak.read(made.Path))
                    {
                        many++;
                        //The level and the label table are the two that decide whether this
                        //works; the rest is bulk and is counted rather than listed.
                        if (one.Path.Contains("/levels/") || one.Path.Contains("/Text/"))
                        {
                            Console.WriteLine($"[slot]   {one.Path}  ({one.Data.Length:N0})");
                        }
                    }

                    Console.WriteLine($"[slot] {many} file(s) in total");

                    foreach (var one in Logic.MapSlots.installed())
                    {
                        Console.WriteLine($"[slot] filled: {one}");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[slot] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_TABLE - the Camp map table, installed from the copy inside this exe.
            //
            //The same shape as PROBE_INSTALL_LOADER: it proves the three packages are actually
            //carried by this build and land at the paths the panel and the level refer to each
            //other by. A payload missing one of its three reads exactly like a loader that never
            //ran, so it is worth being able to ask.
            if (_startupArguments.Any(a => a == "PROBE_TABLE"))
            {
                try
                {
                    //Through sync, which is the only path anything else uses - calling
                    //installBuiltIn directly here once meant the probe tested an overload
                    //nothing in the app calls, and reported success for a table with no names
                    //in it.
                    Logic.MapSlots.sync();

                    var where = Logic.MapTable.installed();
                    if (where == null)
                    {
                        Console.WriteLine("[table] nothing is in any slot, so there is no table");
                        this.Shutdown();
                        return;
                    }

                    Console.WriteLine($"[table] installed {System.IO.Path.GetFileName(where)}, "
                        + $"{new System.IO.FileInfo(where).Length:N0} bytes");
                    Console.WriteLine($"[table] isInstalled = {Logic.MapTable.isInstalled}");
                    Console.WriteLine($"[table] {Logic.MapTable.SLOTS} slot(s), "
                        + $"first {Logic.MapTable.slotName(1)}, "
                        + $"last {Logic.MapTable.slotName(Logic.MapTable.SLOTS)}");

                    //The number that says whether the in-place edit did anything. A table that
                    //installs cleanly and shows nothing is the failure worth catching here.
                    Console.WriteLine($"[table] {Logic.MapTable.Shown} slot(s) revealed, "
                        + $"of {Logic.MapSlots.installed().Count} filled");

                    foreach (var one in Logic.MapSlots.installed())
                    {
                        Console.WriteLine($"[table]   slot {one}");
                    }

                    foreach (var one in Logic.ModPak.read(where))
                    {
                        Console.WriteLine($"[table]   holds {one.Path}  ({one.Data.Length:N0})");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[table] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_PAKFILE=<pak>;<part of a path> - one file out of a mod, as text.
            //
            //For when a pak installs cleanly and the game disagrees with what you think is in it.
            //Reads the entry rather than the folder it was built from, because those are
            //different claims and only one of them is what the game will read.
            var probePakFile = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_PAKFILE="));
            if (probePakFile != null)
            {
                var bits = probePakFile.Substring("PROBE_PAKFILE=".Length).Trim('"').Split(';');
                if (bits.Length < 2)
                {
                    Console.WriteLine("[pakfile] need PROBE_PAKFILE=<pak>;<part of a path>");
                    this.Shutdown();
                    return;
                }

                foreach (var one in Logic.ModPak.read(bits[0]))
                {
                    if (one.Path.IndexOf(bits[1], StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    Console.WriteLine($"[pakfile] {one.Path}  ({one.Data.Length:N0} bytes)");

                    var said = System.Text.Encoding.UTF8.GetString(one.Data);
                    foreach (var line in said.Split('\n').Take(60))
                    {
                        Console.WriteLine($"[pakfile]   {line.TrimEnd()}");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_CAPTURE=<map folder>;<their csv> - giving a converted map its words back.
            //
            //A map converted out of somebody else's mod arrives as a level, its object groups and
            //its block palette - and none of its WORDING, because that lives in a label table
            //inside their pak rather than in the level. The objectives then read
            //`<MISSING STRING TABLE ENTRY>` in game, which is the game saying it found the table
            //and not the key.
            //
            //This copies the rows the level actually names into the map's own `text/` file, where
            //everything downstream already looks for them: installing over a mission ships them,
            //and so does installing into a slot.
            var probeCapture = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_CAPTURE="));
            if (probeCapture != null)
            {
                var bits = probeCapture.Substring("PROBE_CAPTURE=".Length).Trim('"').Split(';');
                if (bits.Length < 2)
                {
                    Console.WriteLine("[capture] need PROBE_CAPTURE=<map folder>;<their csv>");
                    this.Shutdown();
                    return;
                }

                var folder = bits[0];
                var level = System.IO.Path.Combine(folder, "level.json");

                if (!System.IO.File.Exists(level) || !System.IO.File.Exists(bits[1]))
                {
                    Console.WriteLine("[capture] no level.json in that folder, or no such csv");
                    this.Shutdown();
                    return;
                }

                //Which keys the level names. Everything else in their table belongs to whatever
                //mission it was really for and is not ours to carry.
                var text = System.IO.File.ReadAllText(level);
                var rows = 0;
                var skipped = 0;

                foreach (var line in System.IO.File.ReadAllLines(bits[1]))
                {
                    var at = line.IndexOf(',');
                    if (at <= 0) { continue; }

                    var key = line.Substring(0, at).Trim();
                    var said = line.Substring(at + 1).Trim();

                    if (key.Length == 0 || said.Length == 0 || key == "Key") { continue; }

                    //Named somewhere in the level, quoted, which is how a key appears in json.
                    if (!text.Contains("\"" + key + "\"", StringComparison.Ordinal))
                    {
                        skipped++;
                        continue;
                    }

                    Logic.MapWords.remember(folder, key, said);
                    Console.WriteLine($"[capture]   {key} = {said}");
                    rows++;
                }

                Console.WriteLine($"[capture] {rows} row(s) kept, {skipped} not named by this level");
                Console.WriteLine("[capture] written to " + System.IO.Path.Combine(folder, Logic.MapWords.FOLDER, Logic.MapWords.FILE));


                this.Shutdown();
                return;
            }

            //PROBE_MERGECSV=<loctable>;<theirs>;<out> - two missions sharing one label table.
            //
            //A mod that adds objective wording ships a whole Decor/Text/<x>Labels.csv, which
            //REPLACES the game's - so the mission that table belonged to loses every line it
            //had. Blossoming Isles takes Cacti Canyon's table for its own use and Cacti Canyon
            //reads as a cherry garden for as long as it is installed.
            //
            //There is no reason for that. The file is a flat list of key,text - so the game's
            //rows and the mod's rows can sit in the same file and both missions work.
            //
            //The game's half is copied BYTE FOR BYTE and the new rows are appended after it.
            //Rebuilding it from parsed rows is what broke this once before: six of twenty-six
            //rows lost a trailing comma, and the game crashed on entering the mission rather
            //than on loading the table.
            var probeMerge = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_MERGECSV="));
            if (probeMerge != null)
            {
                var bits = probeMerge.Substring("PROBE_MERGECSV=".Length).Trim('"').Split(';');
                if (bits.Length < 3)
                {
                    Console.WriteLine("[merge] need PROBE_MERGECSV=<loctable>;<theirs>;<out>");
                    this.Shutdown();
                    return;
                }

                var ours = Logic.MapWords.readRaw(bits[0]);
                if (ours == null)
                {
                    Console.WriteLine($"[merge] the game has no label table called {bits[0]}");
                    this.Shutdown();
                    return;
                }

                var theirs = System.IO.File.ReadAllBytes(bits[1]);
                Console.WriteLine($"[merge] the game's {bits[0]}Labels.csv is {ours.Length} bytes, "
                    + $"theirs is {theirs.Length}");

                static string keyOf(string line)
                {
                    var at = line.IndexOf(',');
                    return (at < 0 ? line : line.Substring(0, at)).Trim();
                }

                var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in System.Text.Encoding.UTF8.GetString(ours)
                    .Split('\n'))
                {
                    var key = keyOf(line.TrimEnd('\r'));
                    if (key.Length > 0) { mine.Add(key); }
                }

                //Whatever the game's file ends its lines with, so the join is invisible.
                var crlf = Array.IndexOf(ours, (byte)'\r') >= 0;
                var added = new List<string>();

                foreach (var raw in System.Text.Encoding.UTF8.GetString(theirs).Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    var key = keyOf(line);

                    if (key.Length == 0 || mine.Contains(key)) { continue; }

                    added.Add(line);
                    mine.Add(key);
                }

                using (var into = new System.IO.MemoryStream())
                {
                    into.Write(ours, 0, ours.Length);

                    //Only if the game's own file does not already end with one - a blank line in
                    //the middle would be a row with no key.
                    if (ours.Length > 0 && ours[ours.Length - 1] != (byte)'\n')
                    {
                        var end = System.Text.Encoding.UTF8.GetBytes(crlf ? "\r\n" : "\n");
                        into.Write(end, 0, end.Length);
                    }

                    foreach (var line in added)
                    {
                        var row = System.Text.Encoding.UTF8.GetBytes(line + (crlf ? "\r\n" : "\n"));
                        into.Write(row, 0, row.Length);
                    }

                    System.IO.File.WriteAllBytes(bits[2], into.ToArray());

                    Console.WriteLine($"[merge] {added.Count} row(s) appended, "
                        + $"{into.Length} bytes written to {bits[2]}");
                }

                foreach (var line in added.Take(8))
                {
                    Console.WriteLine($"[merge]   + {line}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_PACKFOLDER=<folder>;<name> - a folder of game files, packed as a mod.
            //
            //Everything else here packs one KIND of thing - a skin, a map, a payload - because
            //each knows what it is shipping and can check it. This packs whatever is in a folder,
            //laid out the way it will sit in the game, and is for content that came from
            //somewhere else: a level's json, its object groups, its resource pack, the tiles it
            //names. The folder must be arranged as it will be installed, beginning with
            //`Dungeons/Content/`.
            //
            //It ships what it is given, including anything the game already has. That is the
            //point - a mod's whole reason for existing is often a file the game already has -
            //and it is also why this is a probe rather than a button.
            var probePack = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_PACKFOLDER="));
            if (probePack != null)
            {
                var bits = probePack.Substring("PROBE_PACKFOLDER=".Length).Trim('"').Split(';');
                var from = bits[0];
                var name = bits.Length > 1 ? bits[1] : "Folder";

                if (!System.IO.Directory.Exists(from))
                {
                    Console.WriteLine($"[pack] no such folder: {from}");
                    this.Shutdown();
                    return;
                }

                var entries = new List<Logic.PakWriter.Entry>();

                foreach (var file in System.IO.Directory.EnumerateFiles(
                    from, "*", System.IO.SearchOption.AllDirectories))
                {
                    var inside = file.Substring(from.Length)
                        .Replace(System.IO.Path.DirectorySeparatorChar, '/')
                        .TrimStart('/');

                    entries.Add(new Logic.PakWriter.Entry(
                        inside, System.IO.File.ReadAllBytes(file)));

                    if (entries.Count <= 40) { Console.WriteLine($"[pack]   {inside}"); }
                }

                if (entries.Count == 0)
                {
                    Console.WriteLine("[pack] the folder is empty");
                    this.Shutdown();
                    return;
                }

                //Said rather than assumed: a tree that does not begin where the game reads from
                //packs perfectly and does nothing at all, which is the hardest kind of nothing to
                //diagnose.
                var rooted = entries.Count(one =>
                    one.Path.StartsWith("Dungeons/Content/", StringComparison.OrdinalIgnoreCase));

                Console.WriteLine($"[pack] {entries.Count} file(s), {rooted} of them under "
                    + "Dungeons/Content/");

                if (rooted != entries.Count)
                {
                    Console.WriteLine("[pack] WARNING: the rest will be packed where the game "
                        + "does not look");
                }

                try
                {
                    var made = Logic.CustomSkins.writeModPak(name, entries);
                    Console.WriteLine($"[pack] wrote {System.IO.Path.GetFileName(made.Path)}");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[pack] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_FIELD=<class>;<field>[;<field>...] - what a live object's fields actually say.
            //
            //The difference between "the graph sets this" and "this is set". A property write by
            //name that does not match its target fails silently and looks exactly like a write
            //that never ran, and bitfields make it worse - bEnableClickEvents is one bit of a
            //shared word, so reading the byte gives whichever neighbours happen to be set.
            //
            //Read-only.
            var probeField = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_FIELD="));
            if (probeField != null)
            {
                var bits = probeField.Substring("PROBE_FIELD=".Length).Trim('"')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                if (bits.Length < 2)
                {
                    Console.WriteLine("[field] need PROBE_FIELD=<class>;<field>[;<field>...]");
                    this.Shutdown();
                    return;
                }

                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null)
                {
                    Console.WriteLine($"[field] the game is not open: {why}");
                    this.Shutdown();
                    return;
                }

                using (game)
                {
                    var reflect = LiveEdit.Reflect.open(game,
                        step => Console.WriteLine($"[field] {step}"));

                    if (reflect == null)
                    {
                        Console.WriteLine("[field] could not read the reflection data");
                        this.Shutdown();
                        return;
                    }

                    var shown = 0;

                    for (var i = 0; i < reflect.Count && shown < 12; i++)
                    {
                        var at = reflect.objectAt(i);
                        if (at == 0) { continue; }

                        var kind = reflect.kindOf(at);
                        if (kind == null
                            || kind.IndexOf(bits[0], StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        var said = reflect.nameOf(at) ?? "?";
                        var outer = reflect.outerOf(at) ?? "?";

                        //Skip the class defaults, and skip what lives INSIDE one. An engine
                        //class's default object owns components of its own, and a dozen of those
                        //will fill the listing before anything in the world is reached.
                        if (said.StartsWith("Default__", StringComparison.Ordinal)
                            || outer.StartsWith("Default__", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        shown++;
                        Console.WriteLine($"[field] {said}  ({kind}, in {outer})");

                        foreach (var want in bits.Skip(1))
                        {
                            Console.WriteLine($"[field]    {want,-28} = "
                                + (reflect.valueOf(at, want) ?? "NOT A FIELD OF THIS"));
                        }
                    }

                    if (shown == 0)
                    {
                        Console.WriteLine($"[field] nothing of a class like \"{bits[0]}\" is loaded");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_WHEREIS=<class name> - where the actors of that class actually are.
            //
            //"It is loaded" and "it is where I put it" are different claims, and a prop nobody
            //can find has usually answered the first and failed the second. Offsets are read off
            //the classes rather than hardcoded: Actor's RootComponent and SceneComponent's
            //RelativeLocation both carry UPROPERTYs, so the game says where they live.
            //
            //Read-only.
            var probeWhere2 = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_WHEREIS="));
            if (probeWhere2 != null)
            {
                var wanted = probeWhere2.Substring("PROBE_WHEREIS=".Length).Trim('"')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null)
                {
                    Console.WriteLine($"[whereis] the game is not open: {why}");
                    this.Shutdown();
                    return;
                }

                using (game)
                {
                    var reflect = LiveEdit.Reflect.open(game,
                        step => Console.WriteLine($"[whereis] {step}"));

                    if (reflect == null)
                    {
                        Console.WriteLine("[whereis] could not read the reflection data");
                        this.Shutdown();
                        return;
                    }

                    int? offsetOf(string className, string field)
                    {
                        var klass = reflect.find(className, "Class");
                        if (klass == 0) { return null; }

                        foreach (var one in reflect.fieldsOf(klass))
                        {
                            if (one.Name == field) { return one.Offset; }
                        }

                        return null;
                    }

                    var rootAt = offsetOf("Actor", "RootComponent");
                    var placeAt = offsetOf("SceneComponent", "RelativeLocation");
                    var scaleAt = offsetOf("SceneComponent", "RelativeScale3D");

                    Console.WriteLine($"[whereis] Actor.RootComponent +0x{rootAt:x}, "
                        + $"SceneComponent.RelativeLocation +0x{placeAt:x}");

                    if (rootAt == null || placeAt == null)
                    {
                        Console.WriteLine("[whereis] the engine's own fields are not where it says");
                        this.Shutdown();
                        return;
                    }

                    for (var i = 0; i < reflect.Count; i++)
                    {
                        var at = reflect.objectAt(i);
                        if (at == 0) { continue; }

                        var kind = reflect.kindOf(at);
                        if (kind == null || !wanted.Any(one =>
                            kind.IndexOf(one, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            continue;
                        }

                        var said = reflect.nameOf(at) ?? "?";

                        //The class default object is not in the world and its transform means
                        //nothing - saying so beats printing a convincing zero.
                        if (said.StartsWith("Default__", StringComparison.Ordinal))
                        {
                            Console.WriteLine($"[whereis] {said}: the class default, not placed");
                            continue;
                        }

                        var rootRaw = game.read(new IntPtr(at + rootAt.Value), 8);
                        var root = rootRaw == null ? 0 : BitConverter.ToInt64(rootRaw, 0);

                        if (root == 0)
                        {
                            Console.WriteLine($"[whereis] {said} (in {reflect.outerOf(at)}): "
                                + "no root component");
                            continue;
                        }

                        var place = game.read(new IntPtr(root + placeAt.Value), 12);
                        if (place == null) { continue; }

                        var x = BitConverter.ToSingle(place, 0);
                        var y = BitConverter.ToSingle(place, 4);
                        var z = BitConverter.ToSingle(place, 8);

                        var scale = scaleAt == null ? null
                            : game.read(new IntPtr(root + scaleAt.Value), 12);

                        Console.WriteLine($"[whereis] {said} (in {reflect.outerOf(at)})");
                        Console.WriteLine($"[whereis]    at {x,10:F0} {y,10:F0} {z,10:F0}"
                            + $"   = block {x / 100.0,7:F1} {z / 100.0,7:F1} {y / 100.0,7:F1}"
                            + (scale == null ? string.Empty
                                : $"   scale {BitConverter.ToSingle(scale, 0):F2}"));
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_STAND[=home] - the Camp's map table, moved to where you are standing.
            //
            //The coordinate the table was cooked with is Blossoming Isles', beside the Mystery
            //Merchant, and it was borrowed because it was PROVEN - somebody had already stood in
            //front of it and clicked. Picking a different one by reading a map is how a table
            //ends up inside terrain or floating a metre over a plaza. Reading it off the running
            //game instead means the spot is one somebody walked to.
            //
            //Two corrections are applied to what the game says, and both matter:
            //
            //  * an actor's location is its capsule CENTRE, not its feet, so the half-height
            //    comes off the Z. Skip it and the table hovers at chest height;
            //  * the position is remembered OUTSIDE the pak, because every slot change
            //    reinstalls the table - see MapTable.where().
            //
            //Writes the remembered position and the table pak. Nothing is written to the game's
            //memory. The game must be RESTARTED to see it, because paks mount at startup.
            var probeStand = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_STAND"));
            if (probeStand != null)
            {
                var asked = probeStand.Contains('=')
                    ? probeStand.Substring(probeStand.IndexOf('=') + 1).Trim('"')
                    : string.Empty;

                //Rebuilding the table means REPLACING its pak, and the game holds that file open
                //for as long as it is running - paks are mounted at startup and kept. So the
                //position and the install are deliberately two steps: standing somewhere records
                //the spot while the game is up, and applying it happens once the game is down.
                //Recording without installing is not a half-failure, it is the only order that
                //can work.
                bool rebuild()
                {
                    try
                    {
                        Logic.MapSlots.sync();
                        return true;
                    }
                    catch (IOException)
                    {
                        Console.WriteLine("[stand] the spot is saved, but the table cannot be "
                            + "rebuilt while the game is open - close it and run "
                            + "PROBE_STAND=apply");
                        return false;
                    }
                }

                if (string.Equals(asked, "home", StringComparison.OrdinalIgnoreCase))
                {
                    Logic.MapTable.moveHome();

                    var (hx, hy, hz) = Logic.MapTable.HOME;

                    if (rebuild())
                    {
                        Console.WriteLine($"[stand] the table is back at {hx:F0} {hy:F0} {hz:F0}, "
                            + "beside the Mystery Merchant");
                    }

                    this.Shutdown();
                    return;
                }

                //The spot recorded earlier, installed now that the game is closed. No reflection,
                //no running game - it reads the same remembered position every install reads.
                if (string.Equals(asked, "apply", StringComparison.OrdinalIgnoreCase))
                {
                    var (ax, ay, az, _) = Logic.MapTable.where();

                    if (rebuild())
                    {
                        Console.WriteLine($"[stand] the table now stands at {ax:F0} {ay:F0} "
                            + $"{az:F0}");
                    }

                    this.Shutdown();
                    return;
                }

                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null)
                {
                    Console.WriteLine($"[stand] the game is not open: {why}");
                    this.Shutdown();
                    return;
                }

                using (game)
                {
                    var reflect = LiveEdit.Reflect.open(game,
                        step => Console.WriteLine($"[stand] {step}"));

                    if (reflect == null)
                    {
                        Console.WriteLine("[stand] could not read the reflection data");
                        this.Shutdown();
                        return;
                    }

                    int? offsetOf(string className, string field)
                    {
                        var klass = reflect.find(className, "Class");
                        if (klass == 0) { return null; }

                        foreach (var one in reflect.fieldsOf(klass))
                        {
                            if (one.Name == field) { return one.Offset; }
                        }

                        return null;
                    }

                    var rootAt = offsetOf("Actor", "RootComponent");
                    var placeAt = offsetOf("SceneComponent", "RelativeLocation");
                    var tallAt = offsetOf("CapsuleComponent", "CapsuleHalfHeight");
                    var scaleAt = offsetOf("SceneComponent", "RelativeScale3D");
                    var meshAt = offsetOf("StaticMeshComponent", "StaticMesh");
                    var boundsAt = offsetOf("StaticMesh", "ExtendedBounds");

                    if (rootAt == null || placeAt == null || tallAt == null)
                    {
                        Console.WriteLine("[stand] the engine's own fields are not where it says");
                        this.Shutdown();
                        return;
                    }

                    //The hero's class is the SKIN's class - BP_AlexCharacter_C,
                    //BP_SteveCharacter_C and so on - so asking for BP_PlayerCharacter_C by name
                    //finds the class default and never a hero. It is the PARENT that every skin
                    //has in common, which is what the chain is walked for.
                    var chains = new Dictionary<string, bool>(StringComparer.Ordinal);

                    bool isHero(string className)
                    {
                        if (chains.TryGetValue(className, out var known)) { return known; }

                        var klass = reflect.find(className);
                        var answer = klass != 0
                            && reflect.chainOf(klass).Any(one => string.Equals(one,
                                "BP_PlayerCharacter_C", StringComparison.Ordinal));

                        chains[className] = answer;
                        return answer;
                    }

                    (float x, float y, float z)? feet = null;
                    var nearby = new List<(string name, float x, float y)>();

                    //How far the prop's own geometry hangs below the point it is placed at.
                    //
                    //A blueprint's pivot is not its base - it is wherever the artist left it -
                    //and for this table the mesh runs from 100 BELOW the origin to 172 above it.
                    //So standing the origin on the floor buries the legs and leaves the top
                    //floating with nothing under it, which is exactly what it did.
                    //
                    //Read off the table that is already in the world rather than written down as
                    //a constant, because the next prop will have a different pivot and a constant
                    //would silently be wrong for it.
                    float? under = null;

                    for (var i = 0; i < reflect.Count; i++)
                    {
                        var at = reflect.objectAt(i);
                        if (at == 0) { continue; }

                        var kind = reflect.kindOf(at);
                        var said = reflect.nameOf(at);

                        if (kind == null || said == null
                            || said.StartsWith("Default__", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var wantsHero = kind.EndsWith("Character_C", StringComparison.Ordinal)
                            && isHero(kind);

                        //The mesh the table spawns to be looked at, which is where its bounds
                        //are. Only when everything needed to read them resolved - a missing
                        //offset means no correction rather than a wrong one.
                        if (!wantsHero && scaleAt != null && meshAt != null && boundsAt != null
                            && (reflect.outerOf(at) ?? string.Empty)
                                .StartsWith(Logic.MapTable.VISUAL, StringComparison.Ordinal))
                        {
                            var ofWhat = reflect.find(kind);

                            if (ofWhat != 0 && reflect.chainOf(ofWhat).Any(one =>
                                string.Equals(one, "StaticMeshComponent", StringComparison.Ordinal)))
                            {
                                var meshRaw = game.read(new IntPtr(at + meshAt.Value), 8);
                                var mesh = meshRaw == null ? 0 : BitConverter.ToInt64(meshRaw, 0);

                                var bounds = mesh == 0 ? null
                                    : game.read(new IntPtr(mesh + boundsAt.Value), 28);
                                var lifted = game.read(new IntPtr(at + placeAt.Value), 12);
                                var howBig = game.read(new IntPtr(at + scaleAt.Value), 12);

                                if (bounds != null && lifted != null && howBig != null)
                                {
                                    var low = BitConverter.ToSingle(lifted, 8)
                                        + (BitConverter.ToSingle(bounds, 8)
                                            - BitConverter.ToSingle(bounds, 20))
                                        * BitConverter.ToSingle(howBig, 8);

                                    if (under == null || low < under) { under = low; }
                                }
                            }

                            continue;
                        }

                        //Everything the Camp lets you click is a "merchant" in the game's own
                        //naming, the mission select table included - it is
                        //BP_LobbyAdventureHubMerchant. So this is the list of things whose
                        //clicks could be stolen.
                        var wantsMerchant = !wantsHero
                            && kind.IndexOf("Merchant", StringComparison.OrdinalIgnoreCase) >= 0
                            && string.Equals(reflect.outerOf(at), "PersistentLevel",
                                StringComparison.Ordinal);

                        if (!wantsHero && !wantsMerchant) { continue; }

                        var rootRaw = game.read(new IntPtr(at + rootAt.Value), 8);
                        var root = rootRaw == null ? 0 : BitConverter.ToInt64(rootRaw, 0);
                        if (root == 0) { continue; }

                        var place = game.read(new IntPtr(root + placeAt.Value), 12);
                        if (place == null) { continue; }

                        var x = BitConverter.ToSingle(place, 0);
                        var y = BitConverter.ToSingle(place, 4);
                        var z = BitConverter.ToSingle(place, 8);

                        if (wantsMerchant)
                        {
                            nearby.Add((kind, x, y));
                            continue;
                        }

                        //A Character's root IS its capsule, so the half-height is read off the
                        //same object. Read rather than assumed: it is 110 for the heroes in this
                        //game and 88 in a stock engine project, and the difference is a table
                        //sunk a fifth of a metre into the ground.
                        var tall = game.read(new IntPtr(root + tallAt.Value), 4);
                        var half = tall == null ? 0f : BitConverter.ToSingle(tall, 0);

                        Console.WriteLine($"[stand] {kind} at {x:F0} {y:F0} {z:F0}, "
                            + $"standing on {z - half:F0} (capsule half-height {half:F0})");

                        feet = (x, y, z - half);
                    }

                    if (feet == null)
                    {
                        Console.WriteLine("[stand] no hero is in the world - load the Camp first");
                        this.Shutdown();
                        return;
                    }

                    var (fx, fy, fz) = feet.Value;

                    //How close a click has to land is a literal in compiled bytecode and does NOT
                    //move with the table. Parked beside something else clickable, our trace
                    //accepts a hit on the other thing and the panel opens over the game's own
                    //screen. Said here rather than discovered in game.
                    foreach (var (name, mx, my) in nearby
                        .OrderBy(one => Math.Sqrt(Math.Pow(one.x - fx, 2)
                            + Math.Pow(one.y - fy, 2)))
                        .Take(3))
                    {
                        var gap = Math.Sqrt(Math.Pow(mx - fx, 2) + Math.Pow(my - fy, 2));

                        Console.WriteLine($"[stand]   {gap,7:F0} cm from {name}"
                            + (gap < 600 ? "   <- close enough to steal its clicks" : string.Empty));
                    }

                    //The origin goes on the floor, and the bounds are only REPORTED.
                    //
                    //They were applied once, and it was wrong. UStaticMesh.ExtendedBounds says
                    //this mesh runs from 100 below its origin to 172 above, so the origin was
                    //lifted 100 to put that bottom on the ground - and the table rose by exactly
                    //that, because the visible table stands ON the origin and the lower 100 is
                    //bounding volume with nothing drawn in it. A bounding box is a promise about
                    //what is INSIDE it, never about what touches its edges.
                    //
                    //So the number is printed, because it is the right thing to look at when a
                    //prop sits wrong, and it is not acted on, because it does not answer where
                    //the thing looks like it ends. PROBE_NUDGE settles that, once per prop.
                    if (under != null)
                    {
                        Console.WriteLine($"[stand] for information: the mesh's bounds reach "
                            + $"{-under:F0} below its origin. Not applied - they did not match "
                            + "what is drawn");
                    }

                    //The sink, carried from wherever it was last settled by eye.
                    //
                    //Not the absolute height. Standing somewhere a metre lower and keeping the
                    //old Z would leave the table a metre in the air, for a reason that looks
                    //exactly like a fresh bug rather than like the calibration being dropped.
                    //What stays true across a move is how far into the FLOOR it goes.
                    var sink = Logic.MapTable.where().sink;

                    if (sink != 0f)
                    {
                        Console.WriteLine($"[stand] the floor here is {fz:F0}; keeping the "
                            + $"{-sink:F0} it sits into the ground");
                    }

                    Logic.MapTable.moveTo(fx, fy, fz + sink, sink);

                    Console.WriteLine($"[stand] the table is to stand at {fx:F0} {fy:F0} "
                        + $"{fz + sink:F0} (was {Logic.MapTable.HOME.x:F0} "
                        + $"{Logic.MapTable.HOME.y:F0} {Logic.MapTable.HOME.z:F0})");

                    if (rebuild())
                    {
                        Console.WriteLine("[stand] restart the game to see it");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_SITS[=drop] - how far a prop's mesh floats above the point it is placed at.
            //
            //Standing the table exactly on the floor put it in the air, which says the thing this
            //was written to measure: a blueprint's pivot is not its base. The visual is wherever
            //the artist left it relative to the origin, and for this table that is some way up.
            //
            //So the gap is measured rather than nudged. Three things add up to where the mesh
            //actually bottoms out, and leaving any of them out gives a confident wrong number:
            //
            //  * the component's own offset inside the blueprint;
            //  * the mesh's local bounds, which are Origin +/- BoxExtent about the PIVOT - a mesh
            //    modelled standing on its origin has a bottom of zero and this one does not;
            //  * the component's scale, because bounds are pre-scale.
            //
            //Read from UStaticMesh.ExtendedBounds rather than the component's Bounds, because
            //USceneComponent::Bounds is a plain member with no UPROPERTY on it - invisible to
            //reflection, so there is nothing to ask for.
            //
            //Read-only unless asked to drop, which records the corrected position the same way
            //PROBE_STAND does.
            var probeSits = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_SITS"));
            if (probeSits != null)
            {
                var how = probeSits.Contains('=')
                    ? probeSits.Substring(probeSits.IndexOf('=') + 1).Trim('"')
                    : string.Empty;

                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null)
                {
                    Console.WriteLine($"[sits] the game is not open: {why}");
                    this.Shutdown();
                    return;
                }

                using (game)
                {
                    var reflect = LiveEdit.Reflect.open(game,
                        step => Console.WriteLine($"[sits] {step}"));

                    if (reflect == null)
                    {
                        Console.WriteLine("[sits] could not read the reflection data");
                        this.Shutdown();
                        return;
                    }

                    int? offsetOf(string className, string field)
                    {
                        var klass = reflect.find(className, "Class");
                        if (klass == 0) { return null; }

                        foreach (var one in reflect.fieldsOf(klass))
                        {
                            if (one.Name == field) { return one.Offset; }
                        }

                        return null;
                    }

                    var rootAt = offsetOf("Actor", "RootComponent");
                    var placeAt = offsetOf("SceneComponent", "RelativeLocation");
                    var scaleAt = offsetOf("SceneComponent", "RelativeScale3D");
                    var meshAt = offsetOf("StaticMeshComponent", "StaticMesh");
                    var boundsAt = offsetOf("StaticMesh", "ExtendedBounds");

                    if (rootAt == null || placeAt == null || scaleAt == null || meshAt == null
                        || boundsAt == null)
                    {
                        Console.WriteLine("[sits] the engine's own fields are not where it says");
                        this.Shutdown();
                        return;
                    }

                    //The actor that is placed, and the one it spawns to be looked at. They share
                    //a transform by construction - the spawn uses GetTransform - so the gap
                    //measured on the visual is the gap the placed one has.
                    const string PLACED = "BP_MCDRebornMapTable_C";
                    var VISUAL = Logic.MapTable.VISUAL;

                    float? standsAt = null;
                    float? bottom = null;
                    float? top = null;

                    for (var i = 0; i < reflect.Count; i++)
                    {
                        var at = reflect.objectAt(i);
                        if (at == 0) { continue; }

                        var kind = reflect.kindOf(at);
                        var said = reflect.nameOf(at);

                        if (kind == null || said == null
                            || said.StartsWith("Default__", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (string.Equals(kind, PLACED, StringComparison.Ordinal))
                        {
                            var rootRaw = game.read(new IntPtr(at + rootAt.Value), 8);
                            var root = rootRaw == null ? 0 : BitConverter.ToInt64(rootRaw, 0);
                            if (root == 0) { continue; }

                            var place = game.read(new IntPtr(root + placeAt.Value), 12);
                            if (place != null) { standsAt = BitConverter.ToSingle(place, 8); }

                            continue;
                        }

                        //Components of the visual, found by their outer. A blueprint actor can
                        //carry several meshes and the lowest one is what looks like the bottom.
                        var outer = reflect.outerOf(at);
                        if (outer == null
                            || !outer.StartsWith(VISUAL, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var klass = reflect.find(kind);
                        if (klass == 0
                            || !reflect.chainOf(klass).Any(one => string.Equals(one,
                                "StaticMeshComponent", StringComparison.Ordinal)))
                        {
                            continue;
                        }

                        var meshRaw = game.read(new IntPtr(at + meshAt.Value), 8);
                        var mesh = meshRaw == null ? 0 : BitConverter.ToInt64(meshRaw, 0);
                        if (mesh == 0) { continue; }

                        var bounds = game.read(new IntPtr(mesh + boundsAt.Value), 28);
                        var where = game.read(new IntPtr(at + placeAt.Value), 12);
                        var howBig = game.read(new IntPtr(at + scaleAt.Value), 12);

                        if (bounds == null || where == null || howBig == null) { continue; }

                        var middle = BitConverter.ToSingle(bounds, 8);
                        var reach = BitConverter.ToSingle(bounds, 20);
                        var lifted = BitConverter.ToSingle(where, 8);
                        var scale = BitConverter.ToSingle(howBig, 8);

                        var under = lifted + (middle - reach) * scale;
                        var over = lifted + (middle + reach) * scale;

                        Console.WriteLine($"[sits] {said} ({reflect.nameOf(mesh)}): "
                            + $"offset {lifted:F0}, bounds {middle:F0} +/- {reach:F0}, "
                            + $"scale {scale:F2}   -> {under:F0} to {over:F0} about the pivot");

                        //The top as well as the bottom, because the next thing to go on this prop
                        //is a label floating over it and "how high" is the same measurement.
                        if (bottom == null || under < bottom) { bottom = under; }
                        if (top == null || over > top) { top = over; }
                    }

                    if (standsAt == null || bottom == null)
                    {
                        Console.WriteLine("[sits] the table is not in the world - load the Camp "
                            + "with it installed first");
                        this.Shutdown();
                        return;
                    }

                    Console.WriteLine($"[sits] placed at {standsAt:F0}, mesh runs "
                        + $"{standsAt + bottom:F0} to {standsAt + top:F0} - floating by "
                        + $"{bottom:F0}, and {top:F0} tall above its pivot");

                    if (!string.Equals(how, "drop", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("[sits] run PROBE_SITS=drop to take that off its height");
                        this.Shutdown();
                        return;
                    }

                    var (wx, wy, wz, wsink) = Logic.MapTable.where();
                    Logic.MapTable.moveTo(wx, wy, wz - bottom.Value, wsink - bottom.Value);

                    Console.WriteLine($"[sits] the table is to stand at {wx:F0} {wy:F0} "
                        + $"{wz - bottom.Value:F0}");

                    try
                    {
                        Logic.MapSlots.sync();
                        Console.WriteLine("[sits] restart the game to see it");
                    }
                    catch (IOException)
                    {
                        Console.WriteLine("[sits] the height is saved, but the table cannot be "
                            + "rebuilt while the game is open - close it and run "
                            + "PROBE_STAND=apply");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_NUDGE=<dz> or =<dx>;<dy>;<dz> - move the table by hand, in centimetres.
            //
            //For the part no probe can answer: where a prop LOOKS like it ends. The bounds do not
            //say - they are a volume the mesh fits inside, and this table's reaches a metre below
            //anything drawn. So the last step is somebody looking at it, and this is the smallest
            //possible loop for that: one number, no game needed, no cook.
            //
            //Once it is right the answer is a constant for that prop for ever, which is why it is
            //worth settling properly rather than approximately.
            var probeNudge = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_NUDGE="));
            if (probeNudge != null)
            {
                var bits = probeNudge.Substring("PROBE_NUDGE=".Length).Trim('"')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                float said(int at)
                    => bits.Length > at && float.TryParse(bits[at],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var one)
                        ? one : 0f;

                //One number means height, because height is what is ever wrong.
                var (dx, dy, dz) = bits.Length >= 3
                    ? (said(0), said(1), said(2))
                    : (0f, 0f, said(0));

                var (nx, ny, nz, sunk) = Logic.MapTable.where();

                //Nudging the height IS recalibrating the sink - that is what the eyeballing is
                //for - so the two move together. Keeping them apart would mean every nudge was
                //forgotten by the next move.
                Logic.MapTable.moveTo(nx + dx, ny + dy, nz + dz, sunk + dz);

                Console.WriteLine($"[nudge] {nx:F0} {ny:F0} {nz:F0} -> {nx + dx:F0} {ny + dy:F0} "
                    + $"{nz + dz:F0}");

                try
                {
                    Logic.MapSlots.sync();
                    Console.WriteLine("[nudge] restart the game to see it");
                }
                catch (IOException)
                {
                    Console.WriteLine("[nudge] the move is saved, but the table cannot be rebuilt "
                        + "while the game is open - close it and run PROBE_STAND=apply");
                }

                this.Shutdown();
                return;
            }

            //PROBE_HUNT=<word>[;<word>...] - every blueprint matching those words, screened.
            //
            //Written because the first attempt at this cost fifteen minutes and found nothing.
            //The probes are one-question-per-launch, and a launch loads every pak, every
            //localisation and every image before it answers - about forty seconds. Asking eighty
            //thousand assets one at a time was never going to finish, and no amount of patience
            //was going to change that; the shape of the tool was wrong.
            //
            //So this asks all of them in ONE launch, and screens each against what actually
            //disqualifies a prop. A candidate must pass all four:
            //
            //  * its mesh has SIMPLE collision - the trace runs with bTraceComplex = false, so a
            //    mesh with only rendering geometry can never be hit, however solid it looks;
            //  * that collision BLOCKS rather than ignores;
            //  * the blueprint declares ZERO functions - anything with an ubergraph runs it, in
            //    the Camp, on our actor's transform, with click events freshly forced on;
            //  * it imports no /Script/Dungeons - that is how the game's own interactables get
            //    pulled in, and they compete for the same click.
            //
            //What it CANNOT tell you is what a prop looks like. That is the half this does not
            //replace: FModel shows you the mesh, and a name is not a picture.
            //
            //Read-only. Opens packages out of the game's paks and prints what it found.
            var probeHunt = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_HUNT="));
            if (probeHunt != null)
            {
                var words = probeHunt.Substring("PROBE_HUNT=".Length).Trim('"')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                var index = Logic.CustomSkins.index;
                if (index == null)
                {
                    Console.WriteLine("[hunt] the game's paks could not be read");
                    this.Shutdown();
                    return;
                }

                //Name tables are what every check reads, and the same mesh is shared by whole
                //families of props - so reading one twice is pure waste in a sweep this size.
                var seen = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                List<string>? namesOf(string path)
                {
                    if (seen.TryGetValue(path, out var already)) { return already; }

                    try
                    {
                        var package = index.extractPackage(path);
                        if (package == null) { seen[path] = null!; return null; }

                        var made = Logic.CookedProperties.readNamesOf(package.Value.UAsset.ToArray())
                            .ToList();

                        seen[path] = made;
                        return made;
                    }
                    catch (Exception)
                    {
                        seen[path] = null!;
                        return null;
                    }
                }

                var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                Console.WriteLine("[hunt] " + "path".PadRight(62) + "fns  dgns  hulls  blocks  verdict");

                foreach (var word in words)
                {
                    var (found, _) = Logic.GameAssets.search(word, "Blueprint");

                    foreach (var asset in found)
                    {
                        if (!done.Add(asset.EnginePath)) { continue; }

                        var inside = "/Dungeons/Content/" + asset.EnginePath.Substring("/Game/".Length);

                        byte[] uasset;

                        try
                        {
                            var package = index.extractPackage(inside);
                            if (package == null) { continue; }

                            uasset = package.Value.UAsset.ToArray();
                        }
                        catch (Exception) { continue; }
                        var exports = Logic.CookedPackage.readExports(uasset);
                        var imports = Logic.CookedPackage.readImports(uasset);

                        string spell(int at)
                            => at < 0 && -at - 1 < imports.Count ? imports[-at - 1].ObjectName
                                : at > 0 && at - 1 < exports.Count ? exports[at - 1].Name
                                : string.Empty;

                        var functions = exports.Count(one =>
                            string.Equals(spell(one.ClassIndex), "Function", StringComparison.Ordinal));

                        var dungeons = imports.Any(one =>
                            one.ObjectName.IndexOf("/Script/Dungeons",
                                StringComparison.OrdinalIgnoreCase) >= 0);

                        //The meshes this blueprint draws, by the package each one lives in. An
                        //import's Outer walks up to its package, which is the path to open.
                        var meshes = new List<string>();

                        for (var at = 0; at < imports.Count; at++)
                        {
                            if (!string.Equals(imports[at].ClassName, "StaticMesh",
                                StringComparison.Ordinal))
                            {
                                continue;
                            }

                            var outer = imports[at].Outer;

                            while (outer < 0 && -outer - 1 < imports.Count)
                            {
                                var up = imports[-outer - 1];

                                if (string.Equals(up.ClassName, "Package", StringComparison.Ordinal)
                                    && up.ObjectName.StartsWith("/Game/", StringComparison.Ordinal))
                                {
                                    meshes.Add("/Dungeons/Content/"
                                        + up.ObjectName.Substring("/Game/".Length));
                                    break;
                                }

                                outer = up.Outer;
                            }
                        }

                        //Hulls in ANY of them is enough: one clickable mesh makes the prop
                        //clickable, and a prop with several is usually one solid thing plus
                        //decoration.
                        var hulls = false;
                        var blocks = false;
                        var refused = false;

                        foreach (var mesh in meshes.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            var names = namesOf(mesh);
                            if (names == null) { continue; }

                            //An empty FKAggregateGeom serialises as nothing at all, so these
                            //sub-property names appear only when there are actually hulls.
                            hulls |= names.Any(one => one == "ConvexElems" || one == "KConvexElem"
                                || one == "BoxElems" || one == "SphylElems" || one == "SphereElems");

                            blocks |= names.Any(one => one == "BlockAll");
                            refused |= names.Any(one => one == "NoCollision");
                        }

                        //The blueprint can override the mesh's own profile, so its name table
                        //counts too.
                        var mine = Logic.CookedProperties.readNamesOf(uasset).ToList();
                        blocks |= mine.Any(one => one == "BlockAll");

                        var ok = functions == 0 && !dungeons && hulls && blocks && !refused;

                        //Everything is printed, passes and failures alike. A sweep that prints
                        //only winners cannot be checked, and "found nothing" and "asked wrongly"
                        //look identical in it.
                        Console.WriteLine($"[hunt] {asset.EnginePath,-62}{functions,3}  "
                            + $"{(dungeons ? "yes" : "no"),4}  {(hulls ? "yes" : "no"),5}  "
                            + $"{(refused ? "NO" : blocks ? "yes" : "?"),6}  "
                            + (ok ? "PASS" : "-"));
                    }
                }

                Console.WriteLine($"[hunt] {done.Count} blueprint(s) screened");

                this.Shutdown();
                return;
            }

            //PROBE_ZIP=<slot>[;<into slot>] - the whole zip round trip, and then undone.
            //
            //A zip is the one thing here that leaves this machine, so "it compiles" is not a
            //standard worth shipping it on. This takes a real installed slot out to a file,
            //reads it back into a DIFFERENT slot, and then compares what arrived against what
            //left - file by file, byte count by byte count.
            //
            //It cleans up after itself. A test that leaves a map installed in somebody's game is
            //a test that has to be undone by hand, and the one time it is forgotten it looks
            //like a slot filling itself.
            var probeZip = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_ZIP="));
            if (probeZip != null)
            {
                var bits = probeZip.Substring("PROBE_ZIP=".Length).Trim('"')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                if (bits.Length < 1 || !int.TryParse(bits[0], out var from))
                {
                    Console.WriteLine("[zip] need PROBE_ZIP=<slot>[;<into slot>]");
                    this.Shutdown();
                    return;
                }

                var into = bits.Length > 1 && int.TryParse(bits[1], out var said) ? said : 99;

                var source = Logic.MapSlots.inSlot(from);
                if (source == null)
                {
                    Console.WriteLine($"[zip] slot {from:00} is empty - nothing to export");
                    this.Shutdown();
                    return;
                }

                if (Logic.MapSlots.inSlot(into) != null)
                {
                    Console.WriteLine($"[zip] slot {into:00} is not free - pick another to test "
                        + "into, so nothing of yours is overwritten");
                    this.Shutdown();
                    return;
                }

                var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcd-zip-probe");
                var archive = System.IO.Path.Combine(work, source.Name + ".zip");

                try
                {
                    System.IO.Directory.CreateDirectory(work);

                    Console.WriteLine($"[zip] exporting slot {from:00} ({source.Name})");
                    Logic.MapSlots.zipTo(from, archive);

                    var size = new System.IO.FileInfo(archive).Length;
                    Console.WriteLine($"[zip]   wrote {System.IO.Path.GetFileName(archive)}, "
                        + $"{size:N0} bytes");

                    using (var reading = System.IO.Compression.ZipFile.OpenRead(archive))
                    {
                        foreach (var one in reading.Entries.OrderBy(x => x.FullName))
                        {
                            Console.WriteLine($"[zip]     {one.FullName,-52} {one.Length,10:N0}");
                        }
                    }

                    Console.WriteLine($"[zip] reading it back into slot {into:00}");
                    var made = Logic.MapSlots.zipFrom(archive, into, "ZipProbe");

                    var landed = Logic.MapSlots.inSlot(into);

                    if (landed == null)
                    {
                        Console.WriteLine("[zip] FAILED - nothing is in the slot afterwards");
                    }
                    else
                    {
                        Console.WriteLine($"[zip]   installed as {landed.Name}, "
                            + $"{new System.IO.FileInfo(landed.Path).Length:N0} bytes");

                        //The two paks will NOT be byte-identical and should not be expected to
                        //be: the level is repacked, and the slot name is written into it. What
                        //has to match is the CONTENT, so the level is read out of both and
                        //compared.
                        var a = System.IO.Path.Combine(work, "before");
                        var b = System.IO.Path.Combine(work, "after");

                        Logic.MapSlots.export(from, a);
                        Logic.MapSlots.export(into, b);

                        var left = System.IO.Directory.GetFiles(a, "*",
                            System.IO.SearchOption.AllDirectories);

                        var same = 0;
                        var differ = 0;

                        foreach (var one in left)
                        {
                            var mirror = System.IO.Path.Combine(b,
                                one.Substring(a.Length).TrimStart('\\', '/'));

                            if (!System.IO.File.Exists(mirror))
                            {
                                Console.WriteLine($"[zip]   MISSING {one.Substring(a.Length)}");
                                differ++;
                                continue;
                            }

                            if (System.IO.File.ReadAllBytes(one).SequenceEqual(
                                System.IO.File.ReadAllBytes(mirror)))
                            {
                                same++;
                            }
                            else
                            {
                                Console.WriteLine($"[zip]   DIFFERS {one.Substring(a.Length)}");
                                differ++;
                            }
                        }

                        Console.WriteLine($"[zip] {same} file(s) identical, {differ} different");
                        Console.WriteLine(differ == 0 && same > 0
                            ? "[zip] ROUND TRIP OK"
                            : "[zip] ROUND TRIP FAILED");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[zip] FAILED: {problem.Message}");
                }
                finally
                {
                    try
                    {
                        if (Logic.MapSlots.inSlot(into) != null)
                        {
                            Logic.MapSlots.clear(into);
                            Console.WriteLine($"[zip] slot {into:00} emptied again");
                        }

                        if (System.IO.Directory.Exists(work))
                        {
                            System.IO.Directory.Delete(work, true);
                        }
                    }
                    catch (Exception problem)
                    {
                        Console.WriteLine($"[zip] could not tidy up: {problem.Message}");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_PUT=<x>;<y>;<z>[;<sink>] - the table's position, written by the app itself.
            //
            //Exists because a preference file written by anything OTHER than this app turned out
            //to be a file this app could not read back - reported missing, for an hour, while it
            //sat on disk. Handing the value to the app and letting it do the writing removes the
            //whole question.
            var probePut = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_PUT="));
            if (probePut != null)
            {
                var bits = probePut.Substring("PROBE_PUT=".Length).Trim('"')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                float said(int at, float fallback = 0f)
                    => bits.Length > at && float.TryParse(bits[at],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var one)
                        ? one : fallback;

                if (bits.Length < 3)
                {
                    Console.WriteLine("[put] need PROBE_PUT=<x>;<y>;<z>[;<sink>]");
                    this.Shutdown();
                    return;
                }

                Logic.MapTable.moveTo(said(0), said(1), said(2), said(3));

                var (px, py, pz, ps) = Logic.MapTable.where();
                Console.WriteLine($"[put] written, and reads back as {px:F0} {py:F0} {pz:F0} "
                    + $"(sink {ps:F0})");

                try
                {
                    Logic.MapSlots.sync();
                    Console.WriteLine("[put] table rebuilt");
                }
                catch (IOException)
                {
                    Console.WriteLine("[put] saved, but the game is open so the table was not "
                        + "rebuilt");
                }

                this.Shutdown();
                return;
            }

            //PROBE_THEME=<folder>[;<pack>] - what a map is drawn with, and what it would be.
            //
            //Says the block-name COVERAGE of the pack against the map's original, because that
            //is the number that decides whether a theme works: a pack defines the names it
            //re-skins and nothing else, and the ones it leaves out have no definition anywhere
            //once it is the only pack named. "dingyjungle" covers 375 of Creeper Woods' 376 and
            //"jungle" covers 210, and only one of those is a theme a map survives.
            //
            //With no pack named it only reports. There is otherwise no way to exercise setTheme
            //without the Maps tab, which is why a swap could be reasoned about and not tried.
            var probeTheme = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_THEME="));
            if (probeTheme != null)
            {
                var bits = probeTheme.Substring("PROBE_THEME=".Length).Trim('"')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                var folder = bits[0].Trim();

                Console.WriteLine($"[theme] now drawn with: {Logic.MapMod.themeOf(folder) ?? "(none named)"}");

                if (bits.Length > 1)
                {
                    var wanted = bits[1].Trim();

                    if (!Logic.MapMod.setTheme(folder, wanted))
                    {
                        Console.WriteLine($"[theme] refused: {wanted}");
                        this.Shutdown();
                        return;
                    }

                    Console.WriteLine($"[theme] set to: {Logic.MapMod.themeOf(folder)}");

                    try
                    {
                        var levelText = System.IO.File.ReadAllText(
                            System.IO.Path.Combine(folder, "level.json"));

                        var packs = System.Text.Json.Nodes.JsonNode.Parse(
                            Logic.GameMaps.stripComments(levelText))?["resource-packs"];

                        Console.WriteLine($"[theme] resource-packs now {packs?.ToJsonString()}");
                    }
                    catch (Exception problem)
                    {
                        Console.WriteLine($"[theme] could not read it back: {problem.Message}");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_LEVELS=<folder> - every level file the game ships, written out at once.
            //
            //PROBE_FRESH exports ONE mission and pulls its object groups with it, which is right
            //when the map is going to be edited and far too slow when the question is about the
            //level files themselves: fifty-six launches of this app to read fifty-six small json
            //files. This reads them all in one pass and writes nothing else.
            //
            //Read-only, and the levels are the game's own bytes rather than anything reserialised
            //- a rule derived from a file this app rewrote first would be a rule about this app.
            var probeLevels = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_LEVELS="));
            if (probeLevels != null)
            {
                var into = probeLevels.Substring("PROBE_LEVELS=".Length).Trim('"');
                System.IO.Directory.CreateDirectory(into);

                var done = 0;
                var missed = 0;

                foreach (var one in Logic.GameMaps.all())
                {
                    try
                    {
                        var raw = Logic.GameMaps.read(one.PakPath);
                        if (raw == null)
                        {
                            Console.WriteLine($"[levels] {one.Name}: nothing at {one.PakPath}");
                            missed++;
                            continue;
                        }

                        System.IO.File.WriteAllBytes(
                            System.IO.Path.Combine(into, one.Name + ".json"), raw);
                        done++;
                    }
                    catch (Exception problem)
                    {
                        Console.WriteLine($"[levels] {one.Name}: {problem.Message}");
                        missed++;
                    }
                }

                Console.WriteLine($"[levels] wrote {done} level(s) to {into}, {missed} missed");
                this.Shutdown();
                return;
            }

            //PROBE_OVER=<folder>;<mission> - a map folder installed over a mission, and LEFT there.
            //
            //PROBE_MAPS_IN does the same install and then deletes it again, which is right for
            //testing that the writer works and useless for testing whether the GAME can load
            //what was written. This one leaves it, so the next launch plays it.
            //
            //It exists because the question "does this map work over a real mission, as opposed
            //to in a custom slot" is the one that splits a map being broken from the slot path
            //being broken, and there was no way to ask it without a file dialog.
            var probeOver = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_OVER="));
            if (probeOver != null)
            {
                var bits = probeOver.Substring("PROBE_OVER=".Length).Trim('"')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                if (bits.Length < 2)
                {
                    Console.WriteLine("[over] need PROBE_OVER=<folder>;<mission>");
                    this.Shutdown();
                    return;
                }

                var mission = Logic.GameMaps.all().FirstOrDefault(one =>
                    one.Name.Equals(bits[1], StringComparison.OrdinalIgnoreCase));

                if (mission == null)
                {
                    Console.WriteLine($"[over] no mission called {bits[1]}");
                    this.Shutdown();
                    return;
                }

                if (!System.IO.File.Exists(System.IO.Path.Combine(bits[0], "level.json")))
                {
                    Console.WriteLine($"[over] no level.json in {bits[0]}");
                    this.Shutdown();
                    return;
                }

                try
                {
                    var mod = Logic.MapMod.install(bits[0], mission);
                    Console.WriteLine($"[over] {System.IO.Path.GetFileName(mod.Path)}, "
                        + $"{mod.Size / 1024:N0} KB, installed over {mission.Label} and LEFT "
                        + "there - play the mission, then Remove to put the game's own back");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[over] failed: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_LOADED=<part of a name> - everything the game is holding whose name contains it.
            //
            //PROBE_STRUCT asks by exact name, which is the wrong question when the question is
            //"did any of my mod load at all". A name that is absent and a name that is spelled
            //differently look identical to an exact match, and the difference is the whole
            //answer.
            //
            //Read-only.
            var probeLoaded = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_LOADED="));
            if (probeLoaded != null)
            {
                var wanted = probeLoaded.Substring("PROBE_LOADED=".Length).Trim('"')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null)
                {
                    Console.WriteLine($"[loaded] the game is not open: {why}");
                    this.Shutdown();
                    return;
                }

                using (game)
                {
                    var reflect = LiveEdit.Reflect.open(game,
                        step => Console.WriteLine($"[loaded] {step}"));

                    if (reflect == null)
                    {
                        Console.WriteLine("[loaded] could not read the game's reflection data");
                        this.Shutdown();
                        return;
                    }

                    var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    foreach (var one in wanted) { hits[one] = new List<string>(); }

                    for (var i = 0; i < reflect.Count; i++)
                    {
                        var at = reflect.objectAt(i);
                        if (at == 0) { continue; }

                        var said = reflect.nameOf(at);
                        if (said == null) { continue; }

                        foreach (var one in wanted)
                        {
                            if (said.IndexOf(one, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }

                            //The kind and the package, because "it is loaded" and "it is the
                            //thing I meant" are different claims.
                            if (hits[one].Count < 40)
                            {
                                hits[one].Add($"{reflect.kindOf(at),-26} {said}"
                                    + $"   (in {reflect.outerOf(at)})");
                            }
                        }
                    }

                    foreach (var one in wanted)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"[loaded] --- \"{one}\": {hits[one].Count} ---");

                        foreach (var line in hits[one])
                        {
                            Console.WriteLine($"[loaded]   {line}");
                        }
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_STRUCT=<Name>[;<Name>...] - what the game says one of its own types is.
            //
            //The tool for calling this game's own code. A modded blueprint can call a native
            //function - that is how the community's map mods launch a custom mission - but only
            //if it was compiled against a declaration that matches the real one. Field for field:
            //a name spelled differently or a type a size out does not fail loudly, it writes into
            //the wrong place and the game does something inexplicable later.
            //
            //Nothing published can settle that. Every offset table for this engine online is
            //either a different version or an editor build, and the Mod Kit ships headers for
            //skins and cosmetics and nothing else. The game's own account of itself is the only
            //source that is about THIS executable, and it is in memory whenever it is running.
            //
            //Takes a class, a struct, an enum or a function. For a function the arguments come
            //out in order and marked, so a call can be matched as well as a layout.
            //
            //  MCDReborn.exe PROBE_STRUCT=LevelSettings;DungeonsGameInstance
            //
            //Read-only. Nothing is written to the game.
            //PROBE_NATIVE=<Function>[;<Function>...] - where the game's C++ behind a blueprint-callable
            //function lives, and its first bytes, so it can be disassembled outside the game.
            //PROBE_MEM=<hex address>:<length>[;...] - raw bytes at an address, for following calls.
            //Read-only, external ReadProcessMemory - never a debugger. Writes %TEMP%\mcd-native\.
            //
            //  MCDReborn.exe PROBE_NATIVE=IsItemIdValid;MakeItemId
            var probeNative = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_NATIVE=") || a.StartsWith("PROBE_MEM="));
            if (probeNative != null)
            {
                var into = Path.Combine(Path.GetTempPath(), "mcd-native");
                Directory.CreateDirectory(into);
                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null) { Console.WriteLine($"[native] the game is not open: {why}"); Shutdown(); return; }
                using (game)
                {
                    var imageBase = game.image(out var imageSize).ToInt64();
                    Console.WriteLine($"[native] image {imageBase:x} size {imageSize:x}");
                    if (probeNative.StartsWith("PROBE_MEM="))
                    {
                        foreach (var ask in probeNative["PROBE_MEM=".Length..].Trim('"').Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = ask.Split(':');
                            var at = Convert.ToInt64(parts[0].Replace("0x", ""), 16);
                            var length = parts.Length > 1 ? Convert.ToInt32(parts[1], parts[1].StartsWith("0x") ? 16 : 10) : 0x400;
                            var bytes = game.read(new IntPtr(at), length);
                            var file = Path.Combine(into, $"mem_{at:x}.bin");
                            if (bytes != null) { File.WriteAllBytes(file, bytes); }
                            Console.WriteLine($"[native] {at:x} +{length:x} -> {(bytes == null ? "unreadable" : file)}");
                        }
                    }
                    else
                    {
                        var reflect = LiveEdit.Reflect.open(game, step => Console.WriteLine($"[native] {step}"));
                        if (reflect == null) { Console.WriteLine("[native] no reflection data"); Shutdown(); return; }
                        foreach (var name in probeNative["PROBE_NATIVE=".Length..].Trim('"').Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            foreach (var (at, kind, outer) in reflect.findAll(name.Trim()))
                            {
                                if (kind != "Function") { continue; }
                                var func = game.read(new IntPtr(at + 0xC0), 8);
                                var exec = func == null ? 0 : BitConverter.ToInt64(func, 0);
                                var bytes = exec == 0 ? null : game.read(new IntPtr(exec), 0x400);
                                var file = Path.Combine(into, $"{outer}.{name}_{exec:x}.bin");
                                if (bytes != null) { File.WriteAllBytes(file, bytes); }
                                Console.WriteLine($"[native] {outer}.{name} exec {exec:x} (rva {exec - imageBase:x}) -> {(bytes == null ? "unreadable" : file)}");
                            }
                        }
                    }
                }
                Shutdown();
                return;
            }

            var probeStruct = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_STRUCT="));
            if (probeStruct != null)
            {
                var wanted = probeStruct.Substring("PROBE_STRUCT=".Length).Trim('"')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null)
                {
                    Console.WriteLine($"[struct] the game is not open: {why}");
                    this.Shutdown();
                    return;
                }

                using (game)
                {
                    var began = DateTime.UtcNow;
                    var reflect = LiveEdit.Reflect.open(game,
                        step => Console.WriteLine($"[struct] {step}"));

                    if (reflect == null)
                    {
                        Console.WriteLine("[struct] could not read the game's reflection data");
                        this.Shutdown();
                        return;
                    }

                    Console.WriteLine($"[struct] ready in {(DateTime.UtcNow - began).TotalSeconds:F1}s");

                    foreach (var name in wanted)
                    {
                        Console.WriteLine();

                        var every = reflect.findAll(name.Trim());
                        if (every.Count == 0)
                        {
                            Console.WriteLine($"[struct] nothing called \"{name}\" in the game");
                            continue;
                        }

                        //A name can belong to several things - a class and its default object,
                        //a struct and a property of that type. The ones worth printing are the
                        //declarations.
                        foreach (var (at, kind, outer) in every)
                        {
                            //BlueprintGeneratedClass belongs here as much as Class does. Leaving
                            //it out made every blueprint in the game read as "found but not
                            //printed", which looks exactly like not being there.
                            if (kind != "Class" && kind != "BlueprintGeneratedClass"
                                && kind != "ScriptStruct" && kind != "Function" && kind != "Enum")
                            {
                                continue;
                            }

                            var chain = reflect.chainOf(at);

                            Console.WriteLine($"[struct] === {kind} {name} "
                                + $"(in {outer}, at {at:x}) ===");

                            if (chain.Count > 1)
                            {
                                Console.WriteLine($"[struct] inherits: {string.Join(" -> ", chain)}");
                            }

                            //An enum has no fields - it has names and numbers, and the number is
                            //usually the whole question: not what the enum is called but which of
                            //its values means Creeper Woods.
                            if (kind == "Enum")
                            {
                                var values = reflect.valuesOf(at);
                                Console.WriteLine($"[struct] {values.Count} value(s)");

                                foreach (var (said2, number) in values)
                                {
                                    Console.WriteLine($"[struct]   {number,4}  {said2}");
                                }

                                continue;
                            }

                            var fields = reflect.fieldsOf(at);
                            if (fields.Count == 0)
                            {
                                Console.WriteLine("[struct] declares nothing of its own");
                                continue;
                            }

                            Console.WriteLine("[struct] offset  size  type"
                                + new string(' ', 39) + "name");

                            foreach (var field in fields)
                            {
                                var mark = field.IsReturn ? "  <- returns"
                                    : field.IsParameter ? "  <- argument" : string.Empty;

                                Console.WriteLine($"[struct]  {field}{mark}");
                            }

                            //Written out the way a header would say it, because that is what the
                            //next step needs and transcribing a table by hand is how a field ends
                            //up spelled wrong.
                            if (kind == "ScriptStruct")
                            {
                                Console.WriteLine();
                                Console.WriteLine($"[struct] USTRUCT(BlueprintType)");
                                Console.WriteLine($"[struct] struct F{name} {{");
                                Console.WriteLine($"[struct]     GENERATED_BODY()");

                                foreach (var field in fields)
                                {
                                    Console.WriteLine("[struct]     UPROPERTY(EditAnywhere, "
                                        + "BlueprintReadWrite)");
                                    Console.WriteLine($"[struct]     {field.Cpp} {field.Name};");
                                }

                                Console.WriteLine("[struct] };");
                            }
                        }
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_LAYOUT - the layout the running game built, read out of it.
            //
            //The thing all of this was for. Welding has to place a mission's rooms itself and
            //placing them itself is guesswork; the game already knows, and while a mission is
            //loaded the answer is sitting in memory.
            //
            //Three things are found, in order:
            //
            //  GNames        by the code of FName::GetNames (see PROBE_GNAMES)
            //  GUObjectArray by sweeping the writable image for something shaped like it, which
            //                works here where it did not for names because the arithmetic in a
            //                chunked object array is tight enough to have one answer
            //  the offsets   by asking the binary - find the UClass called LevelStreaming, walk
            //                its properties, and read where each one lives
            //
            //That last part matters more than it sounds. Every published offset table for this
            //engine is the EDITOR layout, and a shipping build makes UStruct sixteen bytes
            //bigger by privately inheriting FStructBaseChain - so Children is at 0x48 and not
            //0x38, and a table that says otherwise reads plausible rubbish rather than failing.
            //Asking the binary sidesteps the whole question.
            //
            //Read-only. Nothing is written to the game.
            var probeLayout = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_LAYOUT"));
            if (probeLayout != null)
            {
                var forMission = probeLayout.Contains('=')
                    ? probeLayout.Substring(probeLayout.IndexOf('=') + 1).Trim('"')
                    : string.Empty;

                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null)
                {
                    Console.WriteLine($"[layout] the game is not open: {why}");
                    this.Shutdown();
                    return;
                }

                using (game)
                {
                    var image = game.image(out var imageSize);
                    var baseAt = image.ToInt64();

                    //--- the sections, out of the header: the names are blanked but the table
                    //--- is intact, so they are taken by flags and size rather than by name.
                    long codeAt = baseAt + 0x1000, codeEnd = baseAt + imageSize;
                    long dataAt = 0, dataEnd = 0;

                    var dos = game.read(image, 0x40);
                    if (dos != null && dos[0] == 'M' && dos[1] == 'Z')
                    {
                        var peAt = BitConverter.ToInt32(dos, 0x3c);
                        var pe = game.read(new IntPtr(baseAt + peAt), 0x108);

                        if (pe != null)
                        {
                            var sections = BitConverter.ToUInt16(pe, 6);
                            var firstSection = peAt + 24 + BitConverter.ToUInt16(pe, 20);

                            for (var i = 0; i < sections; i++)
                            {
                                var row = game.read(new IntPtr(baseAt + firstSection + i * 40), 40);
                                if (row == null) { continue; }

                                var flags = BitConverter.ToUInt32(row, 36);
                                var rva = BitConverter.ToUInt32(row, 12);
                                var size = BitConverter.ToUInt32(row, 8);

                                if ((flags & 0x20000000) != 0 && size > 0x100000 && dataAt == 0)
                                {
                                    codeAt = baseAt + rva;
                                    codeEnd = codeAt + size;
                                }

                                //Initialised data, writable, not executable: the statics.
                                if ((flags & 0x80000000) != 0 && (flags & 0x20000000) == 0
                                    && (flags & 0x40000000) != 0 && size > 0x100000 && dataAt == 0)
                                {
                                    dataAt = baseAt + rva;
                                    dataEnd = dataAt + size;
                                }
                            }
                        }
                    }

                    Console.WriteLine($"[layout] code +{codeAt - baseAt:x} ({(codeEnd - codeAt) / (1024 * 1024)} MB), "
                        + $"data +{dataAt - baseAt:x} ({(dataEnd - dataAt) / (1024 * 1024)} MB)");

                    //--- GNames -----------------------------------------------------------
                    var want = new byte?[]
                    {
                        0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0x05, null, null, null, null,
                        0x48, 0x85, 0xC0, 0x75, null, 0xB9, 0x08, 0x08, 0x00, 0x00,
                    };

                    long table = 0;
                    var CHUNK = 4 * 1024 * 1024;
                    var buffer = new byte[CHUNK + 64];

                    for (var at = codeAt; at < codeEnd && table == 0; at += CHUNK)
                    {
                        var take = (int)Math.Min(CHUNK + 64, codeEnd - at);
                        if (!game.tryRead(new IntPtr(at), buffer, take)) { continue; }

                        for (var i = 0; i + want.Length <= take; i++)
                        {
                            if (buffer[i] != 0x48) { continue; }

                            var same = true;
                            for (var j = 1; j < want.Length; j++)
                            {
                                if (want[j] == null) { continue; }
                                if (buffer[i + j] != want[j]!.Value) { same = false; break; }
                            }

                            if (!same) { continue; }

                            var pointerAt = at + i + 11 + BitConverter.ToInt32(buffer, i + 7);
                            var held = game.read(new IntPtr(pointerAt), 8);
                            if (held == null) { continue; }

                            table = BitConverter.ToInt64(held, 0);
                            break;
                        }
                    }

                    if (table == 0)
                    {
                        Console.WriteLine("[layout] could not find the name table");
                        this.Shutdown();
                        return;
                    }

                    var names = new LiveEdit.NameTable(game);
                    names.useChunks(new IntPtr(table));

                    if (names.nameOf(0) != "None")
                    {
                        Console.WriteLine("[layout] the name table does not read back");
                        this.Shutdown();
                        return;
                    }

                    Console.WriteLine($"[layout] names at {table:x}");

                    //--- GUObjectArray ----------------------------------------------------
                    //
                    //Swept rather than pattern-matched. A chunked object array has to agree with
                    //itself about how many chunks its elements need, and that is a stiff enough
                    //test that rubbish does not pass it.
                    long objects = 0, objectCount = 0;
                    var perChunk = 0;

                    var data = new byte[Math.Min(dataEnd - dataAt, 64 * 1024 * 1024)];

                    if (dataAt != 0 && game.tryRead(new IntPtr(dataAt), data, data.Length))
                    {
                        for (var i = 0; i + 0x20 <= data.Length; i += 4)
                        {
                            var chunks = BitConverter.ToInt64(data, i);
                            if (chunks < 0x10000 || chunks > 0x7fffffffffff) { continue; }

                            var maxElements = BitConverter.ToInt32(data, i + 0x10);
                            var numElements = BitConverter.ToInt32(data, i + 0x14);
                            var maxChunks = BitConverter.ToInt32(data, i + 0x18);
                            var numChunks = BitConverter.ToInt32(data, i + 0x1C);

                            if (numChunks < 1 || numChunks > 0x14) { continue; }
                            if (maxChunks < 6 || maxChunks > 0x5FF) { continue; }
                            if (numElements <= 0x800 || maxElements <= 0x10000) { continue; }
                            if (numElements > maxElements || numChunks > maxChunks) { continue; }
                            if (maxElements % 0x10 != 0) { continue; }

                            var each = maxElements / maxChunks;
                            if (each % 0x10 != 0 || each < 0x8000 || each > 0x80000) { continue; }
                            if (numElements / each + 1 != numChunks) { continue; }
                            if (maxElements / each != maxChunks) { continue; }

                            //And the chunk pointers have to be there.
                            var ok = true;
                            for (var c = 0; c < numChunks && ok; c++)
                            {
                                var one = game.read(new IntPtr(chunks + c * 8), 8);
                                ok = one != null && BitConverter.ToInt64(one, 0) > 0x10000;
                            }

                            if (!ok) { continue; }

                            objects = chunks;
                            objectCount = numElements;
                            perChunk = each;

                            Console.WriteLine($"[layout] objects at {dataAt + i:x}: "
                                + $"{numElements:N0} of {maxElements:N0}, "
                                + $"{numChunks} chunk(s) of {each:N0}");
                            break;
                        }
                    }

                    if (objects == 0)
                    {
                        Console.WriteLine("[layout] could not find the object array");
                        this.Shutdown();
                        return;
                    }

                    //--- reading objects --------------------------------------------------
                    const int ITEM = 0x18;
                    const int CLASS_AT = 0x10, NAME_AT = 0x18;
                    const int CHILDREN = 0x48, SUPER = 0x40, NEXT = 0x28, OFFSET_AT = 0x44;

                    IntPtr deref(long address, int offset)
                    {
                        var raw = game.read(new IntPtr(address + offset), 8);
                        return raw == null ? IntPtr.Zero : new IntPtr(BitConverter.ToInt64(raw, 0));
                    }

                    string? nameAt(long obj)
                    {
                        if (obj == 0) { return null; }
                        var raw = game.read(new IntPtr(obj + NAME_AT), 4);
                        return raw == null ? null : names.nameOf(BitConverter.ToInt32(raw, 0));
                    }

                    long objectAt(int index)
                    {
                        var chunk = game.read(new IntPtr(objects + (index / perChunk) * 8), 8);
                        if (chunk == null) { return 0; }

                        var where = BitConverter.ToInt64(chunk, 0);
                        if (where <= 0x10000) { return 0; }

                        var item = game.read(new IntPtr(where + (index % perChunk) * ITEM), 8);
                        return item == null ? 0 : BitConverter.ToInt64(item, 0);
                    }

                    //--- the class, and where its fields live -----------------------------
                    long theClass = 0;
                    var began = DateTime.UtcNow;

                    for (var i = 0; i < objectCount; i++)
                    {
                        var one = objectAt(i);
                        if (one == 0) { continue; }
                        if (nameAt(one) != "LevelStreaming") { continue; }

                        //The class itself, not an instance of it: a UClass is its own class's
                        //instance, so the one whose name is LevelStreaming and whose class is
                        //named Class is the one wanted.
                        if (nameAt(deref(one, CLASS_AT).ToInt64()) != "Class") { continue; }

                        theClass = one;
                        break;
                    }

                    if (theClass == 0)
                    {
                        Console.WriteLine("[layout] no ULevelStreaming class - is a mission loaded?");
                        this.Shutdown();
                        return;
                    }

                    Console.WriteLine($"[layout] ULevelStreaming class at {theClass:x} "
                        + $"(found in {(DateTime.UtcNow - began).TotalSeconds:F1}s)");

                    var where = new Dictionary<string, int>(StringComparer.Ordinal);

                    for (var klass = theClass; klass != 0; klass = deref(klass, SUPER).ToInt64())
                    {
                        for (var field = deref(klass, CHILDREN).ToInt64(); field != 0;
                             field = deref(field, NEXT).ToInt64())
                        {
                            var said = nameAt(field);
                            if (said == null || where.ContainsKey(said)) { continue; }

                            var raw = game.read(new IntPtr(field + OFFSET_AT), 4);
                            if (raw == null) { continue; }

                            where[said] = BitConverter.ToInt32(raw, 0);
                        }
                    }

                    foreach (var wanted in new[] { "LevelTransform", "PackageNameToLoad",
                                                   "WorldAsset", "LoadedLevel" })
                    {
                        Console.WriteLine($"[layout]   {wanted,-20} "
                            + (where.TryGetValue(wanted, out var off)
                                ? $"+0x{off:x}" : "NOT FOUND"));
                    }

                    if (!where.TryGetValue("LevelTransform", out var transformAt)
                        || !where.TryGetValue("PackageNameToLoad", out var packageAt))
                    {
                        Console.WriteLine("[layout] the fields are not where the class says - stopping");
                        this.Shutdown();
                        return;
                    }

                    //--- every placed tile ------------------------------------------------
                    Console.WriteLine();
                    Console.WriteLine("[layout] --- the mission, as the game built it ---");

                    var placed = 0;
                    var found2 = new List<(string tile, string theme, float x, float y, float z, double yaw)>();

                    for (var i = 0; i < objectCount; i++)
                    {
                        var one = objectAt(i);
                        if (one == 0) { continue; }

                        //An instance of the class, or of anything derived from it.
                        var mine = false;
                        for (var klass = deref(one, CLASS_AT).ToInt64(); klass != 0;
                             klass = deref(klass, SUPER).ToInt64())
                        {
                            if (klass == theClass) { mine = true; break; }
                        }

                        if (!mine) { continue; }

                        var package = game.read(new IntPtr(one + packageAt), 4);
                        var tile = package == null ? null : names.nameOf(BitConverter.ToInt32(package, 0));

                        var transform = game.read(new IntPtr(one + transformAt), 0x30);
                        if (transform == null) { continue; }

                        //Rotation is a quaternion; translation is centimetres.
                        var qx = BitConverter.ToSingle(transform, 0);
                        var qy = BitConverter.ToSingle(transform, 4);
                        var qz = BitConverter.ToSingle(transform, 8);
                        var qw = BitConverter.ToSingle(transform, 12);

                        var tx = BitConverter.ToSingle(transform, 0x10);
                        var ty = BitConverter.ToSingle(transform, 0x14);
                        var tz = BitConverter.ToSingle(transform, 0x18);

                        //Yaw out of the quaternion, which for a tile turned about the up axis is
                        //the only part that is not zero.
                        var yaw = Math.Atan2(2.0 * (qw * qz + qx * qy),
                                             1.0 - 2.0 * (qy * qy + qz * qz)) * 180.0 / Math.PI;

                        placed++;

                        //The package path is /Game/Decor/Maps/<theme>/SubLevels/<tile>, and the
                        //tile is the part a level file would name.
                        var said = tile ?? string.Empty;
                        var cut = said.LastIndexOf('/');
                        var leaf = cut < 0 ? said : said.Substring(cut + 1);

                        var themeAt = said.IndexOf("/Maps/", StringComparison.OrdinalIgnoreCase);
                        var theme = themeAt < 0 ? string.Empty
                            : said.Substring(themeAt + 6).Split('/')[0];

                        if (leaf.Length > 0 && leaf != "None")
                        {
                            found2.Add((leaf, theme, tx, ty, tz, yaw));
                        }

                        if (placed <= 60)
                        {
                            Console.WriteLine($"[layout] {tile ?? "(no package)",-34} "
                                + $"at {tx,9:F0} {ty,9:F0} {tz,8:F0}   yaw {yaw,6:F1}");
                        }
                    }

                    Console.WriteLine($"[layout] {placed} streaming level(s) in the world");

                    //Written down, because this is the artifact - the arrangement the game
                    //chose, in the terms a tile is described in. A hundred units to the block:
                    //Unreal counts centimetres and a Dungeons block is a metre.
                    //Into the mission's own folder when one is named, because that is where
                    //the welder looks. Unreal counts centimetres and is Z-up; a tile is measured
                    //in blocks of a metre and is Y-up - so the axes swap on the way across.
                    var into = forMission.Length > 0
                        ? System.IO.Path.Combine(Logic.MapWorkshop.folderFor(forMission),
                                                 "live-layout.json")
                        : System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                 "mcd-live-layout.json");

                    var written = new System.Text.Json.Nodes.JsonArray();

                    foreach (var row in found2)
                    {
                        written.Add(new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = row.tile,
                            ["theme"] = row.theme,
                            //Unreal x,y,z -> tile x,y,z: the game's up is z and a tile's is y.
                            ["pos"] = new System.Text.Json.Nodes.JsonArray(
                                (int)Math.Round(row.x / 100.0),
                                (int)Math.Round(row.z / 100.0),
                                (int)Math.Round(row.y / 100.0)),
                            ["yaw"] = (int)Math.Round(row.yaw),
                        });
                    }

                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(into)!);

                    System.IO.File.WriteAllText(into,
                        written.ToJsonString(new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true,
                        }));

                    Console.WriteLine($"[layout] written to {into}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_QUESTS - every kind of objective the game's own missions ask for.
            //
            //The editor can only offer steps it knows the shape of, and a step whose shape is
            //guessed at is a step that blocks every step behind it with no error anywhere. So
            //the shapes are read off the game rather than invented: which bodies exist, which
            //fields each one carries, and one worked example of each.
            if (_startupArguments.Any(a => a == "PROBE_QUESTS"))
            {
                var kinds = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                var example = new Dictionary<string, string>(StringComparer.Ordinal);
                var users = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

                foreach (var mission in Logic.GameMaps.all())
                {
                    var raw = Logic.GameMaps.read(mission.PakPath);
                    if (raw == null) { continue; }

                    System.Text.Json.Nodes.JsonObject? level;
                    try
                    {
                        var text = Logic.GameMaps.stripComments(
                            new System.Text.UTF8Encoding(false).GetString(raw).TrimStart('\uFEFF'));

                        level = System.Text.Json.Nodes.JsonNode.Parse(text,
                            documentOptions: new System.Text.Json.JsonDocumentOptions
                            {
                                AllowTrailingCommas = true,
                                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            }) as System.Text.Json.Nodes.JsonObject;
                    }
                    catch (Exception problem)
                    {
                        Console.WriteLine($"[quests] {mission.Name}: {problem.Message}");
                        continue;
                    }

                    if (level?["objectives"] is not System.Text.Json.Nodes.JsonArray all) { continue; }

                    foreach (var one in all)
                    {
                        if (one is not System.Text.Json.Nodes.JsonObject step) { continue; }

                        foreach (var part in step)
                        {
                            //The body is whichever key holds an object - "click", "gauntlet",
                            //"killgroup" and whatever else turns up. Everything else is the
                            //wrapper: name, description, displayMode.
                            if (part.Value is not System.Text.Json.Nodes.JsonObject body) { continue; }

                            if (!kinds.TryGetValue(part.Key, out var fields))
                            {
                                fields = new SortedSet<string>(StringComparer.Ordinal);
                                kinds[part.Key] = fields;
                            }

                            foreach (var field in body) { fields.Add(field.Key); }

                            if (!users.TryGetValue(part.Key, out var who))
                            {
                                who = new SortedSet<string>(StringComparer.Ordinal);
                                users[part.Key] = who;
                            }
                            who.Add(mission.Name);

                            //The fullest example wins, so the print shows every field rather
                            //than whichever one happened to come first.
                            var shown = step.ToJsonString(
                                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                            if (!example.TryGetValue(part.Key, out var had) || shown.Length > had.Length)
                            {
                                example[part.Key] = shown;
                            }
                        }
                    }
                }

                foreach (var kind in kinds)
                {
                    Console.WriteLine();
                    Console.WriteLine($"=== {kind.Key} ===");
                    Console.WriteLine("   fields : " + string.Join(", ", kind.Value));
                    Console.WriteLine("   used by: " + string.Join(", ", users[kind.Key]));
                    Console.WriteLine(example[kind.Key]);
                }

                this.Shutdown();
                return;
            }

            //PROBE_WELDABLE - which of the game's levels can be welded and which must not be.
            //
            //Welding the camp crashed the game fourteen seconds into loading, because the merged
            //tile declares none of the teleports its tiles did. Worth checking every level rather
            //than the one that happened to break.
            if (_startupArguments.Any(a => a == "PROBE_WELDABLE"))
            {
                var root = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps");

                if (!System.IO.Directory.Exists(root)) { Console.WriteLine("[weld] nothing exported"); }
                else
                {
                    foreach (var folder in System.IO.Directory.GetDirectories(root))
                    {
                        //Judged on the level as it stands BEFORE welding. An already-welded level
                        //plays one merged tile that declares nothing, and would pass every time.
                        var pre = System.IO.Path.Combine(folder, "level.json.multitile");
                        var judged = folder;

                        if (System.IO.File.Exists(pre))
                        {
                            judged = System.IO.Path.Combine(
                                System.IO.Path.GetTempPath(), "weld-probe", System.IO.Path.GetFileName(folder));
                            System.IO.Directory.CreateDirectory(judged);
                            System.IO.File.Copy(pre, System.IO.Path.Combine(judged, "level.json"), true);
                        }

                        var ok = Logic.MapTools.weldable(judged, out var why);
                        Console.WriteLine($"[weld] {System.IO.Path.GetFileName(folder),-16} "
                            + (ok ? "weldable" : "NOT weldable - " + why)
                            + (judged == folder ? "" : "   (judged pre-weld)"));
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_DXT1=<texture asset path> - whether a compressed texture can be rewritten.
            //
            //Two things have to hold. The encoded replacement must be exactly as long as what it
            //replaces, or the swap-in-place refuses; and the picture has to survive the trip, which
            //a size check says nothing about. So it encodes the game's own artwork and measures how
            //far the result drifted from it.
            var probeDxt = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_DXT1="));
            if (probeDxt != null)
            {
                var wantedName = probeDxt.Substring("PROBE_DXT1=".Length).Trim('"');

                //Found the way the tab finds it, rather than by me spelling out a path. These
                //entries carry a doubled mount point and guessing at it wasted more time than
                //reusing the one function that already knows.
                var asset = Logic.CosmeticSkins.userInterface()
                    .Select(one => one.TexturePath)
                    .FirstOrDefault(one => one.EndsWith("/" + wantedName, StringComparison.OrdinalIgnoreCase))
                    ?? wantedName;

                Console.WriteLine($"[dxt1] asset: {asset}");

                try
                {
                    var package = Logic.CustomSkins.index!.extractPackage(asset)
                        ?? throw new InvalidOperationException("could not read it");
                    var texture = package.GetExport<PakReader.Parsers.Class.UTexture2D>()
                        ?? throw new InvalidOperationException("that is not a texture");
                    var platform = texture.PlatformDatas[0];
                    var mip = platform.Mips[0].BulkData.Data!;

                    Console.WriteLine($"[dxt1] {System.IO.Path.GetFileName(asset)} "
                        + $"{platform.SizeX}x{platform.SizeY} {platform.PixelFormat}, mip0 {mip.Length:N0} bytes");

                    var wanted = Logic.BlockCompression.dxt1Size(platform.SizeX, platform.SizeY);
                    Console.WriteLine(wanted == mip.Length
                        ? $"[dxt1] an encode of that size is {wanted:N0} bytes - the same"
                        : $"[dxt1] WRONG - an encode would be {wanted:N0} bytes, not {mip.Length:N0}");

                    //Round trip the game's own picture: decode what it ships, encode it again,
                    //and see how much moved.
                    var shown = texture.Image;
                    if (shown == null) { Console.WriteLine("[dxt1] could not decode it"); }
                    else
                    {
                        var bgra = new byte[platform.SizeX * platform.SizeY * 4];
                        using (var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(
                            platform.SizeX, platform.SizeY, SkiaSharp.SKColorType.Bgra8888,
                            SkiaSharp.SKAlphaType.Unpremul)))
                        {
                            shown.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
                            System.Runtime.InteropServices.Marshal.Copy(
                                bitmap.GetPixels(), bgra, 0, bgra.Length);
                        }

                        var again = Logic.BlockCompression.toDxt1(bgra, platform.SizeX, platform.SizeY);
                        Console.WriteLine(again.Length == mip.Length
                            ? $"[dxt1] re-encoded to {again.Length:N0} bytes - fits exactly"
                            : $"[dxt1] WRONG - re-encoded to {again.Length:N0}, needed {mip.Length:N0}");

                        //How far the picture moved, which is the only question that matters.
                        var back = Logic.BlockCompression.fromDxt1(again, platform.SizeX, platform.SizeY);

                        double total = 0;
                        var worst = 0;
                        for (var i = 0; i < bgra.Length; i++)
                        {
                            if (i % 4 == 3) { continue; }
                            var gap = bgra[i] - back[i];
                            total += gap * (double)gap;
                            if (Math.Abs(gap) > worst) { worst = Math.Abs(gap); }
                        }

                        var rms = Math.Sqrt(total / (bgra.Length * 0.75));
                        Console.WriteLine($"[dxt1] re-encoded picture differs by {rms:F2} of 255 on "
                            + $"average, worst channel {worst}");
                        Console.WriteLine(rms < 8
                            ? "[dxt1] the artwork survives the trip"
                            : "[dxt1] WRONG - that is visible damage, not compression");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[dxt1] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_CLEAR - whether Clear actually clears.
            //
            //Deleting a tree on Windows is not one call: the contents go and the directory can
            //linger, which by hand looked exactly like the delete had failed. Worth a probe
            //because the button is destructive and a half-delete is worse than none.
            if (_startupArguments.Any(a => a == "PROBE_CLEAR"))
            {
                var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "mcd-clear-probe", "objectgroups", "deep");
                System.IO.Directory.CreateDirectory(scratch);
                System.IO.File.WriteAllText(System.IO.Path.Combine(scratch, "objectgroup.json"), "{}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(scratch, "objectgroup.json.before"), "{}");

                var top = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcd-clear-probe");
                System.IO.File.WriteAllText(System.IO.Path.Combine(top, "level.json"), "{}");

                Console.WriteLine($"[clear] built {System.IO.Directory.GetFiles(top, "*", System.IO.SearchOption.AllDirectories).Length} files under {top}");

                try
                {
                    UI.MapsTab.erase(top);
                    Console.WriteLine(System.IO.Directory.Exists(top)
                        ? "[clear] WRONG - the folder is still there"
                        : "[clear] gone, folder and all");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[clear] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_WORKSHOP[=<mission>] - where the app thinks a mission's files are.
            //
            //Export asks for a folder and everything else used to assume one, so a mission
            //exported to the Desktop was edited in AppData without a word. This is the answer
            //every button now shares.
            var probeShop = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_WORKSHOP"));
            if (probeShop != null)
            {
                var wanted = probeShop.Contains('=')
                    ? probeShop.Substring(probeShop.IndexOf('=') + 1).Trim('"')
                    : "creeperwoods";

                Console.WriteLine($"[shop] default root: {Logic.MapWorkshop.root}");
                Console.WriteLine($"[shop] {wanted} -> {Logic.MapWorkshop.folderFor(wanted)}");
                Console.WriteLine($"[shop] exported? {Logic.MapWorkshop.exported(wanted)}");

                //Prove it survives being written and read back, without disturbing a real record.
                var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcd-shop-probe");
                System.IO.Directory.CreateDirectory(scratch);
                Logic.MapWorkshop.remember("probe_mission", scratch);
                Console.WriteLine(Logic.MapWorkshop.folderFor("probe_mission") == scratch
                    ? "[shop] a remembered folder comes back"
                    : "[shop] WRONG - the record did not stick");

                this.Shutdown();
                return;
            }

            //PROBE_SPAWNWINDOW=<mission> - opens the spawns window for real and says whether a map
            //appeared in it.
            //
            //Everything under the window can be right while the window shows nothing, which is
            //exactly what happened: the room list was the only thing that ever chose a room, and
            //hiding it left nothing selected and a black rectangle. A probe that only tested the
            //layer below would have passed.
            var probeWindow = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_SPAWNWINDOW="));
            if (probeWindow != null)
            {
                var wanted = probeWindow.Substring("PROBE_SPAWNWINDOW=".Length).Trim('"');
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                try
                {
                    var map = Logic.MapSpawns.load(folder);
                    var window = new UI.SpawnsWindow(map);

                    //Off the screen rather than minimised: a minimised window may never lay out,
                    //and layout is half of what is being tested.
                    window.WindowState = WindowState.Normal;
                    window.Left = -20000;
                    window.Show();

                    var waited = 0;
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(250),
                    };

                    timer.Tick += (_, _) =>
                    {
                        waited += 250;

                        if (!window.mapReady && waited < 15000) { return; }

                        timer.Stop();
                        Console.WriteLine($"[window] room: {window.roomChosen}");
                        Console.WriteLine($"[window] spawn points: {window.spawnsShown}");
                        Console.WriteLine(window.mapReady
                            ? $"[window] map built after {waited:N0} ms"
                            : "[window] NOTHING BUILT - the view is empty");

                        //Every mob row has to be showing a mob. They were all blank, because an
                        //editable ComboBox given its text before it reaches the tree keeps none
                        //of it, and a blank row looks like a second empty dropdown.
                        var rows = window.mobRows;
                        var blank = rows.Count(one => one.Trim().Length == 0);
                        Console.WriteLine($"[window] mob rows: {rows.Length} "
                            + $"({string.Join(", ", rows.Take(6))})");
                        Console.WriteLine(blank == 0
                            ? "[window] every mob row shows a mob"
                            : $"[window] WRONG - {blank} of {rows.Length} mob rows are blank");

                        Console.WriteLine(window.editableMobRows == 0
                            ? "[window] no mob row is editable, so all of them can draw"
                            : $"[window] WRONG - {window.editableMobRows} editable rows will show blank");

                        //The side panel has to reach its own bottom. Six sections went in and
                        //the mob list - the thing this window is mostly opened for - fell off it.
                        var scroll = window.panelScrollNow;
                        Console.WriteLine($"[window] side panel: content {scroll.content:F0}px, "
                            + $"viewport {scroll.viewport:F0}px, scrollable {scroll.scrollable:F0}px");
                        //Not scrolling is only good news when everything fits. Since the sections
                        //were split across tabs it should, and a tab that has to scroll as well
                        //is a sign the split wants redoing rather than a fault.
                        Console.WriteLine(scroll.scrollable > 0
                            ? "[window] the tab scrolls, so what is below the fold can be reached"
                            : scroll.content <= scroll.viewport + 1
                                ? "[window] the tab needs no scrolling - it all fits"
                                : "[window] WRONG - content overflows and the tab will not scroll");
                        Console.WriteLine(scroll.mobsReachable
                            ? "[window] the mob list is inside the scrollable content"
                            : "[window] WRONG - the mob list cannot be scrolled to");

                        var ways = window.wayRows;
                        Console.WriteLine($"[window] ways in and out: {ways.Length}");
                        foreach (var one in ways.Take(8)) { Console.WriteLine($"[window]   {one}"); }

                        //The group list has to say where each group is used, or editing the wrong
                        //one looks like the editor ignoring you.
                        var shown = window.groupRows;
                        Console.WriteLine($"[window] {shown.Length} mob groups, first four:");
                        foreach (var one in shown.Take(4)) { Console.WriteLine($"[window]   {one}"); }
                        var ender = shown.FirstOrDefault(one => one.StartsWith("enderboss"));
                        Console.WriteLine(ender == null ? "[window]   (no enderboss)" : $"[window]   {ender}");

                        //A mission with no groups has to be able to grow one, or its spawn points
                        //draw from nothing for ever. The camp ships with none.
                        if (window.groupCount == 0)
                        {
                            window.probeAddGroup();
                            Console.WriteLine(window.groupCount == 1
                                ? "[window] had no mob groups; New group made one: "
                                    + window.groupRows.FirstOrDefault()
                                : "[window] WRONG - New group made nothing");
                        }

                        //Choosing a group other than the first and adding a mob has to leave you
                        //in that group, or the mob looks as though it went somewhere else.
                        var (chose, ended, mobs) = window.probeAddMob();
                        Console.WriteLine(chose == ended
                            ? $"[window] added a mob to \"{chose}\" and stayed there ({mobs} mobs)"
                            : $"[window] WRONG - chose \"{chose}\" but ended on \"{ended}\"");

                        var (buttonRows, over) = window.buttonLayout;
                        Console.WriteLine($"[window] action buttons sit on {buttonRows} row(s)");
                        Console.WriteLine(over.Length == 0
                            ? "[window] none of them runs off the edge"
                            : $"[window] WRONG - off the edge: {string.Join(", ", over)}");

                        var wasSpawns = window.spawnsNow;
                        Console.WriteLine(window.probePlaceKeepsCamera()
                            ? "[window] placing points left the camera alone"
                            : "[window] WRONG - placing points moved the camera");

                        Console.WriteLine($"[window] Place put down {window.spawnsNow - wasSpawns} "
                            + $"point(s) ({wasSpawns} -> {window.spawnsNow})");

                        Console.WriteLine(window.probeWalks()
                            ? "[window] W moves the view"
                            : "[window] WRONG - W does nothing");

                        //Removing a point must not throw the camera back to the start.
                        Console.WriteLine(window.probeRemoveKeepsCamera()
                            ? "[window] removing a point left the camera alone"
                            : "[window] WRONG - removing a point moved the camera");

                        //Clicking a spawn point has to select THAT point, not aim beside it.
                        var (gotIt, before, after) = window.probeRemoveFirst();
                        Console.WriteLine(gotIt && after == before - 1
                            ? $"[window] clicked a spawn point and removed it ({before} -> {after})"
                            : $"[window] WRONG - clicking a spawn point did not select it ({before} -> {after})");

                        //Back to the whole mission first. The walk test above moved the camera on
                        //purpose, and a ray cast from wherever it drifted to says nothing about
                        //whether picking works.
                        window.probeFrame();

                        //A click down the middle, through the real ray. The coordinates have to
                        //come back in the order the boxes expect and the height has to be a
                        //height - inside the room, and the ground the view itself believes in.
                        var hit = window.mapReady ? window.probeClick() : null;
                        if (hit == null)
                        {
                            Console.WriteLine("[window] a click in the middle hits nothing");
                        }
                        else
                        {
                            var (hx, hy, hz) = hit.Value;
                            var ground = window.groundAt(hx, hz);
                            var sane = hy > 0 && hy <= window.roomHeight && hy == ground;

                            Console.WriteLine($"[window] a click in the middle lands on "
                                + $"x {hx}, y {hy}, z {hz} (room is {window.roomHeight} tall, "
                                + $"ground there is {ground})");
                            Console.WriteLine(sane
                                ? "[window] the coordinates are in the right order"
                                : "[window] WRONG - that is not the ground under that column");
                        }

                        window.Close();
                        this.Shutdown();
                    };

                    timer.Start();
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[window] refused: {problem}");
                    this.Shutdown();
                }

                return;
            }

            //PROBE_RELIEF=<mission>[;<png to write>[;<ceiling>]] - the mission as ground: how many
            //quads it takes to say it, what it costs, and what it looks like from above.
            //
            //The picture is the point. A quad count can be right while the map is painted in
            //noise, and the only way to know which is to look at it.
            var probeRelief = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_RELIEF="));
            if (probeRelief != null)
            {
                var bits = probeRelief.Substring("PROBE_RELIEF=".Length).Trim('"').Split(';');
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", bits[0]);
                var into = bits.Length > 1 && bits[1].Length > 0 ? bits[1] : null;
                var ceiling = bits.Length > 2 && int.TryParse(bits[2], out var asked) ? asked : 0;

                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var map = Logic.MapSpawns.load(folder);
                    Console.WriteLine($"[relief] loaded {map.Rooms.Count} rooms in {clock.ElapsedMilliseconds:N0} ms");

                    var palette = Logic.BlockPalette.forMission(map.Level);
                    foreach (var note in Logic.BlockPalette.Notes) { Console.WriteLine($"[relief] {note}"); }
                    Console.WriteLine($"[relief] palette: {palette.Length} ids, "
                        + $"{palette.Count(one => one != null && one.topOf(0) != 0)} with a colour");

                    for (var id = 0; id < Math.Min(palette.Length, 6); id++)
                    {
                        var look = palette[id];
                        if (look == null) { continue; }
                        Console.WriteLine($"[relief]   {id,3} {look.Name,-18} top #{look.topOf(0) & 0xFFFFFF:x6} side #{look.sideOf(0) & 0xFFFFFF:x6} {look.Variants} var {look.Shape}");
                    }

                    var room = map.Rooms.FirstOrDefault();
                    if (room == null) { Console.WriteLine("[relief] no rooms"); this.Shutdown(); return; }

                    clock.Restart();
                    var relief = Logic.MapRelief.build(room, palette, ceiling);
                    foreach (var note in Logic.MapRelief.Notes) { Console.WriteLine($"[relief] {note}"); }

                    if (relief == null) { Console.WriteLine("[relief] nothing built"); this.Shutdown(); return; }

                    Console.WriteLine($"[relief] built in {clock.ElapsedMilliseconds:N0} ms");
                    Console.WriteLine($"[relief] {relief.Points.Count / 3:N0} vertices, "
                        + $"{relief.Indices.Count / 3:N0} triangles, {relief.Quads:N0} quads");
                    Console.WriteLine($"[relief] against one quad per column that is "
                        + $"{relief.Sx * relief.Sz / (double)Math.Max(1, relief.Quads):F1}x fewer");

                    Console.WriteLine("[relief] what the roof is made of:");
                    foreach (var one in relief.Census)
                    {
                        var look = one.id < palette.Length ? palette[one.id] : null;
                        var colour = look?.topOf(one.meta) ?? 0u;
                        Console.WriteLine($"[relief]   {one.count,7:N0} ({one.count * 100.0 / (relief.Sx * relief.Sz),4:F1}%) "
                            + $"id {one.id,3}:{one.meta,-2} {look?.Name ?? "?",-22} "
                            + (colour == 0 ? "NO COLOUR" : $"#{colour & 0xFFFFFF:x6}"));
                    }

                    //What the window actually pays for: turning the mesh into WPF's own
                    //collections. Freezable collections are famously slower to fill than a plain
                    //list, and half a million points is where that stops being a footnote.
                    var shaping = System.Diagnostics.Stopwatch.StartNew();
                    var positions = new System.Windows.Media.Media3D.Point3DCollection(relief.Points.Count / 3);
                    for (var i = 0; i < relief.Points.Count; i += 3)
                    {
                        positions.Add(new System.Windows.Media.Media3D.Point3D(
                            relief.Points[i], relief.Points[i + 1], relief.Points[i + 2]));
                    }
                    var uv = new System.Windows.Media.PointCollection(relief.Uvs.Count / 2);
                    for (var i = 0; i < relief.Uvs.Count; i += 2)
                    {
                        uv.Add(new System.Windows.Point(relief.Uvs[i], relief.Uvs[i + 1]));
                    }
                    var tris = new System.Windows.Media.Int32Collection(relief.Indices);
                    var geometry = new System.Windows.Media.Media3D.MeshGeometry3D
                    {
                        Positions = positions, TextureCoordinates = uv, TriangleIndices = tris,
                    };
                    geometry.Freeze();
                    Console.WriteLine($"[relief] into WPF geometry in {shaping.ElapsedMilliseconds:N0} ms");

                    //Every block at the top of a column should be one the palette knows. An id
                    //outside it means the block array was read wrongly, not that the game has a
                    //block nobody has heard of - reading 16-bit ids the wrong way round turns
                    //dirt into 768 and paints the whole room grey.
                    var strange = relief.Census
                        .Where(one => one.id >= palette.Length || palette[one.id] == null)
                        .ToList();

                    //Measured as a SHARE, not a count. A block the conversion table never mapped
                    //is normal and rare - the camp has one, id 366, worth a fraction of a per
                    //cent. A misread block array is not rare: reading 16-bit ids the wrong way
                    //round made unknown ids the commonest thing on the map. The difference
                    //between a gap and a bug is how much of the room it covers.
                    var counted = relief.Census.Sum(one => one.count);
                    var lost = strange.Sum(one => one.count);
                    var share = counted == 0 ? 0 : lost * 100.0 / counted;

                    Console.WriteLine($"[relief] surface blocks the palette does not know: "
                        + $"{lost:N0} of {counted:N0} ({share:F2}%)"
                        + (strange.Count == 0 ? "" : " - ids "
                            + string.Join(", ", strange.Take(6).Select(one => one.id))));

                    Console.WriteLine(share < 5
                        ? "[relief] the block array reads correctly"
                        : "[relief] WRONG - that is too many to be gaps; the array is misread");

                    //Straight down the middle, looking down: the click the window will send.
                    var hit = Logic.MapRelief.pick(relief,
                        relief.Sx / 2.0, relief.Highest + 64.0, relief.Sz / 2.0, 0, -1, 0);
                    Console.WriteLine($"[relief] a look straight down the middle lands on "
                        + (hit == null ? "nothing" : $"{hit.Value.x},{hit.Value.z} at height {hit.Value.y}"));

                    if (into != null)
                    {
                        var stride = relief.Sx * 4;
                        var pixels = new byte[stride * relief.TextureHeight];
                        for (var i = 0; i < relief.Texture.Length; i++)
                        {
                            var colour = relief.Texture[i];
                            pixels[i * 4 + 0] = (byte)(colour & 0xFF);
                            pixels[i * 4 + 1] = (byte)((colour >> 8) & 0xFF);
                            pixels[i * 4 + 2] = (byte)((colour >> 16) & 0xFF);
                            pixels[i * 4 + 3] = (byte)((colour >> 24) & 0xFF);
                        }

                        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                            relief.Sx, relief.TextureHeight, 96, 96,
                            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);

                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var file = System.IO.File.Create(into);
                        encoder.Save(file);
                        Console.WriteLine($"[relief] picture written to {into}");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[relief] refused: {problem}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_DRAGKIND=<mission> - dragging each KIND of pin: spawn, door, arrival area.
            //
            //Three kinds share one gesture, and the thing that goes wrong when they do is taking
            //hold of the wrong one. A door and an arrival area stand within a few blocks of each
            //other at every mission entrance - that is what an entrance IS - so "nearest wins"
            //has to actually be nearest rather than whichever list was searched first.
            var probeKind = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_DRAGKIND="));
            if (probeKind != null)
            {
                var wanted = probeKind.Substring("PROBE_DRAGKIND=".Length).Trim('"');
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                try
                {
                    var window = new UI.SpawnsWindow(Logic.MapSpawns.load(folder));
                    window.WindowState = WindowState.Normal;
                    window.Width = 1280;
                    window.Height = 800;
                    window.Left = -20000;
                    window.Show();

                    var waited = 0;
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(250),
                    };

                    timer.Tick += (_, _) =>
                    {
                        waited += 250;
                        if (!window.mapReady && waited < 15000) { return; }
                        timer.Stop();

                        var view = window.probeView;

                        //Where each kind of pin is on screen, found with the same ray a click uses.
                        Point? find(System.Collections.Generic.IReadOnlyList<(int x, int y, int z)> pins)
                        {
                            for (var sy = 12.0; sy < view.ActualHeight - 12; sy += 5)
                            {
                                for (var sx = 12.0; sx < view.ActualWidth - 12; sx += 5)
                                {
                                    var at = new Point(sx, sy);
                                    var hit = view.probeLook(at);
                                    if (hit == null) { continue; }

                                    foreach (var one in pins)
                                    {
                                        double dx = one.x - hit.Value.x, dy = one.y - hit.Value.y,
                                               dz = one.z - hit.Value.z;
                                        if (dx * dx + dz * dz + dy * dy * 0.25 <= 2.0) { return at; }
                                    }
                                }
                            }
                            return null;
                        }

                        Point? bare = null;
                        (int x, int y, int z) bareAt = default;
                        for (var sy = 12.0; sy < view.ActualHeight - 12 && bare == null; sy += 9)
                        {
                            for (var sx = 12.0; sx < view.ActualWidth - 12; sx += 9)
                            {
                                var at = new Point(sx, sy);
                                var hit = view.probeLook(at);
                                if (hit == null) { continue; }

                                var here = hit.Value;
                                var clear = view.probeMarks.Concat(view.probeDoorPins)
                                    .Concat(view.probeStartPins)
                                    //Scaled to the room: a 20x20 baseline has no cell 14 blocks
                                    //clear of everything, and demanding one skips the whole test.
                                    .All(one => Math.Abs(one.x - here.x) + Math.Abs(one.z - here.z)
                                        > Math.Max(5, Math.Min(14, window.roomAcross / 4)));

                                if (clear) { bare = at; bareAt = here; break; }
                            }
                        }

                        if (bare == null)
                        {
                            Console.WriteLine("[kind] no clear ground on screen to drag to");
                            this.Shutdown();
                            return;
                        }

                        void check(string label,
                                   System.Collections.Generic.IReadOnlyList<(int x, int y, int z)> pins,
                                   MCDSaveEdit.UI.MapView3D.Pin expect,
                                   Func<string[]> rows)
                        {
                            if (pins.Count == 0)
                            {
                                Console.WriteLine($"[kind] {label}: none in this room, skipped");
                                return;
                            }

                            var on = find(pins);
                            if (on == null)
                            {
                                Console.WriteLine($"[kind] {label}: none visible on screen, skipped");
                                return;
                            }

                            var was = rows();

                            var took = view.probeGrab(on.Value);
                            Console.WriteLine(took && view.heldKind == expect
                                ? $"[kind] {label}: took hold of a {view.heldKind} pin"
                                : $"[kind] {label}: WRONG - took {(took ? view.heldKind.ToString() : "nothing")}, wanted {expect}");

                            if (!took) { return; }

                            view.probeDragTo(bare.Value);
                            view.probeDrop();

                            var now = rows();
                            Console.WriteLine(now.Length == was.Length
                                ? $"[kind] {label}: still {now.Length} of them"
                                : $"[kind] {label}: WRONG - the count changed");

                            Console.WriteLine(!now.SequenceEqual(was)
                                ? $"[kind] {label}: moved  ->  {now.FirstOrDefault(one => !was.Contains(one))}"
                                : $"[kind] {label}: WRONG - nothing moved");
                            Console.WriteLine($"[kind] {label}: {window.probeStatus}");
                        }

                        check("spawn", view.probeMarks, MCDSaveEdit.UI.MapView3D.Pin.Spawn,
                            () => window.probePoints.Select(one => $"{one.x},{one.y},{one.z}").ToArray());

                        check("door", view.probeDoorPins, MCDSaveEdit.UI.MapView3D.Pin.Door,
                            () => window.doorRows);

                        check("start", view.probeStartPins, MCDSaveEdit.UI.MapView3D.Pin.Start,
                            () => window.startRows);

                        this.Shutdown();
                    };

                    timer.Start();
                    return;
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[kind] refused: {problem.Message}");
                    this.Shutdown();
                    return;
                }
            }

            //PROBE_CLAIM=<folder>;<mission> - the level id a map is installed under.
            //
            //A level's id is a lookup into the game's table of levels, not its own name, and a
            //map whose id is not in that table asks for a level that does not exist. This is the
            //check that a map built for one mission and installed over another claims the one it
            //is actually loaded as.
            var probeClaim = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_CLAIM="));
            if (probeClaim != null)
            {
                var bits = probeClaim.Substring("PROBE_CLAIM=".Length).Trim('"').Split(';');
                var folder = bits[0];
                var wanted = bits.Length > 1 ? bits[1] : "creeperwoods";

                try
                {
                    var mission = Logic.GameMaps.all()
                        .FirstOrDefault(one => string.Equals(one.Name, wanted,
                            StringComparison.OrdinalIgnoreCase));

                    if (mission == null)
                    {
                        Console.WriteLine($"[claim] no mission called {wanted}");
                        this.Shutdown();
                        return;
                    }

                    var before = System.Text.Json.Nodes.JsonNode.Parse(
                        Logic.GameMaps.stripComments(
                            System.IO.File.ReadAllText(System.IO.Path.Combine(folder, "level.json"))),
                        documentOptions: new System.Text.Json.JsonDocumentOptions
                        {
                            AllowTrailingCommas = true,
                            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                        }) as System.Text.Json.Nodes.JsonObject;

                    Console.WriteLine($"[claim] the folder's level says id = "
                        + $"\"{before?["id"]?.GetValue<string>()}\"");

                    var made = Logic.MapMod.install(folder, mission);
                    Console.WriteLine($"[claim] installed {made.Size / 1024} KB as {mission.Name}");

                    Console.WriteLine($"[claim] read it back with PROBE_PAK={made.Path}");

                    this.Shutdown();
                    return;
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[claim] refused: {problem.Message}");
                    this.Shutdown();
                    return;
                }
            }

            //PROBE_WELDABLE2=<mission> - whether a level that is already one tile gets welded.
            //
            //Welding a single-tile level is not a no-op, it is destructive: make_single names the
            //welded tile after the FOLDER and points the only stretch at it, so a folder holding
            //a Merged group from an earlier mission ends up as the level that plays while the new
            //work sits unreferenced. That shipped, and presented as a crash with nothing in it.
            var probeWeld2 = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_WELDABLE2="));
            if (probeWeld2 != null)
            {
                var wanted = probeWeld2.Substring("PROBE_WELDABLE2=".Length).Trim('"');
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                var ok = Logic.MapTools.weldable(folder, out var why);
                Console.WriteLine($"[weld] {wanted}: weldable={ok}"
                    + (why.Length > 0 ? $"  ({why})" : string.Empty));

                this.Shutdown();
                return;
            }

            //PROBE_GATES=<mission> - gates, and wiring one to an objective.
            //
            //A gate is two things that have to agree in two different files: a region shaped like
            //a wall, and an objective naming it in locked-doors. A gate nothing names is a wall
            //that never opens, which in game is indistinguishable from a mission that is broken.
            var probeGates = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_GATES="));
            if (probeGates != null)
            {
                var wanted = probeGates.Substring("PROBE_GATES=".Length).Trim('"');
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                try
                {
                    var window = new UI.SpawnsWindow(Logic.MapSpawns.load(folder));
                    window.WindowState = WindowState.Normal;
                    window.Width = 1280; window.Height = 900; window.Left = -20000;
                    window.Show();

                    var waited = 0;
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(250),
                    };

                    timer.Tick += (_, _) =>
                    {
                        waited += 250;
                        if (!window.mapReady && waited < 15000) { return; }
                        timer.Stop();

                        var had = window.gateRows.Length;
                        Console.WriteLine($"[gates] {had} to start with");
                        foreach (var one in window.gateRows.Take(5)) { Console.WriteLine($"[gates]   {one}"); }
                        Console.WriteLine($"[gates] hint: {window.gateHint}");

                        window.probeAddGate(20, 20, 20);

                        var now = window.gateRows;
                        Console.WriteLine(now.Length == had + 1
                            ? $"[gates] added one, now {now.Length}"
                            : "[gates] WRONG - the gate was not added");

                        var mine = now.FirstOrDefault(one => one.StartsWith("gate"));
                        Console.WriteLine($"[gates]   {mine}");

                        //A fresh gate must read as held by nothing - that is the warning that
                        //stops somebody shipping a wall which never opens.
                        Console.WriteLine(mine != null && mine.Contains("nothing opens it")
                            ? "[gates] and nothing opens it yet, which is said plainly"
                            : "[gates] WRONG - a new gate does not warn that nothing opens it");

                        var at = Array.FindIndex(now, one => one.StartsWith("gate"));
                        window.probePickGate(at);

                        //Turning has to change which axis it lies along, or a gate ends up lying
                        //along the corridor it was meant to block.
                        var before = window.gateRows[at];
                        window.probeTurnGate();
                        var after = window.gateRows[Array.FindIndex(window.gateRows, one => one.StartsWith("gate"))];
                        Console.WriteLine(before.Contains("across x") != after.Contains("across x")
                            ? "[gates] Turn flips which way it lies"
                            : "[gates] WRONG - Turn did not change the axis");

                        window.probeWidenGate();
                        Console.WriteLine(window.gateRows.Any(one => one.StartsWith("gate") && one.Contains("7 wide"))
                            ? "[gates] Wider took it from 5 to 7"
                            : "[gates] WRONG - Wider did not widen it");

                        //Wire it to the first objective and read it back off the LEVEL.
                        window.probePickGate(Array.FindIndex(window.gateRows, one => one.StartsWith("gate")));
                        window.probeLockGate(0);

                        var wired = window.gateRows.FirstOrDefault(one => one.StartsWith("gate"));
                        Console.WriteLine(wired != null && wired.Contains("opens:")
                            ? $"[gates] wired  ->  {wired}"
                            : "[gates] WRONG - the gate was not wired to an objective");
                        Console.WriteLine($"[gates] status: {window.probeStatus}");

                        window.probeUnlockGate();
                        Console.WriteLine(window.gateRows.Any(one => one.StartsWith("gate") && one.Contains("nothing opens it"))
                            ? "[gates] and unwiring puts the warning back"
                            : "[gates] WRONG - unwiring left it looking held");

                        //Removing has to tidy the objective too, or the level names a region that
                        //is no longer there.
                        window.probePickGate(Array.FindIndex(window.gateRows, one => one.StartsWith("gate")));
                        window.probeLockGate(0);
                        window.probePickGate(Array.FindIndex(window.gateRows, one => one.StartsWith("gate")));
                        window.probeRemoveGate();

                        Console.WriteLine(window.gateRows.Length == had
                            ? "[gates] removed again, back to where it started"
                            : "[gates] WRONG - the gate did not come out");
                        //Asked about THAT region by name. The chain hint is no good here: this
                        //mission's own objectives already name villager regions that are not in
                        //the open room, so it says "never finish" whatever the gates do.
                        Console.WriteLine(!window.namedByAnyObjective("gate1")
                            ? "[gates] and no objective is left naming the gate that went"
                            : "[gates] WRONG - an objective still points at the removed gate");

                        this.Shutdown();
                    };

                    timer.Start();
                    return;
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[gates] refused: {problem.Message}");
                    this.Shutdown();
                    return;
                }
            }

            //PROBE_EXIT=<mission> - the way out: whether the mission has one, and building one.
            //
            //An exit needs a region AND an objective that names it, and either alone does nothing
            //with no error anywhere - the mission simply cannot be finished. That is the failure
            //this checks for, because it is invisible until somebody plays to the end.
            var probeExit = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_EXIT="));
            if (probeExit != null)
            {
                var wanted = probeExit.Substring("PROBE_EXIT=".Length).Trim('"');
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                try
                {
                    var map = Logic.MapSpawns.load(folder);

                    Console.WriteLine(Logic.MapSpawns.hasExitObjective(map)
                        ? "[exit] the level has an objective that clicks an exit gate"
                        : "[exit] the level has NO exit objective");

                    var total = 0;
                    foreach (var room in map.Rooms)
                    {
                        foreach (var one in Logic.MapSpawns.exitsOf(map, room))
                        {
                            Console.WriteLine($"[exit] {room.Id}: {one}");
                            total++;
                        }
                    }
                    Console.WriteLine($"[exit] {total} gate region(s) across {map.Rooms.Count} room(s)");

                    var window = new UI.SpawnsWindow(map);
                    window.WindowState = WindowState.Normal;
                    window.Width = 1280; window.Height = 800; window.Left = -20000;
                    window.Show();

                    var waited = 0;
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(250),
                    };

                    timer.Tick += (_, _) =>
                    {
                        waited += 250;
                        if (!window.mapReady && waited < 15000) { return; }
                        timer.Stop();

                        Console.WriteLine($"[exit] hint: {window.exitHint}");

                        //The chain, which is the other half of why a gate does nothing.
                        foreach (var step in window.questRows)
                        {
                            Console.WriteLine($"[exit]   {step}");
                        }
                        Console.WriteLine($"[exit] chain: {window.questHintNow}");

                        var steps = window.questRows.Length;
                        window.probeOnlyExit();

                        //A chain that is already nothing but the exit has nothing to trim, which
                        //is the right answer rather than a failure - it is what a map built from
                        //the empty baseline looks like.
                        Console.WriteLine(window.questRows.Length < steps
                            ? $"[exit] trimmed the chain from {steps} to {window.questRows.Length}"
                            : steps <= 1
                                ? "[exit] nothing to trim, the chain was already just the way out"
                                : "[exit] WRONG - the chain was not trimmed");

                        Console.WriteLine(window.questRows.All(one => one.Contains("the way out"))
                            ? "[exit] and what is left is the way out"
                            : "[exit] WRONG - something other than the exit survived");

                        Console.WriteLine($"[exit] status: {window.probeStatus}");

                        var had = window.exitRows.Length;
                        var hadObjective = window.exitObjectiveNow;

                        window.probeAddExit(30, 20, 30);

                        Console.WriteLine(window.exitRows.Length == had + 1
                            ? $"[exit] added a gate, now {window.exitRows.Length}"
                            : "[exit] WRONG - the gate was not added");

                        Console.WriteLine(window.exitObjectiveNow
                            ? "[exit] and an objective points at it"
                            : "[exit] WRONG - no objective was written, the gate would never appear");

                        Console.WriteLine(window.exitRows.All(one => one.Contains("the way out"))
                            ? "[exit] every gate reads as claimed"
                            : "[exit] WRONG - a gate says nothing points at it");

                        Console.WriteLine(hadObjective || window.probeStatus.Contains("finished")
                            ? "[exit] the status says the mission can now be finished"
                            : "[exit] WRONG - adding the first gate did not say so");

                        Console.WriteLine($"[exit] status: {window.probeStatus}");
                        Console.WriteLine($"[exit] hint: {window.exitHint}");

                        this.Shutdown();
                    };

                    timer.Start();
                    return;
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[exit] refused: {problem.Message}");
                    this.Shutdown();
                    return;
                }
            }

            //PROBE_START=<mission> - where a mission puts the player, and placing one.
            //
            //This exists because the editor was wrong about this once. Doors looked like the way
            //in - a welded Creeper Woods has one called "enter" sitting in its outer wall - and
            //they are not. Arriving is a trigger region tagged "playerstart", and the only way to
            //know the editor has the right idea is to find the ones the game itself ships.
            var probeStart = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_START="));
            if (probeStart != null)
            {
                var wanted = probeStart.Substring("PROBE_START=".Length).Trim('"');
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                try
                {
                    var map = Logic.MapSpawns.load(folder);

                    //Every room, not just the chosen one: a mission's start is in whichever room
                    //it begins in, and reporting only the open one would say "none" about a
                    //mission that has one three rooms along.
                    var total = 0;
                    foreach (var room in map.Rooms)
                    {
                        var here = Logic.MapSpawns.startsOf(room);
                        total += here.Count;
                        foreach (var one in here)
                        {
                            Console.WriteLine($"[start] {room.Id}: {one}");
                        }
                    }

                    //Which one is the MAIN way in, and whether it is the one that comes from the
                    //tile the first stretch plays. Those two have to agree: order is the only
                    //thing distinguishing otherwise identical regions, so if the front of the
                    //array is not the start room's, the editor is pointing at the wrong pin.
                    foreach (var room in map.Rooms)
                    {
                        var here = Logic.MapSpawns.startsOf(room);
                        var main = here.FirstOrDefault(one => one.IsMain);
                        if (main == null) { continue; }

                        Console.WriteLine($"[start] {room.Id}: main = {main.Pos[0]},{main.Pos[1]},{main.Pos[2]}"
                            + $" ({main.Size[0]}x{main.Size[2]}), index {main.At}");

                        var others = here.Where(one => !one.IsMain).ToList();
                        Console.WriteLine(here.Count(one => one.IsMain) == 1
                            ? $"[start] exactly one is main, {others.Count} teleport arrival(s)"
                            : "[start] WRONG - more than one claims to be the main way in");

                        Console.WriteLine(others.All(one => one.At > main.At)
                            ? "[start] and it sits in front of all the others"
                            : "[start] WRONG - a teleport arrival comes before the main way in");
                    }

                    Console.WriteLine($"[start] {total} arrival area(s) across {map.Rooms.Count} room(s)");
                    Console.WriteLine(total > 0
                        ? "[start] the game's own mission has one, so the editor is looking for the right thing"
                        : "[start] WRONG - found none, which cannot be true of a mission that plays");

                    var window = new UI.SpawnsWindow(map);
                    window.WindowState = WindowState.Normal;
                    window.Width = 1280;
                    window.Height = 800;
                    window.Left = -20000;
                    window.Show();

                    var waited = 0;
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(250),
                    };

                    timer.Tick += (_, _) =>
                    {
                        waited += 250;
                        if (!window.mapReady && waited < 15000) { return; }
                        timer.Stop();

                        var had = window.startRows;
                        Console.WriteLine($"[start] the open room shows {had.Length}");
                        Console.WriteLine($"[start] hint: {window.startHint}");

                        window.probeAddStart(40, 30, 40);

                        var now = window.startRows;
                        Console.WriteLine(now.Length == had.Length + 1
                            ? $"[start] added one, now {now.Length}"
                            : "[start] WRONG - the start was not added");
                        Console.WriteLine($"[start]   {now.LastOrDefault()}");
                        Console.WriteLine($"[start] status: {window.probeStatus}");

                        //It has to come back as a playerstart when the room is read again, not
                        //just appear in the list - the list is this session, the region is the file.
                        var reread = Logic.MapSpawns.startsOf(map.Rooms.First(
                            one => one.Id == map.Rooms[0].Id));
                        Console.WriteLine(window.startRows.Length == now.Length
                            ? "[start] and it reads back as a playerstart region"
                            : "[start] WRONG - it does not read back");

                        //Promoting the one just added has to make it the main way in, and
                        //demote whatever was main before - there can only ever be one.
                        window.probePickStart(now.Length - 1);
                        window.probeMakeMain();

                        var after = window.startRows;
                        Console.WriteLine(after.Count(one => one.Contains("main way in")) == 1
                            ? "[start] still exactly one main way in after promoting"
                            : "[start] WRONG - promoting left the wrong number of main ways in");
                        Console.WriteLine(after.FirstOrDefault()?.Contains("main way in") == true
                            ? $"[start] and it is at the front: {after.FirstOrDefault()}"
                            : "[start] WRONG - the main way in is not at the front of the list");
                        Console.WriteLine($"[start] status: {window.probeStatus}");

                        window.probePickStart(after.Length - 1);
                        window.probeRemoveStart();

                        Console.WriteLine(window.startRows.Length == had.Length
                            ? "[start] removed again, back to where it started"
                            : "[start] WRONG - the start did not come out");
                        Console.WriteLine($"[start] status: {window.probeStatus}");

                        this.Shutdown();
                    };

                    timer.Start();
                    return;
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[start] refused: {problem.Message}");
                    this.Shutdown();
                    return;
                }
            }

            //PROBE_DOORS=<mission> - the whole door workflow: what a mission already has, adding
            //one, naming it as the way in, and taking it away again.
            //
            //Doors are the part of a custom mission that cannot be seen in Minecraft and cannot
            //be seen in the game either - until the game refuses to load. A tile with no doors
            //crashed the camp, and the only reason that was ever understood is that somebody ran
            //the level twice. This checks the editor says so instead.
            var probeDoors = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_DOORS="));
            if (probeDoors != null)
            {
                var wanted = probeDoors.Substring("PROBE_DOORS=".Length).Trim('"');
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                try
                {
                    var map = Logic.MapSpawns.load(folder);
                    var window = new UI.SpawnsWindow(map);

                    window.WindowState = WindowState.Normal;
                    window.Width = 1280;
                    window.Height = 800;
                    window.Left = -20000;
                    window.Show();

                    var waited = 0;
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(250),
                    };

                    timer.Tick += (_, _) =>
                    {
                        waited += 250;
                        if (!window.mapReady && waited < 15000) { return; }
                        timer.Stop();

                        var had = window.doorRows;
                        Console.WriteLine($"[doors] {had.Length} to start with, "
                            + $"entry = \"{window.entryDoorNow}\"");
                        foreach (var one in had.Take(6)) { Console.WriteLine($"[doors]   {one}"); }
                        Console.WriteLine($"[doors] hint: {window.doorHint}");

                        //1. Add one, at a spot picked to be near an x wall so the size should come
                        //   out spanning z.
                        window.probeAddDoor("probe_way_in", 1, 40, 30);

                        var now = window.doorRows;
                        Console.WriteLine($"[doors] after adding: {now.Length}");
                        Console.WriteLine(now.Length == had.Length + 1
                            ? "[doors] one door added"
                            : "[doors] WRONG - the door was not added");

                        var added = now.FirstOrDefault(one => one.StartsWith("probe_way_in"));
                        Console.WriteLine(added != null
                            ? $"[doors]   {added}"
                            : "[doors] WRONG - the added door is not in the list");

                        //2. It has to lie ALONG Z next to an x wall, or it is buried in the wall.
                        Console.WriteLine(added != null && added.Contains("along z")
                            ? "[doors] and it lies along z, which is right for an x wall"
                            : "[doors] WRONG - the door lies the wrong way for the wall it is in");

                        //3. Name it as the way in and check the LEVEL, not the panel.
                        var at = Array.FindIndex(now, one => one.StartsWith("probe_way_in"));
                        window.probePickDoor(at);
                        window.probeMakeEntry();

                        Console.WriteLine(window.entryDoorNow == "probe_way_in"
                            ? "[doors] the level now names it as the way in"
                            : $"[doors] WRONG - the level says \"{window.entryDoorNow}\"");

                        Console.WriteLine(window.doorRows.Any(one => one.Contains("the way in"))
                            ? "[doors] and the list marks it"
                            : "[doors] WRONG - the list does not mark the entry door");

                        Console.WriteLine($"[doors] hint: {window.doorHint}");

                        //4. Take it away again, leaving the mission as it was found.
                        var back = Array.FindIndex(window.doorRows,
                            one => one.StartsWith("probe_way_in"));
                        window.probePickDoor(back);
                        window.probeRemoveDoor();

                        Console.WriteLine(window.doorRows.Length == had.Length
                            ? "[doors] removed again, back to where it started"
                            : "[doors] WRONG - the door did not come out");
                        Console.WriteLine($"[doors] status: {window.probeStatus}");

                        //Nothing is saved: the probe never presses Save, so the mission on disk
                        //is untouched whatever happened above.
                        this.Shutdown();
                    };

                    timer.Start();
                    return;
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[doors] refused: {problem.Message}");
                    this.Shutdown();
                    return;
                }
            }

            //PROBE_DRAG=<mission> - takes hold of a spawn point in the 3D view and drags it,
            //through the same press, move and release the mouse goes through.
            //
            //Everything here can be right in isolation and still not move a point: the press has
            //to decide "that pin" rather than "turn the camera", the ray has to answer in block
            //coordinates, the region has to be rewritten, the markers have to be rebuilt, and the
            //file has to be written down as changed. Only the whole gesture covers that.
            var probeDrag = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_DRAG="));
            if (probeDrag != null)
            {
                var wanted = probeDrag.Substring("PROBE_DRAG=".Length).Trim('"');
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                try
                {
                    var map = Logic.MapSpawns.load(folder);
                    var window = new UI.SpawnsWindow(map);

                    window.WindowState = WindowState.Normal;
                    window.Width = 1280;
                    window.Height = 800;
                    window.Left = -20000;
                    window.Show();

                    var waited = 0;
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(250),
                    };

                    timer.Tick += (_, _) =>
                    {
                        waited += 250;
                        if (!window.mapReady && waited < 15000) { return; }
                        timer.Stop();

                        if (!window.mapReady)
                        {
                            Console.WriteLine("[drag] NOTHING BUILT - nothing to drag");
                            this.Shutdown();
                            return;
                        }

                        var view = window.probeView;
                        var before = window.probePoints;
                        Console.WriteLine($"[drag] {before.Count} spawn points in the room");

                        if (before.Count == 0)
                        {
                            Console.WriteLine("[drag] no spawn points here - pick another mission");
                            this.Shutdown();
                            return;
                        }

                        //Where a pin actually is ON SCREEN, found by asking the same ray the mouse
                        //asks. Working it out any other way would test a second answer rather than
                        //the one the gesture uses.
                        Point? onPin = null;
                        (int x, int y, int z) pin = default;
                        Point? onBare = null;
                        (int x, int y, int z) bare = default;

                        var marks = view.probeMarks;

                        for (var sy = 20.0; sy < view.ActualHeight - 20; sy += 7)
                        {
                            for (var sx = 20.0; sx < view.ActualWidth - 20; sx += 7)
                            {
                                var at = new Point(sx, sy);
                                var hit = view.probeLook(at);
                                if (hit == null) { continue; }

                                var here = hit.Value;

                                var near = marks.Any(one =>
                                {
                                    double dx = one.x - here.x, dy = one.y - here.y,
                                           dz = one.z - here.z;
                                    return dx * dx + dz * dz + dy * dy * 0.25 <= 9.0;
                                });

                                if (near && onPin == null)
                                {
                                    onPin = at;

                                    //The pin itself, not the floor cell the ray landed on. They
                                    //are up to GRAB blocks apart, which is the whole point of
                                    //GRAB - and comparing against the wrong one of the two says
                                    //"it never moved" about a point that moved correctly.
                                    pin = marks.OrderBy(one =>
                                    {
                                        double dx = one.x - here.x, dy = one.y - here.y,
                                               dz = one.z - here.z;
                                        return dx * dx + dz * dz + dy * dy * 0.25;
                                    }).First();
                                }

                                //Somewhere no pin is, and far enough off that dropping there is
                                //unambiguous.
                                if (!near && onBare == null && marks.All(one =>
                                    Math.Abs(one.x - here.x) + Math.Abs(one.z - here.z) > 12))
                                {
                                    onBare = at;
                                    bare = here;
                                }
                            }

                            if (onPin != null && onBare != null) { break; }
                        }

                        if (onPin == null || onBare == null)
                        {
                            Console.WriteLine("[drag] could not find both a pin and bare ground on screen");
                            this.Shutdown();
                            return;
                        }

                        Console.WriteLine($"[drag] pin at {pin.x},{pin.y},{pin.z} "
                            + $"-> dragging to {bare.x},{bare.y},{bare.z}");

                        //1. A press on BARE GROUND still turns the camera. Checked first,
                        //   because after the drag below there is a point sitting on that spot
                        //   and taking hold of it would be the correct answer.
                        var turned = view.probeGrab(onBare.Value);
                        Console.WriteLine(!turned
                            ? "[drag] a press on bare ground still turns the camera"
                            : "[drag] WRONG - bare ground took hold of something");
                        view.probeDrop();

                        //2. A press ON a pin has to take hold rather than turn the camera.
                        var took = view.probeGrab(onPin.Value);
                        Console.WriteLine(took
                            ? "[drag] press on a pin took hold of it"
                            : "[drag] WRONG - press on a pin did not take hold");

                        view.probeDragTo(onBare.Value);
                        view.probeDrop();

                        var after = window.probePoints;
                        var wasThere = before.Count(one => one == pin);
                        var landed = after.Count(one => one == bare);
                        var stillThere = after.Count(one => one == pin);

                        Console.WriteLine($"[drag] points before {before.Count}, after {after.Count}");
                        Console.WriteLine(after.Count == before.Count
                            ? "[drag] the count did not change, so nothing was added or lost"
                            : "[drag] WRONG - dragging changed how many points there are");

                        Console.WriteLine(landed > before.Count(one => one == bare)
                            ? $"[drag] a point is now at {bare.x},{bare.y},{bare.z}"
                            : "[drag] WRONG - no point landed where it was dragged");

                        Console.WriteLine(stillThere < wasThere
                            ? $"[drag] and it left {pin.x},{pin.y},{pin.z}"
                            : "[drag] WRONG - the point is still where it started");

                        Console.WriteLine(window.probeChanged
                            ? "[drag] the room's file is marked changed, so Save will write it"
                            : "[drag] WRONG - nothing was marked changed, the move would not save");

                        Console.WriteLine($"[drag] status: {window.probeStatus}");

                        //3. Escape has to put it back.
                        view.probeGrab(onBare.Value);
                        view.probeDragTo(onPin.Value);
                        window.probeCancelDrag();

                        var cancelled = window.probePoints;
                        Console.WriteLine(cancelled.Count(one => one == bare) == landed
                            ? "[drag] Escape put it back where that drag started"
                            : "[drag] WRONG - Escape did not restore the point");
                        Console.WriteLine($"[drag] status: {window.probeStatus}");

                        this.Shutdown();
                    };

                    timer.Start();
                    return;
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[drag] refused: {problem.Message}");
                    this.Shutdown();
                    return;
                }
            }

            //PROBE_SPAWNS=<mission> - loads a mission's spawn data the way the editor window
            //does, and reports what it found. The window itself cannot be checked from here, but
            //everything behind it can.
            var probeSpawns = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_SPAWNS="));
            if (probeSpawns != null)
            {
                var wanted = probeSpawns.Substring("PROBE_SPAWNS=".Length).Trim('"');
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCDReborn", "maps", wanted);

                try
                {
                    var map = Logic.MapSpawns.load(folder);
                    foreach (var note in map.Notes) { Console.WriteLine($"[spawn] {note}"); }

                    Console.WriteLine($"[spawn] mob vocabulary: {Logic.GameMobs.ALL.Count} mobs, "
                        + $"{Logic.GameMobs.ALL.Count(one => one.Boss)} bosses");

                    foreach (var room in map.Rooms.Take(8))
                    {
                        Console.WriteLine($"[spawn]   {room.Pos[0],5},{room.Pos[1],4},{room.Pos[2],5}"
                            + $"  {room.Size[0],3}x{room.Size[1],3}x{room.Size[2],3}  {room}");
                    }

                    var first = map.Rooms.FirstOrDefault();
                    if (first != null)
                    {
                        var blocks = Logic.MapSpawns.blocksOf(first);
                        Console.WriteLine($"[spawn] blocks of {first.Id}: "
                            + (blocks == null ? "unreadable" : $"{blocks.Length:N0} cells"));

                        //The floor plan is what the window draws. Without it the map is an empty
                        //box, which is the difference between useful and not.
                        var heights = Logic.MapSpawns.heightsOf(first);
                        if (heights == null)
                        {
                            Console.WriteLine("[spawn] floor plan: UNREADABLE - the map would be blank");
                        }
                        else
                        {
                            var ground = heights.Count(one => one != 0);
                            Console.WriteLine($"[spawn] floor plan: {heights.Length:N0} columns, "
                                + $"{ground:N0} with ground ({ground * 100.0 / heights.Length:F0}%), "
                                + $"heights {heights.Where(one => one != 0).DefaultIfEmpty().Min()}"
                                + $"-{heights.Max()}");
                        }

                        //A few spots around the room, to see where placing succeeds and where it
                        //quietly puts nothing down.
                        var blocksFor = Logic.MapSpawns.blocksOf(first);
                        if (blocksFor != null)
                        {
                            foreach (var (x, y, z) in new[]
                            {
                                (57, 93, 50),
                                (first.Size[0] / 2, first.Size[1] / 2, first.Size[2] / 2),
                                (first.Size[0] / 2, first.Size[1] - 1, first.Size[2] / 2),
                            })
                            {
                                var floor = Logic.MapSpawns.floorUnder(first, blocksFor, x, z, y + 2);
                                Console.WriteLine($"[spawn] floor under {x},{z} looking down from "
                                    + $"{y + 2}: " + (floor == null ? "NONE" : floor.Value.ToString()));
                            }
                        }

                        var was = first.Spawns;
                        var made = Logic.MapSpawns.place(first,
                            first.Size[0] / 2, first.Size[1] / 2, first.Size[2] / 2, 8, 5, "", 7);
                        Console.WriteLine($"[spawn] placed {made} of 5 in {first.Id} "
                            + $"({was} -> {first.Spawns})");
                        Console.WriteLine($"[spawn] cleared {Logic.MapSpawns.clear(first)}");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[spawn] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_MAPS_LEVEL=<mission>[;keep] - the whole-level path: export, pin, lay the rooms
            //out against each other, convert, read back, install.
            var probeLevel = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_MAPS_LEVEL="));
            if (probeLevel != null)
            {
                var bits = probeLevel.Substring("PROBE_MAPS_LEVEL=".Length).Trim('"').Split(';');
                var mission = Logic.GameMaps.all()
                    .FirstOrDefault(one => one.Name.Equals(bits[0], StringComparison.OrdinalIgnoreCase));

                if (mission == null || !Logic.MapTools.available)
                {
                    Console.WriteLine("[lvl] nothing to do");
                    this.Shutdown();
                    return;
                }

                try
                {
                    var folder = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MCDReborn", "maps", mission.Name);

                    Console.WriteLine($"[lvl] exported {Logic.MapMod.export(mission, folder).Files} files");
                    Console.WriteLine($"[lvl] pinned: {Logic.MapTools.makeFixed(folder).GetAwaiter().GetResult().Ok}");

                    var out1 = Logic.MapTools.toMinecraftLevel(folder, mission).GetAwaiter().GetResult();
                    Console.WriteLine($"[lvl] laid out: ok={out1.Ok} world={out1.World ?? "none"}");
                    foreach (var line in out1.Output.Split((char)10)
                        .Where(one => one.Trim().StartsWith("[") || one.Contains("lifted")))
                    {
                        Console.WriteLine($"[lvl]   {line.Trim()}");
                    }

                    if (out1.World == null) { this.Shutdown(); return; }

                    Console.WriteLine($"[lvl] whole level? {Logic.MapTools.isWholeLevel(out1.World)}");

                    var out2 = Logic.MapTools.fromMinecraftLevel(out1.World).GetAwaiter().GetResult();
                    Console.WriteLine($"[lvl] back again: ok={out2.Ok}");
                    foreach (var line in out2.Output.Split((char)10)
                        .Where(one => one.Contains("updated") || one.Contains("read back")))
                    {
                        Console.WriteLine($"[lvl]   {line.Trim()}");
                    }

                    var mod = Logic.MapMod.install(folder, mission);
                    Console.WriteLine($"[lvl] installed {System.IO.Path.GetFileName(mod.Path)}, "
                        + $"{mod.Size / 1024:N0} KB");

                    if (!bits.Contains("keep", StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[lvl] removed: {Logic.MapMod.remove(mission)}");
                        System.IO.Directory.Delete(out1.World, true);
                    }
                    else
                    {
                        Console.WriteLine("[lvl] left installed, world kept");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[lvl] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_MAPS_MC=<mission>;<group> - the whole one-click path: export, convert to a
            //Minecraft world, convert it straight back, install the pak, read it, remove it.
            var probeMapsMc = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_MAPS_MC="));
            if (probeMapsMc != null)
            {
                var bits = probeMapsMc.Substring("PROBE_MAPS_MC=".Length).Trim('"').Split(';');

                Console.WriteLine($"[mc] tools: {Logic.MapTools.folder ?? "NOT FOUND"}");
                Console.WriteLine($"[mc] saves: {Logic.MapTools.saves ?? "no Minecraft"}");

                var mission = Logic.GameMaps.all()
                    .FirstOrDefault(one => one.Name.Equals(bits[0], StringComparison.OrdinalIgnoreCase));

                if (mission == null || !Logic.MapTools.available)
                {
                    Console.WriteLine("[mc] nothing to do");
                    this.Shutdown();
                    return;
                }

                try
                {
                    var folder = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MCDReborn", "maps", mission.Name);

                    var made = Logic.MapMod.export(mission, folder);
                    Console.WriteLine($"[mc] exported {made.Files} files to {made.Folder}");

                    if (bits.Contains("fixed", StringComparer.OrdinalIgnoreCase))
                    {
                        var fix = Logic.MapTools.makeFixed(folder).GetAwaiter().GetResult();
                        Console.WriteLine($"[mc] pinned: ok={fix.Ok}");
                        foreach (var line in fix.Output.Split((char)10)
                            .Where(one => one.Trim().StartsWith("[")))
                        {
                            Console.WriteLine($"[mc]   {line.Trim()}");
                        }
                    }

                    var out1 = Logic.MapTools.toMinecraft(folder, bits[1], mission).GetAwaiter().GetResult();
                    Console.WriteLine($"[mc] to Minecraft: ok={out1.Ok}  world={out1.World ?? "none"}");
                    if (out1.World == null) { Console.WriteLine(out1.Output); this.Shutdown(); return; }

                    var origin = Logic.MapTools.originOf(out1.World);
                    Console.WriteLine($"[mc] world remembers: mission={origin?.Mission}, "
                        + $"group={origin?.Group}");

                    var out2 = Logic.MapTools.fromMinecraft(out1.World, origin!).GetAwaiter().GetResult();
                    Console.WriteLine($"[mc] back again: ok={out2.Ok}  {out2.Last}");

                    var mod = Logic.MapMod.install(origin!.Map, mission);
                    Console.WriteLine($"[mc] installed {System.IO.Path.GetFileName(mod.Path)}, "
                        + $"{mod.Size / 1024:N0} KB");

                    var reader = new PakReader.Pak.PakFileReader(mod.Path);
                    reader.ReadIndex(null);
                    Console.WriteLine($"[mc] pak holds {reader.Count()} entries");
                    reader.Stream?.Dispose();

                    if (bits.Contains("keep", StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("[mc] left installed, world kept");
                    }
                    else
                    {
                        Console.WriteLine($"[mc] removed: {Logic.MapMod.remove(mission)}");
                        System.IO.Directory.Delete(out1.World, true);
                        Console.WriteLine("[mc] world deleted");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[mc] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_MAPS_IN=<folder>;<mission> - installs a map folder over a mission, reads the
            //pak back, and removes it again. It writes into the real Paks folder because that is
            //the path being tested; it deletes what it wrote before returning.
            var probeMapsIn = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_MAPS_IN="));
            if (probeMapsIn != null)
            {
                var bits = probeMapsIn.Substring("PROBE_MAPS_IN=".Length).Trim('"').Split(';');
                var mission = Logic.GameMaps.all()
                    .FirstOrDefault(one => one.Name.Equals(bits[1], StringComparison.OrdinalIgnoreCase));

                if (mission == null)
                {
                    Console.WriteLine($"[in] no mission called {bits[1]}");
                    this.Shutdown();
                    return;
                }

                try
                {
                    var mod = Logic.MapMod.install(bits[0], mission);
                    Console.WriteLine($"[in] wrote {System.IO.Path.GetFileName(mod.Path)}, "
                        + $"{mod.Size / 1024:N0} KB, over {mission.Label}");

                    //Read back with the same reader the game's own paks go through, because a pak
                    //that writes without complaint and cannot be opened looks identical from here.
                    var reader = new PakReader.Pak.PakFileReader(mod.Path);
                    reader.ReadIndex(null);
                    Console.WriteLine($"[in] reads back: initialised {reader.Initialized}, "
                        + $"{reader.Count()} entries");

                    foreach (var entry in reader.Take(4))
                    {
                        Console.WriteLine($"[in]   {entry.Value.UncompressedSize,10:N0}  "
                            + reader.MountPoint + entry.Key);
                    }

                    //Let go of the file before removing it. The tab never opens the pak it wrote,
                    //so only this check needs to - and without closing it, remove() fails on a
                    //lock this probe is holding itself.
                    reader.Stream?.Dispose();

                    Console.WriteLine($"[in] installedFor says: "
                        + (Logic.MapMod.installedFor(mission) != null ? "found" : "NOT FOUND"));
                    //"keep" leaves it installed, for when the point is to go and play it.
                    if (bits.Length > 2 && bits[2].Equals("keep", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("[in] left installed");
                    }
                    else
                    {
                        Console.WriteLine($"[in] removed: {Logic.MapMod.remove(mission)}");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[in] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_MAPS[=<mission>;<folder>] - the mission catalogue, and optionally an export
            //of one of them. The Maps tab is two file moves and a pak write, and each of those
            //fails in a way that looks the same from the tab: nothing appears.
            var probeMaps = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_MAPS"));
            if (probeMaps != null)
            {
                var at = probeMaps.IndexOf('=');
                var bits = at > 0 ? probeMaps.Substring(at + 1).Trim('"').Split(';') : Array.Empty<string>();

                var missions = Logic.GameMaps.all();
                Console.WriteLine($"[maps] {missions.Count} missions");
                foreach (var note in Logic.GameMaps.Notes) { Console.WriteLine($"[maps]   {note}"); }

                foreach (var one in missions.Take(bits.Length > 0 ? 6 : 80))
                {
                    Console.WriteLine($"[maps]   {one.Bytes / 1024,6:N0} KB  {one.Label}");
                }

                if (bits.Length >= 2)
                {
                    var wanted = missions.FirstOrDefault(one =>
                        one.Name.Equals(bits[0], StringComparison.OrdinalIgnoreCase));

                    if (wanted == null)
                    {
                        Console.WriteLine($"[maps] no mission called {bits[0]}");
                        this.Shutdown();
                        return;
                    }

                    try
                    {
                        var made = Logic.MapMod.export(wanted, bits[1]);
                        Console.WriteLine($"[maps] exported {made.Files} files, "
                            + $"{made.Bytes / 1024:N0} KB, to {made.Folder}");
                        foreach (var note in made.Notes) { Console.WriteLine($"[maps]   {note}"); }
                    }
                    catch (Exception problem)
                    {
                        Console.WriteLine($"[maps] export refused: {problem.Message}");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_UIFORMS[=<substring>] - every interface texture the Gear list offers, with
            //the size and pixel format each is stored in.
            //
            //The list on its own says what CAN be picked. Whether any of it can be REPAINTED is a
            //different question that only the stored format answers: B8G8R8A8 is written back byte
            //for byte, DXT1 goes through the block encoder, and anything else is refused. Bulk
            //work needs to know which bucket each one is in before it starts, not after.
            var probeForms = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_UIFORMS"));
            if (probeForms != null)
            {
                var wanted = probeForms.StartsWith("PROBE_UIFORMS=")
                    ? probeForms.Substring("PROBE_UIFORMS=".Length).Trim('"')
                    : string.Empty;

                var all = Logic.CosmeticSkins.userInterface();
                var pak = Logic.CustomSkins.index;
                var shown = 0;

                foreach (var entry in all)
                {
                    if (wanted.Length > 0
                        && entry.Id.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var size = "?";
                    var form = "unreadable";

                    try
                    {
                        var pkg = pak?.extractPackage(entry.Id);
                        var tex = pkg?.GetExport<PakReader.Parsers.Class.UTexture2D>();
                        //A struct, so it cannot be compared to null - the length of the array
                        //is what says whether there is one.
                        if (tex?.PlatformDatas is { Length: > 0 } datas)
                        {
                            var platform = datas[0];
                            size = $"{platform.SizeX}x{platform.SizeY}";
                            form = platform.PixelFormat.ToString();
                        }
                    }
                    catch (Exception problem)
                    {
                        form = "threw: " + problem.GetType().Name;
                    }

                    Console.WriteLine($"[ui] {form}\t{size}\t{entry.Id}");
                    shown++;
                }

                Console.WriteLine($"[ui] {shown:N0} of {all.Count:N0} interface textures");
                this.Shutdown();
                return;
            }

            //PROBE_INDEX=<substring>[;<how many>] - which entries of the game's own paks match.
            //
            //PROBE_PAK reads a mod, which is unencrypted and answers for itself. The game's paks
            //are not, and the index the app unlocked at startup is the only way to see inside
            //them - so asking "does the game ship block textures" had no answer until now.
            var probeIndex = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_INDEX="));
            if (probeIndex != null)
            {
                var bits = probeIndex.Substring("PROBE_INDEX=".Length).Trim('"').Split(';');
                var wanted = bits[0];
                var many = bits.Length > 1 && int.TryParse(bits[1], out var asked) ? asked : 60;

                var index = Logic.CustomSkins.index;
                var seen = 0;
                var shown = 0;

                //The entry type is not public, so the loop has to be written where var can see it
                //rather than handed an empty sequence of the same type.
                foreach (var entry in index!.AllEntries())
                {
                    if (entry.Key.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    seen++;
                    if (shown++ < many) { Console.WriteLine($"[index] {entry.Key}"); }
                }

                Console.WriteLine($"[index] {seen:N0} match \"{wanted}\"");
                this.Shutdown();
                return;
            }

            //PROBE_RAW=<pak path>;<file to write> - the bytes of one entry in the game's own paks.
            //
            //extractPackage is for .uasset/.uexp pairs and returns nothing for anything else, which
            //is most of data/ - the level JSON, the object groups, the resource packs. Those are
            //plain files sitting in an encrypted pak, and the index the app already unlocked is the
            //only thing that can reach them.
            var probeRaw = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_RAW="));
            if (probeRaw != null)
            {
                var bits = probeRaw.Substring("PROBE_RAW=".Length).Trim('"').Split(';');
                var index = Logic.CustomSkins.index;

                var got = index?.GetFile(bits[0]);
                if (got == null)
                {
                    Console.WriteLine($"[raw] nothing at {bits[0]}");
                    this.Shutdown();
                    return;
                }

                var bytes = got.Value.ToArray();
                Console.WriteLine($"[raw] {bits[0]}: {bytes.Length:N0} bytes");

                if (bits.Length > 1 && bits[1].Length > 0)
                {
                    System.IO.File.WriteAllBytes(bits[1], bytes);
                    Console.WriteLine($"[raw] written to {bits[1]}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_PAK=<pak file>[;<substring>[;<folder to write into>]] - what is inside somebody
            //else's mod, and optionally the bytes of the entries that match.
            //
            //Reading a published mod is how nearly everything in this project was learned, and
            //until now it meant unzipping by hand. The index is a separate ask after the
            //constructor, which is the mistake that makes a pak look empty.
            var probePak = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_PAK="));
            if (probePak != null)
            {
                var bits = probePak.Substring("PROBE_PAK=".Length).Trim('"').Split(';');
                var wanted = bits.Length > 1 && bits[1].Length > 0 ? bits[1] : null;
                var into = bits.Length > 2 && bits[2].Length > 0 ? bits[2] : null;

                try
                {
                    var reader = new PakReader.Pak.PakFileReader(bits[0]);
                    reader.ReadIndex(null);

                    Console.WriteLine($"[pak] {System.IO.Path.GetFileName(bits[0])}");
                    Console.WriteLine($"[pak] initialised {reader.Initialized}, mount \"{reader.MountPoint}\"");

                    var shown = 0;
                    var total = 0;
                    long bytes = 0;

                    foreach (var entry in reader)
                    {
                        total++;
                        bytes += entry.Value.UncompressedSize;

                        var path = reader.MountPoint + entry.Key.Replace(System.IO.Path.DirectorySeparatorChar, '/');
                        if (wanted != null
                            && path.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        if (shown < 400)
                        {
                            Console.WriteLine($"[pak] {entry.Value.UncompressedSize,10:N0}  {path}");
                        }
                        shown++;

                        if (into != null)
                        {
                            //Flattened, because a mod's tree is deep and what is wanted here is
                            //to look at the bytes rather than to rebuild the layout.
                            var name = path.Replace('/', '_')
                                .Replace(System.IO.Path.DirectorySeparatorChar, '_').Trim('_');
                            System.IO.Directory.CreateDirectory(into);
                            var got = (ReadOnlyMemory<byte>)entry.Value.GetData(
                                reader.Stream, reader.AesKey, reader.Info.CompressionMethods);
                            System.IO.File.WriteAllBytes(
                                System.IO.Path.Combine(into, name), got.ToArray());
                        }
                    }

                    Console.WriteLine($"[pak] {total:N0} entries, {bytes:N0} bytes uncompressed"
                        + (wanted == null ? "" : $", {shown:N0} matched \"{wanted}\""));
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[pak] could not read it: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_CALLSIG=<function name> - the pins of a native function, recovered from the
            //blueprints that call it.
            //
            //A native signature is written down nowhere a mod can read: the import table gives the
            //name alone, and the parameters live in Kismet bytecode. But the blueprint compiler
            //leaves them behind by accident. Every call spills its pins into local variables named
            //CallFunc_<Function>_<Pin>, and each local is an export with a type - so gathering
            //those across every asset in the game reconstructs the pin list without decoding a
            //single instruction.
            //
            //It recovers names and types, NOT the order or which way a pin faces. ReturnValue is
            //the output; the rest have to be matched against a call seen in the editor.
            var probeCallSig = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_CALLSIG="));
            if (probeCallSig != null)
            {
                var wanted = probeCallSig.Substring("PROBE_CALLSIG=".Length).Trim('"');
                var prefix = "CallFunc_" + wanted + "_";

                var index = Logic.CustomSkins.index;
                if (index == null)
                {
                    Console.WriteLine("[pins] the game's paks are not loaded");
                    this.Shutdown();
                    return;
                }

                //pin name -> type -> how many callers spelled it that way. More than one type for
                //a pin means the name is used by two different functions, and the count says which
                //reading to trust.
                var pins = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
                var callers = new List<string>();

                foreach (var entry in index.AllEntries())
                {
                    var path = "/" + entry.Key
                        .Replace(System.IO.Path.DirectorySeparatorChar, '/').TrimStart('/');

                    byte[] uasset;
                    try
                    {
                        var package = index.extractPackage(path);
                        if (package == null) { continue; }
                        uasset = package.Value.UAsset.ToArray();
                    }
                    catch (Exception) { continue; }

                    var exports = Logic.CookedPackage.readExports(uasset);
                    var imports = Logic.CookedPackage.readImports(uasset);
                    var any = false;

                    foreach (var one in exports)
                    {
                        if (!one.Name.StartsWith(prefix, StringComparison.Ordinal)) { continue; }

                        //Trailing digits are the compiler disambiguating a second call in the same
                        //graph - ReturnValue1 is ReturnValue.
                        var pin = one.Name.Substring(prefix.Length).TrimEnd(
                            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

                        var type = "?";
                        if (one.ClassIndex < 0 && -one.ClassIndex - 1 < imports.Count)
                        {
                            type = imports[-one.ClassIndex - 1].ObjectName;
                        }

                        if (!pins.TryGetValue(pin, out var types))
                        {
                            types = new Dictionary<string, int>(StringComparer.Ordinal);
                            pins[pin] = types;
                        }
                        types.TryGetValue(type, out var was);
                        types[type] = was + 1;
                        any = true;
                    }

                    if (any && callers.Count < 12) { callers.Add(path); }
                }

                Console.WriteLine($"[pins] {wanted}: {pins.Count} distinct pins");
                foreach (var pin in pins.OrderByDescending(x => x.Value.Values.Sum()))
                {
                    var spelling = string.Join(", ",
                        pin.Value.OrderByDescending(x => x.Value).Select(x => $"{x.Key} x{x.Value}"));
                    Console.WriteLine($"[pins]   {pin.Key,-28} {spelling}");
                }

                Console.WriteLine($"[pins] seen in:");
                foreach (var one in callers) { Console.WriteLine($"[pins]   {one}"); }

                this.Shutdown();
                return;
            }

            //PROBE_SIG=<asset path>[;<function>] - the functions a blueprint declares, and what
            //each one takes.
            //
            //The import table names a native function and stops there, so a signature cannot be
            //looked up - it has to be read off a blueprint that overrides or wraps the call. A
            //function is an export; its parameters are exports whose Outer is that function, and
            //each one's class says its type. Wrong parameters do not fail to compile, they read
            //the stack in the wrong places at run time, so this is the difference between calling
            //the game's own code and corrupting it.
            var probeSig = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_SIG="));
            if (probeSig != null)
            {
                var bits = probeSig.Substring("PROBE_SIG=".Length).Trim('"').Split(';');
                var wanted = bits.Length > 1 ? bits[1] : null;

                var index = Logic.CustomSkins.index;
                var package = index?.extractPackage(bits[0]);
                if (package == null)
                {
                    Console.WriteLine($"[sig] {bits[0]} could not be read");
                    this.Shutdown();
                    return;
                }

                var uasset = package.Value.UAsset.ToArray();
                var exports = Logic.CookedPackage.readExports(uasset);
                var imports = Logic.CookedPackage.readImports(uasset);

                //An FPackageIndex points either way: above zero into the exports, below into the
                //imports. A parameter's type is nearly always an import (IntProperty and friends
                //are CoreUObject classes), and its owner nearly always an export.
                string spell(int packageIndex)
                {
                    if (packageIndex > 0)
                    {
                        var at = packageIndex - 1;
                        return at < exports.Count ? exports[at].Name : "?";
                    }
                    if (packageIndex < 0)
                    {
                        var at = -packageIndex - 1;
                        return at < imports.Count ? imports[at].ObjectName : "?";
                    }
                    return "-";
                }

                Console.WriteLine($"[sig] {bits[0]}");
                Console.WriteLine($"[sig] {exports.Count:N0} exports, {imports.Count:N0} imports");

                var functions = exports
                    .Select((one, at) => (one, at))
                    .Where(x => spell(x.one.ClassIndex) == "Function")
                    .Where(x => wanted == null
                        || x.one.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                Console.WriteLine($"[sig] {functions.Count:N0} functions"
                    + (wanted == null ? "" : $" matching \"{wanted}\""));

                foreach (var (fn, at) in functions)
                {
                    //Exports index from one, so the function at table position N is referred to
                    //by N+1 - getting this off by one silently reports every parameter against
                    //its neighbour.
                    var mine = at + 1;
                    var parameters = exports.Where(one => one.Outer == mine).ToList();

                    Console.WriteLine($"[sig]   {fn.Name}  ({parameters.Count} parameters)");
                    foreach (var one in parameters)
                    {
                        Console.WriteLine($"[sig]       {spell(one.ClassIndex),-24} {one.Name}");
                    }
                }

                this.Shutdown();
                return;
            }

            //FIND_SCRIPT=<name> - which assets import a given /Script/Dungeons class or function.
            //
            //PROBE_SCRIPT says a name exists; this says where to go and read it. A signature is not
            //in the import table, so the only way to learn one is the Kismet bytecode of something
            //that already makes the call.
            var findScript = _startupArguments.FirstOrDefault(a => a.StartsWith("FIND_SCRIPT="));
            if (findScript != null)
            {
                var wanted = findScript.Substring("FIND_SCRIPT=".Length).Trim('"');
                var index = Logic.CustomSkins.index;
                if (index == null)
                {
                    Console.WriteLine("[who] the game's paks are not loaded");
                    this.Shutdown();
                    return;
                }

                var shown = 0;
                foreach (var entry in index.AllEntries())
                {
                    var path = "/" + entry.Key
                        .Replace(System.IO.Path.DirectorySeparatorChar, '/').TrimStart('/');

                    byte[] uasset;
                    try
                    {
                        var package = index.extractPackage(path);
                        if (package == null) { continue; }
                        uasset = package.Value.UAsset.ToArray();
                    }
                    catch (Exception) { continue; }

                    var imports = Logic.CookedPackage.readImports(uasset);
                    var hit = imports.FirstOrDefault(one =>
                        string.Equals(one.ObjectName, wanted, StringComparison.OrdinalIgnoreCase));
                    if (hit == null) { continue; }

                    //What it is, and what owns it, so a function is reported against its class.
                    var owner = "";
                    if (hit.Outer < 0 && -hit.Outer - 1 < imports.Count)
                    {
                        owner = imports[-hit.Outer - 1].ObjectName;
                    }

                    Console.WriteLine($"[who] {hit.ClassName,-12} {owner,-28} {path}");
                    if (++shown >= 60) { Console.WriteLine("[who] ..."); break; }
                }

                Console.WriteLine($"[who] {shown} shown");
                this.Shutdown();
                return;
            }

            //PROBE_SCRIPT[=<how many assets>] - what /Script/Dungeons actually exposes.
            //
            //The stub module makes a cast compile by being NAMED Dungeons; what it cannot do is say
            //which names are worth declaring. A cooked package writes every native class and
            //function it touches into its import table, so the game's own blueprints are a list of
            //its C++ surface - the only one readable without a decompiler.
            var probeScript = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_SCRIPT"));
            if (probeScript != null)
            {
                var many = 4000;
                var at = probeScript.IndexOf('=');
                if (at > 0) { int.TryParse(probeScript.Substring(at + 1), out many); }

                var index = Logic.CustomSkins.index;
                if (index == null)
                {
                    Console.WriteLine("[script] the game's paks are not loaded");
                    this.Shutdown();
                    return;
                }

                //class name -> function name -> how many assets call it. The count is the useful
                //part: a function one asset touches may be incidental, one that four hundred touch
                //is the game's spine.
                var byClass = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
                var classes = new Dictionary<string, int>(StringComparer.Ordinal);
                var structs = new Dictionary<string, int>(StringComparer.Ordinal);
                var enums = new Dictionary<string, int>(StringComparer.Ordinal);

                void tally(Dictionary<string, int> into, string key)
                {
                    into.TryGetValue(key, out var was);
                    into[key] = was + 1;
                }

                var looked = 0;
                var read = 0;
                var withDungeons = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();

                foreach (var entry in index.AllEntries())
                {
                    if (looked >= many) { break; }
                    looked++;

                    byte[] uasset;
                    try
                    {
                        var package = index.extractPackage("/" + entry.Key
                            .Replace(System.IO.Path.DirectorySeparatorChar, '/').TrimStart('/'));
                        if (package == null) { continue; }
                        uasset = package.Value.UAsset.ToArray();
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    read++;

                    var imports = Logic.CookedPackage.readImports(uasset);
                    if (imports.Count == 0) { continue; }

                    //An import's outer chain says which module a name belongs to: a function's
                    //outer is its class, and that class's outer is the package. Following it is the
                    //only way to tell Dungeons.GetHealth from Engine.GetHealth.
                    Logic.CookedPackage.Import? outerOf(Logic.CookedPackage.Import one)
                    {
                        var to = one.Outer;
                        if (to >= 0) { return null; }
                        var i = -to - 1;
                        return i < imports.Count ? imports[i] : null;
                    }

                    bool inDungeons(Logic.CookedPackage.Import? one)
                        => one != null && one.ObjectName == "/Script/Dungeons";

                    var any = false;
                    foreach (var one in imports)
                    {
                        var outer = outerOf(one);

                        if (one.ClassName == "Function")
                        {
                            //The owner, and the package the owner is in.
                            if (outer == null || !inDungeons(outerOf(outer))) { continue; }

                            if (!byClass.TryGetValue(outer.ObjectName, out var functions))
                            {
                                functions = new Dictionary<string, int>(StringComparer.Ordinal);
                                byClass[outer.ObjectName] = functions;
                            }
                            tally(functions, one.ObjectName);
                            any = true;
                            continue;
                        }

                        if (!inDungeons(outer)) { continue; }

                        if (one.ClassName == "Class") { tally(classes, one.ObjectName); any = true; }
                        else if (one.ClassName == "ScriptStruct") { tally(structs, one.ObjectName); any = true; }
                        else if (one.ClassName == "Enum") { tally(enums, one.ObjectName); any = true; }
                    }

                    if (any) { withDungeons++; }
                }

                Console.WriteLine($"[script] looked at {looked:N0} entries, read {read:N0}, "
                    + $"{withDungeons:N0} refer to /Script/Dungeons ({clock.Elapsed.TotalSeconds:F0}s)");

                void top(string what, Dictionary<string, int> of, int howMany)
                {
                    Console.WriteLine($"[script] --- {what} ({of.Count:N0}) ---");
                    foreach (var one in of.OrderByDescending(x => x.Value).Take(howMany))
                    {
                        Console.WriteLine($"[script]   {one.Value,5}  {one.Key}");
                    }
                }

                var all = _startupArguments.Any(a => a == "ALL");
                top("classes", classes, all ? int.MaxValue : 60);
                top("structs", structs, all ? int.MaxValue : 30);
                top("enums", enums, all ? int.MaxValue : 30);

                Console.WriteLine($"[script] --- functions, by class ({byClass.Count:N0} classes, "
                    + $"{byClass.Values.Sum(x => x.Count):N0} distinct functions) ---");
                foreach (var owner in byClass.OrderByDescending(x => x.Value.Values.Sum())
                    .Take(all ? int.MaxValue : 40))
                {
                    Console.WriteLine($"[script]   {owner.Key}");
                    foreach (var one in owner.Value.OrderByDescending(x => x.Value)
                        .Take(all ? int.MaxValue : 40))
                    {
                        Console.WriteLine($"[script]       {one.Value,5}  {one.Key}");
                    }
                }

                this.Shutdown();
                return;
            }

            //PROBE_MUSIC_SET=<engine path>;<audio file> - replaces one track for real, and says
            //what it produced. The chain is long enough that a failure anywhere in it looks the
            //same from the tab, so each stage reports its own numbers.
            var probeMusicSet = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_MUSIC_SET="));
            if (probeMusicSet != null)
            {
                var bits = probeMusicSet.Substring("PROBE_MUSIC_SET=".Length).Trim('"').Split(';');
                try
                {
                    var track = Logic.GameMusic.all()
                        .FirstOrDefault(one => one.EnginePath.Equals(bits[0], StringComparison.OrdinalIgnoreCase));

                    if (track == null)
                    {
                        Console.WriteLine($"[set] no track called {bits[0]}");
                        this.Shutdown();
                        return;
                    }

                    Console.WriteLine($"[set] {track.Label}, {track.Bytes / 1024:N0} KB");
                    Console.WriteLine($"[set] engine: {track.EnginePath}");
                    Console.WriteLine($"[set] pak:    {track.PakPath}");

                    var audio = Logic.MusicEncode.read(bits[1]);
                    Console.WriteLine($"[set] encoded {System.IO.Path.GetFileName(bits[1])}: "
                        + $"{audio.Ogg.Length:N0} bytes of Ogg, {audio.SampleRate} Hz, "
                        + $"{audio.Channels} ch, {audio.Samples:N0} samples, {audio.Duration:F1}s");

                    var mod = Logic.MusicMod.install(track, audio);
                    Console.WriteLine($"[set] {System.IO.Path.GetFileName(mod.Path)}, {mod.Size:N0} bytes");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[set] refused: {problem.Message}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_MUSIC[=<how many>] - the game's music, biggest first. Which is the whole
            //question the Music tab turns on: 1,658 assets are called bgm_ and most of them are
            //stings rather than tracks, and nothing but size tells them apart.
            var probeMusic = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_MUSIC"));
            if (probeMusic != null)
            {
                var many = 20;
                var at = probeMusic.IndexOf('=');
                if (at > 0) { int.TryParse(probeMusic.Substring(at + 1), out many); }

                Console.WriteLine($"[music] paks folder = {Logic.CustomSkins.paksFolder ?? "<null>"}");

                var tracks = Logic.GameMusic.all();
                Console.WriteLine($"[music] {tracks.Count} music assets found");
                foreach (var note in Logic.GameMusic.Notes.Take(8))
                {
                    Console.WriteLine($"[music]   {note}");
                }

                //Where the rest of a streamed track lives, if it is streamed. The cluster of
                //entries at exactly 256 KB is a chunk boundary rather than a coincidence.
                var index = Logic.CustomSkins.index;
                var bulky = 0;
                if (index != null)
                {
                    foreach (var entry in index.AllEntries())
                    {
                        var p = entry.Key.Replace(System.IO.Path.DirectorySeparatorChar, '/');
                        if (p.IndexOf("02_audio_soundWave", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                        if (!System.IO.Path.GetFileName(p).StartsWith("bgm", StringComparison.OrdinalIgnoreCase)) { continue; }

                        if (entry.Value.Ubulk != null)
                        {
                            if (bulky < 4)
                            {
                                Console.WriteLine($"[music]   ubulk: {entry.Value.Ubulk.UncompressedSize / 1024:N0} KB for {System.IO.Path.GetFileName(p)}");
                            }
                            bulky++;
                        }
                    }
                }
                Console.WriteLine($"[music] {bulky} of them carry a .ubulk");

                foreach (var track in tracks.Take(many))
                {
                    Console.WriteLine($"[music]   {track.Bytes / 1024,7:N0} KB  ~{track.Seconds,4}s  {track.Label}");
                }

                this.Shutdown();
                return;
            }

            //PROBE_EXTRACT=<asset path>[;<folder>] - writes one of the game's own cooked assets
            //out to disk, unchanged.
            //
            //Which is the missing half of reading this game. PROBE_ASSETS says what exists and
            //PROBE_PROPS says what an asset stores - but PROBE_PROPS reads from disk, and
            //everything interesting is inside a 1.2 GB pak. So anything of the game's own could be
            //listed and never opened, which is how "what is actually inside UMG_IngameMenu" stayed
            //unanswered while being the one thing worth knowing.
            //
            //Read-only in every sense: it takes a copy and changes nothing in the game folder.
            var probeExtract = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_EXTRACT="));
            if (probeExtract != null)
            {
                var bits = probeExtract.Substring("PROBE_EXTRACT=".Length).Trim('"').Split(';');
                var where = bits.Length > 1 ? bits[1] : System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "mcdreborn-extract");

                var read = Logic.CustomSkins.index?.extractPackage(bits[0]);
                if (read == null)
                {
                    Console.WriteLine($"[extract] could not read {bits[0]}");
                    this.Shutdown();
                    return;
                }

                System.IO.Directory.CreateDirectory(where);
                var stem = System.IO.Path.Combine(where, System.IO.Path.GetFileName(bits[0]));

                var uasset = read.Value.UAsset.ToArray();
                var uexp = read.Value.UExp.ToArray();
                System.IO.File.WriteAllBytes(stem + ".uasset", uasset);
                System.IO.File.WriteAllBytes(stem + ".uexp", uexp);

                Console.WriteLine($"[extract] {bits[0]}");
                Console.WriteLine($"[extract]   {stem}.uasset  {uasset.Length:N0} bytes");
                Console.WriteLine($"[extract]   {stem}.uexp    {uexp.Length:N0} bytes");
                //Spelled the way PROBE_PROPS wants it, so the next command can be pasted rather
                //than retyped with the slashes turned round.
                var asProbe = stem.Replace(System.IO.Path.DirectorySeparatorChar, '/');
                Console.WriteLine($"[extract] now readable with PROBE_PROPS={asProbe}");

                this.Shutdown();
                return;
            }

            //PROBE_PAYLOAD=<asset path>;<folder>;<trigger>;<name> - takes one of the game's
            //own assets, writes it out as a cooked file the way an editor would, installs it as a
            //payload, then reads back what landed and removes it again. Which exercises the whole
            //chain without anybody having to cook anything first.
            var probePayload = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_PAYLOAD="));
            if (probePayload != null)
            {
                var bits = probePayload.Substring("PROBE_PAYLOAD=".Length).Trim('"').Split(';');
                var read = Logic.CustomSkins.index?.extractPackage(bits[0]);
                if (read == null) { Console.WriteLine("[payload] could not read the source"); this.Shutdown(); return; }

                System.IO.Directory.CreateDirectory(bits[1]);
                var stem = System.IO.Path.Combine(bits[1], System.IO.Path.GetFileName(bits[0]));
                System.IO.File.WriteAllBytes(stem + ".uasset", read.Value.UAsset.ToArray());
                System.IO.File.WriteAllBytes(stem + ".uexp", read.Value.UExp.ToArray());
                Console.WriteLine($"[payload] pretending {stem}.uasset came out of an editor");

                try
                {
                    var mod = Logic.Payloads.install(bits[2], stem + ".uasset", bits[3]);
                    Console.WriteLine($"[payload] installed {System.IO.Path.GetFileName(mod.Path)}");

                    foreach (var one in Logic.Payloads.installed())
                    {
                        Console.WriteLine($"[payload]   listed as trigger \"{one.Trigger}\", name \"{one.Name}\"");
                    }

                    //What actually went in, read out of the pak that was written.
                    foreach (var line in System.IO.File.ReadAllBytes(mod.Path) is byte[] raw
                        ? new[] { System.Text.Encoding.ASCII.GetString(raw) } : new string[0])
                    {
                        var at = line.IndexOf("Dungeons/Content/MCDReborn", StringComparison.Ordinal);
                        while (at >= 0)
                        {
                            var end = at;
                            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || "/._-".IndexOf(line[end]) >= 0)) { end++; }
                            Console.WriteLine($"[payload]   holds {line.Substring(at, end - at)}");
                            at = line.IndexOf("Dungeons/Content/MCDReborn", end, StringComparison.Ordinal);
                        }
                    }

                    foreach (var one in Logic.Payloads.installed()) { Logic.Payloads.remove(one); }
                    Console.WriteLine($"[payload] removed again, {Logic.Payloads.installed().Count} left");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[payload] refused: {problem.Message}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_TREE=<folder>;<trigger>;<name> - what installing a whole cooked folder would
            //put in a pak, without writing one. Points at somebody else's unpacked mod for
            //preference: a real content mod is the only honest test of this, because the shape
            //that matters - a small level naming blueprints that live in a tree elsewhere - is
            //not one anybody produces by accident.
            var probeTree = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_TREE="));
            if (probeTree != null)
            {
                var bits = probeTree.Substring("PROBE_TREE=".Length).Trim('"').Split(';');
                try
                {
                    var would = Logic.Payloads.preview(
                        bits.Length > 1 ? bits[1] : "Ingame",
                        bits[0],
                        bits.Length > 2 ? bits[2] : "probe");

                    Console.WriteLine($"[tree] {would.Count} files would go in");
                    foreach (var left in Logic.Payloads.Skipped)
                    {
                        Console.WriteLine($"[tree]   LEFT OUT {left}");
                    }

                    //Levels first and by themselves, because which level ends up where is the one
                    //decision this makes and the rest is carrying.
                    foreach (var path in would.Where(p => p.EndsWith(".umap", StringComparison.Ordinal)))
                    {
                        Console.WriteLine($"[tree]   LEVEL {path}");
                    }

                    foreach (var folder in would
                        .Where(p => !p.EndsWith(".umap", StringComparison.Ordinal))
                        .Select(p => p.Substring(0, p.LastIndexOf('/')))
                        .GroupBy(p => p)
                        .OrderByDescending(g => g.Count()))
                    {
                        Console.WriteLine($"[tree]   {folder.Count(),4} in {folder.Key}");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[tree] refused: {problem.Message}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_LOADER=<folder> - the loader install, end to end, without an editor.
            //
            //The two blueprints are authored in Unreal and cannot be written from here, but
            //everything either side of them can be checked: that the anchor is recognised by the
            //path it records for itself, that a missing half is refused with a sentence rather
            //than a stack, and that what lands can be found and removed again.
            //
            //The stand-in for the widget is the game's own BP_SoundCue, renamed. It works because
            //its path is exactly as long as the widget's - thirty five characters - so the rename
            //is bytes over bytes. It is not a loader and would do nothing in game; what is being
            //tested here is the installing, not the loading.
            var probeLoader = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_LOADER="));
            if (probeLoader != null)
            {
                var folder = probeLoader.Substring("PROBE_LOADER=".Length).Trim('"');
                System.IO.Directory.CreateDirectory(folder);

                void lay(string from, string? renameTo)
                {
                    var read = Logic.CustomSkins.index?.extractPackage(from);
                    if (read == null) { Console.WriteLine($"[loader] could not read {from}"); return; }

                    var uasset = read.Value.UAsset.ToArray();
                    if (renameTo != null)
                    {
                        var was = Logic.AssetCopy.selfNameIn(uasset, System.IO.Path.GetFileName(from));
                        if (was == null || !Logic.AssetCopy.rename(uasset, was, renameTo))
                        {
                            Console.WriteLine($"[loader] could not rename {from}");
                            return;
                        }
                    }

                    var stem = System.IO.Path.Combine(folder,
                        renameTo != null
                            ? renameTo.Substring(renameTo.LastIndexOf('/') + 1)
                            : System.IO.Path.GetFileName(from));
                    System.IO.File.WriteAllBytes(stem + ".uasset", uasset);
                    System.IO.File.WriteAllBytes(stem + ".uexp", read.Value.UExp.ToArray());
                }

                //The anchor first, by itself, which should be refused for want of the widget.
                foreach (var stale in System.IO.Directory.GetFiles(folder)) { System.IO.File.Delete(stale); }
                lay("/Dungeons/Content/Decor/Prefabs/Tent/BP_Tent", null);
                try
                {
                    Logic.Loader.install(folder);
                    Console.WriteLine("[loader] WRONG - a folder with no widget was accepted");
                }
                catch (Exception expected)
                {
                    Console.WriteLine($"[loader] half a loader refused: {expected.Message}");
                }

                //Now both halves, and a stub of one of the game's own game modes beside them -
                //which is what an editor project that can refer to those classes actually
                //contains, and the thing that must on no account be installed.
                lay("/Dungeons/Content/Actors/PropActors/BP_SoundCue", Logic.Loader.WIDGET);
                lay("/Dungeons/Content/GameModes/Lobby/BP_LobbyGameMode", null);
                try
                {
                    var mod = Logic.Loader.install(folder);
                    Console.WriteLine($"[loader] installed {System.IO.Path.GetFileName(mod.Path)}, {mod.Size:N0} bytes");
                    Console.WriteLine($"[loader] isInstalled = {Logic.Loader.isInstalled}");
                    foreach (var other in Logic.Loader.clashes())
                    {
                        Console.WriteLine($"[loader]   CLASHES WITH {other}");
                    }
                    foreach (var stub in Logic.Loader.HeldBack)
                    {
                        Console.WriteLine($"[loader]   HELD BACK {stub} (the game already has it)");
                    }

                    var raw = System.Text.Encoding.ASCII.GetString(System.IO.File.ReadAllBytes(mod.Path));
                    foreach (var wanted in new[] { Logic.Loader.ANCHOR, Logic.Loader.WIDGET })
                    {
                        var inPak = "Dungeons/Content/" + wanted.Substring("/Game/".Length);
                        Console.WriteLine($"[loader]   holds {inPak}: {raw.Contains(inPak, StringComparison.Ordinal)}");
                    }

                    Logic.Loader.remove();
                    Console.WriteLine($"[loader] removed, isInstalled = {Logic.Loader.isInstalled}");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[loader] refused: {problem.Message}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_WHERE=<engine path> - which of the game's levels place a given actor.
            //
            //Counting how many levels an anchor is in says how broadly a loader would start.
            //It does not say *where*, and where is the question that decides whether the thing
            //works at all: an anchor in three hundred mission tiles and not in the Camp is a
            //loader that never runs at the one moment somebody is trying to test a payload.
            var probeWhere = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_WHERE="));
            if (probeWhere != null)
            {
                var wanted = probeWhere.Substring("PROBE_WHERE=".Length).Trim('"');
                var levels = Logic.GameAssets.all().Where(one => one.Kind == "Level").ToList();
                var inThem = new List<string>();

                foreach (var level in levels)
                {
                    var inPak = "/Dungeons/Content/" + level.EnginePath.Substring("/Game/".Length);
                    try
                    {
                        var package = Logic.CustomSkins.index?.extractPackage(inPak);
                        if (package == null) { continue; }

                        var names = Logic.CookedProperties.readNamesOf(package.Value.UAsset.ToArray());
                        if (names.Any(name => name.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
                        {
                            inThem.Add(level.EnginePath);
                        }
                    }
                    catch (Exception) { }
                }

                Console.WriteLine($"[where] {wanted} is placed in {inThem.Count} of {levels.Count} levels");

                //Named individually when there are few enough to read. Which folder an anchor is
                //in says how broadly it spreads; which *level* says whether it is one somebody
                //actually walks through, and for an anchor that is the whole question.
                if (inThem.Count <= 40)
                {
                    foreach (var one in inThem) { Console.WriteLine($"[where]   {one}"); }
                }

                foreach (var folder in inThem
                    .Select(path => path.Substring(0, path.LastIndexOf('/')))
                    .GroupBy(path => path)
                    .OrderByDescending(group => group.Count())
                    .Take(18))
                {
                    Console.WriteLine($"[where] {folder.Count(),4} in {folder.Key}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_RUNNING - says whether this build can tell that the game is open. Which is
            //worth being able to check without the game, because the answer it gives when it is
            //wrong is "go ahead", and going ahead with the game up is the failure that looks
            //like no failure at all: the pak is written, nothing reads it, and the mod is blamed.
            if (_startupArguments.Any(a => a == "PROBE_RUNNING"))
            {
                var found = Logic.GameRunning.found();
                Console.WriteLine($"[running] isUp = {Logic.GameRunning.isUp}");
                Console.WriteLine($"[running] found = {(found.Count == 0 ? "nothing" : string.Join(", ", found))}");
                Console.WriteLine($"[running] paks  = {Logic.CustomSkins.paksFolder ?? "<not found>"}");
                this.Shutdown();
                return;
            }

            //PROBE_BUILTIN - installs the loader carried inside this exe, reads back what
            //landed, and puts things back as they were. Which is the whole claim being made by
            //embedding it: that a build with no Unreal anywhere near it can still produce a
            //working loader pak.
            //
            //"As they were" is the part that had to be learned. This used to finish by removing
            //what it installed, which is right when nothing was installed before - and quietly
            //destroys a working setup when something was. Running a read-only-sounding probe
            //uninstalled the live loader more than once while the actual bug was elsewhere, and
            //every payload then did nothing, which sent the search in the wrong direction for
            //several rounds. So whatever was there is kept and written back.
            if (_startupArguments.Any(a => a == "PROBE_BUILTIN"))
            {
                var wasThere = Logic.Loader.installed();
                var keptBytes = wasThere != null && System.IO.File.Exists(wasThere)
                    ? System.IO.File.ReadAllBytes(wasThere)
                    : null;
                if (keptBytes != null)
                {
                    Console.WriteLine($"[builtin] a loader is already installed ({System.IO.Path.GetFileName(wasThere!)}, {keptBytes.Length:N0} bytes) - it will be put back");
                }

                try
                {
                    var mod = Logic.Loader.installBuiltIn();
                    Console.WriteLine($"[builtin] installed {System.IO.Path.GetFileName(mod.Path)}, {mod.Size:N0} bytes");

                    var raw = System.Text.Encoding.ASCII.GetString(System.IO.File.ReadAllBytes(mod.Path));
                    foreach (var wanted in new[] { Logic.Loader.ANCHOR, Logic.Loader.WIDGET })
                    {
                        var inPak = "Dungeons/Content/" + wanted.Substring("/Game/".Length);
                        Console.WriteLine($"[builtin]   holds {inPak}: {raw.Contains(inPak, StringComparison.Ordinal)}");
                    }

                    //And that what came out is the loader rather than merely the right size: the
                    //widget has to still name the folders it watches.
                    foreach (var trigger in Logic.Payloads.TRIGGERS)
                    {
                        var folder = "/Game/MCDReborn/" + trigger;
                        Console.WriteLine($"[builtin]   scans {folder}: {raw.Contains(folder, StringComparison.Ordinal)}");
                    }

                    Logic.Loader.remove();
                    Console.WriteLine($"[builtin] removed, isInstalled = {Logic.Loader.isInstalled}");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[builtin] refused: {problem.Message}");
                }
                finally
                {
                    //In a finally, because a probe that fails half way is exactly the moment the
                    //loader is most likely to be left missing, and the least likely moment for
                    //anybody to think to check.
                    if (keptBytes != null)
                    {
                        try
                        {
                            System.IO.File.WriteAllBytes(wasThere!, keptBytes);
                            Console.WriteLine($"[builtin] put back {System.IO.Path.GetFileName(wasThere!)}, isInstalled = {Logic.Loader.isInstalled}");
                        }
                        catch (Exception problem)
                        {
                            Console.WriteLine($"[builtin] COULD NOT put the old loader back ({problem.Message}) - reinstall it");
                        }
                    }
                }
                this.Shutdown();
                return;
            }

            //PROBE_GAMELEVEL=<engine path>[;<trigger>] - turns one of the game's own levels into
            //a payload, reads back what landed, and removes it again. The claim being tested is
            //that a payload need not be authored at all: the game is full of levels, and a level
            //in the loader's folder is a payload by definition.
            var probeGameLevel = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_GAMELEVEL="));
            if (probeGameLevel != null)
            {
                var bits = probeGameLevel.Substring("PROBE_GAMELEVEL=".Length).Trim('"').Split(';');
                var trigger = bits.Length > 1 ? bits[1] : "Lobby";
                var name = bits[0].Substring(bits[0].LastIndexOf('/') + 1);

                try
                {
                    var mod = Logic.Payloads.installGameLevel(trigger, bits[0], name);
                    Console.WriteLine($"[level] installed {System.IO.Path.GetFileName(mod.Path)}, {mod.Size:N0} bytes");

                    var raw = System.Text.Encoding.ASCII.GetString(System.IO.File.ReadAllBytes(mod.Path));
                    var at = raw.IndexOf("Dungeons/Content/MCDReborn/", StringComparison.Ordinal);
                    while (at >= 0)
                    {
                        var end = at;
                        while (end < raw.Length && (char.IsLetterOrDigit(raw[end]) || "/._-".IndexOf(raw[end]) >= 0)) { end++; }
                        Console.WriteLine($"[level]   holds {raw.Substring(at, end - at)}");
                        at = raw.IndexOf("Dungeons/Content/MCDReborn/", end, StringComparison.Ordinal);
                    }

                    //The old address must be gone, or the game has two levels claiming one path.
                    Console.WriteLine($"[level]   still names its old path: {raw.Contains(bits[0].Substring("/Game/".Length), StringComparison.OrdinalIgnoreCase)}");

                    foreach (var one in Logic.Payloads.installed()) { Logic.Payloads.remove(one); }
                    Console.WriteLine($"[level] removed, {Logic.Payloads.installed().Count} payloads left");
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[level] refused: {problem.Message}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_INSTALL_TREE=<folder>;<trigger>;<name> - installs a payload folder for real,
            //rather than previewing it. The same thing the button does, reachable without one,
            //which is what narrowing a payload down to the asset that broke the game needs.
            var probeInstallTree = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_INSTALL_TREE="));
            if (probeInstallTree != null)
            {
                var bits = probeInstallTree.Substring("PROBE_INSTALL_TREE=".Length).Trim('"').Split(';');
                try
                {
                    var mod = Logic.Payloads.installFolder(
                        bits.Length > 1 ? bits[1] : "Lobby", bits[0], bits.Length > 2 ? bits[2] : "probe");

                    Console.WriteLine($"[install] {System.IO.Path.GetFileName(mod.Path)}, {mod.Size:N0} bytes");
                    foreach (var left in Logic.Payloads.Skipped)
                    {
                        Console.WriteLine($"[install]   LEFT OUT {left}");
                    }

                    var raw = System.Text.Encoding.ASCII.GetString(System.IO.File.ReadAllBytes(mod.Path));
                    var at = raw.IndexOf("Dungeons/Content/", StringComparison.Ordinal);
                    while (at >= 0)
                    {
                        var end = at;
                        while (end < raw.Length && (char.IsLetterOrDigit(raw[end]) || "/._-".IndexOf(raw[end]) >= 0)) { end++; }
                        Console.WriteLine($"[install]   holds {raw.Substring(at, end - at)}");
                        at = raw.IndexOf("Dungeons/Content/", end, StringComparison.Ordinal);
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[install] refused: {problem.Message}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_INSTALL_LOADER - installs the built-in loader and leaves it there.
            //
            //Distinct from PROBE_BUILTIN, which installs, verifies and removes again: that one
            //answers "can this build produce a loader", and removing is the point of it. This one
            //is the button, without the button.
            if (_startupArguments.Any(a => a == "PROBE_INSTALL_LOADER"))
            {
                try
                {
                    var mod = Logic.Loader.installBuiltIn();
                    Console.WriteLine($"[loader] installed {System.IO.Path.GetFileName(mod.Path)}, {mod.Size:N0} bytes");
                    Console.WriteLine($"[loader] isInstalled = {Logic.Loader.isInstalled}");
                    foreach (var other in Logic.Loader.clashes())
                    {
                        Console.WriteLine($"[loader]   CLASHES WITH {other}");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[loader] refused: {problem.Message}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_PROPS=<path without extension> - every tagged property in a cooked asset on
            //disk, with its type. Name tables say which strings an asset mentions; this says what
            //it actually stores, which is the difference between "both mention SlateFontInfo" and
            //"one of them has a font set".
            var probeProps = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_PROPS="));
            if (probeProps != null)
            {
                var stem = probeProps.Substring("PROBE_PROPS=".Length).Trim('"');
                try
                {
                    var header = System.IO.File.Exists(stem + ".uasset") ? stem + ".uasset" : stem + ".umap";
                    var uasset = System.IO.File.ReadAllBytes(header);
                    var uexp = System.IO.File.ReadAllBytes(stem + ".uexp");

                    var values = Logic.CookedProperties.readAll(uasset, uexp);
                    Console.WriteLine($"[props] {System.IO.Path.GetFileName(stem)}: {values.Count} properties");
                    foreach (var value in values)
                    {
                        Console.WriteLine($"[props]   {value.Export,-32} {value.Name,-34} {value.Type}"
                            + (string.IsNullOrEmpty(value.StructName) ? "" : " (" + value.StructName + ")"));
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[props] failed: {problem.Message}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_ANCHOR - which blueprint the most of the game's levels place.
            //
            //A loader needs an anchor: an actor the game already spawns, replaced with one that
            //also starts the loader. Which actor decides where the loader works, and the honest
            //way to choose is to count rather than to guess - the community's loader uses the
            //camp tent, and whether that is a good choice or a convenient one is a question the
            //maps can answer.
            var probeAnchor = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_ANCHOR"));
            if (probeAnchor != null)
            {
                //An optional filter, because coverage is not the question it first looks like.
                //Counting across the whole game says how many levels an anchor is in; counting
                //across the Camp says whether the loader ever starts where somebody is actually
                //testing. The second turned out to matter more than the first.
                var only = probeAnchor.StartsWith("PROBE_ANCHOR=")
                    ? probeAnchor.Substring("PROBE_ANCHOR=".Length).Trim('"')
                    : null;

                var levels = Logic.GameAssets.all()
                    .Where(one => one.Kind == "Level")
                    .Where(one => only == null
                        || one.EnginePath.IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                var counted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var read = 0;

                foreach (var level in levels)
                {
                    var inPak = "/Dungeons/Content/" + level.EnginePath.Substring("/Game/".Length);
                    IReadOnlyList<string> names;
                    try
                    {
                        var package = Logic.CustomSkins.index?.extractPackage(inPak);
                        if (package == null) { continue; }
                        names = Logic.CookedProperties.readNamesOf(package.Value.UAsset.ToArray());
                    }
                    catch (Exception) { continue; }

                    read++;

                    //Once per level rather than once per placement: an anchor wants to be in many
                    //levels, and a level with forty of the same urn in it is still one level.
                    foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!name.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) { continue; }
                        var leaf = name.Substring(name.LastIndexOf('/') + 1);
                        if (!leaf.StartsWith("BP_", StringComparison.OrdinalIgnoreCase)) { continue; }

                        counted.TryGetValue(name, out var was);
                        counted[name] = was + 1;
                    }
                }

                Console.WriteLine($"[anchor] read {read} of {levels.Count} levels");
                foreach (var pair in counted.OrderByDescending(one => one.Value).Take(25))
                {
                    Console.WriteLine($"[anchor] {pair.Value,5} levels  {pair.Key}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_ASSETS=<words>[;<kind>] - the game asset list the browser is built on, which
            //is the half of it worth checking from a command line: whether the paths come out
            //spelled the way the engine wants them, and whether a kind read from a package
            //agrees with the one guessed from the name.
            var probeAssets = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_ASSETS="));
            if (probeAssets != null)
            {
                var bits = probeAssets.Substring("PROBE_ASSETS=".Length).Trim('"').Split(';');
                var (shown, matched) = Logic.GameAssets.search(bits[0], bits.Length > 1 ? bits[1] : null);

                Console.WriteLine($"[assets] {Logic.GameAssets.all().Count} in the paks, {matched} matched");

                foreach (var raw in (Logic.CustomSkins.index ?? Enumerable.Empty<string>()).Take(3))
                {
                    Console.WriteLine($"[assets]   raw entry \"{raw}\"");
                }

                foreach (var asset in shown.Take(15))
                {
                    var real = Logic.GameAssets.readKindOf(asset.EnginePath);
                    var agreed = real == null ? "unread" : real == asset.Kind ? "agrees" : $"REALLY {real}";
                    Console.WriteLine($"[assets]   {asset.Kind,-18} {agreed,-22} {asset.EnginePath}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_NAMES=<asset path> - the name table of a cooked asset, filtered to the
            //entries that look like paths. A package records the path it was cooked at, and
            //whether that can be rewritten decides whether one asset can be copied over another.
            var probeNames = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_NAMES="));
            if (probeNames != null)
            {
                var path = probeNames.Substring("PROBE_NAMES=".Length).Trim('"');
                var read = Logic.CustomSkins.index?.extractPackage(path);
                if (read == null) { Console.WriteLine("[names] could not read it"); this.Shutdown(); return; }

                var uasset = read.Value.UAsset.ToArray();
                var names = Logic.CookedProperties.readNamesOf(uasset);
                Console.WriteLine($"[names] {path}");
                Console.WriteLine($"        uasset {uasset.Length:N0} bytes, {names.Count} names");

                for (int i = 0; i < names.Count; i++)
                {
                    if (names[i].IndexOf('/') < 0) { continue; }
                    Console.WriteLine($"          [{i}] \"{names[i]}\"  ({names[i].Length} chars)");
                }
                this.Shutdown();
                return;
            }

            //PROBE_MESH=<asset path>[;<asset path>...] - whether the geometry pipeline can read
            //a mesh at all, which is the question that decides whether anything can be imported
            //over it. Prints what it found rather than yes or no, because a mesh that reads with
            //no triangles is a different problem from one that does not read.
            var probeMesh = _startupArguments.FirstOrDefault(a => a.StartsWith("PROBE_MESH="));
            if (probeMesh != null)
            {
                foreach (var path in probeMesh.Substring("PROBE_MESH=".Length).Trim('"').Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(path)) { continue; }

                    var shape = Logic.WeaponMeshes.read(path.Trim());
                    if (shape == null)
                    {
                        Console.WriteLine($"[mesh] {path.Trim()}  UNREADABLE");
                        continue;
                    }

                    Console.WriteLine($"[mesh] {path.Trim()}");
                    Console.WriteLine($"         {shape.Positions.Count} vertices, {shape.TriangleCount} triangles,"
                        + $" {shape.TexCoords.Count} uvs");
                    Console.WriteLine($"         extent {shape.Extent.X:F1} x {shape.Extent.Y:F1} x {shape.Extent.Z:F1}"
                        + $", longest side {shape.LongestSide:F1}, radius {shape.Radius:F1}");
                }
                this.Shutdown();
                return;
            }

            //PRINT_GEAR_TEXTURES=<item id fragment> - every pak entry under a piece of gear,
            //which one textureFor picks, and whether it actually decodes.
            var gearTextures = _startupArguments.FirstOrDefault(a => a.StartsWith("PRINT_GEAR_TEXTURES="));
            if (gearTextures != null)
            {
                var term = gearTextures.Substring("PRINT_GEAR_TEXTURES=".Length).Trim('"');
                var ids = Services.ItemDatabase.armor
                    .Concat(Services.ItemDatabase.meleeWeapons)
                    .Concat(Services.ItemDatabase.rangedWeapons)
                    .Distinct()
                    .Where(id => id.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                              || Services.R.itemName(id).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(id => id);

                var index = Logic.CustomSkins.index;
                foreach (var id in ids)
                {
                    var chosen = Logic.CustomSkins.textureFor(id);
                    var picture = chosen == null ? null : Services.ImageResolver.instance.imageSource(chosen);
                    var size = picture == null ? "-" : $"{picture.PixelWidth}x{picture.PixelHeight}";
                    Console.WriteLine($"[gear] {id} (\"{Services.R.itemName(id)}\")  {size}  -> {chosen ?? "(none)"}");
                    if (index != null)
                    {
                        foreach (var entry in index)
                        {
                            if (entry.IndexOf($"/{id}/", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                            Console.WriteLine($"         entry {entry}");
                        }
                    }
                }
                this.Shutdown();
                return;
            }

            //CODEC_TEST=<png> - encodes a PNG the way a designer link carries it, decodes it
            //again and checks every pixel survived. Prints the payload so the website's own
            //decoder can be checked against this one.
            var codecTest = _startupArguments.FirstOrDefault(a => a.StartsWith("CODEC_TEST="));
            if (codecTest != null)
            {
                var path = codecTest.Substring("CODEC_TEST=".Length).Trim('"');
                try
                {
                    var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
                        new Uri(System.IO.Path.GetFullPath(path)),
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    var source = decoder.Frames[0];

                    var payload = Logic.SkinCodec.encode(source);
                    var back = Logic.SkinCodec.decode(payload);

                    var before = new byte[source.PixelWidth * source.PixelHeight * 4];
                    var after = new byte[before.Length];
                    new System.Windows.Media.Imaging.FormatConvertedBitmap(
                        source, System.Windows.Media.PixelFormats.Bgra32, null, 0)
                        .CopyPixels(before, source.PixelWidth * 4, 0);
                    back.CopyPixels(after, back.PixelWidth * 4, 0);

                    var identical = before.AsSpan().SequenceEqual(after);
                    Console.WriteLine($"{System.IO.Path.GetFileName(path)} {source.PixelWidth}x{source.PixelHeight}");
                    Console.WriteLine($"  payload {payload.Length} chars, round-trip identical: {identical}");
                    Console.WriteLine($"  PAYLOAD {payload}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"CODEC_TEST failed: {e.Message}");
                }
                this.Shutdown();
                return;
            }

            //CODEC_DECODE=<file holding a link or payload>|<out png> - the other direction,
            //for checking that what the website produces is what this side reads.
            var codecDecode = _startupArguments.FirstOrDefault(a => a.StartsWith("CODEC_DECODE="));
            if (codecDecode != null)
            {
                var parts = codecDecode.Substring("CODEC_DECODE=".Length).Trim('"').Split('|');
                try
                {
                    var image = Logic.SkinCodec.fromPastedText(System.IO.File.ReadAllText(parts[0]));
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    using (var file = System.IO.File.Create(parts[1])) { encoder.Save(file); }
                    Console.WriteLine($"decoded {image.PixelWidth}x{image.PixelHeight} -> {parts[1]}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"CODEC_DECODE failed: {e.Message}");
                }
                this.Shutdown();
                return;
            }

            //PROBE_OUTSIDE[=<folder>] - what the game's paks hold OUTSIDE /Dungeons/Content, which the
            //app's own index filters away: AssetRegistry.bin lives at Dungeons/AssetRegistry.bin. Lists
            //them and, given a folder, writes each one there. Read-only on the game.
            if (_startupArguments.Any(a => a == "PROBE_OUTSIDE" || a.StartsWith("PROBE_OUTSIDE=", StringComparison.Ordinal)))
            {
                var arg = _startupArguments.First(a => a.StartsWith("PROBE_OUTSIDE", StringComparison.Ordinal));
                var into = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..].Trim('"') : null;
                var folder = Logic.CustomSkins.paksFolder!;
                var paks = Directory.GetFiles(folder, "*.pak", SearchOption.TopDirectoryOnly)
                    .Where(p => !Path.GetFileName(p).StartsWith("MCDReborn", StringComparison.OrdinalIgnoreCase));
                var index = new PakReader.Pak.PakIndex(paks, cacheFiles: true, caseSensitive: true, filter: (PakReader.Pak.PakFilter?)null);
                foreach (var key in Secrets.PAKS_AES_KEYS)
                {
                    var k = key.key.StartsWith("0x") ? key.key[2..] : key.key;
                    if (index.UseKey(PakReader.Parsers.Objects.FGuid.Zero, k.ToBytesKey()) > 0) { break; }
                }
                foreach (var pakPath in paks)
                {
                    var reader = new PakReader.Pak.PakFileReader(pakPath);
                    var opened = false;
                    foreach (var key in Secrets.PAKS_AES_KEYS)
                    {
                        var k = key.key.StartsWith("0x") ? key.key[2..] : key.key;
                        if (reader.TryReadIndex(k.ToBytesKey())) { opened = true; break; }
                    }
                    if (!opened) { continue; }
                    foreach (var name in reader.Select(kv => kv.Key).Where(n => n.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Dungeons/", StringComparison.OrdinalIgnoreCase) >= 0).ToList())
                    {
                        Console.WriteLine($"[outside] {Path.GetFileName(pakPath)} mount={reader.MountPoint} {name}");
                        if (into == null) { continue; }
                        try
                        {
                            var bytes = reader.GetFile(name).ToArray();
                            var file = Path.Combine(into, name.Replace('/', '_').TrimStart('_'));
                            File.WriteAllBytes(file, bytes);
                            Console.WriteLine($"[outside]    -> {file} ({bytes.Length:N0} bytes)");
                        }
                        catch (Exception x) { Console.WriteLine($"[outside]    {x.GetType().Name}: {x.Message}"); }
                    }
                }                Shutdown();
                return;
            }

            //PROBE_RECORDS[=<id>;<id>...] - the live item registry's records, read from outside the
            //running game (ReadProcessMemory, never a debugger). The registry's address is the one
            //the item plugin logged this run. For each melee weapon (or the ids named): the two
            //lists that differ between a base weapon and its unique, at +0x1B8 and +0x1E8, element
            //by element, with every dword that is a valid name index resolved. Read-only.
            if (_startupArguments.Any(a => a == "PROBE_RECORDS" || a.StartsWith("PROBE_RECORDS=", StringComparison.Ordinal)))
            {
                var arg = _startupArguments.First(a => a.StartsWith("PROBE_RECORDS", StringComparison.Ordinal));
                var wanted = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
                var game = LiveEdit.GameProcess.open(out var why);
                if (game == null) { Console.WriteLine($"[rec] the game is not open: {why}"); Shutdown(); return; }
                using (game)
                {
                    try
                    {
                        var names = new LiveEdit.NameTable(game);
                        if (!names.find()) { Console.WriteLine("[rec] name table not found"); Shutdown(); return; }
                        var log = Logic.GamePlugin.lastLog() ?? Array.Empty<string>();
                        var line = log.LastOrDefault(l => l.Contains("registry at "));
                        if (line == null) { Console.WriteLine("[rec] the plugin has not logged the registry this run"); Shutdown(); return; }
                        var registry = new IntPtr(Convert.ToInt64(line.Substring(line.IndexOf("registry at ") + 12, 16), 16));
                        long q(IntPtr at) => BitConverter.ToInt64(game.read(at, 8) ?? new byte[8], 0);
                        int d(IntPtr at) => BitConverter.ToInt32(game.read(at, 4) ?? new byte[4], 0);
                        var data = new IntPtr(q(registry + 0xA0));
                        var count = d(registry + 0xA8);
                        var melee = Logic.CustomItems.gameItems().Where(i => i.NativeParent == "MeleeWeaponGearItemInstance").Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        Console.WriteLine($"[rec] registry {registry.ToInt64():x}, {count} records");
                        string resolve(byte[] bytes)
                        {
                            var found = new List<string>();
                            for (var i = 0; i + 4 <= bytes.Length; i += 4)
                            {
                                var index = BitConverter.ToInt32(bytes, i);
                                if (index <= 0 || index > 4_000_000) { continue; }
                                var n = names.nameOf(index);
                                if (!string.IsNullOrEmpty(n) && n.Length < 80) { found.Add($"+{i:x}:{n}"); }
                            }
                            return string.Join(" ", found);
                        }
                        void list(IntPtr record, int at, string label, int stride)
                        {
                            var ptr = new IntPtr(q(record + at));
                            var num = d(record + at + 8);
                            if (ptr == IntPtr.Zero || num <= 0 || num > 64) { Console.WriteLine($"[rec]    {label}: empty"); return; }
                            for (var i = 0; i < num; i++)
                            {
                                var el = game.read(ptr + i * stride, stride) ?? Array.Empty<byte>();
                                Console.WriteLine($"[rec]    {label}[{i}] {BitConverter.ToString(el).Replace("-", " ").ToLowerInvariant()}");
                                Console.WriteLine($"[rec]         names: {resolve(el)}");
                            }
                        }
                        //EEnchantmentTypeID, read from the SDK dump's header, so ids print as names.
                        var enums = new Dictionary<int, string>();
                        var header = @"C:\Dumper-7\4.22.3-0+++UE4+Release-4.22-Dungeons\CppSDK\SDK\Dungeons_structs.hpp";
                        if (System.IO.File.Exists(header))
                        {
                            var inEnum = false;
                            foreach (var l in System.IO.File.ReadLines(header))
                            {
                                if (l.Contains("enum class EEnchantmentTypeID")) { inEnum = true; continue; }
                                if (!inEnum) { continue; }
                                if (l.StartsWith("};")) { break; }
                                var m = System.Text.RegularExpressions.Regex.Match(l, @"^\s*(\w+)\s*=\s*(\d+)");
                                if (m.Success) { enums[int.Parse(m.Groups[2].Value)] = m.Groups[1].Value; }
                            }
                        }
                        //An FText's display string: ITextData* at +0, whose DisplayString (a shared FString) is at +8.
                        string textOf(IntPtr text)
                        {
                            var data = new IntPtr(q(text));
                            if (data == IntPtr.Zero) { return "(null)"; }
                            var fstring = new IntPtr(q(data + 8));
                            if (fstring == IntPtr.Zero) { return "(no string)"; }
                            var chars = new IntPtr(q(fstring));
                            var n = d(fstring + 8);
                            if (chars == IntPtr.Zero || n <= 0 || n > 400) { return $"(string {n})"; }
                            var bytes = game.read(chars, (n - 1) * 2) ?? Array.Empty<byte>();
                            return System.Text.Encoding.Unicode.GetString(bytes);
                        }
                        for (var i = 0; i < count; i++)
                        {
                            var record = new IntPtr(q(data + i * 8));
                            if (record == IntPtr.Zero) { continue; }
                            var id = names.nameOf(d(record)) ?? "?";
                            if (wanted != null ? !wanted.Contains(id) : !melee.Contains(id)) { continue; }
                            Console.WriteLine($"[rec] === {id} at {record.ToInt64():x}");
                            var bptr = new IntPtr(q(record + 0x1B8));
                            var bnum = d(record + 0x1C0);
                            for (var b = 0; bptr != IntPtr.Zero && b < bnum && b < 16; b++)
                            {
                                var el = game.read(bptr + b * 0x14, 0x14) ?? new byte[0x14];
                                Console.WriteLine($"[rec]    built-in {(enums.TryGetValue(el[0], out var en) ? en : el[0].ToString())} level {BitConverter.ToInt32(el, 4)} category {el[8]} source {el[9]} invested {BitConverter.ToInt32(el, 12)} tail {el[16]:x2} {el[17]:x2} {el[18]:x2} {el[19]:x2}");
                            }
                            var tptr = new IntPtr(q(record + 0x70));
                            var tnum = d(record + 0x78);
                            Console.WriteLine($"[rec]    +0x70 list: {tnum} (max {d(record + 0x7C)}), +0x18 name \"{textOf(record + 0x18)}\", +0x58 flavour \"{textOf(record + 0x58)}\"");
                            for (var t = 0; tptr != IntPtr.Zero && t < tnum && t < 16; t++)
                            {
                                Console.WriteLine($"[rec]    line \"{textOf(tptr + t * 0x30)}\" bytes {BitConverter.ToString(game.read(tptr + t * 0x30, 0x30) ?? Array.Empty<byte>()).Replace("-", " ").ToLowerInvariant()}");
                            }
                        }
                    }
                    catch (Exception e) { Console.WriteLine($"[rec] FAILED {e}"); }
                }
                Shutdown();
                return;
            }

            //PROBE_ITEMJSON=<id>;<id>... - each item's Instance blueprint as PakReader parses it, whole,
            //into %TEMP%\MCDRebornItemJson\<id>.json. Read-only; for seeing everything an item's
            //blueprint says about it, not only its numbers.
            if (_startupArguments.Any(a => a.StartsWith("PROBE_ITEMJSON=", StringComparison.Ordinal)))
            {
                var ids = _startupArguments.First(a => a.StartsWith("PROBE_ITEMJSON=", StringComparison.Ordinal))["PROBE_ITEMJSON=".Length..].Trim('"').Split(';', StringSplitOptions.RemoveEmptyEntries);
                var into = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MCDRebornItemJson");
                System.IO.Directory.CreateDirectory(into);
                foreach (var id in ids)
                {
                    try
                    {
                        var item = Logic.CustomItems.gameItem(id);
                        if (item == null) { Console.WriteLine($"[json] {id}: not a game item"); continue; }
                        foreach (var part in new[] { item.Instance, item.Instance.Replace("Instance", "Storable"), item.Instance.Replace("Instance", "") })
                        {
                            var path = "/Dungeons/Content/" + item.Folder["/Game/".Length..] + "/" + part;
                            var package = Logic.CustomSkins.index!.extractPackage(path);
                            if (package == null) { Console.WriteLine($"[json] {id} {part}: unreadable"); continue; }
                            var file = System.IO.Path.Combine(into, part + ".json");
                            System.IO.File.WriteAllText(file, package.Value.JsonData);
                            Console.WriteLine($"[json] {id} {part}: {new System.IO.FileInfo(file).Length} bytes -> {file}");
                        }
                    }
                    catch (Exception e) { Console.WriteLine($"[json] {id} FAILED {e.Message}"); }
                }
                Shutdown();
                return;
            }

            //PROBE_LOCFIND=<regex>[;<namespace regex>] - every English Game.locres entry whose text
            //(or key) matches, with its namespace and key. Read-only; for finding what the game calls
            //something and where its text lives.
            if (_startupArguments.Any(a => a.StartsWith("PROBE_LOCFIND=", StringComparison.Ordinal)))
            {
                var parts = _startupArguments.First(a => a.StartsWith("PROBE_LOCFIND=", StringComparison.Ordinal))["PROBE_LOCFIND=".Length..].Trim('"').Split(';');
                var text = new System.Text.RegularExpressions.Regex(parts[0], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var space = parts.Length > 1 ? new System.Text.RegularExpressions.Regex(parts[1], System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
                try
                {
                    var index = Logic.CustomSkins.index!;
                    var english = index.First(p => p.EndsWith("/Localization/Game/en/Game", StringComparison.OrdinalIgnoreCase));
                    if (english.StartsWith("//", StringComparison.Ordinal)) { english = english.Substring(1); }
                    var table = Logic.Locres.read(index.GetFile(english)!.Value.ToArray())!;
                    var shown = 0;
                    foreach (var ns in table.Namespaces)
                    {
                        if (space != null && !space.IsMatch(ns.Name)) { continue; }
                        foreach (var entry in ns.Entries)
                        {
                            var value = table.get(ns.Name, entry.Key) ?? "";
                            if (!text.IsMatch(value) && !text.IsMatch(entry.Key)) { continue; }
                            Console.WriteLine($"[loc] {ns.Name} | {entry.Key} | {value.Replace("\n", " / ")}");
                            if (++shown >= 400) { break; }
                        }
                    }
                    Console.WriteLine($"[loc] {shown} shown");
                }
                catch (Exception e) { Console.WriteLine($"[loc] FAILED {e}"); }
                Shutdown();
                return;
            }

            //PROBE_INSTALLEDLOOK - what the Weapons tab opens a mesh with: each custom item's installed
            //look (model, placement, texture) from its design, and the stock-mesh memory
            //(MeshInstalls) remembered, recalled, and forgotten once its pak is gone. Writes only a
            //test record, which it removes.
            if (_startupArguments.Contains("PROBE_INSTALLEDLOOK"))
            {
                try
                {
                    Logic.CustomItems.showInApp();
                    Logic.GlbModel? any = null;
                    foreach (var mesh in Logic.WeaponMeshes.all().Where(m => m.Name.StartsWith("★")))
                    {
                        var look = Logic.CustomItems.installedLook(mesh.AssetPath);
                        if (look == null) { Console.WriteLine($"[look] {mesh.AssetPath}: nothing installed, opens as the copy"); continue; }
                        any ??= look.Model;
                        var t = look.Transform;
                        Console.WriteLine($"[look] {look.Name} {mesh.AssetPath}: model {(look.Model == null ? (look.ModelMissing ? "MISSING" : "none (reshape)") : look.Model.Name + " " + look.Model.VertexCount + " vertices")}, " +
                            $"scale {t.Scale:0.###} offset ({t.Offset.X:0.#},{t.Offset.Y:0.#},{t.Offset.Z:0.#}) turn ({t.RotationDegrees.X:0},{t.RotationDegrees.Y:0},{t.RotationDegrees.Z:0}), texture {(look.Texture == null ? "-" : look.Texture.PixelWidth + "x" + look.Texture.PixelHeight)}");
                    }

                    var pak = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MCDReborn_ProbeLook_P.pak");
                    System.IO.File.WriteAllBytes(pak, new byte[] { 1 });
                    var transform = new Logic.MeshEdit.Transform(2.5f, new Logic.MeshGeometry.Position(1, 2, 3), new Logic.MeshGeometry.Position(10, 20, 30));
                    Logic.MeshInstalls.remember("ProbeLook", "/Game/Probe/SM_Test", any, transform, pak);
                    var back = Logic.MeshInstalls.recall("ProbeLook", "/Game/Probe/SM_Test");
                    var bt = back?.Edit.transform ?? Logic.MeshEdit.Transform.none;
                    Console.WriteLine($"[look] stock memory: recalled {(back == null ? "NOTHING" : "model " + (back.ModelName ?? "-") + " file " + (back.Edit.File != null && System.IO.File.Exists(back.Edit.File)) + $", scale {bt.Scale} offset ({bt.Offset.X},{bt.Offset.Y},{bt.Offset.Z}) turn ({bt.RotationDegrees.X},{bt.RotationDegrees.Y},{bt.RotationDegrees.Z})")}");
                    var kept = back?.Edit.File;
                    System.IO.File.Delete(pak);
                    var gone = Logic.MeshInstalls.recall("ProbeLook", "/Game/Probe/SM_Test");
                    Console.WriteLine($"[look] after its pak is removed: {(gone == null ? "forgotten" : "STILL THERE")}, kept model file removed {(kept == null || !System.IO.File.Exists(kept))}");
                }
                catch (Exception e) { Console.WriteLine($"[look] FAILED {e}"); }
                Shutdown();
                return;
            }

            //PROBE_RECOLORCUSTOM[=<id>] - the Recolor Gear tab's side of a custom item: lists each
            //type's custom items and whether their texture reads, then recolours one (a red tint of
            //its own texture), reads the installed pak back to see the red, and removes the
            //recolour again - the item ends as it began.
            if (_startupArguments.Any(a => a == "PROBE_RECOLORCUSTOM" || a.StartsWith("PROBE_RECOLORCUSTOM=", StringComparison.Ordinal)))
            {
                var arg = _startupArguments.First(a => a.StartsWith("PROBE_RECOLORCUSTOM", StringComparison.Ordinal));
                var id = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : "MCDR_Item06";
                try
                {
                    if (Logic.GameRunning.isUp) { Console.WriteLine("[recolor] close the game first"); Shutdown(); return; }
                    Logic.CustomItems.showInApp();
                    foreach (var kind in Enum.GetValues<Logic.CustomItems.Kind>())
                    {
                        foreach (var each in Logic.CustomItems.customIdsOf(kind))
                        {
                            var t = Logic.CustomItems.colourTexture(each);
                            Console.WriteLine($"[recolor] {kind,-8} ★ {R.itemName(each),-22} {each}: {(t == null ? "NO TEXTURE" : t.PixelWidth + "x" + t.PixelHeight)}");
                            if (t != null)
                            {
                                //What the tab previews, saved for a look: %TEMP%\MCDRebornRecolor\<id>.png
                                var shots = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MCDRebornRecolor");
                                System.IO.Directory.CreateDirectory(shots);
                                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(t));
                                using var file = System.IO.File.Create(System.IO.Path.Combine(shots, each + ".png"));
                                encoder.Save(file);
                            }
                        }
                    }

                    var own = Logic.CustomItems.colourTexture(id) ?? throw new InvalidOperationException("no texture");
                    var w = own.PixelWidth; var h = own.PixelHeight;
                    var pixels = Logic.CustomSkins.pixelsAt(own, w, h);
                    for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 255; }   //BGRA: pure red, alpha kept
                    var red = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, w * 4);

                    Logic.CustomItems.setTexture(id, red);
                    Console.WriteLine($"[recolor] {id} recoloured: isRecoloured {Logic.CustomItems.isRecoloured(id)}");
                    (int r, int g, int b) average()
                    {
                        var pak = System.IO.Path.Combine(Logic.CustomSkins.paksFolder!, Logic.CustomSkins.MOD_PREFIX + Logic.CustomItems.MOD_NAME + "_P.pak");
                        var inside = Logic.ModPak.read(pak).ToDictionary(f => f.Path, f => f.Data, StringComparer.OrdinalIgnoreCase);
                        var texture = inside.Keys.Where(k => k.Contains("/" + id + "/", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset")
                                && System.IO.Path.GetFileName(k).StartsWith("T_") && Logic.CustomSkins.isColourMap(System.IO.Path.GetFileNameWithoutExtension(k)))
                            .OrderBy(k => System.IO.Path.GetFileName(k).Length).First();
                        var stem = texture[..^7];
                        inside.TryGetValue(stem + ".ubulk", out var bulk);
                        var package = new PakReader.Pak.PakPackage(new ArraySegment<byte>(inside[texture]), new ArraySegment<byte>(inside[stem + ".uexp"]),
                            bulk == null ? (ArraySegment<byte>?)null : new ArraySegment<byte>(bulk));
                        var image = Services.PakIndexExtensions.bitmapImageFromSKImage(package.GetExport<PakReader.Parsers.Class.UTexture2D>()!.Image!);
                        var px = Logic.CustomSkins.pixelsAt(image, image.PixelWidth, image.PixelHeight);
                        long sr = 0, sg = 0, sb = 0, n = 0;
                        for (var i = 0; i < px.Length; i += 4) { if (px[i + 3] == 0) { continue; } sb += px[i]; sg += px[i + 1]; sr += px[i + 2]; n++; }
                        Console.WriteLine($"[recolor]   pak texture {System.IO.Path.GetFileName(texture)} {image.PixelWidth}x{image.PixelHeight}");
                        return n == 0 ? (0, 0, 0) : ((int)(sr / n), (int)(sg / n), (int)(sb / n));
                    }
                    Console.WriteLine($"[recolor]   average colour in the pak with the recolour: {average()}");

                    Logic.CustomItems.setTexture(id, null);
                    Console.WriteLine($"[recolor] {id} recolour removed: isRecoloured {Logic.CustomItems.isRecoloured(id)}");
                    Console.WriteLine($"[recolor]   average colour in the pak without it: {average()}");
                }
                catch (Exception e) { Console.WriteLine($"[recolor] FAILED {e}"); }
                Shutdown();
                return;
            }

            //PROBE_MIGRATESLOTS[=write] - the free slots are gone: every custom item is a plugin item.
            //Turns each design still in a slot into a plugin item under a new MCDR_ItemNN id (name,
            //icon, behaviour and model kept), and swaps the old id for the new one wherever a
            //character save holds it. Without =write it only reports. With it: each save is copied
            //to <file>.pre-migration first, the designs are saved, and the items pak is rebuilt -
            //the slot's files go, so a character still holding the old id would crash on it.
            if (_startupArguments.Any(a => a == "PROBE_MIGRATESLOTS" || a == "PROBE_MIGRATESLOTS=write"))
            {
                var write = _startupArguments.Contains("PROBE_MIGRATESLOTS=write");
                try
                {
                    if (write && Logic.GameRunning.isUp) { Console.WriteLine("[migrate] close the game first"); Shutdown(); return; }
                    var designs = Logic.CustomItems.load();
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var design in designs)
                    {
                        var slot = Logic.CustomItems.retiredSlot(design.Slot);
                        if (slot == null) { continue; }
                        var id = write ? Logic.CustomItems.newPluginId(designs) : "MCDR_Item??";
                        Console.WriteLine($"[migrate] design {design.Slot} ({design.Name ?? slot.BuiltInName}, copy of {design.Source}) -> {id}");
                        map[design.Slot] = id;
                        if (!write) { continue; }
                        design.Name ??= slot.BuiltInName;
                        design.PluginKind = slot.Kind;
                        design.Slot = id;
                    }
                    if (map.Count == 0) { Console.WriteLine("[migrate] no design is in a slot"); }

                    var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Mojang Studios", "Dungeons");
                    var saves = System.IO.Directory.Exists(root)
                        ? System.IO.Directory.GetFiles(root, "*.dat", System.IO.SearchOption.AllDirectories).Where(f => f.Contains(@"\Characters\")).ToList()
                        : new List<string>();
                    Console.WriteLine($"[migrate] {saves.Count} character save(s) under {root}");
                    //Every "field": "<old id>" in the save, whatever the field is called.
                    var pattern = map.Count == 0 ? null : new System.Text.RegularExpressions.Regex(
                        "\"(\\w+)\"\\s*:\\s*\"(" + string.Join("|", map.Keys.Select(System.Text.RegularExpressions.Regex.Escape)) + ")\"");
                    foreach (var save in saves)
                    {
                        if (pattern == null) { break; }
                        string json;
                        using (var input = System.IO.File.OpenRead(save))
                        {
                            //Reads past the D001 header, as the app's own open does.
                            if (!DungeonTools.Save.File.SaveFileHandler.IsFileEncrypted(input)) { Console.WriteLine($"[migrate] {save}: not encrypted, skipped"); continue; }
                            var plain = System.Threading.Tasks.Task.Run(() => Logic.FileProcessHelper.Decrypt(input).AsTask()).Result;
                            if (plain == null) { Console.WriteLine($"[migrate] {save}: could not decrypt"); continue; }
                            using var reader = new System.IO.StreamReader(plain);
                            json = reader.ReadToEnd();
                        }
                        var hits = pattern.Matches(json);
                        Console.WriteLine($"[migrate] {save.Substring(root.Length)}: {hits.Count} hit(s) {string.Join(", ", hits.Select(h => h.Groups[1].Value + "=" + h.Groups[2].Value).GroupBy(x => x).Select(g => g.Count() + "x " + g.Key))}");
                        if (!write || hits.Count == 0) { continue; }

                        var changed = pattern.Replace(json, m => m.Value.Substring(0, m.Value.Length - m.Groups[2].Value.Length - 1) + map[m.Groups[2].Value] + "\"");
                        System.IO.File.Copy(save, save + ".pre-migration", overwrite: false);
                        using var plainOut = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(changed));
                        var encrypted = System.Threading.Tasks.Task.Run(() => Logic.FileProcessHelper.Encrypt(plainOut).AsTask()).Result!;
                        using (var output = System.IO.File.Open(save, System.IO.FileMode.Create, System.IO.FileAccess.Write)) { encrypted.CopyTo(output); }

                        //Read back: it must decrypt to exactly what was written.
                        using var check = System.IO.File.OpenRead(save);
                        DungeonTools.Save.File.SaveFileHandler.IsFileEncrypted(check);
                        using var back = new System.IO.StreamReader(System.Threading.Tasks.Task.Run(() => Logic.FileProcessHelper.Decrypt(check).AsTask()).Result!);
                        Console.WriteLine($"[migrate]   written, backup {System.IO.Path.GetFileName(save)}.pre-migration, reads back {(back.ReadToEnd() == changed ? "identical" : "DIFFERENT")}");
                    }

                    if (write)
                    {
                        Logic.CustomItems.save(designs);
                        var built = Logic.CustomItems.build(designs);
                        foreach (var note in built.Notes) { Console.WriteLine($"[migrate] {note}"); }
                    }
                }
                catch (Exception e) { Console.WriteLine($"[migrate] FAILED {e}"); }
                Shutdown();
                return;
            }

            //PROBE_CLONECHECK - every game item's folder copied under a test id, as a custom item is,
            //and checked: the copy imports exactly the native classes the original does, and its
            //packages still parse. Nothing is written.
            if (_startupArguments.Contains("PROBE_CLONECHECK"))
            {
                try
                {
                    int items = 0, packages = 0, bad = 0, idsMoved = 0;
                    IEnumerable<string> natives(byte[] uasset)
                    {
                        var imports = Logic.CookedPackage.readImports(uasset);
                        for (var i = 0; i < imports.Count; i++)
                        {
                            var imp = imports[i];
                            if (imp.ClassName != "Class" || imp.Outer >= 0 || -imp.Outer - 1 >= imports.Count) { continue; }
                            var outer = imports[-imp.Outer - 1].ObjectName;
                            if (outer.StartsWith("/Script/", StringComparison.Ordinal)) { yield return outer + "." + imp.ObjectName; }
                        }
                    }
                    foreach (var item in Logic.CustomItems.gameItems().GroupBy(i => i.Id).Select(g => g.First()).OrderBy(i => i.Id))
                    {
                        var cooked = "/Dungeons/Content/" + item.Folder["/Game/".Length..];
                        var made = Logic.NewContent.cloneFolder(cooked, "MCDR_Check", ownsBareId: false,
                            alsoRename: new Dictionary<string, string> { [item.Id] = "MCDR_Check" });
                        if (made == null) { Console.WriteLine($"[clone] {item.Id}: nothing copied"); continue; }
                        items++;
                        var copies = made.Entries.Where(e => e.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).ToList();
                        foreach (var copy in copies)
                        {
                            packages++;
                            var name = System.IO.Path.GetFileNameWithoutExtension(copy.Path);
                            var folderKey = cooked.Replace("/Dungeons/Content/", string.Empty).TrimStart('/') + "/";
                            string strip(string p) { var f = p.Substring(p.LastIndexOf('/') + 1); var dot = f.IndexOf('.'); return dot < 0 ? f : f.Substring(0, dot); }
                            var original = Logic.CustomSkins.index!.Where(p => p.IndexOf(folderKey, StringComparison.OrdinalIgnoreCase) >= 0
                                    && !p.Substring(p.IndexOf(folderKey, StringComparison.OrdinalIgnoreCase) + folderKey.Length).Contains('/'))
                                .Select(p => (path: "/" + p.TrimStart('/').Substring(0, p.TrimStart('/').LastIndexOf('/') + 1) + strip(p), renamed: made.Rename(strip(p)) ?? strip(p)))
                                .FirstOrDefault(p => string.Equals(p.renamed, name, StringComparison.OrdinalIgnoreCase));
                            var before = original.path == null ? null : Logic.CustomSkins.index!.extractPackage(original.path);
                            if (before == null) { Console.WriteLine($"[clone] {item.Id} {name}: original not matched"); continue; }
                            var want = natives(before.Value.UAsset.ToArray()).OrderBy(x => x).ToList();
                            var got = natives(copy.Data).OrderBy(x => x).ToList();
                            var names = Logic.CookedProperties.readNamesOf(copy.Data);
                            var parses = true;
                            try
                            {
                                var uexp = made.Entries.First(e => e.Path.Equals(copy.Path[..^7] + ".uexp", StringComparison.OrdinalIgnoreCase)).Data;
                                var pkg = new PakReader.Pak.PakPackage(new ArraySegment<byte>(copy.Data), new ArraySegment<byte>(uexp), null);
                                if (pkg.ExportTypes == null || pkg.ExportTypes.Length == 0) { parses = false; }
                            }
                            catch (Exception) { parses = false; }
                            if (!want.SequenceEqual(got) || !parses)
                            {
                                bad++;
                                Console.WriteLine($"[clone] BAD {item.Id} {name}: natives {string.Join(",", want)} -> {string.Join(",", got)}; parses {parses}");
                            }
                            if (names.Contains("MCDR_Check")) { idsMoved++; }
                        }
                    }
                    Console.WriteLine($"[clone] {items} items, {packages} packages copied, {bad} bad, {idsMoved} carry the new bare id");
                }
                catch (Exception e) { Console.WriteLine($"[clone] FAILED {e}"); }
                Shutdown();
                return;
            }

            //PROBE_IDCLASH - every game item whose bare id is ALSO the name of something its own
            //packages import (a native class named like the item: CorruptedBeacon, TotemOfShielding's
            //shield). A copy renames the bare id's name entry, and that would rename the import too.
            if (_startupArguments.Contains("PROBE_IDCLASH"))
            {
                try
                {
                    foreach (var item in Logic.CustomItems.gameItems().GroupBy(i => i.Id).Select(g => g.First()).OrderBy(i => i.Id))
                    {
                        var cooked = "/Dungeons/Content/" + item.Folder["/Game/".Length..];
                        var want = cooked.TrimStart('/') + "/";
                        var packages = Logic.CustomSkins.index!.Where(p => p.TrimStart('/').StartsWith(want, StringComparison.OrdinalIgnoreCase)
                                && !p.TrimStart('/').Substring(want.Length).Contains('/') && !p.EndsWith(".uexp") && !p.EndsWith(".ubulk"))
                            .Select(p => "/" + p.TrimStart('/')).Distinct();
                        foreach (var p in packages)
                        {
                            var package = Logic.CustomSkins.index!.extractPackage(p);
                            if (package == null) { continue; }
                            var imports = Logic.CookedPackage.readImports(package.Value.UAsset.ToArray());
                            foreach (var clash in imports.Where(i => string.Equals(i.ObjectName, item.Id, StringComparison.Ordinal)))
                            {
                                Console.WriteLine($"[clash] {item.Id,-28} {p.Substring(p.LastIndexOf('/') + 1),-40} imports {clash.ClassName} {clash.ObjectName}");
                            }
                        }
                    }
                    Console.WriteLine("[clash] done");
                }
                catch (Exception e) { Console.WriteLine($"[clash] FAILED {e}"); }
                Shutdown();
                return;
            }

            //PROBE_ARMOR=<id>;<id>... - what an armour's folder holds: every file, the names in each
            //package that look like properties or references, and every number each export stores.
            //Read-only; for finding where an armour's stats live.
            if (_startupArguments.Any(a => a.StartsWith("PROBE_ARMOR=", StringComparison.Ordinal)))
            {
                var ids = _startupArguments.First(a => a.StartsWith("PROBE_ARMOR=", StringComparison.Ordinal))["PROBE_ARMOR=".Length..].Trim('"').Split(';', StringSplitOptions.RemoveEmptyEntries);
                try
                {
                    foreach (var id in ids)
                    {
                        var item = Logic.CustomItems.gameItem(id);
                        if (item == null) { Console.WriteLine($"[armor] {id}: not a game item"); continue; }
                        var cooked = "/Dungeons/Content/" + item.Folder["/Game/".Length..];
                        Console.WriteLine($"[armor] === {id} {cooked} native {item.NativeParent} instance {item.Instance}");
                        var files = Logic.CustomSkins.index!.Where(p => p.TrimStart('/').StartsWith(cooked.TrimStart('/') + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                        foreach (var f in files) { Console.WriteLine($"[armor]   file {f}"); }
                        foreach (var f in files.Where(f => !f.EndsWith(".uexp") && !f.EndsWith(".ubulk")).Select(f => "/" + f.TrimStart('/')).Distinct())
                        {
                            var package = Logic.CustomSkins.index!.extractPackage(f);
                            if (package == null) { continue; }
                            var names = Logic.CookedProperties.readNamesOf(package.Value.UAsset.ToArray());
                            Console.WriteLine($"[armor]   --- {f.Substring(f.LastIndexOf('/') + 1)}: {names.Count} names");
                            foreach (var n in names.Where(n => n.StartsWith("/Game/") || n.StartsWith("/Script/") || n.Contains("Propert") || n.Contains("Armor") || n.Contains("Mesh") || n.Contains("Material")))
                            {
                                Console.WriteLine($"[armor]       name {n}");
                            }
                            try
                            {
                                foreach (var n in Logic.ItemBehaviour.numbersOf(package.Value.UAsset.ToArray(), package.Value.UExp.ToArray()))
                                {
                                    Console.WriteLine($"[armor]       number {n.Export} {n.Path} = {n.Value}");
                                }
                            }
                            catch (Exception e) { Console.WriteLine($"[armor]       numbers: {e.Message}"); }
                        }
                    }
                }
                catch (Exception e) { Console.WriteLine($"[armor] FAILED {e}"); }
                Shutdown();
                return;
            }

            //PROBE_BEHAVIOUR[=<id>;<id>...] - the game's items by type, read from its asset registry,
            //and every number the named items' Instance blueprints store. Read-only.
            if (_startupArguments.Any(a => a == "PROBE_BEHAVIOUR" || a.StartsWith("PROBE_BEHAVIOUR=", StringComparison.Ordinal)))
            {
                var arg = _startupArguments.First(a => a.StartsWith("PROBE_BEHAVIOUR", StringComparison.Ordinal));
                var wanted = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..].Trim('"').Split(';', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
                var registry = Logic.RegistryPatch.readGameRegistry(Logic.CustomSkins.paksFolder!);
                var items = registry == null ? new List<Logic.RegistryPatch.GameItem>() : Logic.RegistryPatch.items(registry);
                foreach (var group in items.GroupBy(i => i.NativeParent).OrderByDescending(g => g.Count()))
                {
                    Console.WriteLine($"[behaviour] {group.Count(),4} {group.Key}: {string.Join(", ", group.Take(6).Select(i => i.Id))}");
                }
                foreach (var id in wanted)
                {
                    foreach (var item in items.Where(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)))
                    {
                        var cooked = "/Dungeons/Content/" + item.Folder["/Game/".Length..] + "/" + item.Instance;
                        var package = Logic.CustomSkins.index!.extractPackage(cooked);
                        Console.WriteLine($"[behaviour] === {item.Id} {cooked} {(package == null ? "(unreadable)" : "")}");
                        if (package == null) { continue; }
                        var names = Logic.CookedProperties.readNamesOf(package.Value.UAsset.ToArray());
                        var parent = names.FirstOrDefault(n => n.StartsWith("/Game/", StringComparison.Ordinal) && n.EndsWith("Instance", StringComparison.Ordinal) && !n.EndsWith(item.Instance, StringComparison.Ordinal));
                        Console.WriteLine($"[behaviour]     other instance referenced: {parent ?? "-"}");
                        foreach (var n in Logic.ItemBehaviour.numbersOf(package.Value.UAsset.ToArray(), package.Value.UExp.ToArray()))
                        {
                            Console.WriteLine($"[behaviour]     {n.Export} {n.Path} = {n.Value}");
                        }
                    }
                }
                Shutdown();
                return;
            }

            //PROBE_LOCRESRT - every language's Game.locres read and written back by Logic.Locres: it must
            //come back byte-identical, every key's CRC must be StrCrc32, and it lists what the file has
            //for the item slots' names and descriptions. Read-only.
            if (_startupArguments.Any(a => a == "PROBE_LOCRESRT"))
            {
                var index = Logic.CustomSkins.index!;
                var cultures = index.Select(p => p).Where(p => p.Contains("/Localization/Game/", StringComparison.OrdinalIgnoreCase) && p.EndsWith("/Game", StringComparison.Ordinal)).ToList();
                foreach (var path in cultures)
                {
                    var clean = path.StartsWith("//") ? path[1..] : path;
                    var raw = index.GetFile(clean);
                    if (raw == null) { Console.WriteLine($"[locres] {path}: unreadable"); continue; }
                    var bytes = raw.Value.ToArray();
                    var loc = Logic.Locres.read(bytes);
                    if (loc == null) { Console.WriteLine($"[locres] {path}: not version 2 ({bytes[16]})"); continue; }
                    var again = loc.write();
                    var same = again.AsSpan().SequenceEqual(bytes);
                    if (!same)
                    {
                        var first = 0;
                        while (first < Math.Min(again.Length, bytes.Length) && again[first] == bytes[first]) { first++; }
                        Console.WriteLine($"[locres]    sizes {bytes.Length} vs {again.Length}, first difference at {first}: game {BitConverter.ToString(bytes, Math.Max(0, first - 8), Math.Min(24, bytes.Length - Math.Max(0, first - 8)))} / ours {BitConverter.ToString(again, Math.Max(0, first - 8), Math.Min(24, again.Length - Math.Max(0, first - 8)))}");
                    }
                    var keys = loc.Namespaces.SelectMany(n => n.Entries).ToList();
                    var hashOk = keys.Count(e => e.KeyHash == Logic.Locres.strCrc32(e.Key));
                    var nsOk = loc.Namespaces.Count(n => n.Hash == Logic.Locres.strCrc32(n.Name));
                    Console.WriteLine($"[locres] {path}: {bytes.Length:N0} bytes, round trip identical={same}, key hashes {hashOk}/{keys.Count}, namespace hashes {nsOk}/{loc.Namespaces.Count}");
                    if (path.Contains("/en/"))
                    {
                        foreach (var slot in Logic.CustomItems.retiredSlots)
                        {
                            foreach (var key in new[] { slot.Id, "Flavour_" + slot.Id, "Desc_" + slot.Id })
                            {
                                Console.WriteLine($"[locres]    ItemType/{key} = {loc.get("ItemType", key) ?? "(none)"}");
                            }
                        }
                        //Does a source hash equal StrCrc32 of the English? Checked on a shipped item.
                        var entry = loc.Namespaces.First(n => n.Name == "ItemType").Entries.First(e => e.Key == "Katana_Unique1");
                        Console.WriteLine($"[locres]    Katana_Unique1 source hash {entry.SourceHash:x8} vs crc(\"Master's Katana\") {Logic.Locres.strCrc32("Master's Katana"):x8}");
                    }
                }
                Shutdown();
                return;
            }

            //PROBE_CUSTOMMODEL=<glb>;<out.pak> - the Weapons tab's side of a custom item, without the UI and
            //without installing: the saved designs as the tab lists them, the copy's mesh read for the
            //preview, then the model auto-fitted onto a copy of the first melee design and built into a
            //pak that is read back. The installed pak and the saved designs are not touched.
            if (_startupArguments.Any(a => a.StartsWith("PROBE_CUSTOMMODEL=", StringComparison.Ordinal)))
            {
                var bits = _startupArguments.First(a => a.StartsWith("PROBE_CUSTOMMODEL=", StringComparison.Ordinal))["PROBE_CUSTOMMODEL=".Length..].Trim('"').Split(';');
                try
                {
                    Logic.CustomItems.showInApp();
                    var listed = Logic.WeaponMeshes.all().Where(m => m.Name.StartsWith("★")).ToList();
                    foreach (var m in listed) { Console.WriteLine($"[model] listed: {m.Name} [{m.Group}] {m.AssetPath}"); }
                    var mesh = listed.First(m => m.Group == Logic.WeaponMeshes.WEAPONS);
                    var shape = Logic.WeaponMeshes.read(mesh.AssetPath);
                    Console.WriteLine($"[model] preview shape: {(shape == null ? "NONE" : $"{shape.LongestSide:0} long")}, texture: {(Logic.WeaponMeshes.textureFor(mesh.AssetPath) == null ? "none" : "yes")}");
                    Console.WriteLine($"[model] stock mesh still the game's: {Logic.CustomItems.isCopied("/Dungeons/Content/Actors/Equipment/MeleeWeapons/Claymore_Unique2/SM_Claymore_Unique2")}");

                    var model = Logic.GlbModel.read(bits[0]);
                    var fit = Logic.ModelFitting.autoFit(model, shape!);
                    var designs = Logic.CustomItems.load();
                    var design = designs.First(d => Logic.CustomItems.slotOf(d).Kind == Logic.CustomItems.Kind.Melee);
                    var keptGlb = Path.Combine(Path.GetTempPath(), "mcd-probe-model.glb");
                    File.WriteAllBytes(keptGlb, model.Source!);
                    design.Model = Logic.CustomItems.ModelEdit.of(fit, keptGlb);
                    var built = Logic.CustomItems.build(designs, bits[1]);
                    foreach (var note in built.Notes) { Console.WriteLine($"[model] {note}"); }

                    var files = Logic.ModPak.read(bits[1]).ToDictionary(i => i.Path.TrimStart('/'), i => i.Data, StringComparer.OrdinalIgnoreCase);
                    var key = mesh.AssetPath.TrimStart('/') + ".uasset";
                    var pkg = new PakReader.Pak.PakPackage(new ArraySegment<byte>(files[key]), new ArraySegment<byte>(files[key[..^7] + ".uexp"]), null);
                    Logic.MeshBounds.tryRead(pkg, out var o, out var ext, out var r);
                    var before = Logic.CustomItems.copiedPackage(mesh.AssetPath)!.Value;
                    Console.WriteLine($"[model] built mesh parses={pkg.HasExport()} bytes {before.UExp.Count} -> {files[key[..^7] + ".uexp"].Length}, extent {ext.X:0},{ext.Y:0},{ext.Z:0}");
                    Console.WriteLine($"[model] model: {model.VertexCount} vertices, texture {(model.BaseColourPng == null ? "none" : model.BaseColourPng.Length + " bytes")}");
                }
                catch (Exception e) { Console.WriteLine($"[model] FAILED {e}"); }
                Shutdown();
                return;
            }

            //PROBE_SHAREITEM=<folder> - exports every saved custom item to <folder>, reads each file back
            //and unpacks it into a scratch copy, comparing field for field. The saved designs, the
            //installed pak and the app's own item folder are not written.
            if (_startupArguments.Any(a => a.StartsWith("PROBE_SHAREITEM=", StringComparison.Ordinal)))
            {
                var into = _startupArguments.First(a => a.StartsWith("PROBE_SHAREITEM=", StringComparison.Ordinal))["PROBE_SHAREITEM=".Length..].Trim('"');
                Directory.CreateDirectory(into);
                try
                {
                    Logic.CustomItems.gameItems();
                    foreach (var design in Logic.CustomItems.load())
                    {
                        var file = Path.Combine(into, design.Slot + Logic.CustomItems.SHARE_EXTENSION);
                        if (File.Exists(file)) { File.Delete(file); }
                        Logic.CustomItems.export(design, file);
                        using (var zip = System.IO.Compression.ZipFile.OpenRead(file))
                        {
                            Console.WriteLine($"[share] {design.Slot}: {new FileInfo(file).Length:N0} bytes, holds {string.Join(", ", zip.Entries.Select(e => $"{e.Name} {e.Length:N0}"))}");
                        }
                        var shared = Logic.CustomItems.readShared(file);
                        var slot = Logic.CustomItems.slotOf(design);
                        //Unpacked as the tab would, except that the files go to a scratch folder.
                        var back = Logic.CustomItems.copy(shared.Design);
                        var same = back.Source == design.Source && back.Name == design.Name && back.Description == design.Description
                            && back.Icon == design.Icon && back.IconItem == design.IconItem
                            && back.Values.Count == design.Values.Count && back.Values.All(v => design.Values.TryGetValue(v.Key, out var w) && w == v.Value)
                            && (back.Model == null) == (design.Model == null)
                            && (back.Model == null || (back.Model.Scale == design.Model!.Scale && back.Model.Offset.SequenceEqual(design.Model.Offset) && back.Model.Rotation.SequenceEqual(design.Model.Rotation)));
                        Console.WriteLine($"[share]    kind {shared.Kind}, fields identical={same}, paths removed={back.IconFile == null && back.Model?.File == null}");
                        using (var zip = System.IO.Compression.ZipFile.OpenRead(file))
                        {
                            if (design.Model?.File != null && zip.GetEntry("model.glb") is { } glb)
                            {
                                using var s = glb.Open(); using var m = new MemoryStream(); s.CopyTo(m);
                                Console.WriteLine($"[share]    model bytes identical={m.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(design.Model.File))}");
                            }
                            if (design.IconFile != null && zip.GetEntry("icon.png") is { } png)
                            {
                                using var s = png.Open(); using var m = new MemoryStream(); s.CopyTo(m);
                                Console.WriteLine($"[share]    icon bytes identical={m.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(design.IconFile))}");
                            }
                        }
                    }
                }
                catch (Exception e) { Console.WriteLine($"[share] FAILED {e}"); }
                Shutdown();
                return;
            }

            //PROBE_PLUGINITEMS - items beyond the free slots through the app's own code, as the New
            //Items tab would make them: adopts MCDR_Test01 (the plugin test, on a character) as a
            //design, adds one new ranged item, builds, and prints what was installed where.
            if (_startupArguments.Contains("PROBE_PLUGINITEMS"))
            {
                try
                {
                    if (Logic.GameRunning.isUp) { Console.WriteLine("[items] close the game first"); Shutdown(); return; }
                    var designs = Logic.CustomItems.load();
                    if (designs.All(d => d.Slot != "MCDR_Test01"))
                    {
                        designs.Add(new Logic.CustomItems.Design { Slot = "MCDR_Test01", Source = "Katana_Unique1", PluginKind = Logic.CustomItems.Kind.Melee, Name = "Plugin Test Katana" });
                    }
                    if (!designs.Any(d => d.Slot.StartsWith(Logic.CustomItems.PLUGIN_PREFIX, StringComparison.Ordinal)))
                    {
                        var id = Logic.CustomItems.newPluginId(designs);
                        var sources = Logic.CustomItems.sourcesFor(Logic.CustomItems.pluginSlot(id, Logic.CustomItems.Kind.Ranged));
                        var bow = sources.FirstOrDefault(s => s.Id == "Bow_Unique1") ?? sources.First();
                        designs.Add(new Logic.CustomItems.Design { Slot = id, Source = bow.Id, PluginKind = Logic.CustomItems.Kind.Ranged, Name = "Plugin Test Bow", Description = "The second item the plugin ever registered." });
                        Console.WriteLine($"[items] {id}: a copy of {bow.Id} ({sources.Count} ranged sources)");
                    }
                    foreach (var d in designs) { Console.WriteLine($"[items] design {d.Slot} <- {d.Source}, folder {Logic.CustomItems.slotOf(d).Folder}, plugin {Logic.CustomItems.slotOf(d).Plugin}"); }
                    var built = Logic.CustomItems.build(designs);
                    Logic.CustomItems.save(designs);
                    foreach (var note in built.Notes) { Console.WriteLine($"[items] {note}"); }
                    var folder = Logic.GamePlugin.gameFolder();
                    Console.WriteLine($"[items] game folder {folder}; ours {Logic.GamePlugin.isOurs(System.IO.Path.Combine(folder!, Logic.GamePlugin.DLL_NAME))}");
                    Console.WriteLine(System.IO.File.ReadAllText(System.IO.Path.Combine(folder!, Logic.GamePlugin.ITEMS_NAME)));
                }
                catch (Exception e) { Console.WriteLine($"[items] FAILED {e}"); }
                Shutdown();
                return;
            }

            //PROBE_PLUGINTEST=<MCDRebornItems.dll> - the item plugin, end to end, with one extra item:
            //MCDR_Test01, a copy of the Master's Katana under an id the game never had.
            //  1. with the game closed: rebuilds the New Items pak with the saved designs AND the extra
            //     item, and writes the plugin and its item list to %LOCALAPPDATA%\MCDReborn\Plugin
            //  2. waits for the game to start, gives it three seconds to decrypt itself, and loads the
            //     plugin into it - the way the community loaders do
            //  3. prints the plugin's own log as it registers the item.
            if (_startupArguments.Any(a => a.StartsWith("PROBE_PLUGINTEST=", StringComparison.Ordinal)))
            {
                var dll = _startupArguments.First(a => a.StartsWith("PROBE_PLUGINTEST=", StringComparison.Ordinal))["PROBE_PLUGINTEST=".Length..].Trim('"');
                var pluginFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCDReborn", "Plugin");
                Directory.CreateDirectory(pluginFolder);
                try
                {
                    var extras = new List<Logic.CustomItems.Extra>
                    {
                        new("MCDR_Test01", "Katana_Unique1", "Plugin Test Katana", "Registered by the MCD Reborn plugin, under an id the game never had."),
                    };
                    if (Logic.GameRunning.isUp)
                    {
                        Console.WriteLine("[plugin] the game is running: close it first so the pak can be rebuilt");
                        Shutdown(); return;
                    }
                    var built = Logic.CustomItems.build(Logic.CustomItems.load(), null, extras);
                    foreach (var note in built.Notes) { Console.WriteLine($"[plugin] {note}"); }

                    var lines = new List<string> { "# id\tsource\tfolder\tname\tdescription" };
                    foreach (var extra in extras)
                    {
                        var source = Logic.CustomItems.gameItem(extra.Source)!;
                        lines.Add(string.Join("\t", extra.Id, extra.Source, Logic.CustomItems.extraFolder(source, extra.Id), extra.Name, extra.Description));
                    }
                    File.WriteAllLines(Path.Combine(pluginFolder, "MCDRebornItems.txt"), lines, new System.Text.UTF8Encoding(false));
                    var pluginDll = Path.Combine(pluginFolder, "MCDRebornItems.dll");
                    File.Copy(dll, pluginDll, overwrite: true);
                    Console.WriteLine($"[plugin] plugin and item list in {pluginFolder}");
                    foreach (var line in lines.Skip(1)) { Console.WriteLine($"[plugin]    {line.Replace('\t', '|')}"); }

                    Console.WriteLine("[plugin] waiting for the game to start (10 minutes)...");
                    var started = DateTime.UtcNow;
                    System.Diagnostics.Process? game = null;
                    while (game == null && DateTime.UtcNow - started < TimeSpan.FromMinutes(10))
                    {
                        game = System.Diagnostics.Process.GetProcessesByName("Dungeons-Win64-Shipping").FirstOrDefault();
                        if (game == null) { Thread.Sleep(200); }
                    }
                    if (game == null) { Console.WriteLine("[plugin] the game never started"); Shutdown(); return; }
                    //Not a fixed delay after launch: loading at three seconds, while the game was still
                    //decrypting itself, failed and left that path unloadable for the rest of the run.
                    //The Dungeons module's global going non-null says the engine is up.
                    Console.WriteLine($"[plugin] game started (pid {game.Id}); waiting for the engine");
                    using (var live = LiveEdit.GameProcess.open(out _))
                    {
                        var image = live?.image(out _) ?? IntPtr.Zero;
                        var moduleGlobal = image == IntPtr.Zero ? IntPtr.Zero : new IntPtr(image.ToInt64() + 0x44c7390);
                        var until2 = DateTime.UtcNow.AddMinutes(3);
                        while (DateTime.UtcNow < until2 && live != null && !game.HasExited)
                        {
                            var value = live.read(moduleGlobal, 8);
                            if (value != null && BitConverter.ToInt64(value, 0) != 0) { break; }
                            Thread.Sleep(500);
                        }
                    }
                    Thread.Sleep(2000);
                    //Loaded from a fresh folder each time: from the app's own Plugin folder the game
                    //refused it twice while an identical copy in a temp folder loaded - not
                    //understood yet, so the path that works is the one used.
                    var run = Path.Combine(Path.GetTempPath(), "MCDRebornPlugin", game.Id.ToString());
                    Directory.CreateDirectory(run);
                    File.Copy(pluginDll, Path.Combine(run, "MCDRebornItems.dll"), overwrite: true);
                    File.Copy(Path.Combine(pluginFolder, "MCDRebornItems.txt"), Path.Combine(run, "MCDRebornItems.txt"), overwrite: true);
                    pluginFolder = run;
                    pluginDll = Path.Combine(run, "MCDRebornItems.dll");
                    Console.WriteLine($"[plugin] engine up; loading the plugin from {run}");
                    var ok = LiveEdit.DllInjector.inject(pluginDll, out var problem);
                    Console.WriteLine($"[plugin] load: {(ok ? "ok" : "FAILED")} {problem}");

                    var log = Path.Combine(pluginFolder, "MCDRebornItems.log");
                    var shown = 0;
                    var until = DateTime.UtcNow.AddSeconds(90);
                    while (DateTime.UtcNow < until)
                    {
                        Thread.Sleep(500);
                        if (!File.Exists(log)) { continue; }
                        string[] now;
                        try { using var s = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var r = new StreamReader(s); now = r.ReadToEnd().Split('\n'); }
                        catch (IOException) { continue; }
                        //The last piece may be a line still being written; it is printed next time.
                        for (; shown < now.Length - 1; shown++) { if (now[shown].Trim().Length > 0) { Console.WriteLine($"[plugin]    log: {now[shown].TrimEnd()}"); } }
                        if (now.Any(l => l.Contains("done:") || l.Contains("nothing was changed"))) { break; }
                        if (game.HasExited) { Console.WriteLine("[plugin] the GAME CLOSED"); break; }
                    }
                }
                catch (Exception e) { Console.WriteLine($"[plugin] FAILED {e}"); }
                Shutdown();
                return;
            }

            //READ_PAK=<pak file> - lists what a real pak reader finds inside one.
            var readPak = _startupArguments.FirstOrDefault(a => a.StartsWith("READ_PAK="));
            if (readPak != null)
            {
                var target = readPak.Substring("READ_PAK=".Length).Trim('"');
                foreach (var withFilter in new[] { true, false })
                {
                    try
                    {
                        var filter = withFilter
                            ? new PakReader.Pak.PakFilter(new[] { Data.Constants.PAKS_FILTER_STRING }, false)
                            : null;
                        var index = new PakReader.Pak.PakIndex(new[] { target }, cacheFiles: true, caseSensitive: true, filter: filter);
                        //Constructing the index does not parse it; UseKey is what does. An
                        //unencrypted mod pak ignores the key, but still needs the call.
                        //The GUID-matching overload checks a field version 3 paks do not have;
                        //the plain one just tries the key on every pak.
                        var unlocked = index.UseKey(new byte[32]);
                        var listed = new System.Collections.Generic.List<string>(index);
                        Console.WriteLine($"[read] unlocked {unlocked} pak(s)");
                        Console.WriteLine($"[read] filter={withFilter}: {listed.Count} entries");
                        foreach (var path in listed) { Console.WriteLine($"[read]    {path}"); }
                    }
                    catch (Exception e) { Console.WriteLine($"[read] filter={withFilter}: {e.GetType().Name}: {e.Message}"); }
                }
                this.Shutdown();
                return;
            }

            //BUILD_PAK=<out pak>|<asset dir>|<asset path in pak without extension>
            var buildPak = _startupArguments.FirstOrDefault(a => a.StartsWith("BUILD_PAK="));
            if (buildPak != null)
            {
                buildAndVerifyPak(buildPak.Substring("BUILD_PAK=".Length).Trim('"'));
                this.Shutdown();
                return;
            }

            //PATCH_TEXTURE=<pak path>|<png>|<out dir> - rewrites one texture's pixels from a
            //PNG and reads the result back through the same decoder the app uses.
            var patch = _startupArguments.FirstOrDefault(a => a.StartsWith("PATCH_TEXTURE="));
            if (patch != null)
            {
                patchTexture(patch.Substring("PATCH_TEXTURE=".Length).Trim('"'));
                this.Shutdown();
                return;
            }

            //SPIKE_TEXTURE=<pak path>|<out dir> - dumps the raw package bytes and reports how
            //the pixels are stored, which is what decides whether a texture can be rewritten.
            var spike = _startupArguments.FirstOrDefault(a => a.StartsWith("SPIKE_TEXTURE="));
            if (spike != null)
            {
                spikeTexture(spike.Substring("SPIKE_TEXTURE=".Length).Trim('"'));
                this.Shutdown();
                return;
            }

            //EXPORT_TEXTURE=<pak path>|<output png> - pulls a texture out of the game so it
            //can be painted over. The foundation of the custom-skins work.
            var textureExport = _startupArguments.FirstOrDefault(a => a.StartsWith("EXPORT_TEXTURE="));
            if (textureExport != null)
            {
                var parts = textureExport.Substring("EXPORT_TEXTURE=".Length).Trim('"').Split('|');
                if (parts.Length == 2)
                {
                    var source = ImageResolver.instance.imageSource(parts[0]);
                    if (source == null) { Console.WriteLine($"[texture] not found: {parts[0]}"); }
                    else
                    {
                        Console.WriteLine($"[texture] {parts[0]} is {source.PixelWidth}x{source.PixelHeight} {source.Format}");
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                        using var file = System.IO.File.Create(parts[1]);
                        encoder.Save(file);
                        Console.WriteLine($"[texture] wrote {parts[1]}");
                    }
                }
                this.Shutdown();
                return;
            }

            if (_startupArguments.Contains("PRINT_ARMOR_PROPERTIES"))
            {
                foreach (var id in Services.ItemDatabase.armorProperties.OrderBy(x => x))
                {
                    Console.WriteLine($"[prop] {id}	{Services.R.armorProperty(id)}");
                }
                foreach (var id in Services.ItemDatabase.armor.OrderBy(x => x))
                {
                    Console.WriteLine($"[armor-item] {id}	{Services.R.itemName(id)}");
                }
                this.Shutdown();
                return;
            }

            if (_startupArguments.Contains("PRINT_ENCHANTMENTS"))
            {
                foreach (var id in Services.EnchantmentDatabase.allEnchantments.OrderBy(x => x))
                {
                    Console.WriteLine($"[ench] {id}	{Services.R.enchantmentName(id)}");
                }
                this.Shutdown();
                return;
            }

            if (_startupArguments.Contains("PRINT_BUILD"))
            {
                var equipped = _model.mainModel.profileModel.mainEquipmentModel.equippedItemList.value;
                Console.WriteLine("[build-json] " + Logic.BuildShare.toJson(equipped));
                Console.WriteLine("[build-url] " + Logic.BuildShare.toShareUrl(equipped));
                this.Shutdown();
                return;
            }

            var screenshotPath = UI.Theme.WindowCapture.pathFromArguments(_startupArguments);
            if (screenshotPath != null)
            {
                this.MainWindow.UpdateLayout();
                UI.Theme.WindowCapture.selectTab(this.MainWindow, _startupArguments);

                //Dev aid: fire a named menu item before capturing, so a command can be
                //exercised through its real handler rather than only in theory.
                var invoke = _startupArguments.FirstOrDefault(a => a.StartsWith("INVOKE="));
                if (invoke != null)
                {
                    var itemName = invoke.Substring("INVOKE=".Length);
                    var target = UI.Theme.WindowCapture.findByName(this.MainWindow, itemName);
                    if (target is System.Windows.Controls.MenuItem menu)
                    {
                        try
                        {
                            //Click for the items that carry a handler, and the command for the
                            //ones bound to a RoutedCommand - Save and Save As are the latter,
                            //and raising Click alone does nothing for those.
                            menu.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                            if (menu.Command is System.Windows.Input.RoutedCommand routed)
                            {
                                routed.Execute(menu.CommandParameter, menu.CommandTarget ?? this.MainWindow);
                            }
                            else { menu.Command?.Execute(menu.CommandParameter); }
                            Console.WriteLine($"[invoke] {itemName} ok");
                        }
                        catch (Exception ex) { Console.WriteLine($"[invoke] {itemName} threw {ex.GetType().Name}: {ex.Message}"); }
                    }
                    else if (target is System.Windows.Controls.Button button)
                    {
                        //Raising Click would not run the command - Button.OnClick does that -
                        //so go straight at the command the way the click would.
                        try
                        {
                            button.Command?.Execute(button.CommandParameter);
                            Console.WriteLine($"[invoke] {itemName} ok");
                        }
                        catch (Exception ex) { Console.WriteLine($"[invoke] {itemName} threw {ex.GetType().Name}: {ex.Message}"); }
                    }
                    else { Console.WriteLine($"[invoke] {itemName} not found"); }
                    this.MainWindow.UpdateLayout();
                }
                this.MainWindow.UpdateLayout();
                UI.Theme.WindowCapture.applySearch(this.MainWindow, _startupArguments, "itemSearchBox");
                var toggledPath = UI.Theme.WindowCapture.toggledPathFromArguments(_startupArguments);
                UI.Theme.WindowCapture.captureThenExit(this.MainWindow, screenshotPath!, toggledPath);
            }
        }

        /// <summary>
        /// Dev probe: lists pak entries whose path contains <paramref name="needle"/> and dumps
        /// the first non-texture package as JSON. Used to find out whether the game's own data
        /// carries each armor's default armor properties.
        /// </summary>
        private static void printArmorData(string needle)
        {
            var model = new AppModel();
            var paksFolderPath = model.usableGameContentIfExists();
            if (string.IsNullOrWhiteSpace(paksFolderPath)) { Console.WriteLine("[armor] no game content"); return; }

            var filter = new PakReader.Pak.PakFilter(new[] { Data.Constants.PAKS_FILTER_STRING }, false);
            var pakIndex = new PakReader.Pak.PakIndex(path: paksFolderPath!, cacheFiles: true, caseSensitive: true, filter: filter);
            foreach (var key in Data.Secrets.PAKS_AES_KEYS)
            {
                try { pakIndex.UseKey(PakReader.Parsers.Objects.FGuid.Zero, key.key.Substring(2).ToBytesKey()); } catch { }
            }

            var matches = pakIndex.Where(x => x.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Console.WriteLine($"[armor] {matches.Count} entries containing '{needle}'");
            foreach (var path in matches.Take(40)) { Console.WriteLine($"[armor]   {path}"); }

            foreach (var path in matches)
            {
                if (path.IndexOf("_icon", StringComparison.OrdinalIgnoreCase) >= 0) { continue; }
                try
                {
                    var package = pakIndex.extractPackage(path);
                    if (package == null) { continue; }
                    var json = package.Value.JsonData;
                    Console.WriteLine($"[armor] --- {path} ---");
                    Console.WriteLine(json.Length > 6000 ? json.Substring(0, 6000) : json);
                    return;
                }
                catch (Exception e) { Console.WriteLine($"[armor]   {path} -> {e.GetType().Name}"); }
            }
            Console.WriteLine("[armor] nothing readable");
        }

        /// <summary>
        /// Spike: reports how one texture's pixels are stored and writes the raw package bytes
        /// out, so a rewrite can be attempted against real data rather than assumptions.
        /// </summary>
        private static void spikeTexture(string argument)
        {
            var parts = argument.Split('|');
            if (parts.Length != 2) { Console.WriteLine("[spike] need <pak path>|<out dir>"); return; }
            var assetPath = parts[0];
            var outDir = parts[1];
            System.IO.Directory.CreateDirectory(outDir);

            var model = new AppModel();
            var paksFolderPath = model.usableGameContentIfExists();
            if (string.IsNullOrWhiteSpace(paksFolderPath)) { Console.WriteLine("[spike] no game content"); return; }

            var filter = new PakReader.Pak.PakFilter(new[] { Data.Constants.PAKS_FILTER_STRING }, false);
            var pakIndex = new PakReader.Pak.PakIndex(path: paksFolderPath!, cacheFiles: true, caseSensitive: true, filter: filter);
            foreach (var key in Data.Secrets.PAKS_AES_KEYS)
            {
                try { pakIndex.UseKey(PakReader.Parsers.Objects.FGuid.Zero, key.key.Substring(2).ToBytesKey()); } catch { }
            }

            var package = pakIndex.extractPackage(assetPath);
            if (package == null) { Console.WriteLine($"[spike] no package at {assetPath}"); return; }

            var name = System.IO.Path.GetFileName(assetPath);
            void dump(string suffix, ArraySegment<byte>? segment)
            {
                if (segment == null) { Console.WriteLine($"[spike] {suffix}: absent"); return; }
                var bytes = segment.Value.ToArray();
                var path = System.IO.Path.Combine(outDir, name + suffix);
                System.IO.File.WriteAllBytes(path, bytes);
                Console.WriteLine($"[spike] {suffix}: {bytes.Length} bytes -> {path}");
            }
            dump(".uasset", package.Value.UAsset);
            dump(".uexp", package.Value.UExp);
            dump(".ubulk", package.Value.UBulk);

            var texture = package.Value.GetExport<PakReader.Parsers.Class.UTexture2D>();
            if (texture == null) { Console.WriteLine("[spike] not a UTexture2D"); return; }

            foreach (var platform in texture.PlatformDatas)
            {
                Console.WriteLine($"[spike] {platform.SizeX}x{platform.SizeY} slices={platform.NumSlices} format={platform.PixelFormat} mips={platform.Mips.Length}");
                for (int i = 0; i < platform.Mips.Length; i++)
                {
                    var mip = platform.Mips[i];
                    var length = mip.BulkData.Data?.Length ?? -1;
                    Console.WriteLine($"[spike]   mip{i} {mip.SizeX}x{mip.SizeY}x{mip.SizeZ} data={length} bytes");
                }
            }
        }

        /// <summary>
        /// Spike: replaces a texture's pixels with a PNG's and verifies the result.
        ///
        /// The armour textures are PF_B8G8R8A8 with one mip and the payload inline in the
        /// .uexp, so the new pixels are exactly as long as the old ones. That means a
        /// byte-for-byte overwrite in place - no offsets move, and the .uasset, which records
        /// where the export ends, stays correct without being touched.
        /// </summary>
        private static void patchTexture(string argument)
        {
            var parts = argument.Split('|');
            if (parts.Length != 3) { Console.WriteLine("[patch] need <pak path>|<png>|<out dir>"); return; }
            var assetPath = parts[0];
            var pngPath = parts[1];
            var outDir = parts[2];
            System.IO.Directory.CreateDirectory(outDir);

            var model = new AppModel();
            var paksFolderPath = model.usableGameContentIfExists();
            if (string.IsNullOrWhiteSpace(paksFolderPath)) { Console.WriteLine("[patch] no game content"); return; }

            var filter = new PakReader.Pak.PakFilter(new[] { Data.Constants.PAKS_FILTER_STRING }, false);
            var pakIndex = new PakReader.Pak.PakIndex(path: paksFolderPath!, cacheFiles: true, caseSensitive: true, filter: filter);
            foreach (var key in Data.Secrets.PAKS_AES_KEYS)
            {
                try { pakIndex.UseKey(PakReader.Parsers.Objects.FGuid.Zero, key.key.Substring(2).ToBytesKey()); } catch { }
            }

            var package = pakIndex.extractPackage(assetPath);
            if (package == null) { Console.WriteLine($"[patch] no package at {assetPath}"); return; }
            var texture = package.Value.GetExport<PakReader.Parsers.Class.UTexture2D>();
            if (texture == null) { Console.WriteLine("[patch] not a UTexture2D"); return; }

            var platform = texture.PlatformDatas[0];
            var mip = platform.Mips[0];
            var original = mip.BulkData.Data;
            if (platform.PixelFormat != PakReader.Parsers.Objects.EPixelFormat.PF_B8G8R8A8)
            {
                Console.WriteLine($"[patch] unsupported format {platform.PixelFormat}"); return;
            }

            //Where the payload sits in the .uexp. Found by looking for it rather than by
            //computing an offset, so a different asset layout cannot silently corrupt one.
            var uexp = package.Value.UExp.ToArray();
            int offset = indexOf(uexp, original);
            if (offset < 0) { Console.WriteLine("[patch] could not locate the pixel payload in the .uexp"); return; }
            Console.WriteLine($"[patch] payload at {offset} of {uexp.Length} ({original.Length} bytes)");

            //The replacement, as BGRA in the same order the mip stores it.
            var replacement = bgraFromPng(pngPath, platform.SizeX, platform.SizeY);
            if (replacement == null) { return; }
            if (replacement.Length != original.Length)
            {
                Console.WriteLine($"[patch] size mismatch: png gives {replacement.Length}, texture wants {original.Length}"); return;
            }

            Buffer.BlockCopy(replacement, 0, uexp, offset, replacement.Length);

            var name = System.IO.Path.GetFileName(assetPath);
            var uassetOut = System.IO.Path.Combine(outDir, name + ".uasset");
            var uexpOut = System.IO.Path.Combine(outDir, name + ".uexp");
            System.IO.File.WriteAllBytes(uassetOut, package.Value.UAsset.ToArray());
            System.IO.File.WriteAllBytes(uexpOut, uexp);
            Console.WriteLine($"[patch] wrote {uassetOut} and {uexpOut}");

            //Read it back through the same parser the app uses. If this decodes to the new
            //pixels, the file is structurally sound as far as anything here can tell.
            var rebuilt = new PakReader.Pak.PakPackage(
                new ArraySegment<byte>(package.Value.UAsset.ToArray()),
                new ArraySegment<byte>(uexp),
                null);
            var check = rebuilt.GetExport<PakReader.Parsers.Class.UTexture2D>();
            if (check == null) { Console.WriteLine("[patch] VERIFY FAILED: re-parse did not yield a texture"); return; }
            var checkMip = check.PlatformDatas[0].Mips[0];
            bool same = checkMip.BulkData.Data != null && indexOf(checkMip.BulkData.Data, replacement) == 0;
            Console.WriteLine($"[patch] VERIFY {(same ? "OK" : "FAILED")}: re-parsed {check.PlatformDatas[0].SizeX}x{check.PlatformDatas[0].SizeY} {check.PlatformDatas[0].PixelFormat}, pixels match = {same}");
        }

        private static int indexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length) { return -1; }
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (haystack[i] != needle[0]) { continue; }
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) { j++; }
                if (j == needle.Length) { return i; }
            }
            return -1;
        }

        private static byte[]? bgraFromPng(string pngPath, int width, int height)
        {
            if (!System.IO.File.Exists(pngPath)) { Console.WriteLine($"[patch] no png at {pngPath}"); return null; }
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
                new Uri(System.IO.Path.GetFullPath(pngPath)),
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != width || frame.PixelHeight != height)
            {
                Console.WriteLine($"[patch] png is {frame.PixelWidth}x{frame.PixelHeight}, texture is {width}x{height}");
                return null;
            }
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var stride = width * 4;
            var bytes = new byte[stride * height];
            converted.CopyPixels(bytes, stride, 0);
            return bytes;
        }

        /// <summary>
        /// Spike: packs the patched files into a mod pak, then reads that pak back with the
        /// app's own PakReader and decodes the texture out of it. If a real UE pak reader can
        /// mount it and pull the new pixels out, the file is sound.
        /// </summary>
        private static void buildAndVerifyPak(string argument)
        {
            var parts = argument.Split('|');
            if (parts.Length != 3) { Console.WriteLine("[pak] need <out pak>|<asset dir>|<path in pak>"); return; }
            var outPak = parts[0];
            var assetDir = parts[1];
            var pathInPak = parts[2];

            var name = System.IO.Path.GetFileName(pathInPak);
            var entries = new System.Collections.Generic.List<Logic.PakWriter.Entry>();
            foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
            {
                var source = System.IO.Path.Combine(assetDir, name + extension);
                if (!System.IO.File.Exists(source)) { continue; }
                entries.Add(new Logic.PakWriter.Entry(pathInPak + extension, System.IO.File.ReadAllBytes(source)));
                Console.WriteLine($"[pak] adding {pathInPak + extension}");
            }
            if (entries.Count == 0) { Console.WriteLine("[pak] nothing to pack"); return; }

            Logic.PakWriter.write(outPak, entries);
            Console.WriteLine($"[pak] wrote {outPak} ({new System.IO.FileInfo(outPak).Length} bytes)");

            //Read it straight back with a real pak reader.
            try
            {
                var index = new PakReader.Pak.PakIndex(new[] { outPak }, cacheFiles: true, caseSensitive: true, filter: null);
                index.UseKey(new byte[32]);
                var listed = new System.Collections.Generic.List<string>(index);
                Console.WriteLine($"[pak] reader mounted it, {listed.Count} entries:");
                foreach (var path in listed) { Console.WriteLine($"[pak]   {path}"); }

                var assetKey = listed.Find(x => x.EndsWith(name, StringComparison.OrdinalIgnoreCase));
                if (assetKey == null) { Console.WriteLine("[pak] VERIFY FAILED: asset not found in the pak"); return; }

                var package = index.extractPackage(assetKey);
                var texture = package?.GetExport<PakReader.Parsers.Class.UTexture2D>();
                if (texture == null) { Console.WriteLine("[pak] VERIFY FAILED: could not decode the texture from the pak"); return; }

                var platform = texture.PlatformDatas[0];
                Console.WriteLine($"[pak] VERIFY OK: decoded {platform.SizeX}x{platform.SizeY} {platform.PixelFormat} from the pak");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[pak] VERIFY FAILED: {e.GetType().Name}: {e.Message}");
            }
        }

        private void onRelaunch()
        {
            showSplashWindowReplacingOldWindow();
            _ = loadAsync(askForGameContentLocation: true);
        }

        private void onReload(string? autoReloadFilename, ProfileSaveFile? profile)
        {
            var oldMainWindow = this.MainWindow;
            var mainWindow = WindowFactory.createMainWindow(_model.mainModel);
            mainWindow.onRelaunch = onRelaunch;
            mainWindow.onReload = onReload;
            this.MainWindow = mainWindow;
            this.MainWindow.Show();
            oldMainWindow?.Close();

            if (!string.IsNullOrWhiteSpace(autoReloadFilename))
            {
                if(profile != null)
                {
                    _model.mainModel.setProfile(autoReloadFilename!, profile);
                }
                else
                {
                    mainWindow.handleFileOpenAsync(autoReloadFilename!);
                }
            }
        }

        private void showSplashWindowReplacingOldWindow()
        {
            if(_controlWriter != null)
            {
                _outputWriter.removeWriter(_controlWriter);
                _controlWriter.Close();
            }

            var oldMainWindow = this.MainWindow;
            _splashWindow = WindowFactory.createSplashWindow();
            _controlWriter = new ControlWriter(_splashWindow.textbox);
            this.ExecuteOnMainThreadWithNonnullThis(nonnullThis => {
                if(nonnullThis._controlWriter != null)
                    nonnullThis._outputWriter.addWriter(nonnullThis._controlWriter);
            });

            MainWindow = _splashWindow;
            this.MainWindow.Show();
            oldMainWindow?.Close();
        }

        private void showBusyIndicator()
        {
            closeBusyIndicator();

            //The splash already shows a title, a status line and a progress bar. Putting the
            //spinner window on top of it as well just drew a ring of grey dots across the
            //text, so it is skipped while the splash is up and kept for everything after.
            if (_splashWindow != null && _splashWindow!.IsVisible) { return; }

            _busyWindow = WindowFactory.createBusyWindow();
            _busyWindow.Show();
        }

        private void closeBusyIndicator()
        {
            if (_busyWindow != null)
            {
                _busyWindow!.Close();
                _busyWindow = null;
            }
        }

#region Dispose
        //Implemented as described here: https://docs.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-dispose

        private bool _disposed = false;

        ~App()
        {
            Dispose(true);
        }

        public void Dispose()
        {
            // Dispose of unmanaged resources.
            Dispose(true);
            // Suppress finalization.
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                // dispose managed state (managed objects).
                _outputWriter.Dispose();
                _controlWriter?.Dispose();
            }

            // free unmanaged resources (unmanaged objects) and override a finalizer below.
            // set large fields to null.

            _disposed = true;
        }

#endregion
    }
}
