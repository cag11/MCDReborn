# &nbsp;[![icon](MCDSaveEdit/Properties/icon.ico)]() MCD Reborn

[![GitHub](https://img.shields.io/github/license/cag11/MCDReborn)](https://github.com/cag11/MCDReborn/blob/main/LICENSE)
[![GitHub release (latest by date)](https://img.shields.io/github/v/release/cag11/MCDReborn?label=latest)](https://github.com/cag11/MCDReborn/releases/latest)
[![GitHub Release Date](https://img.shields.io/github/release-date/cag11/MCDReborn)](https://github.com/cag11/MCDReborn/releases/latest)
[![GitHub all releases](https://img.shields.io/github/downloads/cag11/MCDReborn/total)](https://github.com/cag11/MCDReborn/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

 Windows application for adding custom skins and modifying [Minecraft: Dungeons](https://www.minecraft.net/en-us/about-dungeons/) save files.
 This is new and upgraded Reborn version of [MCDSaveEdit](https://github.com/CutFlame/MCDSaveEdit) by CutFlame.

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

#### Custom Builds Feature
* **Open in MCD Builder**: send the equipped loadout straight to [mcdbuilder.vercel.app](https://mcdbuilder.vercel.app/)
* **Import Build** and **Import and Equip** from the clipboard, refused up front if the inventory has no room for it

https://github.com/user-attachments/assets/509496fd-7186-4422-a639-9d10272be407

#### Recolor Gear Feature
* **Recolor Gear**: put your own artwork on a piece of gear, installed as a mod pak beside the game's own; the originals are never modified and Remove undoes it completely
* The texture travels to [mcddesigner.vercel.app](https://mcddesigner.vercel.app/) and back. Find, upload and download again

https://github.com/user-attachments/assets/1109d845-6a2e-49da-992d-f185dd0f4d26

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

##### Application Stopped Working

If during launch you get a popup saying that the application has stopped working,
this means an internal error occurred and could mean various issues.

The releases are self-contained and need no runtime installed. If you are running a
framework-dependent build instead, it needs the .NET 10 Desktop Runtime:

- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

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
