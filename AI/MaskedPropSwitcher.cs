using System;
using UnityEngine;

namespace Phoney.AI;

/// <summary>
/// Obsolete prototype component superseded by <see cref="MaskedHeldItemHolder"/>,
/// <see cref="MaskedHeldItemManager"/>, and <see cref="MaskedScrapManager"/>.
/// Retained only for binary/assembly backwards compatibility.
/// </summary>
[Obsolete("Superseded by MaskedHeldItemHolder and MaskedScrapManager.")]
public class MaskedPropSwitcher : MonoBehaviour
{
    public void Initialize(MaskedPlayerEnemy masked, PhoneyDeceptiveAI ai,
                           GameObject? flashlightProp, Transform? rightHandBone)
    {
        // No-op: all prop management is now handled exclusively by MaskedHeldItemHolder and MaskedScrapManager
    }
}
