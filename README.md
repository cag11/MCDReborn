# &nbsp;[![icon](MCDSaveEdit/Properties/icon.ico)]() MCD Reborn

[![GitHub](https://img.shields.io/github/license/cag11/MCDReborn)](https://github.com/cag11/MCDReborn/blob/main/LICENSE)
[![GitHub release (latest by date)](https://img.shields.io/github/v/release/cag11/MCDReborn?label=latest)](https://github.com/cag11/MCDReborn/releases/latest)
[![GitHub Release Date](https://img.shields.io/github/release-date/cag11/MCDReborn)](https://github.com/cag11/MCDReborn/releases/latest)
[![GitHub all releases](https://img.shields.io/github/downloads/cag11/MCDReborn/total)](https://github.com/cag11/MCDReborn/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

 A companion app and modding platform for [Minecraft: Dungeons](https://www.minecraft.net/en-us/about-dungeons/) edit your save, restyle your heroes and gear, and change the camera while you play.
 This is extended Reborn version of [MCDSaveEdit](https://github.com/CutFlame/MCDSaveEdit) by CutFlame.

https://github.com/user-attachments/assets/37a02d17-7f2b-4d0c-a0f2-47f183cfc7b5

### Features

* **Everything MCDSaveEdit already did**: editing items, enchantments, stats, counters and currencies; reading and writing the encrypted save format
* Ported from .NET Framework 4.8 to **.NET 10**, every package updated, the known advisories cleared
* **Custom Skins tab**: play as any of the game's 67 hero skins, written to the save for you, no mod needed
* Wear any 64×64 Minecraft skin as your hero; import any skin from your favorite skin website
* A **Show armours** switch on both the Custom Skins and Recolor Gear tabs: wearing a skin hides armour so the skin can be seen, and one tick puts it all back
* Five themes in the **View** menu (Dark, Light, Nether, End and Frost), swapped live and remembered between runs
* Item search across the whole inventory, filtering as you type, without covering the UI
* A search box on every picker (items, armor, melee, ranged, artifacts, armor properties and enchantments), narrowing within whatever the filters already allow
* A **Defaults** button fills in the properties an armor drops with in game; changing an armor's type no longer replaces them by itself
* Right-click an inventory item to equip it; artifacts offer all three hotbar slots
* Right-click an equipment slot to unequip
* Every save backs the file up first, keeping one timestamped `.bak` beside it
* The enchantment picker opens on the gear type being enchanted, with **Melee / Armor / Ranged / Other** toggles
* A loading screen that is a themed card with a vector mark, version and status line
* **The Tower** tab: tower runs that you have started and saved will show here, allow modifying gear and floor progress
* 36 new enchantments the game carries but never offers, under the **Other** toggle
* Bulk Delete items on the inventory and storage chest tabs
* **Difficulty Tab:** Change how hard the game is while it is running. How tough, how fast and how heavy the enemies are, and your own speed, roll cooldown, roll charges, gravity and attack speed
* **Escalation Mode:** Enemies get worse the longer you stay in a level. A slim bar over the game says which of the nine stages you are in, how long until the next one, and what the multipliers are now. The clock restarts when you load a new level.
* **Press J to throw every enemy in the level into the air**, with the enemy gravity slider deciding how long they stay there - at normal weight they reach 141 units, at a fortieth nearly 3000. The two go together: gravity does nothing to a mob standing on the floor
* **Music Tab:** Play your own MP3 over any of the game's 112 music tracks, installed as a mod pak beside the game's own so removing it puts the original back
* **Installed Mods Tab:** Every mod pak in one place. You can import and export multiple mods via a zip package.
* **Payload loader**, built into the app one button. Install a payload folder, point it at a folder cooked in Unreal and the whole tree is installed at once, the level and the blueprints, models, materials and resource packs it is built from. 

#### Custom Maps Feature
* **A mission in this game is a Minecraft world** - a level file naming a sequence of rooms, and tiles whose geometry is Minecraft blocks. So any of the game's missions can be exported, changed and put back
* **You can save and later edit terrain in Minecraft**: the whole mission goes out as one Minecraft world you can walk start to end, you build in it, and **Bring back** writes your blocks into the tiles they came from
* **Edit spawns and mobs in the app**, in a 3D view of the mission drawn in its own block colours - taken from the mission's own resource pack. Drag to turn, WASD to move, and a slider takes the roof off so you can see inside buildings
* **Place spawn points by clicking the ground**, with a radius and a count; each one drops onto the floor beneath it. Click one to pick it out and remove it. 
* **Change what spawns**: every mob group in the mission, bosses included, with each one saying whether it roams the level, fires only inside an arena fight, or is not used at all
* **Installed as a mod pak** beside the game's own files. The game's files are never modified, and **Remove** puts the original mission back

See **[CustomMaps.md](CustomMaps.md)** for the whole workflow: which Minecraft blocks survive the trip, what the converter needs, and the three reasons a mob edit can look ignored.

#### Camera Feature
* **Change the camera while the game is running** - distance, angle, field of view, where it looks, shoulder offset and swing smoothing, all applied as you drag the slider. No mod pak, nothing written to disk, and quitting the game puts everything back
* **Third person and first person**, in a game that has neither: mouse look turns the view, W A S D move you, and clicking attacks instead of walking you there
* **Presets** - third person, third person far, first person, first person shooter
* **A crosshair** for the shooter preset, in five shapes - cross, chevron, circle, dot, brackets - eight colours and any size. 
* **Ride**, can summon and ride the nearest creature, your summons, even your custom mounts via imported texture
* **Jump**, you can activate jump buy pressing the Q key when enabled which allows improved movement. 
* **Fly**, the default hotkey is G: makes character lift out of the level entirely, and switching it off drops them back to the floor. Space climbs and C drops, and forward follows wherever the camera points
* F10 turns the camera setup on and off from inside the game

#### Custom Builds Feature
* **Open in MCD Builder**: send the equipped loadout straight to [mcdbuilder.vercel.app](https://mcdbuilder.vercel.app/)
* **Import Build** and **Import and Equip** from the clipboard, refused up front if the inventory has no room for it

https://github.com/user-attachments/assets/509496fd-7186-4422-a639-9d10272be407

#### Recolor Gear Feature
* **Recolor Gear**: put your own artwork on a piece of gear, installed as a mod pak beside the game's own; the originals are never modified and Remove undoes it completely
* Armor, melee, ranged, artifacts, **capes, pets, enchantment icons and the interface and HUD**, picked one category at a time
* The texture travels to [mcddesigner.vercel.app](https://mcddesigner.vercel.app/) and back. Find, upload and download again

https://github.com/user-attachments/assets/1109d845-6a2e-49da-992d-f185dd0f4d26

#### Weapon Import Feature
* **Import your own model** onto any weapon and projectiles: export a `.glb` from Blender (File -> Export -> glTF Binary) and the weapon comes out wearing it, mesh and texture together, weapons installed as a mod pak beside the game's own so deleting it undoes everything
* Two things worth knowing before you model: this game's weapon textures are tiny - often 32 square - and your artwork is scaled to whatever the one you are replacing uses, so fine detail cannot survive. And an **enchanted** weapon wears the game's purple glint over whatever it is; on a dark model that is most of what you will see, so export brighter than feels right
* **Or just reshape the game's own model**: resize from a tenth up to **8x**, move and rotate it - a claymore at half size, a dagger the length of a spear, or something absurd

#### Mob Import Feature
* **Import your own model onto a creature**: export a `.glb` from Blender the same way, pick a sheep, a pig, a wolf or any of 122 other meshes, and the creature comes out wearing it. A skateboard, a mount, a truck - anything is possible as long as you have 3D design for it.
* **Summoned creatures come out right too.** An artifact summons a variant with its own custom skin, you can change skin to achieve custom looking summons. 

#### DISCLAIMER: Please keep backups of your save files! This app does not guarantee your save file to be playable after editing!

<img src="Screenshots/overview.png"/>

---

### Installing and Running

For full features and functionality you need Minecraft: Dungeons installed and preferably in the default install location.

1. Download the latest release from [the releases section](https://github.com/cag11/MCDReborn/releases). It is a single self-contained `MCDReborn.exe` with no prerequisites. A smaller framework-dependent build is also published; that one needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

### How to Use

1. Go to `File > Open` in the menu bar.
2. Open the folder `%HOMEPATH%\Saved Games\Mojang Studios\Dungeons`. *Be sure to backup this folder before editing your save files.*
3. You will see a folder with a 16-digit number (E.g. 2612325419657064). Open this folder, then open the "Characters" folder.
    1. NOTE: If multiple Mojang or Microsoft accounts were used in-game you might see more than one folder. 
4. Select one of the files ending in `.dat`. There will be one for each character.
5. Click `Open` and edit your save. When ready go to `File > Save` or `File > Save As...` to save your edits.

<p></p>

---

### Troubleshooting

##### Fix Missing Images

<img src="Screenshots/GameContentLocationDialog.png"/>

If you see this popup, that means it couldn't find the game content in the default location. You need to provide the path to these `.pak` files:

<img src="Screenshots/LocatePakFiles.png"/>

##### Default location of pak files

Minecraft Launcher:

`%localappdata%\Mojang\products\dungeons\dungeons\Dungeons\Content\Paks`

Steam:

`%programfiles(x86)%\Steam\steamapps\common\MinecraftDungeons\Dungeons\Content\Paks`

Microsoft Store:

`C:\XboxGames\Minecraft Dungeons\Content\Dungeons\Content\Paks`

If you're not sure which version you have, additional information may be found on [Dokustash - stash.dokucraft.co.uk](https://stash.dokucraft.co.uk/?help=modding-dungeons)

##### If it closes by itself

It keeps a log at `%LOCALAPPDATA%\MCDReborn\log.txt`, and anything that went wrong is in it with
the full stack. Ordinarily it writes a handful of lines a session. For the whole story of what you
were doing, use the `-debug` build, or put an empty file called `verbose.txt` beside the exe.

##### Application Stopped Working

If during launch you get a popup saying that the application has stopped working,
this means an internal error occurred and could mean various issues.

The releases are self-contained and need no runtime installed.

---

### Special Thanks

Thanks to the original author CutFlame for coming up with the idea and creating the original MCDSaveEdit application.

Thanks to Plushy for discussing ideas, giving guidance and testing the application.

### Legal Disclaimer

This project is not affiliated with Mojang Studios, Microsoft, XBox Game Studios, Double Eleven or the Minecraft brand.

"Minecraft" is a trademark of Mojang Synergies AB.

Other trademarks referenced herein are property of their respective owners.

### External Credits and Licenses

[MCDSaveEdit](https://github.com/CutFlame/MCDSaveEdit) © Michael Holt ([MIT](https://github.com/CutFlame/MCDSaveEdit/blob/master/LICENSE))

[DungeonTools](https://github.com/HellPie/DungeonTools) © Diego Russi ([AGPL 3.0](https://github.com/HellPie/DungeonTools/blob/master/LICENSE))

[FModel](https://github.com/iAmAsval/FModel) © Free Software Foundation, Inc. ([GPL 3.0](https://github.com/iAmAsval/FModel/blob/master/LICENSE))

[PakReader](https://github.com/WorkingRobot/PakReader) © Aleks Margarian ([MIT](https://github.com/WorkingRobot/PakReader/blob/master/LICENSE))

[Game-icons.net](https://game-icons.net/) © Lorc, Delapouite and contributors ([CC BY 3.0](http://creativecommons.org/licenses/by/3.0/))
