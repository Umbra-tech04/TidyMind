using System.Collections.Generic;
using System.Windows;

namespace TidyMind
{
    // The guided tour's steps, in one place. Each step's title and sentence are the strings "Tour.<Key>.Title" and
    // "Tour.<Key>.Text" (Strings.resx), looked up as the step is shown, so a language switch during the tour shows the
    // current step in the new language. Every step points at a part of the main window, with Home shown and nothing
    // open over it (MainWindow.ReadyForTour, before each step), and says what it's good for in a sentence or two. A
    // step whose element is hidden, scrolled away or too tall for the window is skipped (GuidedTour.OnScreen).
    public static class TourSteps
    {
        // First run on a fresh install: the tour, the example memories, or neither.
        public static readonly TourStep Welcome = new TourStep
        {
            Key = "Welcome",
            Primary = "Tour.Button.Start",
            PrimaryChoice = TourChoice.Begin,
            Secondary = "Tour.Button.NotNow",
            SecondaryChoice = TourChoice.Dismiss,
            Extra = "Examples.Add",
            ExtraChoice = TourChoice.Examples
        };

        public static List<TourStep> Build(MainWindow main)
        {
            FrameworkElement? On(string name) => GuidedTour.Find(main, name);
            TourStep Step(string key, string target) =>
                new TourStep { Key = key, Target = () => On(target), Prepare = main.ReadyForTour };

            return new List<TourStep>
            {
                Step("Memories", "NavScroll"),
                Step("NewMemory", "NewButtons"),
                Step("Todos", "AddRow"), // Home's to-do box: its height never changes with the list
                Step("Corner", "OverlayButtons"),
                Step("Search", "NavSearch"),
                new TourStep
                {
                    Key = "End",
                    Target = () => On("HelpButton"),
                    Prepare = main.ReadyForTour,
                    Primary = "Tour.Button.Finish",
                    PrimaryChoice = TourChoice.Finish
                }
            };
        }
    }
}
