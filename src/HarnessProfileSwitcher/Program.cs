using System;
using System.Threading;
using System.Windows.Forms;
using HarnessProfileSwitcher.Application;
using HarnessProfileSwitcher.Core;
using HarnessProfileSwitcher.UI;

namespace HarnessProfileSwitcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] arguments)
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                if (arguments.Length == 3 && string.Equals(arguments[0], "--render-preview", StringComparison.OrdinalIgnoreCase))
                {
                    PreviewRenderer.Render(arguments[1], arguments[2]);
                    return 0;
                }

                bool ownsMutex;
                using (Mutex mutex = new Mutex(true, @"Local\HarnessProfileSwitcher.UI", out ownsMutex))
                {
                    if (!ownsMutex)
                    {
                        MessageBox.Show("Harness Profile Switcher is already open.", "Harness Profile Switcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 2;
                    }
                    System.Windows.Forms.Application.Run(new MainForm(new ApplicationServices()));
                }
                return 0;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    SafeUserMessages.ForStartup(exception),
                    "Harness Profile Switcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
