using MCDSaveEdit.Logic;
using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using MCDSaveEdit.ViewModels;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Interaction logic for InventoryTab.xaml
    /// </summary>
    public partial class ChestTab : UserControl
    {
        private static readonly BitmapImage? _enchantmentImage = ImageResolver.instance.imageSource("/Dungeons/Content/UI/Materials/Inventory2/Enchantment/enchantscore_background");

        public static void preload() { }

        private ProfileViewModel? _model;
        public ProfileViewModel? model {
            get { return _model; }
            set {
                _model = value;
                itemListScreen.model = _model?.storageChestEquipmentModel;
                setupCommands();
                //updateUI(); //Not Needed
            }
        }

        public ChestTab()
        {
            InitializeComponent();

            if (AppModel.gameContentLoaded)
            {
                useGameContentImages();
            }

            //Clear out design/testing values
            updateUI();
        }

        public void deleteCurrentSelectedItem()
        {
            if (selectedItemScreen.item != null)
            {
                removeItem(selectedItemScreen.item!);
            }
        }

        private void useGameContentImages()
        {
            remainingEnchantmentPointsLabelImage.Source = _enchantmentImage;
        }

        private void setupCommands()
        {
            if (_model == null) { return; }
            var model = _model!;

            model.profile.subscribe(_ => this.updateUI());

            var equipmentModel = model.storageChestEquipmentModel;

            selectedItemScreen.itemLocation = ItemLocationEnum.Chest;
            selectedItemScreen.selectEnchantment = new RelayCommand<Enchantment>(equipmentModel.selectEnchantment);
            selectedItemScreen.saveChanges = new RelayCommand<Item>(equipmentModel.saveItem);
            selectedItemScreen.duplicateItem = new RelayCommand<Item>(duplicateItem);
            selectedItemScreen.deleteItem = new RelayCommand<Item>(removeItem);
            itemListScreen.deleteItems = new RelayCommand<IEnumerable<Item>>(removeItems);
            selectedItemScreen.moveItemToInventory = new RelayCommand<Item>(moveItemToInventory);
            selectedItemScreen.addEnchantmentSlot = new RelayCommand<object>(equipmentModel.addEnchantmentSlot);
            selectedEnchantmentScreen.close = new RelayCommand<Enchantment>(equipmentModel.selectEnchantment);
            selectedEnchantmentScreen.saveChanges = new RelayCommand<Enchantment>(equipmentModel.saveEnchantment);

            equipmentModel.selectedItem.subscribe(item => this.selectedItemScreen.item = item);
            equipmentModel.selectedEnchantment.subscribe(updateEnchantmentScreenUI);
            equipmentModel.remainingEnchantmentPoints.subscribe(updateEnchantmentPointsUI);
        }

        private void duplicateItem(Item item)
        {
            model?.storageChestEquipmentModel?.selectEnchantment(null);
            model?.storageChestEquipmentModel?.addItemToList(item);
            model?.storageChestEquipmentModel?.selectItem(item);
        }

        /// <summary>
        /// Deletes a run of items chosen in the grid.
        ///
        /// The editor on the right is cleared first, once: it may be showing one of the items
        /// about to go, and a screen editing something that no longer exists is worse than an
        /// empty one. The model then removes them in a single pass rather than one at a time.
        /// </summary>
        private void removeItems(IEnumerable<Item> items)
        {
            if (items == null) { return; }
            model?.storageChestEquipmentModel?.selectEnchantment(null);
            model?.storageChestEquipmentModel?.selectItem(null);
            model?.storageChestEquipmentModel?.removeItems(items);
        }

        private void removeItem(Item item)
        {
            model?.storageChestEquipmentModel?.selectEnchantment(null);
            model?.storageChestEquipmentModel?.selectItem(null);
            model?.storageChestEquipmentModel?.removeItem(item);
        }

        private void moveItemToInventory(Item item)
        {
            model?.mainEquipmentModel?.selectEnchantment(null);
            model?.mainEquipmentModel?.addItemToList(item);
            model?.mainEquipmentModel?.selectItem(item);

            removeItem(item);
        }

        #region UI

        public void updateUI()
        {
            updateEnchantmentPointsUI(model?.storageChestEquipmentModel.remainingEnchantmentPoints.value);
            itemListScreen.updateUI();
            selectedItemScreen.item = model?.storageChestEquipmentModel?.selectedItem.value;
        }

        private void updateEnchantmentPointsUI(int? value)
        {
            remainingEnchantmentPointsLabel.Content = value?.ToString() ?? string.Empty;
        }

        private void updateEnchantmentScreenUI(Enchantment? enchantment)
        {
            if (enchantment == null)
            {
                selectedEnchantmentScreen.Visibility = Visibility.Collapsed;
                selectedEnchantmentScreenBackShadowRectangle.Visibility = Visibility.Collapsed;
            }
            else
            {
                selectedEnchantmentScreen.Visibility = Visibility.Visible;
                selectedEnchantmentScreenBackShadowRectangle.Visibility = Visibility.Visible;
                selectedEnchantmentScreen.enchantment = enchantment;
                selectedEnchantmentScreen.item = this.selectedItemScreen.item;
                selectedEnchantmentScreen.isGilded = this.selectedItemScreen.item?.NetheriteEnchant != null;
                selectedItemScreen.updateEnchantmentsUI();
            }
        }

        #endregion

    }
}
