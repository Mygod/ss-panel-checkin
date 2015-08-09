using System;
using System.IO;

namespace Mygod.SSPanel.Checkin
{
    /// <summary>
    /// A thread-safe logger.
    /// </summary>
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
        public static void WriteLine(string type, string id, string message)
        {
            WriteLine(string.Format("[{0}] ({2}) {1}: {3}", DateTime.Now, type, id, message));
        }

        public static void ConsoleLine(string message, params object[] args)
        {
            lock (FileWriter) Console.WriteLine(message, args);
        }
    }
}
