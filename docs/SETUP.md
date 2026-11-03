# STRAFTAT modding workspace

Game: `D:\Straftat splitscreen\game\STRAFTAT` (Steam appid 2386720)
Engine: **Unity 2021.3.45f2**, **Mono** scripting backend (not IL2CPP — so real IL, clean decompile)

## Two copies of the game — mod the one on D:

| | |
|---|---|
| `C:\Program Files (x86)\Steam\steamapps\common\STRAFTAT` | Steam's install. **Never touch it.** Pristine reference and the source for re-copies. |
| `D:\Straftat splitscreen\game\STRAFTAT` | Byte-identical copy + BepInEx + our plugin. Everything gets modified here. |

An earlier attempt patched the Steam install in place, which Steam then clobbered on the next
update/verify (and takes the mod with it). The copy exists so that never happens again: patched
`Assembly-CSharp.dll`s survive, and "Verify integrity" is always available as a clean rollback.

Re-copy from Steam at any time — this mirrors, so it also wipes BepInEx out of the copy:

```
robocopy "C:\Program Files (x86)\Steam\steamapps\common\STRAFTAT" "D:\Straftat splitscreen\game\STRAFTAT" /MIR /MT:16
```

To refresh only the game files and keep the mod, drop `/MIR` for `/E /XD BepInEx /XF steam_appid.txt winhttp.dll doorstop_config.ini`.

The copy carries a `steam_appid.txt` (2386720) so Steamworks initialises when you launch
`STRAFTAT.exe` directly rather than through Steam. Steam must still be running and logged in.

## Layout

| Folder | What it is | Compiles? |
|---|---|---|
| `decompiled/` | C# recovered from `Assembly-CSharp.dll` + `-firstpass.dll` (692 files) | ⚠️ firstpass yes, Assembly-CSharp no |
| `UnityProject/ExportedProject/` | AssetRipper output — 375 scenes, 658 prefabs, 3010 materials, 8564 meshes (22.9 GB) | ✅ scripts compile, still won't play |
| `mod/StraftatSplitscreen/` | BepInEx 5 + Harmony plugin — **this is what you actually ship** | ✅ yes |
| `game/STRAFTAT/` | Copy of the Steam install, BepInEx-patched — **run this one** | — |
| `tools/dnSpy/` | dnSpyEx 6.6.0, prebuilt — decompiler + live Mono debugger | — |
| `dnSpy-master/` | dnSpy source tree, unbuilt. Not needed; use `tools/dnSpy/` | — |
| `tools/AssetRipper/` | AssetRipper 1.3.14 | — |
| `tools/game-backup/` | `winhttp.dll` / `doorstop_config.ini` from the pre-copy BepInEx setup | — |

Unity editor: `D:\Unity\2021.3.45f2`

## Reading the code

Open `decompiled/STRAFTAT.sln` in VS Code. Retargeted to `net472` with references resolving
against the game's `Managed/` folder, so IntelliSense and go-to-definition work.

