using MCDSaveEdit.Data;
using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Melee / Armor / Ranged / Other toggles above an enchantment list.
    ///
    /// Toggles rather than a single choice: putting an armor enchantment on a sword is a
    /// normal thing to want out of a save editor, so the other lists stay one click away. The
    /// item being enchanted decides which is on when the window opens, which is the answer
    /// wanted almost every time.
    ///
    /// Other - mob-exclusive, unused, and anything this app has not heard of - is a toggle of
    /// its own, off to begin with, so an ordinary choice is not padded out with enchantments
    /// no gear can roll.
    ///
    /// A search box sits under the toggles, because the categories narrow a hundred and
    /// eighteen enchantments to perhaps forty and knowing the name is faster than scrolling.
    /// It narrows within whatever the toggles let through, so the two compose rather than
    /// fight, and it matches the displayed name.
    ///
    /// Both selection windows host this, so the behaviour cannot drift between them.
    /// </summary>
    public class EnchantmentFilterBar : Border
    {
        private readonly StackPanel _row = new StackPanel();

        //The game's own words where it has them, which is what makes these follow the language
        //someone picked in the Language menu. The row of item filters beside this one has always
        //done that; this row was reading from the English resource alone, so it stayed English
        //in every language. Even "Other" has one, under ItemTag_Other.
        private static readonly (EnchantmentCategory category, Func<string> label)[] BUTTONS = {
            (EnchantmentCategory.Melee,  () => R.getString("ItemTag_Melee") ?? R.MELEE_ITEMS_FILTER),
            (EnchantmentCategory.Armor,  () => R.getString("ItemTag_Armor") ?? R.ARMOR_ITEMS_FILTER),
            (EnchantmentCategory.Ranged, () => R.getString("ItemTag_Ranged") ?? R.RANGED_ITEMS_FILTER),
            (EnchantmentCategory.Other,  () => R.getString("ItemTag_Other") ?? R.OTHER_ENCHANTMENTS_FILTER),
        };

        private readonly Dictionary<EnchantmentCategory, ToggleButton> _toggles =
            new Dictionary<EnchantmentCategory, ToggleButton>();

        private readonly SearchBox _search = new SearchBox(R.SEARCH_HINT);

        private bool _isProcessing;

        /// <summary>Raised when the user turns a category on or off, or types in the search box.</summary>
        public event Action? changed;

        /// <summary>Raised on Down or Enter in the search box, for focusing the list below.</summary>
        public event Action? advance;

        public EnchantmentCategory selected { get; private set; } = EnchantmentCategory.All;

        public EnchantmentFilterBar()
        {
            //Its own strip, in the menu colour, so the row reads as a header rather than as
            //controls floating on the window's ground.
            SetResourceReference(BackgroundProperty, "Brush.Menu");
            SetResourceReference(BorderBrushProperty, "Brush.Border");
            BorderThickness = new Thickness(0, 0, 0, 1);
            Padding = new Thickness(6, 7, 6, 7);

            _row.Orientation = Orientation.Horizontal;
            _row.HorizontalAlignment = HorizontalAlignment.Center;

            _search.Margin = new Thickness(3, 7, 3, 0);
            _search.changed += () => changed?.Invoke();
            _search.advance += () => advance?.Invoke();

            var column = new StackPanel { Orientation = Orientation.Vertical };
            column.Children.Add(_row);
            column.Children.Add(_search);
            Child = column;

            foreach (var (category, label) in BUTTONS)
            {
                var captured = category;
                var toggle = new ToggleButton {
                    Content = label(),
                    Margin = new Thickness(3, 0, 3, 0),
                    FontSize = 12,
                };
                toggle.Checked += (s, e) => onToggled(captured, true);
                toggle.Unchecked += (s, e) => onToggled(captured, false);
                _toggles.Add(category, toggle);
                _row.Children.Add(toggle);
            }
        }

        /// <summary>
        /// Turns on the category the item can roll, and nothing else. An item with no single
        /// answer - an artifact, or a type the game content does not know - opens with all
        /// three gear categories on, since there is nothing better to guess.
        /// </summary>
        public void selectForItem(EnchantmentCategory category)
        {
            set(category == EnchantmentCategory.None ? EnchantmentCategory.Gear : category);
        }

        private void set(EnchantmentCategory categories)
        {
            //Assigning IsChecked raises Checked/Unchecked, which would report a user action.
            _isProcessing = true;
            selected = categories;
            foreach (var pair in _toggles)
            {
                pair.Value.IsChecked = (categories & pair.Key) != EnchantmentCategory.None;
            }
            _isProcessing = false;
        }

        /// <summary>
        /// Whether an enchantment survives both the toggles and the search box.
        ///
        /// One call rather than two checks at each site, so neither window can apply half of
        /// the filter.
        /// </summary>
        public bool allows(string? enchantmentId)
        {
            if (!EnchantmentCategories.matches(enchantmentId, selected)) { return false; }
            if (enchantmentId == null) { return true; }
            return _search.matches(R.enchantmentName(enchantmentId));
        }

        public void focusSearch() => _search.focus();

        private void onToggled(EnchantmentCategory category, bool on)
        {
            if (_isProcessing) { return; }

            var updated = on ? selected | category : selected & ~category;
            if (updated == selected) { return; }
            selected = updated;
            changed?.Invoke();
        }
    }
}
