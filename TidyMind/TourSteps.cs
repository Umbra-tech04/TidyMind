using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // The guided tour's steps and every text it shows, in one place. Plain words, one sentence per step.
    public static class TourSteps
    {
        public const string SkipLabel = "Skip tour";
        public const string NextLabel = "Next";
        public const string HelpEntry = "Take the tour";
        public const string ConfirmTitle = "Take the tour";
        public const string ConfirmText = "This adds an example memory. Your own data is not changed.";
        public const string DeleteNote = "This is the example memory.";

        // Home's "What can TidyMind do?" row: title, sentence, and the step it starts at (BackupCard opens Settings).
        public const string DiscoveryTitle = "What can TidyMind do?";
        public const string DiscoveryHide = "Hide";
        public const string BackupCard = "backup";
        public static readonly (string Title, string Text, string Start)[] Discovery =
        {
            ("Projects", "Plan anything with tasks, notes and files.", "open"),
            ("Collections", "Keep a list of the things you own.", "collection"),
            ("Notebook", "Write notes on pages you can turn.", "notes"),
            ("Search", "Find anything with Ctrl+Shift+F.", "search"),
            ("Backgrounds", "Give pages and cards a color or a picture.", "background"),
            ("Backup", "Save all your data to one file.", BackupCard)
        };

        // First run on a fresh install, before the example memory exists.
        public static readonly TourStep Welcome = new TourStep
        {
            Id = "welcome",
            Title = "Welcome to TidyMind",
            Text = "A memory is one area of your life, listed in the sidebar. A project memory holds projects, each with "
                + "its tasks, notes and files. A collection memory keeps a list of things you own, like clothes or a bike.",
            Primary = "Start the tour",
            PrimaryChoice = TourChoice.Begin,
            Secondary = "Not now",
            SecondaryChoice = TourChoice.Dismiss
        };

        public static int IndexOf(IReadOnlyList<TourStep> steps, string? id) =>
            id == null ? 0 : System.Math.Max(0, steps.ToList().FindIndex(s => s.Id == id));

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
                    Id = "open", Title = "Open a project",
                    Text = "Click this card to open the project.",
                    Target = FirstCard, Prepare = ShowProjects, Signal = TourSignals.ProjectOpened
                },
                new TourStep
                {
                    Id = "notes", Title = "Turn the page",
                    Text = "Click the right arrow to go to page two, then click it again to start a new page.",
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
                    Id = "close", Title = "Close the project",
                    Text = "Click Save to close the project and keep your changes.",
                    Target = () => InProject("SaveButton"),
                    AlreadyDone = () => GuidedTour.Open<ProjectWindow>() == null,
                    Signal = TourSignals.ProjectClosed
                },
                new TourStep
                {
                    Id = "card", Title = "Change a card's look",
                    Text = "Right-click this card and choose Customize Card to give it a color or a picture.",
                    Target = FirstCard, Prepare = ShowProjects, Signal = TourSignals.CardCustomized
                },
                new TourStep
                {
                    Id = "todo", Title = "Keep a to-do list",
                    Text = "Type a to-do and press Enter, then click its text to change it.",
                    Target = () => GuidedTour.Find(main, "TodoPanel"), Prepare = ShowProjects, Signal = TourSignals.TodoEdited
                },
                new TourStep
                {
                    Id = "basics-done", Title = "Basics done",
                    Text = "That covers the everyday things, and the tricks take about another minute.",
                    Primary = "Continue with tricks", PrimaryChoice = TourChoice.Next,
                    Secondary = "Finish", SecondaryChoice = TourChoice.Finish
                },

                // ---- Handy tricks ----
                new TourStep
                {
                    Id = "select", Title = "Select several cards",
                    Text = "Drag a rectangle across the empty space around the cards to select more than one.",
                    Target = () => GuidedTour.Find(main, "CardScroll"), Prepare = ShowProjects, Signal = TourSignals.CardsSelected
                },
                new TourStep
                {
                    Id = "collection", Title = "Collections",
                    Text = "Open the Welcome collection in the sidebar to see a list of things you own.",
                    Target = () => main.SampleNavItem(ProfileType.Collection), Signal = TourSignals.CollectionOpened
                },
                new TourStep
                {
                    Id = "background", Title = "Change the page background",
                    Text = "Right-click an empty part of the page and choose Edit Background.",
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
                    Id = "search", Title = "Search everything",
                    Text = "Press Ctrl+Shift+F and type a word, for example bike.",
                    Target = () => main.IsSearchOpen ? GuidedTour.Find(main, "QueryBox") : null,
                    Signal = TourSignals.Searched
                },
                new TourStep
                {
                    Id = "corner", Title = "Notes, reminders and calendar",
                    Text = "These buttons open quick notes, your reminders and the calendar from any page.",
                    Target = () => GuidedTour.Find(main, "OverlayButtons"),
                    Prepare = () => OverlayManager.Close(Overlay.Search)
                },
                new TourStep
                {
                    Id = "end", Title = "That's the tour",
                    Text = "Help and shortcuts are behind the ? button, and you can delete the Welcome memories from the sidebar any time.",
                    Target = () => GuidedTour.Find(main, "HelpButton"),
                    Primary = "Finish", PrimaryChoice = TourChoice.Finish
                }
            };
        }
    }
}
