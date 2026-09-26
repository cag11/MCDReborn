using System.Collections.Generic;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Every enchantment's own numbers: the float, int and bool properties its C++ class (and
    /// the classes between it and Enchantment) declares, with the value its blueprint's default
    /// object holds in the shipped game. A new enchantment's copied blueprint can override each.
    ///
    /// GENERATED from PROBE_ENCHNUMBERS, a live read of the game at the main menu (2026-09-26):
    /// the classes PreloadEnchantments loaded, walked through reflection. Eight enchantments the
    /// game never loads there (Aiding, BlindMelee and six hidden ones) are not in it.
    /// </summary>
    public static class EnchantmentNumbers
    {
        public enum Kind { Float, Int, Bool }

        /// <summary>One number: the property as the class spells it, its type, the game's value, and which class declares it.</summary>
        public sealed record Number(string Property, Kind Kind, double Game, string Declaring);

        /// <summary>The blueprint of each enchantment whose blueprint is not BP_ and its name.</summary>
        public static readonly IReadOnlyDictionary<string, string> BLUEPRINTS = new Dictionary<string, string>
        {
            ["Heavyweight"] = "BP_HeavyweightEnchantment",
        };

        public static string blueprintOf(string enchantment) => BLUEPRINTS.TryGetValue(enchantment, out var bp) ? bp : "BP_" + enchantment;

        public static readonly IReadOnlyDictionary<string, Number[]> NUMBERS = new Dictionary<string, Number[]>
        {
            ["Knockback"] = new[]
            {
                new Number("knockbackPower", Kind.Float, 6, "Knockback"),
            },
            ["Looting"] = new[]
            {
                new Number("DropRateIncreaseBaseChance", Kind.Float, 1, "DropIncreasingEnchantment"),
                new Number("DropRateIncreasePerLevel", Kind.Float, 1, "DropIncreasingEnchantment"),
            },
            ["Prospector"] = new[]
            {
                new Number("DropRateIncreaseBaseChance", Kind.Float, 1, "DropIncreasingEnchantment"),
                new Number("DropRateIncreasePerLevel", Kind.Float, 1, "DropIncreasingEnchantment"),
            },
            ["FireAspect"] = new[]
            {
                new Number("MobDamagePerSecond", Kind.Float, 100, "FireAspect"),
                new Number("damagePerSecond", Kind.Float, 30, "FireAspect"),
                new Number("fireDuration", Kind.Float, 3, "FireAspect"),
            },
            ["Rampaging"] = new[]
            {
                new Number("AttackSpeedBoost", Kind.Float, 1.5, "Rampaging"),
                new Number("BoostTime", Kind.Float, 5, "Rampaging"),
                new Number("TriggerChance", Kind.Float, 0.1, "Rampaging"),
            },
            ["Exploding"] = new[]
            {
                new Number("ExplosionRadius", Kind.Float, 450, "Exploding"),
                new Number("explosionMaxHealthFactorBase", Kind.Float, 0.2, "Exploding"),
                new Number("explosionMaxHealthFactorPerLevel", Kind.Float, 0.2, "Exploding"),
            },
            ["CriticalHit"] = new[]
            {
                new Number("DamageMultiplier", Kind.Float, 3, "CriticalHit"),
            },
            ["Freezing"] = new[]
            {
                new Number("SlowAmountBase", Kind.Float, 0.7, "FreezingEnchantmentBase"),
                new Number("SlowAmountPerLevel", Kind.Float, 0.1, "FreezingEnchantmentBase"),
                new Number("FreezeTime", Kind.Float, 3, "FreezingEnchantmentBase"),
            },
            ["PoisonedMelee"] = new[]
            {
                new Number("MobDamagePerSecond", Kind.Float, 150, "Poisoned"),
                new Number("BaseDamagePerSecond", Kind.Float, 15, "Poisoned"),
                new Number("TriggerChance", Kind.Float, 0.3, "Poisoned"),
                new Number("CloudDuration", Kind.Float, 2.5, "Poisoned"),
                new Number("MobCloudDuration", Kind.Float, 4, "Poisoned"),
            },
            ["JunglePoisonMelee"] = new[]
            {
                new Number("DamagePercentage", Kind.Float, 0.2, "JunglePoisonMelee"),
                new Number("Duration", Kind.Float, 5, "JunglePoisonMelee"),
            },
            ["Leeching"] = new[]
            {
                new Number("HealPerLevelFactor", Kind.Float, 0.02, "Leeching"),
                new Number("BaseHealFactor", Kind.Float, 0.05, "Leeching"),
            },
            ["GravityMelee"] = new[]
            {
                new Number("PullTime", Kind.Float, 0.5, "Gravity"),
                new Number("PullRadius", Kind.Float, 500, "Gravity"),
                new Number("MobPullTime", Kind.Float, 3, "Gravity"),
                new Number("MobRangedPullTime", Kind.Float, 1, "Gravity"),
            },
            ["EnigmaResonatorMelee"] = new[]
            {
                new Number("DamageMultiplier", Kind.Float, 3, "EnigmaResonatorBase"),
            },
            ["AnimaConduitMelee"] = new[]
            {
                new Number("HealingPerSoulFactorPerLevel", Kind.Float, 0.02, "AnimaConduitMelee"),
                new Number("HealingPerSoulFactorBase", Kind.Float, 0.02, "AnimaConduitMelee"),
            },
            ["Stunning"] = new[]
            {
                new Number("ChanceToStunPerLevel", Kind.Float, 0.05, "Stunning"),
                new Number("DurationSeconds", Kind.Float, 2, "Stunning"),
            },
            ["Swirling"] = new[]
            {
                new Number("Range", Kind.Float, 400, "Swirling"),
                new Number("DamageIncreaseFactorPerLevel", Kind.Float, 0.5, "Swirling"),
                new Number("SwirlBaseDamage", Kind.Float, 40, "Swirling"),
                new Number("MobSwirlDamage", Kind.Float, 150, "Swirling"),
                new Number("MinimalHitCountToTrigger", Kind.Int, 1, "Swirling"),
            },
            ["Smiting"] = new[]
            {
                new Number("DamageMultiplierBase", Kind.Float, 1.15, "Smiting"),
                new Number("DamageMultiplierPerLevel", Kind.Float, 0.1, "Smiting"),
            },
            ["Committed"] = new[]
            {
                new Number("MobMaxDamageBonus", Kind.Float, 3, "Committed"),
            },
            ["SoulSiphon"] = new[]
            {
                new Number("BaseSoulSpawnAmount", Kind.Int, 1, "SoulSiphon"),
                new Number("PerLevelSoulSpawnAmount", Kind.Int, 2, "SoulSiphon"),
                new Number("TriggerChance", Kind.Float, 0.2, "SoulSiphon"),
            },
            ["RadianceMelee"] = new[]
            {
                new Number("HealAreaExpandSizePerSecond", Kind.Float, 750, "Radiance"),
                new Number("HealAreaDuration", Kind.Float, 0.5, "Radiance"),
                new Number("HealChance", Kind.Float, 0.2, "Radiance"),
                new Number("BaseHealing", Kind.Float, 30, "Radiance"),
                new Number("MobHealing", Kind.Float, 350, "Radiance"),
            },
            ["Chains"] = new[]
            {
                new Number("BaseMobChainAmount", Kind.Int, 2, "Chains"),
                new Number("ChainDelayTime", Kind.Float, 0.1, "Chains"),
                new Number("BaseChainDuration", Kind.Float, 1, "Chains"),
                new Number("MobChainDuration", Kind.Float, 4, "Chains"),
                new Number("TriggerChance", Kind.Float, 0.3, "Chains"),
                new Number("ChainRange", Kind.Float, 1200, "Chains"),
            },
            ["Thundering"] = new[]
            {
                new Number("StrikeRadius", Kind.Float, 150, "Thundering"),
                new Number("IndividualChainRadius", Kind.Float, 700, "Thundering"),
                new Number("ChainLightningDelay", Kind.Float, 0.1, "Thundering"),
                new Number("BaseDamage", Kind.Float, 12.5, "Thundering"),
                new Number("MobDamage", Kind.Float, 100, "Thundering"),
                new Number("TriggerChance", Kind.Float, 0.3, "Thundering"),
            },
            ["Shockwave"] = new[]
            {
                new Number("BaseDamage", Kind.Float, 30, "Shockwave"),
                new Number("BaseMoveSpeed", Kind.Float, 1850, "Shockwave"),
                new Number("spawnOffset", Kind.Float, 150, "Shockwave"),
                new Number("spawnSafetyMargin", Kind.Float, 15, "Shockwave"),
                new Number("MobDamage", Kind.Float, 125, "Shockwave"),
            },
            ["Weakening"] = new[]
            {
                new Number("WeakenRange", Kind.Float, 300, "Weakening"),
                new Number("MobWeakenAmount", Kind.Float, 0.4, "Weakening"),
                new Number("Duration", Kind.Float, 5, "Weakening"),
                new Number("MobDuration", Kind.Float, 5, "Weakening"),
            },
            ["BusyBee"] = new[]
            {
                new Number("SpawnDelaySeconds", Kind.Float, 0.1, "MobSummonRandomChanceEnchantment"),
                new Number("BaseTriggerChance", Kind.Float, 0.2, "MobSummonRandomChanceEnchantment"),
                new Number("TriggerChanceIncreasePerLevel", Kind.Float, 0.1, "MobSummonRandomChanceEnchantment"),
                new Number("MaxNumMobs", Kind.Int, 3, "MobSummonRandomChanceEnchantment"),
                new Number("DestroySummonsOnDestruction", Kind.Bool, 1, "MobSummonRandomChanceEnchantment"),
            },
            ["DynamoMelee"] = new[]
            {
                new Number("BaseDamage", Kind.Float, 25, "Dynamo"),
                new Number("DamageIncreasePerLevel", Kind.Float, 15, "Dynamo"),
            },
            ["BaneOfIllagers"] = new[]
            {
                new Number("BaneMultiplier", Kind.Float, 1.25, "BaneEnchantment"),
                new Number("BanePercentagePerLevel", Kind.Float, 0.1, "BaneEnchantment"),
            },
            ["Rushdown"] = new[]
            {
                new Number("speedBonus", Kind.Float, 1.1, "Rushdown"),
                new Number("speedDurationAfterKill", Kind.Float, 0.8, "Rushdown"),
            },
            ["Heavyweight"] = new[]
            {
                new Number("Resistance", Kind.Float, 0.6, "EnchantmentHeavyweight"),
                new Number("PerLevelResistance", Kind.Float, 0.1, "EnchantmentHeavyweight"),
            },
            ["DamageSynergy"] = new[]
            {
                new Number("BaseBonusDamagePercentage", Kind.Float, 1.2, "DamageSynergy"),
                new Number("BonusDamagePercentagePerLevel", Kind.Float, 0.2, "DamageSynergy"),
            },
            ["PainCycle"] = new[]
            {
                new Number("MinimumBonusDamageMultiplier", Kind.Int, 3, "PainCycle"),
                new Number("BonusDamageMultiplierPerExtraLevel", Kind.Int, 1, "PainCycle"),
                new Number("DrainMagnitude", Kind.Float, 0.03, "PainCycle"),
            },
            ["GuardingStrike"] = new[]
            {
                new Number("effectBaseDuration", Kind.Float, 2, "GuardingStrike"),
                new Number("effectDurationPerLevel", Kind.Float, 1, "GuardingStrike"),
                new Number("DamageReduction", Kind.Float, 0.5, "GuardingStrike"),
            },
            ["PotionThirstMelee"] = new[]
            {
                new Number("cooldownReductionBase", Kind.Float, 1, "PotionThirstMelee"),
                new Number("cooldownReductionPerLevel", Kind.Float, 1, "PotionThirstMelee"),
            },
            ["WitherEnchantmentMelee"] = new[]
            {
                new Number("WitherDamageTotalFraction", Kind.Float, 0.1, "WitherEnchantment"),
            },
            ["SharedPain"] = new[]
            {
                new Number("MobsRange", Kind.Float, 500, "SharedPain"),
            },
            ["Backstabber"] = new[]
            {
                new Number("DamageMultiplier", Kind.Float, 0.2, "Backstabber"),
            },
            ["ShadowFlash"] = new[]
            {
                new Number("Damage", Kind.Float, 100, "ShadowFlash"),
            },
            ["DamageCounter"] = new[]
            {
                new Number("healthLossTriggerThreshold", Kind.Float, 0.3, "DamageCounter"),
                new Number("EffectDuration", Kind.Float, 3.5, "DamageCounter"),
            },
            ["TempoTheft"] = new[]
            {
                new Number("AmountToStealPerLevel", Kind.Float, 0.1666, "TempoTheft"),
                new Number("MobAmountToSteal", Kind.Float, 0.5, "TempoTheft"),
            },
            ["Ricochet"] = new[]
            {
                new Number("MaxAllowedRecursionCount", Kind.Int, 3, "Ricochet"),
            },
            ["Power"] = new[]
            {
                new Number("MobDamageMultiplier", Kind.Float, 2, "Power"),
            },
            ["MultiShot"] = new[]
            {
                new Number("ExtraArrowsWhenTriggered", Kind.Int, 4, "MultiShot"),
            },
            ["Piercing"] = new[]
            {
                new Number("NumArrowsToShootUntilPiercing", Kind.Int, 3, "Piercing"),
            },
            ["ProjectileCounter"] = new[]
            {
                new Number("TargetCount", Kind.Int, 15, "ProjectileCounter"),
                new Number("EffectDuration", Kind.Float, 3.5, "ProjectileCounter"),
            },
            ["ChainReaction"] = new[]
            {
                new Number("ArrowsToSpawn", Kind.Int, 5, "ChainReaction"),
            },
            ["Gravity"] = new[]
            {
                new Number("PullTime", Kind.Float, 0.5, "Gravity"),
                new Number("PullRadius", Kind.Float, 500, "Gravity"),
                new Number("MobPullTime", Kind.Float, 3, "Gravity"),
                new Number("MobRangedPullTime", Kind.Float, 1, "Gravity"),
            },
            ["EnigmaResonatorRanged"] = new[]
            {
                new Number("DamageMultiplier", Kind.Float, 3, "EnigmaResonatorBase"),
            },
            ["AnimaConduitRanged"] = new[]
            {
                new Number("HealingPerSoulFactorPerLevel", Kind.Float, 0.02, "AnimaConduitMelee"),
                new Number("HealingPerSoulFactorBase", Kind.Float, 0.04, "AnimaConduitMelee"),
            },
            ["PoisonedRanged"] = new[]
            {
                new Number("MobDamagePerSecond", Kind.Float, 150, "Poisoned"),
                new Number("BaseDamagePerSecond", Kind.Float, 15, "Poisoned"),
                new Number("TriggerChance", Kind.Float, 0.3, "Poisoned"),
                new Number("CloudDuration", Kind.Float, 2.5, "Poisoned"),
                new Number("MobCloudDuration", Kind.Float, 4, "Poisoned"),
            },
            ["JunglePoisonRanged"] = new[]
            {
                new Number("DamagePercentage", Kind.Float, 0.1, "JunglePoisonRanged"),
                new Number("Duration", Kind.Float, 10, "JunglePoisonRanged"),
            },
            ["FreezingRanged"] = new[]
            {
                new Number("SlowAmountBase", Kind.Float, 0.8, "FreezingEnchantmentBase"),
                new Number("SlowAmountPerLevel", Kind.Float, 0.1, "FreezingEnchantmentBase"),
                new Number("FreezeTime", Kind.Float, 3, "FreezingEnchantmentBase"),
            },
            ["BonusShot"] = new[]
            {
                new Number("MobDamageFraction", Kind.Float, 0.5, "BonusShot"),
            },
            ["FuseShot"] = new[]
            {
                new Number("BaseDamageExplosionDamageFactor", Kind.Float, 1, "FuseShot"),
                new Number("ExplosionRadius", Kind.Float, 300, "FuseShot"),
                new Number("ExplosionDelaySeconds", Kind.Float, 1, "FuseShot"),
            },
            ["RadianceRanged"] = new[]
            {
                new Number("HealAreaExpandSizePerSecond", Kind.Float, 750, "Radiance"),
                new Number("HealAreaDuration", Kind.Float, 0.5, "Radiance"),
                new Number("HealChance", Kind.Float, 0.5, "Radiance"),
                new Number("BaseHealing", Kind.Float, 30, "Radiance"),
                new Number("MobHealing", Kind.Float, 350, "Radiance"),
            },
            ["Accelerating"] = new[]
            {
                new Number("resetDuration", Kind.Float, 1, "Accelerating"),
                new Number("resetDurationMob", Kind.Float, 5, "Accelerating"),
            },
            ["Growing"] = new[]
            {
                new Number("MaxDistance", Kind.Float, 1600, "Growing"),
                new Number("MobMaxDistance", Kind.Float, 1200, "Growing"),
                new Number("MobDamageFractionBonus", Kind.Float, 3, "Growing"),
            },
            ["WildRage"] = new[]
            {
                new Number("Duration", Kind.Float, 5, "WildRage"),
            },
            ["SlowBowEnchantment"] = new[]
            {
                new Number("mDuration", Kind.Float, 3, "SlowBowEnchantment"),
                new Number("mInitialFreezeAmount", Kind.Float, 0.5, "SlowBowEnchantment"),
                new Number("mFreezePerLevel", Kind.Float, 0.1, "SlowBowEnchantment"),
            },
            ["DynamoRanged"] = new[]
            {
                new Number("BaseDamage", Kind.Float, 25, "Dynamo"),
                new Number("DamageIncreasePerLevel", Kind.Float, 15, "Dynamo"),
            },
            ["BurstBowstring"] = new[]
            {
                new Number("AdditionalAttacksPerLevel", Kind.Int, 1, "BurstBowstring"),
                new Number("AttacksAtLevelOne", Kind.Int, 1, "BurstBowstring"),
                new Number("DamageMultiplier", Kind.Float, 0.4, "BurstBowstring"),
                new Number("TargetGatheringRadius", Kind.Float, 1200, "BurstBowstring"),
                new Number("TimeBetweenAttacks", Kind.Float, 0.05, "BurstBowstring"),
            },
            ["ChargingAcceleration"] = new[]
            {
                new Number("ChargeIntervalSeconds", Kind.Float, 0.5, "ChargingAcceleration"),
                new Number("AccelerationAmount", Kind.Float, 0.2, "ChargingAcceleration"),
            },
            ["CogCrossbowEnchantment"] = new[]
            {
                new Number("ChargeDelay", Kind.Float, 1, "CogCrossbowEnchantment"),
                new Number("bCanAttack", Kind.Bool, 0, "CogCrossbowEnchantment"),
            },
            ["WindBowEnchantment"] = new[]
            {
                new Number("KnockbackRadius", Kind.Float, 400, "WindBowEnchantment"),
                new Number("KnockbackStrength", Kind.Float, 7, "WindBowEnchantment"),
                new Number("KnockbackZFactor", Kind.Float, 0.4, "WindBowEnchantment"),
                new Number("StunTime", Kind.Float, 1.25, "WindBowEnchantment"),
            },
            ["ReliableRicochet"] = new[]
            {
                new Number("MaxAllowedRecursionCount", Kind.Int, 3, "Ricochet"),
            },
            ["PotionThirstRanged"] = new[]
            {
                new Number("cooldownReductionBase", Kind.Float, 1, "PotionThirstRanged"),
                new Number("cooldownReductionPerLevel", Kind.Float, 1, "PotionThirstRanged"),
            },
            ["CooldownShot"] = new[]
            {
                new Number("CooldownDecreasePerLevelSeconds", Kind.Float, 0.5, "CooldownShot"),
            },
            ["WitherEnchantmentRanged"] = new[]
            {
                new Number("WitherDamageTotalFraction", Kind.Float, 0.1, "WitherEnchantment"),
            },
            ["ShockWeb"] = new[]
            {
                new Number("Range", Kind.Float, 2400, "ShockWeb"),
                new Number("damagePerSecond", Kind.Float, 37.5, "ShockWeb"),
                new Number("Period", Kind.Float, 0.25, "ShockWeb"),
                new Number("Duration", Kind.Float, 20, "ShockWeb"),
            },
            ["ShadowShot"] = new[]
            {
                new Number("DurationMagnitude", Kind.Float, 3, "ShadowShot"),
                new Number("MeleePowerBoostAmount", Kind.Float, 8, "ShadowShot"),
                new Number("TriggerChance", Kind.Float, 0.5, "ShadowShot"),
            },
            ["LevitationShot"] = new[]
            {
                new Number("Duration", Kind.Float, 2, "Levitation"),
                new Number("EffectStrength", Kind.Float, -0.03, "Levitation"),
                new Number("LaunchStrength", Kind.Float, 0.3, "Levitation"),
                new Number("FallDamagePercentagePerLevel", Kind.Float, 0.1, "Levitation"),
            },
            ["DippingPoison"] = new[]
            {
                new Number("arrows_given", Kind.Int, 6, "DippingPoison"),
                new Number("arrows_given", Kind.Int, 6, "DippingPoison"),
                new Number("arrows_given", Kind.Int, 6, "DippingPoison"),
            },
            ["FreezingAoe"] = new[]
            {
                new Number("SlowAmountBase", Kind.Float, 0.8, "FreezingEnchantmentBase"),
                new Number("SlowAmountPerLevel", Kind.Float, 0.1, "FreezingEnchantmentBase"),
                new Number("FreezeTime", Kind.Float, 3, "FreezingEnchantmentBase"),
            },
            ["FinalShout"] = new[]
            {
                new Number("triggerCooldownBase", Kind.Float, 12, "FinalShout"),
                new Number("triggerCooldownPerLevel", Kind.Float, 2, "FinalShout"),
                new Number("triggerHealthFractionThreshold", Kind.Float, 0.25, "FinalShout"),
            },
            ["Regeneration"] = new[]
            {
                new Number("MobRegenerationAmountPerSecond", Kind.Float, 30, "Regeneration"),
                new Number("RegenerationAmountPerSecond", Kind.Float, 3, "Regeneration"),
                new Number("MobAmountPerSecond", Kind.Float, 3, "Regeneration"),
                new Number("MobTimeUntilRegeneration", Kind.Float, 0.5, "Regeneration"),
                new Number("TimeUntilRegeneration", Kind.Float, 10, "Regeneration"),
            },
            ["Thorns"] = new[]
            {
                new Number("PercentDamageReturnedBase", Kind.Float, 1, "Thorns"),
                new Number("PercentDamageReturnedPerLevel", Kind.Float, 0.5, "Thorns"),
                new Number("PercentDamageReturnedMob", Kind.Float, 0.1, "Thorns"),
            },
            ["Altruistic"] = new[]
            {
                new Number("PercentageOfDamageToHeal", Kind.Float, 0.25, "Altruistic"),
                new Number("AffectionRadius", Kind.Float, 6000, "Altruistic"),
            },
            ["Shielding"] = new[]
            {
                new Number("ShieldMultiplier", Kind.Float, 0.95, "Shielding"),
                new Number("ShieldRadius", Kind.Float, 1000, "Shielding"),
            },
            ["Barrier"] = new[]
            {
                new Number("AffectionRadius", Kind.Float, 1500, "Barrier"),
            },
            ["Chilling"] = new[]
            {
                new Number("FreezeTime", Kind.Float, 1, "Chilling"),
                new Number("FreezeInterval", Kind.Float, 2, "Chilling"),
                new Number("Radius", Kind.Float, 1000, "Chilling"),
            },
            ["Electrified"] = new[]
            {
                new Number("ZapDelayMob", Kind.Float, 4, "Electrified"),
                new Number("ZapCountMob", Kind.Int, 1, "Electrified"),
                new Number("ZapCount", Kind.Int, 2, "Electrified"),
                new Number("ZapDamage", Kind.Float, 50, "Electrified"),
                new Number("MobZapDamage", Kind.Float, 90, "Electrified"),
                new Number("Radius", Kind.Float, 700, "Electrified"),
            },
            ["Burning"] = new[]
            {
                new Number("Radius", Kind.Float, 300, "Burning"),
                new Number("BurnBaseDamage", Kind.Float, 5, "Burning"),
                new Number("MobBurnDamage", Kind.Float, 45, "Burning"),
                new Number("BurnInterval", Kind.Float, 0.5, "Burning"),
            },
            ["Snowing"] = new[]
            {
                new Number("MobStunDuration", Kind.Float, 2, "Snowing"),
                new Number("PlayerStunDuration", Kind.Float, 1, "Snowing"),
                new Number("Radius", Kind.Float, 1000, "Snowing"),
                new Number("BaseInterval", Kind.Float, 5, "Snowing"),
                new Number("IntervalPerLevel", Kind.Float, -2, "Snowing"),
                new Number("MinInterval", Kind.Float, 1, "Snowing"),
            },
            ["GravityPulse"] = new[]
            {
                new Number("BaseRadius", Kind.Float, 400, "GravityPulse"),
                new Number("PulseInterval", Kind.Float, 3, "GravityPulse"),
            },
            ["FireTrail"] = new[]
            {
                new Number("BaseDamagePerSecond", Kind.Float, 20, "FireTrail"),
                new Number("DurationAfterDodgeEnd", Kind.Float, 0.5, "FireTrail"),
                new Number("FireBlockDuration", Kind.Float, 4, "FireTrail"),
            },
            ["Frenzied"] = new[]
            {
                new Number("TriggerThreshold", Kind.Float, 0.5, "Frenzied"),
            },
            ["Swiftfooted"] = new[]
            {
                new Number("swiftDuration", Kind.Float, 3, "Swiftfooted"),
            },
            ["SpiritSpeed"] = new[]
            {
                new Number("speedBonus", Kind.Float, 0.05, "SpiritSpeed"),
            },
            ["PotionFortification"] = new[]
            {
                new Number("DamageAbsorbationIncrease", Kind.Float, 10, "PotionFortification"),
            },
            ["SpeedSynergy"] = new[]
            {
                new Number("MovementSpeedMultiplier", Kind.Float, 1.2, "SpeedSynergy"),
            },
            ["Explorer"] = new[]
            {
                new Number("BlockThreshold", Kind.Int, 100, "Explorer"),
            },
            ["TumbleBee"] = new[]
            {
                new Number("SpawnDelaySeconds", Kind.Float, 0.1, "MobSummonRandomChanceEnchantment"),
                new Number("BaseTriggerChance", Kind.Float, 0.33, "MobSummonRandomChanceEnchantment"),
                new Number("TriggerChanceIncreasePerLevel", Kind.Float, 0.34, "MobSummonRandomChanceEnchantment"),
                new Number("MaxNumMobs", Kind.Int, 3, "MobSummonRandomChanceEnchantment"),
                new Number("DestroySummonsOnDestruction", Kind.Bool, 1, "MobSummonRandomChanceEnchantment"),
            },
            ["BagOfSouls"] = new[]
            {
                new Number("MaxSoulsPercentageGainedPerLevel", Kind.Float, 0.5, "BagOfSouls"),
            },
            ["Acrobat"] = new[]
            {
                new Number("ReductionPerlevel", Kind.Float, 0.15, "Acrobat"),
            },
            ["WindResistance"] = new[]
            {
                new Number("Resistance", Kind.Float, 0.5, "WindResistance"),
                new Number("PerLevelResistance", Kind.Float, 0.1, "WindResistance"),
            },
            ["RollCharge"] = new[]
            {
                new Number("Duration", Kind.Float, 1, "RollCharge"),
                new Number("DurationPerLevel", Kind.Float, 1, "RollCharge"),
            },
            ["EmeraldDivination"] = new[]
            {
                new Number("BlockThreshold", Kind.Int, 1, "EmeraldDivination"),
                new Number("PercentageToSpawn", Kind.Float, 0.05, "EmeraldDivination"),
                new Number("EmeraldToSpawnBase", Kind.Float, 1, "EmeraldDivination"),
                new Number("EmeraldToSpawnPerLevel", Kind.Float, 2, "EmeraldDivination"),
            },
            ["DeathBarter"] = new[]
            {
                new Number("InvulnerabilityDuration", Kind.Float, 3, "DeathBarter"),
            },
            ["ResurrectionSurge"] = new[]
            {
                new Number("SurgeMultiplier", Kind.Float, 1.11, "ResurrectionSurge"),
                new Number("SurgePerLevel", Kind.Float, 0.11, "ResurrectionSurge"),
            },
            ["Huge"] = new[]
            {
                new Number("Scale", Kind.Float, 3, "Huge"),
            },
            ["ResurrectSurroundingMobs"] = new[]
            {
                new Number("ResurrectRadius", Kind.Float, 5000, "ResurrectSurroundingMobs"),
                new Number("ResurrectChance", Kind.Float, 1, "ResurrectSurroundingMobs"),
                new Number("ResurrectTime", Kind.Float, 3, "ResurrectSurroundingMobs"),
            },
            ["Invisible"] = new[]
            {
                new Number("FadeDurationSeconds", Kind.Float, 2, "Invisible"),
            },
            ["PoisonFocus"] = new[]
            {
                new Number("DamageIncreasePerLevel", Kind.Float, 0.25, "ElementalDamageIncreaseEnchant"),
            },
            ["FireFocus"] = new[]
            {
                new Number("DamageIncreasePerLevel", Kind.Float, 0.25, "ElementalDamageIncreaseEnchant"),
            },
            ["SoulFocus"] = new[]
            {
                new Number("DamageIncreasePerLevel", Kind.Float, 0.1, "ElementalDamageIncreaseEnchant"),
            },
            ["LightningFocus"] = new[]
            {
                new Number("DamageIncreasePerLevel", Kind.Float, 0.25, "ElementalDamageIncreaseEnchant"),
            },
            ["Flee"] = new[]
            {
                new Number("resetDuration", Kind.Float, 1, "Flee"),
                new Number("percentPerLevel", Kind.Float, 0.3, "Flee"),
            },
            ["BeastSurge"] = new[]
            {
                new Number("BaseModifier", Kind.Float, 1.5, "BeastSurge"),
                new Number("Level2Modifier", Kind.Float, 2, "BeastSurge"),
                new Number("Level3Modifier", Kind.Float, 2.5, "BeastSurge"),
                new Number("PerLevelModifier", Kind.Float, 0.3, "BeastSurge"),
                new Number("EffectDuration", Kind.Float, 10, "BeastSurge"),
            },
            ["BeastBurst"] = new[]
            {
                new Number("ExplosionRadius", Kind.Float, 840, "BeastBurst"),
                new Number("BaseDamageValue", Kind.Float, 250, "BeastBurst"),
                new Number("ExtraDamagePerLevel", Kind.Float, 250, "BeastBurst"),
            },
            ["BeastBoss"] = new[]
            {
                new Number("BaseDamageBoost", Kind.Float, 0.2, "BeastBoss"),
                new Number("PerLevelDamageBoost", Kind.Float, 0.2, "BeastBoss"),
            },
            ["Reckless"] = new[]
            {
                new Number("DamageIncreaseAtLevelOne", Kind.Float, 0.5, "Reckless"),
                new Number("DamageIncreasePerLevel", Kind.Float, 0.2, "Reckless"),
                new Number("HealthDecrease", Kind.Float, 0.4, "Reckless"),
            },
            ["ThriveUnderPressure"] = new[]
            {
                new Number("TriggerRange", Kind.Float, 600, "ThriveUnderPressure"),
                new Number("MobThreshold", Kind.Int, 4, "ThriveUnderPressure"),
            },
            ["VoidBlast"] = new[]
            {
                new Number("blastRange", Kind.Float, 500, "VoidBlast"),
                new Number("blastDamage", Kind.Float, 40, "VoidBlast"),
                new Number("slowDuration", Kind.Float, 2, "VoidBlast"),
                new Number("slowMultiplier", Kind.Float, 0.2, "VoidBlast"),
            },
            ["ShulkerSentry"] = new[]
            {
                new Number("MobStunDuration", Kind.Float, 2, "ShulkerSentry"),
                new Number("PlayerStunDuration", Kind.Float, 1, "ShulkerSentry"),
                new Number("Radius", Kind.Float, 2200, "ShulkerSentry"),
                new Number("BaseInterval", Kind.Float, 5, "ShulkerSentry"),
                new Number("IntervalPerLevel", Kind.Float, -2, "ShulkerSentry"),
                new Number("MinInterval", Kind.Float, 1, "ShulkerSentry"),
            },
            ["LuckOfTheSea"] = new[]
            {
                new Number("luck_level", Kind.Float, 0.1, "LuckOfTheSea"),
                new Number("luck_level", Kind.Float, 0.2, "LuckOfTheSea"),
                new Number("luck_level", Kind.Float, 0.3, "LuckOfTheSea"),
            },
            ["PassiveRegen"] = new[]
            {
                new Number("HealthRegenerationAmountPerSecond", Kind.Float, 10, "PassiveRegen"),
                new Number("TimeUntilHealthRegeneration", Kind.Float, 10, "PassiveRegen"),
                new Number("TimeUntilFirstBarrierRegeneration", Kind.Float, 6, "PassiveRegen"),
                new Number("TimeUntilDefaultBarrierRegeneration", Kind.Float, 6, "PassiveRegen"),
            },
        };
    }
}
