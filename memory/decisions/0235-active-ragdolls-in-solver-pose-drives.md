# ADR 0235 — Active ragdolls: in-solver pose drives on internal joint nodes

- **Date:** 2026-11-03
- **Status:** accepted
- **Docs:** docs/design/physics-joints.md#active-ragdolls, memory/decisions/0234-engine-owned-jitter2-constraints.md

## Context

`PhysicalBoneSimulator3D` (ADR 0212) either holds bodies kinematic on the animation or lets them fall. G9.10c makes
them active: a character that staggers when pushed and keeps standing. The owner chose drives in the solver over
pre-step PD torques, and local drives plus world holds over local drives only (proposal questions 2 and 3). Building it
on the Demo character showed how stiff the drives must be, and what the solver needs to hold a tall stack of light
bodies.

## Decision

- **`PoseDrive`, a custom Jitter2 constraint** (ADR 0234's rules, 192 bytes).
  - **Angular part:** a soft 3×3 block on the rotation error of body 2 against body 1 × target. Its coefficients come
    from `SoftRow` with Jitter2's step dt, and the accumulated impulse's length is capped at `MaxTorque / idt`.
  - **Linear part:** soft rows at body 2's centre towards a target point, capped by `MaxForce`. It is meant for a
    static body 1 (the world).
  - **Flags of 0 cost a call.**
- **Drives are internal joint nodes.** `BoneDrive3D` is a `Joint3D` the simulator creates per bone, with no owner, so
  it is never saved and never in the editor's tree, like the physical bones' joints. Each bone gets a local drive (to
  its nearest ancestor's body) and a world hold. They inherit deferred creation, bodies leaving and returning, and
  waiting while neither body is dynamic. They don't exclude collisions, never break, and draw nothing. The simulator
  writes targets and strengths straight into the constraint every fixed step (`BoneDrive3D.Drive`), without the
  joint-state path.
- **Targets come from the animated pose, not the skeleton.** While bones are simulated, `OnProcess` writes the bodies
  into the skeleton, so reading the skeleton in the fixed step would make the bodies chase themselves. Targets use the
  animated local poses the simulator already keeps per bone, composed parents first.
- **Readbacks work whenever a bone is simulated**, at any strength. Targets keep updating at strength 0 and only the
  force turns off, so a game can read `GetHoldError()` after fading the hold out and tell the character has lost its
  balance.
- **A driven body is kept awake**, and so is one whose drive stops. `SetActivationState(true)` is the only public way to
  reset Jitter2's sleep timer: a body still for long enough in a perfect hold would otherwise sleep through its drive
  turning off.
- **Strength scales frequency and maximum.** Effective strength is the simulator's × the bone's. Both default to 0, so
  an existing scene behaves exactly as before. `Build` sets `HoldStrength` 1 on the root body.
- **Defaults are 30 Hz drives and 15 Hz holds** (the proposal's 10 Hz and 4 Hz were too soft), and an active ragdoll
  needs at least 2 substeps.
  - A frequency-based drive's stiffness is its body's inertia (or mass) × ω², and a pelvis hold or spine drive carries
    the whole body.
  - On the Demo character (20 bodies of 1 kg), at 1 substep and 4 Hz, the pelvis sagged 31 cm and the upper body
    folded about 90°.
  - At 2 substeps, 30/15 Hz and holds on the pelvis and head, the pelvis and head stay within 1 cm. A 40 N·s chest push
    staggers it 18 cm, and it is back within 7 mm 1.5 s later.
  - One substep isn't enough: the solver doesn't converge through the stack (33 cm stagger, still 7 cm off after
    1.5 s). More iterations help less than substeps.
  - The Demo runs 2 substeps.

## Ruled out

- **Effective inertia about the joint's anchor** (like ADR 0211's PD springs) instead of about the centre. In the
  solver, an impulse sized for the larger inertia is applied to the body's spin about its centre, so it over-corrects
  several-fold on every iteration and diverges: the character exploded in its first frame. The PD springs get away with
  it because they apply forces once, before the step.
- **Pre-step PD torques:** the owner chose in-solver drives (proposal question 2).
- **One substep with stiffer drives:** at 30 Hz the soft rows are already nearly rigid (mass scale about 0.94). The
  collapse is convergence, not stiffness.

## Consequences

- An active ragdoll costs little. 20 active characters step faster than 20 passive ones (267 µs against 465 µs at
  2 substeps), because passive ones collapse into piles of contacts.
- Generic limits from Create physical skeleton (45°/30° cone-twists at the ready pose) can't reach every animated
  pose. The Demo's upper arms stay about 29° off. Humanoid limits (G1c) fix that.
- Games that want active ragdolls set `SubstepCount` to 2 or more in `project.mfproj`.

## Revisit when

- G1c's humanoid limits land: re-tune and tighten the arm checks.
- A game needs active ragdolls at 1 substep, which would mean drives with coupled joint solving (articulations), or a
  hybrid with pre-step torques.
