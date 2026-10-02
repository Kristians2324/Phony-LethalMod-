# Phoney: The AI Conversational & Deceptive Mimic Mod
### An Uncensored, Context-Aware Voice Mimic & Multi-Phase Imposter for Lethal Company

**Phoney** transforms the Masked enemy from a predictable, mindless runner into a truly terrifying, deceptive "fake friend." 

Instead of robotic text-to-speech clones or random audio playbacks, **Phoney** transcribes and indexes your friends' actual voice chat in real-time with **zero profanity filtering**, responds contextually to whatever you say to it, and completely overhauls the enemy AI into a **4-phase deceptive imposter**.

---

## Features

### 1. The 4-Phase Deceptive AI System
* **Phase 1: Undercover Looter (The Infiltration):**
  * **Hands Down:** Arms rest by its sides like a real player—no outstretched zombie arms giving it away!
  * **Normal Walking Speed:** Walks at player speed (3.5 units/s) and wanders between rooms and scrap nodes as if scavenging.
  * **Friendly Manners:** When approached, it stops, looks at you, and performs friendly player mannerisms (crouch-spam greetings!).
  * **Innocent Banter:** Calls out scrap, asks where players are, and answers your questions via the AI brain.
* **Phase 2: The Lure / Fake Friend:**
  * **Companion Stalking:** Follows or leads players while maintaining a natural companion distance (4–7 meters).
  * **Group Safety Mechanic:** If 2 or more players are watching the mimic, it stays peaceful and will **not attack** to preserve its disguise.
  * **Bait Voice Lines:** Uses deceptive voice lines recorded from your friend to lure you into dark, isolated areas:
    * *"Bro come here, there's a big engine!"*
    * *"I found the apparatus, help me carry this"*
    * *"Over here guys, look at this"*
* **Phase 3: The Ambush / Betrayal (The Strike):**
  * **Triggers:**
    * You turn your back to the mimic within 5 meters.
    * OR you follow it into an isolated room alone.
  * **The Horror:** Its mask eyes ignite with a piercing glow, it emits a sudden terrifying scream or laugh in your friend's voice, raises zombie arms, and sprints full-speed for the kill!
* **Phase 4: Tactical Retreat & Reset:**
  * If struck by a shovel or outmatched by a group, the mimic breaks chase, lowers its arms, flees into the darkness or vents, and resets its disguise to hunt you from behind later.

---

### 2. Cross-Moon Spawn Injection
* In vanilla Lethal Company, Masked enemies only spawn naturally on Titan (and rarely Rend/Dine).
* **Phoney** injects Masked enemies across **all moons** (Assurance, March, Offense, Vow, Adamance, etc.) with a configurable percentage chance and rarity weight.

---

### 3. Authentic Voice Preservation & Zero Censorship
* **Real Voices:** Indexes your crew's actual microphone audio—preserving real giggles, stutters, vocal inflections, and mic characteristics.
* **Zero Profanity Filtering:** Raw cursing, screams, dark humor, and jokes are transcribed verbatim without asterisks or censors.
* **Human Hesitation:** Injects a natural 0.5s–1.1s delay before speaking so it feels like a real person thinking.
* **100% Free & Standalone:** Runs entirely inside C# using embedded `Whisper.net`. No Python, PyTorch, or paid ElevenLabs subscriptions required.

---

## Configuration (`BepInEx/config/com.user.phoney.cfg`)

Generated automatically on launch:

| Category | Setting | Default | Description |
| :--- | :--- | :--- | :--- |
| **General** | `EnableMimicAI` | `true` | Enable AI-driven voice mimicry. |
| **General** | `EnableUnfilteredBanter` | `true` | Preserve raw profanity, screams, and dark jokes. |
| **Behavior** | `Aggressiveness` | `0.7` | Frequency multiplier for proactive wandering banter (0.1 = rare, 1.0 = chatty). |
| **Behavior** | `ResponseDelaySeconds` | `0.7` | Hesitation delay before the mimic answers your voice in seconds. |
| **Behavior** | `MaskedOnly` | `true` | Restrict mimicry strictly to Masked enemies. |
| **DeceptiveAI** | `EnableDeceptiveAI` | `true` | Enable the 4-phase deceptive imposter state machine. |
| **DeceptiveAI** | `AmbushDistanceThreshold` | `5.0` | Proximity (meters) to trigger ambush when player's back is turned. |
| **DeceptiveAI** | `AllowTacticalRetreat` | `true` | Mimic flees into darkness and resets disguise if struck with a shovel. |
| **Spawning** | `EnableCrossMoonSpawning` | `true` | Inject Masked enemies onto moons other than Titan. |
| **Spawning** | `MoonSpawnChance` | `40.0` | Percentage chance (0-100) that mimics can spawn on any given moon. |
| **Spawning** | `MoonSpawnRarity` | `25` | Spawn rarity weight in the indoor dungeon spawn table. |

---

## How to Test in Lethal Company

1. Press **`Ctrl + Shift + B`** in VS Code (or run `dotnet build LethalMods.sln`).
   * The mod is automatically deployed to your r2modman profiles (`Default` and `Lethal11`).
2. Launch Lethal Company modded via **r2modman**.
3. Land on any moon (even early moons like *Assurance* or *Offense*).
4. Watch for a teammate who seems to be looting or wandering with arms down.
5. Talk to them: *"Did you find scrap?"* / *"Where are you?"*
6. Notice them crouching friendly... but don't turn your back on them when you're alone!
