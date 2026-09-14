using MCDSaveEdit.Data;
using MCDSaveEdit.Interfaces;
using MCDSaveEdit.Logic;
using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Interaction logic for EquipmentScreen.xaml
    /// </summary>
    public partial class EquipmentScreen : UserControl
    {
        private static readonly BitmapImage? _levelFrameImageSource = ImageResolver.instance.imageSource("/Dungeons/Content/UI/Materials/Character/STATS_LV_frame");
        private static readonly BitmapImage? _powerFrameImageSource = ImageResolver.instance.imageSource("/Dungeons/Content/UI/Materials/Inventory2/Power/gearstrenght_icon");

        public static void preload() { }

        private IEquipItems? _model;
        public IEquipItems? model {
            get { return _model; }
            set {
                _model = value;
                setupCommands();
                //updateUI();
            }
        }

        public EquipmentScreen()
        {
            InitializeComponent();
            translateStaticStrings();
            if (AppModel.gameContentLoaded)
            {
                useGameContentImages();
            }
        }

        private void useGameContentImages()
        {
            levelLabelImage.Source = _levelFrameImageSource;
            powerLabelImage.Source = _powerFrameImageSource;
        }

        private void translateStaticStrings()
        {
            levelTitleLabel.Content = R.getString("HUD_Level") ?? R.LEVEL;
            powerTitleLabel.Content = R.getString("gearpower_POWER") ?? R.POWER;
        }

        private void setupCommands()
        {
            if (_model == null) { return; }
            var model = _model!;

            meleeGearSlotButton.Command = new RelayCommand<Item>(model.selectItem);
            armorGearSlotButton.Command = new RelayCommand<Item>(model.selectItem);
            rangedGearSlotButton.Command = new RelayCommand<Item>(model.selectItem);
            hotbarSlot1Button.Command = new RelayCommand<Item>(model.selectItem);
            hotbarSlot2Button.Command = new RelayCommand<Item>(model.selectItem);
            hotbarSlot3Button.Command = new RelayCommand<Item>(model.selectItem);
            newItemForMeleeGearSlotButton.Command = new RelayCommandImmutable<EquipmentSlotEnum>(addNewEquippedItemButton_Click);
            newItemForMeleeGearSlotButton.CommandParameter = EquipmentSlotEnum.MeleeGear;
            newItemForArmorGearSlotButton.Command = new RelayCommandImmutable<EquipmentSlotEnum>(addNewEquippedItemButton_Click);
            newItemForArmorGearSlotButton.CommandParameter = EquipmentSlotEnum.ArmorGear;
            newItemForRangedGearSlotButton.Command = new RelayCommandImmutable<EquipmentSlotEnum>(addNewEquippedItemButton_Click);
            newItemForRangedGearSlotButton.CommandParameter = EquipmentSlotEnum.RangedGear;
            newItemForHotbarSlot1Button.Command = new RelayCommandImmutable<EquipmentSlotEnum>(addNewEquippedItemButton_Click);
            newItemForHotbarSlot1Button.CommandParameter = EquipmentSlotEnum.HotbarSlot1;
            newItemForHotbarSlot2Button.Command = new RelayCommandImmutable<EquipmentSlotEnum>(addNewEquippedItemButton_Click);
            newItemForHotbarSlot2Button.CommandParameter = EquipmentSlotEnum.HotbarSlot2;
            newItemForHotbarSlot3Button.Command = new RelayCommandImmutable<EquipmentSlotEnum>(addNewEquippedItemButton_Click);
            newItemForHotbarSlot3Button.CommandParameter = EquipmentSlotEnum.HotbarSlot3;

            addUnequipMenu(meleeGearSlotButton);
            addUnequipMenu(armorGearSlotButton);
            addUnequipMenu(rangedGearSlotButton);
            addUnequipMenu(hotbarSlot1Button);
            addUnequipMenu(hotbarSlot2Button);
            addUnequipMenu(hotbarSlot3Button);

            //model.profile.subscribe(_ => this.updateUI());
            model.level.subscribe(updateLevelUI);
            model.equippedItemList.subscribe(_ => this.updateEquippedItemsUI());
            model.characterPower.subscribe(updateCharacterPowerUI);
        }

        public void updateUI()
        {
            updateLevelUI(_model?.level.value);
            updateEquippedItemsUI();
            updateCharacterPowerUI(_model?.characterPower.value);
        }

        private void updateLevelUI(int? level)
        {
            if (level != null)
            {
                levelTextBox.IsEnabled = false;
                levelTextBox.Text = level.ToString();
                levelTextBox.IsEnabled = true;
            }
            else
            {
                levelTextBox.IsEnabled = false;
                levelTextBox.Text = string.Empty;
            }
        }

        private void updateEquippedItemsUI()
        {
            if (_model == null || _model!.equipmentCanExist.value == false)
            {
                meleeGearSlotItemControl.item = null;
                meleeGearSlotButton.CommandParameter = null;
                meleeGearSlotButton.Visibility = Visibility.Visible;
                newItemForMeleeGearSlotButton.Visibility = Visibility.Collapsed;
                armorGearSlotItemControl.item = null;
                armorGearSlotButton.CommandParameter = null;
                armorGearSlotButton.Visibility = Visibility.Visible;
                newItemForArmorGearSlotButton.Visibility = Visibility.Collapsed;
                rangedGearSlotItemControl.item = null;
                rangedGearSlotButton.CommandParameter = null;
                rangedGearSlotButton.Visibility = Visibility.Visible;
                newItemForRangedGearSlotButton.Visibility = Visibility.Collapsed;
                hotbarSlot1ItemControl.item = null;
                hotbarSlot1Button.CommandParameter = null;
                hotbarSlot1Button.Visibility = Visibility.Visible;
                newItemForHotbarSlot1Button.Visibility = Visibility.Collapsed;
                hotbarSlot2ItemControl.item = null;
                hotbarSlot2Button.CommandParameter = null;
                hotbarSlot2Button.Visibility = Visibility.Visible;
                newItemForHotbarSlot2Button.Visibility = Visibility.Collapsed;
                hotbarSlot3ItemControl.item = null;
                hotbarSlot3Button.CommandParameter = null;
                hotbarSlot3Button.Visibility = Visibility.Visible;
                newItemForHotbarSlot3Button.Visibility = Visibility.Collapsed;

                powerLabel.Content = string.Empty;
                return;
            }
            var model = _model!;

            var meleeGearItem = model.meleeGearItem();
            meleeGearSlotItemControl.item = meleeGearItem;
            meleeGearSlotButton.CommandParameter = meleeGearItem;
            if (meleeGearItem != null)
            {
                meleeGearSlotButton.Visibility = Visibility.Visible;
                newItemForMeleeGearSlotButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                meleeGearSlotButton.Visibility = Visibility.Collapsed;
                newItemForMeleeGearSlotButton.Visibility = Visibility.Visible;
            }
            var armorGearItem = model.armorGearItem();
            armorGearSlotItemControl.item = armorGearItem;
            armorGearSlotButton.CommandParameter = armorGearItem;
            if (armorGearItem != null)
            {
                armorGearSlotButton.Visibility = Visibility.Visible;
                newItemForArmorGearSlotButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                armorGearSlotButton.Visibility = Visibility.Collapsed;
                newItemForArmorGearSlotButton.Visibility = Visibility.Visible;
            }
            var rangedGearItem = model.rangedGearItem();
            rangedGearSlotItemControl.item = rangedGearItem;
            rangedGearSlotButton.CommandParameter = rangedGearItem;
            if (rangedGearItem != null)
            {
                rangedGearSlotButton.Visibility = Visibility.Visible;
                newItemForRangedGearSlotButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                rangedGearSlotButton.Visibility = Visibility.Collapsed;
                newItemForRangedGearSlotButton.Visibility = Visibility.Visible;
            }
            var hotbarSlot1GearItem = model.hotbarSlot1Item();
            hotbarSlot1ItemControl.item = hotbarSlot1GearItem;
            hotbarSlot1Button.CommandParameter = hotbarSlot1GearItem;
            if (hotbarSlot1GearItem != null)
            {
                hotbarSlot1Button.Visibility = Visibility.Visible;
                newItemForHotbarSlot1Button.Visibility = Visibility.Collapsed;
            }
            else
            {
                hotbarSlot1Button.Visibility = Visibility.Collapsed;
                newItemForHotbarSlot1Button.Visibility = Visibility.Visible;
            }
            var hotbarSlot2GearItem = model.hotbarSlot2Item();
            hotbarSlot2ItemControl.item = hotbarSlot2GearItem;
            hotbarSlot2Button.CommandParameter = hotbarSlot2GearItem;
            if (hotbarSlot2GearItem != null)
            {
                hotbarSlot2Button.Visibility = Visibility.Visible;
                newItemForHotbarSlot2Button.Visibility = Visibility.Collapsed;
            }
            else
            {
                hotbarSlot2Button.Visibility = Visibility.Collapsed;
                newItemForHotbarSlot2Button.Visibility = Visibility.Visible;
            }
            var hotbarSlot3GearItem = model.hotbarSlot3Item();
            hotbarSlot3ItemControl.item = hotbarSlot3GearItem;
            hotbarSlot3Button.CommandParameter = hotbarSlot3GearItem;
            if (hotbarSlot3GearItem != null)
            {
                hotbarSlot3Button.Visibility = Visibility.Visible;
                newItemForHotbarSlot3Button.Visibility = Visibility.Collapsed;
            }
            else
            {
                hotbarSlot3Button.Visibility = Visibility.Collapsed;
                newItemForHotbarSlot3Button.Visibility = Visibility.Visible;
            }
        }

        private void updateCharacterPowerUI(int? power)
        {
            powerLabel.Content = power;
        }

        #region Unequipping

        /// <summary>
        /// Right-click on a filled slot to take the item off. The menu is built when it
        /// opens so it names whatever is in the slot at that moment.
        /// </summary>
        private void addUnequipMenu(Button button)
        {
            button.ContextMenu = new ContextMenu();
            button.ContextMenuOpening += slotButton_ContextMenuOpening;
        }

        private void slotButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            //An empty slot shows the "+" button instead of this one, and carries no item.
            if (_model == null || !(sender is Button button)
                || !(button.ContextMenu is ContextMenu menu)
                || !(button.CommandParameter is Item item))
            {
                e.Handled = true;
                return;
            }

            menu.Items.Clear();
            menu.Items.Add(ContextMenuFactory.header(item));
            menu.Items.Add(new Separator());

            var entry = new MenuItem { Header = R.UNEQUIP };
            entry.Click += (s, args) => unequipItem(item);
            menu.Items.Add(entry);
        }

        private void unequipItem(Item item)
        {
            EventLogger.logEvent("unequipMenuItem_Click",
                new Dictionary<string, object>() { { "slot", item.EquipmentSlot ?? "null" } });

            //Unequipping only adds to the inventory, so unlike equipping it can be refused.
            if (_model?.tryUnequipItem(item) == false)
            {
                MessageBox.Show(
                    R.formatINVENTORY_FULL_ERROR_MESSAGE(Constants.MAXIMUM_INVENTORY_ITEM_COUNT),
                    R.ERROR);
            }
        }

        #endregion

        private void addNewEquippedItemButton_Click(EquipmentSlotEnum equipmentSlot)
        {
            EventLogger.logEvent("addNewEquippedItemButton_Click", new Dictionary<string, object>() { { "equipmentSlot", equipmentSlot.ToString() } });
            var item = Constants.createDefaultItemForEquipmentSlot(equipmentSlot);
            _model?.addEquippedItem(item);
            _model?.selectItem(item);
        }

        private void levelTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_model == null || _model!.equipmentCanExist.value == false || !levelTextBox.IsEnabled) { return; }
            if (int.TryParse(levelTextBox.Text, out int level))
            {
                EventLogger.logEvent("levelTextBox_TextChanged", new Dictionary<string, object>() { { "level", level } });
                _model!.level.setValue = level;
            }
        }

        private void upButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model == null || _model!.equipmentCanExist.value == false || !levelTextBox.IsEnabled) { return; }
            if (int.TryParse(levelTextBox.Text, out int level) && level < Constants.MAXIMUM_CHARACTER_LEVEL)
            {
                int newLevel = level + 1;
                EventLogger.logEvent("levelTextBox_upButton_Click", new Dictionary<string, object>() { { "newLevel", newLevel } });
                _model!.level.setValue = newLevel;
            }
        }

        private void downButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model == null || _model!.equipmentCanExist.value == false || !levelTextBox.IsEnabled) { return; }
            if (int.TryParse(levelTextBox.Text, out int level) && level > Constants.MINIMUM_CHARACTER_LEVEL)
            {
                int newLevel = level - 1;
                EventLogger.logEvent("levelTextBox_downButton_Click", new Dictionary<string, object>() { { "newLevel", newLevel } });
                _model!.level.setValue = newLevel;
            }
        }
    }
}
