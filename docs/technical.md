# Technical notes

## How it works

Valheim's camera is derived entirely from the player's eye transform. `GameCamera.GetCameraPosition` places the camera behind the eye, points it along the eye's rotation, and runs wall collision, near clipping, the water clamp and ship tilt from there.

While the key is held, FreeLook takes the look input on its way into `Player.SetMouseLook` and spends it on two offset angles instead of on the character. The character's facing and the eye are never written, so the body stands still and aim, interaction and the build ghost stay where they were.

For the length of one camera call per frame, FreeLook gives `GameCamera` a rotated eye built with the game's own formula, with the offset inserted between its yaw and pitch, and then restores the real one. Orbit, collision and tilt remain the game's own. The combined angle is kept inside the range the game allows, and `SoftLimit` makes the last part of the travel toward each limit stiffer.

The key is read as a plain key rather than a BepInEx shortcut, because a shortcut does not fire while any other key is held.

## Limits

Chairs and beds give the camera a fixed transform of their own and skip the code FreeLook hooks, so it does nothing there. The key is keyboard only, although a gamepad's right stick steers the view while it is held. A mod that rewrites the eye rotation itself will have its change ignored during that one camera call.

## Building

Requires the .NET SDK 8 or newer. To build against the reference stubs in `lib/`, with no game installation needed:

```
dotnet build -c Release -p:LibsDir=lib
```

To build against a local installation and copy the result into a BepInEx profile:

```
dotnet build -c Release -p:ValheimDir="<Valheim folder>" -p:ProfileDir="<profile folder>"
```

The `VALHEIM_DIR` environment variable can be used instead of `ValheimDir`. Without these, a default Steam installation and a default Gale profile are assumed.

The files in `lib/` contain metadata only: every method body is replaced and resources are removed, so they can be compiled against but not run. Regenerate them after a game update with `tools/strip-references.ps1`. Releasing is described in [releasing.md](releasing.md).
