using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace TidyMind
{
    // Every message box in the app: Windows' own MessageBox (its look, its keys, Ctrl+C), with its buttons labelled in
    // the app's language rather than in Windows' display language. The labels are set through a hook on this thread
    // that sees the box as it opens, then removes itself.
    public static class Msg
    {
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None) =>
            Show(null, text, caption, buttons, icon, defaultResult);

        public static MessageBoxResult Show(Window? owner, string text, string caption,
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            using (new ButtonLabels(buttons))
                return owner != null
                    ? MessageBox.Show(owner, text, caption, buttons, icon, defaultResult)
                    : MessageBox.Show(text, caption, buttons, icon, defaultResult);
        }

        private sealed class ButtonLabels : IDisposable
        {
            private const int WH_CBT = 5, HCBT_ACTIVATE = 5;
            private const int IDOK = 1, IDCANCEL = 2, IDYES = 6, IDNO = 7;

            private readonly MessageBoxButton buttons;
            private readonly HookProc proc; // held here so it isn't collected while Windows can still call it
            private IntPtr hook;

            public ButtonLabels(MessageBoxButton buttons)
            {
                this.buttons = buttons;
                proc = OnHook;
                hook = SetWindowsHookEx(WH_CBT, proc, IntPtr.Zero, GetCurrentThreadId());
            }

            private IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
            {
                if (code == HCBT_ACTIVATE && IsDialog(wParam))
                {
                    // A box with only OK gives that button the Cancel id (Esc closes it), so both are labelled OK.
                    switch (buttons)
                    {
                        case MessageBoxButton.OK:
                            SetDlgItemText(wParam, IDOK, Loc.T("Common.Ok"));
                            SetDlgItemText(wParam, IDCANCEL, Loc.T("Common.Ok"));
                            break;
                        case MessageBoxButton.OKCancel:
                            SetDlgItemText(wParam, IDOK, Loc.T("Common.Ok"));
                            SetDlgItemText(wParam, IDCANCEL, Loc.T("Common.Cancel"));
                            break;
                        default:
                            SetDlgItemText(wParam, IDYES, Loc.T("Common.Yes"));
                            SetDlgItemText(wParam, IDNO, Loc.T("Common.No"));
                            SetDlgItemText(wParam, IDCANCEL, Loc.T("Common.Cancel"));
                            break;
                    }
                    Dispose();
                }
                return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            }

            private static bool IsDialog(IntPtr window)
            {
                StringBuilder name = new StringBuilder(16);
                GetClassName(window, name, name.Capacity);
                return name.ToString() == "#32770";
            }

            public void Dispose()
            {
                if (hook == IntPtr.Zero)
                    return;
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }

            private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

            [DllImport("user32.dll")]
            private static extern bool UnhookWindowsHookEx(IntPtr hook);

            [DllImport("user32.dll")]
            private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern bool SetDlgItemText(IntPtr dialog, int id, string text);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetClassName(IntPtr window, StringBuilder name, int max);

            [DllImport("kernel32.dll")]
            private static extern uint GetCurrentThreadId();
        }
    }
}
