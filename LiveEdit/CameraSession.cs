using System;
using System.Collections.Generic;

namespace LiveEdit
{
    /// <summary>
    /// A connection to the camera in the running game, held open so it can be changed as it is
    /// being looked at.
    ///
    /// The whole point of this over editing a pak is the wait. A pak means close the game, write
    /// the file, start the game, walk to somewhere worth looking at, decide the angle is slightly
    /// wrong, and do it again. Writing the running game's memory means dragging a slider and
    /// seeing the answer, which is the difference between tuning a camera and guessing at one.
    ///
    /// Two things make this cheaper than it sounds, and both were established by trying:
    ///
    /// - **The write stays written.** Nothing in the game recomputes these values, so this does
    ///   not have to race the game loop sixty times a second. It writes when something changes
    ///   and then leaves the game alone.
    /// - **It takes effect immediately**, without having to nudge the engine into rebuilding
    ///   anything.
    ///
    /// Nothing here is permanent. Restarting the game restores everything, because nothing was
    /// ever written to disk - which also makes this safe to experiment with in a way a pak is not.
    /// </summary>
    public sealed class CameraSession : IDisposable
    {
        private readonly GameProcess _game;
        private readonly IReadOnlyList<CameraLink.Found> _cameras;
        private readonly Dictionary<long, float[]> _original = new Dictionary<long, float[]>();

        private CameraSession(GameProcess game, IReadOnlyList<CameraLink.Found> cameras)
        {
            _game = game;
            _cameras = cameras;
            remember();
        }

        public int Count => _cameras.Count;
        public bool IsRunning => _game.IsRunning;

        /// <summary>
        /// Attaches and finds the camera, or explains why not.
        ///
        /// The values to search for are the game's own, which the caller reads from the paks - so
        /// this does not need to know what a camera is worth, only what to look for.
        /// </summary>
        public static CameraSession? attach(float armLength, float seekArmLength, float pitch, float yaw,
            out string problem)
        {
            var game = GameProcess.open(out problem);
            if (game == null) { return null; }

            var found = new CameraLink(game).search(armLength, seekArmLength, pitch, yaw);
            if (found.Count == 0)
            {
                game.Dispose();
                problem = "Attached to the game, but its camera is not where it was expected.\n" +
                    "  Remove any camera pak from ~mods and restart the game - a pak changes the values this looks for.";
                return null;
            }

            return new CameraSession(game, found);
        }

        /// <summary>
        /// What everything was before this touched it, so it can all be put back.
        ///
        /// Taken once, at the start. Taken later it would record this program's own changes as the
        /// originals, and "back to normal" would mean back to whatever was last tried.
        /// </summary>
        private void remember()
        {
            foreach (var camera in _cameras)
            {
                var pitch = _game.readFloat(camera.Rotation) ?? 0f;
                var yaw = _game.readFloat(camera.Rotation + 4) ?? 0f;
                var arm = _game.readFloat(camera.ArmLength) ?? 0f;
                var seek = _game.readFloat(camera.SeekArmLength) ?? 0f;
                _original[camera.Rotation.ToInt64()] = new[] { pitch, yaw, arm, seek };
            }
        }

        public void setPitch(float degrees) => writeAll(camera => camera.Rotation, degrees);

        public void setYaw(float degrees) => writeAll(camera => camera.Rotation + 4, degrees);

        /// <summary>
        /// How far back the camera sits.
        ///
        /// The seek length moves with it, in proportion, because that is what the arm reaches
        /// towards - moving one without the other leaves the camera drifting back to where it was.
        /// </summary>
        public void setArmLength(float units)
        {
            foreach (var camera in _cameras)
            {
                if (!_original.TryGetValue(camera.Rotation.ToInt64(), out var before)) { continue; }

                var ratio = before[2] > 0.01f ? before[3] / before[2] : 1f;
                _game.writeFloat(camera.ArmLength, units);
                _game.writeFloat(camera.SeekArmLength, units * ratio);
            }
        }

        private void writeAll(Func<CameraLink.Found, IntPtr> where, float value)
        {
            foreach (var camera in _cameras) { _game.writeFloat(where(camera), value); }
        }

        /// <summary>Everything back as it was found.</summary>
        public void restore()
        {
            foreach (var camera in _cameras)
            {
                if (!_original.TryGetValue(camera.Rotation.ToInt64(), out var before)) { continue; }

                _game.writeFloat(camera.Rotation, before[0]);
                _game.writeFloat(camera.Rotation + 4, before[1]);
                _game.writeFloat(camera.ArmLength, before[2]);
                _game.writeFloat(camera.SeekArmLength, before[3]);
            }
        }

        public void Dispose()
        {
            //Put back on the way out, so closing the editor does not leave somebody with a camera
            //they cannot explain and no obvious way to undo.
            try { if (_game.IsRunning) { restore(); } }
            catch (Exception) { }

            _game.Dispose();
        }
    }
}
