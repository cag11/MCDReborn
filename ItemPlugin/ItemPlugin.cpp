// MCD Reborn's item plugin: registers custom item ids inside Minecraft Dungeons.
//
// The game's item list is C++. Each item is registered once at startup by a static block that
// builds an item description and hands it to the game's registration routine, which allocates a
// 0x230-byte record and adds it to three things: a map from id to record, an array of records and
// a map from id to its index in that array. Nothing a pak can reach adds to them, and an id that is
// not in the first map is deleted from a save when the save loads.
//
// This does what the static blocks do, for ids MCD Reborn lists in MCDRebornItems.txt beside this
// DLL. Each new item is a copy of an existing item's record - its type, flags, stats arrays and
// base item are what it should inherit - with the parts that name it replaced: its id, its folder,
// its six asset paths, its name and its description. Then it goes into the same three structures,
// through the game's own container functions, the way the registration routine does it.
//
// Every game function is found by byte pattern, never by address, and cross-checked before
// anything is written: a pattern that matches nothing, or matches something that does not behave
// as expected, stops the plugin with a line in MCDRebornItems.log and changes nothing in the game.
//
// Build: build.bat (MSVC x64). Loaded by MCD Reborn after the game starts.

#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace
{
    // ---------------------------------------------------------------- engine types, as laid out in 4.22

    struct FName { int32_t Index; int32_t Number; };
    struct FString { wchar_t* Data; int32_t Num; int32_t Max; };
    struct FText { void* Data; void* Controller; uint32_t Flags; uint32_t Pad; };
    struct TArrayRaw { uint8_t* Data; int32_t Num; int32_t Max; };

    // The registry: map 1 (id -> record) at 0, map 2 (id -> index) at 0x50, the array at 0xA0.
    constexpr size_t MAP1_AT = 0x00;
    constexpr size_t MAP2_AT = 0x50;
    constexpr size_t ARRAY_AT = 0xA0;
    constexpr size_t MAP1_ELEMENT = 24;       // FName key, record pointer, hash links

    // The record: what a copy must change.
    constexpr size_t RECORD_SIZE = 0x230;
    constexpr size_t ID_AT = 0x00;
    constexpr size_t ID_AGAIN_AT = 0x0C;
    constexpr size_t NAME_AT = 0x18;
    constexpr size_t FOLDER_AT = 0x30;
    constexpr size_t FLAVOUR_AT = 0x58;
    constexpr size_t NO_LOOT_AT = 0xA8;       // 1 on the game's own cut items: never generated as loot
    constexpr size_t CLASS_CACHE_A = 0x18C;   // weak pointers to the loaded Storable / Instance class
    constexpr size_t CLASS_CACHE_B = 0x194;
    constexpr size_t PATHS_AT = 0x1F8;        // six FNames: icon, inventory icon, gear icon, ammo icon, storable, instance
    // What an item says it is and what it does beyond its blueprint, read off the live registry:
    //  - its property lines, the tooltip's "Spin attack" or "Dual Wield": 0x30 each - the text,
    //    then an icon and a tag byte and an optional enchantment (16 bytes, set-flag at +0x2C),
    //    all left empty by the builder the game's own items use (b19860). Written as bare 0x18
    //    FTexts the first time, the tooltip read the second line out of the middle of the first
    //    and crashed on a reference count at -1 (copy loop at af3a59, which walks 0x30);
    //  - its built-in enchantments, Firebrand's Fire Aspect: FEnchantmentData (type id, level,
    //    category, source, invested points) and a byte the game sets to 2 on a unique's, 0x14 each.
    //    A built-in's own tooltip line ("Burns Mobs") is not stored: the game adds it from the
    //    enchantment when it draws the item.
    constexpr size_t LINES_AT = 0x70;
    constexpr size_t LINE_SIZE = 0x30;
    constexpr size_t BUILTINS_AT = 0x1B8;
    constexpr size_t BUILTIN_SIZE = 0x14;

    using FNameCtor = FName* (__fastcall*)(FName* self, const char* name, int findType);
    using CreateText = FText* (__fastcall*)(FText* out, const wchar_t* source, const wchar_t* space, const wchar_t* key);
    using Accessor = uint8_t* (__fastcall*)();
    using Malloc = void* (__fastcall*)(size_t size);
    using MapAdd = void* (__fastcall*)(void* map, int32_t* outId, void* args, bool* alreadyThere);
    using MapFind = int32_t* (__fastcall*)(void* map, int32_t* outIndex, FName key);
    using ArrayGrow = void (__fastcall*)(void* array, int32_t oldNum);
    using Refresh = void (__fastcall*)(void* finder, bool force);
    using StaticClass = void* (__fastcall*)();
    using AddPath = void (__fastcall*)(void* finder, void* assetClass, FName path, FName id);

    // The item asset finder: the game's index from item id to the paths of its blueprints and
    // icons, built from the asset registry for every id in the item list AT THE TIME IT IS BUILT.
    // An id registered later is not in it, and the first thing that asks for its icon or its
    // blueprint dereferences the missing entry - the crash the first live test hit. It lives
    // inside the game's Dungeons module, which hands it out from its virtual at +0x48, and has a
    // refresh that rebuilds it on request.
    //
    // The refresh does not read the item list. It indexes the ids in the finder's path table
    // (+0x78: path name -> item id and asset class, six rows an item), which the finder's
    // constructor fills once from the item list. A later id has to be put in that table first,
    // with the constructor's own routine and the same classes, and only then refreshed.
    constexpr size_t FINDER_MAP_AT = 0xD8;
    constexpr size_t FINDER_ELEMENT = 0x60;
    constexpr size_t FINDER_PATHS_AT = 0x78;
    constexpr size_t FINDER_PATH_ELEMENT = 0x20;
    // The record's six paths, in the order and with the class the constructor files each under:
    // the instance class, the storable class, then four textures.
    struct RecordPath { size_t at; int cls; };
    constexpr RecordPath RECORD_PATHS[6] = { {0x220, 0}, {0x218, 1}, {0x200, 2}, {0x1F8, 2}, {0x208, 2}, {0x210, 2} };

    struct Game
    {
        FNameCtor fname = nullptr;
        CreateText createText = nullptr;
        Accessor registry = nullptr;
        Malloc malloc = nullptr;
        MapAdd map1Add = nullptr;
        MapAdd map2Add = nullptr;
        MapFind find = nullptr;
        ArrayGrow arrayGrow = nullptr;
        Refresh refresh = nullptr;
        AddPath addPath = nullptr;            // the finder's own: one of an item's paths into its path table
        StaticClass pathClass[3]{};           // the classes the finder files those paths under
        uint8_t** module = nullptr;           // the Dungeons module, once the engine has made it
    };

    FILE* g_log = nullptr;
    std::wstring g_folder;

    void say(const char* format, ...)
    {
        if (!g_log) { return; }
        SYSTEMTIME now; GetLocalTime(&now);
        fprintf(g_log, "%02d:%02d:%02d.%03d  ", now.wHour, now.wMinute, now.wSecond, now.wMilliseconds);
        va_list args; va_start(args, format); vfprintf(g_log, format, args); va_end(args);
        fputc('\n', g_log);
        fflush(g_log);
    }

    // ---------------------------------------------------------------- finding the game's code

    struct Pattern { std::vector<int> bytes; };

    Pattern parse(const char* text)
    {
        Pattern p;
        for (const char* at = text; *at; )
        {
            while (*at == ' ') { at++; }
            if (!*at) { break; }
            if (at[0] == '?') { p.bytes.push_back(-1); at += (at[1] == '?') ? 2 : 1; continue; }
            p.bytes.push_back(static_cast<int>(strtol(std::string(at, 2).c_str(), nullptr, 16)));
            at += 2;
        }
        return p;
    }

    // Every match in the main module's readable, committed memory. The image is scanned region by
    // region, because a packed game leaves pages that cannot be read, and touching one is a crash.
    std::vector<uint8_t*> scan(const Pattern& pattern)
    {
        std::vector<uint8_t*> found;
        auto base = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
        auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
        auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
        uint8_t* end = base + nt->OptionalHeader.SizeOfImage;

        MEMORY_BASIC_INFORMATION info;
        for (uint8_t* at = base; at < end && VirtualQuery(at, &info, sizeof(info)); at = static_cast<uint8_t*>(info.BaseAddress) + info.RegionSize)
        {
            const DWORD readable = PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_WRITECOPY | PAGE_WRITECOPY;
            if (info.State != MEM_COMMIT || !(info.Protect & readable) || (info.Protect & PAGE_GUARD)) { continue; }
            auto from = static_cast<uint8_t*>(info.BaseAddress);
            size_t size = info.RegionSize;
            size_t n = pattern.bytes.size();
            for (size_t i = 0; i + n <= size; i++)
            {
                size_t j = 0;
                for (; j < n; j++)
                {
                    int want = pattern.bytes[j];
                    if (want >= 0 && from[i + j] != static_cast<uint8_t>(want)) { break; }
                }
                if (j == n) { found.push_back(from + i); }
            }
        }
        return found;
    }

    // The target of the `call rel32` at `at`, or null when there is no call there.
    uint8_t* callTarget(uint8_t* at)
    {
        if (at[0] != 0xE8) { return nullptr; }
        int32_t rel; memcpy(&rel, at + 1, 4);
        return at + 5 + rel;
    }

    // The registration routine, and the helpers it calls, at the offsets its own calls sit at. Read
    // from the routine itself rather than scanned for: the container functions are template code
    // that the compiler emitted many identical copies of, and only these are the ones it uses.
    bool find(Game& game)
    {
        auto reg = scan(parse("40 55 53 56 57 41 54 41 56 41 57 48 8D 6C 24 90 48 81 EC 10 02 00 00 48"));
        auto text = scan(parse("48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 30 49 8B D9 49 8B F8 48 8B F2 48 8B E9 E8 ?? ?? ?? ?? 48 8B C8 48 89 5C 24 20 4C 8B CF"));
        auto names = scan(parse("40 53 48 83 EC 30 48 8B D9 48 85 D2 74 21 45 8B C8 C7 44 24 28 FF FF FF FF 45 33 C0 C6 44 24 20 01 E8"));
        say("patterns: register %zu, create text %zu, name constructors %zu", reg.size(), text.size(), names.size());
        if (reg.size() != 1 || text.size() != 1 || names.size() != 2) { return false; }

        uint8_t* r = reg[0];
        uint8_t* calls[6] = { r + 0x2F, r + 0x3EC, r + 0x412, r + 0x425, r + 0x46D, r + 0x4A7 };
        for (auto c : calls)
        {
            if (!callTarget(c)) { say("no call at +%zx in the registration routine: another build of the game", static_cast<size_t>(c - r)); return false; }
        }
        game.malloc = reinterpret_cast<Malloc>(callTarget(calls[0]));
        game.registry = reinterpret_cast<Accessor>(callTarget(calls[1]));
        game.map1Add = reinterpret_cast<MapAdd>(callTarget(calls[2]));
        game.find = reinterpret_cast<MapFind>(callTarget(calls[3]));
        game.arrayGrow = reinterpret_cast<ArrayGrow>(callTarget(calls[4]));
        game.map2Add = reinterpret_cast<MapAdd>(callTarget(calls[5]));
        game.createText = reinterpret_cast<CreateText>(text[0]);
        // The narrow-string constructor comes first; the wide one follows it.
        game.fname = reinterpret_cast<FNameCtor>(names[0] < names[1] ? names[0] : names[1]);

        auto refresh = scan(parse("40 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 D9 48 81 EC 00 01 00 00 48 C7 45 B7 FE FF FF FF 48 89 9C 24 50 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 17 44 0F B6 FA 4C 8B E1"));
        auto module = scan(parse("40 53 48 83 EC 30 48 C7 44 24 20 FE FF FF FF 8B 0D ?? ?? ?? ?? 65 48 8B 04 25 58 00 00 00 BA 20 00 00 00 48 8B 0C C8 8B 04 0A 39 05 ?? ?? ?? ?? 7F 0D 48 8B 05 ?? ?? ?? ?? 48 83 C4 30 5B C3 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 83 3D ?? ?? ?? ?? FF 75 DE 41 B8 01 00 00 00 48 8D 15"));
        say("patterns: finder refresh %zu, module accessor %zu", refresh.size(), module.size());
        if (refresh.size() != 1 || module.size() != 1) { return false; }
        game.refresh = reinterpret_cast<Refresh>(refresh[0]);
        // The accessor's `mov rax, [rip+x]` names the module's global; read it, never call the
        // accessor - calling it early would make the engine load the module out of order.
        uint8_t* load = module[0] + 0x32;
        if (load[0] != 0x48 || load[1] != 0x8B || load[2] != 0x05) { say("the module accessor is not the expected shape"); return false; }
        int32_t rel; memcpy(&rel, load + 3, 4);
        game.module = reinterpret_cast<uint8_t**>(load + 7 + rel);

        // The finder constructor's loop over the item list: class getter, then the path insert,
        // for the instance path (+0x220) and the storable path (+0x218), then the texture class.
        auto paths = scan(parse("E8 ?? ?? ?? ?? 4C 8B CB 4C 8B 87 20 02 00 00 48 8B D0 48 8B CE E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B CB 4C 8B 87 18 02 00 00 48 8B D0 48 8B CE E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B CB 4C 8B 87 00 02 00 00"));
        say("patterns: finder path table %zu", paths.size());
        if (paths.size() != 1) { return false; }
        uint8_t* p = paths[0];
        if (callTarget(p + 0x15) != callTarget(p + 0x2F)) { say("the finder's path insert is not the same routine twice"); return false; }
        game.pathClass[0] = reinterpret_cast<StaticClass>(callTarget(p));
        game.pathClass[1] = reinterpret_cast<StaticClass>(callTarget(p + 0x1A));
        game.pathClass[2] = reinterpret_cast<StaticClass>(callTarget(p + 0x34));
        game.addPath = reinterpret_cast<AddPath>(callTarget(p + 0x15));
        return true;
    }

    // ---------------------------------------------------------------- the registry

    FName name(Game& game, const std::string& text)
    {
        FName made{};
        game.fname(&made, text.c_str(), 1 /* FNAME_Add */);
        return made;
    }

    uint8_t* recordOf(Game& game, uint8_t* registry, FName id)
    {
        int32_t index = -1;
        game.find(registry + MAP1_AT, &index, id);
        if (index < 0) { return nullptr; }
        auto elements = *reinterpret_cast<uint8_t**>(registry + MAP1_AT);
        return *reinterpret_cast<uint8_t**>(elements + static_cast<size_t>(index) * MAP1_ELEMENT + 8);
    }

    int32_t count(uint8_t* registry) { return *reinterpret_cast<int32_t*>(registry + ARRAY_AT + 8); }

    struct Item
    {
        std::string id, source, folder;
        std::wstring name, flavour;
        // Given: replace the source's. Not given ("-" or no field): keep the source's.
        bool hasSkills = false;
        std::vector<std::pair<int, int>> skills;                       // enchantment type id, level
        bool hasLines = false;
        std::vector<std::pair<std::wstring, std::wstring>> lines;      // text key, English text
    };

    std::wstring wide(const std::string& utf8)
    {
        if (utf8.empty()) { return std::wstring(); }
        int n = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), static_cast<int>(utf8.size()), nullptr, 0);
        std::wstring out(n, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), static_cast<int>(utf8.size()), &out[0], n);
        return out;
    }

    // id \t source id \t folder (MeleeWeapons/<id>) \t name \t description, one item a line, UTF-8.
    std::vector<Item> readItems()
    {
        std::vector<Item> items;
        FILE* file = _wfopen((g_folder + L"MCDRebornItems.txt").c_str(), L"rb");
        if (!file) { say("no MCDRebornItems.txt beside the plugin: nothing to register"); return items; }
        std::string all;
        char buffer[4096];
        size_t got;
        while ((got = fread(buffer, 1, sizeof(buffer), file)) > 0) { all.append(buffer, got); }
        fclose(file);
        if (all.size() >= 3 && static_cast<uint8_t>(all[0]) == 0xEF) { all.erase(0, 3); }

        size_t start = 0;
        while (start < all.size())
        {
            size_t end = all.find('\n', start);
            if (end == std::string::npos) { end = all.size(); }
            std::string line = all.substr(start, end - start);
            start = end + 1;
            if (!line.empty() && line.back() == '\r') { line.pop_back(); }
            if (line.empty() || line[0] == '#') { continue; }

            std::vector<std::string> parts;
            size_t from = 0;
            for (;;)
            {
                size_t tab = line.find('\t', from);
                parts.push_back(line.substr(from, tab == std::string::npos ? std::string::npos : tab - from));
                if (tab == std::string::npos) { break; }
                from = tab + 1;
            }
            if (parts.size() < 5) { say("skipped a line with %zu fields", parts.size()); continue; }
            Item item{ parts[0], parts[1], parts[2], wide(parts[3]), wide(parts[4]) };

            // Skills: "5:1;17:1" - enchantment type ids and levels. Empty means none.
            if (parts.size() > 5 && parts[5] != "-")
            {
                item.hasSkills = true;
                size_t at = 0;
                while (at < parts[5].size())
                {
                    size_t semi = parts[5].find(';', at);
                    std::string one = parts[5].substr(at, semi == std::string::npos ? std::string::npos : semi - at);
                    at = semi == std::string::npos ? parts[5].size() : semi + 1;
                    size_t colon = one.find(':');
                    if (colon == std::string::npos) { continue; }
                    int type = atoi(one.substr(0, colon).c_str());
                    int level = atoi(one.substr(colon + 1).c_str());
                    if (type > 0 && type < 256 && level > 0 && level < 100) { item.skills.push_back({ type, level }); }
                }
            }
            // Lines: "dual_wield=Dual Wield|spin_attack=Spin attack". Empty means none.
            if (parts.size() > 6 && parts[6] != "-")
            {
                item.hasLines = true;
                size_t at = 0;
                while (at < parts[6].size())
                {
                    size_t bar = parts[6].find('|', at);
                    std::string one = parts[6].substr(at, bar == std::string::npos ? std::string::npos : bar - at);
                    at = bar == std::string::npos ? parts[6].size() : bar + 1;
                    size_t equals = one.find('=');
                    if (equals == std::string::npos || equals == 0) { continue; }
                    item.lines.push_back({ wide(one.substr(0, equals)), wide(one.substr(equals + 1)) });
                }
            }
            items.push_back(item);
        }
        return items;
    }

    bool add(Game& game, uint8_t* registry, const Item& item)
    {
        FName id = name(game, item.id);
        if (recordOf(game, registry, id)) { say("%s is already registered", item.id.c_str()); return true; }

        uint8_t* source = recordOf(game, registry, name(game, item.source));
        if (!source) { say("%s: its source %s is not a game item", item.id.c_str(), item.source.c_str()); return false; }

        auto record = static_cast<uint8_t*>(game.malloc(RECORD_SIZE));
        if (!record) { say("%s: out of memory", item.id.c_str()); return false; }
        memcpy(record, source, RECORD_SIZE);

        memcpy(record + ID_AT, &id, sizeof(id));
        memcpy(record + ID_AGAIN_AT, &id, sizeof(id));

        // The folder, as the game's own string: its allocator, its layout.
        std::wstring folder = wide(item.folder);
        auto chars = static_cast<wchar_t*>(game.malloc((folder.size() + 1) * sizeof(wchar_t)));
        memcpy(chars, folder.c_str(), (folder.size() + 1) * sizeof(wchar_t));
        FString folderString{ chars, static_cast<int32_t>(folder.size() + 1), static_cast<int32_t>(folder.size() + 1) };
        memcpy(record + FOLDER_AT, &folderString, sizeof(folderString));

        // Name and description as the game makes its own: localised text in the ItemType
        // namespace, keyed by the id, with our words as the source a missing translation falls back to.
        std::wstring key = wide(item.id);
        std::wstring flavourKey = L"Flavour_" + key;
        FText text{};
        game.createText(&text, item.name.c_str(), L"ItemType", key.c_str());
        memcpy(record + NAME_AT, &text, sizeof(text));
        FText flavour{};
        game.createText(&flavour, item.flavour.c_str(), L"ItemType", flavourKey.c_str());
        memcpy(record + FLAVOUR_AT, &flavour, sizeof(flavour));

        // Skills and lines, when the item gives its own. The copied record still points at the
        // source's lists, which the source goes on using: new ones are made in the game's own
        // memory and pointed at instead, never written into the source's.
        if (item.hasSkills)
        {
            size_t n = item.skills.size();
            TArrayRaw list{ nullptr, 0, 0 };
            if (n > 0)
            {
                auto data = static_cast<uint8_t*>(game.malloc(n * BUILTIN_SIZE));
                memset(data, 0, n * BUILTIN_SIZE);
                for (size_t i = 0; i < n; i++)
                {
                    uint8_t* one = data + i * BUILTIN_SIZE;
                    one[0] = static_cast<uint8_t>(item.skills[i].first);
                    int32_t level = item.skills[i].second;
                    memcpy(one + 4, &level, 4);
                    one[0x10] = 2;
                }
                list = TArrayRaw{ data, static_cast<int32_t>(n), static_cast<int32_t>(n) };
            }
            memcpy(record + BUILTINS_AT, &list, sizeof(list));
            say("%s: %zu built-in skill(s)", item.id.c_str(), n);
        }
        if (item.hasLines)
        {
            size_t n = item.lines.size();
            TArrayRaw list{ nullptr, 0, 0 };
            if (n > 0)
            {
                auto data = static_cast<uint8_t*>(game.malloc(n * LINE_SIZE));
                memset(data, 0, n * LINE_SIZE);
                for (size_t i = 0; i < n; i++)
                {
                    // The game's own key and English: shown in the player's language like any line.
                    FText line{};
                    game.createText(&line, item.lines[i].second.c_str(), L"ItemType", item.lines[i].first.c_str());
                    memcpy(data + i * LINE_SIZE, &line, sizeof(line));
                }
                list = TArrayRaw{ data, static_cast<int32_t>(n), static_cast<int32_t>(n) };
            }
            memcpy(record + LINES_AT, &list, sizeof(list));
            say("%s: %zu property line(s)", item.id.c_str(), n);
        }

        const std::string fid = item.folder.substr(item.folder.find_last_of('/') + 1);
        const std::string paths[6] = {
            item.folder + "/T_" + fid + "_Icon",
            item.folder + "/T_" + fid + "_Icon_Inventory",
            item.folder + "/T_" + fid + "_GearIcon",
            item.folder + "/T_" + fid + "_AmmoIconSmall",
            item.folder + "/BP_" + fid + "Storable",
            item.folder + "/BP_" + fid + "Instance",
        };
        for (int i = 0; i < 6; i++)
        {
            FName path = name(game, paths[i]);
            memcpy(record + PATHS_AT + i * 8, &path, sizeof(path));
        }

        // The class caches start empty, or the copy would use the source's blueprint.
        const int32_t empty[2] = { -1, 0 };
        memcpy(record + CLASS_CACHE_A, empty, sizeof(empty));
        memcpy(record + CLASS_CACHE_B, empty, sizeof(empty));
        record[NO_LOOT_AT] = 1;

        // Map 1, then the array, then map 2 - the registration routine's own order.
        //
        // Map 1 holds its records as unique pointers: adding one MOVES it, and the variable handed
        // in comes back null. The first live test put that null into the array, where every loop
        // over all items would have dereferenced it. So the record is read back out of the map, as
        // the game's own routine does, and never used from the moved-from variable again.
        int32_t element = -1;
        uint8_t* moved = record;
        void* pair1[2] = { &id, &moved };
        game.map1Add(registry + MAP1_AT, &element, pair1, nullptr);
        record = recordOf(game, registry, id);
        if (!record) { say("%s: map 1 did not take it; stopped before the array", item.id.c_str()); return false; }

        auto array = reinterpret_cast<TArrayRaw*>(registry + ARRAY_AT);
        int32_t index = array->Num;
        array->Num = index + 1;
        if (array->Num > array->Max) { game.arrayGrow(array, index); }
        reinterpret_cast<uint8_t**>(array->Data)[index] = record;

        uint16_t shortIndex = static_cast<uint16_t>(index);
        void* pair2[2] = { &id, &shortIndex };
        game.map2Add(registry + MAP2_AT, &element, pair2, nullptr);

        say("%s registered at index %d, a copy of %s, in %s", item.id.c_str(), index, item.source.c_str(), item.folder.c_str());
        return reinterpret_cast<uint8_t**>(array->Data)[index] == record;
    }

    // Reads that cannot take the game down. Anything the plugin is not certain of - an object
    // found by following pointers rather than by a pattern it matched - is read through these, so
    // a wrong guess costs a line in the log instead of the game. (The second live test crashed on
    // exactly such a guess: a vtable read from where the finder was assumed to be.)
    bool safeRead(const void* at, void* out, size_t size)
    {
        __try { memcpy(out, at, size); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    template <typename T> bool safeGet(const void* at, T& out) { return safeRead(at, &out, sizeof(T)); }

    bool inFinder(uint8_t* finder, FName id, bool& readable)
    {
        TArrayRaw map{};
        readable = safeGet(finder + FINDER_MAP_AT, map) && map.Num >= 0 && map.Num <= map.Max && map.Max < 100000;
        if (!readable) { return false; }
        for (int32_t i = 0; i < map.Num; i++)
        {
            FName key{};
            if (!safeGet(map.Data + static_cast<size_t>(i) * FINDER_ELEMENT, key)) { readable = false; return false; }
            if (key.Index == id.Index && key.Number == id.Number) { return true; }
        }
        return false;
    }

    // Whether the finder's path table files anything under this id.
    bool inPathTable(uint8_t* finder, FName id, bool& readable)
    {
        TArrayRaw table{};
        readable = safeGet(finder + FINDER_PATHS_AT, table) && table.Num >= 0 && table.Num <= table.Max && table.Max < 1000000;
        if (!readable) { return false; }
        for (int32_t i = 0; i < table.Num; i++)
        {
            FName owner{};
            if (!safeGet(table.Data + static_cast<size_t>(i) * FINDER_PATH_ELEMENT + 8, owner)) { readable = false; return false; }
            if (owner.Index == id.Index && owner.Number == id.Number) { return true; }
        }
        return false;
    }

    // The finder, as the game itself reaches it: the Dungeons module's virtual at +0x48 returns it.
    // Its first instruction says where it is - `lea rax,[rcx+X]` for a member, `mov rax,[rcx+X]`
    // for a pointer - so it is decoded rather than assumed. Anything else: null, and the bytes logged.
    uint8_t* finderOf(Game& game, uint8_t* module)
    {
        uint8_t* vtable = nullptr;
        uint8_t* getter = nullptr;
        if (!safeGet(module, vtable) || !safeGet(vtable + 0x48, getter)) { say("the module's vtable is not readable"); return nullptr; }
        uint8_t code[16]{};
        for (int hop = 0; hop < 4; hop++)
        {
            if (!safeRead(getter, code, sizeof(code))) { say("the finder getter is not readable"); return nullptr; }
            if (code[0] != 0xE9) { break; }
            int32_t rel; memcpy(&rel, code + 1, 4);
            getter = getter + 5 + rel;
        }
        int32_t offset; memcpy(&offset, code + 3, 4);
        if (code[0] == 0x48 && code[1] == 0x8D && code[2] == 0x81 && code[7] == 0xC3) { return module + offset; }
        if (code[0] == 0x48 && code[1] == 0x8B && code[2] == 0x81 && code[7] == 0xC3)
        {
            uint8_t* finder = nullptr;
            return safeGet(module + offset, finder) ? finder : nullptr;
        }
        // The shipped game: sub rsp,28 / call holder / mov rax,[rax+x] / add rsp,28 / ret. The holder
        // returns a cached object at [module+y] and makes it when that is empty; making it is the
        // game's business, so an empty one means the finder is not built yet.
        if (code[0] == 0x48 && code[1] == 0x83 && code[2] == 0xEC && code[3] == 0x28 && code[4] == 0xE8
            && code[9] == 0x48 && code[10] == 0x8B && code[11] == 0x40
            && code[13] == 0x48 && code[14] == 0x83 && code[15] == 0xC4)
        {
            int32_t rel; memcpy(&rel, code + 5, 4);
            uint8_t* holderFn = getter + 9 + rel;
            uint8_t head[10]{};
            // push rsi / sub rsp,50 / mov rax,[rcx+y]
            if (!safeRead(holderFn, head, sizeof(head)) || head[0] != 0x40 || head[1] != 0x56
                || head[6] != 0x48 || head[7] != 0x8B || head[8] != 0x41)
            {
                say("the finder holder is not a shape this knows: %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X",
                    head[0], head[1], head[2], head[3], head[4], head[5], head[6], head[7], head[8], head[9]);
                return nullptr;
            }
            uint8_t* holder = nullptr;
            uint8_t* finder = nullptr;
            if (!safeGet(module + head[9], holder)) { say("the finder holder is not readable"); return nullptr; }
            if (!holder) { say("the finder holder is not made yet"); return nullptr; }
            if (!safeGet(holder + code[12], finder)) { say("the finder is not readable"); return nullptr; }
            say("finder: module %p, holder %p (+0x%X), finder %p (+0x%X)", module, holder, head[9], finder, code[12]);
            return finder;
        }
        say("the finder getter is not a shape this knows: %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X",
            code[0], code[1], code[2], code[3], code[4], code[5], code[6], code[7],
            code[8], code[9], code[10], code[11], code[12], code[13], code[14], code[15]);
        (void)game;
        return nullptr;
    }

    // After registering: the finder, if the engine has already built it, is rebuilt so that it
    // knows the new ids. Before the engine has made the module, there is no finder yet, and the
    // one it builds later will include them by itself.
    void refreshFinder(Game& game, uint8_t* registry, const std::vector<FName>& ids)
    {
        uint8_t* module = nullptr;
        if (!safeGet(game.module, module)) { say("the module global is not readable"); return; }
        if (!module) { say("the finder is not built yet; it will include the new items when it is"); return; }
        uint8_t* finder = finderOf(game, module);
        if (!finder) { say("the finder was not found; NOT refreshed - do not open an item this added"); return; }

        // Only call it on the object it belongs to: the refresh must be in the finder's vtable.
        uint8_t* vtable = nullptr;
        bool ours = false;
        if (safeGet(finder, vtable))
        {
            for (int slot = 0; slot < 160 && !ours; slot++)
            {
                void* entry = nullptr;
                if (!safeGet(vtable + slot * 8, entry)) { break; }
                ours = entry == reinterpret_cast<void*>(game.refresh);
            }
        }
        if (!ours) { say("the object at %p is not the item finder; NOT refreshed", finder); return; }

        int missing = 0;
        bool readable = true;
        for (auto& id : ids) { if (!inFinder(finder, id, readable)) { missing++; } }
        if (!readable) { say("the finder's table does not read as a table; NOT refreshed"); return; }
        say("finder at %p; %d of %zu new item(s) missing from it", finder, missing, ids.size());
        if (missing == 0) { return; }

        // Into the path table first, as the constructor would have put them.
        for (auto& id : ids)
        {
            if (inPathTable(finder, id, readable) || !readable) { continue; }
            uint8_t* record = recordOf(game, registry, id);
            if (!record) { continue; }
            for (const RecordPath& path : RECORD_PATHS)
            {
                FName name = *reinterpret_cast<FName*>(record + path.at);
                game.addPath(finder, game.pathClass[path.cls](), name, id);
            }
        }
        if (!readable) { say("the finder's path table does not read as a table; NOT refreshed"); return; }
        int filed = 0;
        for (auto& id : ids) { if (inPathTable(finder, id, readable)) { filed++; } }
        say("path table: %d of %zu new item(s) filed", filed, ids.size());

        game.refresh(finder, true);
        missing = 0;
        for (auto& id : ids) { if (!inFinder(finder, id, readable)) { missing++; } }
        say("finder refreshed: %d new item(s) still missing%s", missing, missing ? " - do not open an item this added" : "");
    }

    DWORD WINAPI run(LPVOID)
    {
        wchar_t path[MAX_PATH];
        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&run), &self);
        GetModuleFileNameW(self, path, MAX_PATH);
        g_folder = path;
        g_folder = g_folder.substr(0, g_folder.find_last_of(L"\\/") + 1);
        g_log = _wfopen((g_folder + L"MCDRebornItems.log").c_str(), L"w");
        say("MCD Reborn item plugin loaded");

        // The game decrypts itself after it starts, so the patterns can be missing for a while.
        Game game;
        bool ready = false;
        for (int attempt = 0; attempt < 120 && !ready; attempt++)
        {
            ready = find(game);
            if (!ready) { Sleep(500); }
        }
        if (!ready) { say("the game's code was never found; nothing was changed"); return 0; }

        // The registry is filled by the game's static blocks as it starts. Wait for all of them:
        // loaded with the game, this runs before they have, and a half-filled list is not one to
        // add to. Two quiet polls in a row, or nothing is changed.
        uint8_t* registry = game.registry();
        int32_t last = -1;
        int quiet = 0;
        for (int attempt = 0; attempt < 480 && quiet < 2; attempt++)
        {
            int32_t now = count(registry);
            quiet = (now > 300 && now == last) ? quiet + 1 : 0;
            last = now;
            if (quiet < 2) { Sleep(250); }
        }
        say("registry at %p holds %d items", registry, count(registry));
        if (quiet < 2) { say("the item list never settled; nothing was changed"); return 0; }

        // Cross-check before writing: a name made here must find the game's own record for it.
        uint8_t* katana = recordOf(game, registry, name(game, "Katana"));
        if (!katana || memcmp(katana + ID_AT, &(*reinterpret_cast<FName*>(katana)), 8) != 0)
        {
            say("the cross-check failed: names made here do not find the game's items; nothing was changed");
            return 0;
        }
        say("cross-check passed");

        int added = 0;
        std::vector<FName> ids;
        for (const Item& item : readItems())
        {
            if (add(game, registry, item)) { added++; ids.push_back(name(game, item.id)); }
        }
        if (!ids.empty()) { refreshFinder(game, registry, ids); }
        say("done: %d item(s), registry now holds %d", added, count(registry));
        return 0;
    }
}

