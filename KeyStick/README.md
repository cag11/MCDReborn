# KeyStick

WASD for Minecraft Dungeons, which does not have it.

## Why this exists at all

The game cannot be played with the keyboard, and that is not a setting anybody has overlooked:

- Its own config binds W, A, S and D **twice** - once as actions, once as axes called `MoveForward`
  and `MoveRight`.
- Both do nothing.
- Its keybinding screen has **no Move entry** to change.

Those bindings were never connected to anything. So there is nothing to repair and nothing a mod
pak can add, because what is missing is code rather than data. Editing the game's `Input.ini` does
not help either - it was tried.

What the game reads faultlessly is a **gamepad stick**. Four axes, `MoveForwardGamepad` and the
rest, wired to a left stick and working perfectly.

So this stops arguing with the game and hands it a controller.

## What it does

Presents a virtual Xbox pad to Windows through **ViGEmBus**, and pushes your keys into it as stick
deflection. The game sees an ordinary controller.

Nothing is injected. No process is opened. Nothing belonging to the game is modified or replaced.
A game patch cannot break it, because the game never learns this is here.

| key | does |
| --- | --- |
| W A S D | move |
| mouse left | attack |
| mouse right | second attack |
| mouse thumb | dodge |
| Space | interact, skip, dodge forward |
| 1 2 3 | artifacts |
| Q E | shoulder buttons |
| I / Tab | inventory, back |
| V | emote |
| Escape | pause |

The layout is taken from the game's own config rather than invented. Its keyboard bindings say what
each action is meant to be reached by - `MainAttack = LeftMouseButton`, `Dodge = ThumbMouseButton`,
`Use Item Slot 1 = One` - and its gamepad bindings say which part of a pad it listens to for the
same things. Matching the two gives a layout the game was designed around.

**The buttons are insurance, not necessity.** The keyboard already works for all of that on its
own; only the movement axes are dead. They are mapped here in case the game stops reading the mouse
and keyboard once a pad appears. Pass `--move-only` to send nothing but the stick.

## Parked, and why

**It works, and it is not usable.** The stick moves the character correctly. The problem is what
happens next.

The game decides which input mode it is in from whatever device was used last. Moving needs the
stick and aiming needs the mouse, so doing both makes it flip between controller and
mouse-and-keyboard continuously - and that flipping costs frames. Tested and confirmed in game.

There is no way out of it from here:

- The pad has no aim axis to move aiming onto. The right stick is **roll direction** in this game,
  not a camera, which follows from the camera being fixed.
- Suppressing the mouse so only the pad is seen would mean a system-wide input hook, and would
  leave no way to aim.
- Nothing in the game's config locks the input mode.

So the conflict is in the approach rather than in this code. Anyone picking this up again should
start by finding out whether the game's input-mode switch can be pinned - if it can, everything
here works as it stands.

Left in the solution and building. It is complete and tested up to the point above.

## Running it

```
KeyStick.exe
```

There is no camera-rotation axis to map the mouse to. The right stick is **roll direction** in this
game, not a camera - which follows from the camera being fixed in the first place.

Input is sent **only while Minecraft Dungeons is the window in front**, so the stick falls still
the moment you alt-tab. Pass `--anywhere` to send regardless, which is useful for testing with a
controller tester and unhelpful otherwise.

Ctrl+C stops it, and the controller disappears with it.

**ViGEmBus** has to be installed once, from
[its releases page](https://github.com/nefarius/ViGEmBus/releases). If it is missing, this says so
rather than throwing a COM error that names a device path and helps nobody.

## How it is put together

Four small pieces, because three details decide whether it reads as a stick or as a keyboard
pretending to be one:

- **`Stick.cs`** - the two that matter. **Diagonals** are brought back onto the rim, or holding W
  and D would be forty per cent faster than either alone, which is the classic bug of every
  homemade movement system. And a **ramp** carries the stick to full deflection over about eighty
  milliseconds, because a key goes from nothing to everything in one pass and a thumb cannot, so
  without it the character teleports into motion rather than accelerating.
- **`Keyboard.cs`** - key state by polling, not by a hook. A low level hook sees every keystroke on
  the machine, needs a message pump, and attracts the attention of security software, all to answer
  a question that can be asked directly a few times a pass. It also caches the foreground window's
  process, which is the frame-rate fix: naming the process behind a window means opening it, and
  doing that a hundred and twenty times a second costs a handle and an allocation each time -
  enough to take frames off the game being asked about. The window handle is free to read and only
  changes on alt-tab, so only that is checked every pass.
- **`VirtualPad.cs`** - the ViGEm wrapper. Centres the stick before disconnecting, since a pad that
  vanishes at full deflection can leave the value latched, and a character walking into a wall for
  ever is a puzzling way to learn that a program exited.
- **`Program.cs`** - the loop, at 120 Hz.

## What it is not

It does not make the camera follow you, and it cannot. That lives in `LovikaSpringArmComponent`,
which computes its own rotation in native code and ignores what the asset says about inheriting -
proved by setting the inherit flag, watching it read back as set, and watching the camera sit
still. The camera's **distance, angle, field of view, pivot and wall collision** are all editable,
and that is what the editor's Camera tab does.
