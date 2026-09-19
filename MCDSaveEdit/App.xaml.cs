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
