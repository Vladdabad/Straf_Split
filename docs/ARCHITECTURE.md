# How STRAFTAT executes, and what 4-player splitscreen implies

## Boot sequence

```
Steam launches STRAFTAT.exe
  └─ UnityPlayer.dll starts
       └─ winhttp.dll (Doorstop) hijacks Mono startup BEFORE any game code runs
            └─ BepInEx.Preloader → loads BepInEx/plugins/*.dll
                 └─ your Plugin.Awake() runs, Harmony rewrites game methods in memory
       └─ Unity loads scene 0, game code starts running (already patched)
```

Harmony doesn't edit files on disk. It rewrites methods in memory at load time, which is why
`Assembly-CSharp.dll` in the game folder stays untouched and Steam's file verification passes.

## Match flow

1. **MainMenu scene** — `SteamManager` / Heathen initialise Steamworks
2. **Lobby** — `SteamLobby.cs` creates or joins a Steam lobby; `LobbyController` tracks members
3. **Transport** — FishNet `NetworkManager` runs over **FishySteamworks** (Steam P2P relay)
4. **Host** starts server *and* a local client; others connect as pure clients
5. **Map load** — `MapsManager` / `SceneLoader` / `SceneMotor` load one of the 375 map scenes
6. **Spawn** — the *server* spawns a player `NetworkObject` per connection and assigns ownership
7. **`PlayerSetup.OnStartClient()`** runs **on every client, for every player object**

Step 7 is the whole story. Each client receives all four player objects, and this single branch
decides which one is "you":

```csharp
if (!base.IsOwner) {  /* disable cameras, HUD, controls — it's someone else */ }
else               {  /* enable cameras, set Settings.Instance.localPlayer, wire HUD */ }
```

**"Local player" is defined by network ownership, not by anything local.** The game has no
concept of "player 2 on this machine." That single fact is what makes splitscreen hard, and
it's what the two architectures below deal with differently.

## Architecture A — true splitscreen (one process, 4 local players)

One game instance owns four player objects and draws four viewports. This is what Halo did.

What it requires:

- **`IsOwner` stops discriminating.** FishNet ownership is per-*connection*. Four players owned
  by one connection means `IsOwner == true` for all four simultaneously, so every
  `if (IsOwner)` in the codebase fires four times. Every one of them needs a "which local
  player?" parameter that doesn't currently exist.
- **Singletons must become per-player.** `Settings.Instance.localPlayer`,
  `Crosshair.Instance.player`, `PauseManager.Instance` (ammo text, HUD) all assume exactly one.
- **One `AudioListener`.** Unity permits a single active listener per process. Four-player audio
  needs a custom mixing approach.
- **Server-side spawning.** The server spawns one player per *connection*; it must be taught to
  spawn four for one connection.
- **RPC guards.** `OwnerMatches(conn)` and `conn.IsLocalClient` in every `RpcReader___` cannot
  distinguish four local players sharing a connection.

Realistically this is re-architecting the game's player model. Every one of the ~200 `IsOwner`
sites is a potential bug.

## Architecture B — four processes composited into one window

Four normal game clients, each rendering full-screen at quarter size, positioned into quadrants.
Each process is an ordinary client as far as the game is concerned.

What it requires:

- **Networking: already solved.** `FishNet.Transporting.Tugboat.Tugboat` ships in
  `FishNet.Runtime.dll` — verified as a complete class with real method bodies, not stripped.
  Tugboat is direct-IP UDP over LiteNetLib. Four instances can connect over `127.0.0.1` with
  **no Steam lobby involved at all**, sidestepping both Steam matchmaking and the
  "one Steam account can't run a game twice" problem for the *connection* itself.
- **Window composition.** Launch each instance borderless at quarter resolution and position it
  with Win32 `SetWindowPos`; or reparent all four into one host window with `SetParent`.
- **Input isolation.** Each process must listen to exactly one device and ignore the rest. This
  is the real engineering. Unity's Input System supports device filtering, but a background
  (unfocused) Unity window ignores input by default — that has to be defeated.
- **Steam init.** Four Steamworks inits under one account is the main unknown. If Tugboat carries
  all traffic, Steam may be stubbable for the three guest instances.
- **Cost:** 4× CPU/GPU. STRAFTAT is visually light, so this is likely fine.

Zero changes to singletons, ownership, `IsOwner`, RPCs, HUD, or audio — each process keeps its
own copy of everything. That is an enormous amount of work avoided.

## Recommendation

**Architecture B**, which is what you described. It converts a game-architecture rewrite into
three tractable, independent problems: process launching, window placement, and input routing.

None of those require Unity. All three are solvable from a BepInEx plugin plus a small launcher.

## Suggested order

1. **Spike the networking.** Get two instances connected over Tugboat/localhost with no Steam
   lobby. This is make-or-break — if it works, the project is viable. Do this before anything else.
2. **Window placement.** Two instances, borderless, side by side.
3. **Input isolation.** Bind one gamepad to each instance; defeat background-focus input loss.
4. **Scale to four.** Quadrants.
5. **Polish.** Hide redundant UI, unified launcher, per-instance audio balance.

Step 1 is where the risk lives. Everything after it is known-solvable engineering.
