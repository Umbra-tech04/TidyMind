using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace TidyMind
{
    // A window's undo keys and its toast. Ctrl+Z undoes, Ctrl+Y and Ctrl+Shift+Z redo, the newest step of the histories
    // the window reaches first; never while an editable text box has the keyboard (it keeps its own undo, also when it
    // has nothing left to undo, so running out of typing to undo never deletes data). The toast is a dark bar at the
    // bottom of the window: "“Car” deleted   Undo" right after a delete, "Undone: …   Redo" after an undo. It never
    // takes the keyboard; Tab reaches its buttons, Esc in it closes it, it stays while the mouse or the keyboard is in it,
    // and a screen reader reads it out (a polite live region).
    public sealed class UndoHost
    {
        private static readonly TimeSpan ShownFor = TimeSpan.FromSeconds(8);
        private static readonly List<UndoHost> hosts = new List<UndoHost>();

        private readonly Window window;
        private readonly Func<IEnumerable<UndoHistory>> histories;
        private readonly Func<UndoStep, bool> accepts;
        private readonly Func<bool> ready;
        private readonly Action<UndoHistory, UndoStep> applied;

        private readonly Border toast;
        private readonly TextBlock message;
        private readonly Button action;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = ShownFor };
        private UndoStep? shown;
        private bool shownUndoes; // the toast's button undoes `shown` (else redoes it)

        // histories: what the window's keys undo. applied: after an undo or redo here, so the window shows the data again.
        // accepts: the steps it may undo (the item window: its own item's). ready: false while the keys must wait (a tour,
        // an edit going on). bottom: the toast's distance from the window's bottom edge (above a footer of buttons).
        private UndoHost(Window window, Func<IEnumerable<UndoHistory>> histories, Action<UndoHistory, UndoStep>? applied,
            Func<UndoStep, bool>? accepts, Func<bool>? ready, double bottom)
        {
            this.window = window;
            this.histories = histories;
            this.applied = applied ?? ((h, s) => { });
            this.accepts = accepts ?? (s => true);
            this.ready = ready ?? (() => true);

            message = new TextBlock
            {
                Foreground = (Brush)Application.Current.FindResource("SidebarText"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
            action = new Button { Style = (Style)Application.Current.FindResource("ToastButtonStyle"), Margin = new Thickness(16, 0, 0, 0) };
            action.Click += (s, e) => ActOnShown();
            Button close = new Button
            {
                Style = (Style)Application.Current.FindResource("ToastButtonStyle"),
                // Its own text block: the app's text style would otherwise give the glyph the reading font.
                Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10 }, // Cancel
                Foreground = message.Foreground,
                Margin = new Thickness(4, 0, 0, 0)
            };
            close.Click += (s, e) => Hide();

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(message);
            row.Children.Add(action);
            row.Children.Add(close);
            toast = new Border
            {
                Background = (Brush)Application.Current.FindResource("Sidebar"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 6, 6, 6),
                Margin = new Thickness(16, 0, 16, bottom),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.25 },
                Child = row
            };
            Loc.Track(toast, () =>
            {
                Hide(); // what it says was in the language before
                AutomationProperties.SetName(close, Loc.T("Undo.Dismiss"));
                close.ToolTip = Loc.T("Undo.Dismiss");
            });
            toast.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape)
                    return;
                Hide();
                e.Handled = true; // not the window's Esc (which may close it)
            };
            toast.MouseLeave += (s, e) => timer.Start();
            toast.IsKeyboardFocusWithinChanged += (s, e) => timer.Start();
            // Kept while the pointer is on it, or the keyboard (not a click, which leaves the focus on the button) is in it.
            timer.Tick += (s, e) =>
            {
                if (!toast.IsMouseOver && !(toast.IsKeyboardFocusWithin && InputManager.Current.MostRecentInputDevice is KeyboardDevice))
                    Hide();
            };

            // Over the window's own content, which stays where it is.
            if (window.Content is UIElement content)
            {
                window.Content = null;
                Grid layer = new Grid();
                layer.Children.Add(content);
                layer.Children.Add(toast);
                window.Content = layer;
            }
            window.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown));
        }

        public static UndoHost Attach(Window window, Func<IEnumerable<UndoHistory>> histories, Action<UndoHistory, UndoStep>? applied = null,
            Func<UndoStep, bool>? accepts = null, Func<bool>? ready = null, double bottom = 24)
        {
            UndoHost host = new UndoHost(window, histories, applied, accepts, ready, bottom);
            hosts.Add(host);
            UndoService.Changed += host.Follow;
            window.Closed += (s, e) =>
            {
                hosts.Remove(host);
                host.timer.Stop();
                UndoService.Changed -= host.Follow;
            };
            return host;
        }

        // What the toast says now, or null while it's hidden (for the harness).
        public string? Shown => toast.Visibility == Visibility.Visible ? message.Text : null;

        // The toast's button, as Tab reaches it (for the harness).
        internal Button ToastButton => action;

        // The keyboard is in the toast: its Esc closes the toast, not the window.
        public bool ToastHasKeyboard => toast.IsKeyboardFocusWithin;

        internal static UndoHost? Of(Window window) => hosts.FirstOrDefault(h => h.window == window);

        // ---- After a delete -----------------------------------------------

        // Said in the window it happened in (once a window it closed is gone): the one in front that reaches its history.
        public static void Announce(UndoStep step)
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                if (step.History?.NextUndo != step)
                    return;
                List<UndoHost> able = hosts.Where(h => h.window.IsVisible && h.window.IsEnabled && h.Reaches(step)).ToList();
                UndoHost? host = able.LastOrDefault(h => h.window.IsActive) ?? able.LastOrDefault();
                host?.Show(step.Label, step, undoes: true);
            }), DispatcherPriority.Background);
        }

        private bool Reaches(UndoStep step) => histories().Contains(step.History) && accepts(step);

        // ---- Keys ---------------------------------------------------------

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled)
                return;
            ModifierKeys modifiers = Keyboard.Modifiers;
            bool undo = e.Key == Key.Z && modifiers == ModifierKeys.Control;
            bool redo = (e.Key == Key.Y && modifiers == ModifierKeys.Control) || (e.Key == Key.Z && modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
            if ((!undo && !redo) || TextHasKeyboard() || !ready())
                return;
            e.Handled = true;
            if (undo)
                Undo();
            else
                Redo();
        }

        // An editable text box (a rich text box too) or a password box: Ctrl+Z is its own. Selectable read-only text isn't.
        public static bool TextHasKeyboard() =>
            Keyboard.FocusedElement is TextBoxBase box ? !box.IsReadOnly : Keyboard.FocusedElement is PasswordBox;

        // Ctrl+Z: the newest step of the window's histories. True if one was undone.
        public bool Undo() => Apply(undo: true);

        public bool Redo() => Apply(undo: false);

        private bool Apply(bool undo)
        {
            UndoStep? step = histories()
                .Select(h => undo ? h.NextUndo : h.NextRedo)
                .Where(s => s != null && accepts(s))
                .OrderByDescending(s => s!.Stamp)
                .FirstOrDefault();
            if (step == null)
            {
                Show(Loc.T(undo ? "Undo.NothingToUndo" : "Undo.NothingToRedo"), null, false);
                return false;
            }
            return Apply(step, undo);
        }

        private bool Apply(UndoStep step, bool undo)
        {
            UndoHistory history = step.History!;
            if (!history.Apply(step, undo, window))
            {
                Hide();
                applied(history, step); // a step dropped as a conflict: the window shows what is stored
                return false;
            }
            applied(history, step);
            KeepKeyboard();
            Show(Loc.F(undo ? "Undo.Undone" : "Undo.Redone", step.Label), step, undoes: !undo);
            return true;
        }

        // The window drew itself again from the data, and the element that had the keyboard may be gone with it: the
        // keyboard stays in the window, so the next Ctrl+Z (or Ctrl+Y) still reaches it.
        private void KeepKeyboard()
        {
            if (window.IsActive && !window.IsKeyboardFocusWithin)
                window.Focus();
        }

        // ---- The toast ----------------------------------------------------

        private void ActOnShown()
        {
            UndoStep? step = shown;
            bool undo = shownUndoes;
            if (step == null || step.History == null || step != (undo ? step.History.NextUndo : step.History.NextRedo))
            {
                Hide();
                return;
            }
            Apply(step, undo);
        }

        // step: what the button undoes (undoes) or redoes; none: no button.
        private void Show(string text, UndoStep? step, bool undoes)
        {
            shown = step;
            shownUndoes = undoes;
            message.Text = text;
            action.Content = Loc.T(undoes ? "Undo.Button" : "Undo.RedoButton");
            action.ToolTip = Loc.T(undoes ? "Undo.ButtonTip" : "Undo.RedoTip");
            action.Visibility = step == null ? Visibility.Collapsed : Visibility.Visible;
            toast.Visibility = Visibility.Visible;
            timer.Stop();
            timer.Start();
            (UIElementAutomationPeer.FromElement(message) ?? UIElementAutomationPeer.CreatePeerForElement(message))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private void Hide()
        {
            timer.Stop();
            shown = null;
            bool hadKeyboard = toast.IsKeyboardFocusWithin;
            toast.Visibility = Visibility.Collapsed;
            if (hadKeyboard)
                window.Focus(); // not left on a button that is gone
        }

        // A step undone, redone or dropped elsewhere: a toast whose button can't do what it says any more goes.
        private void Follow()
        {
            if (shown == null)
                return;
            UndoHistory? history = shown.History;
            if (history == null || shown != (shownUndoes ? history.NextUndo : history.NextRedo))
                Hide();
        }
    }

    // A page of the main window that shows stored data: after an undo or redo it reads it again.
    public interface IUndoPage
    {
        void ReloadAfterUndo();
    }
}
