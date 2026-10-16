using System;
using System.Reflection;
using Microsoft.Win32.TaskScheduler;
using TidyMind;

// Harness probe <data>: what the reminder code registers, and how Windows answers for a known and an unknown sender.
static partial class Program
{
    static int Probe()
    {
        foreach (string id in new[] { App.UserModelId, "TidyMind.Probe.NeverRegistered" })
        {
            try
            {
                var notifier = Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(id);
                Console.WriteLine(id + ": Setting = " + notifier.Setting);
            }
            catch (Exception e)
            {
                Console.WriteLine(id + ": Setting threw " + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message);
            }
        }
        Guid probe = Guid.NewGuid();
        typeof(ReminderManager).GetMethod("RegisterTask", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { probe, DateTime.Now.AddYears(1), "probe" });
        using (TaskService service = new TaskService())
        {
            Task task = service.GetTask("TidyMind_Reminder_" + probe);
            Console.WriteLine(task.Xml);
            service.RootFolder.DeleteTask(task.Name);
        }
        return 0;
    }
}
