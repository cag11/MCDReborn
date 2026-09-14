using MCDSaveEdit.Data;
using MCDSaveEdit.Logic;
using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Services;
using MCDSaveEdit.Save.Models.Profiles;
using System;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.ViewModels
{
    public abstract class ItemListViewModel: EquipmentViewModel
    {
        protected static IEnumerable<Item> applyFilterToItems(IEnumerable<Item> items, ItemFilterEnum filter)
        {
            switch (filter)
            {
                case ItemFilterEnum.All: return items;
                case ItemFilterEnum.MeleeWeapons: return items.Where(x => x.isMeleeWeapon());
                case ItemFilterEnum.RangedWeapons: return items.Where(x => x.isRangedWeapon());
                case ItemFilterEnum.Armor: return items.Where(x => x.isArmor());
                case ItemFilterEnum.Artifacts: return items.Where(x => x.isArtifact());
                case ItemFilterEnum.Enchanted: return items.Where(x => x.enchantmentPoints() > 0);
            }
            throw new NotImplementedException();
        }
        /// <summary>
        /// Free-text narrowing on top of the category filter. Matches the item's
        /// display name first, then its raw type id, so "sword" and "Sword_Unique"
        /// both find something.
        /// </summary>
        protected static IEnumerable<Item> applySearchToItems(IEnumerable<Item> items, string? search)
        {
            if (string.IsNullOrWhiteSpace(search)) { return items; }
            var term = search!.Trim();
            return items.Where(x => itemMatchesSearch(x, term));
        }

        private static bool itemMatchesSearch(Item item, string term)
        {
            if (item.Type == null) { return false; }
            if (R.itemName(item.Type).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            return item.Type.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        protected static Item? equipmentSlot(IEnumerable<Item> itemList, EquipmentSlotEnum equipmentSlot)
        {
            var equipmentSlotString = equipmentSlot.ToString();
            return itemList.FirstOrDefault(x => x.EquipmentSlot == equipmentSlotString);
        }
        protected static int? computeCharacterPower(IEnumerable<Item> items)
        {
            var melee = equipmentSlot(items, EquipmentSlotEnum.MeleeGear)?.Power ?? 0;
            var armor = equipmentSlot(items, EquipmentSlotEnum.ArmorGear)?.Power ?? 0;
            var ranged = equipmentSlot(items, EquipmentSlotEnum.RangedGear)?.Power ?? 0;
            var slot1 = equipmentSlot(items, EquipmentSlotEnum.HotbarSlot1)?.Power ?? 0;
            var slot2 = equipmentSlot(items, EquipmentSlotEnum.HotbarSlot2)?.Power ?? 0;
            var slot3 = equipmentSlot(items, EquipmentSlotEnum.HotbarSlot3)?.Power ?? 0;
            var characterPower = GameCalculator.characterPowerFromEquippedItemPowers(melee, armor, ranged, slot1, slot2, slot3);
            var chacarterDisplayPower = GameCalculator.levelFromPower(characterPower);
            return chacarterDisplayPower;
        }

        public ItemListViewModel(Property<ProfileSaveFile?> profile) : base(profile)
        {
            equipmentCanExist = _profile.map<ProfileSaveFile?, bool>(p => p != null);
            level = _profile.map<ProfileSaveFile?, int?>(
                p => p?.level() ?? Constants.MINIMUM_CHARACTER_LEVEL,
                setXpFromLevel);
            filteredItemList = _filter.map(getFilteredItems);
            equippedItemList = _profile.map<ProfileSaveFile?, IEnumerable<Item>>(p => p?.Items?.Where(x => x.EquipmentSlot != null) ?? new Item[0]);
            characterPower = equippedItemList.map(computeCharacterPower);

            _profile.subscribe(p => {
                //_remainingEnchantmentPoints.setValue = p?.remainingEnchantmentPoints(); //Not Needed
                this.filter.setValue = ItemFilterEnum.All;
                this._searchText.setValue = string.Empty;
                _selectedItem.value = null;
            });
            _searchText.subscribe(_ => this.refreshFilteredItemList());
            level.subscribe(_ => this.updateEnchantmentPoints());
            equippedItemList.subscribe(_ => this.updateEnchantmentPoints());
            selectedItem.subscribe(_ => this.updateEnchantmentPoints());
        }

        private void setXpFromLevel(ProfileSaveFile? p, int? value)
        {
            if (p == null || !value.HasValue) { return; }
            p!.Xp = GameCalculator.experienceForLevel(value.Value);
        }
        protected abstract IEnumerable<Item> items { get; set; }

        protected IEnumerable<Item> getFilteredItems(ItemFilterEnum filter)
        {
            var items = this.items.Where(x => x.EquipmentSlot == null) ?? new Item[0];
            var matching = applySearchToItems(applyFilterToItems(items, filter), _searchText.value);
            //Sorts anything without an index last rather than throwing. An equipped item
            //carries no InventoryIndex, so a path that unequips one can otherwise take the
            //whole app down from here.
            return matching.OrderBy(x => x.InventoryIndex ?? long.MaxValue);
        }


        protected readonly Property<string> _searchText = new Property<string>(string.Empty);
        public IReadWriteProperty<string> searchText { get { return _searchText; } }

        /// <summary>
        /// filteredItemList is mapped from _filter alone, so a change to the search text
        /// has to push a new value through by hand. Setting MappedProperty.value calls
        /// sendNext, which is the same refresh the subclasses already use.
        /// </summary>
        protected void refreshFilteredItemList()
        {
            if (filteredItemList is MappedProperty<ItemFilterEnum, IEnumerable<Item>> mapped)
            {
                mapped.value = getFilteredItems(_filter.value);
            }
        }

        protected readonly Property<ItemFilterEnum> _filter = new Property<ItemFilterEnum>(ItemFilterEnum.All);
        public IReadWriteProperty<ItemFilterEnum> filter { get { return _filter; } }

        public IReadProperty<IEnumerable<Item>> filteredItemList { get; protected set; }

        public int totalItemCount => this.items.Count(x => x.EquipmentSlot == null);

        public void addItemToList(Item item)
        {
            if (item == null || profile.value == null) { return; }
            var inventory = addingItemToCollection(this.items, item);
            this.items = inventory;

            triggerSubscribersForItem(item);
        }

        /// <summary>
        /// Moves an equipped item back into the inventory, at the front of it.
        ///
        /// Clearing EquipmentSlot on its own is not enough: the inventory is ordered by
        /// InventoryIndex, so one has to be assigned on the way out. It goes to index 0 and
        /// everything already there moves up one - an item you just took off is the one you
        /// are looking at, and finding it meant scrolling to the bottom of three hundred.
        ///
        /// Renumbering rather than using "lowest - 1" keeps every index at zero or above,
        /// which is what the game's own saves look like. Adding one to each preserves the
        /// order they were already in.
        /// </summary>
        public void unequipItem(Item item)
        {
            if (item == null || profile.value == null) { return; }

            item.EquipmentSlot = null;
            foreach (var other in this.items)
            {
                if (ReferenceEquals(other, item) || other.EquipmentSlot != null) { continue; }
                other.InventoryIndex = (other.InventoryIndex ?? 0) + 1;
            }
            item.InventoryIndex = 0;

            triggerSubscribersForItem(item);
        }

        public void removeItem(Item item)
        {
            if (item == null || profile.value == null) { return; }
            var inventory = this.items.removing(item);
            this.items = inventory;

            triggerSubscribersForItem(item);
        }

        public override void saveItem(Item item)
        {
            if (item == null || profile.value == null || selectedItem.value == null) { return; }
            var inventory = this.items.replacing(selectedItem.value!, item);
            this.items = inventory;

            triggerSubscribersForItem(item);
        }

        protected IEnumerable<Item> addingItemToCollection(IEnumerable<Item> collection, Item item)
        {
            var index = getMaxIndex(collection) + 1;
            item.InventoryIndex = index;
            return collection.Append(item);
        }

        private long getFirstAvailableIndex(IEnumerable<Item> collection)
        {
            var inventoryIndexes = new HashSet<long>(collection.Select(i => i.InventoryIndex ?? 0));
            long index = 0;
            while (inventoryIndexes.Contains(index))
            {
                index++;
            }
            return index;
        }

        private long getMaxIndex(IEnumerable<Item> collection)
        {
            var list = collection.ToList();
            var maxIndex = list.Count > 0 ? list.Select(i => i.InventoryIndex ?? 0).Max() : -1;
            return maxIndex;
        }
    }
}
