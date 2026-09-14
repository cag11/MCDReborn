using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Pieces shared by the right-click menus on the inventory grid and on the equipment
    /// slots. Both open on an item and both name it before offering anything.
    /// </summary>
    public static class ContextMenuFactory
    {
        /// <summary>
        /// A caption naming the item the menu belongs to, so the menu says what it is acting
        /// on rather than leaving it to be inferred from whatever was under the cursor.
        ///
        /// Disabled, so it cannot be clicked or highlighted. Its colour is set locally, which
        /// outranks the template's disabled-grey trigger, so it reads as a title rather than
        /// as an option that happens to be unavailable.
        /// </summary>
        public static MenuItem header(Item? item)
        {
            var caption = new MenuItem {
                Header = itemName(item),
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
            };
            if (Application.Current?.TryFindResource("Brush.Accent") is Brush accent)
            {
                caption.Foreground = accent;
            }
            return caption;
        }

        /// <summary>Display name, falling back to the raw type where there is no translation.</summary>
        public static string itemName(Item? item)
        {
            if (item?.Type == null) { return string.Empty; }
            var name = R.itemName(item.Type!);
            return string.IsNullOrWhiteSpace(name) ? item.Type! : name;
        }
    }
}
