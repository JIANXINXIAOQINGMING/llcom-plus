using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Window;

namespace llcom_plus.Tools
{
    class InputDialog
    {
        public static Tuple<bool, string> OpenDialog(string prompt, string defaultInput = "", string title = null)
        {
            InputDialogWindow dialog = new InputDialogWindow(prompt, defaultInput, title);
            var app = System.Windows.Application.Current;
            var owner = app?.Windows
                .OfType<System.Windows.Window>()
                .FirstOrDefault(window => window.IsActive) ?? app?.MainWindow;
            if (owner != null && owner.IsVisible)
                dialog.Owner = owner;
            else
                dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

            bool ret = dialog.ShowDialog() ?? false;
            return Tuple.Create(ret, dialog.Value);
        }
    }

    class MessageBox
    {
        public static void Show(string s)
        {
            try
            {
                InputDialog.OpenDialog(s, null, null);
            }
            catch
            {
                System.Windows.Forms.MessageBox.Show(
                    s,
                    "Notice",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.ServiceNotification);
            }
        }
    }
}
