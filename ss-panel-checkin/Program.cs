using System;
using System.Net.NetworkInformation;
using System.Threading;
using Mygod.Net;

namespace Mygod.SSPanel.Checkin
{
    static class Program
    {
        private static readonly Config Config = new Config("config.csv");
        private static DateTime lastUpdateCheckTime = DateTime.MinValue;

        private static void Main()
        {
            Log.WriteLine("INFO", "Main", "ss-panel-checkin initialized, press Esc to exit.");
            while (true)
            {
                if (Console.KeyAvailable && Console.ReadKey().Key == ConsoleKey.Escape) break;
                if (NetworkInterface.GetIsNetworkAvailable())
                {
                    Config.DoCheckin();
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
