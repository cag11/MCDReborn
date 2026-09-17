using System;
using System.Collections.Generic;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Changing where the camera sits, as a pak rather than as a memory hack.
    ///
    /// The usual way to move this game's camera is to attach a debugger to the running process and
    /// write floats into it. That works, briefly: the camera is driven by native code that
    /// recomputes its position every frame, so a value written once is gone in about sixteen
    /// milliseconds and anything lasting means fighting the game's own tick sixty times a second.
    ///
    /// None of that is necessary. The camera's resting values - how far back the arm is, what
    /// angle it holds, the field of view - are ordinary tagged properties on a Blueprint's
    /// component templates, sitting in a pak file. They can be read, changed and shipped the same
    /// way a recoloured texture is: no process to attach to, no administrator rights, nothing to
    /// keep running, and deleting the pak is the whole of undo.
    ///
    /// What that buys is a different resting camera, not a live one. A slider that moves the view
    /// while you play is still the memory route; this is the setting the game starts from.
    /// </summary>
    public static partial class CameraMod
    {
        /// <summary>
        /// The assets carrying a camera, in the order they matter.
        ///
        /// The player character is the one that counts: it owns the spring arm the game actually
        /// looks through. `BP_CoopCamera` is a second camera used to frame several players at
        /// once, and it holds the same numbers - 2450 back at minus 45 degrees - which is exactly
        /// why it is so easy to change the wrong one and conclude the whole approach does not
        /// work. Both are changed together so that co-op does not snap back to the stock view.
        /// </summary>
        //Spelled as the game spells them. The workbench mounts its index case-insensitively and
        //the app mounts its own case-sensitively, so a lowercased path works in one and silently
        //finds nothing in the other - which is how this tab first shipped with no values in it.
        //Real case satisfies both.
        public static readonly string[] ASSETS = {
            "/Dungeons/Content/Actors/Characters/Player/BP_PlayerCharacter",
            "/Dungeons/Content/Actors/BP_CoopCamera",
        };

        /// <summary>
        /// Everything worth changing. Anything left null keeps the game's own value, so a mod that
        /// only widens the lens does not silently also straighten the angle.
        /// </summary>
        public sealed class Settings
        {
            /// <summary>How far back the camera sits. 2450 in the game.</summary>
            public float? ArmLength { get; set; }
            /// <summary>Where the arm reaches for when it is catching up. 2800 in the game.</summary>
            public float? SeekArmLength { get; set; }
            /// <summary>Degrees below horizontal. -45 in the game, which is the isometric angle.</summary>
            public float? Pitch { get; set; }
            /// <summary>Degrees around. 45 in the game.</summary>
            public float? Yaw { get; set; }
            public float? Roll { get; set; }
            /// <summary>Field of view in degrees. 55 in the game.</summary>
            public float? FieldOfView { get; set; }

            /// <summary>
            /// Whether the camera turns with the character.
            ///
            /// All three are off in the game, which is what makes it isometric: the view holds one
            /// angle no matter which way you face. Turning yaw on is the difference between a
            /// close isometric camera and something that follows you like a third person game.
            /// </summary>
            public bool? InheritPitch { get; set; }
            public bool? InheritYaw { get; set; }
            public bool? InheritRoll { get; set; }

            /// <summary>
            /// Whether the arm pulls in when a wall is behind you. Off in the game.
            ///
            /// Off is right for a camera twenty five metres up, where nothing is ever between you
            /// and it. Bring the camera down to head height and it becomes the difference between
            /// a third person view and a view from inside the wall behind you.
            /// </summary>
            public bool? CollisionTest { get; set; }

            /// <summary>
            /// How quickly the camera swings to a new angle. 40 in the game, which is nearly
            /// instant - fine when it never rotates, unpleasant when it follows you.
            /// </summary>
            public float? RotationLagSpeed { get; set; }

            /// <summary>Where the arm is anchored. (0, 0, -80) in the game.</summary>
            public float? SocketSide { get; set; }
            public float? SocketHeight { get; set; }

            /// <summary>
            /// Whether the character's own facing follows where the player is aiming. False in
            /// the game.
            ///
            /// This is the one that decides whether a following camera is possible at all. The
            /// camera can be told to inherit the character's yaw, but if the character's *actor*
            /// never turns - if only its mesh is rotated to face the cursor, which is how a
            /// top-down game can get away with it - then there is nothing to inherit and the
            /// camera sits still no matter what it is told.
            /// </summary>
            public bool? ControllerYaw { get; set; }

            /// <summary>
            /// How fast the character turns, in degrees a second. 1080 in the game.
            ///
            /// A tenth of a second to spin on the spot is invisible from above and reads as a
            /// twitch from behind.
            /// </summary>
            public float? TurnRate { get; set; }

            public bool isNothing =>
                ArmLength == null && SeekArmLength == null && Pitch == null && Yaw == null &&
                Roll == null && FieldOfView == null && InheritPitch == null && InheritYaw == null &&
                InheritRoll == null && CollisionTest == null && RotationLagSpeed == null &&
                SocketSide == null && SocketHeight == null && TurnRate == null &&
                ControllerYaw == null;
        }


        /// <summary>
        /// Actors that move the camera while you play.
        ///
        /// The game does not leave the camera where the player character put it. Volumes scattered
        /// through the levels pull it out to 3500 for a wide room or a set piece, and they do it by
        /// setting an absolute distance rather than a multiple - so a camera brought in to 450 is
        /// yanked nearly eight times further out the moment you walk into one, and then stays
        /// there. Changing the camera without changing these is why a close camera feels like it
        /// keeps breaking.
        /// </summary>
        public static readonly string[] ZOOM_VOLUMES = {
            "/Dungeons/Content/Decor/Prefabs/Blueprints/BP_CameraZoomOut",
            "/Dungeons/Content/Decor/Prefabs/Blueprints/BP_CameraZoomOutRotationAware",
            "/Dungeons/Content/Content_DLC5/Decor/Prefabs/_HiddenDepths/CameraLagVolume/BP_CameraLagZoomOut",
        };

        /// <summary>The distances those volumes set, by the name they store them under.</summary>
        private static readonly string[] ZOOM_DISTANCES = {
            "NewCameraDistance",
            "Post delay zoom distance",
        };

        /// <summary>
        /// The same asset with every zoom distance scaled by the same factor the camera was.
        ///
        /// Scaled rather than replaced, because these are not all the same number - a volume that
        /// pulls out for a boss room and one that eases back for a corridor are different
        /// intentions, and keeping their ratio keeps both.
        /// </summary>
        public static byte[]? scaleZoom(byte[] uasset, byte[] uexp, float factor)
        {
            var values = CookedProperties.readAll(uasset, uexp);
            var patched = (byte[])uexp.Clone();
            var changed = false;

            foreach (var value in values)
            {
                if (value.Type != "FloatProperty" || value.Size != 4) { continue; }
                if (Array.IndexOf(ZOOM_DISTANCES, value.Name) < 0) { continue; }
                if (value.At < 0 || value.At + 4 > patched.Length) { continue; }

                putFloat(patched, value.At, BitConverter.ToSingle(patched, value.At) * factor);
                changed = true;
            }

            return changed ? patched : null;
        }


        /// <summary>What one asset's camera is set to, or nothing if it has no camera on it.</summary>
        public static Settings? read(byte[] uasset, byte[] uexp)
        {
            var values = CookedProperties.readAll(uasset, uexp);
            var arm = exportHolding(values, "TargetArmLength");
            if (arm == null) { return null; }

            var lens = exportHolding(values, "FieldOfView");
            var rotation = find(values, arm, "RelativeRotation");
            var socket = find(values, arm, "RelativeLocation");
            //The character's turn rate lives on the movement component, which only the player
            //character has - the co-op camera is not a pawn.
            var movement = exportHolding(values, "RotationRate");
            var pawn = exportHolding(values, "bUseControllerRotationYaw");

            return new Settings {
                ArmLength = readFloat(values, uexp, arm, "TargetArmLength"),
                SeekArmLength = readFloat(values, uexp, arm, "SeekArmLength"),
                //A rotator is stored pitch, yaw, roll - not the x, y, z order the name suggests.
                Pitch = rotation == null ? null : BitConverter.ToSingle(uexp, rotation.At),
                Yaw = rotation == null ? null : BitConverter.ToSingle(uexp, rotation.At + 4),
                Roll = rotation == null ? null : BitConverter.ToSingle(uexp, rotation.At + 8),
                FieldOfView = lens == null ? null : readFloat(values, uexp, lens, "FieldOfView"),
                InheritPitch = readFlag(values, arm, "bInheritPitch"),
                InheritYaw = readFlag(values, arm, "bInheritYaw"),
                InheritRoll = readFlag(values, arm, "bInheritRoll"),
                CollisionTest = readFlag(values, arm, "bDoCollisionTest"),
                RotationLagSpeed = readFloat(values, uexp, arm, "CameraRotationLagSpeed"),
                SocketSide = socket == null ? null : BitConverter.ToSingle(uexp, socket.At + 4),
                SocketHeight = socket == null ? null : BitConverter.ToSingle(uexp, socket.At + 8),
                TurnRate = movement == null ? null : readVectorY(values, uexp, movement, "RotationRate"),
                ControllerYaw = pawn == null ? null : readFlag(values, pawn, "bUseControllerRotationYaw"),
            };
        }

        /// <summary>
        /// The asset with the new values in it, everything else untouched.
        ///
        /// Every value being replaced is the same width as the one it replaces, so nothing shifts
        /// and no offset in the header goes stale - the same reason reshaping a mesh needed no
        /// header correction and importing one did.
        /// </summary>
        public static byte[] patch(byte[] uasset, byte[] uexp, Settings settings)
        {
            var values = CookedProperties.readAll(uasset, uexp);
            var arm = exportHolding(values, "TargetArmLength");
            if (arm == null)
            {
                throw new InvalidOperationException("That asset has no spring arm to change.");
            }
            var lens = exportHolding(values, "FieldOfView");

            var patched = (byte[])uexp.Clone();

            writeFloat(values, patched, arm, "TargetArmLength", settings.ArmLength);
            writeFloat(values, patched, arm, "SeekArmLength", settings.SeekArmLength);
            if (lens != null) { writeFloat(values, patched, lens, "FieldOfView", settings.FieldOfView); }

            var rotation = find(values, arm, "RelativeRotation");
            if (rotation != null && rotation.Size >= 12)
            {
                if (settings.Pitch != null) { putFloat(patched, rotation.At, settings.Pitch.Value); }
                if (settings.Yaw != null) { putFloat(patched, rotation.At + 4, settings.Yaw.Value); }
                if (settings.Roll != null) { putFloat(patched, rotation.At + 8, settings.Roll.Value); }
            }

            writeFlag(values, patched, arm, "bInheritPitch", settings.InheritPitch);
            writeFlag(values, patched, arm, "bInheritYaw", settings.InheritYaw);
            writeFlag(values, patched, arm, "bInheritRoll", settings.InheritRoll);
            writeFlag(values, patched, arm, "bDoCollisionTest", settings.CollisionTest);

            //Optional from here down: these exist on the player character and not on every asset
            //carrying a camera, so a missing one is skipped rather than refused.
            writeFloatIfPresent(values, patched, arm, "CameraRotationLagSpeed", settings.RotationLagSpeed);

            var socket = find(values, arm, "RelativeLocation");
            if (socket != null && socket.Size >= 12)
            {
                if (settings.SocketSide != null) { putFloat(patched, socket.At + 4, settings.SocketSide.Value); }
                if (settings.SocketHeight != null) { putFloat(patched, socket.At + 8, settings.SocketHeight.Value); }
            }

            var pawn = exportHolding(values, "bUseControllerRotationYaw");
            if (pawn != null && settings.ControllerYaw != null)
            {
                writeFlag(values, patched, pawn, "bUseControllerRotationYaw", settings.ControllerYaw);
            }

            var movement = exportHolding(values, "RotationRate");
            if (movement != null && settings.TurnRate != null)
            {
                var rate = find(values, movement, "RotationRate");
                //A rotation rate is a rotator, so the yaw is the middle of the three.
                if (rate != null && rate.Size >= 12) { putFloat(patched, rate.At + 4, settings.TurnRate.Value); }
            }

            return patched;
        }

        /// <summary>
        /// Which export carries a given property.
        ///
        /// Found by what it holds rather than by what it is called. The spring arm is
        /// `LovikaSpringArm` on one asset and `LovikaSpringArm_GEN_VARIABLE` on another, and a
        /// name written down here would have been a third thing to get wrong on top of picking
        /// the wrong asset.
        /// </summary>
        private static string? exportHolding(IReadOnlyList<CookedProperties.Value> values, string property)
        {
            foreach (var value in values)
            {
                if (string.Equals(value.Name, property, StringComparison.Ordinal)) { return value.Export; }
            }
            return null;
        }

        private static CookedProperties.Value? find(IReadOnlyList<CookedProperties.Value> values,
            string export, string name) => CookedProperties.find(values, export, name);

        private static float? readFloat(IReadOnlyList<CookedProperties.Value> values, byte[] uexp,
            string export, string name)
        {
            var found = find(values, export, name);
            if (found == null || found.Size != 4 || found.At + 4 > uexp.Length) { return null; }
            return BitConverter.ToSingle(uexp, found.At);
        }

        private static bool? readFlag(IReadOnlyList<CookedProperties.Value> values, string export, string name)
        {
            var found = find(values, export, name);
            return found == null || found.Type != "BoolProperty" ? (bool?)null : found.Flag;
        }

        private static void writeFloat(IReadOnlyList<CookedProperties.Value> values, byte[] uexp,
            string export, string name, float? value)
        {
            if (value == null) { return; }

            var found = find(values, export, name);
            if (found == null || found.Size != 4 || found.At + 4 > uexp.Length)
            {
                //Named rather than swallowed. A camera setting that silently did not apply is
                //worse than one that says it could not be found.
                throw new InvalidOperationException($"Could not find {export}.{name} to change.");
            }
            putFloat(uexp, found.At, value.Value);
        }

        private static void writeFlag(IReadOnlyList<CookedProperties.Value> values, byte[] uexp,
            string export, string name, bool? value)
        {
            if (value == null) { return; }

            var found = find(values, export, name);
            if (found == null || found.Type != "BoolProperty" || found.At < 0 || found.At >= uexp.Length)
            {
                throw new InvalidOperationException($"Could not find {export}.{name} to change.");
            }
            uexp[found.At] = (byte)(value.Value ? 1 : 0);
        }

        private static float? readVectorY(IReadOnlyList<CookedProperties.Value> values, byte[] uexp,
            string export, string name)
        {
            var found = find(values, export, name);
            if (found == null || found.Size < 12 || found.At + 8 > uexp.Length) { return null; }
            return BitConverter.ToSingle(uexp, found.At + 4);
        }

        private static void writeFloatIfPresent(IReadOnlyList<CookedProperties.Value> values, byte[] uexp,
            string export, string name, float? value)
        {
            if (value == null) { return; }
            var found = find(values, export, name);
            if (found == null || found.Size != 4 || found.At + 4 > uexp.Length) { return; }
            putFloat(uexp, found.At, value.Value);
        }

        private static void putFloat(byte[] uexp, int at, float value)
            => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, uexp, at, 4);
    }
}
