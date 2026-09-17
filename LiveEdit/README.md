# LiveEdit

Changing the game while it runs, for the things a pak cannot reach.

Everything else in this repository edits files the game loads. That covers a great deal - meshes,
textures, the camera's resting values - but it stops at anything the game computes rather than
reads. The camera turning to follow the player is the example that forced this: the arm's rotation
is worked out in native code, so no value in any file can change it.

## Not an injector, despite being asked for as one

No DLL is loaded into the game. No code of ours runs inside it. Nothing is hooked.

`ReadProcessMemory` and `WriteProcessMemory` are enough for both of the things wanted - reading
health for a damage meter, writing a camera angle - and staying outside avoids the part that is
genuinely hard on this build. Minecraft Dungeons here is a packaged app running in an AppContainer:
loading a library into one means administrator rights and permission work on the file itself, while
opening a process for reading and writing is an ordinary request.

[UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) is the tool people reach for with Unreal games, and
it is an injected DLL. It would give far more - Lua scripting, a live property editor, an SDK
dump - and it needs the thing this avoids. Worth revisiting if external access proves too limited.

The price of staying outside is that a change has to be repeated rather than made once: with no
hook to sit in, a value the game recomputes must be written again each frame. At sixty times a
second for one float, that is affordable.

## Finding things without a binary to read

The usual way to find a value in a game's memory is to dump the class layout and walk pointers to
it. That is not available here - **the game's binary cannot be read from disk at all** on this
build, which was tried and refused by Windows. There is nothing to dump from.

It is not needed, because the numbers are already known. The camera's values were read out of the
pak: a spring arm 2450 long, seeking 2800, rotated -45 by 45. Five specific floats in a specific
arrangement is a fingerprint, and memory can be searched for it.

This is the same move that has worked throughout the project: the mesh vertices inside a cooked
asset were found by matching them against the bounds the file declared, rather than by walking a
layout. **Do not walk to the thing - recognise it.**

The fingerprint is read from the pak at run time rather than written down here, so a game update
that retunes the camera changes what is searched for without anybody editing this file.

## Why the camera first, and not the damage meter

Both were on the table. The camera is far the easier start:

- **It is one value on one object.** A damage meter has to find health on every enemy, follow them
  as they spawn and die, and attribute changes to the right source.
- **Its fingerprint is already known**, exactly, from the pak. Enemy health has no such anchor.
- **It verifies itself.** A wrong camera is visible in a second; a wrong damage number looks
  plausible for a long time.

What is learned here - attaching, searching, writing, holding a value against the game - is what
the damage meter needs anyway.

## Where it is now

`GameProcess` attaches and reads. `CameraLink` searches for the fingerprint. Nothing is written
yet, on purpose: the first question is whether this build can be opened at all.

```
MeshForge.exe live
```

with the game running. It attaches, reports whether Windows allowed it, searches, and lists every
place the camera appears to be. Several hits are expected and are not a fault - the values exist in
the loaded asset, in the class default, and in each live component made from it. Telling those
apart means changing one and seeing which moves the camera, which is the step after this.

## Injection was tried properly, and this build refuses it

Written down so nobody repeats it. Two methods, both failed, and the reason is the same.

**Proxy DLL** - `dwmapi.dll` beside the executable, the way UE4SS ships. The game died instantly
with `0xC0000005` and **no log at all**, meaning it never reached UE4SS's own first line.

**Runtime injection** after the main menu - `CreateRemoteThread` onto `LoadLibraryW`, which is what
the tools that do work on Unreal games use. `LoadLibraryW` returned **zero**. The game survived,
which is informative: the library was refused cleanly rather than crashing anything.

Everything that could have explained it away was checked and ruled out:

- **Not a signing block.** `GetProcessMitigationPolicy` reports `SignaturePolicy = 0` and
  `DynamicCodePolicy = 0`, so unsigned and dynamic code are both permitted.
- **Not missing dependencies.** UE4SS needs only the universal CRT, which is always present.
- **Not file permissions in the ordinary sense.** ALL APPLICATION PACKAGES and ALL RESTRICTED
  APPLICATION PACKAGES were both granted read and execute.

What is left is one structural difference between a DLL the game loads happily and ours:

```
XGamingRuntimeThunks.dll   S-1-19-512-4096:(OI)(CI)(RX)     present
UE4SS.dll                  -                                absent
```

`S-1-19-*` is a Windows **trust label**. Packaged apps carry them, and a process can refuse to load
any binary without a matching one. `icacls` cannot grant it - *"no mapping between account names
and security IDs"* - because trust labels are applied by the packaging system rather than by
ordinary file permissions.

That accounts for both failures exactly: a hard crash when the loader tried to bring the file in
during startup, and a clean refusal when asked afterwards.

Reports that the Universal Unreal Engine Unlocker supports this game most likely predate this GDK
build, or apply to a different distribution of it.

## What does work, and what is left

**The live camera can be reached, and it can be driven.** This is settled, and it is settled
without injecting anything.

Everything before this searched memory for numbers that looked like a camera, and it never worked.
The authored values - 2450 back, 2800 seeking, minus 45 by 45 - exist in the loaded asset and in
every class default as well as in the live component, all identical, and writing to the wrong one
changes nothing on screen. No amount of searching harder separates copies that are the same.

The SDK dump ended it. With the engine's own layout in hand, the camera has an address rather than
a fingerprint, and the walk to it is seven pointers with nothing guessed:

