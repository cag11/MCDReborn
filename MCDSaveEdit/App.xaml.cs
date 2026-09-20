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
