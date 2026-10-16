using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // The guided tour's steps, in one place. Each step's title and sentence are the strings "Tour.<Key>.Title" and
    // "Tour.<Key>.Text" (Strings.resx), looked up as the step is shown, so a language switch during the tour shows the
    // current step in the new language. Plain words, one sentence per step.
    public static class TourSteps
    {
        // First run on a fresh install, before the example memory exists.
        public static readonly TourStep Welcome = new TourStep
        {
            Key = "Welcome",
            Primary = "Tour.Button.Start",
            PrimaryChoice = TourChoice.Begin,
            Secondary = "Tour.Button.NotNow",
            SecondaryChoice = TourChoice.Dismiss
        };

        // Closes an open project and every overlay, whoever opened them: before the card, day plan, corner and end
        // steps and when the tour ends (MainWindow.TourEnded). Closing a project saves it; one that can't close yet (a
        // file still copying, no name) stays open and says why.
        public static void CloseOpened()
        {
            GuidedTour.Open<ProjectWindow>()?.Close();
            OverlayManager.CloseAll();
        }

        // Basics, a "Basics done" card (stop there or go on), the tricks, and the end card. Opening a project shows
        // it in a window of its own, so the notebook step comes right after it and the card steps after closing it.
        public static List<TourStep> Build(MainWindow main)
        {
            FrameworkElement? FirstCard() =>
                (GuidedTour.Find(main, "ProjectPanel") as Panel)?.Children.OfType<FrameworkElement>().FirstOrDefault();
            FrameworkElement? InProject(string name) => GuidedTour.Find(GuidedTour.Open<ProjectWindow>(), name);
            void ShowProjects() => main.ShowSample(ProfileType.Project);

            return new List<TourStep>
            {
                // ---- Basics ----
                new TourStep
                {
                    Key = "OpenProject",
                    Target = FirstCard, Prepare = ShowProjects, Signal = TourSignals.ProjectOpened
                },
                new TourStep
                {
                    Key = "TurnPage",
                    Target = () => InProject("NextPageButton"),
                    Prepare = () =>
                    {
                        if (!Application.Current.Windows.OfType<ProjectWindow>().Any())
                            main.OpenSampleNotebook();
                    },
                    Signal = TourSignals.NotePageAdded
                },
                new TourStep
                {
                    Key = "CloseProject",
                    Target = () => InProject("SaveButton"),
                    AlreadyDone = () => GuidedTour.Open<ProjectWindow>() == null,
                    Signal = TourSignals.ProjectClosed
                },
                new TourStep
                {
                    Key = "CardLook",
                    Target = FirstCard,
                    Prepare = () =>
                    {
                        CloseOpened(); // the project, if Next was pressed instead of Save
                        ShowProjects();
                    },
                    Signal = TourSignals.CardCustomized
                },
                new TourStep
                {
                    Key = "TodoList",
                    Target = () => GuidedTour.Find(main, "TodoPanel"), Prepare = ShowProjects, Signal = TourSignals.TodoEdited
                },
                new TourStep
                {
                    Key = "BasicsDone",
                    Primary = "Tour.Button.Tricks", PrimaryChoice = TourChoice.Next,
                    Secondary = "Tour.Button.Finish", SecondaryChoice = TourChoice.Finish
                },

                // ---- Handy tricks ----
                new TourStep
                {
                    Key = "SelectCards",
                    Target = () => GuidedTour.Find(main, "CardScroll"), Prepare = ShowProjects, Signal = TourSignals.CardsSelected
                },
                new TourStep
                {
                    Key = "Collections",
                    Target = () => main.SampleNavItem(ProfileType.Collection), Signal = TourSignals.CollectionOpened
                },
                new TourStep
                {
                    Key = "PageBackground",
                    Target = () => GuidedTour.Find(main, "ContentArea"),
                    Prepare = () =>
                    {
                        if (!main.ShowsSample)
                            main.ShowSample(ProfileType.Collection);
                    },
                    Signal = TourSignals.PageBackgroundChanged
                },
                new TourStep
                {
                    // The example word is taken from the example collection, so it finds something whichever
                    // language the examples were written in.
                    Key = "Search",
                    TextArgs = () => new object[] { main.SampleSearchWord() },
                    Target = () => main.IsSearchOpen ? GuidedTour.Find(main, "QueryBox") : null,
                    Signal = TourSignals.Searched
                },
                new TourStep
                {
                    Key = "DayPlan",
                    Target = () => GuidedTour.Find(main, "NavDayPlan"),
                    Prepare = CloseOpened, // search, from the step before
                    Signal = TourSignals.DayPlanOpened
                },
                new TourStep
                {
                    Key = "Corner",
                    // Opened from here, the calendar slides over the buttons: then the calendar is what's lit, until
                    // its own ✕ closes it.
                    Target = () => GuidedTour.Find(main, main.IsCalendarOpen ? "Sheet" : "OverlayButtons"),
                    Prepare = CloseOpened // anything opened on the step before
                },
                new TourStep
                {
                    Key = "End",
                    // No spotlight while an overlay is open: the calendar covers the ? button, Quick Notes and
                    // Reminders sit in front of it.
                    Target = () => OverlayManager.Current == Overlay.None ? GuidedTour.Find(main, "HelpButton") : null,
                    Prepare = CloseOpened,
                    Primary = "Tour.Button.Finish", PrimaryChoice = TourChoice.Finish
                }
            };
        }
    }
}
