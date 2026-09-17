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
        public float RotationLagSpeed { get; set; } = 40f;

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
                    Distance = 800f, Pitch = -12f, FieldOfView = 75f,
                    PivotHeight = 170f, SocketSide = 40f, SocketHeight = 20f,
                },
                new CameraPreset {
                    Name = "Third person, far", BuiltIn = true,
                    //Back far enough to see what is coming, and tipped down to match - a camera
                    //this far back at a shallow angle spends most of its time looking at a wall.
                    Distance = 1500f, Pitch = -25f, FieldOfView = 70f,
                    PivotHeight = 170f, SocketSide = 30f, SocketHeight = 40f,
                },
                new CameraPreset {
                    Name = "First person", BuiltIn = true,
                    //No arm at all, so the camera sits on the pivot - which is put at eye height
                    //rather than the shoulders, and moved off the shoulder offset so it is not
                    //looking out of an ear.
                    Distance = 0f, Pitch = 0f, FieldOfView = 90f,
                    PivotHeight = 200f, SocketSide = 0f, SocketHeight = 0f,
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
