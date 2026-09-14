using MCDSaveEdit.Data;
using MCDSaveEdit.Interfaces;
using MCDSaveEdit.Logic;
using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using MCDSaveEdit.ViewModels;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Interaction logic for ItemListScreen.xaml
    /// </summary>
    public partial class ItemListScreen : UserControl
    {
        private static readonly BitmapImage? _allItemsButtonImageSource = ImageResolver.instance.imageSource("/Dungeons/Content/UI/Materials/Inventory2/Filter/filter_all_default");
        private static readonly BitmapImage? _meleeItemsButtonImageSource = ImageResolver.instance.imageSource("/Dungeons/Content/UI/Materials/Inventory2/Filter/filter_melee_default");
        private static readonly BitmapImage? _rangedItemsButtonImageSource = ImageResolver.instance.imageSource("/Dungeons/Content/UI/Materials/Inventory2/Filter/filter_ranged_default");
        private static readonly BitmapImage? _armorItemsButtonImageSource = ImageResolver.instance.imageSource("/Dungeons/Content/UI/Materials/Inventory2/Filter/filter_armour_default");
        private static readonly BitmapImage? _artifactItemsButtonImageSource = ImageResolver.instance.imageSource("/Dungeons/Content/UI/Materials/Inventory2/Filter/filter_consume_default");
        private static readonly BitmapImage? _enchantedItemsButtonImageSource = ImageResolver.instance.imageSource("/Dungeons/Content/UI/Materials/Inventory2/Filter/filter_enchant_default");

        public static void preload() { }

        private IListItems? _model;
        public IListItems? model
        {
            get { return _model; }
            set
            {
                _model = value;
                setupCommands();
                //updateUI(); //Not Needed
            }
        }

        public ItemListScreen()
        {
            InitializeComponent();
            translateStaticStrings();
            if (AppModel.gameContentLoaded)
            {
                useGameContentImages();
            }
            meleeItemsButton.IsEnabled = AppModel.gameContentLoaded;
            rangedItemsButton.IsEnabled = AppModel.gameContentLoaded;
            armorItemsButton.IsEnabled = AppModel.gameContentLoaded;
            artifactItemsButton.IsEnabled = AppModel.gameContentLoaded;
        }

        private void useGameContentImages()
        {
            tryLoadAndSetImage(allItemsButton, _allItemsButtonImageSource);
            tryLoadAndSetImage(meleeItemsButton, _meleeItemsButtonImageSource);
            tryLoadAndSetImage(rangedItemsButton, _rangedItemsButtonImageSource);
            tryLoadAndSetImage(armorItemsButton, _armorItemsButtonImageSource);
            tryLoadAndSetImage(artifactItemsButton, _artifactItemsButtonImageSource);
            tryLoadAndSetImage(enchantedItemsButton, _enchantedItemsButtonImageSource);
        }

        private void tryLoadAndSetImage(Button button, ImageSource? imageSource)
        {
            if (imageSource != null)
            {
                var image = new Image();
                image.Source = imageSource;
                button.Content = image;
            }
        }

        private void translateStaticStrings()
        {
            allItemsButton.Content = R.getString("ItemTag_All") ?? R.ALL_ITEMS_FILTER;
            meleeItemsButton.Content = R.getString("ItemTag_Melee") ?? R.MELEE_ITEMS_FILTER;
            rangedItemsButton.Content = R.getString("ItemTag_Ranged") ?? R.RANGED_ITEMS_FILTER;
            armorItemsButton.Content = R.getString("ItemTag_Armor") ?? R.ARMOR_ITEMS_FILTER;
            artifactItemsButton.Content = R.getString("ItemTag_Items") ?? R.ARTIFACT_ITEMS_FILTER;
            enchantedItemsButton.Content = R.getString("ItemTag_Enchanted") ?? R.ENCHANTED_ITEMS_FILTER;
        }

        private void setupCommands()
        {
            if (_model == null) { return; }
            var model = _model!;

            model.filteredItemList.subscribe(updateGridItemsUIReloadingAll);
            model.filteredItemList.subscribe(updateSearchCount);
        }

        private void itemSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            itemSearchHint.Visibility = string.IsNullOrEmpty(itemSearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            if (_model == null) { return; }
            _model!.searchText.setValue = itemSearchBox.Text;
        }

        /// <summary>
        /// Only says anything while a search is running, so the row stays quiet at rest.
        /// </summary>
        private void updateSearchCount(IEnumerable<Item>? items)
        {
            itemSearchCountLabel.Text = string.IsNullOrEmpty(itemSearchBox.Text)
                ? string.Empty
                : $"{items?.Count() ?? 0} found";
        }

        public void updateUI()
        {
            updateGridItemsUI(model?.filteredItemList.value);
        }

        private const int ITEMS_PER_ROW = 3;
        private const double INVENTORY_ITEM_SIDE_LENGTH = 100;

        private void updateGridItemsUIReloadingAll(IEnumerable<Item>? items)
        {
            updateGridItemsUI(items, true);
        }

        private void updateGridItemsUI(IEnumerable<Item>? items, bool forceReloadAll = false)
        {
            if (_model?.profile.value == null || items == null)
            {
                inventoryCountLabel.Content = string.Empty;
                itemsGrid.RowDefinitions.Clear();
                itemsGrid.Children.Clear();
                return;
            }

            //Optimization. Only reload the grid if necessary or forced
            if(forceReloadAll == false && itemsGrid.Children.Count == items.Count())
            {
                return;
            }

            itemsGrid.RowDefinitions.Clear();
            itemsGrid.Children.Clear();

            int itemCount = 0;
            foreach (var item in items!)
            {
                var itemControl = new ItemControl();
                itemControl.item = item;
                itemControl.Height = INVENTORY_ITEM_SIDE_LENGTH;
                itemControl.Width = INVENTORY_ITEM_SIDE_LENGTH;

                var itemButton = new Button();
                itemButton.Background = null;
                itemButton.HorizontalAlignment = HorizontalAlignment.Center;
                itemButton.VerticalAlignment = VerticalAlignment.Center;
                itemButton.Height = INVENTORY_ITEM_SIDE_LENGTH;
                itemButton.Width = INVENTORY_ITEM_SIDE_LENGTH;
                itemButton.Margin = new Thickness(0);
                itemButton.Content = itemControl;
                itemButton.Command = new RelayCommand<Item>(_model!.selectItem);
                itemButton.CommandParameter = item;
                //A ContextMenu has one logical parent, so every tile needs its own rather
                //than sharing one. They stay empty - and cost nothing to render - until the
                //handler below fills one in on opening.
                itemButton.ContextMenu = new ContextMenu();
                itemButton.ContextMenuOpening += itemButton_ContextMenuOpening;

                if (itemCount % ITEMS_PER_ROW == 0)
                {
                    var rowDef = new RowDefinition();
                    rowDef.Height = new GridLength(INVENTORY_ITEM_SIDE_LENGTH);
                    itemsGrid.RowDefinitions.Add(rowDef);
                }

                itemsGrid.Children.Add(itemButton);
                Grid.SetRow(itemButton, itemCount / ITEMS_PER_ROW);
                Grid.SetColumn(itemButton, itemCount % ITEMS_PER_ROW);

                itemCount++;
            }

            var currentFilter = _model?.filter.value;
            if (currentFilter != null && currentFilter != ItemFilterEnum.Enchanted && currentFilter != ItemFilterEnum.All)
            {
                var newItemButton = new Button();
                newItemButton.HorizontalAlignment = HorizontalAlignment.Center;
                newItemButton.VerticalAlignment = VerticalAlignment.Center;
                newItemButton.Height = INVENTORY_ITEM_SIDE_LENGTH;
                newItemButton.Width = INVENTORY_ITEM_SIDE_LENGTH;
                newItemButton.Margin = new Thickness(0);
                newItemButton.Content = "+";
                newItemButton.Command = new RelayCommand<object>(_ => { this.addNewItemButton_Click(_model?.filter.value); });

                if (itemCount % ITEMS_PER_ROW == 0)
                {
                    var rowDef = new RowDefinition();
                    rowDef.Height = new GridLength(INVENTORY_ITEM_SIDE_LENGTH);
                    itemsGrid.RowDefinitions.Add(rowDef);
                }

                itemsGrid.Children.Add(newItemButton);
                Grid.SetRow(newItemButton, itemCount / ITEMS_PER_ROW);
                Grid.SetColumn(newItemButton, itemCount % ITEMS_PER_ROW);
            }

            //Slots used, NOT the number on screen: with a filter or a search active the
            //filtered count read as though the inventory had shrunk.
            var slotsUsed = _model?.totalItemCount ?? itemCount;
            string? gameContentString = R.getString("inventory_count");
            if(gameContentString != null)
            {
                gameContentString = gameContentString
                    .Replace("{current}", slotsUsed.ToString())
                    .Replace("{max}", Constants.MAXIMUM_INVENTORY_ITEM_COUNT.ToString());
            }
            else
            {
                gameContentString = R.formatITEMS_COUNT_LABEL(slotsUsed, Constants.MAXIMUM_INVENTORY_ITEM_COUNT);
            }
            inventoryCountLabel.Content = gameContentString;
        }

        #region Equipping

        private static readonly EquipmentSlotEnum[] HOTBAR_SLOTS = {
            EquipmentSlotEnum.HotbarSlot1,
            EquipmentSlotEnum.HotbarSlot2,
            EquipmentSlotEnum.HotbarSlot3,
        };

        /// <summary>
        /// Fills the right-click menu for whichever tile was clicked, and suppresses it
        /// where there is nothing to offer: the storage chest has no equipment slots, and
        /// an item whose type is not in the loaded game content belongs to no category, so
        /// there is no slot to put it in.
        ///
        /// Built on open rather than on grid load so that the "replaces" hints are current
        /// - equipping from the slots on the right does not rebuild this grid.
        /// </summary>
        private void itemButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (!(sender is Button button) || !(button.ContextMenu is ContextMenu menu)
                || !(button.CommandParameter is Item item)
                || !(_model is IEquipItems equipper))
            {
                e.Handled = true;
                return;
            }

            //Nothing to offer for an item whose type is not in the loaded game content: it
            //belongs to no category, so there is no slot it could go in.
            if (!item.isArtifact() && item.soleEquipmentSlot() == null)
            {
                e.Handled = true;
                return;
            }

            menu.Items.Clear();
            menu.Items.Add(ContextMenuFactory.header(item));
            menu.Items.Add(new Separator());

            if (item.isArtifact())
            {
                //Three hotbar slots and no way to guess which one is meant, so all three.
                for (int i = 0; i < HOTBAR_SLOTS.Length; i++)
                {
                    menu.Items.Add(equipMenuItem(equipper, item, HOTBAR_SLOTS[i], i + 1));
                }
            }
            else
            {
                menu.Items.Add(equipMenuItem(equipper, item, item.soleEquipmentSlot()!.Value, null));
            }
        }

        /// <summary>
        /// One equip option. The wording says whether the slot is free - "Equip" against
        /// "Equip over" - and what is in the way is named in the column where a keyboard
        /// shortcut would normally sit.
        /// </summary>
        private MenuItem equipMenuItem(IEquipItems equipper, Item item, EquipmentSlotEnum slot, int? slotNumber)
        {
            var occupant = equipper.getGearItem(slot);
            string header;
            if (slotNumber == null)
            {
                header = occupant == null ? R.EQUIP : R.EQUIP_OVER;
            }
            else
            {
                header = occupant == null
                    ? R.formatEQUIP_IN_SLOT(slotNumber.Value)
                    : R.formatEQUIP_OVER_IN_SLOT(slotNumber.Value);
            }

            var entry = new MenuItem { Header = header };
            if (occupant != null) { entry.InputGestureText = ContextMenuFactory.itemName(occupant); }

            entry.Click += (s, args) => {
                EventLogger.logEvent("equipItemMenuItem_Click",
                    new Dictionary<string, object>() { { "slot", slot.ToString() } });
                equipper.equipItem(item, slot);
            };
            return entry;
        }

        #endregion

        private void addNewItemButton_Click(ItemFilterEnum? currentFilter)
        {
            if (currentFilter == null || currentFilter == ItemFilterEnum.Enchanted || currentFilter == ItemFilterEnum.All) { return; }
            EventLogger.logEvent("addNewItemButton_Click", new Dictionary<string, object>() { { "currentFilter", currentFilter.ToString() } });
            var item = Constants.createDefaultItemForFilter(currentFilter!.Value);
            model?.addItemToList(item);
            model?.selectItem(item);
        }

        private void setItemFilter(ItemFilterEnum filter)
        {
            if(_model?.profile.value == null) { return; }
            _model!.filter.setValue = filter;
        }

        private void allItemsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model?.profile.value == null) { return; }
            EventLogger.logEvent("allItemsButton_Click");
            setItemFilter(ItemFilterEnum.All);
        }
        private void allMeleeItemsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model?.profile.value == null) { return; }
            EventLogger.logEvent("allMeleeItemsButton_Click");
            setItemFilter(ItemFilterEnum.MeleeWeapons);
        }
        private void allRangedItemsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model?.profile.value == null) { return; }
            EventLogger.logEvent("allRangedItemsButton_Click");
            setItemFilter(ItemFilterEnum.RangedWeapons);
        }
        private void allArmorItemsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model?.profile.value == null) { return; }
            EventLogger.logEvent("allArmorItemsButton_Click");
            setItemFilter(ItemFilterEnum.Armor);
        }
        private void allArtifactItemsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model?.profile.value == null) { return; }
            EventLogger.logEvent("allArtifactItemsButton_Click");
            setItemFilter(ItemFilterEnum.Artifacts);
        }
        private void allEnchantedItemsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model?.profile.value == null) { return; }
            EventLogger.logEvent("allEnchantedItemsButton_Click");
            setItemFilter(ItemFilterEnum.Enchanted);
        }

    }
}
