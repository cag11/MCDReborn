using MCDSaveEdit;
using MCDSaveEdit.Data;
using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using MCDSaveEdit.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
#nullable enable

namespace MCDSaveEditTests.ViewModelTests
{
    [TestClass]
    public class MainEquipmentViewModelTests
    {
        [TestMethod]
        public void TestConstructor()
        {
            var property = new Property<ProfileSaveFile?>(null);
            var instance = new MainEquipmentViewModel(property);
            Assert.IsNotNull(instance);
        }

        [TestMethod]
        public void TestDefaultValues()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            Assert.AreEqual(true, instance.equipmentCanExist.value);
            Assert.AreEqual(Constants.MINIMUM_CHARACTER_LEVEL, instance.level.value);
            Assert.AreEqual(ItemFilterEnum.All, instance.filter.value);
            Assert.AreEqual(0, instance.filteredItemList.value.Count());
            Assert.AreEqual(0, instance.equippedItemList.value.Count());
            Assert.AreEqual(0, instance.characterPower.value);
        }

        [TestMethod]
        public void TestAddEquippedItems()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var melee = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.MeleeGear);
            instance.addEquippedItem(melee);
            var armor = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.ArmorGear);
            instance.addEquippedItem(armor);
            var ranged = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.RangedGear);
            instance.addEquippedItem(ranged);
            Assert.AreEqual(0, instance.filteredItemList.value.Count());
            Assert.AreEqual(3, instance.equippedItemList.value.Count());
            Assert.AreEqual(1, instance.characterPower.value);
        }

        [TestMethod]
        public void TestAddEquippedItemsMultipleTimes()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var melee = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.MeleeGear);
            var armor = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.ArmorGear);
            var ranged = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.RangedGear);
            instance.addEquippedItem(melee);
            instance.addEquippedItem(armor);
            instance.addEquippedItem(ranged);

            var melee2 = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.MeleeGear);
            melee2.Type = "AnotherSword";
            instance.addEquippedItem(melee2);
            Assert.AreEqual(0, instance.filteredItemList.value.Count());
            Assert.AreEqual(3, instance.equippedItemList.value.Count());
            Assert.AreEqual(1, instance.characterPower.value);
        }

        [TestMethod]
        public void TestAddItemsToList()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var melee = Constants.createDefaultItemForFilter(ItemFilterEnum.MeleeWeapons);
            instance.addItemToList(melee);
            var armor = Constants.createDefaultItemForFilter(ItemFilterEnum.Armor);
            instance.addItemToList(armor);
            var ranged = Constants.createDefaultItemForFilter(ItemFilterEnum.RangedWeapons);
            instance.addItemToList(ranged);
            Assert.AreEqual(3, instance.filteredItemList.value.Count());
            Assert.AreEqual(0, instance.equippedItemList.value.Count());
            Assert.AreEqual(0, instance.characterPower.value);
        }

        [TestMethod]
        public void TestEquipItemFromInventoryIntoEmptySlot()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var melee = Constants.createDefaultItemForFilter(ItemFilterEnum.MeleeWeapons);
            instance.addItemToList(melee);

            instance.equipItem(melee, EquipmentSlotEnum.MeleeGear);

            Assert.AreEqual(0, instance.filteredItemList.value.Count());
            Assert.AreEqual(1, instance.equippedItemList.value.Count());
            Assert.AreSame(melee, instance.meleeGearItem());
        }

        [TestMethod]
        public void TestEquipItemOverOccupiedSlotReturnsTheOccupant()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var worn = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.MeleeGear);
            instance.addEquippedItem(worn);
            var spare = Constants.createDefaultItemForFilter(ItemFilterEnum.MeleeWeapons);
            spare.Type = "AnotherSword";
            instance.addItemToList(spare);

            instance.equipItem(spare, EquipmentSlotEnum.MeleeGear);

            //The displaced item goes back to the inventory rather than being dropped, and
            //the slot is left holding exactly one item.
            Assert.AreSame(spare, instance.meleeGearItem());
            Assert.AreEqual(1, instance.equippedItemList.value.Count());
            var inventory = instance.filteredItemList.value.ToList();
            Assert.AreEqual(1, inventory.Count);
            Assert.AreSame(worn, inventory[0]);
            Assert.IsNotNull(worn.InventoryIndex);
        }

        [TestMethod]
        public void TestEquipArtifactsIntoEachHotbarSlot()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var first = Constants.createDefaultItemForFilter(ItemFilterEnum.Artifacts);
            var second = Constants.createDefaultItemForFilter(ItemFilterEnum.Artifacts);
            var third = Constants.createDefaultItemForFilter(ItemFilterEnum.Artifacts);
            instance.addItemToList(first);
            instance.addItemToList(second);
            instance.addItemToList(third);

            instance.equipItem(first, EquipmentSlotEnum.HotbarSlot1);
            instance.equipItem(second, EquipmentSlotEnum.HotbarSlot2);
            instance.equipItem(third, EquipmentSlotEnum.HotbarSlot3);

            Assert.AreEqual(0, instance.filteredItemList.value.Count());
            Assert.AreEqual(3, instance.equippedItemList.value.Count());
            Assert.AreSame(first, instance.hotbarSlot1Item());
            Assert.AreSame(second, instance.hotbarSlot2Item());
            Assert.AreSame(third, instance.hotbarSlot3Item());
        }

        [TestMethod]
        public void TestEquipItemIntoTheSlotItAlreadyOccupiesKeepsIt()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var worn = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.HotbarSlot2);
            instance.addEquippedItem(worn);

            instance.equipItem(worn, EquipmentSlotEnum.HotbarSlot2);

            //Equipping an item where it already is must not treat it as its own occupant
            //and unequip it.
            Assert.AreSame(worn, instance.hotbarSlot2Item());
            Assert.AreEqual(1, instance.equippedItemList.value.Count());
            Assert.AreEqual(0, instance.filteredItemList.value.Count());
        }

        [TestMethod]
        public void TestEquipItemMovesItBetweenHotbarSlots()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var artifact = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.HotbarSlot1);
            instance.addEquippedItem(artifact);

            instance.equipItem(artifact, EquipmentSlotEnum.HotbarSlot3);

            Assert.IsNull(instance.hotbarSlot1Item());
            Assert.AreSame(artifact, instance.hotbarSlot3Item());
            Assert.AreEqual(1, instance.equippedItemList.value.Count());
        }

        [TestMethod]
        public void TestUnequipItemMovesItToTheInventory()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var worn = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.ArmorGear);
            instance.addEquippedItem(worn);

            Assert.IsTrue(instance.tryUnequipItem(worn));

            Assert.IsNull(instance.armorGearItem());
            Assert.AreEqual(0, instance.equippedItemList.value.Count());
            var inventory = instance.filteredItemList.value.ToList();
            Assert.AreEqual(1, inventory.Count);
            Assert.AreSame(worn, inventory[0]);
            Assert.IsNull(worn.EquipmentSlot);
            Assert.IsNotNull(worn.InventoryIndex);
        }

        [TestMethod]
        public void TestUnequippedItemLandsAtTheFrontOfTheInventory()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var first = Constants.createDefaultItemForFilter(ItemFilterEnum.Armor);
            first.Type = "FirstArmor";
            var second = Constants.createDefaultItemForFilter(ItemFilterEnum.Armor);
            second.Type = "SecondArmor";
            instance.addItemToList(first);
            instance.addItemToList(second);
            var worn = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.MeleeGear);
            instance.addEquippedItem(worn);

            Assert.IsTrue(instance.tryUnequipItem(worn));

            //The item just taken off is the one being looked at, so it goes first, and the
            //items already there keep the order they were in behind it.
            var inventory = instance.filteredItemList.value.ToList();
            Assert.AreEqual(3, inventory.Count);
            Assert.AreSame(worn, inventory[0]);
            Assert.AreSame(first, inventory[1]);
            Assert.AreSame(second, inventory[2]);
            Assert.AreEqual(0L, worn.InventoryIndex);
            //Every index stays at zero or above, the way the game's own saves look.
            Assert.IsTrue(inventory.All(x => (x.InventoryIndex ?? 0) >= 0));
        }

        [TestMethod]
        public void TestEquipOverAlsoPutsTheDisplacedItemFirst()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var held = Constants.createDefaultItemForFilter(ItemFilterEnum.MeleeWeapons);
            held.Type = "HeldSword";
            instance.addItemToList(held);
            var worn = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.MeleeGear);
            instance.addEquippedItem(worn);
            var spare = Constants.createDefaultItemForFilter(ItemFilterEnum.MeleeWeapons);
            spare.Type = "SpareSword";
            instance.addItemToList(spare);

            instance.equipItem(spare, EquipmentSlotEnum.MeleeGear);

            var inventory = instance.filteredItemList.value.ToList();
            Assert.AreSame(worn, inventory[0]);
            Assert.AreSame(spare, instance.meleeGearItem());
        }

        [TestMethod]
        public void TestUnequipItemIsRefusedWhenTheInventoryIsFull()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var worn = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.MeleeGear);
            instance.addEquippedItem(worn);
            for (int i = 0; i < Constants.MAXIMUM_INVENTORY_ITEM_COUNT; i++)
            {
                instance.addItemToList(Constants.createDefaultItemForFilter(ItemFilterEnum.Armor));
            }

            //Unequipping only adds, so a full inventory has to refuse rather than overflow.
            Assert.IsFalse(instance.tryUnequipItem(worn));

            Assert.AreSame(worn, instance.meleeGearItem());
            Assert.AreEqual(Constants.MAXIMUM_INVENTORY_ITEM_COUNT, instance.totalItemCount);
        }

        [TestMethod]
        public void TestEquippingOverSomethingNeedsNoFreeSpace()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var worn = Constants.createDefaultItemForEquipmentSlot(EquipmentSlotEnum.MeleeGear);
            instance.addEquippedItem(worn);
            for (int i = 0; i < Constants.MAXIMUM_INVENTORY_ITEM_COUNT - 1; i++)
            {
                instance.addItemToList(Constants.createDefaultItemForFilter(ItemFilterEnum.Armor));
            }
            var spare = Constants.createDefaultItemForFilter(ItemFilterEnum.MeleeWeapons);
            spare.Type = "AnotherSword";
            instance.addItemToList(spare);

            //One out, one in: equipping over something is a swap, so a full inventory is fine.
            instance.equipItem(spare, EquipmentSlotEnum.MeleeGear);

            Assert.AreSame(spare, instance.meleeGearItem());
            Assert.AreEqual(Constants.MAXIMUM_INVENTORY_ITEM_COUNT, instance.totalItemCount);
        }

        [TestMethod]
        public void TestUnequipItemThatIsNotEquippedChangesNothing()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new MainEquipmentViewModel(property);
            var loose = Constants.createDefaultItemForFilter(ItemFilterEnum.Armor);
            instance.addItemToList(loose);

            Assert.IsTrue(instance.tryUnequipItem(loose));

            Assert.AreEqual(1, instance.filteredItemList.value.Count());
            Assert.AreEqual(0, instance.equippedItemList.value.Count());
        }

        [TestMethod]
        public void TestEquipItemWithoutAProfileDoesNothing()
        {
            var property = new Property<ProfileSaveFile?>(null);
            var instance = new MainEquipmentViewModel(property);
            var melee = Constants.createDefaultItemForFilter(ItemFilterEnum.MeleeWeapons);

            instance.equipItem(melee, EquipmentSlotEnum.MeleeGear);

            Assert.IsNull(melee.EquipmentSlot);
        }

    }

    [TestClass]
    public class StorageChestEquipmentViewModelTests
    {
        [TestMethod]
        public void TestConstructor()
        {
            var property = new Property<ProfileSaveFile?>(null);
            var instance = new StorageChestEquipmentViewModel(property);
            Assert.IsNotNull(instance);
        }

        [TestMethod]
        public void TestDefaultValues()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new StorageChestEquipmentViewModel(property);
            Assert.AreEqual(ItemFilterEnum.All, instance.filter.value);
            Assert.AreEqual(true, instance.equipmentCanExist.value);
            Assert.AreEqual(Constants.MINIMUM_CHARACTER_LEVEL, instance.level.value);
            Assert.AreEqual(0, instance.remainingEnchantmentPoints.value);
            Assert.AreEqual(0, instance.filteredItemList.value.Count());
            Assert.AreEqual(0, instance.equippedItemList.value.Count());
            Assert.AreEqual(0, instance.characterPower.value);
        }

        [TestMethod]
        public void TestAddItemsToList()
        {
            var property = new Property<ProfileSaveFile?>(new ProfileSaveFile());
            var instance = new StorageChestEquipmentViewModel(property);
            var melee = Constants.createDefaultItemForFilter(ItemFilterEnum.MeleeWeapons);
            instance.addItemToList(melee);
            var armor = Constants.createDefaultItemForFilter(ItemFilterEnum.Armor);
            instance.addItemToList(armor);
            var ranged = Constants.createDefaultItemForFilter(ItemFilterEnum.RangedWeapons);
            instance.addItemToList(ranged);
            Assert.AreEqual(3, instance.filteredItemList.value.Count());
            Assert.AreEqual(0, instance.equippedItemList.value.Count());
            Assert.AreEqual(0, instance.characterPower.value);
        }


    }
}
