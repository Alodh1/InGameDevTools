# Animation Generator — Technique Implementation Tasks

**Audience:** an AI agent implementing features into the procedural **animation generator**
(`source/InGameDevTools/Animations/VanillaAnimationEditor.Generator.cs`, a `partial` of
`DebugWindowManager`).

**Origin:** these tasks come from studying the actual keyframe data of base-game entities plus the
**Draconis** (dragons) and **Fauna of the Stone Age (FotSA)** mods. This mirrors the earlier pass that
fed hand-modeler techniques into the *creature* generator (see memory `vanilla-model-detail-patterns`).
The goal (memory `animation-editor-pro-goal`): push this toward a professional-grade animation tool.
The user said **"don't hold back."** So this is a large, prioritized backlog — implement top-down.

Every claim below is backed by real data. Concrete reference numbers are in **Appendix A**. The analysis
script used to extract them is in **Appendix B** — re-run it against any shape to verify or find more.

---

## Implementation status (updated after the first build-out)

Most of this backlog is now implemented in `VanillaAnimationEditor.Generator.cs` (all new params default to
no-ops; 41 generator tests pass in `AnimationGeneratorTests.cs`). Status per task:

- **Done:** 2 (sparse/optimize keyframes), 3 (RotShortestDistance), 4 (end-handling vocabulary), 5 (additive
  overlay mode + meta snippet), 6 (entity-meta export), 7 (body surge), 8 (sagittal spine flex), 10 (foot
  reach), 11 (mood overlays: ears-back / mouth-open / tail-set), 12 (tail multi-axis + taper), 13 (Climb gait),
  14 (Charge gait), 16 (backward locomotion), 17 (wing-chain phase-lag billow), 20 (tuck legs in flight),
  21 (flight bob + neck S-curve), 22 (Howl/Roar/Call), 23 (prehensile TrunkCurl), 24 (multi-segment neck
  distribution), 26 (Bite/Swipe/Kick/Gore/Stomp/TailWhip), 28 (pose-to-pose transitions), 30 (anticipate/
  overshoot/bounce envelopes), 31 (weight-shift counterbalance), 32 (footstep sound markers in meta),
  33 (squash & stretch), 34 (secondary jiggle), 35 (Version inheritance), plus most of 29 (Sniff/Peck/Dig/
  Lick/Scratch/Loaf/Drink gestures) and 27 (Death + WoundedRest poses + get-up transition).
- **Partial / remaining:** 1 (only collinear pruning is done — curvature-based *insertion* of extra keys at
  transitions is still TODO), 18 (leg-tuck done, but dedicated fold/soar/glide flight poses still TODO),
  25 (ears-back bias done; geometry-driven Y-axis ear fanning still TODO), 27/29 (the full multi-clip
  wounded set and the random idle-fidget layer are still TODO).
- **Not started:** 9 (limb-girdle shoulder/haunch motion), 15 (swim sub-styles), 19 (takeoff/landing gestures).

The sections below are the original spec; treat the unchecked items above as the live backlog.

---

## 0. Read this first — the data model & current architecture

### 0.1 How the game consumes what we generate (from decompiled `Animation.cs` / `AnimationKeyFrameElement.cs`)
- A generated `Animation` has: `QuantityFrames`, `KeyFrames[]`, `Version`, `EaseAnimationSpeed`,
  `OnActivityStopped`, `OnAnimationEnd`. Each `AnimationKeyFrame` has `Frame` + `Elements`
  (name → `AnimationKeyFrameElement`).
- Per element the channels are: `RotationX/Y/Z` (degrees), `OffsetX/Y/Z` (**1/16-block units** — the
  renderer divides by 16), `StretchX/Y/Z` (scale multiplier, **default 1.0**), `OriginX/Y/Z`
  (moves the pivot), and the booleans `RotShortestDistanceX/Y/Z`.
- **Interpolation is linear (`GameMath.Lerp`) between keyframes.** Curves only exist because we sample
  many keyframes. This is why keyframe placement matters.
- **The three flags (position=0, rotation=1, stretch=2) are resolved *independently* per element.** An
  element can have a rotation keyframe at frame 5 and a position keyframe at frame 12; the game seeks the
  nearest left/right keyframe *for that flag*. Our generator currently writes all channels at every
  sample — we are not exploiting sparse, per-channel tracks (Task 2).
- `RotShortestDistance*`: when set, rotation lerps the short way around. Required when a rotation channel
  crosses ±180° (e.g. swim arm circles reach 180° on the player) or it will spin the long way.
- Wrap-around looping works: if the right keyframe's frame < left's, the game interpolates across the loop
  boundary. So a clean loop needs the frame-0 and frame-(N) values to match (our sampler already does this).

### 0.2 The entity-side metadata layer (`AnimationMetaData.cs`) — currently NOT generated
The shape `Animation` is only half the picture. In the **entity JSON** each animation is referenced by an
`AnimationMetaData` that controls *how it plays*: `Weight`, `ElementWeight` (per-element), `BlendMode`
(`Add` / `Average` / `AddAverage`), `ElementBlendMode` (per-element), `AnimationSpeed`, `MulWithWalkSpeed`,
`EaseInSpeed` / `EaseOutSpeed`, `TriggeredBy`, `SupressDefaultAnimation`, and `AnimationSounds`
(frame-keyed sounds). **Many of the best techniques below (steering overlays, additive moods, footstep
sounds) only work in combination with this metadata.** Several tasks therefore ask us to *also emit a meta
snippet* the user can paste into the entity JSON.

