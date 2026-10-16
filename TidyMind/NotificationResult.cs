namespace TidyMind
{
    public enum NotificationOutcome { Shown, TurnedOff, Failed }

    // What happened to one Windows notification (ReminderManager.SendToast). Detail is for reminders.log: English, with
    // Windows' error code and the process's session.
    public sealed record NotificationResult(NotificationOutcome Outcome, string Detail)
    {
        public override string ToString() => Detail;
    }
}
