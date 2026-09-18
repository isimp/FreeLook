using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FreeLook
{
    /// <summary>
    /// Free look: hold a key and the camera swivels around the head while the character keeps
    /// facing — and aiming — where it was. Everything the mod does lives in three Harmony patches;
    /// this class only holds the settings and drives the per-frame state.
    /// </summary>
    [BepInPlugin(Guid, "FreeLook", Version)]
    [BepInProcess("valheim.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "isimp.FreeLook";
        public const string Version = "0.1.0";

        public static ManualLogSource Log;

        // Set when a patch that free look cannot work without fails to apply. Checked by Enabled,
        // so a half-patched mod stays out of the way instead of eating the mouse for nothing.
        private static bool _broken;

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<KeyCode> _key;
        private static ConfigEntry<bool> _blockInBuildMode;
        private static ConfigEntry<float> _maxYaw;
        private static ConfigEntry<float> _maxPitch;
        private static ConfigEntry<float> _pitchCeiling;
        private static ConfigEntry<float> _softLimit;
        private static ConfigEntry<float> _returnTime;
        private static ConfigEntry<float> _sensitivity;
        private static ConfigEntry<bool> _fadeCrosshair;
        private static ConfigEntry<float> _crosshairAlpha;

        public static bool Enabled => !_broken && (_enabled == null || _enabled.Value);
        public static KeyCode Key => _key?.Value ?? KeyCode.LeftAlt;
        public static bool BlockInBuildMode => _blockInBuildMode == null || _blockInBuildMode.Value;
        public static float MaxYaw => _maxYaw?.Value ?? 135f;
        public static float MaxPitch => _maxPitch?.Value ?? 70f;
        public static float PitchCeiling => _pitchCeiling?.Value ?? 89f;
        public static float SoftLimit => _softLimit?.Value ?? 0.35f;
        public static float ReturnTime => _returnTime?.Value ?? 0.15f;
        public static float Sensitivity => _sensitivity?.Value ?? 1f;
        public static bool FadeCrosshair => _fadeCrosshair == null || _fadeCrosshair.Value;
        public static float CrosshairAlpha => _crosshairAlpha?.Value ?? 0.15f;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            _enabled = Config.Bind("General", "Enabled", true,
                "Turn free look off without removing the mod.");

            // A raw KeyCode read through legacy Input, not a KeyboardShortcut: a BepInEx shortcut
            // refuses to fire while any other keyboard key is held, and this one is meant to be
            // held down while you are running with WASD.
            _key = Config.Bind("General", "Key", KeyCode.LeftAlt,
                "Hold this to look around. On release, the camera returns to where the character faces.");

            // The same key is vanilla's AltPlace: the alternate placement modifier in build mode.
            // Free look there would freeze the placement ghost, which follows the look direction.
            _blockInBuildMode = Config.Bind("General", "BlockInBuildMode", true,
                "Disable free look while placing a building piece, so the key keeps its normal alternate placement function.");

            _maxYaw = Config.Bind("Look", "MaxYaw", 135f,
                new ConfigDescription(
                    "How far left or right you can glance, in degrees from where the character faces.",
                    new AcceptableValueRange<float>(10f, 180f)));

            _maxPitch = Config.Bind("Look", "MaxPitch", 70f,
                new ConfigDescription(
                    "How far up or down you can glance, in degrees away from where you were already looking.",
                    new AcceptableValueRange<float>(5f, 89f)));

            // Vanilla clamps its own pitch to +/-89 in Player.SetMouseLook. Free look adds to that
            // pitch rather than replacing it, so the sum is held inside the same bound and the view
            // can never reach an angle the stock camera would refuse.
            _pitchCeiling = Config.Bind("Look", "PitchCeiling", 89f,
                new ConfigDescription(
                    "Hard limit on the resulting up/down angle, counting the pitch you already had. Valheim's own limit is 89, which is also the maximum here.",
                    new AcceptableValueRange<float>(30f, 89f)));

            _softLimit = Config.Bind("Look", "SoftLimit", 0.35f,
                new ConfigDescription(
                    "Fraction of the travel nearest each limit over which the view slows down. 0 is a hard stop; 0.35 slows the last third.",
                    new AcceptableValueRange<float>(0f, 0.9f)));

            _sensitivity = Config.Bind("Look", "Sensitivity", 1f,
                new ConfigDescription(
                    "Multiplies mouse movement while free looking, on top of the game's own sensitivity.",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            _returnTime = Config.Bind("Return", "ReturnTime", 0.15f,
                new ConfigDescription(
                    "Seconds the camera takes to ease back to centre after you let go. 0 snaps back instantly.",
                    new AcceptableValueRange<float>(0f, 1f)));

            // While the glance is out, a crosshair sitting in the middle of the screen is telling
            // you something that is no longer true: the shot still goes where the character faces.
            _fadeCrosshair = Config.Bind("Crosshair", "FadeCrosshair", true,
                "Fade the crosshair as the camera turns away, since aim stays where the character faces.");

            _crosshairAlpha = Config.Bind("Crosshair", "CrosshairAlpha", 0.15f,
                new ConfigDescription(
                    "How much of the crosshair is left at a full glance, as a fraction of its normal opacity. 0 hides it completely.",
                    new AcceptableValueRange<float>(0f, 1f)));

            // Patched one class at a time rather than with PatchAll. Two of the three targets are
            // private methods found by name, so a game update that renames one would take the whole
            // plugin down with it; this way the crosshair can fail on its own and the mod that is
            // left still works, and a failure of the parts that matter turns free look off cleanly.
            _harmony = new Harmony(Guid);
            var swallowsInput = Apply(typeof(SetMouseLookPatch), "Player.SetMouseLook");
            var movesCamera = Apply(typeof(GetCameraPositionPatch), "GameCamera.GetCameraPosition");
            Apply(typeof(UpdateCrosshairPatch), "Hud.UpdateCrosshair");

            _broken = !swallowsInput || !movesCamera;
            if (_broken)
            {
                Log.LogError("FreeLook: free look is off for this session. The game's camera is untouched.");
                return;
            }

            Log.LogInfo($"FreeLook {Version}: free look on {Key}.");
        }

        private bool Apply(Type patch, string target)
        {
            try
            {
                _harmony.CreateClassProcessor(patch).Patch();
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"FreeLook: could not patch {target} — {ex.Message}");
                return false;
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        // Read the key and advance the ease-back here rather than in LateUpdate: the look delta
        // arrives in PlayerController.LateUpdate, so the mode has to be decided before that.
        private void Update()
        {
            Glance.Update(Time.deltaTime);
        }
    }
}
