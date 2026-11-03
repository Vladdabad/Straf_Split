# Splitscreen implementation notes

Status: **v0.14.0 — parametrized player roster: up to four local players, each with its own
controller, HUD, crosshair and own-sound audio. Guns confirmed working at two players; melee
fix not yet confirmed.** The two bugs
solved below were found and fixed at two players in v0.9.x; their write-ups explain why the
current design is shaped the way it is.

Rendering details below are derived from the decompiled source and the ripped prefab.
Treat them as designed-and-plausible, not confirmed.

## Solved: the flickering lower body

Symptom: each player saw only the other's head, arms and hands — then, once the mod started
re-enabling the meshes, the torso and legs flickered instead of staying missing.

The `[render]` watchdog named the three meshes and the frame:

```
[render] frame 1345 P2 SK_Aboubi00_Leg_Left00:  active True->False  hiddenBy=SK_Aboubi00_Leg_Left00 (itself)
[render] frame 1345 P2 SM_Aboubi00_Torso:       active True->False  hiddenBy=SM_Aboubi00_Torso (itself)
[render] frame 1345 P1 ... same three ...
```

Exactly `torso` + both legs, on both pawns, every frame. Those three are wired to
`FirstPersonController.torso / legLeft / legRight`, and `HandleAnimation()` — which runs in
**Update on every pawn the local connection owns** — ends with:

```csharp
if (torso.activeSelf    && !isSliding && !isCrouchSlopeSliding) torso.SetActive(false);
if (legLeft.activeSelf  && !isSliding && !isCrouchSlopeSliding) legLeft.SetActive(false);
if (legRight.activeSelf && !isSliding && !isCrouchSlopeSliding) legRight.SetActive(false);
```

That is vanilla's "you see your own torso and legs only while sliding" effect — **a second
hiding mechanism, entirely separate from `gameObjectsToDisableForMe`**, which is why the
mod's careful handling of that array never touched it. Two local players means two owners, so
both pawns hide their own lower body on every non-sliding frame.

The fix is timing, not force. `ReassertBodyVisibility()` was running on the 0.25 s weapon
sweep — the game switched the meshes off every frame and the mod switched them back on four
times a second, so the lower body appeared for roughly one frame in fifteen: the flicker. It
now runs **every frame from LateUpdate**, which lands after every Update in the frame and
before rendering, so the meshes are always on at the moment they are drawn. (It reads from a
cached owned-pawn list — `FindObjectsOfType` every frame is not something to ship.)

Known cost: P1 no longer sees their own body during a slide, because the layer split culls
their own body layer from their own camera regardless of this flag. Restoring it would mean
moving torso+legs to a layer the owner's camera keeps, only while sliding.

### What this cost the diagnostics, and what changed

The watchdog **reported the meshes as stable** while the screen was visibly flashing, because
it sampled once per frame at a fixed point in LateUpdate — and at that instant the state was
`False` on every single frame. A mesh toggled twice within one frame is invisible to one
sample. `Watch` now takes a `phase` argument and runs in both Update and LateUpdate, keeping
their histories apart, so an oscillation shows up as the same mesh reporting a different
steady value under `[render/update]` than under `[render/late]`.

## Solved: the shared-input bug (and why the current fix is a workaround)

Symptom was that keyboard and mouse drove both characters. The v0.9.0 forensics named the
cause on the first run:

```
[capture] InputManager.RestartEngine rebuilt the action asset — every live pawn still holds the old one. New static = A2.
[capture] FirstPersonController  on 'PlayerIK(Clone)' static=P1 captured=P1 asset=A2
[spawncheck] FirstPersonController  got=A2 role=ORPHAN MISMATCH
[spawncheck] 3 of 3 input component(s) on P2's pawn did NOT capture P2's action asset.
```

**`InputManager.RestartEngine()` runs during the pawn spawn**, between the prefix that
installs P2's `PlayerControls` and the pawn components' `Awake`. It assigns
`InputManager.inputActions = new PlayerControls()`, so the swap is overwritten and the pawn
captures a brand-new asset (`A2`) that is *neither* static — and therefore nothing was
applying a device filter to it. That is why the keyboard reached P2: not a filter that was
too weak, but a filter pointed at an asset no pawn was reading. It fired for P1's spawn too
(`A1`, `A3`…), which is why `[static]` reports a fresh asset every round.

What fixes it in practice is `LocalPlayers.AssignDevices()`: it pins devices on
the asset **each pawn is actually holding**, read back off `FirstPersonController.playerControls`,
rather than on the statics the mod believes it installed. That is immune to the whole
problem — it does not care which instance a pawn captured or when:

```
pawn device filter: p1Pawn=[Keyboard, Mouse] p2Pawn=[Xbox Controller]
```

It is still a workaround. The root fix is to stop the swap being clobbered — either patch
`InputManager.RestartEngine` to no-op while P2's spawn is in flight, or re-assign
`InputManager.inputActions` from the `RestartEngine` postfix when the swap is active. Worth
doing: as it stands the two pawns keep separate assets only because a timer re-pins them
every 0.25 s, so any code path that reads input in the gap gets the wrong device.

## How to use it

1. **Connect one controller per player.**
2. Launch `game\STRAFTAT\STRAFTAT.exe`, host a lobby (private is fine).
3. In the lobby, each player presses **X** on their own controller:

   | button | what it does |
   |---|---|
   | **X** | on a controller nobody holds: claim a player — the first press takes player 1 (who starts with no controller of their own), each one after that brings in a new player, up to four, **readied up on arrival**. On a controller that already has a player: ready that player up |
   | **Y** | same as X on a controller that already has a player: ready up |
   | **B** | ready → unready; pressed again while unready, leave the lobby |

   Not **A**: that is the menu's own confirm button, so joining on it also opened whatever the
   lobby had selected — player 1's customization screen.

4. Player 1 readies up themselves — X on the controller they claimed, or the lobby's own Ready
   button. Start the match once everyone is ready — the Start button stays greyed out until
   they are, because the game counts every player entry. Two players split the screen
   top/bottom (`VerticalSplit = true` for side-by-side); three and four take quadrants.

**Coming back to the menu after a match, everyone has to ready up again.** That is the game's
doing, not the mod's: `PlayerManager.OnLoadSceneEnd` calls `ServerSetPlayerReadyState(false)`
on the way into MainMenu, for every player. Each one presses **X** on their own controller to
go again.

A controller is the only way in: there is no keyboard hotkey that adds a local player, because
a player who has not pressed anything on a controller has nothing to play with. **F8**
(`[Splitscreen] RemovePlayerKey`) is the emergency exit, removing the last added player from
the keyboard when their controller cannot. `ControllerJoin = false` turns the flow off — and
with it, any way to add a local player at all.

Each player keeps the exact controller they claimed, wherever they end up in the roster. Pads
may be plugged in at any time — a player with no pad simply cannot move, and takes the first
spare that appears. Nothing is bound to keyboard or mouse; the keyboard still drives the menus
and the mod's own hotkeys.

**Settings belong to whoever opens them.** Press **Start** on your own controller in a match, or
open Options from the main menu on it, and the sensitivity, invert, hold/toggle, FOV, brightness
and crosshair controls edit *your* player — saved separately, so four people can share a screen
without sharing one aim. Display, audio and voice settings stay shared; there is one screen.
`[settings] the options screen belongs to P3` in the log says who currently has it. See the
per-player settings section for what is and is not split.

Config: `BepInEx\config\straftat.splitscreen.cfg`.

## Architecture: one connection, N ClientInstances

The game never checks "is this THE local player" at the network layer — FishNet guards check
the **connection** (`IsOwner`, `OwnerMatches(conn)`). So each added player is another copy of
the player prefab (`ClientInstance` + `PlayerManager` + `NetworkObject`) spawned server-side
**onto the host's own connection**:

```
nm.SpawnablePrefabs.GetObject(true, hostNob.PrefabId)  -> same registered prefab as player 1
Instantiate + LocalPlayerMarker { Index = n }          -> local-only marker
nm.ServerManager.Spawn(go, hostNob.Owner)              -> host's connection owns it
```

Everything per-connection then just works N times: `SetSyncValues` allocates the next free
`PlayerId`, spawn points index off it, scoring and round flow treat it as a real player, and
every ServerRpc passes `OwnerMatches`. Remote clients see ordinary extra players — no mod
needed on their end (untested).

### Everything is parametrized by player index

There is no "the second player" anywhere in the mod. `LocalPlayer` is one instance per local
player — index, `ClientInstance`, action set, assigned gamepad, pawn, viewport — and
`LocalPlayers` is the roster every other file iterates. **Player 1 is in that roster too**:
`LocalPlayers.EnsureHostPlayer()` adopts the `ClientInstance` the game already made and tags
it `Index = 0`, so it is looked up, spawned, filtered and laid out by exactly the same code as
the rest. That is the property to preserve — a change made for one player is a change made for
all of them, and adding a third or fourth is constructing another `LocalPlayer`, not writing
another branch.

Player 1 differs in three places, each one index test rather than a parallel implementation:
they keep the HUD, they keep the single `AudioListener`, and `ClientInstance.OnStartClient` is
allowed to claim the singletons for them.

