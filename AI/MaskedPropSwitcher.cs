using System.Collections;
using System.Linq;
using UnityEngine;

namespace Phoney.AI;

/// <summary>
/// Handles the periodic swap between holding a flashlight and carrying random scrap loot.
///
/// Behaviour:
///   • Only active during Phase 1 (UndercoverLooting) and Phase 2 (LuringFollower).
///   • Every 45–90 s, the flashlight disappears and the Masked "picks up" a random
///     piece of scrap from the level's item pool, holding it in both hands.
///   • After 20–40 s of carrying the scrap, it drops it and re-lights the flashlight.
///   • On Phase 3 (Ambush) the flashlight is hidden so it doesn't look silly while sprinting.
///   • On Phase 4 (Retreat) the flashlight comes back to sell the "just a scared player" look.
/// </summary>
public class MaskedPropSwitcher : MonoBehaviour
{
    private MaskedPlayerEnemy? _masked;
    private PhoneyDeceptiveAI? _ai;
    private MaskedScrapManager? _scrapManager;

    private GameObject? _flashlightProp;
    private Transform?  _rightHandBone;

    private GameObject? _scrapProp;
    private bool        _holdingScrap;
    private float       _nextSwitchTime;
    private MimicPhase  _lastPhase;

    // ─── Initialization ───────────────────────────────────────────────────────

    public void Initialize(MaskedPlayerEnemy masked, PhoneyDeceptiveAI ai,
                           GameObject? flashlightProp, Transform? rightHandBone)
    {
        _masked        = masked;
        _ai            = ai;
        _scrapManager  = masked.GetComponent<MaskedScrapManager>();
        _flashlightProp = flashlightProp;
        _rightHandBone  = rightHandBone;
        _lastPhase      = ai.CurrentPhase;

        ScheduleNextSwitch();
    }

    // ─── Update ───────────────────────────────────────────────────────────────

    private void Update()
    {
        if (_masked == null || _masked.isEnemyDead || _ai == null) return;
        if (!PhoneyPlugin.EnableHeldItems.Value) return;

        if (_scrapManager == null)
            _scrapManager = _masked.GetComponent<MaskedScrapManager>();

        var phase = _ai.CurrentPhase;

        // ── Phase transition handling ─────────────────────────────────────────
        if (phase != _lastPhase)
        {
            OnPhaseChanged(phase);
            _lastPhase = phase;
        }

        // ── If holding real scrap, turn off flashlight and clear fake prop ────
        if (_scrapManager != null && _scrapManager.HasHeldScrap)
        {
            if (_scrapProp != null) DropScrap(reschedule: false);
            SetFlashlightActive(false);
            return;
        }

        // ── Prop switching only in deceptive phases ───────────────────────────
        if (phase != MimicPhase.UndercoverLooting && phase != MimicPhase.LuringFollower)
            return;

        // If real scrap looting is active, real scrap is handled by MaskedScrapManager
        if (PhoneyPlugin.EnableScrapLooting.Value)
        {
            SetFlashlightActive(true);
            return;
        }

        if (!_holdingScrap && Time.time >= _nextSwitchTime)
            StartCoroutine(PickUpScrapRoutine());
    }

    // ─── Phase change reactions ───────────────────────────────────────────────

    private void OnPhaseChanged(MimicPhase newPhase)
    {
        switch (newPhase)
        {
            case MimicPhase.AmbushStrike:
                // Hide flashlight so sprinting attacker doesn't look silly
                SetFlashlightActive(false);
                if (_holdingScrap) DropScrap(reschedule: false);
                break;

            case MimicPhase.TacticalRetreat:
                // Re-show flashlight — "just a terrified player running away"
                SetFlashlightActive(true);
                break;

            case MimicPhase.UndercoverLooting:
                // Coming back from retreat — restore flashlight if not carrying real scrap
                if (_scrapManager == null || !_scrapManager.HasHeldScrap)
                    SetFlashlightActive(true);
                ScheduleNextSwitch();
                break;
        }
    }

    // ─── Scrap pick-up routine ────────────────────────────────────────────────

    private IEnumerator PickUpScrapRoutine()
    {
        if (_rightHandBone == null) yield break;

        var scrapItem = PickRandomScrap();
        if (scrapItem?.spawnPrefab == null) yield break;

        _holdingScrap = true;

        // Hide flashlight
        SetFlashlightActive(false);

        // Spawn scrap visual
        _scrapProp = MaskedHeldItemManager.MakeVisualProp(scrapItem, isFlashlight: false);
        if (_scrapProp != null)
        {
            Transform holdAnchor = _scrapManager?.HeldItemAnchor ?? _rightHandBone;
            _scrapProp.transform.SetParent(holdAnchor, worldPositionStays: false);
            if (holdAnchor == _scrapManager?.HeldItemAnchor)
            {
                _scrapProp.transform.localPosition = Vector3.zero;
                _scrapProp.transform.localRotation = Quaternion.identity;
            }
            else
            {
                _scrapProp.transform.localPosition = new Vector3(0f, 0.05f, 0.10f);
                _scrapProp.transform.localRotation = Quaternion.Euler(30f, 0f, 0f);
            }
            _scrapProp.transform.localScale = Vector3.one * PhoneyPlugin.HeldItemScrapScale.Value;

            PhoneyPlugin.Logger.LogInfo(
                $"[PropSwitcher] '{_masked?.gameObject.name}' picked up: {scrapItem.itemName}");
        }

        // Carry for 20-40 s
        float holdDuration = UnityEngine.Random.Range(20f, 40f);
        yield return new WaitForSeconds(holdDuration);

        DropScrap(reschedule: true);
    }

    private void DropScrap(bool reschedule)
    {
        if (_scrapProp != null)
        {
            Destroy(_scrapProp);
            _scrapProp = null;
        }

        _holdingScrap = false;

        // Only restore flashlight if we're still in an undercover phase and not holding real scrap
        var phase = _ai?.CurrentPhase;
        if ((phase == MimicPhase.UndercoverLooting || phase == MimicPhase.LuringFollower) &&
            (_scrapManager == null || !_scrapManager.HasHeldScrap))
            SetFlashlightActive(true);

        if (reschedule) ScheduleNextSwitch();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void SetFlashlightActive(bool on)
    {
        if (_flashlightProp != null)
            _flashlightProp.SetActive(on);
        var holder = _masked?.GetComponent<MaskedHeldItemHolder>();
        holder?.SetHeldToolActive(on);
    }


    private void ScheduleNextSwitch()
    {
        _nextSwitchTime = Time.time + UnityEngine.Random.Range(45f, 90f);
    }

    private static Item? PickRandomScrap()
    {
        var list = StartOfRound.Instance?.allItemsList?.itemsList;
        if (list == null) return null;

        // Pick from scrap items that aren't tools (flashlight, walkie, key, etc.)
        var pool = list.Where(i =>
            i != null &&
            i.isScrap &&
            i.spawnPrefab != null &&
            !i.itemName.Contains("flashlight", System.StringComparison.OrdinalIgnoreCase) &&
            !i.itemName.Contains("walkie",     System.StringComparison.OrdinalIgnoreCase) &&
            !i.itemName.Contains("key",        System.StringComparison.OrdinalIgnoreCase) &&
            !i.itemName.Contains("lantern",    System.StringComparison.OrdinalIgnoreCase)
        ).ToList();

        return pool.Count > 0 ? pool[UnityEngine.Random.Range(0, pool.Count)] : null;
    }
}
