using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Services
{
    /// <summary>
    /// Local diagnostic log. This used to forward to GameAnalytics, which was dropped:
    /// the SDK targets Mono and has no .NET 5+ build, and this fork shipped with empty
    /// keys anyway, so nothing was ever submitted.
    /// The public surface is unchanged, so all 89 call sites still work.
    /// </summary>
    public class EventLogger
    {
        public static void init()
        {
            Debug.WriteLine($"[EVENT] session start");
        }

        public static void dispose()
        {
            Debug.WriteLine($"[EVENT] session end");
        }

        public static void logEvent(string eventId, IDictionary<string, object>? fields = null)
        {
            if (fields != null)
            {
                string fieldsStr = string.Join(" ", fields.Select(pair => $"{pair.Key}={pair.Value}"));
                Debug.WriteLine($"[EVENT] {eventId} fields: {fieldsStr}");
            }
            else
            {
                Debug.WriteLine($"[EVENT] {eventId}");
            }
        }

        public static void logEvent(string eventId, double value)
        {
            Debug.WriteLine($"[EVENT] {eventId} value={value}");
        }

        public static void logError(string message)
        {
            Debug.WriteLine($"[ERROR] {message}");
        }
    }
}
