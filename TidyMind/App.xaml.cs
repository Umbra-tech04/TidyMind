using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace TidyMind
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Launched by a Task Scheduler reminder: show the toast and exit, no UI.
            if (TryHandleRemindArgument(e.Args))
            {
                Shutdown();
                return;
            }

            QuickTodoService.RemoveOldCompleted(DateTime.Today);

            EventManager.RegisterClassHandler(typeof(Window), Window.PreviewKeyDownEvent,
                new KeyEventHandler(GlobalPreviewKeyDown));
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(AnimateWindowOpen));

            MainWindow window = new MainWindow();
            this.MainWindow = window;
            this.ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }

        private bool TryHandleRemindArgument(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--remind" && i + 1 < args.Length)
                {
                    if (Guid.TryParse(args[i + 1], out Guid reminderId))
                        ReminderManager.FireReminder(reminderId);

                    return true;
                }
            }

            return false;
        }

        // Fades the content, not the window: top-level Opacity needs AllowsTransparency. All windows share the
        // BgMain background, so the fade never shows a colour change.
        private void AnimateWindowOpen(object sender, RoutedEventArgs e)
        {
            if (sender is MainWindow || !(sender is Window window) || !(window.Content is UIElement content))
                return;

            // Hidden from the very first frame; the fade starts only once the window has actually drawn,
            // otherwise a slow first render (e.g. ProjectWindow) flashes at full opacity or eats the animation.
            content.Opacity = 0;

            window.ContentRendered += (s, args) =>
            {
                DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
                };
                fade.Completed += (o, done) =>
                {
                    content.Opacity = 1;
                    content.BeginAnimation(UIElement.OpacityProperty, null);
                };
                content.BeginAnimation(UIElement.OpacityProperty, fade);
            };
        }

        // The selection if there is one, otherwise the whole text.
        private void SelectableTextCopy_Click(object sender, RoutedEventArgs e)
        {
            if (((ContextMenu)((MenuItem)sender).Parent).PlacementTarget is TextBox box)
                TextCopy.Copy(box.SelectionLength > 0 ? box.SelectedText : box.Text);
        }

        // Registered on the Window class, so this sees every key press in every window — one place for all shortcuts.
        private void GlobalPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (OverlayManager.TryHandleShortcut(e))
                e.Handled = true;
        }
    }
}
