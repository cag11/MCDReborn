using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// A way of looking at the game, kept as a set of numbers.
    ///
    /// Everything here is applied to the running game and nothing is written into it. That is the
    /// whole difference from what this tab used to do: a pak meant closing the game, writing a
    /// file, starting it again, walking somewhere worth looking at, deciding the angle was wrong,
    /// and doing it all again. A preset is the same settings arriving instantly, and undone by
    /// quitting.
    ///
    /// Which also makes presets worth having. Tuning a camera by dragging sliders takes a few
    /// minutes and produces numbers nobody wants to find twice, so what somebody arrives at can be
    /// named and picked again in one click.
    /// </summary>
    public sealed class CameraPreset
    {
        public string Name { get; set; } = "";

        /// <summary>Whether this one ships with the editor, and so cannot be deleted or written over.</summary>
        public bool BuiltIn { get; set; }

        /// <summary>How far back the camera sits. Zero puts it at the pivot, which is first person.</summary>
        public float Distance { get; set; } = 800f;

        /// <summary>Degrees below horizontal.</summary>
        public float Pitch { get; set; } = -12f;

        public float FieldOfView { get; set; } = 75f;

        /// <summary>
        /// How far above the character the camera orbits and looks.
        ///
        /// The arm hangs about ninety units below the character's origin, which is near its feet,
        /// and its head is a hundred and ten above - so a hundred and seventy puts the pivot
        /// around the shoulders and two hundred puts it at eye height.
        /// </summary>
        public float PivotHeight { get; set; } = 170f;

        /// <summary>Sideways from the character, for looking over a shoulder rather than through a head.</summary>
        public float SocketSide { get; set; } = 40f;

        public float SocketHeight { get; set; } = 20f;

        /// <summary>How quickly the camera swings to a new angle. Forty is the game's own.</summary>
        /// <summary>
        /// How far in front of the character's face the camera sits.
        ///
        /// SocketOffset.X, which is the one applied after the view rotation - so it means forward
        /// from where you are looking. TargetOffset, which the pivot slider writes, is added to the
        /// world position before any rotation, so its X is world X and would sit in front of your
        /// face walking one way and behind your head walking the other.
        ///
        /// Worth about twenty units in first person, which is a head's radius: enough to put the
        /// lens outside the mesh so the head cannot be in frame at all. It does swing down a little
        /// when you look down, being view relative - twenty units forward at forty five degrees is
        /// fourteen forward and fourteen down, which is small enough not to matter.
        /// </summary>
        public float SocketForward { get; set; }

        public float RotationLagSpeed { get; set; } = 40f;

        /// <summary>
        /// How quickly the camera catches up with the character, rather than with its rotation.
        ///
        /// The game's own value is 1, which is very slow indeed, and every preset uses it - because
        /// slow is what stops a staircase shaking the view apart. Recorded walking down one and
        /// replayed through the engine's lag maths, a lag of 1 passes through 0.015 of a capsule
        /// that jitters 1.717, and a lag of 100 passes through 0.680.
        ///
        /// Raising it was tried, to stop a jump leaving the camera behind, and it is the wrong
        /// place to fix that: the trade between the two is continuous and there is no value that
        /// does both. The jump makes the camera rigid for its own duration instead.
        ///
        /// Every preset carries a value rather than leaving it alone, so that switching from a
        /// preset that needed a fast camera back to one that did not actually puts it back.
        /// </summary>
        public float LagSpeed { get; set; } = 1f;

        /// <summary>Whether the camera pulls in when something is behind you. Matters once it is close.</summary>
        public bool Collision { get; set; } = true;

        public bool MouseLook { get; set; } = true;
        public bool Wasd { get; set; } = true;

        public CameraPreset copy() => (CameraPreset)MemberwiseClone();

        /// <summary>
        /// The three that ship, which are the three worth starting from.
        ///
        /// Deliberately few. An earlier version of this tab offered presets that each changed one
        /// setting from stock, because at the time nothing was known about which changes broke the
        /// game and a preset that changed six things proved nothing when it failed. That is over -
        /// the settings are understood now - so these are whole ways of playing instead.
        /// </summary>
        public static IReadOnlyList<CameraPreset> builtIn()
        {
            return new[] {
                new CameraPreset {
                    Name = "Third person", BuiltIn = true,
                    Distance = 800f, Pitch = -12f, FieldOfView = 65f,
                    PivotHeight = 170f, SocketSide = 40f, SocketHeight = 20f,
                    LagSpeed = 1f,
                },
                new CameraPreset {
                    Name = "Third person, far", BuiltIn = true,
                    //Back far enough to see what is coming, and tipped down to match - a camera
                    //this far back at a shallow angle spends most of its time looking at a wall.
                    Distance = 1500f, Pitch = -25f, FieldOfView = 70f,
                    PivotHeight = 170f, SocketSide = 30f, SocketHeight = 40f,
                    LagSpeed = 1f,
                },
                new CameraPreset {
                    Name = "First person", BuiltIn = true,
                    //No arm at all, so the camera sits on the pivot, and two hundred is the right
                    //number - which took moving it to find out.
                    //
                    //The arm hangs ninety units below the capsule centre and the capsule half
                    //height is 110, so a pivot of 200 puts the camera on the crown of the head.
                    //Lowering it to eye height sounds better and is not: at 175 the camera is
                    //inside the body and the view fills with the inside of your own cape. The
                    //character has no first person model to step into, and the flag that would
                    //hide it does nothing from out here, so the camera has to sit on top of the
                    //head rather than in it.
                    //
                    //What keeps the head out of frame is not the pivot, it is not being able to
                    //tip far enough down to look at it. See the pitch limits in MouseLook.
                    //Two hundred and thirty rather than two hundred, to get the last of the head
                    //off the bottom of the screen.
                    //
                    //Two hundred puts the camera exactly on the crown, which is fine standing still
                    //and not fine walking: the body shifts about thirty units against the capsule
                    //the camera is bolted to, and the top of the head crosses into the bottom edge
                    //on every bob. The head is roughly 34 wide, so clearing it needs the camera
                    //about twenty units above it - by then the head sits steeper than the bottom of
                    //the view rather than inside it.
                    //
                    //Raise it further and first person starts feeling like a drone again; lower it
                    //and the head comes back, and at 175 the camera is inside the body entirely.
                    //It is a slider, so this is a starting point rather than an answer.
                    Distance = 0f, Pitch = 0f, FieldOfView = 90f,
                    PivotHeight = 230f, SocketSide = 0f, SocketHeight = 0f,
                    //Twenty, which is a compromise arrived at from both ends rather than picked.
                    //
                    //The game's own value is 1. From behind a character that is invisible; from
                    //inside one it is the whole problem, because the camera trails the climb.
                    //Measured walking up a staircase, the camera's height above the body varied by
                    //324 units at a lag of 1 - the model rising into view and then settling as the
                    //camera caught up.
                    //
                    //A hundred fixes that and puts every single step straight into the view. There
                    //is no value that does both: recorded on a staircase, lag 1 passes through
                    //0.015 of a capsule that jitters 1.717, lag 20 passes 0.145 and lag 100 passes
                    //0.680. Twenty keeps a tenth of the shake and most of the responsiveness.
                    //
                    //It is a slider on the camera tab, because this is taste and the number that
                    //suits somebody is not findable from here.
                    LagSpeed = 20f,
                    //Nothing to collide with when the camera is inside the character.
                    Collision = false,
                },
            };
        }
    }

    /// <summary>
    /// Where saved presets live, which is beside the editor's other settings rather than in the
    /// game's folder - nothing here belongs to the game.
    /// </summary>
    public static class CameraPresets
    {
        private static string folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCDReborn");

        private static string file => Path.Combine(folder, "camera-presets.json");

        /// <summary>
        /// The ones that ship, then the ones somebody made.
        ///
        /// Saved presets that fail to load are dropped rather than thrown: a corrupt file should
        /// cost somebody their presets, not their camera tab.
        /// </summary>
        public static List<CameraPreset> all()
        {
            var presets = new List<CameraPreset>(CameraPreset.builtIn());

            try
            {
                if (File.Exists(file))
                {
                    var saved = JsonSerializer.Deserialize<List<CameraPreset>>(File.ReadAllText(file));
                    if (saved != null)
                    {
                        foreach (var one in saved)
                        {
                            one.BuiltIn = false;
                            presets.Add(one);
                        }
                    }
                }
            }
            catch (Exception) { }

            return presets;
        }

        public static void save(IEnumerable<CameraPreset> presets)
        {
            var mine = new List<CameraPreset>();
            foreach (var one in presets)
            {
                if (!one.BuiltIn) { mine.Add(one); }
            }

            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(file, JsonSerializer.Serialize(mine,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception) { }
        }
    }
}