// ---------------------------------------------------------------- standing in for xinput1_3
//
// Installed as xinput1_3.dll beside the game, the plugin is loaded by Windows with the game, before
// any of the game's own code runs - no loader, no injection. The game still needs the real
// XInput, so every export is passed on to the one in the system folder. Loaded that way rather
// than by name: by name, Windows would hand back this DLL again.

namespace
{
    using Any = uintptr_t (WINAPI*)(uintptr_t, uintptr_t, uintptr_t, uintptr_t);

    HMODULE realXInput()
    {
        static HMODULE real = nullptr;
        if (!real)
        {
            wchar_t path[MAX_PATH];
            UINT length = GetSystemDirectoryW(path, MAX_PATH);
            if (length == 0 || length > MAX_PATH - 20) { return nullptr; }
            wcscat_s(path, L"\\xinput1_3.dll");
            real = LoadLibraryW(path);
        }
        return real;
    }

    // Every XInput export takes at most three arguments, none of them floating point, so one shape
    // passes any of them on untouched. ERROR_DEVICE_NOT_CONNECTED when there is no real XInput.
    uintptr_t pass(const char* name, uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d)
    {
        HMODULE real = realXInput();
        Any target = real ? reinterpret_cast<Any>(GetProcAddress(real, name)) : nullptr;
        return target ? target(a, b, c, d) : 1167;
    }
}

#define PASS(export, name) extern "C" uintptr_t WINAPI export(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d) { return pass(name, a, b, c, d); }
PASS(proxyGetState, "XInputGetState")
PASS(proxySetState, "XInputSetState")
PASS(proxyGetCapabilities, "XInputGetCapabilities")
PASS(proxyEnable, "XInputEnable")
PASS(proxyGetDSoundAudioDeviceGuids, "XInputGetDSoundAudioDeviceGuids")
PASS(proxyGetBatteryInformation, "XInputGetBatteryInformation")
PASS(proxyGetKeystroke, "XInputGetKeystroke")
PASS(proxyGetStateEx, MAKEINTRESOURCEA(100))
PASS(proxyWaitForGuideButton, MAKEINTRESOURCEA(101))
PASS(proxyCancelGuideButtonWait, MAKEINTRESOURCEA(102))
PASS(proxyPowerOffController, MAKEINTRESOURCEA(103))
#undef PASS

// How MCD Reborn tells its own xinput1_3.dll from somebody else's: the version of the plugin.
extern "C" int WINAPI MCDRebornPlugin() { return 1; }

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(nullptr, 0, run, nullptr, 0, nullptr);
        if (thread) { CloseHandle(thread); }
    }
    return TRUE;
}