### 0.3 Rig orientation (memory `vs-rig-orientation`, already baked into Locomotion)
Forward = **−X**, up = **+Y**, lateral (left/right) = **Z** (right = −Z). Leg/arm fore-aft swing is
**rotationZ**; body roll = rotationX; head/neck pitch = rotationZ; wing flap = rotationX; tail horizontal
wag = rotationY; vertical bob = OffsetY. Sagittal (back-arch) spine flex is **rotationZ**; lateral spine
coil is **rotationY**.

### 0.4 Current generator map (what already exists — don't rebuild it)
- **Modes** (`VanillaGenMode`): `Oscillation`, `Locomotion`, `Pose`.
- **Oscillation**: per-element wave `VanillaGenChannel`s (Sine/Cosine/Triangle/Saw/Square/Noise) on any
  rotation/offset/stretch axis; phase-per-element, symmetry phase, jitter, amplitude gradient, sharpness.
- **Locomotion** (`BuildVanillaLocomotionChannels`, `EmitVanillaLegChannels`, `BuildVanillaLocomotionRig`):
  extracts leg/arm **joint chains** by name + world position (`VanillaLocoLeg`/`VanillaLocoRig`), assigns
  side/row, picks gait phase (`GaitPhaseFraction`), drives hips with a duty-shaped **Stance** wave and
  knees/ankles with a **SwingBump** wave. Gaits: Walk/Trot/Gallop/Idle/Swim/Fly/Pace/Bound/Stalk/Crawl
  (`ApplyVanillaGaitPreset`). Secondary: body bob/roll/pitch/sway, lateral spine coil (rotationY), tail
  sway/wave (rotationY), head bob/yaw, neck bob, wing flap (rotationX, mirror-signed via
  `VanillaMirroredFlapSign`), ear flop, breathing.
- **Pose** (`BuildVanillaPose`, `BuildVanillaPoseKeyFrames`, `VanillaPoseEnvelope`): folds limb chains +
  spine/head/tail/jaw into a target pose; eased in (and optionally back out), with hold + idle-settle.
  Actions: Sit/Lie/Sleep/Crouch/Rear/Beg/PlayBow/Stretch/Pounce/Eat/Graze/Look\*/Cower/Collapse/Flinch.
- **Output** (`BuildVanillaGenKeyFrames`, `PopulateVanillaGenAnimation`): even-spaced keyframe sampling;
  `OnAnimationEnd` = Repeat/Stop; **`OnActivityStopped` hardcoded to `Rewind`**; `EaseAnimationSpeed` from a
  checkbox; global Intensity/GlobalPhase/Reverse/seed.

### 0.5 Conventions for all tasks
- **Every new parameter defaults to a no-op** so existing presets/tests stay byte-identical (this is the
  established pattern; see the "Advanced (defaults are no-ops)" block in `VanillaGenParams`).
- Reuse the existing wave/sampler plumbing (`VanillaGenElementChannel`, `EvalVanillaGenWave`,
  `AddVanillaGenChannelValue`) wherever possible — most tasks are new channel-emission logic, not new math.
- Classification stays name + hierarchy based (works on any conventionally-named rig). Add tokens, don't
  hardcode entities.
- Add coverage to `tests/InGameDevTools.Tests/AnimationGeneratorTests.cs` for each behavioral task.

---

## Priority tiers
- **P0** — Output-model upgrades that improve *every* generated animation. Do these first.
- **P1** — High-impact realism: locomotion fidelity, the flight overhaul, the gesture/state library.
- **P2** — Specialized appendages and polish.

---

## P0 — Output-model foundations

### Task 1 — Adaptive (curvature-based) keyframe placement
**Evidence:** Vanilla never samples evenly. Wolf `walk` (56 frames) uses 22 keyframes clustered around foot
plants — `[0,2,4,6,10,12,14,16,18,20,24,26,28,30,34,38,40,42,44,46,48,52]` — dense at transitions, sparse on
glides. Wolf `run` keys frames `[0,1,3,5,6,7,8,9,10,11,12,13,15,17,18,19,20,21,22,23]`. Our `BuildVanillaGenKeyFrames`
spaces samples uniformly, which both wastes keyframes on linear stretches and rounds off sharp plants.
**Implementation:** after building per-element channels, sample the *combined* curve densely, then keep a
keyframe only where the second derivative (curvature) exceeds a threshold, always keeping frame 0 and loop
end. Effectively a Douglas–Peucker / curvature decimation on the multi-channel signal. Keep `SampleCount`
as a max budget. Gait-driven curves (Stance/SwingBump) should auto-place a key at the stance↔swing boundary.
**UI:** `Adaptive keyframes` checkbox + `Tolerance` slider (advanced). Default off (= current behavior).
**Acceptance:** generated walk on the wolf shape produces denser keys near plant frames; total keys ≤ budget;
loop still seamless.

### Task 2 — Sparse, per-channel keyframe tracks
**Evidence:** Vanilla sets only the channels that change at each keyframe and relies on the engine's
independent per-flag (position/rotation/stretch) seeking. Wolf `walk` `Jaw` is rotationZ-only; `Tail1` sets
offsetY + rotationY + rotationZ but no X. Writing every channel at every frame (current behavior) bloats the
JSON (matters on the seraph: 236 anims × hundreds of elements — see `animation-editor-pro-goal`) and forces
all channels onto one timeline.
**Implementation:** in `BuildVanillaGenKeyFrames`, only write a channel value into a keyframe when it differs
from linear interpolation of its neighbors (per channel), or when it's an extremum. Drop `AnimationKeyFrameElement`s
that end up empty; drop keyframes that end up empty. Verify the game's left/right seek still resolves
(it does — flags are independent).
**Acceptance:** byte size of a generated wolf walk drops substantially; playback visually identical; no
"all-zero" elements emitted for untargeted parts.

