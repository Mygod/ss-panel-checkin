using System;
using System.Net.NetworkInformation;
using System.Threading;
using Mygod.Net;
using Mygod.Windows;

namespace Mygod.SSPanel.Checkin
{
    static class Program
    {
        private static Config config;
        private static DateTime lastUpdateCheckTime = DateTime.MinValue, nextCheckinTime;
        private static volatile bool running = true;
        private static readonly ManualResetEvent Terminator = new ManualResetEvent(false);

        private static void Main(string[] args)
        {
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
            config = new Config(args == null || args.Length <= 0 ? "config.csv" : args[0]);
            var background = new Thread(BackgroundWork);
            background.Start();
            Log.WriteLine("INFO", "Main", "ss-panel-checkin V{0} initialized, compiled on {1}.",
                          CurrentApp.Version, CurrentApp.CompilationTime);
            Log.ConsoleLine("Available actions:{0}[Q]uit", Environment.NewLine);
            var key = Console.ReadKey(true).Key;
            while (key != ConsoleKey.Q)
            {
                // More actions...
                key = Console.ReadKey(true).Key;
            }
            running = false;
            Terminator.Set();
            background.Join();
            Log.WriteLine("INFO", "Main", "ss-panel-checkin has been closed.");
        }

        private static void NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (e.IsAvailable) Terminator.Set();
        }

        private static void BackgroundWork()
        {
            while (running)
                if (NetworkInterface.GetIsNetworkAvailable())
                {
                    TimeSpan span;
                    var next = config.DoCheckin();
                    if (next == DateTime.MinValue)
                    {
                        Log.WriteLine("WARN", "Main", "No sites configured or all of them has failed.");
                        span = TimeSpan.MaxValue;
                    }
                    else
                    {
                        if (next > DateTime.Now && next > nextCheckinTime)
                            Log.ConsoleLine("Checkin finished. Next checkin time: {0}", nextCheckinTime = next);
                        span = nextCheckinTime - DateTime.Now;
                    }
                    if (DateTime.Now - lastUpdateCheckTime > TimeSpan.FromDays(1))
                    {
                        lastUpdateCheckTime = DateTime.Now;
                        var url = WebsiteManager.Url;
                        if (!string.IsNullOrWhiteSpace(url))
                            Log.WriteLine("INFO", "Main", "Update available. Download at: {0}", url);
                    }
                    var t = DateTime.Now - lastUpdateCheckTime + TimeSpan.FromDays(1);
                    if (t < span) span = t;
                    Terminator.WaitOne(span);
                }
                else Terminator.WaitOne(-1);
        }
    }
}
