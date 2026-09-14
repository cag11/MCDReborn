using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using System.Collections.Generic;
#nullable enable

namespace MCDSaveEdit.Interfaces
{
    public interface IEquipItems
    {
        IReadProperty<bool> equipmentCanExist { get; }
        IReadWriteProperty<int?> level { get; }
        IReadProperty<IEnumerable<Item>> equippedItemList { get; }
        void addEquippedItem(Item item);
        /// <summary>Moves an item already held into an equipment slot.</summary>
        void equipItem(Item item, EquipmentSlotEnum slot);
        /// <summary>
        /// Moves an equipped item back to the inventory. False when there is no room for it.
        /// </summary>
        bool tryUnequipItem(Item item);
        void selectItem(Item? item);
        IReadProperty<int?> remainingEnchantmentPoints { get; }
        Item? getGearItem(EquipmentSlotEnum slot);
        Item? meleeGearItem();
        Item? armorGearItem();
        Item? rangedGearItem();
        Item? hotbarSlot1Item();
        Item? hotbarSlot2Item();
        Item? hotbarSlot3Item();
        IReadProperty<int?> characterPower { get; }
    }
}
