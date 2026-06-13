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

            var app = new App();
            StartupProfiler.Mark("Program.Main App created");

            StartupProfiler.Measure("Program.Main App.InitializeComponent", app.InitializeComponent);
            StartupProfiler.Mark("Program.Main App.Run begin");
            app.Run();
            StartupProfiler.Mark("Program.Main exit");
        }
    }
}