`LocalPlayers.Owning(component)` replaces every bespoke `IsSecond*` helper — it walks up to
the nearest `LocalPlayerMarker` and answers "whose is this?" for a ClientInstance, a pawn, a
camera, or anything under them.

| Patch | Problem it solves |
|---|---|
| `ClientInstance.OnStartClient` prefix/postfix | Owner branch runs for every added player and would steal `ClientInstance.Instance` + `LobbyController.LocalPlayerController`. Save -> run -> restore, for `Index > 0`. Postfix registers the instance on the roster. |
| `PlayerManager.SpawnPlayer(int,int,Vector3,Quaternion)` prefix/postfix | **The input trick.** Every pawn component (FirstPersonController, PlayerPickup, FPArms) reads `InputManager.inputActions` once, in `Awake` during `Instantiate` — including `MouseX/MouseY` lambda subscriptions that can never be unhooked. Swapping the static to that player's own `PlayerControls` for the duration of their spawn rewires the entire pawn in one move. Runs for **every** player including player 1. |
| `PlayerSetup.OnStartClient` prefix/postfix | Owner branch runs for every local pawn — wanted (cameras on, own round-start coroutine) except the singleton writes. Save/restore `Settings.localPlayer` + `Crosshair.player` for `Index > 0`, then re-apply the viewport layout across the roster. |
| `GameManager.RpcLogic___ObserversRoundSpawn_...` postfix | Round transitions respawn only `ClientInstance.Instance`'s pawn. Postfix round-respawns every added player's `PlayerManager`. `RunLocally = true`, so it fires on the host every round. |

Plus the coordinator's LateUpdate guards: `PlayerSetup.Update` re-captures
`Settings.localPlayer` whenever it is null (every round nulls it) — whichever local pawn wins
that race, the slot is handed back to player 1's controller.

### Input: one controller per player, nothing on the keyboard

`LocalPlayers.AssignDevices()` runs every tick and pins each player's asset to their own pad.
Assignment is by **claim**, not by position: a player holds the exact controller that joined
them in the lobby, and keeps it however the roster changes around them. Positional assignment
(`pads[i]` for player `i`) was fine while a keyboard hotkey was the only way in, but with
players joining and leaving on their own controllers it reshuffles everybody every time
somebody drops out.

Anyone without a claim — player 1 before they press A, or a player whose pad was unplugged —
takes the first controller nobody else holds. Re-running every tick is what makes hot-plugging
work: connect a controller mid-match and the next waiting player binds to it with no
reconnect step; unplug one and they are released.

A player with no pad available is pinned to **nothing** — deliberately deaf rather than
falling back to shared keyboard input, which would drive every unbound player at once. That
is the failure this design exists to prevent.

Two details make it hold:

- The pin targets the asset **each pawn is actually holding** (`LocalPlayer.PawnControls`),
  not the statics the mod believes it installed. `InputManager.RestartEngine` fires during the
  spawn and overwrites the static a pawn is about to read, so pawns routinely end up on a
  third instance neither static points at. Pinning what the mod *intended* pins an asset
  nobody reads.
- Player 1 gets their own `PlayerControls` instance too, rather than sharing the static. That
  is what lets their pawn be pinned to a gamepad without taking the keyboard away from the
  pause menu, which reads the shared static. `LocalPlayers.NewControls()` copies the user's
  rebinds across, since a fresh `PlayerControls` otherwise starts from stock bindings.

Mod hotkeys read `Keyboard.current` directly, so they keep working regardless.

### Joining from the controller (`LobbyJoin.cs`)

On the lobby screen, **X** on a controller nobody holds claims a player — and if that claim
brought a new player in, readies them up as soon as their `ClientInstance` lands. **X** on a
controller that already has a player readies *that* player up (**Y** does the same), and **B**
unreadies them, then drops them out of the lobby if pressed again while already unready. This
is the only path that adds a local player; there is no keyboard hotkey for it any more.

X is "yes" at every stage of the screen and B is "back". The reason X has to keep working after
the claim is the trip home from a match: `PlayerManager.OnLoadSceneEnd` unreadies every player
on the way into MainMenu, and by then everyone on the roster is holding the pad they claimed —
so no press is a claim any more, and without this the ready state the match cleared would only
be reachable from Y or the mouse.

**X and not A**, because A is the submit the game's own EventSystem listens for: joining on it
also pressed whatever the lobby had selected, which is player 1's customization screen. Our
press and the UI's press are the same press — there is no reading it "first" — so the fix is a
face button the menu does not use. These buttons are the mod's to name; the menu's are not.

The claim is what makes this one rule instead of two. Player 1 exists before anyone presses
anything, but starts with no controller of their own — until a split is running they play on
everything, keyboard included. So the first A press binds *them*, and only once every player on
the roster holds a controller does an A press mean "make room for someone new". No separate
host case, and a host with a single controller cannot hand it to a second player and lock
themselves out.

Buttons are read straight off `Gamepad.all` rather than through an action asset, for the same
reason the hotkeys read `Keyboard.current`: an unclaimed controller has no asset to read, and a
claimed one is pinned to its player's asset, which must not be disturbed to ask a question.

Readiness is the game's own — `ClientInstance.ServerSetPlayerReadyState`, the call behind the
lobby's Ready button. `LobbyController.UpdatePlayerList` counts every `ClientInstance`, added
ones included, and requires all of them ready before Start lights up.

Joining *is* readying, in the lobby exactly as mid-match: `SplitscreenCoordinator.AdvanceJoins`
readies every added player the moment their `ClientInstance` comes up. A player only exists
because somebody pressed A on a controller to bring them in, so that press is already the
statement "I am in" — a second one on Y only left Start greyed out with no visible reason why.
**B** unreadies, so nobody is stuck in a match they changed their mind about. P1 is the one
exception, and only because they were never added: the host readies themselves.

The spawn is asynchronous — `ClientInstance.OnStartClient` lands a tick or two after
`ServerManager.Spawn` returns — so the ready call is a poll in `AdvanceJoins`, not something
`LobbyJoin.Claim` can do inline. `Claim` arms the join timeout
(`SplitscreenCoordinator.NoteJoinStarted`) so an instance that never comes up is reported
rather than waited on forever.

### Explosives and projectiles (`Patches/ProjectileDamagePatches.cs`)

The hitscan wrap covers weapons whose damage resolves inside `ShootServer*` on the weapon
itself. Nineteen weapons do not work that way — `DualLauncher` (RocketLauncher, HandGrenade,
StunGrenade, Gamma, Kanye, Mortini, Nugget, Prophet, Bublee, HK_Caws, GlandGrenade, DF_Blister,
DF_Cyst) and `WeaponHandSpawner` (APMine, Claymore, ProximityMine, StunMine) spawn an object,
and the damage happens later, in that object's class, on its own call stack.

All of them resolve victims with `Physics.OverlapSphere(pos, radius, bodyLayer)` and then
`GetComponentInParent<PlayerHealth>()`. Those masks admit layer 11 and not 16, and every local
pawn sits on 16 — so the overlap never returned another local player and a rocket to the face
did nothing. The same was true of **remote** players' rockets: they could only ever kill P1,
the one local pawn a remote client leaves on 11.

Eleven damage methods are wrapped with the same swap the guns use. The shooter is not on the
call stack — only a projectile that went off — but every one of these classes stores the pawn
that launched it in `_rootObject`, the field they each already compare against to decide whether
a kill counts as a suicide. That is who gets left alone; a null `_rootObject` means a remote
player threw it, and then every local player is a target.

Two details worth keeping:

- **Prefix + Finalizer, not Postfix.** A Postfix is skipped when the method throws, which would
  strand every other local player on layer 11 — visible to their own next shot.
- **A 30 m proximity filter.** `HandleCollision` runs every frame a projectile is in the air, so
  an unconditional swap would rewrite every hitbox layer in the match twice per frame per
  rocket. No explosion in the game reaches anywhere near 30 m, and skipping distant players
  cannot change the outcome.

`Propeller` and `FlashLight` are left alone: they contain no `Physics` query, no `PlayerHealth`
lookup and no damage call at all.

### The end screen's REMATCH vote (`Patches/VictoryMenuPatches.cs`)

REMATCH is a world-space interactable, and `PlayerPickup` hands `VictoryMenu.OnInteract` the
interacting **pawn's** transform — the one thing in the path that knows which local player
pressed it. Vanilla drops it on the floor: `OnInteract` calls `VoteRematchServer(null)`, and the
server-side logic resolves the voter from the RPC's *connection*, taking the first
`ClientInstance` that connection owns. Every local player shares the host's connection, so that
is always P1 — and the vote is a toggle, so P2 pressing REMATCH cancels P1's vote instead of
casting their own, P3 casts it again, P4 cancels it again. The count never passes one and the
rematch threshold is unreachable.

The prefix resolves the voter from the pawn and toggles that player's own `PlayerId` in the
`VotedPlayers` SyncList directly. We are the server, so this is the same list the RPC's own
server-side logic writes, and it syncs to remote clients identically. `SteamLobby.players`
already contains every added `ClientInstance`, so the `REMATCH : (x/y)` denominator was always
right — only the attribution was wrong.