```
GWorld                              image + 0x04795230
  UWorld.OwningGameInstance                 + 0x0160
  UGameInstance.LocalPlayers[0]             + 0x0038
  UPlayer.PlayerController                  + 0x0030
  APlayerController.AcknowledgedPawn        + 0x03B0
  APlayerCharacter.CameraSpringArm          + 0x0F50
```

It resolves, and the thing at the end proves it is the live one rather than another template: its
`TargetArmLength` reads **2800**, not the authored 2450, because the arm has already eased out to
the length it seeks. That value only exists at runtime. Every earlier search failed precisely
because it was looking for 2450.

### The field no dump names

Writing the camera's distance through the named fields looks like it works and then unwinds over
about three seconds. Neither of them is the control:

- `TargetArmLength` (+0x258) is the *output*. The component recomputes it every tick, so writing
  it sets the thing that is about to be overwritten.
- `SeekArmLength` (+0x2E0) is not what it eases towards. Held at a new value, the camera slid back
  to where it started anyway - which is what ruled it out.

The real control is at **+0x2E8**, inside the 24 bytes Dumper-7 labels `Pad_2E8` and cannot name.
Reflection only sees properties the game marked up, so a native private member is invisible to it;
this is where `SetDesiredArmLegnth` puts its argument. Written once it stays, and the camera eases
to it:

| desired (+0x2E8) | where the arm settled |
|---|---|
| 900 | 1053 |
| 500 | 544 |
| 6000 | 5562 |
| 2800 (stock) | back to stock |

The angle is simpler. `RelativeRotation` at **+0x170**, inherited from `USceneComponent`, reads
back the pak's own `-44.99997329711914 / 44.99997329711914` to the last decimal, and nothing
recomputes it - a write holds until something else changes it.

### Two things the first working build got wrong

Both found by someone using it, and worth writing down because they are different kinds of
mistake.

**"Camera turns to face aim" was never written.** It is `bUseControllerRotationYaw`, and it lives
on the *pawn* at `+0x338` bit 1 - not on the spring arm, where everything else here lives. The
panel offered the setting and the code walked straight past it. It is also the half that makes
"camera follows player" mean anything: a camera told to inherit its character's yaw inherits
nothing if the character never turns, and in a game played from above only the mesh swings round
to face the cursor while the actor underneath holds still.

**The angle stopped working the moment the camera was told to follow.** Not a bug in the writing -
the spring arm reads its yaw from two different places:

```cpp
FRotator DesiredRot = GetComponentRotation();            // the world transform
if (!bInheritYaw) DesiredRot.Yaw = RelativeRotation.Yaw; // the field being written
```

With the inherit flags off it reads `RelativeRotation`, so writing that is enough and always was.
Turn inheriting on and it reads the world transform instead - which writing a field in memory does
not recompute, because the engine only refreshes it inside `SetRelativeRotation`. The value
changed and nothing moved.

So the world rotation is composed from the parent's and written too, at `ComponentToWorld.Rotation`.
That offset is **deduced rather than dumped**: it is not a marked up property, so it has no name in
the SDK, and `+0x190` is the only 16 byte aligned place a 48 byte transform fits inside the pad
that ends where `ComponentVelocity` begins. Shipped with a guard that reads those four floats and
refuses to write unless they are a unit quaternion - four floats whose squares sum to one are not
what arbitrary other state looks like, so a wrong offset declines rather than corrupts.

Confirmed correct in the game. The guard stays anyway: it costs one read, and it is what makes a
future build fail safely instead of writing sixteen bytes into the wrong place.

### It takes sustained control, not just a nudge

Driven at frame rate as mouse-look would need: **353 writes over six seconds, none refused**, a
smooth 360 degree sweep with the pitch rocking underneath it. That is the whole mechanism for
third-person, and it needs no code inside the game.

Which retires the blocker this file spends three sections on. Injection is not needed for the
camera at all - not the proxy that killed the game, not `CreateRemoteThread`, not the manual
mapping that was ruled out. Reading and writing another process's memory is an ordinary thing a
program may ask to do, and it is enough.

What is left is the input half: mouse movement has to come from somewhere, and the game still owns
the keyboard. That is the next thing, and it is no longer waiting on anything.

## The decision: Steam, not manual mapping

Manual mapping - building the library in the game's memory by hand so the loader is never asked,
and the trust label never checked - was considered and **ruled out by the project owner**. It is
not to be revisited.

The route instead is the Steam build of the game, which is an ordinary Win32 application rather
than a packaged one. That removes the cause rather than working around it: no trust label, no PICE,
`LoadLibraryW` works, and - more importantly than the injection - **the binary can be read from
disk**, which is what makes an SDK dump possible. Every clumsy thing in this file exists because
that was not possible.

What to check first on a Steam install, before building anything on it:

1. **The pak encryption key.** Everything already shipped - recolouring, weapon import, the camera
   tab - decrypts the paks with the key in `Secrets`. Almost certainly the same build and the same
   key, but if not, those features need a new one before anything else matters.
2. **Save location.** Characters currently live under an Xbox user id in `%LOCALAPPDATA%\Dungeons`.
   A Steam install may identify differently, and if it does they need migrating rather than
   recreating.
3. **Injection.** Only once the first two are known, because they decide whether the existing
   features still work at all on that install.

## Where it is going

Into the editor as a tab, so there is one application to deal with: MCD Reborn watches for the game
starting, attaches itself, and offers the live settings alongside the ones written into paks.
Nothing separate to run.
