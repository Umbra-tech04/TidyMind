using System;

namespace TidyMind
{
    public enum RepeatType
    {
        Once,
        Daily,
        Weekly,
        Monthly,
        Yearly
    }

    public class Reminder
    {
        public Guid Id { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public DateTime NextFireTime { get; set; }
        public RepeatType RepeatType { get; set; }
    }
}
