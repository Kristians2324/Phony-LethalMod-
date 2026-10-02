using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Phoney.Audio;
using Phoney.Vault;
using Phoney.AI;

namespace Phoney.Network;

/// <summary>
/// Handles lightweight clip-trigger synchronization across the lobby so all players
/// hear the same Masked voice line at the same time.
///
/// PROTOCOL (21 bytes per message):
///   ulong enemyNetworkObjectId   — which Masked enemy speaks
///   ulong speakerSteamId         — whose recorded voice to use
///   float clipTimestamp          — timestamp of the clip recorded (for vault lookup)
///   byte  intentFallback         — SemanticIntent cast to byte (fallback if timestamp miss)
/// </summary>
public class PhoneyNetworkManager
{
    private static PhoneyNetworkManager? _instance;
    public static PhoneyNetworkManager Instance => _instance ??= new PhoneyNetworkManager();

    private const string MessageName = "Phoney_VoiceSync_v1";
    private const string ItemGrabMessageName = "Phoney_ItemGrabSync_v1";
    private const string ItemDropMessageName = "Phoney_ItemDropSync_v1";
    private const string ItemValueMessageName = "Phoney_ItemValueSync_v1";
    private const string DemonicSyncMessageName = "Phoney_DemonicSync_v1";
    private bool _registered;

    // ─── Registration ───────────────────────────────────────────────────────

