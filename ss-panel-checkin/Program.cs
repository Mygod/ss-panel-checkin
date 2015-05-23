using System;
using System.Threading;

namespace Mygod.SSPanel.Checkin
{
    static class Program
    {
        private static readonly Config Config = new Config("config.csv");

        private static void Main()
        {
            Log.WriteLine("INFO", "Main", "ss-panel-checkin initialized, press Esc to exit.");
            while (true)
            {
                if (Console.KeyAvailable && Console.ReadKey().Key == ConsoleKey.Escape) break;
                Config.DoCheckin();
                Thread.Sleep(1000);
            }
            Log.WriteLine("INFO", "Main", "ss-panel-checkin has been closed.");
        }
    }
}
