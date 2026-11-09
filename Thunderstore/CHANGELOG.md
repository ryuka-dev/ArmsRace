# Changelog

## 1.0.4

- Built for SULFUR 1.0.5.
- New option: `GeneratedWeaponCount`. At 2, each level starts with a random gun in both weapon slots and each kill replaces the older one. Your own guns move to your backpack to make room.
- New option: random rechambering (`EnableRandomRechamber`, off by default). Generated guns can roll a different caliber. Calibers that would more than double the damage per shot are skipped.
- Fixed generated weapons staying in your inventory for good after you died or left a level from the pause menu.
- Fixed generated weapons being sellable to vendors.
- `PreferredWeaponSlot = FirstAvailable` now uses an empty weapon slot when you have one, instead of always moving the gun in the first slot.
- Kills only count if you damaged the enemy yourself.
- `EnableMod = false` now also turns off kill rewards.
- Weapons cut from the game no longer show up in the random pool.
- Random Pool settings now apply without restarting the game.
- Removed the `GunsOnly` setting. It never did anything: the pool has always been guns only.
- Added Traditional Chinese and redid the settings translations in every other language.

## 1.0.3

- Works again on SULFUR 1.0 (tested on 1.0.2). The previous build was compiled against an older game version and hit errors on newer ones: since game version 0.19 when it had to move a weapon you already had equipped into your backpack, and on 1.0.2 when selecting the new weapon. No gameplay changes.
- The SULFUR Config translations now ship in a `lang` folder next to the DLL.
- Updating by hand: the plugin file is still `Arms Race.dll`, so replace the old one instead of keeping two copies, put the `lang` folder next to it, and delete the old loose `.json` translation files.
- Corrected the config file name in the description: it is `kumo.sulfur.random_weapon_per_level.cfg`.

## 1.0.2

- Added SULFUR Config translations for 14 languages. No gameplay changes.

## 1.0.1

- The random weapon pool now skips weapons that have no regular projectile type.

## 1.0.0

- Initial release.
- Adds random weapon generation on level start.
- Adds random weapon reward on enemy kill.
- Adds configurable kill reward cooldown.
- Adds random oils, scrolls, compatible attachments, rank adjustment, and durability safety.
- Adds runtime cleanup for generated weapons.
- Adds level transition cleanup.
- Adds Amulet teleport cleanup.
- Adds drop prevention for generated weapons.
- Debug key spawning is available but disabled by default.
