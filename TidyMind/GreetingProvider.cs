using System;
using System.Collections.Generic;

namespace TidyMind
{
    public enum TimeOfDayBucket { Morning, Afternoon, Evening, Night }

    // The big line at the top of Home: one of a few messages for the time of day, kept until the time of day changes.
    public static class GreetingProvider
    {
        private static readonly Dictionary<TimeOfDayBucket, string[]> Messages = new Dictionary<TimeOfDayBucket, string[]>
        {
            [TimeOfDayBucket.Morning] = new[]
            {
                "Good morning.",
                "Early bird today.",
                "Morning. Coffee first, then get started?",
                "Fresh day, fresh list.",
                "Nice morning. Let's ease into it."
            },
            [TimeOfDayBucket.Afternoon] = new[]
            {
                "Good afternoon.",
                "Halfway through the day.",
                "Afternoon. How's it going?",
                "Midday stretch. Keeping the pace.",
                "Afternoon break?"
            },
            [TimeOfDayBucket.Evening] = new[]
            {
                "Good evening.",
                "Evening calm.",
                "The day's winding down.",
                "Evening. What's left for today?"
            },
            [TimeOfDayBucket.Night] = new[]
            {
                "It's late.",
                "Night shift?",
                "Quiet hour. Everything okay?",
                "Still up. That's fine, but mind your sleep too.",
                "Around midnight. Rest if you can."
            }
        };

        // Morning 6:00-9:59, afternoon 10:00-17:59, evening 18:00-22:59, night 23:00-5:59.
        public static TimeOfDayBucket BucketFor(DateTime now)
        {
            int hour = now.Hour;
            if (hour >= 6 && hour < 10) return TimeOfDayBucket.Morning;
            if (hour >= 10 && hour < 18) return TimeOfDayBucket.Afternoon;
            if (hour >= 18 && hour < 23) return TimeOfDayBucket.Evening;
            return TimeOfDayBucket.Night;
        }

        // The greeting picked for the current stretch of the day, kept for the app session: Home is rebuilt on every
        // visit and window activation, and a line that changed each time would be distracting.
        private static string current;
        private static (TimeOfDayBucket Bucket, DateTime Day) currentSpan;

        // The same message all through one bucket; a new one when the bucket changes. The span includes its date so
        // yesterday's morning isn't mistaken for today's, and night counts as one span across midnight.
        public static string Pick(DateTime now)
        {
            TimeOfDayBucket bucket = BucketFor(now);
            DateTime day = bucket == TimeOfDayBucket.Night && now.Hour < 6 ? now.Date.AddDays(-1) : now.Date;

            if (current == null || currentSpan != (bucket, day))
            {
                string[] options = Messages[bucket];
                current = options[Random.Shared.Next(options.Length)];
                currentSpan = (bucket, day);
            }
            return current;
        }
    }
}