    public void TryRegisterHandlers()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null || _registered) return;

        nm.CustomMessagingManager.RegisterNamedMessageHandler(MessageName, OnReceiveSyncMessage);
        nm.CustomMessagingManager.RegisterNamedMessageHandler(ItemGrabMessageName, OnReceiveItemGrabMessage);
        nm.CustomMessagingManager.RegisterNamedMessageHandler(ItemDropMessageName, OnReceiveItemDropMessage);
        nm.CustomMessagingManager.RegisterNamedMessageHandler(ItemValueMessageName, OnReceiveItemValueMessage);
        nm.CustomMessagingManager.RegisterNamedMessageHandler(DemonicSyncMessageName, OnReceiveDemonicSyncMessage);
        _registered = true;
        PhoneyPlugin.Logger.LogInfo("[Network] Registered Phoney network message handlers (voice + scrap + value + demonic sync).");
    }

    public void Unregister()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.CustomMessagingManager != null && _registered)
        {
            nm.CustomMessagingManager.UnregisterNamedMessageHandler(MessageName);
            nm.CustomMessagingManager.UnregisterNamedMessageHandler(ItemGrabMessageName);
            nm.CustomMessagingManager.UnregisterNamedMessageHandler(ItemDropMessageName);
            nm.CustomMessagingManager.UnregisterNamedMessageHandler(ItemValueMessageName);
            nm.CustomMessagingManager.UnregisterNamedMessageHandler(DemonicSyncMessageName);
        }
        _registered = false;
    }

    // ─── Sending (Host only) ─────────────────────────────────────────────────

    /// <summary>
    /// Called by the host when the AI selects a clip to play on a Masked enemy.
    /// Broadcasts the lightweight trigger to ALL clients AND plays locally on the host.
    /// </summary>
    public void SyncAndPlayClip(MaskedPlayerEnemy enemy, PhoneyVoiceEmitter emitter, RecordedClip clip, float hesitationDelay)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        // ── 1. Play locally on host ──────────────────────────────────────────
        emitter.StartCoroutine(emitter.PlayClipRoutine(clip, hesitationDelay, fromNetwork: false));

        // ── 2. Broadcast to all clients (non-host) ───────────────────────────
        if (!nm.IsServer && !nm.IsHost) return;   // only server broadcasts

        if (enemy.NetworkObject == null) return;

        ulong enemyNetId = enemy.NetworkObject.NetworkObjectId;
        ulong speakerSteamId = clip.PlayerSteamId;
        float clipTimestamp = clip.RecordedTimestamp;
        byte intentByte = (byte)clip.Intent;
        bool isDemonic = emitter.IsDemonicMode;

        const int bufferSize = sizeof(ulong) + sizeof(ulong) + sizeof(float) + sizeof(byte) + sizeof(bool);
        using var writer = new FastBufferWriter(bufferSize, Allocator.Temp);
        writer.WriteValueSafe(enemyNetId);
        writer.WriteValueSafe(speakerSteamId);
        writer.WriteValueSafe(clipTimestamp);
        writer.WriteValueSafe(intentByte);
        writer.WriteValueSafe(isDemonic);

        nm.CustomMessagingManager.SendNamedMessageToAll(MessageName, writer);

        PhoneyPlugin.Logger.LogDebug($"[Network] Broadcasted voice-sync: enemy={enemyNetId} speaker={speakerSteamId} t={clipTimestamp:F2} intent={(SemanticIntent)intentByte} demonic={isDemonic}");
    }

    // ─── Receiving (All clients) ─────────────────────────────────────────────

    private void OnReceiveSyncMessage(ulong senderClientId, FastBufferReader reader)
    {
        const int headerSize = sizeof(ulong) + sizeof(ulong) + sizeof(float) + sizeof(byte);
        if (!reader.TryBeginRead(headerSize)) return;

        reader.ReadValueSafe(out ulong enemyNetId);
        reader.ReadValueSafe(out ulong speakerSteamId);
        reader.ReadValueSafe(out float clipTimestamp);
        reader.ReadValueSafe(out byte intentByte);

        bool isDemonic = false;
        if (reader.TryBeginRead(sizeof(bool)))
        {
            reader.ReadValueSafe(out isDemonic);
        }

        var intent = (SemanticIntent)intentByte;

        PhoneyPlugin.Logger.LogDebug($"[Network] Received voice-sync: enemy={enemyNetId} speaker={speakerSteamId} t={clipTimestamp:F2} intent={intent} demonic={isDemonic}");

        // ── Find the enemy ────────────────────────────────────────────────────
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(enemyNetId, out var netObj) || netObj == null)
        {
            PhoneyPlugin.Logger.LogWarning($"[Network] Could not find NetworkObject {enemyNetId} for voice-sync.");
            return;
        }

        var emitter = netObj.GetComponent<PhoneyVoiceEmitter>();
        if (emitter == null) return;

        if (isDemonic)
        {
            emitter.SetDemonicMode(true, syncToNetwork: false);
        }

        // ── Find the closest matching clip in the local vault ─────────────────
        var clip = ClipVault.Instance.FindByTimestamp(speakerSteamId, clipTimestamp, intent);
        if (clip == null)
        {
            PhoneyPlugin.Logger.LogWarning($"[Network] Could not find clip for speaker {speakerSteamId} near t={clipTimestamp:F2}. Skipping.");
            return;
        }

        // ── Play — fromNetwork=true so this client doesn't re-broadcast ───────
        emitter.StartCoroutine(emitter.PlayClipRoutine(clip, 0f, fromNetwork: true));
    }

    // ─── Demonic Voice Synchronization ───────────────────────────────────────

    public void BroadcastDemonicMode(MaskedPlayerEnemy enemy, bool isDemonic)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null) return;
        if (!nm.IsServer && !nm.IsHost) return;
        if (enemy.NetworkObject == null) return;

        ulong enemyNetId = enemy.NetworkObject.NetworkObjectId;
        const int bufferSize = sizeof(ulong) + sizeof(bool);
        using var writer = new FastBufferWriter(bufferSize, Allocator.Temp);
        writer.WriteValueSafe(enemyNetId);
        writer.WriteValueSafe(isDemonic);

        nm.CustomMessagingManager.SendNamedMessageToAll(DemonicSyncMessageName, writer);
        PhoneyPlugin.Logger.LogDebug($"[Network] Broadcasted demonic-sync: enemy={enemyNetId} demonic={isDemonic}");
    }

    private void OnReceiveDemonicSyncMessage(ulong senderClientId, FastBufferReader reader)
    {
        const int headerSize = sizeof(ulong) + sizeof(bool);
        if (!reader.TryBeginRead(headerSize)) return;

        reader.ReadValueSafe(out ulong enemyNetId);
        reader.ReadValueSafe(out bool isDemonic);

        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(enemyNetId, out var netObj) || netObj == null)
        {
            return;
        }

        var emitter = netObj.GetComponent<PhoneyVoiceEmitter>();
        if (emitter != null)
        {
            emitter.SetDemonicMode(isDemonic, syncToNetwork: false);
            PhoneyPlugin.Logger.LogInfo($"[Network] Received demonic-sync for enemy {enemyNetId}: Demonic={isDemonic}");
        }
    }

    // ─── Item Grab / Drop Synchronization ─────────────────────────────────────

    public void BroadcastItemGrab(ulong enemyNetId, ulong itemNetId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null) return;
        if (!nm.IsServer && !nm.IsHost) return;

        const int bufferSize = sizeof(ulong) + sizeof(ulong);
        using var writer = new FastBufferWriter(bufferSize, Allocator.Temp);
        writer.WriteValueSafe(enemyNetId);
        writer.WriteValueSafe(itemNetId);

        nm.CustomMessagingManager.SendNamedMessageToAll(ItemGrabMessageName, writer);
        PhoneyPlugin.Logger.LogDebug($"[Network] Broadcasted item-grab: enemy={enemyNetId} item={itemNetId}");
    }

    public void BroadcastItemDrop(ulong enemyNetId, ulong itemNetId, Vector3 dropPos)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null) return;
        if (!nm.IsServer && !nm.IsHost) return;

        const int bufferSize = sizeof(ulong) + sizeof(ulong) + sizeof(float) * 3;
        using var writer = new FastBufferWriter(bufferSize, Allocator.Temp);
        writer.WriteValueSafe(enemyNetId);
        writer.WriteValueSafe(itemNetId);
        writer.WriteValueSafe(dropPos.x);
        writer.WriteValueSafe(dropPos.y);
        writer.WriteValueSafe(dropPos.z);

        nm.CustomMessagingManager.SendNamedMessageToAll(ItemDropMessageName, writer);
        PhoneyPlugin.Logger.LogDebug($"[Network] Broadcasted item-drop: enemy={enemyNetId} item={itemNetId} pos={dropPos}");
    }

    private void OnReceiveItemGrabMessage(ulong senderClientId, FastBufferReader reader)
    {
        const int size = sizeof(ulong) + sizeof(ulong);
        if (!reader.TryBeginRead(size)) return;

        reader.ReadValueSafe(out ulong enemyNetId);
        reader.ReadValueSafe(out ulong itemNetId);

        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        if (nm.SpawnManager.SpawnedObjects.TryGetValue(enemyNetId, out var enemyObj) &&
            nm.SpawnManager.SpawnedObjects.TryGetValue(itemNetId, out var itemObj))
        {
            var scrapManager = enemyObj.GetComponent<MaskedScrapManager>();
            var grabbable = itemObj.GetComponent<GrabbableObject>();
            if (scrapManager != null && grabbable != null)
            {
                scrapManager.ExecuteGrabLocally(grabbable);
            }
        }
    }

    private void OnReceiveItemDropMessage(ulong senderClientId, FastBufferReader reader)
    {
        const int size = sizeof(ulong) + sizeof(ulong) + sizeof(float) * 3;
        if (!reader.TryBeginRead(size)) return;

        reader.ReadValueSafe(out ulong enemyNetId);
        reader.ReadValueSafe(out ulong itemNetId);
        reader.ReadValueSafe(out float x);
        reader.ReadValueSafe(out float y);
        reader.ReadValueSafe(out float z);

        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        if (nm.SpawnManager.SpawnedObjects.TryGetValue(enemyNetId, out var enemyObj) &&
            nm.SpawnManager.SpawnedObjects.TryGetValue(itemNetId, out var itemObj))
        {
            var scrapManager = enemyObj.GetComponent<MaskedScrapManager>();
            var grabbable = itemObj.GetComponent<GrabbableObject>();
            if (scrapManager != null && grabbable != null)
            {
                scrapManager.ExecuteDropLocally(grabbable, new Vector3(x, y, z));
            }
        }
    }

    public void BroadcastItemScrapValue(ulong itemNetId, int newValue)
    {
        BroadcastItemTaint(itemNetId, newValue, string.Empty);
    }

    public void BroadcastItemTaint(ulong itemNetId, int newValue, string distortedName)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null) return;
        if (!nm.IsServer && !nm.IsHost) return;

        const int bufferSize = 256;
        using var writer = new FastBufferWriter(bufferSize, Allocator.Temp);
        writer.WriteValueSafe(itemNetId);
        writer.WriteValueSafe(newValue);
        writer.WriteValueSafe(distortedName ?? string.Empty);

        nm.CustomMessagingManager.SendNamedMessageToAll(ItemValueMessageName, writer);
        PhoneyPlugin.Logger.LogDebug($"[Network] Broadcasted item taint update: item={itemNetId} value={newValue} name='{distortedName}'");
    }

    private void OnReceiveItemValueMessage(ulong senderClientId, FastBufferReader reader)
    {
        const int size = sizeof(ulong) + sizeof(int);
        if (!reader.TryBeginRead(size)) return;

        reader.ReadValueSafe(out ulong itemNetId);
        reader.ReadValueSafe(out int newValue);
        reader.ReadValueSafe(out string distortedName);

        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        if (nm.SpawnManager.SpawnedObjects.TryGetValue(itemNetId, out var itemObj) && itemObj != null)
        {
            var grabbable = itemObj.GetComponent<GrabbableObject>();
            if (grabbable != null)
            {
                MaskedScrapManager.ApplyTaintLocally(grabbable, newValue, distortedName);
                PhoneyPlugin.Logger.LogDebug($"[Network] Received item taint update: item='{distortedName}' ({itemNetId}) -> ${newValue}");
            }
        }
    }
}
