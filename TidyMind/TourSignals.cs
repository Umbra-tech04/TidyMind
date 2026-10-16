using System;

namespace TidyMind
{
    // Named moments the guided tour waits for (GuidedTour), raised where the action already happens. Nothing listens
    // unless a tour is running, so raising one costs next to nothing.
    public static class TourSignals
    {
        public const string ProjectOpened = "ProjectOpened";
        public const string ProjectClosed = "ProjectClosed";
        public const string NotePageAdded = "NotePageAdded";
        public const string CardCustomized = "CardCustomized";
        public const string TodoEdited = "TodoEdited";
        public const string CardsSelected = "CardsSelected";
        public const string CollectionOpened = "CollectionOpened";
        public const string PageBackgroundChanged = "PageBackgroundChanged";
        public const string Searched = "Searched";
        public const string DayPlanOpened = "DayPlanOpened";

        public static event Action<string> Raised;

        public static void Raise(string signal) => Raised?.Invoke(signal);
    }
}
