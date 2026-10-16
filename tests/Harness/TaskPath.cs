using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32.TaskScheduler;
using TidyMind;

// Harness taskpath <data>: the notification test through a real Task Scheduler task (it starts this exe with
// "--test-notification <token>", handled in Main against a temp data folder), and the popup handover to an open app.
static partial class Program
{
    static int TaskPath()
    {
        Console.WriteLine("Real Task Scheduler path");
        Guid token = Guid.NewGuid();
        ReminderManager.ScheduleTest(token, DateTime.Now.AddSeconds(5));
        using (TaskService service = new TaskService())
        {
            Microsoft.Win32.TaskScheduler.Task task = service.GetTask("TidyMind_NotificationTest_" + token);
            Check(task != null && task.Definition.Principal.LogonType == TaskLogonType.InteractiveToken
                && task.Definition.Actions.OfType<ExecAction>().First().Arguments.Contains("--test-notification"),
                "a one-off task, signed-in user's session (InteractiveToken), --test-notification");
        }
        NotificationResult result = null;
        for (int waited = 0; result == null && waited < 40000; waited += 500)
        {
            Thread.Sleep(500);
            result = ReminderManager.ReadTestResult(token);
        }
        Console.WriteLine("  result from the task's process: " + (result == null ? "none" : result.Outcome + " — " + result.Detail));
        Check(result?.Outcome == NotificationOutcome.Shown, "the scheduled process sent the notification");
        ReminderManager.RemoveTest(token);
        using (TaskService service = new TaskService())
            Check(service.GetTask("TidyMind_NotificationTest_" + token) == null && !File.Exists(ReminderManager.TestResultFile(token)),
                "the test task and its result file are gone");
        Windows.UI.Notifications.ToastNotificationManager.History.Clear(App.UserModelId);

        Console.WriteLine("Popup handover to an open TidyMind");
        App app = new App();
        app.InitializeComponent();
        Check(!ReminderPopups.HandToApp("x", "y"), "with no TidyMind open, nothing takes the handover");
        ReminderPopups.Listen(Dispatcher.CurrentDispatcher);
        Check(ReminderPopups.HandToApp("Harness popup", "Shown because the notification failed"), "the open app takes the handover");
        DispatcherFrame frame = new DispatcherFrame();
        DispatcherTimer stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        stop.Tick += (s, e) => { stop.Stop(); frame.Continue = false; };
        stop.Start();
        Dispatcher.PushFrame(frame);
        Window popup = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "Harness popup");
        Check(popup != null && popup.IsVisible && popup.Topmost, "it shows the reminder in a window on top");
        popup?.Close();
        ReminderPopups.StopListening();
        return 0;
    }

    // Started by the test task: like "TidyMind.exe --test-notification", against a temp data folder.
    static int RunScheduledTest(string token)
    {
        Redirect(Path.Combine(Path.GetTempPath(), "tidymind-harness-taskpath"));
        Loc.Initialize();
        ReminderManager.RunTest(Guid.Parse(token));
        return 0;
    }
}
