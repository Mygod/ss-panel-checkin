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

        private static void Main(string[] args)
        {
            config = new Config(args == null || args.Length <= 0 ? "config.csv" : args[0]);
            Log.WriteLine("INFO", "Main", "ss-panel-checkin V{0} initialized, press Esc to exit.", CurrentApp.Version);
            while (true)
            {
                if (Console.KeyAvailable && Console.ReadKey().Key == ConsoleKey.Escape) break;
                if (NetworkInterface.GetIsNetworkAvailable())
                {
                    var next = config.DoCheckin();
                    if (next == DateTime.MinValue)
                    {
                        Log.WriteLine("WARN", "Main", "No sites configured. Closing...");
                        break;
                    }
                    if (next > DateTime.Now && next > nextCheckinTime)
                        Console.WriteLine("Checkin finished. Next checkin time: {0}", nextCheckinTime = next);
                    if (DateTime.Now - lastUpdateCheckTime > TimeSpan.FromDays(1))
                    {
                        lastUpdateCheckTime = DateTime.Now;
                        var url = WebsiteManager.Url;
                        if (!string.IsNullOrWhiteSpace(url))
                            Log.WriteLine("INFO", "Main", "Update available. Download at: {0}", url);
                    }
                }
                Thread.Sleep(1000);
            }
            Log.WriteLine("INFO", "Main", "ss-panel-checkin has been closed.");
        }
    }
}
