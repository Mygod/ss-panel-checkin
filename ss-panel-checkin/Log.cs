using System;
using System.IO;

namespace Mygod.SSPanel.Checkin
{
    static class Log
    {
        private static readonly StreamWriter FileWriter = new StreamWriter("checkin.log", true) { AutoFlush = true };

        private static void WriteLine(string message)
        {
            lock (FileWriter)
            {
                Console.WriteLine(message);
                FileWriter.WriteLine(message);
            }
        }
        public static void WriteLine(string type, string id, string message, params object[] args)
        {
            WriteLine(string.Format("[{0}] ({2}) {1}: {3}", DateTime.Now, type, id, string.Format(message, args)));
        }
    }
}
