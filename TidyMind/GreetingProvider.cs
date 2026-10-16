using System;
using System.Collections.Generic;

namespace TidyMind
{
    public enum TimeOfDayBucket { Morning, Afternoon, Evening, Night }

    // The big line at the top of Home: one of a few messages for the time of day, kept until the time of day changes.
    // The messages are "Greeting.<bucket>.1", ".2"… (Strings.resx, as many as there are); the pick is kept by number,
    // so after a language switch the same message shows in the new language.
    public static class GreetingProvider
    {
        public static int MessageCount(TimeOfDayBucket bucket)
        {
            int count = 0;
            while (Loc.Find("Greeting." + bucket + "." + (count + 1)) != null)
                count++;
            return count;
        }

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
        private static string currentKey;
        private static (TimeOfDayBucket Bucket, DateTime Day) currentSpan;

        // The same message all through one bucket; a new one when the bucket changes. The span includes its date so
        // yesterday's morning isn't mistaken for today's, and night counts as one span across midnight.
        public static string Pick(DateTime now)
        {
            TimeOfDayBucket bucket = BucketFor(now);
            DateTime day = bucket == TimeOfDayBucket.Night && now.Hour < 6 ? now.Date.AddDays(-1) : now.Date;

            if (currentKey == null || currentSpan != (bucket, day))
            {
                currentKey = "Greeting." + bucket + "." + (Random.Shared.Next(MessageCount(bucket)) + 1);
                currentSpan = (bucket, day);
            }
            return Loc.T(currentKey);
        }
    }
}
