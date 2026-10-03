using System;
using UnityEngine;

namespace Phoney.AI;

/// <summary>
/// Component attached to a MaskedPlayerEnemy to manage its visual-only cosmetic props
/// (walkie-talkie on chest, held weapon/tool/flashlight in hand) and authentic animation layers.
///
/// Features:
///   • Visual-only props: No GrabbableObject, no physics, no ScanNodes, no loot drop on death.
///   • Zero weapon duplication: DestroyAllProps() completely destroys visual props on KillEnemy.
///   • Authentic animation layers: Sets 'HoldingItemsBothHands' or 'HoldingItemsRightHand' layer weights
///     and 'cancelHolding' to match real player animations, eliminating zombie arms entirely in friendly mode.
///   • Seamless inventory swapping: Hides visual props while the mimic carries real scrap.
/// </summary>
public class MaskedHeldItemHolder : MonoBehaviour
{
    public MaskedPlayerEnemy? Masked { get; private set; }
    public Transform? RightHandBone { get; private set; }

    public GameObject? WalkieProp { get; set; }
    public GameObject? HeldToolProp { get; set; }
    public Item? HeldToolDef { get; set; }

    public bool IsTwoHanded { get; set; }
    public bool IsFlashlight { get; set; }
    public bool IsProFlashlight { get; set; }
    public Light? FlashlightLight { get; set; }

    public bool HasHeldTool => HeldToolProp != null;
    public bool HasWalkie => WalkieProp != null;

    private int _rightHandLayer = -1;
    private int _bothHandsLayer = -1;
    private bool _initializedLayers = false;
    private MaskedScrapManager? _scrapManager;

    public void Initialize(MaskedPlayerEnemy masked, Transform? rightHandBone)
    {
        Masked = masked;
        RightHandBone = rightHandBone;
        _scrapManager = masked.GetComponent<MaskedScrapManager>();
        InitLayers();
    }

    private void InitLayers()
    {
        if (_initializedLayers || Masked?.creatureAnimator == null) return;
        _rightHandLayer = Masked.creatureAnimator.GetLayerIndex("HoldingItemsRightHand");
        _bothHandsLayer = Masked.creatureAnimator.GetLayerIndex("HoldingItemsBothHands");
        if (_rightHandLayer >= 0 || _bothHandsLayer >= 0)
        {
            _initializedLayers = true;
        }
    }

    public void SetHeldToolActive(bool active)
    {
        if (HeldToolProp != null && HeldToolProp.activeSelf != active)
        {
            HeldToolProp.SetActive(active);
        }
        if (FlashlightLight != null && FlashlightLight.enabled != active)
        {
            FlashlightLight.enabled = active;
        }
    }

    public void SetWalkieActive(bool active)
    {
        if (WalkieProp != null && WalkieProp.activeSelf != active)
        {
            WalkieProp.SetActive(active);
        }
    }

    /// <summary>
    /// Synchronizes the mimic's creatureAnimator layers to match player holding animations.
    /// In friendly mode:
    ///   - HandsOut is forced false (no zombie arms).
    ///   - Two-handed items (Shotgun, Shovel, Sign, large scrap) set HoldingItemsBothHands = 1f.
    ///   - One-handed items (Flashlights, Knife, small scrap) set HoldingItemsRightHand = 1f.
    ///   - Empty hands set both layers to 0f with cancelHolding = true for natural arm swinging.
    /// In aggressive mode:
    ///   - Holding layers are reset to 0f and HandsOut is set to true for the attack rush.
    /// </summary>
    public void UpdateAnimationLayers(bool hasRealScrap, bool isAggressive)
    {
        if (Masked == null || Masked.creatureAnimator == null) return;
        InitLayers();

        if (isAggressive)
        {
            // Aggressive mode: zombie attack rush
            if (_rightHandLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_rightHandLayer, 0f);
            if (_bothHandsLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_bothHandsLayer, 0f);
            Masked.creatureAnimator.SetBool("cancelHolding", true);
            Masked.handsOut = true;
            Masked.creatureAnimator.SetBool("HandsOut", true);
            return;
        }

        // Friendly / undercover mode: strictly enforce NO zombie arms!
        Masked.handsOut = false;
        Masked.creatureAnimator.SetBool("HandsOut", false);

        if (hasRealScrap)
        {
            _scrapManager ??= Masked.GetComponent<MaskedScrapManager>();
            bool scrapTwoHanded = _scrapManager != null && _scrapManager.IsHoldingTwoHanded;

            if (scrapTwoHanded)
            {
                if (_bothHandsLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_bothHandsLayer, 1f);
                if (_rightHandLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_rightHandLayer, 0f);
            }
            else
            {
                if (_bothHandsLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_bothHandsLayer, 0f);
                if (_rightHandLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_rightHandLayer, 1f);
            }
            Masked.creatureAnimator.SetBool("cancelHolding", false);
        }
        else if (HeldToolProp != null && HeldToolProp.activeSelf)
        {
            if (IsTwoHanded)
            {
                if (_bothHandsLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_bothHandsLayer, 1f);
                if (_rightHandLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_rightHandLayer, 0f);
            }
            else
            {
                if (_bothHandsLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_bothHandsLayer, 0f);
                if (_rightHandLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_rightHandLayer, 1f);
            }
            Masked.creatureAnimator.SetBool("cancelHolding", false);
        }
        else
        {
            // Empty hands: natural crewmate walking arms swing
            if (_bothHandsLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_bothHandsLayer, 0f);
            if (_rightHandLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_rightHandLayer, 0f);
            Masked.creatureAnimator.SetBool("cancelHolding", true);
        }
    }

    /// <summary>
    /// Explicitly destroys all visual props immediately.
    /// Called on enemy death (KillEnemy) so visual items NEVER drop on the ground or duplicate.
    /// </summary>
    public void DestroyAllProps()
    {
        if (WalkieProp != null)
        {
            Destroy(WalkieProp);
            WalkieProp = null;
        }

        if (HeldToolProp != null)
        {
            Destroy(HeldToolProp);
            HeldToolProp = null;
        }

        HeldToolDef = null;
        FlashlightLight = null;
        IsTwoHanded = false;
        IsFlashlight = false;
        IsProFlashlight = false;

        if (Masked?.creatureAnimator != null)
        {
            InitLayers();
            if (_rightHandLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_rightHandLayer, 0f);
            if (_bothHandsLayer >= 0) Masked.creatureAnimator.SetLayerWeight(_bothHandsLayer, 0f);
            Masked.creatureAnimator.SetBool("cancelHolding", true);
        }
    }

    private void OnDestroy()
    {
        DestroyAllProps();
    }
}
