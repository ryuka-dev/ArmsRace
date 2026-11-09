# Arms Race

You accidentally brought habits from another world into SULFUR.  
Now you have to Arms Race your way out.

Enter a level and you get a random gun. Kill something and it turns into a different random gun. You don't get to keep any of them.

## How it plays

Each level starts with a random gun in your hands, about two seconds after you arrive. Every kill gives you a new one, at most once per second by default. An enemy only counts if you hurt it yourself and it's worth XP, so something that dies on its own won't swap your gun.

The guns are vanilla weapons with vanilla upgrades rolled on top:

- 1 to 5 random oils
- a 50% chance of scrolls
- a 60% chance of attachments
- enough rank to hold what it rolled
- at least 50% durability

The mod doesn't change the weapons themselves, so a copy of the same gun you find in a normal run stays normal.

## Two guns at once

Set `GeneratedWeaponCount` to 2 and every level starts with a random gun in both weapon slots. Each kill replaces the older of the two and puts the new one in your hands, so you're holding your newest gun with the previous one as a backup.

**Your own guns get moved to your backpack to make room.** With the default of 1, a gun only moves when both slots are taken. With 2, both of them move. On Medium you lose unequipped loot when you die, and that includes a gun the mod moved to your backpack, so stash anything you care about before you head out.

If your backpack has no room for a gun, it stays in its slot and you get one generated weapon fewer.

## Rechambering

This one is off by default. Turn on `EnableRandomRechamber` and generated guns can roll a different caliber, the same thing a Chamber Chisel does in a normal run. `RechamberChance` sets how often it happens (25% by default).

A rechambered gun starts with a full magazine. Reloading it takes ammo of the new caliber. Lasers and arrow weapons can't be rechambered, same as in the base game.

A caliber is skipped if it would more than double the gun's damage per shot, which you can change with `MaxRechamberDamageRatio`. Guns that fire faster than 600 RPM never roll 12 gauge.

## These guns aren't yours

Generated weapons disappear when you change levels, use the Amulet or die. Dropping one destroys it, and vendors won't buy them. The mod keeps nothing in your save.

Generated guns count for achievements, so you might unlock some of them early.

## Settings

The config file shows up after you launch the game once:

```text
BepInEx/config/kumo.sulfur.random_weapon_per_level.cfg
```

If you have SULFUR Config installed, you can change everything in game instead. Its setting names and descriptions are translated into 15 languages.

The ones you'll most likely want:

```ini
[General]
RandomizeOnLevelStart = true
GeneratedWeaponCount = 1
PreferredWeaponSlot = FirstAvailable
ForceSelectGeneratedWeapon = true

[Kill Reward]
EnableRandomWeaponOnKill = true
RandomWeaponOnKillCooldown = 1

[Random Upgrades]
MinOilCount = 1
MaxOilCount = 5
ScrollChance = 0.5
AttachmentChance = 0.6
EnableRandomRechamber = false
RechamberChance = 0.25
MaxRechamberDamageRatio = 2
```

There's also a debug key that gives you a new gun on demand. It's off unless you set `DebugKeyEnabled = true`.

## Installing

Install it with r2modman, Gale or the Thunderstore Mod Manager.

To install by hand, you need BepInEx first. Put `Arms Race.dll` and the `lang` folder in `BepInEx/plugins/ArmsRace/`.

To remove it, delete that folder. You can delete the config file too. Your save will still load.

## Compatibility

Arms Race can clash with mods that change weapon slots, the inventory, enemy death or level transitions. If another mod also hands you weapons at the start of a level, expect them to fight over your slots.

## Source

The code is at [ryuka-dev/ArmsRace](https://github.com/ryuka-dev/ArmsRace). It only contains my own code and packaging text, no game files or BepInEx binaries. If you're making your own mod, feel free to use it as a reference.