Only the rematch button is intercepted. RETURN TO MENU and QUIT are session-wide by nature, and
the playlist buttons hold no per-player state.

Leaving is the reason `LocalPlayers.Remove` had to grow up. F8 (still there as the emergency
exit) only ever removes the last player, but B can drop anyone, and **list position IS the index every `LocalPlayerMarker` in the
scene refers to** — so a removal from the middle re-stamps the markers of everyone above it in
the same breath. Without that, every marker above the hole points one slot too high and players
start answering for each other.

Everything here is gated on being in the lobby (menu scene, hosting, live local ClientInstance):
B during a match would drop a player out of a round they are playing.

## Mutual visibility: per-camera renderer toggling (`PlayerVisibility.cs`)

Vanilla hides your own body with `SetActive(false)` on the eight third-person meshes in
`gameObjectsToDisableForMe`. With several local players every pawn runs the owner branch, so
all of them switch themselves off and nobody can see anybody.

**This used to be solved with layers** — each player's body on a private layer, culled from
their own camera. It works, but it costs two layers per player (body + first-person arms) and
STRAFTAT has exactly three free layers (29, 30, 31; everything 0–28 is named). That is a hard
ceiling of two players. It also dragged the hitbox skeleton along, since `colliderParent`
lives under `Graphics`, which is why that version had to clone physics collision rows onto the
borrowed layers to keep shooting honest.

Visibility is now decided per camera in `Camera.onPreCull`:

```
for a camera belonging to player O, for every local player p:
    p's body renderers     enabled = (p != O)     // never see your own body
    p's first-person arms  enabled = (p == O)     // your arms are yours alone
```

and everything is switched back on in `onPostRender`, so the rest of the game — and the
diagnostics — always observe a sane state. That is the entire rule, for any number of players.
Body renderers are those under `graphics`; first-person renderers are those under `cameras[0]`,
which picks up the held weapon for free since it is instantiated under the arms.

Consequences worth knowing:

- **Culling masks are never touched.** The game's own `lowMask`/`highMask` quality handling
  keeps working, and there is no base-mask bookkeeping to get wrong.
- **Every pawn keeps its vanilla layers**, so hitboxes never move, `ClonePhysicsRows` is gone,
  and shooting behaves exactly as it does in vanilla. (`PlayerShoot` raycasts unmasked and
  identifies hits by `PlayerHealth`, so it was layer-independent anyway.)
- Held weapons need no layer sweep, so the quarter-second of someone else's gun appearing in
  your weapon camera after a pickup is gone.
- A camera that belongs to no local player — scene camera, kill cam, reflection probe — sees
  the world as it really is.
- If an exception ever escaped between pre-cull and post-render, a player would stay
  invisible; `RestoreAll()` runs as a safety net every LateUpdate.

### Solved: picking up a grenade lit up a line across the screen (v0.27.0)

The rule above was originally written the obvious way — `enabled = (p != O)` for bodies,
`enabled = (p == O)` for arms — and that **force-enables renderers the game deliberately keeps
disabled**. A held item is parented under the camera rig (`Camera/BobPosition/FPArms/
RightHandPositions/HandGrenadeRight/HandGrenade(Clone)`), so at the next `Refresh()` every one
of its renderers joined the first-person set. Both grenade prefabs carry a **`LineRenderer` on
their root authored `m_Enabled: 0`** (the throw arc, on `ParticleFirework.mat`) — so picking one
up switched that arc on, permanently, and no drop switched it back.

Found with the grenade pickup watch below. The signature that identified it as ours rather than
the game's was pure timing: the pickup's own changes (`layer 7->8`, `reparented`) all land on
the pickup frame at `+0.00s`, and the `enabled off->on` landed at **+0.16 s and +0.25 s** in two
separate attempts — the `Refresh()` cadence, with nothing in the game touching the object then.

The fix is the asymmetry now documented in `PlayerVisibility`: **only ever switch renderers off,
and only ever switch back on the ones we switched off ourselves** (`Suppress` / `RestoreAll` with
a `_suppressed` list). "The game had this off" then survives by construction, for every renderer
that is or ever will be parented under a pawn — no allow-list to keep up to date.

General lesson, and the third of its kind in this mod: **a per-camera override must be
expressed as a delta from the game's state, never as an absolute.** The same shape as the
`CameraEffect` material and the shared ammo strings — the mod is only ever entitled to the
difference it needs, not to the whole value.

## Per-player HUD (`HudRouting.cs`)

Every pawn already carries a complete HUD. It is not a shared canvas — it is diegetic
geometry parented under that pawn's own `UICam`, on layer 25, drawn by that pawn's own third
camera:

```
PlayerIK/CameraLeanPivot/LandBobPivot/CameraHolder/Camera/UICam/Transform/
    TransformDown/SM_HUD_00/SM_BulletCounter_L_00/GameObject/ammolefttext   <- left ammo
                  SM_HUD_00/SM_BulletCounter_R_00/GameObject/ammolefttext (1) <- right ammo
                  SM_HUD_00/SM_Heart_00/hEALTHtext                          <- health
    TransformUp/ScoreTexts/..., SM_MatchPoints_*                            <- score
```

So nothing had to be built. Two things were making it look like player 1's HUD:

**1. It was switched off.** The mod disabled everyone else's `hudObject` and `cameras[2]` on
the belief that the HUD camera would redraw one shared canvas. That was true of the old layer
design; it is not true now. Those HUD renderers live under `cameras[0]`, so the per-camera
rule in `PlayerVisibility` already classifies them as first-person — `enabled = (p == O)` —
and each UICam draws only its owner's. The mod no longer touches `hudObject` or `cameras[2]`
at all; the game's own `HideHUD`/quality handling stays in charge.

**2. Ammo travels a global bus.** Health never did — `PlayerHealth.tweenScript` is wired in
the prefab to that pawn's own `HealthTween`, which renders its own `health` field, so health
was per-player the moment the HUD was visible. Ammo goes through one pair of strings:

```
Weapon.WeaponUpdate()  ->  PauseManager.ChangeAmmoText(text, reload, right)
                       ->  writes PauseManager.Instance.{left,right}GunAmmo.text
HUD.Update()           ->  copies that text onto its own TMP, every frame
```

Every owned weapon writes it every frame and every pawn's counter reads it every frame, so
last writer won and all players showed one player's ammo. `HudRouting` gives each player their
own copy of that pair, on their `LocalPlayer`:

| Patch | Role |
|---|---|
| `Weapon.WeaponUpdate` prefix/postfix | Marks which player's weapon is talking. Wrapping this one method attributes the write without patching all seven call sites that reach `ChangeAmmoText`. |
| `PauseManager.ChangeAmmoText` prefix | Copies the value into that player's slot. The original still runs, so the shared bus — which the scene's minimalist UI also reads — behaves exactly as in vanilla. |
| `HUD.Update` prefix | The two ammo counters read their own player's slot instead of the bus. The ping and FPS variants of the same component are genuinely global and are left to vanilla. |

An empty hand is derived from staleness rather than from the "---" clears: every weapon
subclass calls `base.WeaponUpdate()` from its own `Update`, so a held weapon re-pushes every
frame, and a slot older than 0.25 s means that hand is empty. That covers every way of losing
a weapon — drop, despawn, death, round end — without patching each one.

The scale punch on a value change is reproduced rather than skipped: it is the feedback that
makes a shot read as a shot, and dropping it would be a visible regression for player 1.

### The crosshair (`CrosshairSplit.cs`)

The crosshair is the one HUD element that is *not* pawn geometry. It exists once per scene, as
screen-space UI:

```
---USER INTERFACE---          Canvas, ScreenSpaceOverlay
    Crosshair                 anchor (0.5, 0.5), carries the Crosshair component
    ScopeCrosshair            the scope overlay — a SIBLING, not a child
```

A ScreenSpaceOverlay canvas ignores camera viewport rects, so the single crosshair sits at the
centre of the whole screen — in splitscreen, the seam between viewports, wrong for everyone.
And `Crosshair.player` is one reference, so the sprite tracks one player's weapon, sprint and
aim state.

Each extra player therefore gets a clone, moved into their own viewport. Normalized viewport
coordinates map straight onto canvas anchors on a full-screen overlay canvas, which makes the
placement a couple of lines rather than a projection problem.

**The two objects need opposite treatment, though**, and that is the part that bit us:

```csharp
// Crosshair — point-anchored: pin to the centre of the viewport, scale down
anchorMin = anchorMax = new Vector2(rect.x + rect.width / 2, rect.y + rect.height / 2);
localScale = Vector3.one * Mathf.Min(rect.width, rect.height);

// ScopeCrosshair — stretched: span the viewport, keep the authored sizeDelta
anchorMin = new Vector2(rect.xMin, rect.yMin);
anchorMax = new Vector2(rect.xMax, rect.yMax);
```

For the crosshair, scale matters as much as position: it is a fixed 400x400 canvas units and
`Crosshair.Update` rewrites `sizeDelta` every frame, so `sizeDelta` cannot be used — but the
game never touches `localScale`, which makes it the safe lever. At quarter-screen an unscaled
crosshair would swallow the viewport.