### Task 3 — `RotShortestDistance` flags for large-arc rotations
**Evidence:** vanilla sets `rotShortestDistanceZ`/`Y` on continuously-spinning elements (spindle, distaff,
hurdygurdy — 8×Z, 1×Y). The player `swim` reaches `maxRot=180`; wing flaps and limb circles routinely cross
180°. Without the flag the lerp takes the long way and the part spins backwards.
**Implementation:** when an emitted rotation channel's min→max span for an element exceeds ~180° (or wraps),
set the corresponding `RotShortestDistanceX/Y/Z = true` on that element's keyframes. Expose a manual override
in Oscillation per-channel for the "continuous spin" use case (propeller/wheel/spindle).
**Acceptance:** a generated full-circle arm/wing rotation animates the short way; spindle-style continuous
spin can be forced.

### Task 4 — Full `OnActivityStopped` / `OnAnimationEnd` vocabulary
**Evidence:** the generator hardcodes `OnActivityStopped = Rewind` and only toggles `OnAnimationEnd` between
Repeat/Stop. Real data uses the full set: cycles use `onActivityStopped=EaseOut`; one-shot gestures use
`PlayTillEnd` (must finish before blending out — hare attack/sniff/dig, lion attack/roar); death/jump/land
use `onAnimationEnd=Hold` (freeze last frame); some use `onAnimationEnd=EaseOut` (dragon attack, elephant
roar). See Appendix A.
**Implementation:** add `OnActivityStopped` + `OnAnimationEnd` enum dropdowns to the global controls, with
smart defaults per mode: Locomotion → end=Repeat, stopped=EaseOut; Pose held → end=Repeat, stopped=EaseOut;
Pose one-shot (ReturnToRest) → end=Stop or Hold, stopped=PlayTillEnd. Wire into `PopulateVanillaGenAnimation`.
**Acceptance:** a generated walk cycle eases out instead of snapping back; a generated death holds its final
frame; a generated attack plays to completion when interrupted.

### Task 5 — Additive overlay output + entity-meta snippet export (steering/pitch/lean layers)
**This unlocks a whole class of mod-grade motion. High value.**
**Evidence:** Draconis and FotSA implement **directional steering as 1-frame additive poses** layered on top
of the active gait. Dragon `turn-left`/`turn-right` are single-keyframe animations bending Head + Neck1–3 +
Tail1–5; `fly-up`/`fly-down` pitch Head/Throat/Chest/Neck/Tail. In the entity meta they are wired with
`weight≈0.01` (turn) / `0` (pitch), overall `blendMode=average`, and **`elementBlendMode=addAverage` +
`elementWeight` scoped to exactly those spine/tail elements** — so the bend adds onto whatever locomotion is
running. Elephant ships the same `turn-left`/`turn-right`.
**Implementation:**
1. New `VanillaGenMode.Overlay` (or extend Pose with a "single-frame additive" toggle) that emits a
   **1-keyframe** animation containing only a partial-body pose: lateral spine+tail+neck bend (turn L/R),
   sagittal head/neck/tail pitch (look up/down, climb pitch), body roll (bank), lateral lean.
2. Reuse `BuildVanillaPose`'s spine/neck/tail/head classification; emit the bend with phase-per-segment so
   it reads as a smooth curve down the chain.
3. **Emit a copy-to-clipboard `AnimationMetaData` JSON snippet** alongside: the correct `blendMode`,
   near-zero `weight`, and `elementBlendMode`/`elementWeight` maps listing exactly the elements the overlay
   touches. This is the missing glue that makes overlays actually work in-game.
**UI:** overlay sub-type (Turn / Pitch / Bank / Lean), amount (deg), and a "Copy entity meta" button.
**Acceptance:** generating "turn-left" produces a 1-frame anim affecting only the neck/spine/tail and a valid
meta snippet; pasted into an entity, the creature visibly steers while walking.

### Task 6 — General entity-meta authoring panel
**Evidence:** wolf/dragon entity metas set `blendMode`, `weight`, `animationSpeed`, `mulWithWalkSpeed`,
`easeInSpeed`/`easeOutSpeed`, `supressDefaultAnimation`, `triggeredBy`. Gestures use `AddAverage`; full-body
states use `Average`; postures use slow ease (`easeIn/Out=4`); a dominant gesture uses high weight (wolf
attack-withwindup `weight=100`, dragon `wounded-look weight=100`).
**Implementation:** a small "Entity meta" foldout that lets the user pick blendMode/weight/ease/speed/
mulWithWalkSpeed/triggeredBy and exports the matching `AnimationMetaData` JSON snippet (clipboard). Pre-fill
sensible defaults by mode (gait → Average + mulWithWalkSpeed + supressDefaultAnimation; gesture → AddAverage;
posture → Average + slow ease). Generated animation alone can't carry these, so the export is the deliverable.
**Acceptance:** one click yields a paste-ready meta block consistent with the generated animation's role.

---

## P1 — Locomotion fidelity

### Task 7 — Fore-aft body surge for gallop/bound (Origin OffsetX)
**Evidence:** wolf `run` animates `Origin offsetX[-4..1]` (≈ −0.25..0.06 blocks) in addition to `offsetY[0..1.3]` —
the whole body lunges forward during the drive and recoils during the gather. Our gallop only does vertical
bob + a small body pitch. Without longitudinal surge the gallop looks like it's running on a treadmill.
**Implementation:** in `BuildVanillaLocomotionChannels`, on the root/origin element add an OffsetX channel for
asymmetric gaits (Gallop/Bound/Canter), one cycle per stride, phased to peak during suspension. New param
`BodySurge` (1/16 units). Default 0; preset it in `ApplyVanillaGaitPreset` for Gallop/Bound.
**Acceptance:** generated gallop shows visible forward lunge + recoil synced to the leg cycle.

