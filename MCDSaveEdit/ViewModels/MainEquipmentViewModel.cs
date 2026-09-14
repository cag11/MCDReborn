using MCDSaveEdit.Data;
using MCDSaveEdit.Interfaces;
using MCDSaveEdit.Logic;
using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.ViewModels
{
    public class MainEquipmentViewModel : ItemListViewModel, IEquipItems, IListItems
    {
        public MainEquipmentViewModel(Property<ProfileSaveFile?> profile) : base(profile)
        {
        }

        protected override IEnumerable<Item> items {
            get {
                return profile.value?.Items ?? new Item[0];
            }
            set {
                if (profile.value == null) return;
                profile.value!.Items = value.ToArray();
            }
        }

        public void addEquippedItem(Item item)
        {
            if (item == null || profile.value == null) { return; }
            var inventory = this.items;
            if (item.isGearItem())
            {
                var toRemove = item.EquipmentSlot;
                inventory = inventory.Where(i => i.EquipmentSlot != toRemove);
            }                
            this.items = inventory.adding(item);

            triggerSubscribersForItem(item);
        }

        /// <summary>
        /// Moves an item out of the inventory and into an equipment slot.
        ///
        /// Whatever was already in that slot is unequipped rather than dropped - that is
        /// what keeps two items from both claiming it, and it means nothing is ever lost
        /// by equipping over something.
        ///
        /// InventoryIndex is deliberately left alone. The inventory is keyed off
        /// EquipmentSlot being null, not off the index, so the item disappears from the
        /// grid either way, and keeping the index is what lets unequipping put it back
        /// where it came from.
        /// </summary>
        public void equipItem(Item item, EquipmentSlotEnum slot)
        {
            if (item == null || profile.value == null) { return; }

            var slotName = slot.ToString();
            var occupant = this.items.FirstOrDefault(
                x => x.EquipmentSlot == slotName && !ReferenceEquals(x, item));
            if (occupant != null) { unequipItem(occupant); }

            item.EquipmentSlot = slotName;

            triggerSubscribersForItem(item);
            refreshEquipmentLists();
        }

        /// <summary>
        /// Moves an equipped item back to the inventory, reporting false when there is no
        /// room for it.
        ///
        /// This is the only direction that can run out of space. Equipping swaps - one item
        /// leaves the inventory as another arrives - but unequipping only adds.
        /// </summary>
        public bool tryUnequipItem(Item item)
        {
            if (item == null || profile.value == null) { return false; }
            if (item.EquipmentSlot == null) { return true; }
            if (totalItemCount >= Constants.MAXIMUM_INVENTORY_ITEM_COUNT) { return false; }

            unequipItem(item);
            refreshEquipmentLists();
            return true;
        }

        /// <summary>
        /// Pushes both lists regardless of where the item ended up.
        ///
        /// triggerSubscribersForItem refreshes the grid only while the item has an
        /// InventoryIndex and the slots only while it has an EquipmentSlot, which is exactly
        /// wrong for a move between the two: one side of the move always fails its test and
        /// is left showing the item it no longer holds.
        /// </summary>
        private void refreshEquipmentLists()
        {
            refreshFilteredItemList();
            ((MappedProperty<ProfileSaveFile?, IEnumerable<Item>>)this.equippedItemList).value = this.equippedItemList.value;
        }

        protected override void triggerSubscribersForItem(Item item)
        {
            if (item.InventoryIndex != null)
            {
                ((MappedProperty<ItemFilterEnum, IEnumerable<Item>>)this.filteredItemList).value = this.filteredItemList.value;
            }
            if (item.EquipmentSlot != null)
            {
                ((MappedProperty<ProfileSaveFile?, IEnumerable<Item>>)this.equippedItemList).value = this.equippedItemList.value;
            }

            base.triggerSubscribersForItem(item);
        }

        public Item? getGearItem(EquipmentSlotEnum slot)
        {
            return equipmentSlot(equippedItemList.value, slot);
        }

        public Item? meleeGearItem()
        {
            return equipmentSlot(equippedItemList.value, EquipmentSlotEnum.MeleeGear);
        }
        public Item? armorGearItem()
        {
            return equipmentSlot(equippedItemList.value, EquipmentSlotEnum.ArmorGear);
        }
        public Item? rangedGearItem()
        {
            return equipmentSlot(equippedItemList.value, EquipmentSlotEnum.RangedGear);
        }
        public Item? hotbarSlot1Item()
        {
            return equipmentSlot(equippedItemList.value, EquipmentSlotEnum.HotbarSlot1);
        }
        public Item? hotbarSlot2Item()
        {
            return equipmentSlot(equippedItemList.value, EquipmentSlotEnum.HotbarSlot2);
        }
        public Item? hotbarSlot3Item()
        {
            return equipmentSlot(equippedItemList.value, EquipmentSlotEnum.HotbarSlot3);
        }

    }
}
