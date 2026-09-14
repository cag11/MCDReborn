using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using System.Collections.Generic;
#nullable enable

namespace MCDSaveEdit.ViewModels
{
    public interface IListItems
    {
        IReadProperty<ProfileSaveFile?> profile { get; }
        IReadWriteProperty<ItemFilterEnum> filter { get; }
        /// <summary>Free-text narrowing applied on top of <see cref="filter"/>.</summary>
        IReadWriteProperty<string> searchText { get; }
        IReadProperty<IEnumerable<Item>> filteredItemList { get; }
        /// <summary>Unequipped items held, ignoring filter and search.</summary>
        int totalItemCount { get; }
        void selectItem(Item item);

        void addItemToList(Item item);
    }
}