### Task 8 — Sagittal spine flex (rotationZ) for asymmetric gaits
**Evidence:** wolf `run` flexes the spine in the **sagittal** plane on rotationZ — `Rear[-8..19]`,
`Midsection[-17..8]`, `Chest[-8..0]` — the back rounds during the gather and extends during the leap. Our
`SpineBend` is purely lateral (rotationY coil). A galloping mammal *must* arch its back.
**Implementation:** add a `SpineFlex` param: rotationZ on each spine segment, alternating/cumulative so the
column rounds and extends once per stride (2× for bound), phased with `BodyBob`/suspension. Detect the spine
chain the same way `BuildVanillaPose` does (`IsVanillaPoseSpine`). Default 0; preset for Gallop/Bound/Canter.
Keep the lateral coil for Walk/Trot.
**Acceptance:** generated gallop/bound visibly arches and extends the back; walk unchanged.

### Task 9 — Limb girdle (shoulder/haunch) motion
**Evidence:** wolf walk/run animate `Shoulder`/`Haunch` as their own elements — `R Shoulder offsetX[-2..3]
offsetY[-1.2..0.7] rotationZ[0..28]`, `L Haunch offsetX[-1..0.3] offsetY[-1.2..0.5]` — the limb *girdle*
shifts and lifts, not just the leg below it. The creature generator already builds embedded shoulder/haunch
volumes (memory `vanilla-model-detail-patterns`); the animator ignores them.
**Implementation:** in `BuildVanillaLocomotionRig`, detect a `shoulder`/`haunch` element that is the parent of
a leg/arm chain. In `EmitVanillaLegChannels`, add a small girdle lift (OffsetY) + counter-rotation (rotationZ)
phased with that limb's stride. New param `GirdleMotion` (0..1 scale). Default 0.
**Acceptance:** on a wolf-style rig the shoulders/haunches subtly rise and rock with their limbs.

### Task 10 — Foot reach + lift profile (OffsetX during swing)
**Evidence:** wolf walk feet do `offsetX[0..0.4]` (reach) on top of `offsetY[0..0.5]` (lift); in run the front
paw does `offsetX[-0.2..0.4] offsetY[0..1.2]`. Our `FootLift` is OffsetY-only on the last segment with a
symmetric SwingBump. Real feet reach forward as they lift and plant slightly behind.
**Implementation:** extend the foot-lift emission in `EmitVanillaLegChannels` to add an OffsetX reach channel
(forward during early swing, back during plant) alongside the existing OffsetY lift, both SwingBump-shaped.
New param `FootReach`. Default 0.
**Acceptance:** generated walk shows feet arcing forward-up-down rather than just bobbing vertically.

### Task 11 — Gait "mood" posture overlays (ears back, mouth open/pant, tail set, hackles)
**Evidence:** posture is baked into locomotion. Wolf `run`: ears **statically pinned back** (`R Ear`
rotX22 rotY−130 rotZ65 offX1.1 — constant across the whole run), jaw open & panting (`Jaw rotationZ[12..40]`),
tail raised (`Tail rotationZ[32..49]`). Wolf `walk` even bakes a resting open jaw (`Jaw rotationZ` constant 12).
Lion `stalk` lowers the whole front end.
**Implementation:** add optional constant-bias overlays applied per gait: `EarsBack`, `MouthOpen`/`Pant`
(jaw bias + optional cyclic pant), `TailSet` (raised/tucked/level bias on the tail root), `Hackles`
(bristle bias on a `fur`/`mane` element). Implement as bias terms on the relevant channels (bias already
exists in `VanillaGenElementChannel`). Default off; preset aggressive gaits (Gallop/Bound/Charge) to ears-back
+ pant + tail-up.
**Acceptance:** a generated run can pin the ears and open the mouth; toggles are independent.

