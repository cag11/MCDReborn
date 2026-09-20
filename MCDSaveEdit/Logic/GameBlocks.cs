using System.Collections.Generic;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// What block id means what, generated from the converters' own table.
    ///
    /// A resource pack lists its blocks by name, and the key order looks like the id - air 0,
    /// stone 1, grass 2 - which holds until about a hundred and then quietly stops. The pack is
    /// written in Bedrock's order; a mission's blocks are numbered in Java's. By 161 they
    /// disagree: the pack says redstone_block, the map means leaves2, and a forest comes out
    /// painted in red blocks that are really dark oak.
    ///
    /// So the ids come from BlockMap.py - the table the Minecraft round trip already trusts and
    /// which demonstrably brings a map home again - and this says which of the pack's own names
    /// each one wears. Where Dungeons splits what the pack keeps together, a separate id for
    /// every colour of concrete against one id and a nibble, Meta carries the nibble to ask for.
    ///
    /// Fallback is for a block the mission's pack never ships at all. A forest has no reason to
    /// carry sixteen concrete textures, and Minecraft's own colour beats a grey hole.
    /// </summary>
    public static class GameBlocks
    {
        public readonly struct Block
        {
            public Block(string key, int meta, string name, uint fallback)
            {
                Key = key;
                Meta = meta;
                Name = name;
                Fallback = fallback;
            }

            /// <summary>What the mission's resource pack calls it, or "" when it has no name there.</summary>
            public string Key { get; }

            /// <summary>The nibble to look the texture up with, or -1 for the block's own.</summary>
            public int Meta { get; }

            /// <summary>What Minecraft calls it, for saying so out loud.</summary>
            public string Name { get; }

            /// <summary>Minecraft's own colour, for a block no pack here ships. Zero for none.</summary>
            public uint Fallback { get; }
        }

        /// <summary>Every id the converters know, by number.</summary>
        public static readonly Dictionary<int, Block> ALL = new Dictionary<int, Block>
        {
            { 0, new Block("air", -1, "air", 0x00000000u) },
            { 1, new Block("stone", -1, "stone", 0x00000000u) },
            { 2, new Block("grass", -1, "grass_block", 0x00000000u) },
            { 3, new Block("dirt", -1, "dirt", 0x00000000u) },
            { 4, new Block("cobblestone", -1, "cobblestone", 0x00000000u) },
            { 5, new Block("planks", -1, "oak_planks", 0x00000000u) },
            { 6, new Block("sapling", -1, "oak_sapling", 0x00000000u) },
            { 7, new Block("bedrock", -1, "bedrock", 0x00000000u) },
            { 8, new Block("water", -1, "water", 0x00000000u) },
            { 9, new Block("water", -1, "water", 0x00000000u) },
            { 10, new Block("lava", -1, "lava", 0x00000000u) },
            { 11, new Block("lava", -1, "lava", 0x00000000u) },
            { 12, new Block("sand", -1, "sand", 0x00000000u) },
            { 13, new Block("gravel", -1, "gravel", 0x00000000u) },
            { 14, new Block("gold_ore", -1, "gold_ore", 0x00000000u) },
            { 15, new Block("iron_ore", -1, "iron_ore", 0x00000000u) },
            { 16, new Block("coal_ore", -1, "coal_ore", 0x00000000u) },
            { 17, new Block("log", -1, "oak_log", 0x00000000u) },
            { 18, new Block("leaves", -1, "oak_leaves", 0x00000000u) },
            { 19, new Block("sponge.dry", -1, "sponge", 0x00000000u) },
            { 20, new Block("glass", -1, "glass", 0x00000000u) },
            { 21, new Block("lapis_ore", -1, "lapis_ore", 0x00000000u) },
            { 22, new Block("lapis_block", -1, "lapis_block", 0x00000000u) },
            { 23, new Block("dispenser", -1, "dispenser", 0x00000000u) },
            { 24, new Block("sandstone", -1, "sandstone", 0x00000000u) },
            { 25, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 26, new Block("bed", -1, "red_bed", 0x00000000u) },
            { 27, new Block("golden_rail", -1, "powered_rail", 0x00000000u) },
            { 28, new Block("detector_rail", -1, "detector_rail", 0x00000000u) },
            { 29, new Block("sticky_piston", -1, "sticky_piston", 0x00000000u) },
            { 30, new Block("web", -1, "cobweb", 0x00000000u) },
            { 31, new Block("tallgrass", -1, "grass", 0x00000000u) },
            { 35, new Block("wool", -1, "white_wool", 0x00000000u) },
            { 37, new Block("yellow_flower", -1, "dandelion", 0x00000000u) },
            { 38, new Block("red_flower", -1, "poppy", 0x00000000u) },
            { 39, new Block("brown_mushroom", -1, "brown_mushroom", 0x00000000u) },
            { 40, new Block("red_mushroom", -1, "red_mushroom", 0x00000000u) },
            { 41, new Block("gold_block", -1, "gold_block", 0x00000000u) },
            { 42, new Block("iron_block", -1, "iron_block", 0x00000000u) },
            { 43, new Block("stone_slab", -1, "smooth_stone_slab", 0x00000000u) },
            { 44, new Block("stone_slab", -1, "smooth_stone_slab", 0x00000000u) },
            { 45, new Block("brick_block", -1, "bricks", 0x00000000u) },
            { 46, new Block("tnt", -1, "tnt", 0x00000000u) },
            { 47, new Block("bookshelf", -1, "bookshelf", 0x00000000u) },
            { 48, new Block("mossy_cobblestone", -1, "mossy_cobblestone", 0x00000000u) },
            { 49, new Block("obsidian", -1, "obsidian", 0x00000000u) },
            { 50, new Block("torch", -1, "wall_torch", 0x00000000u) },
            { 51, new Block("fire", -1, "fire", 0x00000000u) },
            { 52, new Block("mob_spawner", -1, "spawner", 0x00000000u) },
            { 53, new Block("oak_stairs", -1, "oak_stairs", 0x00000000u) },
            { 54, new Block("chest", -1, "chest", 0x00000000u) },
            { 55, new Block("redstone_wire", -1, "redstone_wire", 0x00000000u) },
            { 56, new Block("diamond_ore", -1, "diamond_ore", 0x00000000u) },
            { 57, new Block("diamond_block", -1, "diamond_block", 0x00000000u) },
            { 58, new Block("crafting_table", -1, "crafting_table", 0x00000000u) },
            { 59, new Block("wheat", -1, "wheat", 0x00000000u) },
            { 60, new Block("farmland", -1, "farmland", 0x00000000u) },
            { 61, new Block("furnace", -1, "furnace", 0x00000000u) },
            { 63, new Block("standing_sign", -1, "oak_sign", 0x00000000u) },
            { 64, new Block("wooden_door", -1, "oak_door", 0x00000000u) },
            { 65, new Block("ladder", -1, "ladder", 0x00000000u) },
            { 66, new Block("rail", -1, "rail", 0x00000000u) },
            { 67, new Block("stone_stairs", -1, "cobblestone_stairs", 0x00000000u) },
            { 68, new Block("wall_sign", -1, "oak_wall_sign", 0x00000000u) },
            { 70, new Block("stone_pressure_plate", -1, "stone_pressure_plate", 0x00000000u) },
            { 73, new Block("redstone_ore", -1, "redstone_ore", 0x00000000u) },
            { 74, new Block("redstone_ore", -1, "redstone_ore", 0x00000000u) },
            { 75, new Block("redstone_torch", -1, "redstone_wall_torch", 0x00000000u) },
            { 76, new Block("redstone_torch", -1, "redstone_wall_torch", 0x00000000u) },
            { 77, new Block("stone_button", -1, "stone_button", 0x00000000u) },
            { 78, new Block("snow_layer", -1, "snow", 0x00000000u) },
            { 79, new Block("ice", -1, "ice", 0x00000000u) },
            { 80, new Block("snow", -1, "snow_block", 0x00000000u) },
            { 81, new Block("cactus", -1, "cactus", 0x00000000u) },
            { 82, new Block("clay", -1, "clay", 0x00000000u) },
            { 83, new Block("reeds", -1, "sugar_cane", 0x00000000u) },
            { 85, new Block("fence", -1, "oak_fence", 0x00000000u) },
            { 86, new Block("pumpkin", -1, "carved_pumpkin", 0x00000000u) },
            { 87, new Block("netherrack", -1, "netherrack", 0x00000000u) },
            { 88, new Block("soul_sand", -1, "soul_sand", 0x00000000u) },
            { 89, new Block("glowstone", -1, "glowstone", 0x00000000u) },
            { 91, new Block("lit_pumpkin", -1, "jack_o_lantern", 0x00000000u) },
            { 93, new Block("unpowered_repeater", -1, "repeater", 0x00000000u) },
            { 94, new Block("unpowered_repeater", -1, "repeater", 0x00000000u) },
            { 96, new Block("trapdoor", -1, "oak_trapdoor", 0x00000000u) },
            { 98, new Block("stonebrick", -1, "stone_bricks", 0x00000000u) },
            { 99, new Block("brown_mushroom_block", -1, "brown_mushroom_block", 0x00000000u) },
            { 100, new Block("red_mushroom_block", -1, "red_mushroom_block", 0x00000000u) },
            { 101, new Block("iron_bars", -1, "iron_bars", 0x00000000u) },
            { 102, new Block("glass_pane", -1, "glass_pane", 0x00000000u) },
            { 103, new Block("melon_block", -1, "melon", 0x00000000u) },
            { 104, new Block("pumpkin_stem", -1, "pumpkin_stem", 0x00000000u) },
            { 105, new Block("melon_stem", -1, "melon_stem", 0x00000000u) },
            { 106, new Block("vine", -1, "vine", 0x00000000u) },
            { 108, new Block("brick_stairs", -1, "brick_stairs", 0x00000000u) },
            { 109, new Block("stone_brick_stairs", -1, "stone_brick_stairs", 0x00000000u) },
            { 110, new Block("invisibleBedrock", -1, "podzol", 0x00000000u) },
            { 111, new Block("waterlily", -1, "lily_pad", 0x00000000u) },
            { 112, new Block("nether_brick", -1, "nether_bricks", 0x00000000u) },
            { 113, new Block("nether_brick_fence", -1, "nether_brick_fence", 0x00000000u) },
            { 114, new Block("nether_brick_stairs", -1, "nether_brick_stairs", 0x00000000u) },
            { 116, new Block("enchanting_table", -1, "enchanting_table", 0x00000000u) },
            { 117, new Block("brewing_stand", -1, "brewing_stand", 0x00000000u) },
            { 118, new Block("cauldron", -1, "cauldron", 0x00000000u) },
            { 120, new Block("end_portal_frame", -1, "end_portal_frame", 0x00000000u) },
            { 123, new Block("redstone_lamp", -1, "redstone_lamp", 0x00000000u) },
            { 125, new Block("dropper", -1, "dropper", 0x00000000u) },
            { 127, new Block("cocoa", -1, "cocoa", 0x00000000u) },
            { 128, new Block("sandstone_stairs", -1, "sandstone_stairs", 0x00000000u) },
            { 129, new Block("emerald_ore", -1, "emerald_ore", 0x00000000u) },
            { 133, new Block("emerald_block", -1, "emerald_block", 0x00000000u) },
            { 134, new Block("spruce_stairs", -1, "spruce_stairs", 0x00000000u) },
            { 135, new Block("birch_stairs", -1, "birch_stairs", 0x00000000u) },
            { 136, new Block("jungle_stairs", -1, "jungle_stairs", 0x00000000u) },
            { 137, new Block("double_stone_slab", -1, "blackstone_slab", 0x00000000u) },
            { 139, new Block("cobblestone_wall", -1, "cobblestone_wall", 0x00000000u) },
            { 141, new Block("carrots", -1, "carrots", 0x00000000u) },
            { 144, new Block("skull", -1, "skeleton_skull", 0x00000000u) },
            { 145, new Block("anvil", -1, "anvil", 0x00000000u) },
            { 149, new Block("unpowered_comparator", -1, "comparator", 0x00000000u) },
            { 150, new Block("unpowered_comparator", -1, "comparator", 0x00000000u) },
            { 152, new Block("redstone_block", -1, "redstone_block", 0x00000000u) },
            { 153, new Block("quartz_ore", -1, "quartz_ore", 0x00000000u) },
            { 155, new Block("quartz_block", -1, "quartz_block", 0x00000000u) },
            { 156, new Block("quartz_stairs", -1, "quartz_stairs", 0x00000000u) },
            { 157, new Block("wooden_slab", -1, "oak_slab", 0x00000000u) },
            { 158, new Block("wooden_slab", -1, "oak_slab", 0x00000000u) },
            { 159, new Block("stained_hardened_clay", -1, "white_terracotta", 0x00000000u) },
            { 161, new Block("leaves2", -1, "acacia_leaves", 0x00000000u) },
            { 162, new Block("log2", -1, "acacia_log", 0x00000000u) },
            { 163, new Block("acacia_stairs", -1, "acacia_stairs", 0x00000000u) },
            { 164, new Block("dark_oak_stairs", -1, "dark_oak_stairs", 0x00000000u) },
            { 167, new Block("iron_trapdoor", -1, "iron_trapdoor", 0x00000000u) },
            { 170, new Block("hay_block", -1, "hay_block", 0x00000000u) },
            { 171, new Block("carpet", -1, "white_carpet", 0x00000000u) },
            { 172, new Block("hardened_clay", -1, "terracotta", 0x00000000u) },
            { 173, new Block("coal_block", -1, "coal_block", 0x00000000u) },
            { 174, new Block("packed_ice", -1, "packed_ice", 0x00000000u) },
            { 175, new Block("double_plant", -1, "sunflower", 0x00000000u) },
            { 179, new Block("red_sandstone", -1, "red_sandstone", 0x00000000u) },
            { 180, new Block("red_sandstone_stairs", -1, "red_sandstone_stairs", 0x00000000u) },
            { 181, new Block("double_stone_slab2", -1, "red_sandstone_slab", 0x00000000u) },
            { 182, new Block("double_stone_slab2", -1, "red_sandstone_slab", 0x00000000u) },
            { 183, new Block("spruce_fence_gate", -1, "spruce_fence_gate", 0x00000000u) },
            { 184, new Block("birch_fence_gate", -1, "birch_fence_gate", 0x00000000u) },
            { 187, new Block("acacia_fence_gate", -1, "acacia_fence_gate", 0x00000000u) },
            { 193, new Block("spruce_door", -1, "spruce_door", 0x00000000u) },
            { 195, new Block("jungle_door", -1, "jungle_door", 0x00000000u) },
            { 197, new Block("dark_oak_door", -1, "dark_oak_door", 0x00000000u) },
            { 198, new Block("grass_path", -1, "dirt_path", 0x00000000u) },
            { 199, new Block("", -1, "dead_tube_coral_block", 0xFF847975u) },
            { 224, new Block("custom_0", -1, "white_concrete", 0xFFCFD5D6u) },
            { 225, new Block("custom_1", -1, "orange_concrete", 0xFFE06101u) },
            { 226, new Block("custom_2", -1, "magenta_concrete", 0xFFA9309Fu) },
            { 227, new Block("custom_3", -1, "light_blue_concrete", 0xFF2489C7u) },
            { 228, new Block("custom_4", -1, "yellow_concrete", 0xFFF1AF15u) },
            { 229, new Block("custom_5", -1, "lime_concrete", 0xFF5EA918u) },
            { 230, new Block("custom_6", -1, "pink_concrete", 0xFFD5658Fu) },
            { 231, new Block("custom_7", -1, "gray_concrete", 0xFF373A3Eu) },
            { 232, new Block("custom_8", -1, "light_gray_concrete", 0xFF7D7D73u) },
            { 233, new Block("custom_9", -1, "cyan_concrete", 0xFF157788u) },
            { 234, new Block("custom_10", -1, "blue_concrete", 0xFF2C2E8Fu) },
            { 235, new Block("custom_11", -1, "purple_concrete", 0xFF64209Cu) },
            { 236, new Block("custom_12", -1, "brown_concrete", 0xFF603C20u) },
            { 237, new Block("custom_13", -1, "green_concrete", 0xFF495B24u) },
            { 238, new Block("custom_14", -1, "red_concrete", 0xFF8E2121u) },
            { 239, new Block("custom_15", -1, "black_concrete", 0xFF080A0Fu) },
            { 243, new Block("mycelium", -1, "mycelium", 0x00000000u) },
            { 245, new Block("stonecutter", -1, "stonecutter", 0x00000000u) },
            { 251, new Block("", -1, "observer", 0xFF6D6D6Du) },
            { 267, new Block("", -1, "sea_lantern", 0xFFACB0A9u) },
            { 268, new Block("planks", 7, "warped_planks", 0x00000000u) },
            { 269, new Block("crimson_nylium", -1, "crimson_nylium", 0x00000000u) },
            { 270, new Block("warped_nylium", -1, "warped_nylium", 0x00000000u) },
            { 272, new Block("", -1, "honeycomb_block", 0xFFE5952Bu) },
            { 273, new Block("lodestone", -1, "lodestone", 0x00000000u) },
            { 274, new Block("", -1, "warped_wart_block", 0xFF167E86u) },
            { 276, new Block("planks", 6, "crimson_planks", 0x00000000u) },
            { 295, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 296, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 302, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 303, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 304, new Block("quartz_bricks", -1, "quartz_bricks", 0x00000000u) },
            { 305, new Block("basalt", -1, "basalt", 0x00000000u) },
            { 306, new Block("basalt", -1, "basalt", 0x00000000u) },
            { 307, new Block("basalt", -1, "basalt", 0x00000000u) },
            { 331, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 340, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 341, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 342, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 343, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 344, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 346, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 347, new Block("crying_obsidian", -1, "crying_obsidian", 0x00000000u) },
            { 348, new Block("", -1, "dried_kelp_block", 0xFF333F23u) },
            { 349, new Block("nether_gold_ore", -1, "nether_gold_ore", 0x00000000u) },
            { 351, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 352, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 353, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 354, new Block("polished_basalt", -1, "polished_basalt", 0x00000000u) },
            { 355, new Block("polished_basalt", -1, "polished_basalt", 0x00000000u) },
            { 356, new Block("polished_basalt", -1, "polished_basalt", 0x00000000u) },
            { 357, new Block("", -1, "crimson_stem", 0xFF6A3245u) },
            { 360, new Block("noteblock", -1, "note_block", 0x00000000u) },
            { 490, new Block("stripped_oak_log", -1, "stripped_oak_log", 0x00000000u) },
            { 519, new Block("wooden_slab", 6, "crimson_slab", 0x00000000u) },
            { 520, new Block("wooden_slab", 7, "warped_slab", 0x00000000u) },
        };

        /// <summary>The block an id means, or nothing when no table knows it.</summary>
        public static Block? of(int id) =>
            ALL.TryGetValue(id, out var found) ? found : (Block?)null;
    }
}