`ScopeCrosshair` is authored the other way: `anchorMin (0,0)`, `anchorMax (1,1)`,
`sizeDelta (0,0)` — Unity's spelling of "fill the parent", where the size lives *entirely* in
the gap between the anchors. Collapsing both anchors to a point, which is what the crosshair
branch does, therefore leaves `sizeDelta (0,0)` meaning a rect of literally **zero size**.

> **Solved (v0.28.0): scoped rifles showed no reticle.** The scope overlay was being placed with
> the point-anchored rule, collapsing a full-screen image to 0x0 px — present, correct sprite,
> right alpha, drawing nothing. The ordinary crosshair survived the identical collapse only by
> accident, because `Crosshair.Update` puts its `sizeDelta` back to 400x400 every frame; nothing
> does that for the scope image. That asymmetry is exactly why the crosshair looked fine while
> the reticle was gone. This is the same rule as the per-camera overrides: **write what the
> object was authored to mean, never a value that assumes one authoring.**

Placement always reads the *authored* layout of the scene's own objects, captured once before
anything moves them — clone or not, since a clone comes out of `Instantiate` with the source's
values and by the second frame the live anchors are ours rather than the scene's.

Player 1 is not special here either: their scene crosshair is re-anchored by the same code and
restored to dead centre when the roster drops back to one.

Two details the clone depends on:

- **It has to be a pair.** `Crosshair.scopeImage` points at `ScopeCrosshair`, a sibling
  *outside* the crosshair's own hierarchy, and `Instantiate` only remaps references that point
  within the copied subtree — so a lone clone keeps driving player 1's scope overlay. Both
  objects are cloned and the clone's `scopeImage` is rewired.
- **`rectTransform` is repaired on the way past.** 372 of the game's 373 map scenes leave that
  serialized field null, and `Crosshair.Update` dereferences it every frame; the split points
  it at the object's own RectTransform.

Clones are rebuilt whenever `Crosshair.Instance` changes, because every map scene carries its
own crosshair and a map load orphans the previous set. A player between pawns has their
crosshair hidden rather than left frozen on the last sprite.

#### Routing the game's writes to the right crosshair (`Patches/ScopeCrosshairPatches.cs`)

Cloning the rig is only half of it. The game decides what a reticle *shows* somewhere else
entirely, and always through the singleton — so every one of those writes is wrapped in the
mod's usual swap: install the holder's crosshair for the duration of the call, let the game's
own code run unchanged, hand the static back in a **finalizer** so a throw inside the game's
method cannot strand the singleton on another player's crosshair.

Three writes, at two levels:

| Where | What it writes | Wrapped by |
|---|---|---|
| `ItemBehaviour.OnGrab` | `standCrosshair`, `aimCrosshair`, `canScopeAim`, `instantAimLens` | `ItemBehaviour_Crosshair_Patch` |
| `ItemBehaviour.Update` | `canScopeAim`, every frame the weapon is held | `ItemBehaviour_Crosshair_Patch` |
| `PlayerPickup` pickup RPC | reads `canScopeAim` to compute `zoomFOV` | `PlayerPickup_Crosshair_Patch` |

> **Solved (v0.29.0): P2's scopes zoomed to the wrong FOV.** The last line of the pickup RPC is
> `zoomFOV = aimFOV + (!Crosshair.Instance.canScopeAim ? defaultFOV - 68f : 0f)` — a **read** of
> the singleton, so for player 2 it answered with player 1's crosshair, and whether P2's sniper
> zoomed correctly depended on what P1 happened to be holding. It ran the other way too: P2
> picking up a pistol while P1 held a sniper got an over-zoomed ADS. The `68f` is a hardcoded
> baseline that does not match `Settings.normalFovValue` (default **75**), so the error was +7
> at stock settings and grew with the FOV slider — at FOV 120 a scope that should reach `aimFOV`
> landed 52 short. Far enough that on light-ADS weapons it could push `playerCamera.fieldOfView`
> back above the reticle threshold and hide the scope reticle outright, which is how it turned
> up while fixing that.

Both singleton touches in the pickup path — `OnGrab` and the `zoomFOV` read — sit in the same
method fifteen lines apart, so the fix wraps their **caller** rather than adding a second
narrow patch. `PlayerPickup.SetObjectInHandObserver` is the one to hook, out of the four
methods FishNet generates around that RPC:

- It is **hand-written**, so its name carries no signature hash. Patching
  `RpcLogic___SetObjectInHandObserver_46969756` by name would break silently the first time
  that signature changed.
- It is the **host path**. `SetObjectInHandServer` runs locally on the host and reaches the
  logic through here, so every local player's grab passes this way. Remote clients arrive via
  `RpcReader___` instead, but they are not ours and the assignment is behind `IsOwner` anyway.
- Its **blast radius is exactly the two reads**. Nothing else in that method — parenting,
  animator state, weapon wiring, ammo display — touches `Crosshair.Instance`.

The inner `OnGrab` swap is then redundant on the host and no-ops on a reference comparison. It
is kept deliberately: it costs nothing, it is the documented mechanism, and it is what still
covers `OnGrab` if the call chain into it ever changes.

### The death banner (`DeathOverlaySplit.cs`)

The second piece of screen-space UI, and the same class of bug. "DEAD" and the
"ENEMY HEALTH : n HP" line under it are two `PauseManager` fields driven by one coroutine:

```
PlayerSetup.OnDisable (the dying pawn, owner only)
    -> PauseManager.ShowEnemyHealth(enemyHealth, ownHealth)
    -> wait 0.2s -> write both texts -> wait 2.7s -> clear
```

One pair of texts on the overlay canvas, so whoever died, the banner appeared at its authored
place on the whole screen — inside player 1's slice. Everyone was told player 2's death was
theirs, and player 2 was not told at all.

Each extra player gets a clone of the pair, placed inside their own viewport by the same code
that runs for player 1.

Placement is a **pure translation**, and deliberately not the crosshair's proportional remap.
The banner is authored at a fixed offset from the top-left of the screen; it keeps that offset,
its size and its scale, and only the corner it is measured from changes — from the screen's
top-left to the viewport's:

```csharp
delta = new Vector2(rect.x, rect.y + rect.height - 1f);   // viewport's top-left, screen-relative
anchoredPosition = home + delta * canvasSize;             // canvas rects run bottom-up, so delta.y <= 0
```

| layout | player | banner lands |
|---|---|---|
| two stacked | P1 | unchanged — the top half's own top-left |
| two stacked | P2 | half a screen down, same distance from the left |
| four | P2 | top centre |
| four | P4 | dead centre of the screen, plus its original offset |

Player 1 always comes out at `(0,0)`: every layout in `PlayerVisibility.Viewport` starts their
slice in the screen's top-left corner, so their banner never moves. Translating through
`anchoredPosition` rather than the anchors keeps the element's size and shape whatever its
anchors are, stretched or fixed, and the offset is applied to the captured home rather than to
the current position, so re-applying it on every layout change cannot accumulate drift.

Nothing is scaled down for a quarter-screen. The banner is small and corner-anchored, so it
fits; the crosshair needed scaling because it is a fixed 400x400 centred sprite.

The write is attributed by patching `ShowEnemyHealth`, which is handed the dying pawn's own
`PlayerHealth` — one patch covers every way of dying, with nothing to add for a third or
fourth player. Player 1 keeps the vanilla coroutine (only their anchors moved); for everyone
else the prefix returns false and `DeathOverlaySplit.Tick` reproduces the timing off the frame
tick, since the mod owns no MonoBehaviour to run coroutines on. Scaled time, deliberately:
`WaitForSeconds` stops while the game is paused and so does this.

Two details it keeps from vanilla:

- **The health check is what keeps "DEAD" off a survivor's screen.** `OnDisable` also fires
  when a living pawn despawns at round end; vanilla only writes the text if health is `<= 0`
  when the 0.2 s is up. If the pawn is gone by then, the health read at request time is used.
- **`onEndRoundScreen` blanks the banner.** `PauseManager.Update` does that to the original;
  the tick does it to every clone.

A death that cannot be placed — no roster entry, or the private fields could not be resolved —
falls through to vanilla, which is a banner in the wrong slice rather than no banner. Clones
are rebuilt whenever the source pair changes, for the same map-load reason as the crosshair.

## Full-screen camera effects (`ScreenEffects.cs`)

The red damage flash and the suppression blur were on everybody's screen at once, and the
component was never the problem. `CameraEffect` is already per-pawn — it sits on that pawn's
own camera and blits in `OnRenderImage` — and the game's triggers already name the right pawn:

```
PlayerHealth.HitFeedbackObservers (on the pawn that was hit)
    -> HitFeeback()            -> if (IsOwner) camHit.TakeHit()
Suppression.SuppressionTrigger (a near miss past that pawn)
    -> if (IsOwner) camPP.TrigSup()
```

What is shared is the **material**:

```yaml
material: {fileID: 2100000, guid: bb8ac678ce993534b8e5675ee875c967, type: 2}   # type 2 = project asset
```

Every pawn's effect points at that one asset, and the effect's entire state lives in its
`_Damage` / `_Suppression` floats — `TakeHit` writes them, and every camera's `OnRenderImage`
reads, decays and re-writes them. One pawn takes a hit; every local camera blits the same red.

