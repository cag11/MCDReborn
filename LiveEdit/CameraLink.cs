using System;
using System.Collections.Generic;

namespace LiveEdit
{
    /// <summary>
    /// Finding the camera in the running game, by the numbers it is made of.
    ///
    /// The usual way to find something in a game's memory is to dump its class layout and walk
    /// pointers to it. That is not available here: the game's binary cannot even be read from
    /// disk on this build, so there is nothing to dump from and no offsets to walk.
    ///
    /// It is not needed either, because the camera's numbers are already known - they were read
    /// out of the pak. A spring arm holds its length, the length it seeks, and its rotation, and
    /// in this game those are 2450, 2800 and minus 45 by 45. Five specific floats in a specific
    /// order is a fingerprint. Searching memory for it is the same trick used to find a mesh's
    /// vertices inside a cooked asset: do not walk to the thing, recognise it.
    ///
    /// The fingerprint comes from the pak rather than being written down here, so a game update
    /// that retunes the camera changes what is searched for without anybody editing this.
    /// </summary>
    public sealed class CameraLink
    {
        /// <summary>One candidate: where in the game's memory a spring arm appears to be.</summary>
        public sealed class Found
        {
            public IntPtr ArmLength { get; set; }
            public IntPtr SeekArmLength { get; set; }
            public IntPtr Rotation { get; set; }
            public float ArmLengthValue { get; set; }
            public float PitchValue { get; set; }
            public float YawValue { get; set; }
        }

        private readonly GameProcess _game;

        public CameraLink(GameProcess game)
        {
            _game = game;
        }

        /// <summary>
        /// Every place in memory holding the spring arm's fingerprint.
        ///
        /// More than one is expected and is not a fault: the values exist in the loaded asset, in
        /// the class default, and in each live component made from it. Telling those apart is done
        /// by changing one and seeing which moves the camera, which is the next step rather than
        /// this one.
        /// </summary>
        public IReadOnlyList<Found> search(float armLength, float seekArmLength, float pitch, float yaw,
            Action<int>? progress = null)
        {
            var found = new List<Found>();
            var searched = 0;

            foreach (var (at, size) in _game.writableRegions())
            {
                var buffer = new byte[size];
                if (!_game.tryRead(at, buffer, (int)size))
                {
                    //A region can be freed between being listed and being read. Ordinary.
                    continue;
                }

                scan(buffer, at, armLength, seekArmLength, pitch, yaw, found);

                progress?.Invoke(++searched);
            }

            return found;
        }


        /// <summary>
        /// Every address holding one particular float, with no other requirement.
        ///
        /// The fingerprint search wants a spring arm's whole shape, and that has turned out to
        /// find only the templates - the loaded asset and the class defaults, which all hold the
        /// authored number and are not what the game renders from. The live component is somewhere
        /// its neighbours do not match the authored layout.
        ///
        /// So this drops every assumption but one: somewhere in memory is a float equal to the
        /// camera's distance, and when the game moves the camera, that float moves. Finding which
        /// of them does is the job of whoever watches them afterwards. This only collects the
        /// haystack.
        /// </summary>
        public IReadOnlyList<IntPtr> everyFloat(float value, int most = 200000)
        {
            var found = new List<IntPtr>();

            foreach (var (at, size) in _game.writableRegions())
            {
                var buffer = new byte[size];
                if (!_game.tryRead(at, buffer, (int)size)) { continue; }

                for (int i = 0; i + 4 <= buffer.Length; i += 4)
                {
                    if (!close(BitConverter.ToSingle(buffer, i), value)) { continue; }

                    found.Add(new IntPtr(at.ToInt64() + i));
                    if (found.Count >= most) { return found; }
                }
            }

            return found;
        }

        //Where the rotation sits relative to the arm length inside a spring arm, measured from
        //the game rather than assumed: both instances found in a running game put the rotation
        //exactly 0xE8 bytes before the arm length, which is what two objects of one class look
        //like.
        private const int ROTATION_BEFORE_ARM = 0xE8;

        /// <summary>
        /// Looks for the arm length, confirms it with the seek length, then reads the rotation
        /// from where the layout says it is.
        ///
        /// The rotation is located structurally rather than searched for, and that is deliberate:
        /// searching for it means knowing what it currently says, and the moment this tool has
        /// changed the angle once, it no longer says that. A search keyed on the rotation finds
        /// nothing as soon as it has worked - which is a confusing way to be told it worked.
        ///
        /// The two lengths are safe to key on because nothing here changes them without being
        /// asked to, and a lone float of 2450 is everywhere in a game's memory while 2450 with
        /// 2800 a few hundred bytes away is not.
        /// </summary>
        private static void scan(byte[] buffer, IntPtr baseAddress, float armLength, float seekArmLength,
            float pitch, float yaw, List<Found> found)
        {
            //How far from the arm length the rest of the component can sit. A spring arm is a few
            //hundred bytes of object; this is generous without being meaningless.
            const int NEARBY = 512;

            for (int at = 0; at + 4 <= buffer.Length; at += 4)
            {
                if (!close(BitConverter.ToSingle(buffer, at), armLength)) { continue; }

                var seekAt = findNear(buffer, at, NEARBY, seekArmLength);
                if (seekAt < 0) { continue; }

                var rotationAt = at - ROTATION_BEFORE_ARM;
                if (rotationAt < 0 || rotationAt + 12 > buffer.Length) { continue; }

                //Not a check that it is the expected angle - only that it is an angle. A pitch
                //beyond straight up or down, or a yaw off the compass, means this is not a
                //rotation and the match was luck.
                var foundPitch = BitConverter.ToSingle(buffer, rotationAt);
                var foundYaw = BitConverter.ToSingle(buffer, rotationAt + 4);
                if (!isAngle(foundPitch, 90f) || !isAngle(foundYaw, 360f)) { continue; }

                found.Add(new Found {
                    ArmLength = new IntPtr(baseAddress.ToInt64() + at),
                    SeekArmLength = new IntPtr(baseAddress.ToInt64() + seekAt),
                    Rotation = new IntPtr(baseAddress.ToInt64() + rotationAt),
                    ArmLengthValue = BitConverter.ToSingle(buffer, at),
                    PitchValue = foundPitch,
                    YawValue = foundYaw,
                });
            }
        }

        private static bool isAngle(float value, float limit)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) { return false; }
            return Math.Abs(value) <= limit;
        }

        private static int findNear(byte[] buffer, int around, int distance, float wanted)
        {
            var from = Math.Max(0, around - distance);
            var to = Math.Min(buffer.Length - 4, around + distance);

            for (int at = from; at <= to; at += 4)
            {
                if (at != around && close(BitConverter.ToSingle(buffer, at), wanted)) { return at; }
            }
            return -1;
        }

        /// <summary>
        /// Equal to within a hair.
        ///
        /// Not exact, because the value in memory has usually been through the engine's own
        /// arithmetic since it was loaded - the pak says the pitch is -44.999973 and what is in
        /// memory may be a few bits off that.
        /// </summary>
        private static bool close(float found, float wanted)
        {
            if (float.IsNaN(found) || float.IsInfinity(found)) { return false; }
            return Math.Abs(found - wanted) <= Math.Max(0.01f, Math.Abs(wanted) * 0.0005f);
        }
    }
}
