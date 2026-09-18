# &nbsp;[![icon](MCDSaveEdit/Properties/icon.ico)]() MCD Reborn

[![GitHub](https://img.shields.io/github/license/cag11/MCDReborn)](https://github.com/cag11/MCDReborn/blob/main/LICENSE)
[![GitHub release (latest by date)](https://img.shields.io/github/v/release/cag11/MCDReborn?label=latest)](https://github.com/cag11/MCDReborn/releases/latest)
[![GitHub Release Date](https://img.shields.io/github/release-date/cag11/MCDReborn)](https://github.com/cag11/MCDReborn/releases/latest)
[![GitHub all releases](https://img.shields.io/github/downloads/cag11/MCDReborn/total)](https://github.com/cag11/MCDReborn/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

 A companion app for [Minecraft: Dungeons](https://www.minecraft.net/en-us/about-dungeons/) edit your save, restyle your heroes and gear, and change the camera while you play.
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

#### Camera Feature
* **Change the camera while the game is running** - distance, angle, field of view, where it looks, shoulder offset and swing smoothing, all applied as you drag the slider. No mod pak, nothing written to disk, and quitting the game puts everything back
* **Third person and first person**, in a game that has neither: mouse look turns the view, W A S D move you, and clicking attacks instead of walking you there
* **Presets** - third person, third person far, first person - and you can save your own under a name and bring it back in one click
* F10 turns the camera setup on and off from inside the game
* **Camera smoothing**, which matters most in first person and has no right answer - low and the camera trails you up a staircase until your own model rises into view, high and every step goes straight into it. It is a slider because it is taste: first person starts at 20, third person at the game's own 1
* **Ride**, which is a mount without a mount: nothing can be spawned from outside a process, so the nearest creature is stopped where it stands and carried under you instead. Press R once and it keeps looking - use a summoning artifact afterwards and it climbs onto the sheep, wolf or llama as soon as that arrives. Speed is adjustable, the rider's legs can be frozen, and the creature is put back under you five hundred times a second, with its gravity taken away for as long as you are on it, so it neither steps along behind you nor sinks between one correction and the next
* **Camera forward**, which slides the lens out in front of the character's face - the setting that can put the head out of frame entirely in first person
* **Jump** on Q, which the game has no button for at all - not the character jumping but a launch written straight into its velocity, which is why it works when asking the character politely does not. Height, steering in mid-air and the number of jumps before landing are all adjustable

* A **Ride height** slider, because the rider is placed on the creature's collision capsule rather than on what is drawn - fine for the game's own animals, and the difference you have to make up when your own model is on one
* **Fly on G**, off again on G: the character lifts out of the level entirely, and switching it off drops them back to the floor with gravity and their own flight settings put back exactly as they were found
* Q, G and R are all live the moment the camera is switched on - the game has no button for any of them, so there is nothing of its own to clash with

#### Custom Builds Feature
* **Open in MCD Builder**: send the equipped loadout straight to [mcdbuilder.vercel.app](https://mcdbuilder.vercel.app/)
* **Import Build** and **Import and Equip** from the clipboard, refused up front if the inventory has no room for it

https://github.com/user-attachments/assets/509496fd-7186-4422-a639-9d10272be407

#### Recolor Gear Feature
* **Recolor Gear**: put your own artwork on a piece of gear, installed as a mod pak beside the game's own; the originals are never modified and Remove undoes it completely
* Armor, melee, ranged, artifacts, **capes, pets, enchantment icons and the interface and HUD**, picked one category at a time
* The texture travels to [mcddesigner.vercel.app](https://mcddesigner.vercel.app/) and back. Find, upload and download again

https://github.com/user-attachments/assets/1109d845-6a2e-49da-992d-f185dd0f4d26

#### Difficulty Feature
* **Change how hard the game is while it is running** - how tough and how fast the enemies are, and your own speed, roll cooldown, roll charges, gravity and attack speed
* Toughness changes the damage enemies take rather than their health, so it works the same on every kind of enemy
* Held against the game rather than set once: enemies that appear later, a new level, or dying and respawning all get the settings put back within half a second
* Nothing is written to disk - quitting the game puts everything back, and so does the button
* Your own damage reduction is left alone on purpose, because that number comes from your gear
* **Only animate what you can see**: the game poses every enemy in the level every frame, including the ones behind you. Turning that off measured 54 frames a second to 138 in a fight with 198 enemies, and changes nothing you can see

#### Weapon Import Feature
* **Import your own model** onto any weapon: export a `.glb` from Blender (File -> Export -> glTF Binary) and the weapon comes out wearing it, mesh and texture together, weapons installed as a mod pak beside the game's own so deleting it undoes everything
* **Or just reshape the game's own model**: resize from a tenth up to **8x**, move and rotate it - a claymore at half size, a dagger the length of a spear, or something absurd
* A live 3D preview **painted with the real texture**, which you can drag to turn and scroll to zoom, with the **original ghosted behind your model**. That outline is where the game already knows how to hold the weapon, so keeping the handle end of your model on the handle end of the outline is all there is to aligning it
* Imported models are **fitted on arrival** - scaled to the weapon's size and centred on it - so the sliders start somewhere sensible instead of at a speck or a wall
* It replaces the weapon's model and texture - not its stats, and not how it behaves. A weapon using several materials is refused rather than mangled

#### Mob Import Feature
* **Import your own model onto a creature**: export a `.glb` from Blender the same way, pick a sheep, a pig, a wolf or any of 122 other meshes, and the creature comes out wearing it. A skateboard, a mount, a truck - anything that should move under its own power, because a creature is the one thing the engine already animates
* **It rides rather than bends.** The model is attached rigidly to the skeleton's root, so it walks, charges and gets summoned by an artifact without being deformed by the walk cycle. For a vehicle that is the point rather than a compromise
* The same live 3D preview as the weapon tab - drag to turn, scroll to zoom, real texture, **original ghosted behind**. Stand your model on the outline's feet and face it the same way, or it will walk sunk into the floor or backwards
* Grouped into **creatures and props**, with a search box. Props are the game's animated scenery - gates, doors, windmills, banners
* **Summoned creatures come out right too.** An artifact does not summon the animal that wanders the level - it summons a variant with its own skin and a coloured glow, which is why an imported model used to arrive pink. Every skin a creature has is painted, and the game's colouring is turned off, so the model you brought is the model you get
* Installed as a mod pak beside the game's own, so deleting it undoes everything
* Creatures drawn at several levels of detail, in several pieces, or with cloth on them are **left out of the list rather than listed and refused**

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