Each local player gets their own `new Material(shared)` instead, player 1 included, so nothing
writes to the shipped asset at runtime at all. Instances are destroyed when their pawn's
component goes, and handed back when the roster drops to one.

Two neighbours were checked and need nothing:

- **The taser stun.** `PlayerSetup.StunMatObservers` toggles `VFX_TaserStunned_00`, a particle
  system on the stunned pawn, and sets `Float2` through `Renderer.materials`, which hands back
  per-renderer instances. Already per-player — and correctly visible to *everyone*, since
  seeing who is stunned is the point of it.
- **Screen shake** tweens that pawn's own camera transform (`PlayerHealth.LocalScreenshake`).

## The death view (`KillCamSplit.cs`)

When a player dies their pawn is deactivated, taking its camera with it, and the scene's
`Main Camera` — the one carrying `KillCam` — is switched on to spectate:

```
PlayerHealth.Update   health <= 0  -> mainCamObject.GetComponent<Camera>().enabled = true
PlayerSetup.OnDisable              -> sceneCamera.enabled = true
PlayerSetup.Update    (every OWNED, living pawn, every frame)
                                   -> Camera.main.enabled = false
```

One camera, a full-screen rect, and a switch that every living local pawn flips back off. (That
last line only runs for owned pawns — `OnStartClient` does `base.enabled = false` on everyone
else's — which is exactly why it never bit in vanilla and does here: *all* local pawns are
owned.) The dead player gets a black slice, and the frames the kill cam does survive paint over
everybody.

So the kill cam gets a viewport like any other player's camera, and is held on while anyone
local is dead. Additional dead players get a **mirror**: a bare `Camera` of ours that copies the
scene camera's settings and pose each frame into their own slice. `Camera.CopyFrom` rather than
cloning the GameObject, so no second `KillCam`, `AudioListener` or post-process stack comes with
it — one KillCam still drives the pose, and the mirrors are pure output. The mirror is
deliberately untagged: a second object tagged `MainCamera` would be found by `Camera.main`, and
`PlayerSetup.Update` would start switching *ours* off.

It runs on the late tick, after every pawn's `Update` has had its say about that switch and
before anything renders.

Two cases deliberately keep vanilla's single full-screen kill cam: **nobody dead** (nothing to
show, and the game owns the switch) and **everybody dead**, which is also every round
transition, when all the pawns are briefly gone.

Still shared: there is one `KillCam`, so every dead player sees the same pose and the same
spectator target, and it reads the shared input static — free cam and target switching answer to
player 1. Splitting that means a second `KillCam` driving the shared `SwitchCamText` /
`PlayerNameText` / `PlayerHpText`, which are singletons of their own.
`[Splitscreen] SplitKillCam` turns the whole thing off.

## Audio (`AudioSplit.cs`)

This one has a hard ceiling. **Unity mixes for exactly one AudioListener.** Two enabled
listeners is not "two players hearing" — it is an error the engine warns about and then
resolves arbitrarily, and one stereo output cannot carry two independent spatializations. No
API makes per-player audio possible; the question is only what to do with the one mix.

What was actually broken is narrower than "audio is player 1's", and fixable. All nine
AudioSources on a pawn ship fully 3D (`spatialBlend = 1`, MinDistance 3.54, MaxDistance
60-500) and the listener lives on that pawn's own `Camera`:

| source sits on | what it is |
|---|---|
| `PlayerIK` (x2) | footsteps, body impacts |
| `CameraLeanPivot`, `LandBobPivot`, `CameraHolder` | movement, landings, wind |
| `Camera/Suppression/SupressionBox` (x4) | near-miss whip-cracks |

For whoever holds the listener those all originate at their own head, inside MinDistance, and
play at full volume. For every other local player the same sounds are emitted metres from the
one listener and attenuate to near silence — **they cannot hear themselves move, land, or be
shot at**, and the little they do hear is positioned relative to someone else's head.

The game already has a rule for exactly this, and applies it to weapons:

```csharp
// Weapon.WeaponUpdate()
if (IsOwner) GetComponent<AudioSource>().spatialBlend = 0f;   // your own gun is 2D
else         GetComponent<AudioSource>().spatialBlend = 1f;
```

Your own weapon is 2D so you hear it at full volume wherever the listener is. Splitscreen just
means there are several owners, so the same rule is extended to the rest of the pawn: **every
local player who is not holding the listener gets their own pawn's sounds in 2D.** The
listener holder keeps vanilla 3D throughout, so nothing changes for them — directional
footsteps, directional near-misses, exactly as in single player. Originals are captured per
source and restored when the roster drops back to one.

`[Audio] ListenerPlayer` chooses whose ears the world is heard from (default 1), so that is a
setting rather than something pinned to player 1. `[Audio] HearOwnSounds` turns the 2D rule
off if you would rather everyone stayed 3D.

### Keeping it to one listener

Several things switch listeners on without knowing about each other: `PlayerHealth` enables
the kill cam's the moment a pawn dies, and `PlayerSetup.OnDisable` re-enables the scene
camera's. With one local player those fire once and hand over cleanly. With four, somebody is
dying most of the time, and each of those events would add a second listener.

So `EnforceSingleListener` runs on the same refresh cadence: while the listener holder has a
pawn, theirs is the only one enabled and every other listener in the scene is switched off. If
they have no pawn, vanilla's kill-cam handover is left alone — but the other players'
listeners stay suppressed, because a dead player's kill cam has no business taking the mix
from a living one.

### What this still does not do

