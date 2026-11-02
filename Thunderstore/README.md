# Arms Race

You accidentally brought habits from another world into SULFUR.  
Now you have to Arms Race your way out.

**Arms Race** is a BepInEx mod for **SULFUR** that turns weapon progression into a chaotic run-based weapon rotation system. Entering a level gives you a randomized weapon, and killing enemies can grant a new randomized weapon after a short cooldown.

Generated weapons use the game’s existing weapon systems: random vanilla weapons, random oils, random scrolls, compatible attachments, rank adjustment, and durability safety.

## Features

- Gives a random weapon when entering a non-safe-zone level.
- Killing eligible enemies can grant a new random weapon.
- Kill reward has a configurable cooldown.
- Generated weapons are placed into Weapon Slot 1 or Weapon Slot 2 when possible.
- Old generated weapons are removed before generating a new one.
- Generated weapons are removed before level transitions.
- Generated weapons are removed when using The Amulet to return to the safe zone.
- Generated weapons are destroyed if dropped, preventing pickup/tag issues.
- Uses vanilla weapons, oils, scrolls, attachments, rank, and durability systems.
- Does not modify original game files.
- Does not permanently add custom mod data to the save file.

## What Gets Randomized

Generated weapons can include:

- A random vanilla weapon
- Random oils
- Random scrolls
- Random compatible attachments
- Rank adjustment based on applied enchantments
- Durability normalization and minimum durability protection

This mod does **not** directly modify `WeaponSO` base weapon assets.  
That means it does not globally alter the original weapon definitions, and it does not permanently change all copies of the same weapon.

## Arms Race Behavior

By default:

- Entering a playable non-safe-zone level grants one random generated weapon.
- Killing an eligible enemy can grant another generated weapon.
- Kill reward cooldown is 1 second.
- Each new generated weapon removes the previous generated weapon.
- Debug key spawning is disabled by default.

The intended experience is:

```text
Get a random weapon.
Fight with it.
Kill enemies.
Get another random weapon.
Keep adapting.
````

## Generated Weapon Lifetime

Generated weapons are designed for the current run/level flow.

To keep the game safe and avoid save issues:

* Generated weapons are runtime-tagged.
* Old generated weapons are removed when a new generated weapon is created.
* Generated weapons are removed before level transitions.
* Generated weapons are removed before Amulet teleport.
* Dropping a generated weapon destroys it instead of creating a ground pickup.

This is intentional. Generated weapons are not meant to be stored permanently.

## Configuration

After launching the game once with the mod installed, a config file will be created:

```text
BepInEx/config/kumo.sulfur.arms_race.cfg
```

Main options include:

```ini
[General]
EnableMod = true
RandomizeOnLevelStart = true
PreferredWeaponSlot = FirstAvailable
ForceSelectGeneratedWeapon = true
CleanupOldGeneratedWeapons = true

[Kill Reward]
EnableRandomWeaponOnKill = true
RandomWeaponOnKillCooldown = 1
KillRewardRequiresPlayableLevel = true
KillRewardRequireExperienceOnKill = true

[Random Upgrades]
EnableRandomOils = true
MinOilCount = 1
MaxOilCount = 5
EnableRandomScrolls = true
ScrollChance = 0.5
EnableRandomAttachments = true
AttachmentChance = 0.6
GrantRankForAppliedOils = true
RespectEnchantmentSlots = false

[Safety]
CleanupGeneratedWeaponsBeforeLevelTransition = true
DestroyGeneratedWeaponsOnDrop = true
FixLowDurability = true
MinimumDurabilityNormalized = 0.5

[Debug]
DebugKeyEnabled = false
DebugKey = K
```

## Recommended Default Experience

The default configuration is intended to be chaotic but still playable:

* Oils are always added, from 1 to 5.
* Higher oil counts are slightly less common.
* Scrolls have a moderate chance to appear.
* Attachments have a moderate chance to appear.
* Kill reward cooldown prevents excessive weapon spam.
* Debug spawning is disabled unless manually enabled.

## Debug Key

Debug spawning is disabled by default.

To enable it:

```ini
[Debug]
DebugKeyEnabled = true
DebugKey = K
```

When enabled, pressing the configured key generates a new random weapon using the same mod logic.

## Installation

### Thunderstore / r2modman

Install through Thunderstore Mod Manager or r2modman.

Make sure BepInEx is installed.

### Manual Installation

1. Install BepInEx for SULFUR.
2. Start the game once so BepInEx creates its folders.
3. Place the mod DLL into:

```text
BepInEx/plugins/
```

4. Start the game.

## Compatibility

This mod should be compatible with most mods that do not heavily rewrite the player inventory, weapon slot, weapon generation, or level transition systems.

Potential conflict areas:

* Mods that change weapon equipment behavior
* Mods that change inventory slot behavior
* Mods that patch enemy death handling
* Mods that modify level transition logic
* Mods that replace weapon attachment or enchantment systems

This mod does not edit original game files.

## Known Notes

* Generated weapons are not meant to be kept permanently.
* Dropping a generated weapon destroys it.
* If a generated weapon is removed during level transition, that is intended.
* Some vanilla oils or scrolls may have unusual effects because the mod uses the game’s own upgrade pool.
* The mod does not directly randomize internal base weapon stats such as raw damage, fire rate, or projectile speed. It randomizes through vanilla weapons and vanilla upgrade systems.

## Uninstallation

Remove the mod DLL from:

```text
BepInEx/plugins/
```

Optionally remove the config file:

```text
BepInEx/config/kumo.sulfur.arms_race.cfg
```

The mod does not require custom save data to function. Removing the mod should not prevent the game from loading.

## Source Code

The source code for this mod is available in my GitHub repository.

It is shared for learning, reference, and transparency.
The repository only contains my original mod source code and packaging text. It does not include SULFUR game files, Unity assemblies, BepInEx binaries, paid assets, or decompiled game source.

If you are another modder, feel free to study how the mod works or use it as a reference for your own implementation.

[ryuka-sulfur-mods](https://github.com/ryuka-dev/ryuka-sulfur-mods)

## Changelog

### 1.0.0

* Initial release.
* Adds random weapon generation on level start.
* Adds random weapon reward on enemy kill.
* Adds configurable kill reward cooldown.
* Adds random oils, scrolls, compatible attachments, rank adjustment, and durability safety.
* Adds runtime cleanup for generated weapons.
* Adds level transition cleanup.
* Adds Amulet teleport cleanup.
* Adds drop prevention for generated weapons.
* Debug key spawning is available but disabled by default.

## SULFUR Config Localization Support

This mod includes localization files for SULFUR Config.

This localization support is only for the in-game configuration page provided by SULFUR Config. It localizes the mod name, config sections, setting names, and setting descriptions shown in the config UI.

It does not change the game’s own text, item names, dialogue, or gameplay content.

Supported SULFUR Config languages:

- English
- Simplified Chinese
- Swedish
- French
- Italian
- German
- Spanish
- Portuguese
- Russian
- Polish
- Japanese
- Korean
- Turkish
- Arabic