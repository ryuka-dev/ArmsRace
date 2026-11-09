using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.UI;
using PerfectRandom.Sulfur.Core.UI.Inventory;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Weapons;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using UnityEngine.SceneManagement;

namespace RandomWeaponPerLevel
{
    public enum PreferredWeaponSlotMode
    {
        Weapon0,
        Weapon1,
        Random,
        FirstAvailable
    }

    public sealed class RandomWeaponMarker : MonoBehaviour
    {
        public int generationId;
        public int levelSignature;
        public string reason;
        public string weaponName;
    }

    [BepInPlugin("kumo.sulfur.random_weapon_per_level", "Random Weapon Per Level", PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginVersion = "1.0.4";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        private Harmony harmony;

        private ConfigEntry<bool> enableMod;
        private ConfigEntry<bool> randomizeOnLevelStart;
        private ConfigEntry<bool> debugKeyEnabled;
        private ConfigEntry<Key> debugKey;

        private int lastTransitionCleanupFrame = -1;

        private ConfigEntry<PreferredWeaponSlotMode> preferredWeaponSlot;
        private ConfigEntry<bool> forceSelectGeneratedWeapon;
        private ConfigEntry<bool> cleanupOldGeneratedWeapons;
        private ConfigEntry<int> generatedWeaponCount;

        private ConfigEntry<bool> destroyGeneratedWeaponsOnDrop;
        private ConfigEntry<bool> cleanupGeneratedWeaponsBeforeLevelTransition;

        private ConfigEntry<bool> excludeStartsEmpty;
        private ConfigEntry<bool> requireWeaponPrefab;
        private ConfigEntry<bool> requireAmmoMagazine;
        private ConfigEntry<bool> requireUsableByPlayer;

        private ConfigEntry<bool> enableRandomOils;
        private ConfigEntry<int> minOilCount;
        private ConfigEntry<int> maxOilCount;
        private ConfigEntry<bool> respectEnchantmentSlots;
        private ConfigEntry<bool> grantRankForAppliedOils;

        private ConfigEntry<bool> enableRandomScrolls;
        private ConfigEntry<float> scrollChance;
        private ConfigEntry<int> minScrollCount;
        private ConfigEntry<int> maxScrollCount;

        private ConfigEntry<bool> enableRandomWeaponOnKill;
        private ConfigEntry<float> randomWeaponOnKillCooldown;
        private ConfigEntry<bool> killRewardRequiresPlayableLevel;
        private ConfigEntry<bool> killRewardRequireExperienceOnKill;
        private ConfigEntry<bool> logKillRewardChecks;

        private float nextKillRewardAllowedTime;
        private readonly HashSet<int> rewardedKillUnitIds = new HashSet<int>();

        private ConfigEntry<bool> enableRandomAttachments;
        private ConfigEntry<float> attachmentChance;
        private ConfigEntry<int> minAttachmentCount;
        private ConfigEntry<int> maxAttachmentCount;

        private ConfigEntry<bool> enableRandomRechamber;
        private ConfigEntry<float> rechamberChance;
        private ConfigEntry<float> maxRechamberDamageRatio;

        private ConfigEntry<float> minimumDurabilityNormalized;
        private ConfigEntry<bool> fixLowDurability;

        private ConfigEntry<bool> announcePickup;
        private ConfigEntry<bool> logActions;
        private ConfigEntry<bool> logWeaponPool;
        private ConfigEntry<float> autoGiveDelayAfterLevelStart;
        private ConfigEntry<int> postCreateWaitFrames;
        private ConfigEntry<float> sceneSignatureCheckInterval;

        private readonly List<WeaponSO> cachedWeaponPool = new List<WeaponSO>();
        private readonly HashSet<int> autoGivenLevelSignatures = new HashSet<int>();

        private bool sawLoadingOrLevelTransition;
        private bool autoGiveRoutineRunning;
        private bool grantInProgress;
        private bool transitionPending;
        private float transitionPendingExpiry;

        private int currentLevelSignature;
        private int cachedLevelSignature;
        private float nextSignatureCheckTime;
        private int generationCounter;

        // Upper bound on how long a transition blocks new grants if the game never reports loading.
        private const float TransitionPendingTimeoutSeconds = 5f;

        // Keeps shotgun pellets on very fast guns from flooding the game with projectiles.
        private const float MaxRechamberProjectilesPerSecond = 80f;

        private static readonly CaliberTypes[] RechamberCalibers =
        {
            CaliberTypes._9mm,
            CaliberTypes._12ga,
            CaliberTypes._556mm,
            CaliberTypes._762mm,
            CaliberTypes._50BMG
        };

        internal int CleanupGeneratedWeaponsForTransition(string reason)
        {
            if (!ShouldCleanupBeforeLevelTransition())
                return 0;

            // The game keeps running for a moment after this point and saves the inventory later,
            // so a weapon granted in that window would be saved as a normal item.
            transitionPending = true;
            transitionPendingExpiry = Time.unscaledTime + TransitionPendingTimeoutSeconds;

            // 同一帧多个 transition 方法连续触发时，只清一次。
            if (lastTransitionCleanupFrame == Time.frameCount)
                return 0;

            lastTransitionCleanupFrame = Time.frameCount;

            int removed = CleanupOldGeneratedWeapons(null);

            if (removed > 0)
            {
                Log?.LogInfo(
                    "Removed generated weapon(s) before transition: " +
                    removed +
                    " (" +
                    reason +
                    ")"
                );
            }
            else if (logActions != null && logActions.Value)
            {
                Log?.LogInfo(
                    "Transition cleanup checked, no generated weapon found. (" +
                    reason +
                    ")"
                );
            }

            return removed;
        }

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            enableMod = Config.Bind("General", "EnableMod", true, "Enable this mod.");

            randomizeOnLevelStart = Config.Bind(
                "General",
                "RandomizeOnLevelStart",
                true,
                "Give random weapons when entering a non-safe-zone level."
            );

            enableRandomWeaponOnKill = Config.Bind(
                "Kill Reward",
                "EnableRandomWeaponOnKill",
                true,
                "If true, killing an eligible enemy grants one generated random weapon."
            );

            randomWeaponOnKillCooldown = Config.Bind(
                "Kill Reward",
                "RandomWeaponOnKillCooldown",
                1.0f,
                new ConfigDescription(
                    "Cooldown in seconds for random weapon rewards from enemy kills.",
                    new AcceptableValueRange<float>(0f, 30f)
                )
            );

            killRewardRequiresPlayableLevel = Config.Bind(
                "Kill Reward",
                "KillRewardRequiresPlayableLevel",
                true,
                "If true, kill rewards only trigger in playable non-safe-zone levels."
            );

            killRewardRequireExperienceOnKill = Config.Bind(
                "Kill Reward",
                "KillRewardRequireExperienceOnKill",
                true,
                "If true, only units with ExperienceOnKill > 0 can trigger a random weapon reward. This avoids most props and non-enemy objects."
            );

            logKillRewardChecks = Config.Bind(
                "Kill Reward",
                "LogKillRewardChecks",
                false,
                "Log detailed kill reward checks. Keep false for normal gameplay."
            );

            nextKillRewardAllowedTime = 0f;

            announcePickup = Config.Bind(
                "General",
                "AnnouncePickup",
                true,
                "Show pickup notification when a random weapon is generated."
            );

            preferredWeaponSlot = Config.Bind(
                "General",
                "PreferredWeaponSlot",
                PreferredWeaponSlotMode.FirstAvailable,
                "Which weapon slot should receive the generated weapon."
            );

            generatedWeaponCount = Config.Bind(
                "General",
                "GeneratedWeaponCount",
                1,
                new ConfigDescription(
                    "How many generated weapons you carry at once. At 2, entering a level fills both weapon slots and each kill replaces the older generated weapon. Weapons you have equipped move to your backpack.",
                    new AcceptableValueRange<int>(1, 2)
                )
            );

            forceSelectGeneratedWeapon = Config.Bind(
                "General",
                "ForceSelectGeneratedWeapon",
                true,
                "Force-select the generated weapon after it is equipped."
            );

            cleanupOldGeneratedWeapons = Config.Bind(
                "General",
                "CleanupOldGeneratedWeapons",
                true,
                "Remove older runtime-tagged weapons generated by this mod before generating a new one."
            );

            cleanupGeneratedWeaponsBeforeLevelTransition = Config.Bind(
                "Safety",
                "CleanupGeneratedWeaponsBeforeLevelTransition",
                true,
                "Remove generated weapons before level transitions and death handling, so they never reach your save."
            );

            destroyGeneratedWeaponsOnDrop = Config.Bind(
                "Safety",
                "DestroyGeneratedWeaponsOnDrop",
                true,
                "Destroy runtime-tagged generated weapons when the player tries to drop them. This prevents tag loss through ground pickup data."
            );

            autoGiveDelayAfterLevelStart = Config.Bind(
                "Timing",
                "AutoGiveDelayAfterLevelStart",
                2.0f,
                "Delay before giving the level-start random weapon after entering a playable level."
            );

            postCreateWaitFrames = Config.Bind(
                "Timing",
                "PostCreateWaitFrames",
                8,
                new ConfigDescription(
                    "Frames to wait after SpawnItemInSlot before searching for the created InventoryItem.",
                    new AcceptableValueRange<int>(1, 60)
                )
            );

            sceneSignatureCheckInterval = Config.Bind(
                "Timing",
                "SceneSignatureCheckInterval",
                0.5f,
                new ConfigDescription(
                    "How often the mod checks the current level signature.",
                    new AcceptableValueRange<float>(0.1f, 5f)
                )
            );

            debugKeyEnabled = Config.Bind("Debug", "DebugKeyEnabled", false, "Enable debug key spawning.");

            debugKey = Config.Bind(
                "Debug",
                "DebugKey",
                Key.K,
                "Press this key to generate one random debug weapon. This can be used repeatedly."
            );

            logActions = Config.Bind("Debug", "LogActions", true, "Log generated weapon information.");

            logWeaponPool = Config.Bind(
                "Debug",
                "LogWeaponPool",
                false,
                "Log every weapon in the random pool. Keep false unless debugging."
            );

            excludeStartsEmpty = Config.Bind(
                "Random Pool",
                "ExcludeStartsEmpty",
                true,
                "Exclude weapons marked as startsEmpty."
            );

            requireWeaponPrefab = Config.Bind(
                "Random Pool",
                "RequireWeaponPrefab",
                true,
                "Only include weapons with a prefab. Recommended."
            );

            requireAmmoMagazine = Config.Bind(
                "Random Pool",
                "RequireAmmoMagazine",
                true,
                "Only include weapons with iAmmoMax > 0."
            );

            requireUsableByPlayer = Config.Bind(
                "Random Pool",
                "RequireUsableByPlayer",
                true,
                "Only include WeaponSO.usableByPlayer weapons."
            );

            enableRandomOils = Config.Bind(
                "Random Upgrades",
                "EnableRandomOils",
                true,
                "Add random oil enchantments to generated weapons."
            );

            minOilCount = Config.Bind(
                "Random Upgrades",
                "MinOilCount",
                1,
                new ConfigDescription(
                    "Minimum random oil enchantments added to generated weapons.",
                    new AcceptableValueRange<int>(1, 5)
                )
            );

            maxOilCount = Config.Bind(
                "Random Upgrades",
                "MaxOilCount",
                5,
                new ConfigDescription(
                    "Maximum random oil enchantments added to generated weapons.",
                    new AcceptableValueRange<int>(1, 5)
                )
            );

            respectEnchantmentSlots = Config.Bind(
                "Random Upgrades",
                "RespectEnchantmentSlots",
                false,
                "If true, only add oils/scrolls when the weapon has free enchantment slots. If false, this mod can create chaos guns regardless of rank."
            );

            grantRankForAppliedOils = Config.Bind(
                "Random Upgrades",
                "GrantRankForAppliedOils",
                true,
                "If true, generated weapons gain enough XP to match the total applied enchantment count."
            );

            enableRandomScrolls = Config.Bind(
                "Random Upgrades",
                "EnableRandomScrolls",
                true,
                "Add random scroll enchantments to generated weapons."
            );

            scrollChance = Config.Bind(
                "Random Upgrades",
                "ScrollChance",
                0.5f,
                new ConfigDescription(
                    "Chance for a generated weapon to receive scroll enchantments. 0 = never, 1 = always.",
                    new AcceptableValueRange<float>(0f, 1f)
                )
            );

            minScrollCount = Config.Bind(
                "Random Upgrades",
                "MinScrollCount",
                1,
                new ConfigDescription(
                    "Minimum scroll enchantments if scroll generation succeeds.",
                    new AcceptableValueRange<int>(1, 5)
                )
            );

            maxScrollCount = Config.Bind(
                "Random Upgrades",
                "MaxScrollCount",
                2,
                new ConfigDescription(
                    "Maximum scroll enchantments if scroll generation succeeds.",
                    new AcceptableValueRange<int>(1, 5)
                )
            );

            enableRandomAttachments = Config.Bind(
                "Random Upgrades",
                "EnableRandomAttachments",
                true,
                "Add random compatible attachments to generated weapons."
            );

            attachmentChance = Config.Bind(
                "Random Upgrades",
                "AttachmentChance",
                0.6f,
                new ConfigDescription(
                    "Chance for a generated weapon to receive attachments. 0 = never, 1 = always.",
                    new AcceptableValueRange<float>(0f, 1f)
                )
            );

            minAttachmentCount = Config.Bind(
                "Random Upgrades",
                "MinAttachmentCount",
                1,
                new ConfigDescription(
                    "Minimum attachments if attachment generation succeeds.",
                    new AcceptableValueRange<int>(1, 4)
                )
            );

            maxAttachmentCount = Config.Bind(
                "Random Upgrades",
                "MaxAttachmentCount",
                2,
                new ConfigDescription(
                    "Maximum attachments if attachment generation succeeds.",
                    new AcceptableValueRange<int>(1, 4)
                )
            );

            enableRandomRechamber = Config.Bind(
                "Random Upgrades",
                "EnableRandomRechamber",
                false,
                "Let generated weapons roll a different caliber, like using a Chamber Chisel."
            );

            rechamberChance = Config.Bind(
                "Random Upgrades",
                "RechamberChance",
                0.25f,
                new ConfigDescription(
                    "Chance for a generated weapon to be rechambered. 0 = never, 1 = always.",
                    new AcceptableValueRange<float>(0f, 1f)
                )
            );

            maxRechamberDamageRatio = Config.Bind(
                "Random Upgrades",
                "MaxRechamberDamageRatio",
                2f,
                new ConfigDescription(
                    "A caliber is skipped if it would multiply the weapon's damage per shot by more than this.",
                    new AcceptableValueRange<float>(1f, 10f)
                )
            );

            fixLowDurability = Config.Bind(
                "Safety",
                "FixLowDurability",
                true,
                "Raise generated weapon durability to MinimumDurabilityNormalized if it is lower."
            );

            minimumDurabilityNormalized = Config.Bind(
                "Safety",
                "MinimumDurabilityNormalized",
                0.5f,
                new ConfigDescription(
                    "Minimum durability for generated weapons. 0.5 = at least 50%.",
                    new AcceptableValueRange<float>(0f, 1f)
                )
            );

            SceneManager.sceneLoaded += OnSceneLoaded;
            Config.SettingChanged += OnConfigSettingChanged;

            harmony = new Harmony("kumo.sulfur.random_weapon_per_level");
            TryPatchHarmony();

            sawLoadingOrLevelTransition = true;
            currentLevelSignature = 0;
            cachedLevelSignature = 0;
            nextSignatureCheckTime = 0f;
            generationCounter = 0;

            Logger.LogInfo(
                "Random Weapon Per Level " + PluginVersion + " loaded. " +
                "EnableMod=" + enableMod.Value +
                ", RandomizeOnLevelStart=" + randomizeOnLevelStart.Value +
                ", DebugKeyEnabled=" + debugKeyEnabled.Value +
                ", DebugKey=" + debugKey.Value +
                ", PreferredWeaponSlot=" + preferredWeaponSlot.Value +
                ", GeneratedWeaponCount=" + generatedWeaponCount.Value +
                ", EnableRandomRechamber=" + enableRandomRechamber.Value
            );
        }

        private void OnConfigSettingChanged(object sender, SettingChangedEventArgs args)
        {
            if (args.ChangedSetting.Definition.Section == "Random Pool")
                cachedWeaponPool.Clear();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Config.SettingChanged -= OnConfigSettingChanged;

            harmony?.UnpatchSelf();

            if (Instance == this)
                Instance = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            sawLoadingOrLevelTransition = true;
            currentLevelSignature = 0;
            cachedLevelSignature = 0;
            nextSignatureCheckTime = 0f;
            rewardedKillUnitIds.Clear();

            if (logActions != null && logActions.Value)
            {
                Logger.LogInfo("Scene loaded: " + scene.name + ". Auto-give transition armed.");
            }
        }

        private void TryPatchHarmony()
        {
            try
            {
                MethodInfo dropMethod = AccessTools.Method(
                    typeof(InventoryItem),
                    nameof(InventoryItem.DropFromPlayer)
                );

                MethodInfo dropPrefix = AccessTools.Method(
                    typeof(GeneratedWeaponDropFromPlayerHook),
                    nameof(GeneratedWeaponDropFromPlayerHook.Prefix)
                );

                if (dropMethod != null && dropPrefix != null)
                {
                    harmony.Patch(dropMethod, prefix: new HarmonyMethod(dropPrefix));
                    Logger.LogInfo("Patched InventoryItem.DropFromPlayer.");
                }
                else
                {
                    Logger.LogWarning("Could not patch InventoryItem.DropFromPlayer.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to patch InventoryItem.DropFromPlayer: " + ex.Message);
            }

            TryPatchLevelTransitionMethods();
            TryPatchNextLevelTrigger();
            TryPatchAmuletHelper();
            TryPatchDeathConsequences();
            TryPatchSelling();
            TryPatchUnitDie();
        }

        private void TryPatchUnitDie()
        {
            try
            {
                Type unitType = AccessTools.TypeByName(
                    "PerfectRandom.Sulfur.Core.Units.Unit"
                );

                if (unitType == null)
                {
                    Logger.LogWarning("Could not find Unit type for kill reward patch.");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(UnitDieRandomWeaponRewardHook),
                    nameof(UnitDieRandomWeaponRewardHook.Prefix)
                );

                if (prefix == null)
                {
                    Logger.LogWarning("Could not find UnitDieRandomWeaponRewardHook.Prefix.");
                    return;
                }

                int patchedCount = 0;

                foreach (MethodInfo method in unitType.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic))
                {
                    if (method == null)
                        continue;

                    if (method.Name != "Die")
                        continue;

                    try
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                        patchedCount++;

                        Logger.LogInfo(
                            "Patched Unit.Die for random weapon on kill. Params=" +
                            method.GetParameters().Length
                        );
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(
                            "Failed to patch Unit.Die overload: " + ex.Message
                        );
                    }
                }

                if (patchedCount == 0)
                {
                    Logger.LogWarning("No Unit.Die method was patched.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to patch Unit.Die for kill reward: " + ex.Message);
            }
        }

        internal void TryRewardRandomWeaponOnEnemyDeath(object unit)
        {
            if (enableMod == null || !enableMod.Value)
                return;

            if (enableRandomWeaponOnKill == null || !enableRandomWeaponOnKill.Value)
                return;

            if (unit == null)
                return;

            if (killRewardRequiresPlayableLevel.Value && !IsInPlayableLevel())
                return;

            if (Time.unscaledTime < nextKillRewardAllowedTime)
            {
                if (logKillRewardChecks.Value)
                {
                    Logger.LogInfo("Kill reward skipped by cooldown.");
                }

                return;
            }

            if (grantInProgress || transitionPending)
            {
                if (logKillRewardChecks.Value)
                {
                    Logger.LogInfo("Kill reward skipped: a grant or level transition is in progress.");
                }

                return;
            }

            if (!ShouldRewardForDeadUnit(unit))
                return;

            int unitId = GetStableRuntimeUnitId(unit);

            if (unitId == 0)
                return;

            if (!rewardedKillUnitIds.Add(unitId))
            {
                if (logKillRewardChecks.Value)
                {
                    Logger.LogInfo("Kill reward skipped: unit already rewarded. id=" + unitId);
                }

                return;
            }

            nextKillRewardAllowedTime =
                Time.unscaledTime + Mathf.Max(0f, randomWeaponOnKillCooldown.Value);

            if (logActions.Value)
            {
                Logger.LogInfo("Kill reward triggered. Generating random weapon.");
            }

            StartCoroutine(GiveRandomWeaponRoutine("enemy kill reward", currentLevelSignature));
        }

        private bool ShouldRewardForDeadUnit(object unit)
        {
            if (unit == null)
                return false;

            Component component = unit as Component;

            if (component == null)
                return false;

            if (component.gameObject == null)
                return false;

            try
            {
                GameManager gameManager = StaticInstance<GameManager>.Instance;

                if (gameManager != null)
                {
                    if (gameManager.PlayerObject != null &&
                        component.gameObject == gameManager.PlayerObject)
                    {
                        return false;
                    }

                    if (gameManager.PlayerUnit != null &&
                        ReferenceEquals(unit, gameManager.PlayerUnit))
                    {
                        return false;
                    }
                }
            }
            catch
            {
            }

            Unit deadUnit = unit as Unit;

            if (deadUnit != null && !deadUnit.damagedByPlayer)
            {
                if (logKillRewardChecks.Value)
                {
                    Logger.LogInfo("Kill reward skipped: the player never damaged " + component.gameObject.name);
                }

                return false;
            }

            if (killRewardRequireExperienceOnKill.Value)
            {
                float experienceOnKill;

                if (!TryReadFloatMember(unit, "ExperienceOnKill", out experienceOnKill))
                {
                    if (logKillRewardChecks.Value)
                    {
                        Logger.LogInfo(
                            "Kill reward skipped: could not read ExperienceOnKill from " +
                            component.gameObject.name
                        );
                    }

                    return false;
                }

                if (experienceOnKill <= 0f)
                {
                    if (logKillRewardChecks.Value)
                    {
                        Logger.LogInfo(
                            "Kill reward skipped: ExperienceOnKill <= 0 for " +
                            component.gameObject.name
                        );
                    }

                    return false;
                }
            }

            return true;
        }



        private bool TryReadFloatMember(object target, string memberName, out float value)
        {
            value = 0f;

            if (target == null)
                return false;

            Type type = target.GetType();

            try
            {
                PropertyInfo property = type.GetProperty(
                    memberName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

                if (property != null)
                {
                    object raw = property.GetValue(target, null);

                    if (TryConvertToFloat(raw, out value))
                        return true;
                }
            }
            catch
            {
            }

            try
            {
                FieldInfo field = type.GetField(
                    memberName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

                if (field != null)
                {
                    object raw = field.GetValue(target);

                    if (TryConvertToFloat(raw, out value))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private bool TryConvertToFloat(object raw, out float value)
        {
            value = 0f;

            if (raw == null)
                return false;

            try
            {
                if (raw is float)
                {
                    value = (float)raw;
                    return true;
                }

                if (raw is int)
                {
                    value = (int)raw;
                    return true;
                }

                if (raw is double)
                {
                    value = (float)(double)raw;
                    return true;
                }

                value = Convert.ToSingle(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private int GetStableRuntimeUnitId(object unit)
        {
            if (unit == null)
                return 0;

            try
            {
                Component component = unit as Component;

                if (component != null)
                    return component.GetInstanceID();
            }
            catch
            {
            }

            try
            {
                return unit.GetHashCode();
            }
            catch
            {
                return 0;
            }
        }



        private void TryPatchSelling()
        {
            PatchSellMethod("UseSell", nameof(GeneratedWeaponSellHook.UseSellPrefix));
            PatchSellMethod("DropSell", nameof(GeneratedWeaponSellHook.DropSellPrefix));
        }

        private void PatchSellMethod(string targetName, string prefixName)
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(InventoryItem), targetName);

                if (target == null)
                {
                    Logger.LogWarning("Could not find InventoryItem." + targetName + ".");
                    return;
                }

                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GeneratedWeaponSellHook), prefixName))
                );

                Logger.LogInfo("Patched InventoryItem." + targetName + ".");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to patch InventoryItem." + targetName + ": " + ex.Message);
            }
        }

        private void TryPatchDeathConsequences()
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(GameManager), "HandleDeathConsequences");

                if (target == null)
                {
                    Logger.LogWarning("Could not find GameManager.HandleDeathConsequences.");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(DeathConsequencesCleanupHook),
                    nameof(DeathConsequencesCleanupHook.Prefix)
                );

                if (prefix == null)
                {
                    Logger.LogWarning("Could not find DeathConsequencesCleanupHook.Prefix.");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(prefix));

                Logger.LogInfo("Patched GameManager.HandleDeathConsequences.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to patch GameManager.HandleDeathConsequences: " + ex.Message);
            }
        }

        private void TryPatchAmuletHelper()
        {
            try
            {
                Type amuletType = AccessTools.TypeByName(
                    "PerfectRandom.Sulfur.Gameplay.Items.AmuletHelper"
                );

                if (amuletType == null)
                {
                    Logger.LogWarning("Could not find AmuletHelper type.");
                    return;
                }

                MethodInfo doneChanneling = AccessTools.Method(
                    amuletType,
                    "DoneChanneling"
                );

                if (doneChanneling == null)
                {
                    Logger.LogWarning("Could not find AmuletHelper.DoneChanneling.");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(AmuletTeleportCleanupHook),
                    nameof(AmuletTeleportCleanupHook.Prefix)
                );

                if (prefix == null)
                {
                    Logger.LogWarning("Could not find AmuletTeleportCleanupHook.Prefix.");
                    return;
                }

                harmony.Patch(doneChanneling, prefix: new HarmonyMethod(prefix));

                Logger.LogInfo("Patched AmuletHelper.DoneChanneling.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to patch AmuletHelper.DoneChanneling: " + ex.Message);
            }
        }

        private void TryPatchNextLevelTrigger()
        {
            try
            {
                Type triggerType = AccessTools.TypeByName(
                    "PerfectRandom.Sulfur.Core.LevelGeneration.NextLevelTrigger"
                );

                if (triggerType == null)
                {
                    Logger.LogWarning("Could not find NextLevelTrigger type.");
                    return;
                }

                MethodInfo makeTransition = AccessTools.Method(
                    triggerType,
                    "MakeTransition"
                );

                if (makeTransition == null)
                {
                    Logger.LogWarning("Could not find NextLevelTrigger.MakeTransition.");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(NextLevelTriggerCleanupHook),
                    nameof(NextLevelTriggerCleanupHook.Prefix)
                );

                if (prefix == null)
                {
                    Logger.LogWarning("Could not find NextLevelTriggerCleanupHook.Prefix.");
                    return;
                }

                harmony.Patch(makeTransition, prefix: new HarmonyMethod(prefix));

                Logger.LogInfo("Patched NextLevelTrigger.MakeTransition.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to patch NextLevelTrigger.MakeTransition: " + ex.Message);
            }
        }

        private void TryPatchLevelTransitionMethods()
        {
            try
            {
                MethodInfo transitionPrefix = AccessTools.Method(
                    typeof(LevelTransitionCleanupHook),
                    nameof(LevelTransitionCleanupHook.Prefix)
                );

                if (transitionPrefix == null)
                {
                    Logger.LogWarning("Could not find LevelTransitionCleanupHook.Prefix.");
                    return;
                }

                int patchedCount = 0;
                HashSet<MethodBase> patched = new HashSet<MethodBase>();

                foreach (MethodInfo method in typeof(GameManager).GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic))
                {
                    if (method == null)
                        continue;

                    if (!IsLikelyLevelTransitionMethod(method))
                        continue;

                    if (patched.Contains(method))
                        continue;

                    try
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(transitionPrefix));
                        patched.Add(method);
                        patchedCount++;

                        Logger.LogInfo(
                            "Patched level transition method: GameManager." +
                            method.Name +
                            "(" +
                            method.GetParameters().Length +
                            " params)"
                        );
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(
                            "Failed to patch GameManager." +
                            method.Name +
                            ": " +
                            ex.Message
                        );
                    }
                }

                if (patchedCount == 0)
                {
                    Logger.LogWarning(
                        "No likely GameManager level transition methods were patched."
                    );
                }
                else
                {
                    Logger.LogInfo(
                        "Level transition cleanup patched methods: " + patchedCount
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to patch level transition cleanup: " + ex.Message);
            }
        }

        private bool IsLikelyLevelTransitionMethod(MethodInfo method)
        {
            string name = method.Name;

            if (string.IsNullOrEmpty(name))
                return false;

            // 明确已知/高概率切关入口
            if (name == "GoToLevel")
                return true;

            if (name == "GoToChurchHub")
                return true;

            if (name == "GoToCarHub")
                return true;

            if (name == "CompleteLevel")
                return true;

            if (name == "SwitchLevel")
                return true;

            if (name == "GoToNextLevel")
                return true;

            if (name == "GoToPreviousLevel")
                return true;

            if (name == "ReturnToChurch")
                return true;

            if (name == "ReturnToChurchHub")
                return true;

            if (name == "ReturnToHub")
                return true;

            if (name == "LeaveLevel")
                return true;

            if (name == "ExitLevel")
                return true;

            // 兼容不同命名
            if (name.Contains("GoTo") && name.Contains("Level"))
                return true;

            if (name.Contains("Switch") && name.Contains("Level"))
                return true;

            if (name.Contains("Load") && name.Contains("Level"))
                return true;

            if (name.Contains("Next") && name.Contains("Level"))
                return true;

            if (name.Contains("Return") && (name.Contains("Church") || name.Contains("Hub")))
                return true;

            // 不 patch Get/Set/Update 这类，避免每帧误删。
            return false;
        }

        private void Update()
        {
            if (!enableMod.Value)
                return;

            UpdateTransitionPending();
            HandleDebugKey();
            HandleLevelSignatureAutoGive();
        }

        private void UpdateTransitionPending()
        {
            if (!transitionPending)
                return;

            bool loading = false;

            try
            {
                GameManager gameManager = StaticInstance<GameManager>.Instance;

                loading = gameManager != null &&
                          (gameManager.gameState == GameState.Loading ||
                           gameManager.gameState == GameState.Uninitialized);
            }
            catch
            {
            }

            if (loading || Time.unscaledTime >= transitionPendingExpiry)
                transitionPending = false;
        }

        internal bool ShouldCleanupBeforeLevelTransition()
        {
            return cleanupGeneratedWeaponsBeforeLevelTransition != null &&
                   cleanupGeneratedWeaponsBeforeLevelTransition.Value;
        }

        internal bool ShouldDestroyGeneratedWeaponsOnDrop()
        {
            return destroyGeneratedWeaponsOnDrop != null &&
                   destroyGeneratedWeaponsOnDrop.Value;
        }

        internal static bool IsGeneratedWeapon(InventoryItem item)
        {
            if (item == null)
                return false;

            try
            {
                return item.GetComponent<RandomWeaponMarker>() != null;
            }
            catch
            {
                return false;
            }
        }

        internal static void RemoveGeneratedWeaponSafely(InventoryItem item, string reason)
        {
            if (item == null)
                return;

            try
            {
                ReleaseHandsInstanceIfNotHeld(item);
                item.GetRemovedByExternalFactor(reason);
            }
            catch (Exception ex)
            {
                Log?.LogWarning("Failed to remove generated weapon: " + ex.Message);
            }
        }

        // The game only clears the hands instance of the selected slot when a weapon is unequipped.
        private static void ReleaseHandsInstanceIfNotHeld(InventoryItem item)
        {
            GameManager gameManager = StaticInstance<GameManager>.Instance;
            EquipmentManager equipmentManager = gameManager != null ? gameManager.EquipmentManager : null;

            if (equipmentManager == null)
                return;

            PaperdollSlot slot = item.paperdollSlot;

            if (slot != null && equipmentManager.CurrentInventorySlot == slot.exactSlot)
                return;

            equipmentManager.RemoveUsableFromHands(item);
        }

        private void HandleDebugKey()
        {
            if (!debugKeyEnabled.Value)
                return;

            Keyboard keyboard = Keyboard.current;

            if (keyboard == null)
                return;

            Key key = debugKey.Value;

            if (key == Key.None)
                return;

            try
            {
                if (keyboard[key].wasPressedThisFrame)
                {
                    StartCoroutine(GiveRandomWeaponRoutine("debug key " + key, 0));
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to read debug key " + key + ": " + ex.Message);
            }
        }

        private void HandleLevelSignatureAutoGive()
        {
            if (!randomizeOnLevelStart.Value)
                return;

            GameManager gameManager;

            try
            {
                gameManager = StaticInstance<GameManager>.Instance;
            }
            catch
            {
                return;
            }

            if (gameManager == null)
                return;

            if (gameManager.InSafeZone)
            {
                ResetAutoGiveMemoryForSafeZone();
                return;
            }

            if (gameManager.gameState == GameState.Loading ||
                gameManager.gameState == GameState.Uninitialized)
            {
                sawLoadingOrLevelTransition = true;
                currentLevelSignature = 0;
                cachedLevelSignature = 0;
                nextSignatureCheckTime = 0f;
                return;
            }

            if (gameManager.gameState != GameState.Running)
                return;

            if (!IsInPlayableLevel())
                return;

            if (!sawLoadingOrLevelTransition)
                return;

            int signature = GetCachedLevelSignature();

            if (signature == 0)
                return;

            if (currentLevelSignature == 0)
            {
                currentLevelSignature = signature;
            }

            bool signatureChangedAfterTransition =
                sawLoadingOrLevelTransition &&
                currentLevelSignature != signature;

            bool firstPlayableSignature =
                sawLoadingOrLevelTransition &&
                currentLevelSignature == signature;

            if (!signatureChangedAfterTransition && !firstPlayableSignature)
                return;

            currentLevelSignature = signature;
            sawLoadingOrLevelTransition = false;

            TryStartAutoGiveForSignature(signature, "level signature");
        }

        private void ResetAutoGiveMemoryForSafeZone()
        {
            sawLoadingOrLevelTransition = true;
            currentLevelSignature = 0;
            cachedLevelSignature = 0;
            nextSignatureCheckTime = 0f;
            rewardedKillUnitIds.Clear();
            nextKillRewardAllowedTime = 0f;
            autoGivenLevelSignatures.Clear();
        }

        private void TryStartAutoGiveForSignature(int signature, string reason)
        {
            if (signature == 0)
                return;

            if (autoGiveRoutineRunning)
                return;

            if (autoGivenLevelSignatures.Contains(signature))
                return;

            autoGivenLevelSignatures.Add(signature);
            StartCoroutine(AutoGiveForLevelSignature(signature, reason));
        }

        private IEnumerator AutoGiveForLevelSignature(int signature, string reason)
        {
            autoGiveRoutineRunning = true;

            float delay = Mathf.Max(0f, autoGiveDelayAfterLevelStart.Value);

            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            while (grantInProgress)
                yield return null;

            if (!enableMod.Value || !randomizeOnLevelStart.Value)
            {
                autoGivenLevelSignatures.Remove(signature);
                autoGiveRoutineRunning = false;
                yield break;
            }

            if (!IsInPlayableLevel() || transitionPending)
            {
                autoGivenLevelSignatures.Remove(signature);
                sawLoadingOrLevelTransition = true;
                autoGiveRoutineRunning = false;
                yield break;
            }

            int currentSignature = GetCachedLevelSignature();

            if (currentSignature != signature)
            {
                autoGivenLevelSignatures.Remove(signature);
                sawLoadingOrLevelTransition = true;
                autoGiveRoutineRunning = false;
                yield break;
            }

            IEnumerator grant = GiveRandomWeaponRoutine(reason + " " + signature, signature, true);

            try
            {
                while (grant.MoveNext())
                    yield return grant.Current;
            }
            finally
            {
                autoGiveRoutineRunning = false;
            }
        }

        private bool IsInPlayableLevel()
        {
            try
            {
                GameManager gameManager = StaticInstance<GameManager>.Instance;

                if (gameManager == null)
                    return false;

                if (gameManager.gameState != GameState.Running)
                    return false;

                if (gameManager.InSafeZone)
                    return false;

                if (gameManager.PlayerUnit == null)
                    return false;

                if (StaticInstance<UIManager>.Instance == null)
                    return false;

                if (StaticInstance<UIManager>.Instance.PlayerBackpackGrid == null)
                    return false;

                if (StaticInstance<UIManager>.Instance.InventoryUI == null)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private int GetCachedLevelSignature()
        {
            float interval = Mathf.Clamp(sceneSignatureCheckInterval.Value, 0.1f, 5f);

            if (cachedLevelSignature != 0 && Time.unscaledTime < nextSignatureCheckTime)
                return cachedLevelSignature;

            cachedLevelSignature = ComputeLevelSignature();
            nextSignatureCheckTime = Time.unscaledTime + interval;

            return cachedLevelSignature;
        }

        private int ComputeLevelSignature()
        {
            try
            {
                unchecked
                {
                    int hash = 17;

                    GameManager gameManager = StaticInstance<GameManager>.Instance;

                    if (gameManager != null)
                    {
                        hash = hash * 31 + gameManager.InSafeZone.GetHashCode();
                    }

                    hash = hash * 31 + SceneManager.sceneCount;

                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        Scene scene = SceneManager.GetSceneAt(i);

                        if (!scene.IsValid() || !scene.isLoaded)
                            continue;

                        hash = hash * 31 + scene.name.GetHashCode();

                        GameObject[] roots = scene.GetRootGameObjects();

                        hash = hash * 31 + roots.Length;

                        for (int r = 0; r < roots.Length; r++)
                        {
                            GameObject root = roots[r];

                            if (root == null)
                                continue;

                            hash = hash * 31 + root.name.GetHashCode();
                            hash = hash * 31 + root.activeSelf.GetHashCode();
                        }
                    }

                    return hash;
                }
            }
            catch
            {
                return 0;
            }
        }

        private IEnumerator GiveRandomWeaponRoutine(string reason, int levelSignature, bool fillWeaponSlots = false)
        {
            if (grantInProgress || transitionPending)
            {
                if (logActions.Value)
                {
                    Logger.LogInfo(
                        "Skipped random weapon grant by " +
                        reason +
                        ": " +
                        (grantInProgress ? "another grant is running." : "a level transition is in progress.")
                    );
                }

                yield break;
            }

            grantInProgress = true;

            try
            {
                IEnumerator grant = generatedWeaponCount.Value >= 2
                    ? GiveTwoSlotWeaponsRoutine(reason, levelSignature, fillWeaponSlots ? 2 : 1)
                    : GiveSingleWeaponRoutine(reason, levelSignature);

                // Driven by hand so that an exception in the grant still reaches the finally block.
                while (grant.MoveNext())
                    yield return grant.Current;
            }
            finally
            {
                grantInProgress = false;
            }
        }

        private IEnumerator GiveSingleWeaponRoutine(string reason, int levelSignature)
        {
            ItemGrid backpack = GetPlayerBackpackGrid();

            if (backpack == null)
            {
                Logger.LogWarning("Could not give random weapon: PlayerBackpackGrid is null.");
                yield break;
            }

            WeaponSO weapon = PickRandomWeapon();

            if (weapon == null)
            {
                Logger.LogWarning("Could not give random weapon: no valid WeaponSO found.");
                yield break;
            }

            if (cleanupOldGeneratedWeapons.Value)
            {
                int removed = CleanupOldGeneratedWeapons(null);

                if (removed > 0 && logActions.Value)
                {
                    Logger.LogInfo("Removed old generated weapons before generating a new one: " + removed);
                }

                yield return null;
            }

            InventorySlot targetSlot = PrepareTargetWeaponSlot();

            if (targetSlot == InventorySlot.None)
            {
                Logger.LogWarning("Could not give random weapon: no usable Weapon0/Weapon1 slot. Backpack may be full.");
                yield break;
            }

            yield return null;

            if (!IsWeaponSlotEmpty(targetSlot))
            {
                Logger.LogWarning("Could not give random weapon: target slot is still occupied: " + targetSlot);
                yield break;
            }

            if (transitionPending)
                yield break;

            HashSet<InventoryItem> beforeItems = CapturePlayerInventoryItems();

            if (!TrySpawnWeaponInSlot(weapon, targetSlot, backpack))
                yield break;

            InventoryItem createdItem = MarkItemSpawnedInSlot(weapon, targetSlot, reason, levelSignature);
            bool markedAtSpawn = createdItem != null;

            int waitFrames = Mathf.Clamp(postCreateWaitFrames.Value, 1, 60);

            for (int i = 0; i < waitFrames; i++)
                yield return null;

            if (markedAtSpawn && createdItem == null)
                yield break;

            if (!markedAtSpawn)
            {
                createdItem = FindNewInventoryItem(beforeItems, weapon);

                if (createdItem == null)
                {
                    createdItem = GetItemInWeaponSlot(targetSlot);
                }

                if (createdItem != null)
                {
                    MarkGeneratedWeapon(createdItem, reason, levelSignature);
                }
            }

            if (createdItem != null)
            {
                if (forceSelectGeneratedWeapon.Value)
                {
                    TrySelectWeaponSlot(targetSlot);
                }

                ApplyGeneratedWeaponUpgrades(createdItem);

                if (forceSelectGeneratedWeapon.Value)
                {
                    TrySelectWeaponSlot(targetSlot);
                }

                if (cleanupOldGeneratedWeapons.Value)
                {
                    CleanupOldGeneratedWeapons(createdItem);
                }
            }

            LogGeneratedWeapon(reason, targetSlot, weapon, createdItem);
        }

        private IEnumerator GiveTwoSlotWeaponsRoutine(string reason, int levelSignature, int weaponsToGive)
        {
            ItemGrid backpack = GetPlayerBackpackGrid();

            if (backpack == null)
            {
                Logger.LogWarning("Could not give random weapon: PlayerBackpackGrid is null.");
                yield break;
            }

            HashSet<InventoryItem> createdThisGrant = new HashSet<InventoryItem>();
            InventorySlot slotToSelect = InventorySlot.None;

            for (int i = 0; i < weaponsToGive && !transitionPending; i++)
            {
                WeaponSO weapon = PickRandomWeapon();

                if (weapon == null)
                {
                    Logger.LogWarning("Could not give random weapon: no valid WeaponSO found.");
                    break;
                }

                if (cleanupOldGeneratedWeapons.Value)
                {
                    RemoveGeneratedWeaponsOutsideWeaponSlots();
                }

                InventorySlot targetSlot = PrepareNextGeneratedWeaponSlot(createdThisGrant);

                if (targetSlot == InventorySlot.None)
                {
                    if (createdThisGrant.Count > 0)
                    {
                        Logger.LogInfo("Gave fewer generated weapons than requested: no room to move the other equipped weapon.");
                    }
                    else
                    {
                        Logger.LogWarning("Could not give random weapon: no usable Weapon0/Weapon1 slot. Backpack may be full.");
                    }

                    break;
                }

                yield return null;

                if (!IsWeaponSlotEmpty(targetSlot))
                {
                    Logger.LogWarning("Could not give random weapon: target slot is still occupied: " + targetSlot);
                    break;
                }

                if (transitionPending)
                    break;

                if (!TrySpawnWeaponInSlot(weapon, targetSlot, backpack))
                    break;

                InventoryItem createdItem = MarkItemSpawnedInSlot(weapon, targetSlot, reason, levelSignature);

                if (createdItem == null)
                {
                    Logger.LogWarning("Could not find the generated weapon in " + targetSlot + " after spawning it.");
                    break;
                }

                createdThisGrant.Add(createdItem);

                int waitFrames = Mathf.Clamp(postCreateWaitFrames.Value, 1, 60);

                for (int frame = 0; frame < waitFrames; frame++)
                    yield return null;

                if (createdItem == null)
                    break;

                ApplyGeneratedWeaponUpgrades(createdItem);
                LogGeneratedWeapon(reason, targetSlot, weapon, createdItem);

                if (slotToSelect == InventorySlot.None)
                {
                    slotToSelect = targetSlot;
                }
            }

            if (forceSelectGeneratedWeapon.Value && slotToSelect != InventorySlot.None && !transitionPending)
            {
                TrySelectWeaponSlot(slotToSelect);
            }
        }

        private bool TrySpawnWeaponInSlot(WeaponSO weapon, InventorySlot slot, ItemGrid backpack)
        {
            try
            {
                StaticInstance<UIManager>.Instance.InventoryUI.SpawnItemInSlot(weapon, slot, null);

                if (announcePickup.Value && backpack != null)
                {
                    try
                    {
                        backpack.PlayPickupNote(weapon, null);
                    }
                    catch
                    {
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to spawn random weapon " + GetWeaponName(weapon) + " into " + slot + ": " + ex.Message);
                return false;
            }
        }

        // Marks the weapon as soon as it exists. An unmarked weapon looks player-owned to every other
        // code path, and a transition cleanup would leave it in the inventory to be saved.
        private InventoryItem MarkItemSpawnedInSlot(WeaponSO weapon, InventorySlot slot, string reason, int levelSignature)
        {
            InventoryItem item = GetItemInWeaponSlot(slot);

            if (item == null || item.itemDefinition != weapon)
                return null;

            MarkGeneratedWeapon(item, reason, levelSignature);
            return item;
        }

        private void ApplyGeneratedWeaponUpgrades(InventoryItem item)
        {
            ApplyRandomRechamberIfNeeded(item);

            int appliedOilCount = ApplyRandomOilsIfNeeded(item);
            int appliedScrollCount = ApplyRandomScrollsIfNeeded(item);

            GrantMinimumRankForAppliedEnchantments(
                item,
                appliedOilCount + appliedScrollCount
            );

            TrySyncInstancedWeapon(item);

            ApplyRandomAttachmentsIfNeeded(item);

            TrySyncInstancedWeapon(item);

            NormalizeDurabilityIfNeeded(item);
            FixDurabilityIfNeeded(item);

            TrySyncInstancedWeapon(item);
        }

        private void ApplyRandomRechamberIfNeeded(InventoryItem item)
        {
            if (!enableRandomRechamber.Value)
                return;

            WeaponSO weapon = item.itemDefinition as WeaponSO;

            if (weapon == null)
                return;

            if (UnityEngine.Random.value > Mathf.Clamp01(rechamberChance.Value))
                return;

            try
            {
                List<CaliberTypes> candidates = RechamberCalibers
                    .Where(caliber => item.CanRecieveCaliberMod(caliber) && IsRechamberAllowed(weapon, caliber))
                    .ToList();

                if (candidates.Count == 0)
                    return;

                CaliberTypes previous = item.CurrentCaliber;
                CaliberTypes rolled = candidates[UnityEngine.Random.Range(0, candidates.Count)];

                // Not Weapon.OnCaliberMod: it moves the loaded magazine into the player's ammo reserves
                // and forces a reload. Not InventoryItem.ChangeWeaponCaliber: it writes the save.
                item.SetCaliber(rolled);
                item.stats.SetProjectileAmountFromItem(item);
                item.stats.SetSpreadAmountFromItem(item);
                item.stats.SetDamageAmountFromItem(item);

                GameObject instance = GetEquipmentManager()?.GetInstancedInventoryItem(item);
                Weapon instancedWeapon = instance != null ? instance.GetComponent<Weapon>() : null;

                if (instancedWeapon != null)
                {
                    instancedWeapon.SyncCaliberModels();
                    instancedWeapon.GetStoredAmmo();
                }

                if (logActions.Value)
                {
                    Logger.LogInfo(
                        "Rechambered generated weapon " + GetWeaponName(weapon) + ": " + previous + " -> " + rolled
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to rechamber " + GetWeaponName(weapon) + ": " + ex.Message);
            }
        }

        private bool IsRechamberAllowed(WeaponSO weapon, CaliberTypes caliber)
        {
            // A fixed damage value ignores the caliber, so pellets would multiply it past any cap.
            if (weapon.overrideDamage != 0f)
                return false;

            CaliberType current = weapon.caliber.GetAsset();
            CaliberType target = caliber.GetAsset();

            if (current == null || target == null)
                return false;

            float currentDamage = current.baseDamage * current.numberOfProjectiles;
            float targetDamage = target.baseDamage * target.numberOfProjectiles;

            if (currentDamage <= 0f || targetDamage / currentDamage > maxRechamberDamageRatio.Value)
                return false;

            return target.numberOfProjectiles * weapon.rpm / 60f <= MaxRechamberProjectilesPerSecond;
        }

        private void LogGeneratedWeapon(string reason, InventorySlot slot, WeaponSO weapon, InventoryItem createdItem)
        {
            if (!logActions.Value)
                return;

            string itemInfo = createdItem != null
                ? BuildCreatedItemInfo(createdItem)
                : "created item not found after SpawnItemInSlot";

            Logger.LogInfo(
                "Generated random weapon by " +
                reason +
                " into " +
                slot +
                ": " +
                GetWeaponName(weapon) +
                " (" +
                itemInfo +
                ")"
            );
        }

        // Empty slots first, then slots holding the player's own weapon, then the older generated weapon.
        private InventorySlot PrepareNextGeneratedWeaponSlot(HashSet<InventoryItem> createdThisGrant)
        {
            IEnumerable<InventorySlot> slots = GetPreferredWeaponSlotOrder()
                .Where(slot => !createdThisGrant.Contains(GetItemInWeaponSlot(slot)))
                .OrderBy(slot => GetSlotReplacementRank(slot));

            foreach (InventorySlot slot in slots)
            {
                if (TryPrepareWeaponSlot(slot))
                    return slot;
            }

            return InventorySlot.None;
        }

        private int GetSlotReplacementRank(InventorySlot slot)
        {
            InventoryItem item = GetItemInWeaponSlot(slot);

            if (item == null)
                return int.MinValue;

            RandomWeaponMarker marker = item.GetComponent<RandomWeaponMarker>();

            // Generation ids start at 1, so every generated weapon sorts after the player's own.
            return marker != null ? marker.generationId : int.MinValue + 1;
        }

        private void RemoveGeneratedWeaponsOutsideWeaponSlots()
        {
            InventoryItem weapon0 = GetItemInWeaponSlot(InventorySlot.Weapon0);
            InventoryItem weapon1 = GetItemInWeaponSlot(InventorySlot.Weapon1);

            foreach (RandomWeaponMarker marker in Resources.FindObjectsOfTypeAll<RandomWeaponMarker>())
            {
                InventoryItem item = marker != null ? marker.GetComponent<InventoryItem>() : null;

                if (item == null || item == weapon0 || item == weapon1)
                    continue;

                RemoveGeneratedWeaponSafely(item, "Random Weapon Per Level cleanup");
            }
        }

        private InventorySlot PrepareTargetWeaponSlot()
        {
            List<InventorySlot> slots = GetPreferredWeaponSlotOrder();

            foreach (InventorySlot slot in slots)
            {
                if (TryPrepareWeaponSlot(slot))
                    return slot;
            }

            return InventorySlot.None;
        }

        private List<InventorySlot> GetPreferredWeaponSlotOrder()
        {
            List<InventorySlot> result = new List<InventorySlot>();

            PreferredWeaponSlotMode mode = preferredWeaponSlot.Value;

            if (mode == PreferredWeaponSlotMode.Weapon0)
            {
                result.Add(InventorySlot.Weapon0);
                result.Add(InventorySlot.Weapon1);
                return result;
            }

            if (mode == PreferredWeaponSlotMode.Weapon1)
            {
                result.Add(InventorySlot.Weapon1);
                result.Add(InventorySlot.Weapon0);
                return result;
            }

            if (mode == PreferredWeaponSlotMode.Random)
            {
                if (UnityEngine.Random.value < 0.5f)
                {
                    result.Add(InventorySlot.Weapon0);
                    result.Add(InventorySlot.Weapon1);
                }
                else
                {
                    result.Add(InventorySlot.Weapon1);
                    result.Add(InventorySlot.Weapon0);
                }

                return result;
            }

            // FirstAvailable: an empty slot wins, so an equipped weapon only moves when it has to.
            if (!IsWeaponSlotEmpty(InventorySlot.Weapon0) && IsWeaponSlotEmpty(InventorySlot.Weapon1))
            {
                result.Add(InventorySlot.Weapon1);
                result.Add(InventorySlot.Weapon0);
                return result;
            }

            result.Add(InventorySlot.Weapon0);
            result.Add(InventorySlot.Weapon1);
            return result;
        }

        private bool TryPrepareWeaponSlot(InventorySlot slot)
        {
            PaperdollSlot paperdollSlot = GetPaperdollSlot(slot);

            if (paperdollSlot == null)
                return false;

            InventoryItem existing = paperdollSlot.itemInSlot;

            if (existing == null)
                return true;

            if (existing.GetComponent<RandomWeaponMarker>() != null)
            {
                try
                {
                    ReleaseHandsInstanceIfNotHeld(existing);
                    existing.GetRemovedByExternalFactor("Random Weapon Per Level replacing generated weapon");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Failed to remove old generated weapon from " + slot + ": " + ex.Message);
                    return false;
                }
            }

            if (!CanMoveItemToBackpack(existing))
            {
                if (logActions.Value)
                {
                    Logger.LogInfo(
                        "Cannot move existing weapon to backpack, skipping slot " +
                        slot +
                        ": " +
                        existing.itemDefinition.displayName
                    );
                }

                return false;
            }

            try
            {
                existing.TryMoveToPlayerInventory();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to move existing weapon to backpack from " + slot + ": " + ex.Message);
                return false;
            }

            return true;
        }

        private bool IsWeaponSlotEmpty(InventorySlot slot)
        {
            InventoryItem item = GetItemInWeaponSlot(slot);
            return item == null;
        }

        private InventoryItem GetItemInWeaponSlot(InventorySlot slot)
        {
            try
            {
                PaperdollSlot paperdollSlot = GetPaperdollSlot(slot);

                if (paperdollSlot != null)
                    return paperdollSlot.itemInSlot;
            }
            catch
            {
            }

            try
            {
                EquipmentManager equipmentManager = GetEquipmentManager();

                if (equipmentManager != null && equipmentManager.EquippedItems.ContainsKey(slot))
                    return equipmentManager.EquippedItems[slot];
            }
            catch
            {
            }

            return null;
        }

        private PaperdollSlot GetPaperdollSlot(InventorySlot slot)
        {
            try
            {
                UIManager ui = StaticInstance<UIManager>.Instance;

                if (ui == null)
                    return null;

                if (ui.Paperdoll == null)
                    return null;

                return ui.Paperdoll.GetSlot(slot);
            }
            catch
            {
                return null;
            }
        }

        private bool CanMoveItemToBackpack(InventoryItem item)
        {
            if (item == null)
                return false;

            try
            {
                ItemGrid backpack = GetPlayerBackpackGrid();

                if (backpack == null)
                    return false;

                Vector2Int size = item.InventorySize;

                Vector2Int possibleSpace = backpack.GetPossibleSpace(size, false, true);

                if (possibleSpace.x >= 0 && possibleSpace.y >= 0)
                    return true;

                Vector2Int rotatedSize = new Vector2Int(size.y, size.x);
                Vector2Int possibleSpaceRotated = backpack.GetPossibleSpace(rotatedSize, false, true);

                return possibleSpaceRotated.x >= 0 && possibleSpaceRotated.y >= 0;
            }
            catch
            {
                return false;
            }
        }

        private void MarkGeneratedWeapon(InventoryItem item, string reason, int levelSignature)
        {
            if (item == null)
                return;

            RandomWeaponMarker marker = item.GetComponent<RandomWeaponMarker>();

            if (marker == null)
                marker = item.gameObject.AddComponent<RandomWeaponMarker>();

            generationCounter++;

            marker.generationId = generationCounter;
            marker.levelSignature = levelSignature;
            marker.reason = reason;
            marker.weaponName = item.itemDefinition != null ? item.itemDefinition.displayName : "Unknown";
        }

        internal int CleanupOldGeneratedWeapons(InventoryItem keepItem)
        {
            int removed = 0;

            try
            {
                RandomWeaponMarker[] markers = Resources.FindObjectsOfTypeAll<RandomWeaponMarker>();

                foreach (RandomWeaponMarker marker in markers)
                {
                    if (marker == null)
                        continue;

                    InventoryItem item = marker.GetComponent<InventoryItem>();

                    if (item == null)
                        continue;

                    if (keepItem != null && item == keepItem)
                        continue;

                    try
                    {
                        ReleaseHandsInstanceIfNotHeld(item);
                        item.GetRemovedByExternalFactor("Random Weapon Per Level cleanup");
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Failed to remove generated weapon: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to cleanup generated weapons: " + ex.Message);
            }

            return removed;
        }

        private void TrySelectWeaponSlot(InventorySlot slot)
        {
            try
            {
                EquipmentManager equipmentManager = GetEquipmentManager();

                if (equipmentManager == null)
                    return;

                equipmentManager.ChangeWeapon(slot, true);
            }
            catch (Exception ex)
            {
                if (logActions.Value)
                {
                    Logger.LogWarning("Failed to select generated weapon slot " + slot + ": " + ex.Message);
                }
            }
        }

        private ItemGrid GetPlayerBackpackGrid()
        {
            try
            {
                UIManager ui = StaticInstance<UIManager>.Instance;

                if (ui == null)
                    return null;

                return ui.PlayerBackpackGrid;
            }
            catch
            {
                return null;
            }
        }

        private HashSet<InventoryItem> CapturePlayerInventoryItems()
        {
            HashSet<InventoryItem> result = new HashSet<InventoryItem>();

            try
            {
                ItemGrid backpack = GetPlayerBackpackGrid();

                if (backpack != null)
                {
                    foreach (InventoryItem item in backpack.AllItems())
                    {
                        if (item != null)
                            result.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to capture backpack items: " + ex.Message);
            }

            try
            {
                EquipmentManager equipmentManager = GetEquipmentManager();

                if (equipmentManager != null)
                {
                    foreach (InventoryItem item in equipmentManager.EquippedItems.Values)
                    {
                        if (item != null)
                            result.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to capture equipped items: " + ex.Message);
            }

            return result;
        }

        private List<InventoryItem> GetAllPlayerItemsSnapshot()
        {
            HashSet<InventoryItem> set = CapturePlayerInventoryItems();
            return new List<InventoryItem>(set);
        }

        private InventoryItem FindNewInventoryItem(HashSet<InventoryItem> beforeItems, WeaponSO weapon)
        {
            if (weapon == null)
                return null;

            List<InventoryItem> candidates = GetAllPlayerItemsSnapshot();

            foreach (InventoryItem item in candidates)
            {
                if (item == null)
                    continue;

                if (beforeItems != null && beforeItems.Contains(item))
                    continue;

                if (item.itemDefinition == weapon)
                    return item;
            }

            return null;
        }

        private EquipmentManager GetEquipmentManager()
        {
            try
            {
                GameManager gameManager = StaticInstance<GameManager>.Instance;

                if (gameManager == null)
                    return null;

                if (gameManager.EquipmentManager != null)
                    return gameManager.EquipmentManager;

                if (gameManager.PlayerScript != null)
                    return gameManager.PlayerScript.equipmentManager;

                return null;
            }
            catch
            {
                return null;
            }
        }

        private int ApplyRandomOilsIfNeeded(InventoryItem item)
        {
            if (!enableRandomOils.Value)
                return 0;

            if (item == null)
                return 0;

            WeaponSO weapon = item.itemDefinition as WeaponSO;

            if (weapon == null)
                return 0;

            if (!weapon.TypeIsEnchantable)
                return 0;

            int min = Mathf.Clamp(minOilCount.Value, 1, 5);
            int max = Mathf.Clamp(maxOilCount.Value, min, 5);
            int count = PickWeightedOilCount(min, max);

            LootTable oilTable = GetOilLootTable();

            if (oilTable == null || oilTable.entries == null || oilTable.entries.Count == 0)
            {
                Logger.LogWarning("Could not apply random oils: oil loot table is empty.");
                return 0;
            }

            HashSet<ItemId> usedOilItemIds = new HashSet<ItemId>();
            int applied = 0;
            int attempts = 0;
            int maxAttempts = count * 30;

            while (applied < count && attempts < maxAttempts)
            {
                attempts++;

                ItemDefinition oilItem = null;

                try
                {
                    oilItem = oilTable.SelectOneItem();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Failed to select random oil: " + ex.Message);
                    break;
                }

                if (oilItem == null)
                    continue;

                if (!oilItem.appliesEnchantment.IsValid)
                    continue;

                if (usedOilItemIds.Contains(oilItem.id))
                    continue;

                if (IsEnchantmentAlreadyApplied(item, oilItem))
                    continue;

                if (respectEnchantmentSlots.Value && !item.IsEnchantmentCompatible(oilItem))
                    continue;

                try
                {
                    item.AddEnchantment(oilItem, false);
                    usedOilItemIds.Add(oilItem.id);
                    applied++;

                    if (logActions.Value)
                    {
                        Logger.LogInfo(
                            "Applied random oil to " +
                            item.itemDefinition.displayName +
                            ": " +
                            oilItem.displayName
                        );
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        "Failed to apply random oil " +
                        oilItem.displayName +
                        " to " +
                        item.itemDefinition.displayName +
                        ": " +
                        ex.Message
                    );
                }
            }

            return applied;
        }

        private int PickWeightedOilCount(int min, int max)
        {
            min = Mathf.Clamp(min, 1, 5);
            max = Mathf.Clamp(max, min, 5);

            float totalWeight = 0f;

            for (int count = min; count <= max; count++)
            {
                totalWeight += GetOilCountWeight(count);
            }

            if (totalWeight <= 0f)
                return UnityEngine.Random.Range(min, max + 1);

            float roll = UnityEngine.Random.value * totalWeight;
            float current = 0f;

            for (int count = min; count <= max; count++)
            {
                current += GetOilCountWeight(count);

                if (roll <= current)
                    return count;
            }

            return max;
        }

        private float GetOilCountWeight(int count)
        {
            switch (count)
            {
                case 1:
                    return 5.0f;
                case 2:
                    return 4.0f;
                case 3:
                    return 3.5f;
                case 4:
                    return 3.0f;
                case 5:
                    return 2.5f;
                default:
                    return 1.0f;
            }
        }

        private int ApplyRandomScrollsIfNeeded(InventoryItem item)
        {
            if (!enableRandomScrolls.Value)
                return 0;

            if (item == null)
                return 0;

            WeaponSO weapon = item.itemDefinition as WeaponSO;

            if (weapon == null)
                return 0;

            if (!weapon.TypeIsEnchantable)
                return 0;

            float chance = Mathf.Clamp01(scrollChance.Value);

            if (UnityEngine.Random.value > chance)
            {
                if (logActions.Value)
                {
                    Logger.LogInfo("Random scroll roll skipped for " + item.itemDefinition.displayName + ".");
                }

                return 0;
            }

            LootTable scrollTable = GetScrollLootTable();

            if (scrollTable == null || scrollTable.entries == null || scrollTable.entries.Count == 0)
            {
                Logger.LogWarning("Could not apply random scrolls: scroll loot table is empty.");
                return 0;
            }

            int min = Mathf.Clamp(minScrollCount.Value, 1, 5);
            int max = Mathf.Clamp(maxScrollCount.Value, min, 5);
            int targetCount = UnityEngine.Random.Range(min, max + 1);

            HashSet<ItemId> usedScrollItemIds = new HashSet<ItemId>();

            int applied = 0;
            int attempts = 0;
            int maxAttempts = targetCount * 30;

            while (applied < targetCount && attempts < maxAttempts)
            {
                attempts++;

                ItemDefinition scrollItem = null;

                try
                {
                    scrollItem = scrollTable.SelectOneItem();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Failed to select random scroll: " + ex.Message);
                    break;
                }

                if (scrollItem == null)
                    continue;

                if (!scrollItem.appliesEnchantment.IsValid)
                    continue;

                if (usedScrollItemIds.Contains(scrollItem.id))
                    continue;

                if (IsEnchantmentAlreadyApplied(item, scrollItem))
                    continue;

                if (respectEnchantmentSlots.Value && !item.IsEnchantmentCompatible(scrollItem))
                    continue;

                try
                {
                    item.AddEnchantment(scrollItem, false);
                    usedScrollItemIds.Add(scrollItem.id);
                    applied++;

                    if (logActions.Value)
                    {
                        Logger.LogInfo(
                            "Applied random scroll to " +
                            item.itemDefinition.displayName +
                            ": " +
                            scrollItem.displayName
                        );
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        "Failed to apply random scroll " +
                        scrollItem.displayName +
                        " to " +
                        item.itemDefinition.displayName +
                        ": " +
                        ex.Message
                    );
                }
            }

            return applied;
        }

        private bool IsEnchantmentAlreadyApplied(InventoryItem item, ItemDefinition enchantmentItem)
        {
            if (item == null || enchantmentItem == null)
                return false;

            if (!enchantmentItem.appliesEnchantment.IsValid)
                return false;

            try
            {
                EnchantmentDefinition enchantment = enchantmentItem.appliesEnchantment.GetAsset();

                if (enchantment == null)
                    return false;

                return item.enchantments != null && item.enchantments.Contains(enchantment);
            }
            catch
            {
                return false;
            }
        }

        private int ApplyRandomAttachmentsIfNeeded(InventoryItem item)
        {
            if (!enableRandomAttachments.Value)
                return 0;

            if (item == null)
                return 0;

            WeaponSO weapon = item.itemDefinition as WeaponSO;

            if (weapon == null)
                return 0;

            float chance = Mathf.Clamp01(attachmentChance.Value);

            if (UnityEngine.Random.value > chance)
            {
                if (logActions.Value)
                {
                    Logger.LogInfo("Random attachment roll skipped for " + item.itemDefinition.displayName + ".");
                }

                return 0;
            }

            LootTable attachmentTable = GetAttachmentLootTable();

            if (attachmentTable == null || attachmentTable.entries == null || attachmentTable.entries.Count == 0)
            {
                Logger.LogWarning("Could not apply random attachments: attachment loot table is empty.");
                return 0;
            }

            int min = Mathf.Clamp(minAttachmentCount.Value, 1, 4);
            int max = Mathf.Clamp(maxAttachmentCount.Value, min, 4);
            int targetCount = UnityEngine.Random.Range(min, max + 1);

            HashSet<ItemId> usedAttachmentIds = new HashSet<ItemId>();

            int applied = 0;

            while (applied < targetCount)
            {
                List<WeightedAttachmentCandidate> candidates =
                    BuildCompatibleAttachmentCandidates(item, attachmentTable, usedAttachmentIds);

                if (candidates.Count == 0)
                {
                    if (logActions.Value)
                    {
                        Logger.LogInfo(
                            "No more compatible attachments for " +
                            item.itemDefinition.displayName +
                            ". Applied " +
                            applied +
                            "/" +
                            targetCount +
                            "."
                        );
                    }

                    break;
                }

                ItemDefinition attachmentItem = PickWeightedAttachment(candidates);

                if (attachmentItem == null)
                    break;

                try
                {
                    item.AddAttachment(attachmentItem, false);
                    usedAttachmentIds.Add(attachmentItem.id);
                    applied++;

                    if (logActions.Value)
                    {
                        Logger.LogInfo(
                            "Applied random attachment to " +
                            item.itemDefinition.displayName +
                            ": " +
                            attachmentItem.displayName
                        );
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        "Failed to apply random attachment " +
                        attachmentItem.displayName +
                        " to " +
                        item.itemDefinition.displayName +
                        ": " +
                        ex.Message
                    );

                    usedAttachmentIds.Add(attachmentItem.id);
                }
            }

            return applied;
        }

        private sealed class WeightedAttachmentCandidate
        {
            public ItemDefinition Item;
            public float Weight;

            public WeightedAttachmentCandidate(ItemDefinition item, float weight)
            {
                Item = item;
                Weight = weight;
            }
        }

        private List<WeightedAttachmentCandidate> BuildCompatibleAttachmentCandidates(
            InventoryItem item,
            LootTable attachmentTable,
            HashSet<ItemId> usedAttachmentIds
        )
        {
            List<WeightedAttachmentCandidate> candidates = new List<WeightedAttachmentCandidate>();

            if (item == null)
                return candidates;

            if (attachmentTable == null || attachmentTable.entries == null)
                return candidates;

            foreach (var entry in attachmentTable.entries)
            {
                ItemDefinition attachmentItem = entry.lootItem;

                if (attachmentItem == null)
                    continue;

                if (entry.lootWeight <= 0f)
                    continue;

                if (usedAttachmentIds != null && usedAttachmentIds.Contains(attachmentItem.id))
                    continue;

                bool compatible = false;

                try
                {
                    compatible = item.IsAttachmentCompatible(attachmentItem);
                }
                catch (Exception ex)
                {
                    if (logActions.Value)
                    {
                        Logger.LogWarning(
                            "Attachment compatibility check failed for " +
                            attachmentItem.displayName +
                            " on " +
                            item.itemDefinition.displayName +
                            ": " +
                            ex.Message
                        );
                    }

                    compatible = false;
                }

                if (!compatible)
                    continue;

                candidates.Add(new WeightedAttachmentCandidate(attachmentItem, entry.lootWeight));
            }

            return candidates;
        }

        private ItemDefinition PickWeightedAttachment(List<WeightedAttachmentCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            float totalWeight = 0f;

            for (int i = 0; i < candidates.Count; i++)
            {
                totalWeight += Mathf.Max(0f, candidates[i].Weight);
            }

            if (totalWeight <= 0f)
            {
                int fallbackIndex = UnityEngine.Random.Range(0, candidates.Count);
                return candidates[fallbackIndex].Item;
            }

            float roll = UnityEngine.Random.value * totalWeight;
            float current = 0f;

            for (int i = 0; i < candidates.Count; i++)
            {
                current += Mathf.Max(0f, candidates[i].Weight);

                if (roll <= current)
                    return candidates[i].Item;
            }

            return candidates[candidates.Count - 1].Item;
        }

        private void GrantMinimumRankForAppliedEnchantments(InventoryItem item, int appliedEnchantmentCount)
        {
            if (!grantRankForAppliedOils.Value)
                return;

            if (item == null)
                return;

            if (appliedEnchantmentCount <= 0)
                return;

            int targetRank = Mathf.Clamp(appliedEnchantmentCount, 0, 5);

            if (targetRank <= 0)
                return;

            try
            {
                float currentExperience = item.GetExperience();
                float targetExperience = GetMinimumExperienceForRank(targetRank);

                if (currentExperience >= targetExperience)
                    return;

                item.AddExperience(targetExperience - currentExperience);

                if (logActions.Value)
                {
                    Logger.LogInfo(
                        "Raised rank for " +
                        item.itemDefinition.displayName +
                        " to match applied enchantments. Target rank: " +
                        targetRank
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to raise weapon rank for applied enchantments: " + ex.Message);
            }
        }

        private float GetMinimumExperienceForRank(int rank)
        {
            switch (rank)
            {
                case 1:
                    return 50f;
                case 2:
                    return 125f;
                case 3:
                    return 312.5f;
                case 4:
                    return 781.25f;
                case 5:
                    return 1953.125f;
                default:
                    return 0f;
            }
        }

        private LootTable GetOilLootTable()
        {
            try
            {
                GameManager gameManager = StaticInstance<GameManager>.Instance;

                if (gameManager == null || gameManager.Settings == null || gameManager.Settings.LootSettings == null)
                    return null;

                return gameManager.Settings.LootSettings.enchantmentOilLootTable;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to get oil loot table: " + ex.Message);
                return null;
            }
        }

        private LootTable GetScrollLootTable()
        {
            try
            {
                GameManager gameManager = StaticInstance<GameManager>.Instance;

                if (gameManager == null || gameManager.Settings == null || gameManager.Settings.LootSettings == null)
                    return null;

                return gameManager.Settings.LootSettings.enchantmentLootTable;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to get scroll loot table: " + ex.Message);
                return null;
            }
        }

        private LootTable GetAttachmentLootTable()
        {
            try
            {
                GameManager gameManager = StaticInstance<GameManager>.Instance;

                if (gameManager == null || gameManager.Settings == null || gameManager.Settings.LootSettings == null)
                    return null;

                return gameManager.Settings.LootSettings.attachmentLootTable;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to get attachment loot table: " + ex.Message);
                return null;
            }
        }

        private void NormalizeDurabilityIfNeeded(InventoryItem item)
        {
            if (item == null)
                return;

            try
            {
                if (!item.HasDurability)
                    return;

                int max = item.DurabilityMax;
                int current = item.DurabilityCurrent;

                if (current <= max)
                    return;

                item.ModifyDurability(max - current);

                if (logActions.Value)
                {
                    Logger.LogInfo(
                        "Clamped durability for " +
                        item.itemDefinition.displayName +
                        " to max durability: " +
                        max
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to normalize durability: " + ex.Message);
            }
        }

        private void FixDurabilityIfNeeded(InventoryItem item)
        {
            if (!fixLowDurability.Value)
                return;

            if (item == null)
                return;

            try
            {
                if (!item.HasDurability)
                    return;

                float minimum = Mathf.Clamp01(minimumDurabilityNormalized.Value);

                if (item.DurabilityNormalized >= minimum)
                    return;

                int durabilityMax = item.DurabilityMax;
                int targetDurability = Mathf.CeilToInt(durabilityMax * minimum);
                int durabilityChange = targetDurability - item.DurabilityCurrent;

                if (durabilityChange <= 0)
                    return;

                item.ModifyDurability(durabilityChange);

                if (logActions.Value)
                {
                    Logger.LogInfo(
                        "Raised durability for " +
                        item.itemDefinition.displayName +
                        " to at least " +
                        Mathf.RoundToInt(minimum * 100f) +
                        "%"
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to fix generated weapon durability: " + ex.Message);
            }
        }

        private void TrySyncInstancedWeapon(InventoryItem item)
        {
            if (item == null)
                return;

            try
            {
                item.SyncWithInstancedVersion();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to sync generated weapon instance: " + ex.Message);
            }
        }

        private WeaponSO PickRandomWeapon()
        {
            EnsureWeaponPool();

            if (cachedWeaponPool.Count == 0)
                return null;

            int index = UnityEngine.Random.Range(0, cachedWeaponPool.Count);
            return cachedWeaponPool[index];
        }

        private void EnsureWeaponPool()
        {
            if (cachedWeaponPool.Count > 0)
                return;

            RefreshWeaponPool();
        }

        private void RefreshWeaponPool()
        {
            cachedWeaponPool.Clear();

            WeaponSO[] allWeapons;

            try
            {
                allWeapons = Resources.FindObjectsOfTypeAll<WeaponSO>();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to find WeaponSO assets: " + ex.Message);
                return;
            }

            foreach (WeaponSO weapon in allWeapons)
            {
                if (!IsValidWeaponForRandomPool(weapon))
                    continue;

                if (!cachedWeaponPool.Contains(weapon))
                    cachedWeaponPool.Add(weapon);
            }

            cachedWeaponPool.Sort((a, b) =>
                string.Compare(GetWeaponName(a), GetWeaponName(b), StringComparison.OrdinalIgnoreCase)
            );

            Logger.LogInfo("Random weapon pool loaded. Valid weapons: " + cachedWeaponPool.Count);

            if (logWeaponPool.Value)
            {
                foreach (WeaponSO weapon in cachedWeaponPool)
                {
                    Logger.LogInfo("Random pool weapon: " + GetWeaponName(weapon));
                }
            }
        }

        private bool IsValidWeaponForRandomPool(WeaponSO weapon)
        {
            if (weapon == null)
                return false;

            if (requireUsableByPlayer.Value && !weapon.usableByPlayer)
                return false;

            if (weapon.deprecated || !weapon.AllowedInGame)
                return false;

            if (weapon.IsMelee || weapon.IsThrowable)
                return false;

            if (excludeStartsEmpty.Value && weapon.startsEmpty)
                return false;

            if (requireAmmoMagazine.Value && weapon.iAmmoMax <= 0)
                return false;

            if (requireWeaponPrefab.Value && weapon.prefab == null)
                return false;

            if (weapon.slotType != SlotType.Weapon)
                return false;

            if (weapon.caliber == CaliberTypes.None)
                return false;

            bool hasProjectile = weapon.projectileType != ProjectileTypes.None;

            if (!hasProjectile)
                return false;

            if (string.IsNullOrWhiteSpace(weapon.displayName))
                return false;

            return true;
        }

        private string BuildCreatedItemInfo(InventoryItem item)
        {
            if (item == null)
                return "null";

            int enchantmentCount = 0;

            try
            {
                enchantmentCount = item.enchantments != null ? item.enchantments.Count : 0;
            }
            catch
            {
                enchantmentCount = -1;
            }

            int attachmentCount = 0;

            try
            {
                attachmentCount = item.attachments != null ? item.attachments.Count : 0;
            }
            catch
            {
                attachmentCount = -1;
            }

            string rankInfo = "rank=";

            try
            {
                rankInfo += item.GetRankLevel().ToString();
            }
            catch
            {
                rankInfo += "unknown";
            }

            string durabilityInfo = "durability=";

            try
            {
                if (item.HasDurability)
                {
                    durabilityInfo += item.DurabilityCurrent + "/" + item.DurabilityMax;
                }
                else
                {
                    durabilityInfo += "none";
                }
            }
            catch
            {
                durabilityInfo += "unknown";
            }

            string markerInfo = "marker=";

            try
            {
                RandomWeaponMarker marker = item.GetComponent<RandomWeaponMarker>();
                markerInfo += marker != null ? marker.generationId.ToString() : "none";
            }
            catch
            {
                markerInfo += "unknown";
            }

            return
                "created item found, enchantments=" +
                enchantmentCount +
                ", attachments=" +
                attachmentCount +
                ", " +
                rankInfo +
                ", " +
                durabilityInfo +
                ", " +
                markerInfo;
        }

        private static string GetWeaponName(WeaponSO weapon)
        {
            if (weapon == null)
                return "null";

            if (!string.IsNullOrWhiteSpace(weapon.displayName))
                return weapon.displayName;

            return weapon.name;
        }
    }

    internal static class GeneratedWeaponSellHook
    {
        public static bool UseSellPrefix(InventoryItem __instance)
        {
            return !Plugin.IsGeneratedWeapon(__instance);
        }

        public static bool DropSellPrefix(InventoryItem __instance)
        {
            if (!Plugin.IsGeneratedWeapon(__instance))
                return true;

            __instance.DroppedOnInvalid();
            return false;
        }
    }

    internal static class DeathConsequencesCleanupHook
    {
        public static void Prefix()
        {
            try
            {
                if (Plugin.Instance == null)
                    return;

                Plugin.Instance.CleanupGeneratedWeaponsForTransition(
                    "GameManager.HandleDeathConsequences"
                );
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    "Failed to cleanup generated weapons in GameManager.HandleDeathConsequences: " +
                    ex.Message
                );
            }
        }
    }

    internal static class AmuletTeleportCleanupHook
    {
        public static void Prefix()
        {
            try
            {
                if (Plugin.Instance == null)
                    return;

                Plugin.Instance.CleanupGeneratedWeaponsForTransition(
                    "AmuletHelper.DoneChanneling"
                );
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    "Failed to cleanup generated weapons in AmuletHelper.DoneChanneling: " +
                    ex.Message
                );
            }
        }
    }

    internal static class NextLevelTriggerCleanupHook
    {
        public static void Prefix()
        {
            try
            {
                if (Plugin.Instance == null)
                    return;

                Plugin.Instance.CleanupGeneratedWeaponsForTransition(
                    "NextLevelTrigger.MakeTransition"
                );
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    "Failed to cleanup generated weapons in NextLevelTrigger.MakeTransition: " +
                    ex.Message
                );
            }
        }
    }

    internal static class GeneratedWeaponDropFromPlayerHook
    {
        public static bool Prefix(InventoryItem __instance)
        {
            if (Plugin.Instance == null)
                return true;

            if (!Plugin.Instance.ShouldDestroyGeneratedWeaponsOnDrop())
                return true;

            if (!Plugin.IsGeneratedWeapon(__instance))
                return true;

            Plugin.RemoveGeneratedWeaponSafely(
                __instance,
                "Random Weapon Per Level: generated weapon dropped"
            );

            return false;
        }
    }

    internal static class UnitDieRandomWeaponRewardHook
    {
        public static void Prefix(object __instance)
        {
            try
            {
                if (Plugin.Instance == null)
                    return;

                Plugin.Instance.TryRewardRandomWeaponOnEnemyDeath(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    "Failed to process random weapon kill reward: " + ex.Message
                );
            }
        }
    }

    internal static class LevelTransitionCleanupHook
    {
        public static void Prefix(MethodBase __originalMethod)
        {
            try
            {
                if (Plugin.Instance == null)
                    return;

                string methodName = __originalMethod != null
                    ? __originalMethod.DeclaringType.Name + "." + __originalMethod.Name
                    : "unknown transition method";

                Plugin.Instance.CleanupGeneratedWeaponsForTransition(methodName);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    "Failed to cleanup generated weapons before transition: " + ex.Message
                );
            }
        }
    }

}