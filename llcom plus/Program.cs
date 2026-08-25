using System;
using llcom_plus.Tools;

namespace llcom_plus
{
    internal static class Program
    {
        [STAThread]
        public static void Main()
        {
            StartupProfiler.Begin();
            StartupProfiler.Mark("Program.Main enter");

            var isFirstInstance = StartupProfiler.Measure(
                "Program.Main single instance lock",
                Global.TryAcquireSingleInstance);
            if (!isFirstInstance)
            {
                StartupProfiler.Mark("Program.Main duplicate instance silent exit");
                return;
            }

            StartupProfiler.Measure(
                "Program.Main taskbar identity",
                TaskbarIntegration.InitializeProcessIdentity);

            var app = new App();
            StartupProfiler.Mark("Program.Main App created");

            StartupProfiler.Measure("Program.Main App.InitializeComponent", app.InitializeComponent);
            StartupProfiler.Measure("Program.Main load settings and theme", Global.LoadSetting);
            StartupProfiler.Mark("Program.Main App.Run begin");
            app.Run();
            StartupProfiler.Mark("Program.Main exit");
        }
    }
}