With one output, "everyone hears themselves" also means everyone hears everyone else's
own-sounds at full volume and without direction — you cannot locate another local player by
their footsteps. World audio (doors, props, remote players' weapons) is still heard from the
listener holder's position. Both are consequences of the single AudioListener, not shortcuts.

## Solved: local players could not damage each other (`DamageLayers.cs`)

Most weapons did nothing when one local player shot another. There is a self-damage guard
behind it, and it is a **layer**, not a distance or direction test.

The hitbox skeleton is authored on layer 11 (`Body`) — 81 non-trigger colliders under
`colliderParent` — and every hitscan weapon gates damage on exactly that:

```csharp
// Gun.ShootServer; the identical line appears in Shotgun, Minigun, LargeRaycastGun
if (Physics.Raycast(position, direction, out hit, inf, this.playerLayer)
    && hit.transform.gameObject.layer == 11
    && ...PlayerHealth found...)
{
    if (enemyHealth.gameObject == base.transform.root.gameObject) return;   // explicit self-check
```

and `PlayerSetup.OnStartClient`'s **owner** branch ends with:

```csharp
this.SetGameLayerRecursive(this.graphics, 16);   // 16 = "SelfBody"
```

`colliderParent` lives under `graphics`, so the owner's own hitboxes move to layer 16 and can
never satisfy `layer == 11`. In vanilla exactly one pawn is an owner and everything works.
**With several local players every pawn runs the owner branch, so every local pawn sits on 16
and no local player can damage another.** Remote players are unaffected — on their machine our
pawns are non-owners and stay on 11 — which is why this only ever appears between players
sharing a screen.

BeamGun, ChargeGun, RepulsiveGun and Taser have no `layer == 11` line but `CapsuleCast` against
the same `playerLayer` mask, so they fail identically. That is the "many weapons" in the report:
eight of the nine hitscan weapons, all through one cause.

### Confirmed against the shipped prefabs: why grenades work and guns do not

The masks are readable off the assets, and they settle it:

| | mask | layers | 11 `Body` | 16 `SelfBody` |
|---|---|---|---|---|
| Gun `playerLayer` (AK-K, AR15, Crisis, DF_Torrent, …) | `2049` | 0, 11 | yes | **no** |
| Gun `defaultLayer` (environment pass) | `25969665` | 0, 10, 14, 18, 19, 23, 24 | no | no |
| Grenade `bodyLayer` (HandGrenade, StunGrenade, PhysicsHandGrenade, GlandGrenade) | `85759`–`17387263` | … 11, 14, **16** … | yes | **yes** |

So a gun fails **two** independent gates against a pawn on 16 — its raycast mask cannot even
see the collider, and the `layer == 11` equality test would reject it anyway. A grenade passes
because its overlap mask contains 16, it identifies the victim with
`GetComponentInParent<PlayerHealth>()` rather than a layer test, and it applies damage directly:

```csharp
// HandGrenade
Collider[] array = Physics.OverlapSphere(transform.position, explosionRadius, this.bodyLayer);
PlayerHealth h = array[i].GetComponentInParent<PlayerHealth>();
h.sync___set_value_health(h.SyncAccessor_health - 10f, true);
```

Three differences, any one of which would produce "grenades hurt, guns do not":

| | revolver / hitscan guns | grenades |
|---|---|---|
| target search | `Physics.Raycast(..., playerLayer)` — **excludes 16** | `Physics.OverlapSphere(..., bodyLayer)` — **includes 16** |
| victim identification | `hit.gameObject.layer == 11` equality test | `GetComponentInParent<PlayerHealth>()`, no layer test |
| friendly fire | `FriendlyFireCheck(enemyHealth)` blocks same-team | not called at all |

The third only bites in a teams gamemode: in free-for-all `ScoreManager.GetTeamId` falls through
to `SetTeamId(playerId, playerId)`, so P1 is team 0 and P2 is team 1 and the check passes.
`Default2v2()` — which puts PlayerId 0 and 1 on the **same** team — only runs when
`SteamLobby.playingTeams` is set, and would then block all gun damage between P1 and P2 while
leaving grenades working. Worth remembering if this reappears in a teams lobby.

### Why not just put local pawns back on layer 11

The layer is doing real work. On 11 the shooter's **own** hitboxes become raycast targets, so a
shot taken while your own arm crosses the muzzle line finds you first, hits the explicit
self-check above, and is silently swallowed — trading "no damage sometimes" for "no damage at
a different, more confusing time". These are also non-trigger colliders, so 11 would expose
your own body to your own character controller, and the shipped collision matrix is not
recoverable from the ripped project.

### The swap

For the duration of one `ShootServer` call, every local player **except the shooter** is moved
to layer 11, then put straight back. The shooter keeps 16 and stays unable to block or hit
themselves; everyone else reads as an ordinary target. No physics step runs inside a
synchronous method call, so nothing else can observe the change.

All nine hitscan weapons funnel through a method named `ShootServer`, so the patch targets are
discovered by reflection over every `Weapon` subclass rather than listed by hand — a weapon
added by a game update is covered automatically, and a renamed one shows up as a count change
in the `[damage] wrapping N weapon ShootServer method(s)` line rather than failing silently.
Collider lists are cached per pawn and rebuilt by pawn identity, so a shot never resolves
against the previous round's colliders.

`PlayerShoot` is excluded: its `ShootServer` is only the FishNet writer stub, and the real work
happens later in `RpcLogic___ShootServer_1867452766` on a different call stack, where a swap
started here would already have been undone. That path keeps its own patch, which resolves hits
without depending on layers at all.

### Melee: the same bug, one layer lower

Melee never raycasts. `MeleeChildCollision.OnCollisionEnter` resolves a **real physics contact**
and identifies the victim with `GetComponentInParent<PlayerHealth>()` — no layer test anywhere —
so it is governed entirely by the collision matrix, and a swap scoped to a method call cannot
help something resolved during the physics step.

It is the same underlying cause though, because the owner branch moves **two** things:

```csharp
base.gameObject.layer = 6;                      // the pawn root, and the CharacterController on it
this.SetGameLayerRecursive(this.graphics, 16);  // the bone hitboxes
```

A remote player keeps the prefab's layer 3 (`Player`) on the root and 11 (`Body`) on the
hitboxes, and the blade — which `MeleeChildCollision.Update` pins to layer 21 (`Melee`) every
frame — collides with those. Every local pawn is an owner, so all of them sit on 6 and 16, which
21 does not collide with: no contact, no `OnCollisionEnter`, no damage.

So the matrix itself is opened for the duration of splitscreen — 21 may touch 6 and 16 — and the
original values are restored when the roster drops back to one. Both layers matter: 6 is the
capsule, which is what a body blow lands on, and 16 carries the bone colliders, which is where
`Head_Col` comes from. Without 16, local players could be hit but never headshot.

Self-hits were already impossible twice over, which is what makes this safe:
`MeleeChildCollision.OnCollisionEnter` returns early when
`collision.transform.root == behaviour.rootObject.transform` — before it even increments its hit
counter, so a self-contact cannot burn through `hitsAmount` — and `MeleeWeapon.HitServer` checks
again. The blade is a **kinematic** Rigidbody and a CharacterController only moves under script
control, so the extra contacts carry no force in either direction; what changes is that the
callback now fires.

### Why the matrix change alone may not be enough (`MeleeSplit.cs`)

Opening the matrix only helps if a contact can be generated at all, and there is good reason to
doubt it: the blade carries a **kinematic** Rigidbody, and the pawn prefab contains **no
Rigidbody whatsoever** — 81 static colliders plus a CharacterController. Unity sends no
collision events between a kinematic body and a static collider. How vanilla lands melee on a
remote player under those rules is not a question the ripped project can answer: it has already
been caught losing the collision matrix and the crosshair's serialized RectTransform, so its
`isKinematic` flag is not evidence either.

So melee also gets an **overlap test** that reaches the same conclusion without those
preconditions. While a swing is live, the blade's bounds are tested against the layers local
players actually occupy (6 `Self` for the capsule, 16 `SelfBody` for the bone colliders) and any
local player found there is passed to the game's own `MeleeWeapon.HitServer` — the same call,
with the same arguments, the contact path would have made. Damage, knockback, hit markers and
headshot detection all stay the game's; only the detection is new.

Only **other local** players are considered. Remote players are reached by the vanilla contact
path, which works, and must not be hit twice.

Double-hits are prevented from both directions: each swing keeps a set of victims already
struck, the overlap skips anyone in it, and a prefix on `OnCollisionEnter` skips a contact whose
victim is in it too. Whichever path gets there first wins and the other stands down. The
`[melee] hit on P2 via overlap` / `via contact` line says which one did, which is also how we
find out whether the matrix change was needed at all. `[Splitscreen] MeleeOverlapFallback`
turns it off.

The F10 dump prints the relevant `Physics.GetIgnoreLayerCollision` rows, and the
`[damage] melee enabled against local players` line records what the rows were before the mod
touched them — AssetRipper did not recover the shipped matrix, so that log line is the only
record of it.

## Per-player settings (`PlayerSettings.cs`, `SettingsRouting.cs`, `SettingsUi.cs`)

Sensitivity used to belong to player 1 and to nobody else. `Settings` is a singleton, and its
`Update` pushes the look settings onto `Settings.localPlayer` — **one** pawn, which the mod
pins to player 1 — so players 2+ were left on the prefab's stock `lookSpeedX = 2f` with no way
to change it. Four people, one aim.

Now whoever opens the options screen is the one editing it.

### The borrow

Making the settings per-player by rewriting each read was not on: `Settings.Instance.<field>`
appears in the pawn, the crosshair, the damage flash, the weapon and the menu, with a different
shape at each site, and the next game update would add another.

So the singleton is *borrowed* instead. One invariant:

> At rest, `Settings.Instance` holds player 1's values.

and one exception: for the duration of a single call running on someone else's behalf — their
pawn's `Update`, their crosshair's `Update`, their damage flash, their pawn's spawn, or an
options handler they triggered — the singleton is loaded with **their** values and put back
afterwards. The game's own code then reads the right numbers without knowing anyone is there.

This is the same trick the mod already plays with `Settings.localPlayer` and `Crosshair.player`
around `PlayerSetup.OnStartClient`, generalised.

Every borrow is closed by a Harmony **finalizer**, not a postfix. A postfix is skipped when the
method it follows throws, and a skipped restore leaves one player's numbers in the singleton for
everyone else to read — settings that appear to "drift" onto whoever last touched them, with
nothing in the log to say why. `SettingsRouting.Tick` re-asserts the invariant every frame and
logs an error if it ever finds a borrow still open at a frame boundary.

### Who owns the menu

Latched when the screen opens, held until it closes. There is one EventSystem and one cursor, so
only one person is driving the menu anyway, and re-deciding on every stick flick would let a
bored player two seats over steal an edit halfway through.

- **In a match**, `PauseManager.menu` is a standalone serialized `InputAction` — not part of
  anybody's pinned action asset, so every controller can open the pause menu, and the callback
  context names the device that did. That is the exact answer to "who pressed Start", and it is
  what the prefix on `PauseManager.Menu` reads.
- **In the main menu** there is no such moment, so the owner is the player behind the most
  recent *button press* (controllers belong to whoever claimed them in the lobby; keyboard and
  mouse belong to player 1) at the instant the screen opens. Presses only — a stick rests near
  but not at zero, and any threshold either misses a gentle push or lets a drifting pad claim
  the menu from across the room.

`SettingsUi.IsOpen` is the edge. The options menu is toggled with plain `GameObject.SetActive`
*and* scaled up by a tween — `PauseManager.Awake` leaves it active at `localScale` zero — so the
test is "active **and** non-zero scale", which is a physical fact about anything on screen rather
than a guess about the scene. The in-match pause menu is SetActive and nothing else.

### The widgets

The game fills its sliders once, from a coroutine in `DisplayLoadValues.Start`, and never again —
with one player they cannot go stale. With four they have to be re-read whenever the screen
changes hands, or player 3 opens the menu, sees player 1's numbers, nudges a slider and inherits
them. `SettingsUi.Refresh` repaints them on every latch.

Which widget shows which setting is not guessed. `DisplayLoadValues` already tags itself with a
private bool per setting, named exactly like the PlayerPrefs key — so one string is the storage
key, the widget tag, and the table row. Values go in with `SetValueWithoutNotify`: the ordinary
setter fires `onValueChanged`, which is wired to the very handler being intercepted, and a
repaint would register as twenty-two edits by the new owner.

For the same reason an edit only routes while a screen is open. `DisplayLoadValues` assigning
`Slider.value` fires the handler exactly like a drag does, and routing that would hand player 1's
saved values to whoever last opened the menu.

### What is per-player and what is not

The line is "would two people sharing one screen want different answers?".

**Per-player** (22 settings, one table row each in `SettingTable`): the five sensitivities and
the two-axis switch, invert X/Y, the sprint/aim/lean/crouch holds and reverse sprint, inverted
fire binding, FOV, brightness, damage-flash intensity, crosshair off, fixed crosshair,
speedometer, minimalist UI, motion blur.

**Global, untouched**: resolution, fullscreen, exclusive fullscreen, vsync, quality, frame cap,
every volume, voice chat, reduced VFX. There is one screen and one pair of speakers.

### Storage

Slot 0 keeps the game's own PlayerPrefs keys, un-suffixed. That is what makes this invisible to
a vanilla launch: player 1's settings are still *the* settings, in the prefs the game has always
written, so uninstalling loses nothing and installing changes nothing until a second player
joins. Everyone else gets `<key>.p2`, `.p3`, `.p4`, defaulting to player 1's current value for
anything never changed — so a player who has just joined starts out feeling like the host's game
rather than like the prefab's defaults.

Saved per *slot*, which behaves like a controller port: if the player in slot 3 leaves, slot 4
shifts down and keeps the settings they are playing with (those are in memory, and re-saved
under the slot they moved into). It is the saved file that belongs to the seat, not to the
person.

One handler quirk worth knowing, because it is easy to miss: every `Settings.Change*` ends with
`PlayerPrefs.SetFloat("mouseSensitivity", …)` — the *vanilla* key — so running one on player 3's
behalf writes player 3's number into player 1's saved settings. `SettingsRouting.Finish`
re-saves player 1 immediately afterwards to put that right.

`[Splitscreen] PerPlayerSettings` turns the whole thing off.

## Known gaps (in priority order)

1. **World audio is one player's** — the single `AudioListener` rule; see the audio section
   above for what is and is not achievable. Own-sounds are per-player; direction is not.
2. **Minimalist UI is player 1's** — the *setting* is now per-player, but what it switches to is
   not: `Settings.minimalistUi` swaps the pawn HUD for a scene canvas that only exists once. Any
   player who turns it on hides their own pawn HUD and gets no readout in return. Leave it off.
3. **Three and four local players are uncharted for the game itself.** `SpawnPlayer` branches
   on `SteamLobby.players.Count <= 2`; above that it runs 4v4 team spawn logic and
   `ScoreManager` team lookups that nothing here has exercised.
4. The kill cam's *pose* is still one camera's: every dead player spectates the same target
   from the same angle, and free cam / target switching answer to player 1's input. The view
   itself is now per-player — see the death-view section. Its *sensitivity* is player 1's for
   the same reason: there is one `KillCam`, so there is nobody else to read a setting for.
5. `PhysicsProp` barrel-throw reads the shared Interact action directly, and taunts and weapon
   sway read the legacy `UnityEngine.Input` API, which is global. Those three stay with
   player 1 and are skipped for everyone else.
6. Every player's name is player 1's Steam name (all parse the host connection's SteamID).
7. `SteamLobby.players.Count` includes the added players — a 2-max lobby is full as soon as
   one is added.
8. Adding a player mid-match leaves player 1's existing pawn on whatever asset it captured
   before; it corrects itself on the next round respawn, when the pawn goes through
   `SpawnPlayer` and picks up player 1's own action set.
9. Nobody sees their own torso/legs while sliding — vanilla shows them, but the per-camera
   rule hides your own body from your own camera unconditionally. Fixing it means excepting
   those three meshes while `isSliding`.
10. Shadows follow the same per-camera toggling, so your own body casts no shadow in your own
    view.

## What to look for on the first playtest

In rough order of likelihood to be wrong:

1. **Does each controller drive exactly one player?** Move one pad at a time. The `[input]`
   line names the assignment (`P1=[Xbox Controller] P2=[...]`); the F11 report's `[verdict]`
   confirms per player whether their actions resolve to their own pad and nothing else.
2. **Can each player see all the others, and only the others?** Own body invisible, no
   duplicate or floating arms in any viewport. This is the newly rewritten path — per-camera
   renderer toggling replaced the layer split, so it has never run in a real match.
3. **Does anyone flicker?** Restoring in `onPostRender` and re-asserting in LateUpdate should
   make it impossible; if it happens, the `[render]` watchdog names the mesh and the frame.
4. Do bullets register in every direction? Hitboxes now stay on vanilla layers, so this should
   be *more* correct than before, not less.
5. Does everyone respawn each round, or only the round they joined?
   (`GameManager_ObserversRoundSpawn_Patch`.)
6. Viewport aspect: cameras keep their stock FOV, so a half-height or quarter viewport is
   stretched until someone sets `fieldOfView`/aspect per viewport.
7. At three or four players, whether the game's own 4v4 spawn and team logic copes — see gap 3.

## Debugging

`[Debug] VerboseLogging = true` logs every `PlayerSetup.OnStartClient` with camera
rects/masks (`Patches/PlayerSetupPatch.cs` recon postfix). Read
`game\STRAFTAT\BepInEx\LogOutput.log`, or attach dnSpy (`tools/dnSpy/dnSpy.exe`,
doorstop `debug_enabled` already on, port 10000).

### Input forensics (v0.9.0) — "the keyboard drives both players"

The old logging reported the mod's *intent* (`P2 pawn spawned with gamepad-only controls`)
and never checked the result, so every way of getting this wrong produced an identical log.
`InputDiagnostics.cs` reports observed state only, and is built to tell three causes apart:

| | Cause | Proof it looks for |
|---|---|---|
| **A** | P2's pawn captured P1's action asset (the `InputManager.inputActions` swap missed it) | both pawns' cached `InputAction` objects are the *same instance*; or P1's asset lists both pawns as `MouseX.performed` subscribers |
| **B** | Assets are distinct, but P2's actions still resolve to keyboard/mouse controls | `action.controls` on P2 contains `Keyboard/…`, or `activeControl.device` is the keyboard |
| **C** | P2's actions read zero and are bound to nothing, yet P2 still moves | input isn't the channel — shared component reference, networked transform, or a legacy `UnityEngine.Input` read |

Four instruments, all in `[Debug]`:

- **`[capture]`** (`TraceInputCapture`, on by default) — a postfix on each component's `Awake`
  logs the asset it captured *and what the shared static held at that instant*. The whole
  design assumes every `InputManager.inputActions` read falls inside the swap window in
  `PlayerManager.SpawnPlayer`; this is the line that proves or disproves it. Any component
  logging `static=P1 captured=P1` on P2's pawn **is** cause A, with the culprit named.
- **`[spawncheck]`** — the `SpawnPlayer` postfix now reads the asset back off every input
  component of the pawn it just spawned and reports `OK`/`MISMATCH` per component. Fires
  before you can even see the symptom, and needs no gamepad.
- **F11 → `[asset]`/`[pawn]`/`[alias]`/`[verdict]`** — one-shot report. Every pawn component's
  asset (labelled `A0`, `A1`, … so a *third* unexpected instance is obvious), every tracked
  action's resolved controls and `activeControl`, and the `performed` subscriber list of each
  asset's `MouseX`/`MouseY` read out of the Input System's internals — each subscriber named
  down to its pawn, including leaked ones from destroyed pawns. Ends with a `[verdict]` line
  stating which cause the numbers support.
- **F12 → `[trace]`** — live trace, self-stopping after `InputTraceSeconds`. One line per pawn
  per frame in which anything happened, pairing what that pawn's own `move` action read with
  how far the pawn actually moved:

  ```
  [trace] P1 move=0.00,1.00 src=Keyboard/w mouseIn=(0.4, 0.0) posD=0.083 yawD=0.31
  [trace] P2 move=0.00,0.00 src=none      mouseIn=(0.0, 0.0) posD=0.083 yawD=0.31
  ```

  That pair is the whole point: **P2 with `move=0` and `posD≠0` is cause C** — no device
  filtering will ever fix it. P2 reading the same value as P1 is cause A or B, which the F11
  report then separates.

`[static]` warns whenever `InputManager.inputActions` is replaced outside the spawn swap
(`RestartEngine`, a rebind, a settings apply). Every live pawn keeps the old instance at that
moment, so the mod ends up filtering an asset nobody reads while P1's real one goes
unrestricted — a failure that changes nothing else in the log.

#### Procedure

1. Host a lobby, join a second player with X on their controller, start a match, **turn the
   trace on with F12 and move only player 1**,
   then only player 2. Press F11 while both are alive. Quit and read the log.
2. Read it bottom-up: `[verdict]` first, then `[alias]`, then the `[capture]` lines from the
   spawn that produced the live pawns.
3. A gamepad is *not* required to identify the cause. With none connected P2's asset gets an
   empty device list, so P2 should be completely deaf — if P2 still mirrors P1 under those
   conditions, that alone rules out cause B and points at A or C.

`LogKeyPresses` now defaults to **false**: the 2026-08-19 log confirmed the hotkey path
works, and the per-key spam drowns the trace.

### Render forensics (v0.9.1) — "only the head and arms are visible"

The eight third-person meshes are **indistinguishable in the prefab** — all siblings under
`Graphics/PF_Aboubi_04/SK_Aboubi_00`, all layer 3, all renderers enabled, all `activeSelf`:

```
SK_Aboubi00_Arm_Left00   SK_Aboubi00_Hand_Left00   SK_Aboubi00_Leg_Left00   SM_Aboubi_Head00
SK_Aboubi00_Arm_Right00  SK_Aboubi00_Hand_Right00  SK_Aboubi00_Leg_Right00  SM_Aboubi00_Torso
```

and the mod treats all eight identically. So nothing in the prefab or in the mod can explain
a *subset* going missing — whatever does it happens at runtime, and static reading cannot find it.
`RenderDiagnostics.cs` covers the only four ways a mesh that exists can fail to draw:

| | Cause | What the log shows |
|---|---|---|
| 1 | GameObject inactive | `activeSelf=False`, plus `hiddenBy=<the first inactive ancestor>` — which distinguishes "the torso is off" from "`PF_Aboubi_04` is off, taking seven meshes with it" |
| 2 | Renderer disabled | `renderer=enabled=False` |
| 3 | Layer culled by that camera | the per-mesh `-> P1:SEES P2:CULLS` verdict, computed against each owned pawn's real `cam[0]` mask |
| 4 | Draws but invisible anyway | `mat=<name>/<shader>`, `NULL MATERIAL`, `isVisible` |

- **F10** now prints the `[view]` + `[render]` snapshot next to the existing pawn dump: every
  body mesh with all four properties and an explicit per-viewer SEES/CULLS verdict, plus the
  first-person arm meshes listed separately (so "arms" is never ambiguous between
  `SK_Aboubi00_Arm_*` and `SK_FPArmAboubi00_Arm_*`).
- **`[render]` watchdog** (`WatchBodyVisibility`, on by default) samples those four properties
  every frame and logs only transitions, with `Time.frameCount`. This is the half that finds
  the culprit: the game disables these meshes in exactly one place (`PlayerSetup.OnStartClient`,
  owner branch), so a mesh going dark at any other moment is someone else's doing, and the
  frame number ties it to whatever else is in the log around it. It also logs a `first seen`
  line per mesh — a mesh already off before the watchdog ever sampled it produces no
  transition, and that case (game disabled it at spawn, the mod's re-enable never stuck) would
  otherwise be invisible.

It samples in `LateTick` **before** `PlayerVisibility.ReassertBodyVisibility()` runs, so if the game
switches a mesh off and the mod switches it back on up to 0.25 s later, both edges appear and
the fight is legible instead of averaging out into "sometimes invisible".

### Grenade pickup watch (v0.26.0) — "picking a grenade up reveals something invisible" **[TEMPORARY]**

> **Bug found (v0.27.0)** — it was `PlayerVisibility` force-enabling the grenade's authored-off
> `LineRenderer`; see "Solved: picking up a grenade lit up a line across the screen" above. This
> watch is kept only until the fix is confirmed in play, then deleted.

`GrenadeDiagnostics.cs` + `Patches/GrenadeWatchPatches.cs`. **Delete both when the bug is
fixed** — the checklist is in the doc comment on `GrenadeDiagnostics`, and `grep gwatch` finds
every reference.

The report is "an object that is usually not visible becomes visible when I pick up a
grenade", and nothing in the mod names that object. There are only five ways a thing goes from
unseen to seen, so the watcher tracks all five for **every Renderer and Light in the scene**:

| | Cause | What the log shows |
|---|---|---|
| 1 | GameObject activated | `active off->on` / `activeSelf off->on` |
| 2 | Renderer or Light enabled | `enabled off->on` |
| 3 | Layer changed onto a visible one | `layer 8->0` — the likely one: `ItemBehaviour.SetLayer()` rewrites the item's whole subtree to layer 8 on pickup, layer 0 on drop, and `SetLayerAllChildren` **returns** (not `continue`s) on the first child tagged `vfx`, so the walk can abort part way |
| 4 | Reparented out of a hidden rig | `reparented` — e.g. `DualLauncher` unparents `douilleGrenade` when the pin comes out |
| 5 | It did not exist a frame ago | `CREATED (renderer) active=on enabled=on layer=8` |

A transition from not-drawable to drawable is flagged `<== BECAME DRAWABLE`, which is the line
to search for.

- The picture is maintained continuously (that is what makes a "before" state exist) but
  printed only during a window around an item interaction — `GrenadeWatchSeconds`, default 4 s,
  restarted by each new event, so pick up → pull pin → throw is one continuous window.
- Events come from two independent sources on purpose, so a mistake in either still leaves a
  working tool: Harmony postfixes on `ItemBehaviour.OnGrab` / `OnDrop` / `SetLayer`, and a poll
  of what each local player is actually holding. The poll also catches the two things a patch
  cannot — a hand emptied by the game itself (`RightHandFix` drops a weapon on its own) and the
  pin coming out, which is a SyncVar flip with no method worth patching.
- `GrenadeWatchAllPickups` (default on) watches every item, not just grenades. The control
  group is the point: whatever changes for a grenade **and** for a pistol is not the bug.
- Sampled in both `update` and `late` phases with separate histories, for the reason the body
  watchdog above learned: a single sample per frame reported meshes as steady while the screen
  was visibly flashing.
- `Resources.FindObjectsOfTypeAll`, not `FindObjectsOfType`: the object being hunted is by
  definition normally invisible and may sit on a deactivated GameObject, which the ordinary
  lookup skips entirely. Scene-filtered, so prefab assets do not come along.
- Each window closes with a ranked list of everything that moved; **F10** prints the same
  ranked list cumulatively, across every interaction since load.

### Scope reticle watch (v0.28.0) — "scoped rifles show no reticle when you aim" **[TEMPORARY]**

`ScopeDiagnostics.cs` + `Patches/ScopeWatchPatches.cs`. **Delete both when the bug is fixed** —
the checklist is in the doc comment on `ScopeDiagnostics`, and `grep swatch` finds every
reference.

The report is that M2000, HKG-11 and similar scoped weapons show no reticle when aiming down
sights. That reticle is not drawn by the weapon; it is one sprite swap at the end of
`Crosshair.Update`, gated behind a six-term conjunction — and every term is a plain field read,
so the watcher samples all six per player per frame and **names the first one that is false**:

| | Term | What a failure means |
|---|---|---|
| 1 | `playerPickupScript.SyncAccessor_hasObjectInHand` | the SyncVar has not reached this client |
| 2 | `player.gameObject.activeSelf` | the pawn is switched off |
| 3 | `canScopeAim` | `ItemBehaviour.Update` wrote it to another player's crosshair, or its `Update` is not running (it returns early when `rootObject.layer == 3`) |
| 4 | `aimCrosshair != null` | `ItemBehaviour.OnGrab` did not reach this crosshair |
| 5 | `player.isAiming` | not aiming |
| 6 | `playerCamera.fieldOfView < normalFovValue - aimCrosshairLimit` | the zoom has not landed, or `zoomFOV` is wrong |

Getting the right sprite onto the right `Image` is necessary but not sufficient, so the watcher
separately measures whether that `Image` still has **area on screen** and reports the two
verdicts apart:

- `HIDDEN: <term>` — the conjunction failed; term 1–6 above.
- `DRAWN BUT INVISIBLE: <reason>` — the sprite is correct but the object is inactive, the
  component disabled, the alpha zero, or **the rect collapsed to 0x0**.

That last one is the one to watch for. The scene ships `ScopeCrosshair` *stretched*
(`anchorMin (0,0)`, `anchorMax (1,1)`, `sizeDelta (0,0)`), and `CrosshairSplit.Place` collapses
its anchors to a point to centre it in a viewport — which turns "fill the parent" into a 0x0
rect. The ordinary crosshair survives identical treatment only because `Crosshair.Update`
rewrites its `sizeDelta` to 400x400 every frame; nothing does that for the scope image. The log
prints the measured rect in pixels next to the anchors and `sizeDelta` that produced it.

Two more things it calls out that no single term can:

- **`zoomFOV` widened at grab time.** `PlayerPickup` computes
  `zoomFOV = aimFOV + (canScopeAim ? 0 : defaultFOV - 68)` reading `Crosshair.Instance` —
  the singleton, *unswapped* — so on a grab it answers with player 1's crosshair whoever is
  picking the weapon up. When `zoomFOV` does not match `aimFOV` the line says so, because
  term 6 is gated on exactly that.
- **A shared `scopeImage`.** Two players pointing at one `ScopeCrosshair` means the sibling
  rewire in `CrosshairSplit.EnsureRig` did not take, and only one of them can ever show a
  reticle.

- Events come from two independent sources, same as the grenade watch. The Harmony postfixes
  sit on `ItemBehaviour.OnGrab` and `ItemBehaviour.Update` — **postfixes** deliberately, since
  Harmony runs prefix → original → postfix → finalizer and `ItemBehaviour_Crosshair_Patch`
  swaps `Crosshair.Instance` in its prefix, so a postfix sees the singleton exactly as the
  game's own code just did. That is what makes `-> P2's own crosshair, correct` versus
  `<== WRONG CROSSHAIR` a real answer rather than a guess.
- Silent for unscoped weapons, and it prints on change rather than per frame, with a heartbeat
  every `ScopeWatchInterval` (default 1 s). The heartbeat is the point: a reticle stuck hidden
  would otherwise print once and then look identical to nothing happening.
- **F10** prints the current state of every player plus a tally of every verdict seen so far.