### Task 12 — Tail/whip chain: multi-axis, tip-damped, vertical bounce, prehensile curl
**Evidence:** wolf tail uses **three** channels per segment with amplitude **decaying toward the tip** —
`Tail/Tail1` rotationY±3 + rotationZ±3 + offsetY 0.1; `Tail3` Z±2; `Tail4` Z±1 (taper). In run the tail is
both raised (bias) and waving. Dragon tail trails with a rotationZ travelling wave in flight. Elephant trunk
(Task 23) is the prehensile extreme.
**Implementation:** rewrite the tail block in `BuildVanillaLocomotionChannels`:
- emit on rotationY (sway) **and** rotationZ (bob) with a tunable mix,
- multiply amplitude by a taper factor that decays with segment index (tip moves less in absolute base
  rotation but more in world space — match vanilla's literal decay),
- add a small OffsetY bounce on the first 1–2 segments,
- keep the per-segment phase (`TailWave`) for the travelling wave.
New params `TailBob` (Z mix), `TailTaper`. Default to current Y-only behavior.
**Acceptance:** generated tail reads as a soft, tapering travelling wave rather than a rigid uniform wag.

### Task 13 — New gait: Climb
**Evidence:** bear `climb` (31 frames, 11 keys) — a vertical-ascent gait where the body pitches up and limbs
reach overhead and pull down alternately.
**Implementation:** add `VanillaGenGait.Climb`. Phase = diagonal like walk, but: large body pitch-up bias,
arms/front legs reach high (extended hip rotationZ bias + big swing), feet "pull down" power stroke (stance
emphasized), minimal bob, slow. Preset in `ApplyVanillaGaitPreset`.
**Acceptance:** generated climb on a bear/raccoon rig reads as scaling a wall.

### Task 14 — New gait: Charge (aggressive run, head/horns lowered)
**Evidence:** FotSA elephant `charge` and cassowary `charge` are faster, committed runs distinct from `run`.
Bovids/rhinos lower the head and present horns/tusks.
**Implementation:** add `VanillaGenGait.Charge` = Gallop tuned faster/longer-stride + a head/neck **down**
bias + (if a `horn`/`tusk`/`antler` element exists) thrust-forward bias. Lean the torso forward (body pitch).
**Acceptance:** generated charge is visibly faster and lower than run, head down.

### Task 15 — Swim sub-styles
**Evidence:** swimming is not one motion. **Sea turtle** (`cheloniamydas`) paddles 4 flippers (rotX/Y/Z, low
amp, no spine flex). **Fish** (bass) is a subtle axial body wave on the spine (`swim maxRot=30`) + tail.
**Dragon** undulates the whole body. **Player** does breast/flutter with the arms. Our single `Swim` gait
fits none well.
**Implementation:** add a `SwimStyle` enum to the Swim gait: `FlipperPaddle` (limbs as paddles on rotationX
with a back-to-front catch-and-recover, body rigid), `AxialBodyWave` (no limbs; rotationY travelling wave
down spine+tail, tail dominant), `LegPaddle`/dog-paddle (legs alternate quick paddles, body level),
`Undulate` (whole-body sinusoid). Each maps to existing channel emission with different targets/axes.
**Acceptance:** turtle rig → flipper paddle reads correctly; fish rig → body wave; selectable.

### Task 16 — Backward locomotion (walkback/flyback)
**Evidence:** dragon and elephant ship `walkback`; dragon ships `flyback`. These are not just the loop
reversed — the legs still swing heel-to-toe correctly but the body intent is reverse.
**Implementation:** a `Backward` toggle on Locomotion that inverts stride direction and body surge/pitch sign
while keeping the swing/stance duty shape physically correct (foot still lifts, doesn't drag). Distinct from
the existing whole-loop `Reverse` (which just plays frames backwards).
**Acceptance:** generated walkback shows the creature stepping backward with proper foot lift.

---

## P1 — Flight overhaul (currently only a single `WingFlap` rotationX exists)

### Task 17 — Multi-segment wing with phase-lagged distal billow (figure-8 beat)
**Evidence:** dragon `fly` does **not** rotate the wing as a rigid plank. The humerus/metacarpal hold a
static **spread** pose (rotationZ ~44/136°); the **beat** is rotationX on `LShoulder`/`RShoulder` (±42) and
`LForearm`/`RForearm` (±20); the membrane **finger** bones (`LFingie1` rotX[−8..43], `Fingie1a` [−20..15]) and
`ForearmWing` flex **out of phase** — producing tip lag, billow and follow-through. Real wingbeats are also
**asymmetric**: a powered downstroke and a faster, feathered upstroke.
**Implementation:** rework wing handling in `BuildVanillaLocomotionChannels`:
- detect a wing **chain** (shoulder/humerus → forearm/radius → metacarpal → finger/membrane) like the leg
  chain extractor,
- flap the proximal segments together; propagate the same wave to distal segments with an increasing
  **phase lag** and decaying amplitude (billow),
- add a `WingBeatAsymmetry` (downstroke slower/stronger than upstroke) by reusing a duty-shaped wave instead
  of pure sine,
- optional small rotationZ "sweep" so the tip traces a figure-8.
New params `WingChainLag`, `WingBeatAsymmetry`, `WingSweep`. Keep the simple single-element flap as the
fallback when no chain is found.
**Acceptance:** generated dragon/bird flap shows tip lag and a powered downstroke, not a rigid see-saw.

### Task 18 — Flight pose set: folded / soar / glide
**Evidence:** sparrow `foldwings` (wings tucked), `soar` (wings held out, still), `flap` (active); dragon
`wingfold` (1-frame), `glide`. Birds cycle between flapping and gliding.
**Implementation:** add flight **poses** (in Pose mode or as overlays): `WingsFold`, `WingsSoar`
(spread + slight dihedral, no beat), `WingsGlide` (half-fold). Hold poses; combine with Task 5 overlay export
so they can blend over a glide path.
**Acceptance:** generating "soar" spreads and freezes the wings; "fold" tucks them against the body.

### Task 19 — Takeoff / landing transition gestures
**Evidence:** sparrow `lift` (`onEnd=Hold`, takeoff) and `falling`; dragon `jump` (1-frame launch pose).
**Implementation:** `Takeoff` gesture (crouch → explosive leg extension + first downstroke, ends Hold/wings
up) and `Land` gesture (legs reach down + extend, wings cup forward to brake, body absorbs). Reuse Pose
machinery with anticipation/overshoot (Task 30).
**Acceptance:** generated takeoff launches and holds wings up; land cups the wings and flexes the legs.

### Task 20 — Tuck legs (and trail tail) automatically in Fly gait
**Evidence:** dragon `fly` holds the legs in a static **tucked** pose (`RTibia` Z25, `RMetatarsal` Z50 +
offsets) and the tail trails behind with a wave. Our Fly gait just gives legs a tiny stride, leaving them
dangling.
**Implementation:** when gait == Fly/Glide/Hover, fold the leg chains into a tuck (constant bias via the
Pose `Fold` helper) instead of striding them, and route the tail to a trailing rotationZ wave.
**Acceptance:** generated flight has tucked legs and a streaming tail.

### Task 21 — Wingbeat-synced body bob + held neck S-curve in flight
**Evidence:** dragon `fly`: `Origin offsetY[−2..2]` + `BodyOrigin offsetY[−0.1..0.1]` (two-level vertical bob
synced to the beat); neck held in an **S-curve** (`Neck1` Z+16, `Neck2` Z−9, `Neck3` Z−7 — alternating signs)
with slight motion.
**Implementation:** in Fly gait add a body OffsetY bob at wingbeat frequency (peaks on downstroke), and an
alternating-sign rotationZ bias down the neck chain to hold the S. New params `FlightBob`, `NeckCurve`.
**Acceptance:** generated flight bobs vertically in time with the wings and holds a curved neck.

---

## P1 — Gesture & state library (Pose mode expansion)

### Task 22 — Vocalization gestures: Howl / Roar / Call / Bugle
**Evidence:** wolf `howl` (100 frames, multi-stage `[0,10,15,20,30,36,50,80,90,93]`): head tilts back
(`Head rotationZ[−58..0]`), neck raises, **jaw opens wide** (`Jaw[12..60]`), cheeks puff, chest heaves, and
the body **shifts weight** onto one side (one paw repositions). Rooster `roostercall` (80f), cassowary `call`,
elephant/lion `roar`, dragon `greet`.
**Implementation:** `VanillaGenAction.Howl` (and Roar/Call variants): raise neck + tilt head back (rotationZ),
cyclic or sustained jaw open, subtle chest expansion (StretchY or rotationZ), optional throat element bulge.
Multi-stage envelope (anticipation dip → raise → sustain → release) rather than a single eased pose. Pair with
Task 32 (sound at the call frame) and Task 31 (weight shift).
**Acceptance:** generated howl on the wolf rig tilts the head back, opens the jaw, and holds before releasing.

### Task 23 — Prehensile / segmented-appendage curl mode (trunk, tongue, tentacle, prehensile tail)
**Evidence:** elephant trunk (`Trunk 2..11` + tips) curls with an **accumulating** bend toward the tip in
`eat` — rotationZ roughly `Trunk2 −15 → Trunk5 +33 → Trunk9 +55 → Trunk11 +75` — plus a lateral rotationY
wave and independent tip articulation (`Trunk Tip Upper/Lower`). This is a curl-toward-a-target, not the
constant-phase travelling wave our tail uses.
**Implementation:** new appendage handling: detect a long single chain (`trunk`/`tongue`/`tentacle`/
`proboscis`, or a tail flagged prehensile). Provide a `CurlAmount` that distributes an **accumulating** bend
down the chain (each segment adds a fraction, so the tip coils tightest), a `CurlAxis`, a slow idle `Drift`
lateral wave, and optional tip "grab" articulation. Reuse for prehensile-tail poses (monkey/chameleon).
**Acceptance:** generated "trunk curl" coils the elephant trunk up to the mouth; reducing CurlAmount uncoils it.

### Task 24 — Multi-segment neck (S-curve poses + travelling nod)
**Evidence:** dragon neck is `Neck1/Neck1Top/Neck2/Neck3` with alternating-sign holds (S-curve, Task 21).
Our Pose/Locomotion treat "neck" as a single element, so long swan/dragon/sauropod necks move rigidly.
**Implementation:** detect a neck **chain** (multiple `neck*` segments, ordered base→head). Support: S-curve
bias (alternating sign), uniform arch (graze/look-down distributes bend along the chain), and a travelling
nod (phase-per-segment) for bird walk. Update `BuildVanillaPose` and the locomotion head/neck block to
distribute across the chain instead of a single element.
**Acceptance:** graze on a long-necked rig curves the whole neck down smoothly; look-up makes an S.

### Task 25 — Ear/fin/membrane fanning on the correct axis + pinned states
**Evidence:** elephant ears **fan on rotationY** (`R Ear Upper rotationY[−47..22]`), not the X-axis flop our
`EarFlop` uses. Wolf pins ears back (Task 11). Fish fins fan; fan tails spread.
**Implementation:** generalize `EarFlop` into a `Fan` channel that picks the axis from the element's geometry
(broad flat ears fan on Y; floppy ears flop on X) or exposes an axis choice. Add `EarPin` bias state.
Reuse for `fin`/`frill`/`fan` elements.
**Acceptance:** elephant ears fan front-to-back; floppy-eared rigs still flop vertically.

### Task 26 — Expanded attack set with windup→strike→recover phasing
**Evidence:** wolf has `attack`, `attack-withwindup`, `attack-low/high(-withwindup)` — distinct strike zones,
and the windup variants spend frames `[0,10,17,...]` loading before the strike. Lion/cassowary/elephant ship
`attack` lunges (`maxOff` 6–8 → the body lunges at the target). Rooster `peck`; cassowary kick; bovids gore.
**Implementation:** generalize `Pounce` into an attack family with a three-phase envelope (anticipation/windup
→ fast strike → recovery, *not* symmetric smoothstep): `Bite` (lunge + head-down + jaw snap), `Swipe/Slap`
(one arm/paw arcs across), `Kick` (biped hind-leg thrust), `Gore/Headbutt` (head+horn thrust + body drive),
`Stomp`, `TailWhip`. Add a `Windup` fraction param. Pair with Task 32 (`damageAtFrame` marker at the strike).
**Acceptance:** generated bite winds the head back, lunges forward with body offset, snaps the jaw, recovers.

### Task 27 — Wounded / downed state set + recovery transition
**Evidence:** FotSA elephant ships a full injured system — `wounded-idle`, `wounded-breath`, `wounded-spasm`,
`wounded-resthead`, `wounded-look`, `wounded-call`, `wounded-trystand`, `wounded-stand` — i.e. a downed
animal that breathes heavily, spasms, looks around, calls, and **attempts to stand** then **stands**. Dragon
has `wounded-idle/-resthead/-look`. Plus `die` (`onEnd=Hold`) and 1-frame `dead` hold poses.
**Implementation:** a "Downed/Wounded" generator producing: the collapsed lying pose (reuse `Collapse`) as a
held base, plus selectable overlays — labored `Breath` (slow big StretchY/chest), `Spasm` (occasional sharp
jolt), `RestHead`/`Look`/`Call`. And a **transition gesture** `TryStand`→`Stand` (Task 28) from downed to
standing. Also a proper `Death` (collapse, `onEnd=Hold`) and 1-frame `dead` pose export.
**Acceptance:** generates a believable downed-and-breathing loop and a get-up sequence.

### Task 28 — Pose-to-pose transition gestures
**Evidence:** wounded `trystand`→`stand`, bear `stand` (rear up over 140f), sit↔stand. These interpolate
between two named poses with anticipation and effort, not a rest→pose ease.
**Implementation:** let Pose mode take **two** actions (From / To) and generate the eased transition between
them (optionally with an effort overshoot mid-way). Internally: build both poses via `BuildVanillaPose`,
keyframe-interpolate From→To with the envelope. Default From = rest (current behavior).
**Acceptance:** "sit → stand" generates a smooth rise; "downed → stand" generates a labored get-up.

### Task 29 — Foraging / grooming / idle-fidget gestures
**Evidence:** hare `sniff`/`dig`/`longdig`; rooster `peck`/`eat`; cassowary `scratch`; lion `lick`/`loaf`;
fish long `eat` (240f). Idle in vanilla is *almost still* (wolf idle 2 keys, 1–3° + a 0.15 shoulder breath),
and richer entities layer fidgets (`idle1`, ear-twitch, tail-flick, blink).
**Implementation:** add gestures `Sniff` (head dips, small bobs), `Peck` (sharp head-down strike, repeatable),
`Dig`/`Forage` (alternating front-paw scrapes + head dip), `Lick`/`Groom` (head to flank/paw), `Scratch`
(one hind leg to head), `Loaf` (cat tuck). Add an **idle-fidget** layer to Idle: low-probability
seeded blinks (eyelid offset), ear-twitches, tail-flicks, weight-shifts, look-arounds — sparse, not
continuous. Keep base idle whisper-quiet like vanilla.
**Acceptance:** generated idle is mostly still with occasional natural fidgets; peck/dig/groom read clearly.

---

## P2 — Polish & secondary motion

### Task 30 — Anticipation / overshoot / settle envelopes for gestures
**Evidence:** real gestures load before they fire and overshoot before settling — wolf `attack-withwindup`,
player `heavyimpact` (`[0,3,5,7,23,41]` — sharp hit then long settle), `land`. Our `VanillaPoseEnvelope` is a
symmetric smoothstep.
**Implementation:** add envelope shapes to Pose: `Anticipate` (small reverse dip before the move),
`Overshoot` (go past target then settle back), `Bounce` (damped). Let one-shot actions pick an envelope.
**Acceptance:** generated pounce dips back before lunging and overshoots the landing.

### Task 31 — Weight-shift counterbalance for one-limb gestures
**Evidence:** during wolf `howl` and cassowary `scratch`, lifting/repositioning one limb shifts the body's
weight onto the planted side (root offset + opposite-side limb load).
**Implementation:** when a gesture lifts a single limb (scratch/beg/one-paw), automatically add a small root
OffsetZ (lateral) toward the support side + subtle counter-rotation. Hook into `BuildVanillaPose`.
**Acceptance:** generated scratch shifts the body onto the standing legs instead of floating.

### Task 32 — Auto footstep / event sound markers (meta snippet)
**Evidence:** wolf/dragon entity metas attach `animationSounds` at the exact **plant frames** (wolf walk:
frames 2,13,30,42; dragon gallop: 10,70,90,100) with `range`, wildcard `location`, and `pitch/volume`
(dragon even uses `pitchByType`/`volumeByType` per age). Attacks use `damageAtFrame`.
**Implementation:** the generator already computes each foot's gait phase, so it knows the frame each foot
plants. Emit an `animationSounds` array (footstep entries at plant frames) into the **meta export**
(Task 6), with editable location/range/pitch/volume. For attack gestures, emit `damageAtFrame` at the strike
frame and `soundAtFrame` for the vocalization peak.
**Acceptance:** generated walk's meta snippet contains footstep sounds at the frames feet actually land.

### Task 33 — Squash & stretch coupled to bob/impact
**Evidence:** heavy quadrupeds and impacts compress vertically on footfall/landing (bear gaits, player
`land`/`heavyimpact`). Pure rotation/offset misses the weighty "give."
**Implementation:** optionally couple a small body `StretchY` dip (and compensating StretchX/Z bulge) to the
low point of `BodyBob` and to gesture impact frames. New param `Squash` (0..1). Default 0.
**Acceptance:** a heavy walk subtly compresses the body on each footfall.

### Task 34 — Secondary passive jiggle for loose elements (crest, dewlap, fur, wattle, feathers, fin)
**Evidence:** dragon `LCrest1/RCrest1` and wolf `Fur down` jiggle as secondary motion; the creature generator
already builds crest/dewlap/tail-tuft/mane/fan elements (memory `vanilla-model-detail-patterns`) but nothing
animates them.
**Implementation:** detect loose-element tokens (`crest`/`frill`/`dewlap`/`fur`/`wattle`/`feather`/`fin`/
`mane`/`tuft`) and add a low-amplitude, **phase-lagged** follow of their parent's dominant motion (a damped
spring read: lag + slight overshoot), driven by the noise/sine waves already available. New param
`SecondaryJiggle`. Default 0.
**Acceptance:** generating any gait makes the crest/dewlap/fur lag and wobble after the body.

### Task 35 — Verify `Animation.Version` matches the host shape
**Evidence:** `PopulateVanillaGenAnimation` hardcodes `animation.Version = 0`, but the renderer's
`GetLocalTransformMatrix(Version, …)` changes rotation order by version (decomp note: keyframe rotations
apply in animVersion 2). A version mismatch with the shape's other animations can subtly rotate parts wrong.
**Implementation:** set the generated animation's `Version` from the document/shape's existing animations
(or the shape's declared animation version) instead of a constant. Confirm against a shape whose other anims
use a non-zero version.
**Acceptance:** generated animations use the same rotation convention as hand-made ones on the same shape.

---

## Appendix A — Reference data (measured from real shapes)

Paths are under `%AppData%/Vintagestory/assets/` (vanilla) or inside the mod zips in `~/Downloads`.
Offsets are in 1/16-block units as stored in JSON; rotations in degrees.

| Source | Anim | Frames/Keys | Technique highlights |
|---|---|---|---|
| wolf `walk` | survival …/wolf/eurasian-adult | 56 / 22 | leg swing rotationZ; front paw counter-rot −16..61; foot offsetX 0..0.4 + offsetY 0..0.5; shoulder offsetY 0..0.5; Origin offsetY −0.5..0; tail Y±3 Z 3→1 taper + offsetY; jaw held +12 |
| wolf `run` (gallop) | same | 24 / 20 | **Origin offsetX −4..1** (surge) + offsetY 0..1.3; **sagittal spine Z** Rear −8..19 / Mid −17..8 / Chest −8..0; ears statically pinned; jaw 12..40 pant; tail raised Z 32..49 |
| wolf `howl` | same | 100 / 10 | multi-stage `[0,10,15,20,30,36,50,80,90,93]`; head back Z −58; jaw 12..60; cheeks puff; weight shift to one paw |
| wolf `idle` | same | 60 / 2 | whisper-quiet: 1–3° rotations; shoulder breathe offsetY 0..0.15 |
| seraph | game …/humanoid/seraph | 236 anims | every anim has a `-fp` variant with exaggerated offsets (walk maxOff 1.5 → fp 7); swim maxRot 180; onEnd Repeat/Stop/Hold/EaseOut; onStop EaseOut/Rewind/PlayTillEnd |
| dragon `fly` | draconis weaver/dragon | 61 / 6 | flap rotationX shoulder ±42 / forearm ±20; finger billow lag (Fingie rotX −8..43); legs tucked (static); neck S-curve Z +16/−9/−7; two-level bob Origin±2 + BodyOrigin±0.1; tail trailing Z wave |
| dragon `turn-left/right`, `fly-up/down` | same + entity meta | 1 / 1 | 1-frame additive overlays; entity meta weight≈0.01/0, `elementBlendMode=addAverage` scoped to Head/Neck1-3/Tail (turn) or Head/Throat/Chest/Neck/Tail (pitch) |
| dragon gaits (entity) | draconis weaver entity | — | `supressDefaultAnimation=true`; footstep `animationSounds` at plant frames with `pitchByType`/`volumeByType`; `wounded-look weight=100` |
| elephant `eat` (trunk) | FotSA Elephantidae …/loxodontaafricana/adult | 61 / 9 | trunk progressive curl Z: Trunk2 −15 → Trunk5 +33 → Trunk9 +55 → Trunk11 +75; lateral Y wave; ears fan rotationY −47..22 |
| elephant states | same | — | `walk`/`walkback`/`run`/`charge`; `turn-left/right` 1-frame; **wounded set** (idle/breath/spasm/resthead/look/call/trystand/stand); `die` onEnd=Hold; `dead` 1-frame Hold |
| lion | FotSA Pantherinae/leo | — | `stalk` (10-key crouch-walk); `loaf`; `lick` (groom); `roar`; `attack` lunge offset 8 |
| cassowary | FotSA Casuariidae/cereopsis…adult | — | biped `walk`/`highwalk`/`run`/`charge`; `threat`; `call`; `scratch`; kick `attack` |
| sea turtle | FotSA Chelonioidea/cheloniamydas…adult | — | flipper paddle `swim`/`swimfast` (rotX/Y/Z low amp, no spine flex); `walk` lumber |
| sparrow | survival …/passerine/treesparrow-adult-male | — | flight set: `foldwings`/`soar`/`flap`(135°)/`lift`(Hold)/`falling`; `hit-flight` vs `hit`; `die-fall` vs `die-ground` |
| bear | survival …/bear/brown-adult | — | `stand` (rear up, 140f); `climb` gait |
| wolf (entity meta) | survival …/wolf-adult | — | hurt AddAverage spd 2.2 w10; die Average w10 trig `dead`; idle AddAverage easeOut 4 defaultAnim; sit/sleep Average easeIn/Out 4; attack-withwindup w100; walk/run/canter footstep `animationSounds` at plant frames |

**Universal vocabulary** seen across nearly every entity: `idle`, `walk`, `run`, `swim`, `eat`, `hurt`,
`die`/`death` (onEnd=Hold), `sit`, `sleep`, `lie`, `attack`. Birds add a flight layer; predators add
`stalk`/`stand`/`roar`; mods add `walkback`/`charge`/`turn-left/right`/wounded states.

## Appendix B — Analysis tool

`tmp_anim_analyze.js` (Node, no deps) parses a shape JSON and summarizes each animation's frame/keyframe
counts, end-handling, and per-element channel ranges. The lenient variant (strip `//` comments, drop
trailing commas, quote bare keys) also reads JSON5-style **entity** JSONs to dump their `AnimationMetaData`.
Usage: `node tmp_anim_analyze.js <shape.json> [--full] [--anim=<substr>]`. Re-extract mod shapes with
`unzip -o <mod>.zip <path/to/shape.json>`. Use it to confirm reference numbers and to mine additional species
(every FotSA family in `~/Downloads` follows the same structure).
