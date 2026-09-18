using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace FreeLook
{
    /// <summary>
    /// Catches the look delta on its way to the character. PlayerController.LateUpdate has already
    /// applied mouse sensitivity, both invert settings, the gamepad stick and the menu gating by
    /// the time it gets here, so taking the value at this point costs none of that.
    ///
    /// Skipping the original is what keeps the character still: body facing follows m_lookYaw via
    /// Character.UpdateRotation, and aim, interaction and the placement ghost all follow m_lookDir
    /// and the eye. None of those are written while the glance is held.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.SetMouseLook))]
    internal static class SetMouseLookPatch
    {
        private static bool Prefix(Player __instance, Vector2 mouseLook)
        {
            if (!Glance.Active) return true;
            if (__instance != Player.m_localPlayer) return true;

            Glance.Accumulate(mouseLook, __instance);
            return false;
        }
    }

    /// <summary>
    /// Applies the glance to the camera by rotating the eye for the length of one call and putting
    /// it back straight after.
    ///
    /// GameCamera.GetCameraPosition derives everything from that transform — position is
    /// offsetedEyePos - eye.forward * m_distance, rotation is eye.rotation, and the wall collision,
    /// near clipping, water clamp and ship tilt all run off the result. Lending it a rotated eye
    /// therefore buys the correct orbit around the head with vanilla's own geometry, instead of a
    /// second implementation of it that would drift from the real one. Restoring in the postfix is
    /// what keeps the rest of the game looking where the character faces.
    /// </summary>
    [HarmonyPatch(typeof(GameCamera), "GetCameraPosition")]
    internal static class GetCameraPositionPatch
    {
        private static Transform _borrowed;
        private static Quaternion _saved;

        private static void Prefix()
        {
            // If the original ever threw, the postfix did not run. The next tick's UpdateEyeRotation
            // would heal it anyway, but not before this frame's camera read a stale eye.
            Restore();

            if (!Glance.HasOffset) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            var eye = player.m_eye;
            if (eye == null) return;

            // Vanilla's own formula (m_lookYaw * Euler(m_lookPitch, 0, 0)) with the glance inserted
            // between the two halves: yaw turns about the character's vertical, pitch about the
            // right axis that results. Head-anchored, no roll, and identical to stock at zero.
            var pitch = Mathf.Clamp(
                Glance.PitchOf(player) + Glance.Pitch,
                -Plugin.PitchCeiling,
                Plugin.PitchCeiling);

            _borrowed = eye;
            _saved = eye.rotation;
            eye.rotation = player.GetLookYaw()
                           * Quaternion.Euler(0f, Glance.Yaw, 0f)
                           * Quaternion.Euler(pitch, 0f, 0f);
        }

        private static void Postfix()
        {
            Restore();
        }

        private static void Restore()
        {
            // Unity's == is also true for a destroyed transform, so this drops a dead reference
            // rather than repeating a null.
            if (_borrowed == null)
            {
                _borrowed = null;
                return;
            }

            _borrowed.rotation = _saved;
            _borrowed = null;
        }
    }

    /// <summary>
    /// Fades the crosshair as the view turns away. Aim stays where the character faces, so a
    /// crosshair sitting in the middle of a swivelled screen is pointing at the wrong thing.
    ///
    /// Hud.UpdateCrosshair writes the colour every frame — hovering something, holding a bow and
    /// reading a sign each pick their own — so this runs after it and dims whatever it decided,
    /// rather than owning the value. Nothing to put back: the next frame writes it fresh.
    /// </summary>
    [HarmonyPatch(typeof(Hud), "UpdateCrosshair")]
    internal static class UpdateCrosshairPatch
    {
        private static void Postfix(Hud __instance)
        {
            if (!Plugin.FadeCrosshair) return;

            var turned = Glance.Turned;
            if (turned <= 0f) return;

            var keep = Mathf.Lerp(1f, Plugin.CrosshairAlpha, turned);
            Dim(__instance.m_crosshair, keep);
            Dim(__instance.m_crosshairBow, keep);
        }

        private static void Dim(Image image, float keep)
        {
            if (image == null) return;

            var colour = image.color;
            colour.a *= keep;
            image.color = colour;
        }
    }
}
