using CASCConsole.Properties;
using CASCExplorer;
using SimpleWebServer;
using System;
using System.IO;
using System.Net;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace CASCConsole
{
    class Program
    {
        static object progressLock = new object();
        static CASCHandler _cascHandler = null;

        static void Main(string[] args)
        {

            if (args.Length != 4 && args.Length != 5)
            {
                Console.WriteLine("Invalid arguments count!");
                Console.WriteLine("Usage: CASCConsole <pattern> <destination> <localeFlags> <contentFlags> [<startWebServer>]");
                return;
            }
             

            Console.WriteLine("Settings:");
            Console.WriteLine("    WowPath: {0}", Settings.Default.StoragePath);
            Console.WriteLine("    OnlineMode: {0}", Settings.Default.OnlineMode);

            Console.WriteLine("Loading...");

            BackgroundWorkerEx bgLoader = new BackgroundWorkerEx();
            bgLoader.ProgressChanged += BgLoader_ProgressChanged;

            CASCConfig config = Settings.Default.OnlineMode
                ? CASCConfig.LoadOnlineStorageConfig(Settings.Default.Product, "us")
                : CASCConfig.LoadLocalStorageConfig(Settings.Default.StoragePath);

            CASCHandler cascHandler = CASCHandler.OpenStorage(config, bgLoader);
            _cascHandler = cascHandler;

            int startWebServer = 0;

            string pattern = args[0];
            string dest = args[1];
            LocaleFlags locale = (LocaleFlags)Enum.Parse(typeof(LocaleFlags), args[2]);
            ContentFlags content = (ContentFlags)Enum.Parse(typeof(ContentFlags), args[3]);
            if (args.Length == 5) {
                startWebServer = Int32.Parse(args[4]);
            }
            
            cascHandler.Root.LoadListFile(Path.Combine(Environment.CurrentDirectory, "listfile.txt"), bgLoader);
            CASCFolder root = cascHandler.Root.SetFlags(locale, content);

            Console.WriteLine("Loaded.");

            Console.WriteLine("Extract params:", pattern, dest, locale);
            Console.WriteLine("    Pattern: {0}", pattern);
            Console.WriteLine("    Destination: {0}", dest);
            Console.WriteLine("    LocaleFlags: {0}", locale);
            Console.WriteLine("    ContentFlags: {0}", content);
            Console.WriteLine("    startWebServer: {0}", startWebServer);

            if (startWebServer == 1)
            {
                WebServer ws = new WebServer(SendResponse, "http://localhost:8084/get/");
                ws.Run();
                Console.WriteLine("A simple webserver. Press a key to quit.");
                Console.ReadKey();
                ws.Stop();
            }
            else
            {
                Wildcard wildcard = new Wildcard(pattern, true, RegexOptions.IgnoreCase);

                foreach (var file in root.GetFiles())
                {
                    if (wildcard.IsMatch(file.FullName))
                    {
                        Console.Write("Extracting '{0}'...", file.FullName);

                        try
                        {
                            cascHandler.SaveFileTo(file.FullName, dest);
                            Console.WriteLine(" Ok!");
                        }
                        catch (Exception exc)
                        {
                            Console.WriteLine(" Error!");
                            Logger.WriteLine(exc.Message);
                        }
                    }
                }

                Console.WriteLine("Extracted.");
            }
        }

        public static string SendResponse(HttpListenerRequest request, HttpListenerResponse response)
        {
            String fileName = request.Url.LocalPath.Substring(5, request.Url.LocalPath.Length - 5);
            try {
                Stream stream = _cascHandler.OpenFile(fileName);

                response.ContentLength64 = stream.Length;
                response.ContentType = "application/octet-stream";
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Content-Disposition", "attachment; filename=" + fileName.Replace(" ", "_"));
                stream.CopyTo(response.OutputStream);
            } catch(Exception e){

            }
            return string.Format("<HTML><BODY>My web page.<br>{0}; "+fileName+"</BODY></HTML>", DateTime.Now);
        }

        private static void BgLoader_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            lock (progressLock)
            {
                if (e.UserState != null)
                    Console.WriteLine(e.UserState);

                DrawProgressBar(e.ProgressPercentage, 100, 72, '#');
            }
        }

        private static void DrawProgressBar(long complete, long maxVal, int barSize, char progressCharacter)
        {
            float perc = (float)complete / (float)maxVal;
            DrawProgressBar(perc, barSize, progressCharacter);
        }

        private static void DrawProgressBar(float percent, int barSize, char progressCharacter)
        {
            Console.CursorVisible = false;
            int left = Console.CursorLeft;
            int chars = (int)Math.Round(percent / (1.0f / (float)barSize));
            string p1 = String.Empty, p2 = String.Empty;

            for (int i = 0; i < chars; i++)
                p1 += progressCharacter;
            for (int i = 0; i < barSize - chars; i++)
                p2 += progressCharacter;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(p1);
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Console.Write(p2);

            Console.ResetColor();
            Console.Write(" {0}%", (percent * 100).ToString("N2"));
            Console.CursorLeft = left;
        }
    }
}
