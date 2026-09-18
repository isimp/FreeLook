using HarmonyLib;
using UnityEngine;

namespace FreeLook
{
    /// <summary>
    /// The whole state of a glance: two angles away from where the character is facing, and
    /// whether the key is down. Nothing here touches the game — the patches read these.
    /// </summary>
    public static class Glance
    {
        /// <summary>True while the key is held and the game is in a state that can be looked at.</summary>
        public static bool Active { get; private set; }

        /// <summary>Degrees right of where the character faces.</summary>
        public static float Yaw { get; private set; }

        /// <summary>Degrees below where the player was already looking, matching Valheim's sign.</summary>
        public static float Pitch { get; private set; }

        private static float _yawVel;
        private static float _pitchVel;

        // Below this the offset is not worth a transform write, and SmoothDamp would take forever
        // to reach zero exactly.
        private const float Negligible = 0.01f;

        /// <summary>True while the camera is turned away from centre at all, easing back included.</summary>
        public static bool HasOffset =>
            Active || Mathf.Abs(Yaw) > Negligible || Mathf.Abs(Pitch) > Negligible;

        // A glance this wide counts as fully turned away. Small enough that a real look reaches it
        // quickly, wide enough that a twitch does not flicker the crosshair.
        private const float FullGlance = 12f;

        /// <summary>
        /// How far the view has turned from where the character faces, 0 to 1. Follows the ease
        /// back to centre on its own, so anything fading with the glance fades back with it too.
        /// </summary>
        public static float Turned =>
            Mathf.Clamp01(Mathf.Max(Mathf.Abs(Yaw), Mathf.Abs(Pitch)) / FullGlance);

        // m_lookPitch is private; everything else the mod needs is public on Character/Player.
        // Resolved inside a try rather than directly in the initialiser: a game update that renamed
        // it would otherwise throw out of the type initialiser on every frame that touched this class.
        private static readonly AccessTools.FieldRef<Player, float> BasePitch = ResolveLookPitch();

        private static AccessTools.FieldRef<Player, float> ResolveLookPitch()
        {
            try
            {
                return AccessTools.FieldRefAccess<Player, float>("m_lookPitch");
            }
            catch (System.Exception ex)
            {
                // Losing it costs only the share of the up/down limit already spent by the stock
                // look, so the glance stays inside its own limit and the game keeps running.
                Plugin.Log?.LogWarning($"FreeLook: Player.m_lookPitch could not be read ({ex.Message}); the up/down limit will not account for the look you already had.");
                return null;
            }
        }

        /// <summary>The up/down angle the character already had, before any glance is added.</summary>
        public static float PitchOf(Player player) => BasePitch != null ? BasePitch(player) : 0f;

        public static void Update(float dt)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                // Between worlds there is nothing to return to; forget the glance rather than
                // ease it out against a player that no longer exists.
                Active = false;
                Yaw = Pitch = 0f;
                _yawVel = _pitchVel = 0f;
                return;
            }

            Active = Plugin.Enabled
                     && Input.GetKey(Plugin.Key)
                     && !player.IsDead()
                     && !(Plugin.BlockInBuildMode && player.InPlaceMode())
                     && !Blocked();

            if (Active) return;

            if (Mathf.Abs(Yaw) <= Negligible && Mathf.Abs(Pitch) <= Negligible)
            {
                Yaw = Pitch = 0f;
                _yawVel = _pitchVel = 0f;
                return;
            }

            var time = Plugin.ReturnTime;
            if (time <= 0f)
            {
                Yaw = Pitch = 0f;
                _yawVel = _pitchVel = 0f;
                return;
            }

            Yaw = Mathf.SmoothDamp(Yaw, 0f, ref _yawVel, time, Mathf.Infinity, dt);
            Pitch = Mathf.SmoothDamp(Pitch, 0f, ref _pitchVel, time, Mathf.Infinity, dt);
        }

        /// <summary>
        /// Takes the look delta the game would have applied to the character and spends it on the
        /// camera instead. The signs match Player.SetMouseLook, so the game's own sensitivity and
        /// invert settings carry over untouched.
        /// </summary>
        public static void Accumulate(Vector2 mouseLook, Player player)
        {
            var sens = Plugin.Sensitivity;

            Yaw = Advance(Yaw, mouseLook.x * sens, -Plugin.MaxYaw, Plugin.MaxYaw);

            // The glance is added to the pitch the player already had, so the limit has to be
            // shared with it: near the top of the stock range there is less glance left to give.
            // Max/Min against zero keep the clamp from ever forcing a non-zero offset, which would
            // move the camera the moment the key went down.
            var basePitch = PitchOf(player);
            var ceiling = Plugin.PitchCeiling;
            var high = Mathf.Min(Plugin.MaxPitch, Mathf.Max(0f, ceiling - basePitch));
            var low = Mathf.Max(-Plugin.MaxPitch, Mathf.Min(0f, -ceiling - basePitch));

            Pitch = Advance(Pitch, -mouseLook.y * sens, low, high);
        }

        /// <summary>
        /// Moves an angle by a delta, slowing as it runs out of travel in the direction it is
        /// going, so a limit is reached gradually. Movement back towards centre is never slowed,
        /// and the hard clamp still applies.
        /// </summary>
        private static float Advance(float current, float delta, float low, float high)
        {
            if (delta == 0f) return Mathf.Clamp(current, low, high);

            var edge = delta > 0f ? high : low;
            var room = edge - current;

            // No travel left this way. The pitch limits move with the head, so an offset can find
            // itself outside them without ever having been moved there.
            if (room * delta <= 0f) return Mathf.Clamp(current, low, high);

            var zone = Plugin.SoftLimit;
            if (zone > 0f)
            {
                var band = Mathf.Abs(edge) * zone;
                if (band > 0f)
                {
                    delta *= Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Abs(room) / band));
                }
            }

            return Mathf.Clamp(current + delta, low, high);
        }

        /// <summary>
        /// States where the mouse belongs to something other than looking around. Each of these is
        /// a public accessor the game already offers; none of it is guessed from input.
        /// </summary>
        private static bool Blocked()
        {
            if (InventoryGui.IsVisible()) return true;
            if (StoreGui.IsVisible()) return true;
            if (Menu.IsVisible()) return true;
            if (Console.IsVisible()) return true;
            if (TextInput.IsVisible()) return true;
            if (Minimap.instance != null && Minimap.IsOpen()) return true;
            if (Hud.InRadial()) return true;
            if (Chat.instance != null && Chat.instance.HasFocus()) return true;

            // The barber has its own claim on the look delta: Player.SetMouseLook spends it turning
            // the character on the stool and refuses to let it past the halfway point. Swallowing
            // the delta here would leave you unable to turn your head around at all.
            if (PlayerCustomizaton.IsBarberGuiVisible()) return true;

            // The debug fly camera runs its own rotation off m_freeFlyYaw/Pitch and never asks the
            // eye, so a glance there would do nothing visible while still eating the mouse.
            if (GameCamera.InFreeFly()) return true;

            return false;
        }
    }
}
