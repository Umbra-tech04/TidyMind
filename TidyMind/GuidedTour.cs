using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TidyMind
{
    public enum TourChoice { Next, Finish, Dismiss, Begin }

    // How a tour ended. Begin: the welcome card's "Start the tour" (the caller then starts the real one).
    public enum TourEnd { Finished, Skipped, Begin, Failed }

    // One step of a tour (TourSteps): what to point at, what to say, and what ends it.
    public class TourStep
    {
        // Names the step and its texts: "Tour.<Key>.Title" and "Tour.<Key>.Text", in the current language.
        public string Key { get; init; } = "";
        public Func<object?[]>? TextArgs { get; init; }

        public string Title => Loc.T("Tour." + Key + ".Title");
        public string Text => TextArgs == null ? Loc.T("Tour." + Key + ".Text") : Loc.F("Tour." + Key + ".Text", TextArgs());

        // The element to spotlight, looked up again as the page changes. Null, or nothing found: the callout sits in
        // the middle and Next moves on.
        public Func<FrameworkElement?>? Target { get; init; }

        // Puts the app where the step happens (opens the example memory, ...), as the step starts.
        public Action? Prepare { get; init; }

        // Checked as the step starts: true skips it (e.g. "close the project" when it's already closed).
        public Func<bool>? AlreadyDone { get; init; }

        // The TourSignals name that completes the step; null: Next only.
        public string? Signal { get; init; }

        // A card (welcome, basics done, the end) has its own buttons instead of Skip tour / Next and no progress dots.
        // Primary and Secondary are the buttons' string keys.
        public string? Primary { get; init; }
        public TourChoice PrimaryChoice { get; init; } = TourChoice.Next;
        public string? Secondary { get; init; }
        public TourChoice SecondaryChoice { get; init; } = TourChoice.Finish;

        public bool IsCard => Primary != null;
    }

    // The guided tour: dims the window that holds the step's element, cuts a spotlight around it, and shows the step
    // in a callout next to it. A step ends when TourSignals reports its action, or with Next. Clicks pass through
    // only inside the spotlight. Anything that throws ends the tour with one line in error.log: it must never get in
    // the way of the app.
    public sealed class GuidedTour
    {
        private const double HolePadding = 6;

        public static GuidedTour? Current { get; private set; }

        private readonly IReadOnlyList<TourStep> steps;
        private readonly Action<TourEnd> ended;
        // The page changes under the tour (a window opens, the view switches), so the target is looked up again often.
        private readonly DispatcherTimer follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        private int index;
        private Window? host;
        private TourAdorner? adorner;
        private FrameworkElement? callout;
        private FrameworkElement? target;
        private bool scrolledTo;

        private GuidedTour(IReadOnlyList<TourStep> steps, Action<TourEnd> ended)
        {
            this.steps = steps;
            this.ended = ended;
            follow.Tick += (s, e) => Guard(Refresh);
        }

        // Starts at the first step; a tour already running is closed first (without its end being reported).
        public static void Run(IReadOnlyList<TourStep> steps, Action<TourEnd> ended)
        {
            Current?.Close();
            GuidedTour tour = new GuidedTour(steps, ended);
            Current = tour;
            TourSignals.Raised += tour.OnSignal;
            Loc.LanguageChanged += tour.OnLanguageChanged;
            tour.follow.Start();
            tour.Guard(() => tour.Show(0));
        }

        // Closes the tour and reports how it ended.
        public void Stop(TourEnd end)
        {
            if (Current != this)
                return;
            Close();
            try
            {
                ended(end);
            }
            catch (Exception e)
            {
                LogFailure(e);
            }
        }

        private void Close()
        {
            Current = null;
            follow.Stop();
            TourSignals.Raised -= OnSignal;
            Loc.LanguageChanged -= OnLanguageChanged;
            try
            {
                Detach();
            }
            catch (Exception e)
            {
                LogFailure(e);
            }
        }

        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                LogFailure(e);
                Stop(TourEnd.Failed);
            }
        }

        private static void LogFailure(Exception e) =>
            UnhandledErrors.LogQuietly("Guided tour", e.GetType().Name + ": " + e.Message);

        // ---- Steps --------------------------------------------------------

        private TourStep Step => steps[index];

        private void Show(int at)
        {
            for (index = at; index < steps.Count; index++)
            {
                if (Step.AlreadyDone?.Invoke() != true)
                    break;
            }
            if (index >= steps.Count)
            {
                Stop(TourEnd.Finished);
                return;
            }

            Step.Prepare?.Invoke();
            target = null;
            scrolledTo = false;
            callout = BuildCallout(Step);
            adorner?.SetCallout(callout);
            Refresh();
        }

        private void Choose(TourChoice choice)
        {
            switch (choice)
            {
                case TourChoice.Next: Show(index + 1); break;
                case TourChoice.Finish: Stop(TourEnd.Finished); break;
                case TourChoice.Dismiss: Stop(TourEnd.Skipped); break;
                case TourChoice.Begin: Stop(TourEnd.Begin); break;
            }
        }

        // After the action has run its course (a window opening, a list redrawing), not in the middle of it.
        private void OnSignal(string signal)
        {
            if (Step.Signal != signal)
                return;
            int at = index;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => Guard(() =>
            {
                if (Current == this && index == at)
                    Show(index + 1);
            })), DispatcherPriority.Background);
        }

        // The same step again in the new language, without running its Prepare a second time. The spotlight follows
        // the target as the page relays out in the new language (OnLayout).
        private void OnLanguageChanged() => Guard(() =>
        {
            callout = BuildCallout(Step);
            adorner?.SetCallout(callout);
            Refresh();
        });

        // ---- Spotlight ----------------------------------------------------

        private void Refresh()
        {
            FrameworkElement? found = Resolve();
            Window? want = found != null ? Window.GetWindow(found) : DefaultHost();
            if (want != host || adorner == null)
                Attach(want);

            if (found != null && !scrolledTo)
            {
                found.BringIntoView();
                scrolledTo = true;
            }
            target = found;
            UpdateHole();
        }

        private FrameworkElement? Resolve()
        {
            FrameworkElement? found = Step.Target?.Invoke();
            return found != null && found.IsVisible && found.ActualWidth > 0 && PresentationSource.FromVisual(found) != null
                ? found
                : null;
        }

        // An open project window is in front of everything else; otherwise the main window.
        private static Window? DefaultHost() => Open<ProjectWindow>() ?? Application.Current.MainWindow;

        private void Attach(Window? window)
        {
            Detach();
            if (window == null || !window.IsVisible || !(window.Content is UIElement content) || callout == null)
                return;
            AdornerLayer? layer = AdornerLayer.GetAdornerLayer(content);
            if (layer == null)
                return; // not laid out yet: the next tick tries again

            host = window;
            adorner = new TourAdorner(content, callout);
            layer.Add(adorner);
            window.PreviewKeyDown += OnKey;
            content.LayoutUpdated += OnLayout;
        }

        private void Detach()
        {
            if (adorner != null)
            {
                AdornerLayer.GetAdornerLayer(adorner.AdornedElement)?.Remove(adorner);
                adorner.AdornedElement.LayoutUpdated -= OnLayout;
                adorner.Release();
                adorner = null;
            }
            if (host != null)
            {
                host.PreviewKeyDown -= OnKey;
                host = null;
            }
        }

        // Follows the target as the window resizes or the page scrolls.
        private void OnLayout(object? sender, EventArgs e) => Guard(UpdateHole);

        private void UpdateHole()
        {
            if (adorner == null)
                return;
            UIElement page = adorner.AdornedElement;
            Rect? hole = null;
            if (target != null && target.IsVisible && page.IsAncestorOf(target))
            {
                // Only the part that's scrolled into view: the rest is under a header or toolbar, not clickable.
                Rect bounds = target.TransformToAncestor(page).TransformBounds(new Rect(target.RenderSize));
                for (DependencyObject d = VisualTreeHelper.GetParent(target); d != null && d != page; d = VisualTreeHelper.GetParent(d))
                {
                    if (d is ScrollViewer viewer)
                        bounds.Intersect(viewer.TransformToAncestor(page).TransformBounds(new Rect(viewer.RenderSize)));
                }
                if (!bounds.IsEmpty)
                    bounds.Inflate(HolePadding, HolePadding);
                bounds.Intersect(new Rect(page.RenderSize));
                if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                    hole = bounds;
            }
            adorner.SetHole(hole);
        }

        // ---- Keys ---------------------------------------------------------

        // Esc skips, except while typing in a box (it cancels an edit there). Enter and Right belong to whatever control
        // has the focus (a box, the spotlighted button, a list); they mean Next (or a card's main button) only with the
        // focus on nothing in particular, and Right also from the callout, where Enter presses the focused button.
        private void OnKey(object sender, KeyEventArgs e) => Guard(() =>
        {
            if (e.Key != Key.Escape && e.Key != Key.Enter && e.Key != Key.Right)
                return;
            DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
            bool inCallout = focused != null && callout != null && callout.IsAncestorOf(focused);

            if (e.Key == Key.Escape)
            {
                if (!inCallout && (focused is TextBoxBase || focused is PasswordBox))
                    return;
                e.Handled = true;
                Stop(TourEnd.Skipped);
                return;
            }
            if (inCallout ? e.Key == Key.Enter : focused is Control && !(focused is Window))
                return;
            e.Handled = true;
            Choose(Step.IsCard ? Step.PrimaryChoice : TourChoice.Next);
        });

        // ---- Callout ------------------------------------------------------

        private FrameworkElement BuildCallout(TourStep step)
        {
            List<TourStep> counted = steps.Where(s => !s.IsCard).ToList();
            int position = counted.IndexOf(step);

            StackPanel body = new StackPanel();
            if (!step.IsCard)
                body.Children.Add(Dots(position, counted.Count));

            body.Children.Add(new TextBlock
            {
                Text = step.Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("TextMain"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, step.IsCard ? 0 : 12, 0, 0)
            });
            TextBlock text = new TextBlock
            {
                Text = step.Text,
                FontSize = 13,
                Foreground = Res("TextBody"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
                Margin = new Thickness(0, 6, 0, 0)
            };
            // Read out by screen readers as the step appears: where it is, its title and its sentence.
            AutomationProperties.SetName(text, (step.IsCard ? "" : Loc.F("Tour.StepOf", position + 1, counted.Count) + " ")
                + step.Title + ". " + step.Text);
            AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Assertive);
            text.Loaded += (s, e) => UIElementAutomationPeer.CreatePeerForElement(text)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            body.Children.Add(text);

            DockPanel buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
            if (step.IsCard)
            {
                Button main = CalloutButton(Loc.T(step.Primary!), step.PrimaryChoice, null);
                main.Style = StrongButtonStyle();
                DockPanel.SetDock(main, Dock.Right);
                buttons.Children.Add(main);
                if (step.Secondary != null)
                {
                    Button other = CalloutButton(Loc.T(step.Secondary), step.SecondaryChoice, "GhostButtonStyle");
                    other.Margin = new Thickness(0, 0, 8, 0);
                    DockPanel.SetDock(other, Dock.Right);
                    buttons.Children.Add(other);
                }
            }
            else
            {
                Button skip = CalloutButton(Loc.T("Tour.Button.Skip"), TourChoice.Dismiss, "GhostButtonStyle");
                DockPanel.SetDock(skip, Dock.Left);
                buttons.Children.Add(skip);

                Button next = CalloutButton(Loc.T("Tour.Button.Next"), TourChoice.Next, "LinkButtonStyle");
                next.Foreground = Res("BtnHover"); // the darker accent: 6:1 on white
                next.FontSize = 13;
                next.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(next, Dock.Right);
                buttons.Children.Add(next);
            }
            body.Children.Add(buttons);

            // The card's shadow sits on a layer of its own behind the text: an effect on the text's own container
            // would turn off ClearType for all of it.
            CornerRadius radius = new CornerRadius(CardEffects.CardRadius);
            return new Grid
            {
                Width = 320,
                Children =
                {
                    new Border { Background = Res("BgPanel"), CornerRadius = radius, Effect = CardEffects.CreateShadow() },
                    new Border
                    {
                        BorderBrush = Res("Border"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = radius,
                        Padding = new Thickness(20, 16, 20, 16),
                        Child = body
                    }
                }
            };
        }

        private Button CalloutButton(string label, TourChoice choice, string? style)
        {
            Button button = new Button { Content = label };
            if (style != null)
                button.Style = (Style)Application.Current.FindResource(style);
            button.Click += (s, e) => Guard(() => Choose(choice));
            return button;
        }

        // A card's one main button: the app's button in the darker accent, so its white label reads at 6:1 (on the
        // usual accent it's 4.4:1); it lightens a little under the cursor.
        private static Style StrongButtonStyle()
        {
            Style style = new Style(typeof(Button), (Style)Application.Current.FindResource(typeof(Button)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Res("BtnHover")));
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88));
            style.Triggers.Add(hover);
            return style;
        }

        // Where this step is among the tour's steps (cards don't count): done and current in the accent, the current
        // one longer, the rest grey.
        private static FrameworkElement Dots(int position, int count)
        {
            StackPanel dots = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < count; i++)
            {
                dots.Children.Add(new Rectangle
                {
                    Width = i == position ? 16 : 6,
                    Height = 6,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = Res(i <= position ? "Accent" : "TextSub"),
                    Opacity = i <= position ? 1 : 0.75, // the grey at 3:1 on white
                    Margin = new Thickness(0, 0, 5, 0)
                });
            }
            return dots;
        }

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        // ---- Lookups for TourSteps ----------------------------------------

        // The element called `name` in that window (names repeat across windows, e.g. SaveButton). Null without one.
        public static FrameworkElement? Find(Window? window, string name) =>
            window != null && window.IsVisible ? FindIn(window, name) : null;

        // The open window of that kind, if any.
        public static T? Open<T>() where T : Window =>
            Application.Current.Windows.OfType<T>().FirstOrDefault(w => w.IsVisible);

        private static FrameworkElement? FindIn(DependencyObject parent, string name)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement element && element.Name == name)
                    return element;
                FrameworkElement? deeper = FindIn(child, name);
                if (deeper != null)
                    return deeper;
            }
            return null;
        }

        // ---- The overlay --------------------------------------------------

        // Dims the page except a rounded hole around the target, with a ring so the hole reads on light, dark and
        // picture backgrounds alike. Hosts the callout, placed beside the hole where it fits.
        private sealed class TourAdorner : Adorner
        {
            private const double Radius = 10;
            private const double Gap = 14;
            private const double Edge = 16;

            private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
            private static readonly Pen Halo = Frozen(new Pen(Brushes.White, 5));
            private readonly Pen ring;
            private FrameworkElement callout;
            private Rect? hole;

            public TourAdorner(UIElement page, FrameworkElement callout) : base(page)
            {
                this.callout = callout;
                ring = Frozen(new Pen(Res("Accent").CloneCurrentValue(), 2));
                AddVisualChild(callout);
            }

            public void SetCallout(FrameworkElement next)
            {
                RemoveVisualChild(callout);
                callout = next;
                AddVisualChild(next);
                InvalidateMeasure();
                InvalidateVisual();
            }

            public void Release() => RemoveVisualChild(callout);

            public void SetHole(Rect? next)
            {
                if (Nullable.Equals(next, hole))
                    return;
                hole = next;
                InvalidateArrange();
                InvalidateVisual();
            }

            protected override int VisualChildrenCount => 1;

            protected override Visual GetVisualChild(int index) => callout;

            protected override Size MeasureOverride(Size constraint)
            {
                callout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return AdornedElement.RenderSize;
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                callout.Arrange(new Rect(Place(AdornedElement.RenderSize, callout.DesiredSize), callout.DesiredSize));
                return finalSize;
            }

            // Below the hole, else above, else beside it; inside a hole too big for any of those (a whole page area),
            // at its bottom. Without a hole, in the middle. Always kept inside the window.
            private Point Place(Size area, Size size)
            {
                double ClampX(double x) => Math.Max(Edge, Math.Min(x, area.Width - size.Width - Edge));
                double ClampY(double y) => Math.Max(Edge, Math.Min(y, area.Height - size.Height - Edge));

                if (!(hole is Rect h))
                    return new Point(ClampX((area.Width - size.Width) / 2), ClampY((area.Height - size.Height) / 2));

                double centerX = ClampX(h.Left + (h.Width - size.Width) / 2);
                double centerY = ClampY(h.Top + (h.Height - size.Height) / 2);
                if (h.Bottom + Gap + size.Height + Edge <= area.Height)
                    return new Point(centerX, h.Bottom + Gap);
                if (h.Top - Gap - size.Height >= Edge)
                    return new Point(centerX, h.Top - Gap - size.Height);
                if (h.Right + Gap + size.Width + Edge <= area.Width)
                    return new Point(h.Right + Gap, centerY);
                if (h.Left - Gap - size.Width >= Edge)
                    return new Point(h.Left - Gap - size.Width, centerY);
                return new Point(centerX, ClampY(h.Bottom - size.Height - Edge));
            }

            protected override void OnRender(DrawingContext dc)
            {
                Rect all = new Rect(AdornedElement.RenderSize);
                if (!(hole is Rect h))
                {
                    dc.DrawRectangle(DimBrush, null, all);
                    return;
                }
                dc.DrawGeometry(DimBrush, null, new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(all), new RectangleGeometry(h, Radius, Radius)));
                dc.DrawRoundedRectangle(null, Halo, h, Radius, Radius);
                dc.DrawRoundedRectangle(null, ring, h, Radius, Radius);
            }

            // Clicks inside the hole reach the target; everywhere else the dimmed page doesn't take them.
            protected override HitTestResult? HitTestCore(PointHitTestParameters parameters)
            {
                if (hole is Rect h && h.Contains(parameters.HitPoint))
                    return null;
                return new PointHitTestResult(this, parameters.HitPoint);
            }

            private static T Frozen<T>(T freezable) where T : Freezable
            {
                freezable.Freeze();
                return freezable;
            }
        }
    }
}
