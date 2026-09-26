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
    //  - an armor's default armor properties: EArmorPropertyID and EItemRarity, two bytes each, a
    //    unique's own first. What it drops with; an owned armor's are the ones in the save.
    constexpr size_t ARMOR_PROPERTIES_AT = 0x1A8;
    //  - an artifact's numbers, plain floats: soul cost, cooldown and duration in seconds. The only
    //    offsets the numbers field may write; anything else in it is refused.
    constexpr size_t ARTIFACT_NUMBERS[] = { 0x94, 0x98, 0x9C };

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
    using LoadObject = void* (__fastcall*)(void* cls, void* outer, const wchar_t* name, const wchar_t* filename, uint32_t flags, void* sandbox,
        bool allowReconciliation, void* serializeContext);
    using Exec = void (__fastcall*)(void* context, void* stack, void** result);
    using MobDefine = uint8_t* (__fastcall*)(int32_t type);
    using MobName = uint8_t* (__fastcall*)(uint8_t* definition, const FText* name);
    using MobParent = uint8_t* (__fastcall*)(uint8_t* definition, int32_t parent);
    using MobRegistry = uint8_t* (__fastcall*)();
    struct FStringRaw { const wchar_t* Data; int32_t Num; int32_t Max; };
    using MobRegister = void (__fastcall*)(uint8_t* registry, int32_t type, const FStringRaw* path);

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
        StaticClass enchantmentClass = nullptr;  // the class the enchantment finder files a blueprint under
        StaticClass textureClass = nullptr;      // ... an icon under
        StaticClass materialClass = nullptr;     // ... an icon material under
        LoadObject load = nullptr;               // StaticLoadObject (124d170)
        int32_t* objectCount = nullptr;          // GUObjectArray: how many
        uint8_t*** objectChunks = nullptr;       // GUObjectArray: its chunks, 0x10000 items of 0x18
        uint8_t* textureExec = nullptr;          // execGetIconTextureForEnchantmentType (ef6a00)
        uint8_t* materialExec = nullptr;         // its material twin (ef6940)
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

        // The enchantment finder's constructor (707f10) files each definition's three paths the
        // same way: blueprint (+0x120) under one class, icon and material under two more. Only
        // the first is needed, for an enchantment with a blueprint of its own. Optional: without
        // it those are not filed, and the rest works as before.
        auto enchantmentPaths = scan(parse("E8 ?? ?? ?? ?? 4C 8B 4C 24 ?? 4C 8B 46 F8 48 8B D0 48 8B CB E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B 4C 24 ?? 4C 8B 06 48 8B D0 48 8B CB E8"));
        say("patterns: enchantment finder paths %zu", enchantmentPaths.size());
        if (enchantmentPaths.size() == 1 && callTarget(enchantmentPaths[0] + 0x14) == reinterpret_cast<uint8_t*>(game.addPath))
        {
            game.enchantmentClass = reinterpret_cast<StaticClass>(callTarget(enchantmentPaths[0]));
            game.textureClass = reinterpret_cast<StaticClass>(callTarget(enchantmentPaths[0] + 0x19));
            game.materialClass = reinterpret_cast<StaticClass>(callTarget(enchantmentPaths[0] + 0x31));
        }

        // For an enchantment with an icon of its own, all optional. The class getter's
        // fallback load (a627e0): mov rbx,[rip+classes] ... call StaticLoadObject. Its root-set
        // step, which says where GUObjectArray is. And the two exec thunks the UI's icons come
        // through: ..., mov [rbx+20],rdi / call getter / mov rbx,[rsp+30] / mov [rsi],rax.
        auto loads = scan(parse("48 8B 1D ?? ?? ?? ?? 48 89 7C 24 28 89 7C 24 20 45 33 C9 4C 8B C5 33 D2 48 8B C8 E8"));
        auto roots = scan(parse("8B 41 0C 3B 05 ?? ?? ?? ?? 7D ?? 99 0F B7 D2 03 C2 8B C8 0F B7 C0 2B C2 C1 F9 10 48 63 D1 48 8B 0D"));
        say("patterns: object load %zu, object array %zu", loads.size(), roots.size());
        // What the class getter calls is StaticLoadClass (124ccc0): it asks StaticLoadObject for
        // a Class and checks it derives from the one given, so a texture never comes back from
        // it. StaticLoadObject itself is its first call, at +0x6D.
        if (loads.size() == 1)
        {
            uint8_t* loadClass = callTarget(loads[0] + 0x1B);
            if (loadClass[0x6D] == 0xE8) { game.load = reinterpret_cast<LoadObject>(callTarget(loadClass + 0x6D)); }
            else { say("patterns: StaticLoadClass is not the expected shape"); }
        }
        // The root-set step is inlined in many places (11 in the shipped game); every one must
        // name the same two globals.
        for (uint8_t* at : roots)
        {
            int32_t offset; memcpy(&offset, at + 5, 4);
            auto count = reinterpret_cast<int32_t*>(at + 9 + offset);
            memcpy(&offset, at + 33, 4);
            auto chunks = reinterpret_cast<uint8_t***>(at + 37 + offset);
            if (at == roots.front()) { game.objectCount = count; game.objectChunks = chunks; }
            else if (count != game.objectCount || chunks != game.objectChunks) { say("patterns: the object array is named two ways"); game.objectCount = nullptr; game.objectChunks = nullptr; break; }
        }
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
        bool hasArmorProperties = false;
        std::vector<std::pair<int, int>> armorProperties;              // EArmorPropertyID, EItemRarity
        std::vector<std::pair<size_t, float>> numbers;                 // record offset, value
    };

    std::wstring wide(const std::string& utf8)
    {
        if (utf8.empty()) { return std::wstring(); }
        int n = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), static_cast<int>(utf8.size()), nullptr, 0);
        std::wstring out(n, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), static_cast<int>(utf8.size()), &out[0], n);
        return out;
    }

    // ---------------------------------------------------------------- enchantments
    //
    // An enchantment is a definition in a table the game indexes by EEnchantmentTypeID (a pointer
    // array at a global a79ee0 fills), 0x140 bytes each: +0x03 its id; +0x18 name, +0x30
    // description, +0x48 the line it shows when built in, +0x60 its effect label (FText); +0x120,
    // +0x128, +0x130 FNames of its blueprint, icon and icon material, relative to
    // Components/Enchantments - and the blueprint's C++ parent is what it does. Every lookup is
    // table[id], no bounds check, so an id past the enum's Last (162) is safe once it is filled,
    // and no loop walks past Last.
    //
    // Saves spell an enchantment by name through the reflected enum EEnchantmentTypeID, so a new
    // one is also appended to that enum's names (UEnum +0x40, pairs of FName and int64) once the
    // engine has made it.
    constexpr size_t ENCHANTMENT_SIZE = 0x140;

    // Enchantments with a blueprint of their own, by id: filed in the enchantment finder once
    // everything else is done (fileEnchantmentBlueprints).
    struct OwnBlueprint { FName id; FName blueprint; };
    std::vector<OwnBlueprint> g_blueprints;
    constexpr int FIRST_NEW_ENCHANTMENT = 164;      // past Last (162) and _MAX (163)

    struct Enchantment
    {
        std::string id;                // MCDR_Ench01: its name in saves
        int source = 0;                // EEnchantmentTypeID of the one it copies
        std::wstring name, description, builtIn, effect;
        std::string folder;            // its own blueprint, "MCDR_Ench02/BP_MCDR_Ench02", or "-" for the source's
        std::string icon;              // its own icon: "texture object path|material object path", or "-"
    };

    // "@enchantment \t id \t source type id \t name \t description \t built-in line \t effect \t folder",
    // any text "-" for the source's own
    std::vector<Enchantment> readEnchantments()
    {
        std::vector<Enchantment> found;
        FILE* file = _wfopen((g_folder + L"MCDRebornItems.txt").c_str(), L"rb");
        if (!file) { return found; }
        std::string all;
        char buffer[4096];
        size_t got;
        while ((got = fread(buffer, 1, sizeof(buffer), file)) > 0) { all.append(buffer, got); }
        fclose(file);
        size_t start = 0;
        while (start < all.size())
        {
            size_t end = all.find('\n', start);
            if (end == std::string::npos) { end = all.size(); }
            std::string line = all.substr(start, end - start);
            start = end + 1;
            if (!line.empty() && line.back() == '\r') { line.pop_back(); }
            if (line.rfind("@enchantment\t", 0) != 0) { continue; }
            std::vector<std::string> parts;
            size_t from = 0;
            for (;;)
            {
                size_t tab = line.find('\t', from);
                parts.push_back(line.substr(from, tab == std::string::npos ? std::string::npos : tab - from));
                if (tab == std::string::npos) { break; }
                from = tab + 1;
            }
            if (parts.size() < 8) { say("skipped an enchantment line with %zu fields", parts.size()); continue; }
            Enchantment e;
            e.id = parts[1];
            e.source = atoi(parts[2].c_str());
            e.name = wide(parts[3]); e.description = wide(parts[4]); e.builtIn = wide(parts[5]); e.effect = wide(parts[6]);
            e.folder = parts[7];
            e.icon = parts.size() > 8 ? parts[8] : "-";
            found.push_back(e);
        }
        return found;
    }

    // The game's routine that makes a definition: its `mov rcx, [rip+x]` names the table.
    uint8_t** enchantmentTable(TArrayRaw*& header)
    {
        auto hits = scan(parse("40 57 48 83 EC 30 48 C7 44 24 20 FE FF FF FF 48 89 5C 24 40 48 89 74 24 50 0F B6 F9 8B F7 B9 40 01 00 00 E8"));
        if (hits.size() != 1) { say("enchantments: the definition routine was found %zu times", hits.size()); return nullptr; }
        uint8_t* load = hits[0] + 0x4D;
        if (load[0] != 0x48 || load[1] != 0x8B || load[2] != 0x0D) { say("enchantments: the table load is not where expected"); return nullptr; }
        int32_t rel; memcpy(&rel, load + 3, 4);
        header = reinterpret_cast<TArrayRaw*>(load + 7 + rel);
        return reinterpret_cast<uint8_t**>(header->Data);
    }

    // The enum's generated constructor keeps it in a static: found from its parameter block, whose
    // name pointer (+0x10) points at "EEnchantmentTypeID", and the code that loads that block.
    uint8_t** enumStatic(const char* enumName)
    {
        HMODULE exe = GetModuleHandleW(nullptr);
        auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(exe);
        auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(reinterpret_cast<uint8_t*>(exe) + dos->e_lfanew);
        uint8_t* base = reinterpret_cast<uint8_t*>(exe);
        size_t size = nt->OptionalHeader.SizeOfImage;
        size_t length = strlen(enumName) + 1;

        uint8_t* text = nullptr;
        for (size_t i = 1; i + length < size && !text; i++)
        {
            if (base[i - 1] == 0 && memcmp(base + i, enumName, length) == 0) { text = base + i; }
        }
        if (!text) { say("enum %s: its name is not in the image", enumName); return nullptr; }

        uint8_t* block = nullptr;
        for (size_t i = 0; i + 8 <= size && !block; i += 8)
        {
            uint8_t* value; memcpy(&value, base + i, 8);
            if (value == text) { block = base + i - 0x10; }
        }
        if (!block) { say("enum %s: no parameter block names it", enumName); return nullptr; }

        // mov rax,[rip+static] (7) / test rax,rax (3) / jne (2) / lea rdx,[rip+block] (7)
        for (size_t i = 12; i + 7 < size; i++)
        {
            if (base[i] != 0x48 || base[i + 1] != 0x8D || base[i + 2] != 0x15) { continue; }
            int32_t rel; memcpy(&rel, base + i + 3, 4);
            if (base + i + 7 + rel != block) { continue; }
            uint8_t* mov = base + i - 12;
            if (mov[0] != 0x48 || mov[1] != 0x8B || mov[2] != 0x05) { continue; }
            int32_t at; memcpy(&at, mov + 3, 4);
            return reinterpret_cast<uint8_t**>(mov + 7 + at);
        }
        say("enum %s: its constructor was not found", enumName);
        return nullptr;
    }

    // An array header read that survives a bad address.
    bool readArray(TArrayRaw* at, TArrayRaw& out)
    {
        __try { out = *at; return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    // A pointer read that survives a bad address: the enum's static is read while the engine starts.
    bool readPointer(uint8_t** at, uint8_t*& out)
    {
        __try { out = *at; return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    // ---------------------------------------------------------------- mobs
    //
    // A mob is an EntityType: a Bedrock-style number, an id in the low byte and category flags
    // above it (Zombie 0x30a20: id 0x20, monster and undead). Three things know about one:
    //
    //   its definition - a 0x78-byte record in a map keyed by the whole number, made by be5bb0
    //       and named by a3e3d0, which every mob's own static function calls with its MobType
    //       text ("mob_Zombie");
    //   the mob registry - a function-local singleton (ad2c20 returns it, a88a10 builds it)
    //       holding each type's blueprint and class path, filled by ad0a40(registry, type,
    //       "Actors/.../BP_...Character") for 257 mobs. Built the first time anything asks, which
    //       at the main menu nothing has yet;
    //   the reflected enum EntityType, which cooked data names mobs through - a summoning
    //       artifact's list says "EntityType::Skeleton".
    //
    // A new mob takes its source's flags with an id no type uses, so the game treats it as the
    // same kind of creature and nothing keyed by the whole number collides.
    struct Mob { std::string id; int32_t source = 0; std::wstring name; std::wstring blueprint; };

    // "@mob \t id \t source EntityType (decimal) \t name \t blueprint relative to /Game/"
    std::vector<Mob> readMobs()
    {
        std::vector<Mob> found;
        FILE* file = _wfopen((g_folder + L"MCDRebornItems.txt").c_str(), L"rb");
        if (!file) { return found; }
        std::string all;
        char buffer[4096];
        size_t got;
        while ((got = fread(buffer, 1, sizeof(buffer), file)) > 0) { all.append(buffer, got); }
        fclose(file);
        size_t start = 0;
        while (start < all.size())
        {
            size_t end = all.find('\n', start);
            if (end == std::string::npos) { end = all.size(); }
            std::string line = all.substr(start, end - start);
            start = end + 1;
            if (!line.empty() && line.back() == '\r') { line.pop_back(); }
            if (line.rfind("@mob\t", 0) != 0) { continue; }
            std::vector<std::string> parts;
            size_t from = 0;
            for (;;)
            {
                size_t tab = line.find('\t', from);
                parts.push_back(line.substr(from, tab == std::string::npos ? std::string::npos : tab - from));
                if (tab == std::string::npos) { break; }
                from = tab + 1;
            }
            if (parts.size() < 5) { say("skipped a mob line with %zu fields", parts.size()); continue; }
            Mob m;
            m.id = parts[1];
            m.source = static_cast<int32_t>(strtol(parts[2].c_str(), nullptr, 0));
            m.name = wide(parts[3]);
            m.blueprint = wide(parts[4]);
            found.push_back(m);
        }
        return found;
    }

    // Paths handed to the registry: it copies them, but they are kept for the life of the game.
    std::vector<std::wstring> g_mobPaths;

    // Each mob and the type it was given, named in the enum early, registered later.
    struct NamedMob { Mob mob; int32_t type; };
    std::vector<NamedMob> g_mobs;

    // The early step: each mob's type chosen and named in EntityType the moment the engine makes
    // the enum. Cooked data names mobs through it, and the game preloads items as it starts - the
    // first test's summoning artifact was loaded before the name was there, and its entries fell
    // back to its C++ class's sheep. The engine makes its enums before it loads any content.
    void nameMobs(Game& game)
    {
        auto mobs = readMobs();
        if (mobs.empty()) { return; }
        uint8_t** holder = enumStatic("EntityType");
        if (!holder) { say("mobs: the EntityType enum was not found; NOT added"); return; }
        uint8_t* uenum = nullptr;
        for (int wait = 0; wait < 60000 && !uenum; wait++)
        {
            if (!readPointer(holder, uenum) || !uenum) { uenum = nullptr; Sleep(5); }
        }
        if (!uenum) { say("mobs: the EntityType enum was never made; NOT added"); return; }
        auto names = reinterpret_cast<TArrayRaw*>(uenum + 0x40);
        // The enum's names go in as it is made: wait until its count stops moving.
        for (int32_t last = -1, quiet = 0; quiet < 3; Sleep(5))
        {
            quiet = names->Num > 0 && names->Num == last ? quiet + 1 : 0;
            last = names->Num;
        }
        bool used[256]{};
        for (int32_t i = 0; i < names->Num; i++)
        {
            int64_t value; memcpy(&value, names->Data + static_cast<size_t>(i) * 16 + 8, 8);
            used[value & 0xFF] = true;
        }
        int free = 0xFF;
        for (const auto& m : mobs)
        {
            while (free > 0 && used[free]) { free--; }
            if (free <= 0) { say("mobs: no EntityType id left for %s", m.id.c_str()); break; }
            used[free] = true;
            int32_t type = (m.source & ~0xFF) | free;
            if (names->Num >= names->Max)
            {
                int32_t more = names->Max + 16;
                auto bigger = static_cast<uint8_t*>(game.malloc(static_cast<size_t>(more) * 16));
                memcpy(bigger, names->Data, static_cast<size_t>(names->Num) * 16);
                names->Data = bigger;           // the old block is left as it is
                names->Max = more;
            }
            FName full = name(game, "EntityType::" + m.id);
            int64_t value = type;
            memcpy(names->Data + static_cast<size_t>(names->Num) * 16, &full, 8);
            memcpy(names->Data + static_cast<size_t>(names->Num) * 16 + 8, &value, 8);
            names->Num++;
            g_mobs.push_back({ m, type });
            say("%s named in EntityType as 0x%X, a copy of 0x%X (enum of %d names)", m.id.c_str(), type, m.source, names->Num);
        }
    }

    void nameMobsForLevels(Game& game);
    void copyMobInfo();

    // The later step: each named mob's definition and blueprint, in the registry the plugin
    // builds itself - nothing needs them until something spawns.
    void addMobs(Game& game)
    {
        if (g_mobs.empty()) { return; }

        // The functions, from the mobs' own static functions (227 of them, all calling the same
        // three) and the registry getter (the one place that builds it).
        auto named = scan(parse("48 8D 4C 24 28 E8 ?? ?? ?? ?? 48 8B D8 B9 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B C8 48 8B D3 E8 ?? ?? ?? ?? 48 89 05"));
        if (named.empty()) { say("mobs: the mob definitions were not found; NOT added"); return; }
        auto define = reinterpret_cast<MobDefine>(callTarget(named[0] + 18));
        auto setName = reinterpret_cast<MobName>(callTarget(named[0] + 29));
        for (uint8_t* at : named)
        {
            if (callTarget(at + 18) != reinterpret_cast<uint8_t*>(define) || callTarget(at + 29) != reinterpret_cast<uint8_t*>(setName))
            {
                say("mobs: the mob definitions are made two ways; NOT added"); return;
            }
        }
        MobRegistry registryOf = nullptr;
        uint8_t* builder = nullptr;
        for (uint8_t* at : scan(parse("48 8D 1D ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8")))
        {
            uint8_t* start = at - 0x54;
            if (start[0] != 0x40 || start[1] != 0x53) { continue; }
            // The builder registers its first mob within its first few hundred bytes.
            uint8_t* candidate = callTarget(at + 10);
            for (size_t i = 0; i < 0x200 && !registryOf; i++)
            {
                uint8_t* c = candidate + i;
                // mov edx, <type> / mov rcx, rbx / call register
                if (c[0] == 0xBA && c[5] == 0x48 && c[6] == 0x8B && c[7] == 0xCB && c[8] == 0xE8)
                {
                    registryOf = reinterpret_cast<MobRegistry>(start);
                    builder = c + 8;
                }
            }
        }
        if (!registryOf) { say("mobs: the mob registry was not found; NOT added"); return; }
        auto registerMob = reinterpret_cast<MobRegister>(callTarget(builder));
        // The definition lookup (be7ce0): the register function's first call.
        uint8_t* registerCode = reinterpret_cast<uint8_t*>(registerMob);
        MobDefine lookup = registerCode[0x3B] == 0xE8 ? reinterpret_cast<MobDefine>(callTarget(registerCode + 0x3B)) : nullptr;

        // A variant's definition points to its base mob (be9dc0: ZombieVariant1 -> Zombie,
        // SpiderAncient -> Spider; 59 of them), as a small function object at +0x38 whose storage
        // pointer is +0x70 and whose captured type is +0x40. The level spawner asks for it where
        // summoning does not: a copy of ZombieVariant1 without it crashed arno on its first
        // roam. So a copy gets its source's.
        MobParent setParent = nullptr;
        for (uint8_t* at : scan(parse("B9 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B C8 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B C8 48 8B D3 E8")))
        {
            auto found = reinterpret_cast<MobParent>(callTarget(at + 18));
            if (!setParent) { setParent = found; }
            else if (found != setParent) { setParent = nullptr; say("mobs: base mobs are set two ways; NOT copied"); break; }
        }

        uint8_t* registry = registryOf();
        say("mobs: registry at %p", registry);
        for (const auto& [m, type] : g_mobs)
        {
            FText text{};
            std::wstring key = L"mob_" + wide(m.id);
            game.createText(&text, m.name.c_str(), L"MobType", key.c_str());
            uint8_t* definition = define(type);
            setName(definition, &text);
            if (lookup && setParent)
            {
                uint8_t* source = lookup(m.source);
                uint8_t* storage = nullptr;
                if (source && readPointer(reinterpret_cast<uint8_t**>(source + 0x70), storage) && storage == source + 0x38)
                {
                    int32_t parent = 0;
                    memcpy(&parent, source + 0x40, 4);
                    setParent(definition, parent);
                    say("%s: a variant of 0x%X, as its source is", m.id.c_str(), parent);
                }
            }

            g_mobPaths.push_back(m.blueprint);
            const std::wstring& path = g_mobPaths.back();
            FStringRaw fpath{ path.c_str(), static_cast<int32_t>(path.size() + 1), static_cast<int32_t>(path.size() + 1) };
            registerMob(registry, type, &fpath);
            say("%s registered as EntityType 0x%X, blueprint %ls", m.id.c_str(), type, m.blueprint.c_str());
        }
        nameMobsForLevels(game);
        copyMobInfo();
    }

    // Every mob type's info: its tags ("weak", "ranged", "caster", "illager"...) and four
    // numbers, in a table the game builds at start-up (e056b0: e4d250(table, type, numbers,
    // tags) for each) and hands out from a function-local singleton (e4bf10). A level's setup
    // reads it for every mob type its groups name: with a new type missing from it, arno's setup
    // failed quietly, the player was never logged in ("Couldn't spawn player"), and the music
    // manager crashed in the transition map with no player controller. So a copy gets its
    // source's info: an entry made by the game's own get-or-create (e49710), the numbers at +0x48
    // copied, and each tag added by the game's own (cdcd10). The source's entry is found by
    // walking the table, so a source without one is never given an empty one.
    using MobInfoTable = uint8_t* (__fastcall*)();
    using MobInfoEntry = uint8_t* (__fastcall*)(uint8_t* table, int32_t type);
    using MobInfoTag = void (__fastcall*)(uint8_t* entry, const void* tag);

    void copyMobInfo()
    {
        if (g_mobs.empty()) { return; }
        MobInfoTable tableOf = nullptr;
        MobInfoEntry entryOf = nullptr;
        MobInfoTag addTag = nullptr;
        for (uint8_t* at : scan(parse("48 8D 1D ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8")))
        {
            uint8_t* start = at - 0x54;
            if (start[0] != 0x40 || start[1] != 0x53) { continue; }
            uint8_t* builder = callTarget(at + 10);
            for (size_t i = 0; i < 0x400 && !tableOf; i++)
            {
                uint8_t* c = builder + i;
                // mov edx, <type> / mov rcx, r15 / call insert
                if (c[0] != 0xBA || c[5] != 0x49 || c[6] != 0x8B || c[7] != 0xCF || c[8] != 0xE8) { continue; }
                uint8_t* insert = callTarget(c + 8);
                if (insert[0x23] != 0xE8 || insert[0x46] != 0xE8 || insert[0x4B] != 0x48 || insert[0x4C] != 0x83 || insert[0x4D] != 0xC3 || insert[0x4E] != 0x20) { continue; }
                tableOf = reinterpret_cast<MobInfoTable>(start);
                entryOf = reinterpret_cast<MobInfoEntry>(callTarget(insert + 0x23));
                addTag = reinterpret_cast<MobInfoTag>(callTarget(insert + 0x46));
            }
        }
        if (!tableOf) { say("mobs: the mob info table was not found; NOT copied"); return; }
        uint8_t* table = tableOf();
        uint8_t* head = *reinterpret_cast<uint8_t**>(table + 8);
        for (const auto& [m, type] : g_mobs)
        {
            uint8_t* source = nullptr;
            for (uint8_t* node = *reinterpret_cast<uint8_t**>(head); node != head; node = *reinterpret_cast<uint8_t**>(node))
            {
                int32_t key; memcpy(&key, node + 0x10, 4);
                if (key == m.source) { source = *reinterpret_cast<uint8_t**>(node + 0x18); break; }
            }
            if (!source) { say("%s: its source 0x%X has no mob info; the copy gets none", m.id.c_str(), m.source); continue; }
            uint8_t* entry = entryOf(table, type);
            memcpy(entry + 0x48, source + 0x48, 16);
            auto begin = *reinterpret_cast<uint8_t**>(source);
            auto end = *reinterpret_cast<uint8_t**>(source + 8);
            std::string tags;
            for (uint8_t* tag = begin; tag && tag < end; tag += 0x20)
            {
                addTag(entry, tag);
                uint64_t size = 0, capacity = 0;
                memcpy(&size, tag + 0x10, 8);
                memcpy(&capacity, tag + 0x18, 8);
                const char* text = capacity >= 16 ? *reinterpret_cast<const char* const*>(tag) : reinterpret_cast<const char*>(tag);
                if (size < 64) { tags.append(text, size); tags += ' '; }
            }
            float numbers[4]; memcpy(numbers, entry + 0x48, 16);
            say("%s: mob info copied from 0x%X - tags [ %s] numbers %g %g %g %g", m.id.c_str(), m.source, tags.c_str(), numbers[0], numbers[1], numbers[2], numbers[3]);
        }
    }

    // An MSVC std::string written in place: short enough for its own 16-byte buffer, so nothing
    // needs freeing - size at +0x10, capacity 15 at +0x18.
    void shortString(uint8_t* at, const std::string& text)
    {
        memset(at, 0, 0x20);
        memcpy(at, text.data(), text.size());
        uint64_t size = text.size(), capacity = 15;
        memcpy(at + 0x10, &size, 8);
        memcpy(at + 0x18, &capacity, 8);
    }

    // The names levels spawn mobs by - a mob group's {"type": "zombie"} - are an MSVC
    // unordered_map of EntityType to three strings ("minecraft" and two spellings), built at
    // start-up (1a7220, e048c0). Name to type (e1eef0) walks its element list; type to name
    // hashes the type (FNV-1a over its four bytes) into its buckets - and the level spawner asks
    // that way too: with the names only on the list, a mob group of the new type crashed the
    // level the moment it roamed, where the same blueprint under the game's own name did not.
    //
    // So a new mob goes in as the map's own constructor puts each entry in: a node of 0x78 bytes
    // from the game's allocator - next, prev, the type at +0x10, the strings at +0x18, +0x38 and
    // +0x58 - linked at the front of the list with the size bumped, then handed to the map's
    // insert (dffe20: map, result, &key, node), which files it in its bucket and rehashes if it
    // must. The map is found from the name lookup's walk; its constructor from the one place
    // that constructs it; the insert from the constructor's loop.
    using MapInsert = void* (__fastcall*)(void* map, void* result, const int32_t* key, uint8_t* node);

    void nameMobsForLevels(Game& game)
    {
        // mov r14,[rip+list] / mov rbx,[r14] / cmp rbx,r14 - the name lookup's walk.
        uint8_t* list = nullptr;
        for (uint8_t* at : scan(parse("4C 8B 35 ?? ?? ?? ?? 49 8B 1E 49 3B DE")))
        {
            int32_t rel; memcpy(&rel, at + 3, 4);
            uint8_t* found = at + 7 + rel;
            if (!list) { list = found; }
            else if (found != list) { say("mobs: level names are walked from two places; NOT named for levels"); return; }
        }
        if (!list) { say("mobs: the level names were not found; NOT named for levels"); return; }
        uint8_t* map = list - 8;

        // lea rcx,[rip+map] / call constructor - once in the game.
        MapInsert insert = nullptr;
        for (uint8_t* at : scan(parse("48 8D 0D ?? ?? ?? ?? E8")))
        {
            int32_t rel; memcpy(&rel, at + 3, 4);
            if (at + 7 + rel != map) { continue; }
            uint8_t* constructor = callTarget(at + 7);
            // add r8,10 / mov r9,[r9] / lea rdx,[rsp+28] / mov rcx,r15 / call insert
            static const uint8_t loop[] = { 0x49, 0x83, 0xC0, 0x10, 0x4D, 0x8B, 0x09, 0x48, 0x8D, 0x54, 0x24, 0x28, 0x49, 0x8B, 0xCF, 0xE8 };
            for (size_t i = 0; i < 0x180 && !insert; i++)
            {
                if (memcmp(constructor + i, loop, sizeof(loop)) == 0) { insert = reinterpret_cast<MapInsert>(callTarget(constructor + i + 15)); }
            }
        }
        if (!insert) { say("mobs: the level names' insert was not found; NOT named for levels"); return; }

        auto head = *reinterpret_cast<uint8_t**>(map + 8);
        auto size = reinterpret_cast<uint64_t*>(map + 0x10);
        if (!head) { say("mobs: the level names are not built; NOT named for levels"); return; }
        for (const auto& [m, type] : g_mobs)
        {
            std::string spelled = m.id;
            for (auto& c : spelled) { c = static_cast<char>(tolower(static_cast<unsigned char>(c))); }
            if (spelled.size() > 15) { say("%s: too long to name for levels", m.id.c_str()); continue; }
            auto node = static_cast<uint8_t*>(game.malloc(0x78));
            memset(node, 0, 0x78);
            memcpy(node + 0x10, &type, 4);
            shortString(node + 0x18, "minecraft");
            shortString(node + 0x38, spelled);
            shortString(node + 0x58, spelled);
            uint8_t* first = *reinterpret_cast<uint8_t**>(head);
            *reinterpret_cast<uint8_t**>(node) = first;
            *reinterpret_cast<uint8_t**>(node + 8) = head;
            *reinterpret_cast<uint8_t**>(first + 8) = node;
            *reinterpret_cast<uint8_t**>(head) = node;
            (*size)++;
            uint8_t result[16]{};
            insert(map, result, reinterpret_cast<const int32_t*>(node + 0x10), node);
            say("%s named for levels as \"%s\" (%llu names)", m.id.c_str(), spelled.c_str(), static_cast<unsigned long long>(*size));
        }
    }

    // ---------------------------------------------------------------- enchantment icons
    //
    // An enchantment's icon and icon material are what PreloadEnchantments loaded into its two
    // arrays at start-up, for ids up to 161 only; the getters (a62b10, a62b00) just index them
    // and have no fallback, and their one caller each is an exec thunk the UI calls from
    // Blueprint (ef6a00, ef6940). So an icon of its own is loaded on the first call, on the game
    // thread, by wrapping those thunks: the new id's array entry holds a mark of the plugin's
    // own, the original thunk returns it, and the wrapper swaps it for the loaded object - rooted,
    // as the class getter roots what it loads - and stores that in the array for next time.
    //
    // The wrapper goes in through the UFunction's own pointer to its thunk, which is heap data:
    // no game code is changed.
    struct OwnIcon { int id; std::wstring texture, material; void* sourceTexture; void* sourceMaterial; };
    std::vector<OwnIcon> g_icons;
    uint8_t g_marks[256];
    // The wrapper runs on the game thread for as long as the game does, long after the plugin's
    // own thread - and the Game it found things with, a local there - has ended. So it keeps a
    // copy of its own.
    Game g_gameKept;
    Game* g_game = nullptr;
    TArrayRaw* g_iconArrays[2]{};             // icons, materials
    Exec g_originalExec[2]{};

    bool readBlock(const void* at, void* out, size_t size)
    {
        __try { memcpy(out, at, size); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    void rootObject(void* object)
    {
        int32_t index = 0;
        if (!readBlock(static_cast<uint8_t*>(object) + 0xC, &index, 4) || index < 0 || index >= *g_game->objectCount) { return; }
        uint8_t* chunk = (*g_game->objectChunks)[index >> 16];
        if (!chunk) { return; }
        *reinterpret_cast<int32_t*>(chunk + static_cast<size_t>(index & 0xFFFF) * 0x18 + 8) |= 0x40000000;
    }

    void resolveIcon(void** result, int which)
    {
        auto mark = static_cast<uint8_t*>(*result);
        if (mark < g_marks || mark >= g_marks + sizeof(g_marks)) { return; }
        int id = static_cast<int>(mark - g_marks);
        void* found = nullptr;
        for (const auto& icon : g_icons)
        {
            if (icon.id != id) { continue; }
            const std::wstring& path = which == 0 ? icon.texture : icon.material;
            void* cls = which == 0 ? g_game->textureClass() : g_game->materialClass();
            found = g_game->load(cls, nullptr, path.c_str(), nullptr, 0, nullptr, true, nullptr);
            if (found)
            {
                rootObject(found);
                say("enchantment %d: its own %s loaded at %p", id, which == 0 ? "icon" : "icon material", found);
            }
            else
            {
                found = which == 0 ? icon.sourceTexture : icon.sourceMaterial;
                say("enchantment %d: %ls did not load; the source's is shown", id, path.c_str());
            }
            break;
        }
        reinterpret_cast<void**>(g_iconArrays[which]->Data)[id] = found;
        *result = found;
    }

    void __fastcall textureExec(void* context, void* stack, void** result) { g_originalExec[0](context, stack, result); resolveIcon(result, 0); }
    void __fastcall materialExec(void* context, void* stack, void** result) { g_originalExec[1](context, stack, result); resolveIcon(result, 1); }

    // The exec thunks, from their shared tail, told apart by which array their getter reads.
    bool findIconExecs(Game& game, TArrayRaw* icons, TArrayRaw* materials)
    {
        for (uint8_t* tail : scan(parse("48 89 7B 20 E8 ?? ?? ?? ?? 48 8B 5C 24 30 48 89 06")))
        {
            uint8_t* getter = callTarget(tail + 4);
            uint8_t code[15]{};
            if (!readBlock(getter, code, sizeof(code)) || code[0] != 0x48 || code[1] != 0x8B || code[2] != 0x05
                || code[7] != 0x0F || code[8] != 0xB6 || code[9] != 0xCA || code[14] != 0xC3) { continue; }
            int32_t rel; memcpy(&rel, code + 3, 4);
            uint8_t* reads = getter + 7 + rel;
            if (reads == reinterpret_cast<uint8_t*>(icons)) { game.textureExec = tail - 0xA3; }
            if (reads == reinterpret_cast<uint8_t*>(materials)) { game.materialExec = tail - 0xA3; }
        }
        return game.textureExec && game.materialExec;
    }

    // Each UFunction whose thunk pointer is one of the two gets the wrapper instead. Found by
    // walking GUObjectArray for an object holding that pointer; exactly one each, or nothing is
    // swapped.
    bool hookIconExecs(Game& game)
    {
        int32_t count = *game.objectCount;
        uint8_t** chunks = *game.objectChunks;
        uint8_t** slot[2]{};
        int hits[2]{};
        for (int32_t i = 0; i < count; i++)
        {
            uint8_t* chunk = nullptr;
            if (!readBlock(chunks + (i >> 16), &chunk, 8) || !chunk) { continue; }
            uint8_t* object = nullptr;
            if (!readBlock(chunk + static_cast<size_t>(i & 0xFFFF) * 0x18, &object, 8) || !object) { continue; }
            uint8_t* body[0x140 / 8]{};
            if (!readBlock(object, body, sizeof(body))) { continue; }
            for (size_t at = 0; at < 0x140 / 8; at++)
            {
                if (body[at] == game.textureExec) { slot[0] = reinterpret_cast<uint8_t**>(object) + at; hits[0]++; }
                if (body[at] == game.materialExec) { slot[1] = reinterpret_cast<uint8_t**>(object) + at; hits[1]++; }
            }
        }
        say("icon functions: %d texture, %d material", hits[0], hits[1]);
        if (hits[0] != 1 || hits[1] != 1) { return false; }
        g_originalExec[0] = reinterpret_cast<Exec>(game.textureExec);
        g_originalExec[1] = reinterpret_cast<Exec>(game.materialExec);
        *slot[0] = reinterpret_cast<uint8_t*>(&textureExec);
        *slot[1] = reinterpret_cast<uint8_t*>(&materialExec);
        return true;
    }

    // Registers each enchantment: a copy of its source's definition under a new id, then its name
    // in the enum once the engine has made that. Runs on the plugin's own thread.
    void addEnchantments(Game& game)
    {
        auto enchantments = readEnchantments();
        if (enchantments.empty()) { return; }
        TArrayRaw* header = nullptr;
        uint8_t** table = enchantmentTable(header);
        if (!table) { say("enchantments: NOT registered"); return; }

        struct Named { std::string id; int value; int source; bool own; std::string icon; };
        std::vector<Named> named;
        int next = FIRST_NEW_ENCHANTMENT;
        for (const auto& e : enchantments)
        {
            if (next >= header->Max) { say("enchantments: no room past id %d", next); break; }
            if (e.source <= 0 || e.source >= header->Num || !table[e.source]) { say("%s: its source %d is not an enchantment", e.id.c_str(), e.source); continue; }
            auto def = static_cast<uint8_t*>(game.malloc(ENCHANTMENT_SIZE));
            memcpy(def, table[e.source], ENCHANTMENT_SIZE);
            def[3] = static_cast<uint8_t>(next);
            std::wstring key = wide(e.id);
            // "-" keeps the source's text, in whatever language the game is in.
            auto text = [&](const std::wstring& value, const wchar_t* space, const std::wstring& textKey, size_t at)
            {
                if (value == L"-") { return; }
                FText t{};
                game.createText(&t, value.c_str(), space, textKey.c_str());
                memcpy(def + at, &t, sizeof(t));
            };
            text(e.name, L"Enchantment", key, 0x18);
            text(e.description, L"Enchantment", key + L"_desc", 0x30);
            text(e.builtIn, L"ItemType", key + L"_builtin", 0x48);
            text(e.effect, L"Enchantment", key + L"_effect", 0x60);
            // Its own blueprint - a copy of the source's in the New Items pak, carrying its numbers.
            // The icon and material stay the source's.
            bool own = e.folder != "-" && game.enchantmentClass;
            if (e.folder != "-" && !game.enchantmentClass) { say("%s: the enchantment finder is not known; it keeps its source's blueprint and numbers", e.id.c_str()); }
            if (own)
            {
                FName bp = name(game, e.folder);
                memcpy(def + 0x120, &bp, 8);
                g_blueprints.push_back({ name(game, e.id), bp });
            }
            table[next] = def;
            say("%s registered as enchantment %d, a copy of %d%s", e.id.c_str(), next, e.source, own ? ", with its own blueprint" : "");
            named.push_back({ e.id, next, e.source, own, e.icon });
            next++;
        }
        if (named.empty()) { return; }

        uint8_t** holder = enumStatic("EEnchantmentTypeID");
        if (!holder) { say("enchantments: the enum was not found; saves will not keep them"); return; }
        uint8_t* uenum = nullptr;
        for (int wait = 0; wait < 600 && !uenum; wait++)
        {
            if (!readPointer(holder, uenum) || !uenum) { uenum = nullptr; Sleep(200); }
        }
        if (!uenum) { say("enchantments: the enum was never made; saves will not keep them"); return; }

        // The arrays PreloadEnchantments (a72b80) keeps beside the definitions - icons,
        // icon materials and the loaded enchantment classes, 0x30 / 0x20 / 0x10 below the
        // definition table's header - are sized to exactly 162 at start-up and filled per id, so
        // a new id reads past their end: the first test crashed opening the item's tooltip, in
        // IsValid on the garbage icon. Once they are filled, each gets a bigger block with the
        // new ids' entries set to their source's. Their count stays 162, so nothing that walks
        // them sees the new ids; only a lookup by id does.
        TArrayRaw* arrays[3] = {
            reinterpret_cast<TArrayRaw*>(reinterpret_cast<uint8_t*>(header) - 0x30),
            reinterpret_cast<TArrayRaw*>(reinterpret_cast<uint8_t*>(header) - 0x20),
            reinterpret_cast<TArrayRaw*>(reinterpret_cast<uint8_t*>(header) - 0x10),
        };
        int highest = named.back().value;
        bool filled = false;
        for (int wait = 0; wait < 1500 && !filled; wait++)
        {
            filled = true;
            for (auto* a : arrays)
            {
                TArrayRaw now{};
                if (!readArray(a, now) || now.Num != 162 || !now.Data) { filled = false; break; }
                uint8_t* first = nullptr;
                if (!readPointer(reinterpret_cast<uint8_t**>(now.Data) + named.front().source, first) || !first) { filled = false; break; }
            }
            if (!filled) { Sleep(200); }
        }
        if (!filled) { say("enchantments: the icon and class arrays were never filled; NOT usable - do not open an item with one"); }
        else
        {
            // Icons of their own: the thunks wrapped before any mark goes in, so that nothing
            // hands a mark to the UI unwrapped.
            bool iconsHooked = false;
            bool wantIcons = false;
            for (const auto& n : named) { wantIcons = wantIcons || n.icon != "-"; }
            if (wantIcons)
            {
                if (!game.load || !game.objectCount || !game.objectChunks || !game.textureClass || !game.materialClass)
                {
                    say("enchantments: the object loader is not known; own icons NOT used - they show their source's");
                }
                else if (!findIconExecs(game, arrays[0], arrays[1])) { say("enchantments: the icon functions were not found; own icons NOT used"); }
                else
                {
                    g_gameKept = game;
                    g_game = &g_gameKept;
                    g_iconArrays[0] = arrays[0];
                    g_iconArrays[1] = arrays[1];
                    for (const auto& n : named)
                    {
                        if (n.icon == "-") { continue; }
                        size_t bar = n.icon.find('|');
                        if (bar == std::string::npos) { say("%s: its icon field is not texture|material", n.id.c_str()); continue; }
                        g_icons.push_back({ n.value, wide(n.icon.substr(0, bar)), wide(n.icon.substr(bar + 1)),
                            reinterpret_cast<uint8_t**>(arrays[0]->Data)[n.source], reinterpret_cast<uint8_t**>(arrays[1]->Data)[n.source] });
                    }
                    iconsHooked = !g_icons.empty() && hookIconExecs(game);
                    if (!iconsHooked) { say("enchantments: own icons NOT used - they show their source's"); }
                }
            }
            auto ownIcon = [&](const Named& n) { return iconsHooked && n.icon != "-"; };

            for (auto* a : arrays)
            {
                int32_t capacity = highest + 16;
                auto bigger = static_cast<uint8_t**>(game.malloc(static_cast<size_t>(capacity) * 8));
                memset(bigger, 0, static_cast<size_t>(capacity) * 8);
                memcpy(bigger, a->Data, static_cast<size_t>(a->Num) * 8);
                auto old = reinterpret_cast<uint8_t**>(a->Data);
                // An enchantment with a blueprint of its own gets no class: the class getter
                // (a627e0) loads it from the enchantment finder the first time it is asked, as
                // it would any enchantment PreloadEnchantments had not reached.
                for (const auto& n : named)
                {
                    bigger[n.value] = (n.own && a == arrays[2]) ? nullptr
                        : (ownIcon(n) && a != arrays[2]) ? g_marks + n.value
                        : old[n.source];
                }
                a->Max = capacity;
                a->Data = reinterpret_cast<uint8_t*>(bigger);      // the old block is left as it is
            }
            say("enchantments: icons, materials and classes set for %zu new id(s)", named.size());
        }

        auto names = reinterpret_cast<TArrayRaw*>(uenum + 0x40);
        for (const auto& n : named)
        {
            const std::string& id = n.id;
            int value = n.value;
            if (names->Num >= names->Max)
            {
                int32_t more = names->Max + 16;
                auto bigger = static_cast<uint8_t*>(game.malloc(static_cast<size_t>(more) * 16));
                memcpy(bigger, names->Data, static_cast<size_t>(names->Num) * 16);
                names->Data = bigger;           // the old block is left as it is: the game may still hold it
                names->Max = more;
            }
            FName full = name(game, "EEnchantmentTypeID::" + id);
            int64_t number = value;
            memcpy(names->Data + static_cast<size_t>(names->Num) * 16, &full, 8);
            memcpy(names->Data + static_cast<size_t>(names->Num) * 16 + 8, &number, 8);
            names->Num++;
            say("%s named in the enum as %d", id.c_str(), value);
        }
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
            if (line.empty() || line[0] == '#' || line[0] == '@') { continue; }

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
            // Armor properties: "3:2;7:0" - EArmorPropertyID and rarity (0 common, 2 unique).
            if (parts.size() > 7 && parts[7] != "-")
            {
                item.hasArmorProperties = true;
                size_t at = 0;
                while (at < parts[7].size())
                {
                    size_t semi = parts[7].find(';', at);
                    std::string one = parts[7].substr(at, semi == std::string::npos ? std::string::npos : semi - at);
                    at = semi == std::string::npos ? parts[7].size() : semi + 1;
                    size_t colon = one.find(':');
                    if (colon == std::string::npos) { continue; }
                    int property = atoi(one.substr(0, colon).c_str());
                    int rarity = atoi(one.substr(colon + 1).c_str());
                    if (property > 0 && property < 256 && rarity >= 0 && rarity < 3) { item.armorProperties.push_back({ property, rarity }); }
                }
            }
            // Numbers: "98=10;9c=8" - a record offset in hex and a float, for an artifact's own.
            if (parts.size() > 8 && parts[8] != "-")
            {
                size_t at = 0;
                while (at < parts[8].size())
                {
                    size_t semi = parts[8].find(';', at);
                    std::string one = parts[8].substr(at, semi == std::string::npos ? std::string::npos : semi - at);
                    at = semi == std::string::npos ? parts[8].size() : semi + 1;
                    size_t equals = one.find('=');
                    if (equals == std::string::npos) { continue; }
                    size_t offset = strtoul(one.substr(0, equals).c_str(), nullptr, 16);
                    float value = static_cast<float>(atof(one.substr(equals + 1).c_str()));
                    bool allowed = false;
                    for (size_t known : ARTIFACT_NUMBERS) { allowed = allowed || known == offset; }
                    if (allowed && value >= 0 && value < 100000) { item.numbers.push_back({ offset, value }); }
                    else { say("%s: refused number at +%zx", item.id.c_str(), offset); }
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

        if (item.hasArmorProperties)
        {
            size_t n = item.armorProperties.size();
            TArrayRaw list{ nullptr, 0, 0 };
            if (n > 0)
            {
                auto data = static_cast<uint8_t*>(game.malloc(n * 2));
                for (size_t i = 0; i < n; i++)
                {
                    data[i * 2] = static_cast<uint8_t>(item.armorProperties[i].first);
                    data[i * 2 + 1] = static_cast<uint8_t>(item.armorProperties[i].second);
                }
                list = TArrayRaw{ data, static_cast<int32_t>(n), static_cast<int32_t>(n) };
            }
            memcpy(record + ARMOR_PROPERTIES_AT, &list, sizeof(list));
            say("%s: %zu armor propert%s", item.id.c_str(), n, n == 1 ? "y" : "ies");
        }

        for (const auto& number : item.numbers)
        {
            memcpy(record + number.first, &number.second, sizeof(float));
        }
        if (!item.numbers.empty()) { say("%s: %zu number(s) set", item.id.c_str(), item.numbers.size()); }

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
    uint8_t* finderOf(Game& game, uint8_t* module, size_t slot = 0x48)
    {
        uint8_t* vtable = nullptr;
        uint8_t* getter = nullptr;
        if (!safeGet(module, vtable) || !safeGet(vtable + slot, getter)) { say("the module's vtable is not readable"); return nullptr; }
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

    // Enchantments with a blueprint of their own: each blueprint filed in the enchantment finder
    // under the enchantment's id, as the finder's constructor files the game's (707f10), and the
    // finder refreshed - so the class getter's lookup (the id's name in EEnchantmentTypeID, then
    // the definition's +0x120) finds the copy's path. The enchantment finder is the Dungeons
    // module's virtual at +0x50, beside the item finder at +0x48, and the same class.
    void fileEnchantmentBlueprints(Game& game)
    {
        if (g_blueprints.empty()) { return; }
        uint8_t* module = nullptr;
        for (int wait = 0; wait < 600 && !module; wait++)
        {
            if (!safeGet(game.module, module) || !module) { module = nullptr; Sleep(200); }
        }
        if (!module) { say("enchantments: the module was never made; their blueprints are NOT filed - do not use them"); return; }
        uint8_t* finder = finderOf(game, module, 0x50);
        if (!finder) { say("enchantments: the enchantment finder was not found; blueprints NOT filed - do not use them"); return; }

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
        if (!ours) { say("enchantments: the object at %p is not a finder; blueprints NOT filed - do not use them", finder); return; }

        bool readable = true;
        for (const auto& b : g_blueprints)
        {
            if (inPathTable(finder, b.id, readable) || !readable) { continue; }
            game.addPath(finder, game.enchantmentClass(), b.blueprint, b.id);
        }
        if (!readable) { say("enchantments: the finder's path table does not read; NOT refreshed"); return; }
        game.refresh(finder, true);
        int found = 0;
        for (const auto& b : g_blueprints) { if (inFinder(finder, b.id, readable)) { found++; } }
        say("enchantments: %d of %zu own blueprint(s) in the enchantment finder at %p", found, g_blueprints.size(), finder);
    }

    // ---------------------------------------------------------------- mob behaviour
    //
    // A mob's AI is built by a C++ switch on its EntityType (8b5a10: jump tables into one
    // behaviour builder per mob - 967d30 is the zombie's), fed by MobCharacter.EntityType (+0xBA0,
    // read by 905f60). A new type has no case, so a custom mob was spawned, possessed by its
    // MobBtController and never did anything. The switch is code; what reaches it is a virtual of
    // the controller (7c77b0, reading its pawn at +0x360, which sets up the behaviour through
    // 8b59c0), one slot in one vtable. That slot gets a wrapper: for a pawn whose type is a custom
    // mob, the type is its source's while the behaviour is built, and its own again afterwards.
    // It is the controller's OnPossess(pawn): its first call hands the pawn straight on to the
    // parent class's, which is what fills the controller's own pawn field. So the pawn is the
    // argument, and every argument register is passed through untouched - the first try took one
    // argument, left rdx holding whatever the wrapper had used it for, and the parent class read
    // garbage from it (a crash in MobBtController, loading arno).
    using ControllerSetup = void (__fastcall*)(void* controller, void* pawn, void* third, void* fourth);
    ControllerSetup g_originalSetup = nullptr;
    int32_t g_pawnAt = 0;        // the controller's pawn, from 7c77b0's mov rbx,[rdi+x]
    int32_t g_typeAt = 0;        // MobCharacter.EntityType, from 905f60's mov eax,[rcx+x]

    bool nameText(int32_t index, char* out, size_t size);

    // Whether a pawn is a MobCharacter: its class chain (UStruct's SuperStruct is +0x40 in a
    // shipping build) names MobCharacter. OnPossess runs for every pawn, the player's included,
    // and only a mob's +0xBA0 is its EntityType.
    bool isMob(uint8_t* pawn)
    {
        uint8_t* cls = nullptr;
        if (!readBlock(pawn + 0x10, &cls, 8)) { return false; }
        for (int depth = 0; cls && depth < 16; depth++)
        {
            int32_t name[2]{};
            char text[64];
            if (!readBlock(cls + 0x18, name, 8) || !nameText(name[0], text, sizeof(text))) { return false; }
            if (strcmp(text, "MobCharacter") == 0) { return true; }
            if (!readBlock(cls + 0x40, &cls, 8)) { return false; }
        }
        return false;
    }

    void __fastcall controllerSetup(void* controller, void* pawnArgument, void* third, void* fourth)
    {
        auto pawn = static_cast<uint8_t*>(pawnArgument);
        int32_t own = 0;
        int32_t source = 0;
        if (pawn && isMob(pawn) && readBlock(pawn + g_typeAt, &own, 4))
        {
            for (const auto& m : g_mobs) { if (m.type == own) { source = m.mob.source; break; } }
        }
        if (!source) { g_originalSetup(controller, pawnArgument, third, fourth); return; }
        memcpy(pawn + g_typeAt, &source, 4);
        g_originalSetup(controller, pawnArgument, third, fourth);
        memcpy(pawn + g_typeAt, &own, 4);
    }

    void hookMobBehaviour()
    {
        if (g_mobs.empty()) { return; }
        // cmp esi,10E / ja / je / lea eax,[rsi-0E] / cmp eax,FD - the switch, 0x30 into its function
        auto switches = scan(parse("81 FE 0E 01 00 00 0F 87 ?? ?? ?? ?? 0F 84 ?? ?? ?? ?? 8D 46 F2 3D FD 00 00 00"));
        if (switches.size() != 1) { say("mobs: the behaviour switch was found %zu times; custom mobs have no AI", switches.size()); return; }
        uint8_t* dispatch = switches[0] - 0x30;
        // mov r8d,ebx / mov rdx,rdi / mov rcx,rsi / call switch - the behaviour setup, 0x2D in
        uint8_t* setup = nullptr;
        for (uint8_t* at : scan(parse("44 8B C3 48 8B D7 48 8B CE E8"))) { if (callTarget(at + 9) == dispatch) { setup = at - 0x2D; } }
        if (!setup) { say("mobs: the behaviour setup was not found; custom mobs have no AI"); return; }
        // 905f60, the type reader, is the setup's first call: its mov eax,[rcx+x] says where the type is.
        uint8_t* reader = callTarget(setup + 0x18);
        for (int i = 0; i < 0x60 && !g_typeAt; i++)
        {
            if (reader[i] == 0x8B && reader[i + 1] == 0x81 && reader[i + 6] == 0x83 && reader[i + 7] == 0xF8 && reader[i + 8] == 0x01) { memcpy(&g_typeAt, reader + i + 2, 4); }
        }
        // The one function calling the setup, and the one vtable slot holding it.
        uint8_t* caller = nullptr;
        int callers = 0;
        HMODULE exe = GetModuleHandleW(nullptr);
        auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(exe);
        auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(reinterpret_cast<uint8_t*>(exe) + dos->e_lfanew);
        uint8_t* base = reinterpret_cast<uint8_t*>(exe);
        size_t size = nt->OptionalHeader.SizeOfImage;
        for (size_t i = 0; i + 5 < size; i++)
        {
            if (base[i] != 0xE8) { continue; }
            int32_t rel; memcpy(&rel, base + i + 1, 4);
            if (base + i + 5 + rel != setup) { continue; }
            callers++;
            uint8_t* start = base + i;
            while (start > base + 2 && !(start[-1] == 0xCC && start[-2] == 0xCC)) { start--; }
            caller = start;
        }
        if (callers != 1 || !caller) { say("mobs: the behaviour setup has %d callers; custom mobs have no AI", callers); return; }
        // mov rbx,[rdi+x] - where the controller keeps its pawn.
        for (int i = 0; i < 0x40 && !g_pawnAt; i++)
        {
            if (caller[i] == 0x48 && caller[i + 1] == 0x8B && caller[i + 2] == 0x9F) { memcpy(&g_pawnAt, caller + i + 3, 4); }
        }
        uint8_t** slot = nullptr;
        int slots = 0;
        for (size_t i = 0; i + 8 <= size; i += 8)
        {
            uint8_t* value; memcpy(&value, base + i, 8);
            if (value == caller) { slot = reinterpret_cast<uint8_t**>(base + i); slots++; }
        }
        if (slots != 1 || !g_typeAt || !g_pawnAt)
        {
            say("mobs: the controller's setup is in %d vtable slot(s), type at +0x%X, pawn at +0x%X; custom mobs have no AI", slots, g_typeAt, g_pawnAt);
            return;
        }
        DWORD was = 0;
        if (!VirtualProtect(slot, 8, PAGE_READWRITE, &was)) { say("mobs: the controller's vtable cannot be written; custom mobs have no AI"); return; }
        g_originalSetup = reinterpret_cast<ControllerSetup>(*slot);
        *slot = reinterpret_cast<uint8_t*>(&controllerSetup);
        VirtualProtect(slot, 8, was, &was);
        say("mobs: behaviour hooked - setup %p in the vtable at %p, pawn +0x%X, type +0x%X", caller, slot, g_pawnAt, g_typeAt);
    }

    // ---------------------------------------------------------------- crash note
    //
    // When the game faults in its own code, the plugin writes down where and what it was working
    // on, before the crash reporter takes over: for each register that points at a live UObject,
    // its name, its class and its outers - a component's outer is the actor it belongs to. The
    // minidump carries no heap, so this is the only way to say WHICH object a crash was in.
    // Reads only, through SEH; it never handles the fault, so the game crashes exactly as it would.
    uint8_t** g_names = nullptr;              // FName::GetNames' holder: its chunked entry table
    uint8_t* g_imageStart = nullptr;
    size_t g_imageSize = 0;
    volatile LONG g_crashNotes = 0;

    bool nameText(int32_t index, char* out, size_t size)
    {
        out[0] = 0;
        uint8_t* table = nullptr;
        if (!g_names || !readBlock(g_names, &table, 8) || !table || index <= 0) { return false; }
        uint8_t* chunk = nullptr;
        uint8_t* entry = nullptr;
        if (!readBlock(table + static_cast<size_t>(index / 16384) * 8, &chunk, 8) || !chunk) { return false; }
        if (!readBlock(chunk + static_cast<size_t>(index % 16384) * 8, &entry, 8) || !entry) { return false; }
        int32_t header = 0;
        if (!readBlock(entry + 8, &header, 4)) { return false; }
        if (header & 1)
        {
            wchar_t wide[64]{};
            if (!readBlock(entry + 0x0C, wide, sizeof(wide) - 2)) { return false; }
            WideCharToMultiByte(CP_UTF8, 0, wide, -1, out, static_cast<int>(size), nullptr, nullptr);
        }
        else if (!readBlock(entry + 0x0C, out, size - 1)) { return false; }
        out[size - 1] = 0;
        return true;
    }

    // "Name (Class)" for a UObject, or false when the pointer is not one.
    bool describe(uint8_t* object, char* out, size_t size)
    {
        uint8_t* vtable = nullptr;
        uint8_t* cls = nullptr;
        int32_t name[2]{}, clsName[2]{};
        if (!object || !readBlock(object, &vtable, 8) || vtable < g_imageStart || vtable >= g_imageStart + g_imageSize) { return false; }
        if (!readBlock(object + 0x10, &cls, 8) || !cls || !readBlock(object + 0x18, name, 8) || !readBlock(cls + 0x18, clsName, 8)) { return false; }
        char a[96], b[96];
        if (!nameText(name[0], a, sizeof(a)) || !nameText(clsName[0], b, sizeof(b))) { return false; }
        if (name[1] > 0) { snprintf(out, size, "%s_%d (%s)", a, name[1] - 1, b); }
        else { snprintf(out, size, "%s (%s)", a, b); }
        return true;
    }

    volatile LONG g_throwNotes = 0;

    // A C++ throw (MSVC's 0xE06D7363): its type name from the throw info, and - for anything
    // shaped like std::exception, a vtable then the message pointer - its message. Logged as it
    // is thrown, caught or not: code taken from Bedrock reports failures this way, and a level
    // that fails to set up does so quietly otherwise.
    void noteThrow(EXCEPTION_RECORD* r)
    {
        if (r->NumberParameters < 4 || InterlockedIncrement(&g_throwNotes) > 40) { return; }
        auto object = reinterpret_cast<uint8_t*>(r->ExceptionInformation[1]);
        auto info = reinterpret_cast<uint8_t*>(r->ExceptionInformation[2]);
        auto base = reinterpret_cast<uint8_t*>(r->ExceptionInformation[3]);
        // ThrowInfo +0x0C: catchable type array (rva) -> [0] count, [1] first type (rva) -> +4 type
        // descriptor (rva) -> +0x10 its decorated name.
        char type[128] = "?";
        int32_t arrayRva = 0, firstRva = 0, descriptorRva = 0;
        if (info && base && readBlock(info + 0x0C, &arrayRva, 4) && readBlock(base + arrayRva + 4, &firstRva, 4)
            && readBlock(base + firstRva + 4, &descriptorRva, 4))
        {
            readBlock(base + descriptorRva + 0x10, type, sizeof(type) - 1);
            type[sizeof(type) - 1] = 0;
        }
        char message[256] = "";
        const char* what = nullptr;
        if (object && readBlock(object + 8, &what, 8) && what)
        {
            if (!readBlock(what, message, sizeof(message) - 1)) { message[0] = 0; }
            message[sizeof(message) - 1] = 0;
            for (char* c = message; *c; c++) { if (static_cast<unsigned char>(*c) < 0x20 || static_cast<unsigned char>(*c) > 0x7E) { *c = 0; break; } }
        }
        auto from = static_cast<uint8_t*>(r->ExceptionAddress);
        say("THROW NOTE: %s \"%s\" (thrown at %p)", type, message, from);
    }

    LONG CALLBACK crashNote(EXCEPTION_POINTERS* e)
    {
        if (e->ExceptionRecord->ExceptionCode == 0xE06D7363) { noteThrow(e->ExceptionRecord); return EXCEPTION_CONTINUE_SEARCH; }
        if (e->ExceptionRecord->ExceptionCode != EXCEPTION_ACCESS_VIOLATION) { return EXCEPTION_CONTINUE_SEARCH; }
        auto at = static_cast<uint8_t*>(e->ExceptionRecord->ExceptionAddress);
        // Only the game's own code: the plugin's guarded reads fault on purpose.
        if (at < g_imageStart || at >= g_imageStart + g_imageSize) { return EXCEPTION_CONTINUE_SEARCH; }
        if (InterlockedIncrement(&g_crashNotes) > 3) { return EXCEPTION_CONTINUE_SEARCH; }
        CONTEXT* c = e->ContextRecord;
        say("CRASH NOTE: access violation at game+%llX reading %llX", static_cast<unsigned long long>(at - g_imageStart),
            static_cast<unsigned long long>(e->ExceptionRecord->ExceptionInformation[1]));
        const struct { const char* name; DWORD64 value; } registers[] = {
            { "rcx", c->Rcx }, { "rdx", c->Rdx }, { "rbx", c->Rbx }, { "rsi", c->Rsi }, { "rdi", c->Rdi },
            { "r12", c->R12 }, { "r13", c->R13 }, { "r14", c->R14 }, { "r15", c->R15 },
        };
        for (const auto& r : registers)
        {
            auto object = reinterpret_cast<uint8_t*>(r.value);
            char line[160];
            if (!describe(object, line, sizeof(line))) { continue; }
            say("CRASH NOTE:   %s %p = %s", r.name, object, line);
            uint8_t* outer = object;
            for (int depth = 0; depth < 5; depth++)
            {
                if (!readBlock(outer + 0x20, &outer, 8) || !outer || !describe(outer, line, sizeof(line))) { break; }
                say("CRASH NOTE:     in %s", line);
            }
        }
        if (g_log) { fflush(g_log); }
        return EXCEPTION_CONTINUE_SEARCH;
    }

    // A world watch while custom mobs are being tried: once a second, which World objects exist,
    // how many PlayerControllers and how many Zombie-variant characters - logged when that
    // changes. It says whether a level was entered, whether the mob came, and what the game did
    // before it left. Reads only.
    DWORD WINAPI watchWorlds(LPVOID)
    {
        std::string last;
        for (int tick = 0; tick < 3600; tick++)
        {
            Sleep(1000);
            int32_t count = 0;
            uint8_t** chunks = nullptr;
            if (!g_game || !g_game->objectCount || !readBlock(g_game->objectCount, &count, 4) || !readBlock(g_game->objectChunks, &chunks, 8) || !chunks) { continue; }
            std::string worlds;
            int controllers = 0, zombies = 0;
            // Each custom mob's own blueprint class: BP_CreeperBtCharacter_C and its objects.
            std::vector<std::string> classes;
            for (const auto& m : g_mobs)
            {
                std::string bp(m.mob.blueprint.begin(), m.mob.blueprint.end());
                classes.push_back(bp.substr(bp.find_last_of('/') + 1) + "_C");
            }
            for (int32_t i = 0; i < count; i++)
            {
                uint8_t* chunk = nullptr;
                uint8_t* object = nullptr;
                if (!readBlock(chunks + (i >> 16), &chunk, 8) || !chunk) { continue; }
                if (!readBlock(chunk + static_cast<size_t>(i & 0xFFFF) * 0x18, &object, 8) || !object) { continue; }
                uint8_t* cls = nullptr;
                int32_t clsName[2]{}, name[2]{};
                if (!readBlock(object + 0x10, &cls, 8) || !cls || !readBlock(cls + 0x18, clsName, 8)) { continue; }
                char c[96];
                if (!nameText(clsName[0], c, sizeof(c))) { continue; }
                if (strcmp(c, "World") == 0)
                {
                    char n[96];
                    if (readBlock(object + 0x18, name, 8) && nameText(name[0], n, sizeof(n)) && strncmp(n, "Default__", 9) != 0) { worlds += n; worlds += " "; }
                }
                else if (strstr(c, "PlayerController") && strncmp(c, "Default__", 9) != 0) { controllers++; }
                else
                {
                    for (const auto& one : classes) { if (one == c) { zombies++; break; } }
                }
            }
            char line[512];
            snprintf(line, sizeof(line), "worlds [ %s] player controllers %d, custom mobs' blueprint objects %d", worlds.c_str(), controllers, zombies);
            if (last != line) { last = line; say("WATCH: %s", line); }
        }
        return 0;
    }

    void noteCrashes()
    {
        HMODULE exe = GetModuleHandleW(nullptr);
        auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(exe);
        auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(reinterpret_cast<uint8_t*>(exe) + dos->e_lfanew);
        g_imageStart = reinterpret_cast<uint8_t*>(exe);
        g_imageSize = nt->OptionalHeader.SizeOfImage;
        // FName::GetNames: sub rsp,28 / mov rax,[rip+holder] / test rax,rax / jne / mov ecx,808h
        auto getNames = scan(parse("48 83 EC 28 48 8B 05 ?? ?? ?? ?? 48 85 C0 75 ?? B9 08 08 00 00"));
        if (getNames.size() != 1) { say("crash notes: the name table was not found"); return; }
        int32_t rel; memcpy(&rel, getNames[0] + 7, 4);
        g_names = reinterpret_cast<uint8_t**>(getNames[0] + 11 + rel);
        AddVectoredExceptionHandler(1, crashNote);
        say("crash notes: on");
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
        for (int attempt = 0; attempt < 600 && !ready; attempt++)
        {
            ready = find(game);
            if (!ready) { Sleep(100); }
        }
        if (!ready) { say("the game's code was never found; nothing was changed"); return 0; }
        noteCrashes();
        nameMobs(game);
        // The world watch (watchWorlds) found why custom mobs broke levels; it walks every object
        // once a second, so it is only started when MCDRebornWatch.txt sits beside the plugin.
        if (!g_mobs.empty() && game.objectCount && game.objectChunks && GetFileAttributesW((g_folder + L"MCDRebornWatch.txt").c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            g_gameKept = game;
            g_game = &g_gameKept;
            CreateThread(nullptr, 0, watchWorlds, nullptr, 0, nullptr);
        }

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
        addMobs(game);
        hookMobBehaviour();
        addEnchantments(game);
        fileEnchantmentBlueprints(game);
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