`Assembly-CSharp-firstpass` builds clean. `Assembly-CSharp` still reports ~651 errors, all of
them syntax: ILSpy emitted FishNet/compiler-generated members whose IL names are not legal C#
identifiers (`NetworkInitializeEarly_..._Assembly-CSharp.dll`, `<PrivateImplementationDetails>`,
``Read___...List`1<System.Int32>...``). Those names have no valid C# spelling, so the project
will not compile without renaming them. It is still fine to read.

Two things that were wrong in the generated `.csproj`s and are worth not reintroducing if you
re-run the decompiler:

- `<AssemblySearchPaths>{HintPathFromItem}</AssemblySearchPaths>` hides `{TargetFrameworkDirectory}`,
  so `mscorlib`/`System`/`System.Core` never resolve and you get ~24,000 bogus CS0518s.
- The `UnityEngine.dll` facade must be referenced, not just the `UnityEngine.*Module.dll`s.
  DOTween and friends were compiled against the facade; without it every `Vector3`/`Color`
  signature they expose fails with CS0012.

For call graphs use `tools/dnSpy/dnSpy.exe` → open `Assembly-CSharp.dll` → right-click a
member → **Analyze** ("Used By" / "Uses"). dnSpy can also attach to the running game:
set `debug_enabled = true` in `doorstop_config.ini`, launch, then Debug → Attach to Process.

## Building the mod

```
cd "D:\Straftat splitscreen\mod\StraftatSplitscreen"
dotnet build
```

**Close the game before building.** The deploy step copies over a DLL that a running
STRAFTAT has memory-mapped, and the copy fails with MSB3027 ("user-mapped section open")
after ten retries. The compile itself still succeeds — only the deploy fails.

The build auto-copies `StraftatSplitscreen.dll` into the **copy's**
`BepInEx\plugins\StraftatSplitscreen\` (`GameDir` at the top of the `.csproj` is the single
place that path is set). Launch by running `game\STRAFTAT\STRAFTAT.exe` directly — **not**
from Steam, which would start the unmodded C: install. Read `BepInEx\LogOutput.log`; the
console window is already enabled via `[Logging.Console] Enabled = true` in
`BepInEx\config\BepInEx.cfg`.

`debug_enabled = true` is already set in the copy's `doorstop_config.ini`, so dnSpy can
attach to the running game (Debug → Attach to Process) without editing anything first.

To get back to stock: delete `BepInEx/`, `winhttp.dll`, `doorstop_config.ini`,
`.doorstop_version`, `changelog.txt`, `steam_appid.txt` from the copy — or just re-mirror
from Steam with the robocopy line above.

## Opening the ripped project in Unity

1. Unity Hub → **Add** → **Add project from disk**
2. Select `D:\Straftat splitscreen\UnityProject\ExportedProject`
   — the folder containing `Assets/` and `ProjectSettings/`, **not** `UnityProject/` itself
3. Confirm the editor version reads **2021.3.45f2**. Do not let Hub upgrade it.
4. Open. **The first import takes 30–90 minutes** and pins your CPU. Don't interrupt it —
   a half-finished import leaves a corrupt `Library/` you'll have to delete and redo.

### Safe Mode has been fixed — don't re-rip over it

Out of the box AssetRipper's `Assets/Scripts/` did not compile (1830 errors) and Unity opened
in Safe Mode. That has been repaired in place; the scripts now compile and the editor loads
normally, so MonoBehaviour components and their `[SerializeField]` wiring are inspectable.

Nearly all of it was one root cause. STRAFTAT's `Assembly-CSharp.dll` is post-processed by
FishNet's ILPostProcessor, which injects a `sync___set_value_<field>(value, asServer)` method
for every `[SyncVar]`. AssetRipper recovered the *call sites* but not the *declarations*, and
inlined the body into the `SyncAccessor_<field>` property using `value` for **both**
parameters — which is why you saw `if (value || !base.IsServer)` comparing a float to a bool.
Repairs applied, all confirmed against `FishNet.Runtime.dll` by reflection:

- 42 `sync___set_value_*` methods re-declared across 17 files, and their `SyncAccessor_*`
  setters pointed at them (fixes CS1061 / CS0117 / CS0019 / CS1503)
- `ReadSyncVar___<Type>`'s index parameter widened by AssetRipper from `byte` to `uint`,
  which broke `delegate bool SyncVarReadDelegate(PooledReader, byte, bool)` — 17 signatures
  and 37 `case Nu:` labels corrected (CS0123)
- `RaycastHit`'s private backing fields swapped for public accessors (CS0122)
- `CompressionLevel` ambiguity between `UnityEngine` and `System.IO.Compression` (CS0104)

The re-declared method bodies are a faithful reconstruction of FishNet's codegen, not a
byte-exact recovery. They exist so the editor loads; do not treat them as authoritative
networking reference. They are commented as such at each site.

If you ever re-run AssetRipper, all of this comes back. Re-apply the bulk of it with:

```
python tools/fix-assetripper-syncvars.py          # dry run, prints what it would change
python tools/fix-assetripper-syncvars.py --apply
```

The four one-off fixes (RaycastHit, CompressionLevel ×2, and a `bool num2 != 0` in
`LargeRaycastGun.cs`) are not scripted — the errors name the exact lines.

To check script changes without waiting on the editor, `tools/verify-unity-scripts/` compiles
`Assets/Scripts/` against the project's own `Assets/Plugins/` DLLs in about two seconds:

```
dotnet build tools/verify-unity-scripts/verify.csproj
```

It targets `netstandard2.1` to match Unity's API compatibility level — target `net472` there
and you get phantom `MathF`/`Index`/`GetValueOrDefault` errors Unity never reports.

Editor-only halves of Odin Inspector, DOTween, Bakery, ProBuilder and FishNet's code
generator are still absent from a shipped build, so this remains a non-playable project.

### Scenes worth opening first

- `Assets/Scenes/MainMenu/` — menu flow, where a "local co-op" entry point would go
- `Assets/Scenes/TrainingRange/` — smallest gameplay scene, best for experimenting
- `Assets/Scenes/Toilets_00.unity`, `Impasse01.unity` — small standard arenas
- Maps are grouped by author (`Dragonfly/`, `Ozan-Zone/`, `LDFest/`, `Sonova/`…);
  `_Alt` suffixes are variant layouts of the same map

### What this project is good for

- Reading level geometry and finding spawn points / trigger volumes
- Inspecting the player prefab's camera rig — which is exactly what splitscreen needs to duplicate
- Seeing what `[SerializeField]` fields are actually wired to in the shipped prefabs,
  which the decompiled C# can't tell you
- Material and shader setups

### What it is not

Not a buildable game. Do not expect Play mode, and do not plan to rebuild
`Assembly-CSharp.dll` from it. Gameplay changes go through the Harmony plugin.

## Splitscreen: where the seam is

> **Implementation has started — see `docs/SPLITSCREEN.md`** for the working
> architecture (second ClientInstance on the host's connection), the input-swap trick,
> and the current gap list. The analysis below is the original recon that led there.

`PlayerSetup.OnStartClient()` (`decompiled/Assembly-CSharp/PlayerSetup.cs`) branches on
`IsOwner`. The owner branch is the only place that enables cameras, wires the HUD, and sets
`Settings.Instance.localPlayer` / `Crosshair.Instance.player`. Everything a second local
player needs is downstream of that single branch.

Complications, roughly in order of difficulty:

- `Settings.Instance.localPlayer` and `Crosshair.Instance` are **singletons** — one local
  player is baked into the architecture. Second viewport needs per-player state.
- `PlayerSetup.Update()` force-disables `Camera.main` and its `AudioListener` every frame.
  Unity permits exactly one active `AudioListener`.
- `PauseManager.Instance` drives ammo text and HUD for a single player.
- Input: the game uses `Unity.InputSystem`. Splitting devices between two local players
  means per-player `InputUser`/device pairing rather than global input.
- Networking: FishNet ownership is per-connection. Two local players on one connection is
  not something the game's RPC guards (`base.IsOwner`, `OwnerMatches(conn)`) anticipate.

`tools/dnSpy/dnSpy.exe` → Analyze on `Settings.localPlayer` is the fastest way to find
everything that assumes a single local player.

## Legal note

The decompiled source and ripped assets are LemaitreBros' copyrighted work. Keep
`decompiled/` and `UnityProject/` local. Only `StraftatSplitscreen.dll` is yours to
distribute — that's the normal BepInEx mod model, and it's why mods ship as patches
rather than as modified game builds.
