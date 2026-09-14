using MCDSaveEdit.Data;
using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Interaction logic for EnchantmentSetControl.xaml
    /// </summary>
    public partial class EnchantmentSetControl : UserControl
    {
        public EnchantmentSetControl()
        {
            InitializeComponent();
            if (AppModel.gameContentLoaded)
            {
                useGameContentImages();
            }

            updateUI();
        }

        private void useGameContentImages()
        {
            backgroundImage.Source = ImageResolver.instance.imageSource("/Dungeons/Content/UI/Materials/StatusEffect/Enchantment/EnchantmentsBackground");
            topEnchantmentSymbolImage.Source = ImageResolver.instance.imageSource("/Dungeons/Content/UI/Materials/Mobs/enchant_common_icon");
        }

        private Enchantment[]? _enchantments;
        public IEnumerable<Enchantment>? enchantments
        {
            get { return _enchantments; }
            set { _enchantments = value?.ToArray(); updateUI(); }
        }

        /// <summary>
        /// The large icon in the middle of the diamond. -80 is what makes it fill the whole
        /// control the way the game draws an applied enchantment; the empty-slot placeholder
        /// uses a smaller one, because at -80 it overflowed the diamond and ran over the
        /// panel beside it.
        /// </summary>
        private static readonly Thickness APPLIED_MARGIN = new Thickness(-80);
        private static readonly Thickness PLACEHOLDER_MARGIN = new Thickness(-20);

        public void clearAll()
        {
            enchantment1Image.Source = null;
            enchantment1Button.CommandParameter = null;
            enchantment2Image.Source = null;
            enchantment2Button.CommandParameter = null;
            enchantment3Image.Source = null;
            enchantment3Button.CommandParameter = null;
            upgradedEnchantmentButton.Visibility = Visibility.Visible;
            upgradedEnchantmentImage.Margin = PLACEHOLDER_MARGIN;
            upgradedEnchantmentImage.Source = ImageResolver.instance.imageSourceForEnchantment(Constants.DEFAULT_ENCHANTMENT_ID);
            upgradedEnchantmentButton.CommandParameter = null;
        }
        public void updateUI()
        {
            if(_enchantments == null || _enchantments.Length == 0)
            {
                clearAll();
                return;
            }

            //Matches the game: once a choice in the slot has been taken, it is the only one
            //shown, drawn large, and the two that were not taken go away.
            var upgradedEnchantment = _enchantments.FirstOrDefault(x => x.Level > 0);
            if(upgradedEnchantment != null)
            {
                enchantment1Image.Source = null;
                enchantment1Button.CommandParameter = null;
                enchantment2Image.Source = null;
                enchantment2Button.CommandParameter = null;
                enchantment3Image.Source = null;
                enchantment3Button.CommandParameter = null;
                upgradedEnchantmentButton.Visibility = Visibility.Visible;
                upgradedEnchantmentImage.Margin = APPLIED_MARGIN;
                upgradedEnchantmentImage.Source = ImageResolver.instance.imageSourceForEnchantment(upgradedEnchantment);
                upgradedEnchantmentButton.CommandParameter = upgradedEnchantment;
            }
            else
            {
                fillSlot(enchantment1Button, enchantment1Image, 0);
                fillSlot(enchantment2Button, enchantment2Image, 1);
                fillSlot(enchantment3Button, enchantment3Image, 2);
                upgradedEnchantmentButton.Visibility = Visibility.Collapsed;
                upgradedEnchantmentImage.Source = null;
                upgradedEnchantmentButton.CommandParameter = null;
            }
        }

        /// <summary>
        /// One of the three choices in this slot. A set with fewer than three entries - which
        /// a hand-edited save can have - hides the leftovers rather than indexing past the
        /// end, which used to throw.
        /// </summary>
        private void fillSlot(Button button, Image image, int index)
        {
            if (_enchantments == null || index >= _enchantments.Length)
            {
                image.Source = null;
                button.CommandParameter = null;
                button.Visibility = Visibility.Hidden;
                return;
            }

            button.Visibility = Visibility.Visible;
            image.Source = ImageResolver.instance.imageSourceForEnchantment(_enchantments[index]);
            button.CommandParameter = _enchantments[index];
        }

        private ICommand? _command;
        public ICommand? command
        {
            get { return _command; }
            set { _command = value; updateCommand(); }
        }

        public void updateCommand()
        {
            enchantment1Button.Command = _command;
            enchantment2Button.Command = _command;
            enchantment3Button.Command = _command;
            upgradedEnchantmentButton.Command = _command;
        }
    }
}
