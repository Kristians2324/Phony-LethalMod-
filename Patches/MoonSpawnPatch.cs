using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace Phoney.Patches;

[HarmonyPatch(typeof(RoundManager))]
public class MoonSpawnPatch
{
    private static EnemyType? _cachedMaskedEnemyType;

    [HarmonyPatch("LoadNewLevel")]
    [HarmonyPrefix]
    private static void LoadNewLevelPrefix(int randomSeed, SelectableLevel newLevel)
    {
        PhoneyPlugin.Logger.LogInfo("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        PhoneyPlugin.Logger.LogInfo($"[MoonSpawn] LoadNewLevel FIRED — moon: '{newLevel?.PlanetName ?? "NULL"}'");
        PhoneyPlugin.Logger.LogInfo($"[MoonSpawn] EnableCrossMoonSpawning = {PhoneyPlugin.EnableCrossMoonSpawning.Value}");
        PhoneyPlugin.Logger.LogInfo($"[MoonSpawn] MaskedOnlySpawns        = {PhoneyPlugin.MaskedOnlySpawns.Value}");
        PhoneyPlugin.Logger.LogInfo($"[MoonSpawn] MoonSpawnChance         = {PhoneyPlugin.MoonSpawnChance.Value}%");
        PhoneyPlugin.Logger.LogInfo($"[MoonSpawn] MoonSpawnRarity         = {PhoneyPlugin.MoonSpawnRarity.Value}");

        if (newLevel == null)
        {
            PhoneyPlugin.Logger.LogError("[MoonSpawn] newLevel is NULL — aborting.");
            return;
        }

        // ── Step 1: Strip non-Masked enemies from ALL spawn pools (inside, outside, daytime) ──
        if (PhoneyPlugin.MaskedOnlySpawns.Value)
        {
            PhoneyPlugin.Logger.LogInfo("[MoonSpawn] MaskedOnlySpawns = true → clearing all non-Masked enemies from inside, outside, and daytime tables...");
            ClearNonMaskedEnemies(newLevel);
        }
        else
        {
            PhoneyPlugin.Logger.LogInfo("[MoonSpawn] MaskedOnlySpawns = false → leaving other enemies intact.");
        }

        // ── Step 2: Inject/boost Masked into both inside and outside pools ──
        if (PhoneyPlugin.EnableCrossMoonSpawning.Value)
        {
            PhoneyPlugin.Logger.LogInfo("[MoonSpawn] EnableCrossMoonSpawning = true → injecting/boosting Masked into spawn tables...");
            InjectOrBoostMaskedEnemy(newLevel);
        }
        else
        {
            PhoneyPlugin.Logger.LogInfo("[MoonSpawn] EnableCrossMoonSpawning = false → skipping inject/boost.");
        }

        PhoneyPlugin.Logger.LogInfo("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    }

    [HarmonyPatch("SpawnEnemyGameObject")]
    [HarmonyPrefix]
    private static bool SpawnEnemyGameObjectPrefix(
        RoundManager __instance,
        Vector3 spawnPosition,
        float yRot,
        int enemyNumber,
        EnemyType enemyType,
        ref NetworkObjectReference __result)
    {
        if (__instance == null || !__instance.IsServer) return true;

        bool isMasked = false;
        if (enemyType != null && enemyType.enemyName != null &&
            enemyType.enemyName.IndexOf("Masked", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            isMasked = true;
        }
        else if (enemyNumber >= 0 && __instance.currentLevel?.Enemies != null && enemyNumber < __instance.currentLevel.Enemies.Count)
        {
            var target = __instance.currentLevel.Enemies[enemyNumber]?.enemyType;
            if (target?.enemyName != null && target.enemyName.IndexOf("Masked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isMasked = true;
            }
        }
        else if (enemyNumber == -1 && __instance.currentLevel?.Enemies != null && __instance.currentLevel.Enemies.Count > 0)
        {
            if (PhoneyPlugin.MaskedOnlySpawns.Value)
            {
                isMasked = true;
            }
        }
        else if (enemyNumber == -3 && __instance.currentLevel?.OutsideEnemies != null && __instance.currentLevel.OutsideEnemies.Count > 0)
        {
            if (PhoneyPlugin.MaskedOnlySpawns.Value)
            {
                isMasked = true;
            }
        }

        if (isMasked)
        {
            int currentCount = UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>()
                .Count(m => m != null && !m.isEnemyDead);

            if (currentCount >= PhoneyPlugin.MaxMimicCount.Value)
            {
                PhoneyPlugin.Logger.LogInfo(
                    $"[MoonSpawn] Mimic spawn BLOCKED: already at limit ({currentCount}/{PhoneyPlugin.MaxMimicCount.Value}).");
                __result = default;
                return false;
            }
        }

        return true;
    }

    private static void ClearNonMaskedEnemies(SelectableLevel level)
    {
        if (level == null) return;
        ClearList(level.Enemies, "Inside");
        ClearList(level.OutsideEnemies, "Outside");
        ClearList(level.DaytimeEnemies, "Daytime");
    }

    private static void ClearList(List<SpawnableEnemyWithRarity>? list, string label)
    {
        if (list == null) return;
        int before = list.Count;
        list.RemoveAll(e =>
            e?.enemyType == null ||
            e.enemyType.enemyName == null ||
            e.enemyType.enemyName.IndexOf("Masked", StringComparison.OrdinalIgnoreCase) < 0);
        int after = list.Count;
        PhoneyPlugin.Logger.LogInfo($"[MoonSpawn] Cleared {before - after} {label} non-Masked enemies. {after} remain.");
    }

    public static void InjectOrBoostMaskedEnemy(SelectableLevel level)
    {
        if (level == null)
        {
            PhoneyPlugin.Logger.LogError("[MoonSpawn] InjectOrBoostMaskedEnemy: level is NULL!");
            return;
        }

        var maskedType = GetOrFindMaskedEnemyType();
        if (maskedType == null)
        {
            PhoneyPlugin.Logger.LogError("[MoonSpawn] CRITICAL: Could not find Masked EnemyType anywhere! Masked will NOT spawn.");
            return;
        }

        int targetRarity = PhoneyPlugin.MoonSpawnRarity.Value;
        maskedType.MaxCount = PhoneyPlugin.MaxMimicCount.Value;

        // Guarantee inside Masked spawns
        InjectIntoList(level.Enemies, maskedType, targetRarity, level.PlanetName, "Inside");

        // If Masked-only mode is active, ALSO inject Masked into outside spawns so outdoor exploration has mimics
        if (PhoneyPlugin.MaskedOnlySpawns.Value)
        {
            InjectIntoList(level.OutsideEnemies, maskedType, targetRarity, level.PlanetName, "Outside");
        }
    }

    private static void InjectIntoList(List<SpawnableEnemyWithRarity>? list, EnemyType maskedType, int targetRarity, string planet, string label)
    {
        if (list == null) return;

        var existing = list.FirstOrDefault(e =>
            e?.enemyType != null &&
            e.enemyType.enemyName != null &&
            e.enemyType.enemyName.IndexOf("Masked", StringComparison.OrdinalIgnoreCase) >= 0);

        if (existing != null)
        {
            existing.rarity = targetRarity;
            PhoneyPlugin.Logger.LogInfo($"[MoonSpawn] '{planet}' {label}: Masked already present — boosted rarity to {targetRarity}.");
        }
        else
        {
            list.Add(new SpawnableEnemyWithRarity(maskedType, targetRarity));
            PhoneyPlugin.Logger.LogInfo($"[MoonSpawn] '{planet}' {label}: Injected Masked at rarity {targetRarity}.");
        }
    }

    private static EnemyType? GetOrFindMaskedEnemyType()
    {
        if (_cachedMaskedEnemyType != null)
        {
            return _cachedMaskedEnemyType;
        }

        var all = Resources.FindObjectsOfTypeAll<EnemyType>();
        _cachedMaskedEnemyType = all.FirstOrDefault(e =>
            e != null && e.enemyName != null &&
            e.enemyName.IndexOf("Masked", StringComparison.OrdinalIgnoreCase) >= 0);

        if (_cachedMaskedEnemyType != null)
        {
            PhoneyPlugin.Logger.LogInfo($"[MoonSpawn] Found via Resources: '{_cachedMaskedEnemyType.enemyName}'.");
            return _cachedMaskedEnemyType;
        }

        if (StartOfRound.Instance?.levels == null) return null;

        foreach (var lvl in StartOfRound.Instance.levels)
        {
            if (lvl?.Enemies == null) continue;
            foreach (var entry in lvl.Enemies)
            {
                if (entry?.enemyType != null &&
                    entry.enemyType.enemyName != null &&
                    entry.enemyType.enemyName.IndexOf("Masked", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _cachedMaskedEnemyType = entry.enemyType;
                    PhoneyPlugin.Logger.LogInfo($"[MoonSpawn] Found in moon '{lvl.PlanetName}' table: '{_cachedMaskedEnemyType.enemyName}'.");
                    return _cachedMaskedEnemyType;
                }
            }
        }

        return null;
    }
}
