using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.IO;
using System.Threading;
using System.Net;
using System.Configuration;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Octokit;

namespace XRPLCrawler
{
    class Program
    {
        static Stack<string> stack = new Stack<string>();
        static HttpClient client;
        static StreamWriter fs;
        static StreamWriter fsErrors;
        static HashSet<string> tested = new HashSet<string>();

        public const string ServiceName = "XRPL Crawler";
        static bool isRunning = false;

        public class Service : ServiceBase
        {
            public Service()
            {
                ServiceName = Program.ServiceName;
            }

            protected override void OnStart(string[] args)
            {
                Program.Start(args);

                var worker = new Thread(StartServiceProcess);
                worker.Name = "XRPLCrawler";
                worker.IsBackground = false;
                worker.Start();
            }

            protected override void OnStop()
            {
                Program.Stop();
            }
        }

        static void Main(string[] args)
        {
            var filename = ConfigurationManager.AppSettings.Get("NodeListName");
            var path = ConfigurationManager.AppSettings.Get("PathForNodeList");
            if (path == null)
            {
                path = "";
            }
            if (string.IsNullOrEmpty(filename))
            {
                throw new Exception("Filename for output file must be set in the app.config file.  Aborting...");
            }

            if (!Environment.UserInteractive)
            {
                // running as service
                using (var service = new Service())
                    ServiceBase.Run(service);
            }
            else
            {
                // running as console app
                Start(args);

                StartProcess(path, filename);

                string githubOAuthToken = ConfigurationManager.AppSettings.Get("SerivceGithubOAuthToken");

                if (!string.IsNullOrEmpty(githubOAuthToken))
                {
                    Console.WriteLine("Upload history node list to Github? (y/n)");
                    string key = "";
                    while (key != "y" && key != "n")
                    {
                        key = Console.ReadLine().ToLower();
                        if (key == "y")
                        {
                            UploadToGithub(path, filename);
                        }
                        else if (key != "n")
                        {
                            Console.WriteLine("Upload history node list to Github? (y/n)");
                        }
                    }
                }

                Stop();
            }
        }

        private static void Start(string[] args)
        {
            isRunning = true;
        }

        private static void Stop()
        {
            isRunning = false;
        }

        private static void StartServiceProcess()
        {
            var filename = ConfigurationManager.AppSettings.Get("NodeListName");
            var path = ConfigurationManager.AppSettings.Get("PathForNodeList");
            if (path == null)
            {
                path = "";
            }
            if (string.IsNullOrEmpty(filename))
            {
                throw new Exception("Filename for output file must be set in the app.config file.  Aborting...");
            }

            string scheduleDay = ConfigurationManager.AppSettings.Get("ServiceScheduleDay");
            string scheduleTime = ConfigurationManager.AppSettings.Get("ServiceScheduleTime");

            if (string.IsNullOrEmpty(scheduleDay)|| string.IsNullOrEmpty(scheduleTime))
            {
                throw new Exception("There must be a scheduled day of the week and time set in the app.config file.  Aborting...");
            }
            TimeSpan time;
            TimeSpan.TryParse(scheduleTime, out time);
            if (time == null)
            {
                throw new Exception("There must be a valid time set in the app.config file.  Aborting...");
            }
            DayOfWeek dayOfWeek = (DayOfWeek)Enum.Parse(typeof(DayOfWeek), scheduleDay, true);

            var lastDateTimeRun = DateTime.Now;
            var nextDateTimeRun = lastDateTimeRun.Next(dayOfWeek, time);

            //setup weekly schedule
            while (isRunning)
            {
                if (nextDateTimeRun < DateTime.Now)
                {
                    lastDateTimeRun = DateTime.Now;
                    nextDateTimeRun = lastDateTimeRun.Next(dayOfWeek, time);

                    StartProcess(path, filename, true);

                    UploadToGithub(path, filename);
                }
                Thread.Sleep(30000);
            }
        }

        private static void StartProcess(string path, string filename, bool writeToGithub = false)
        {
            var originNodeUrl = ConfigurationManager.AppSettings.Get("OriginNodeUrl");
            
            using (fs = File.CreateText(path + filename))
            {
                using (fsErrors = File.CreateText(path + "error.txt"))
                {
                    using (client = new HttpClient())
                    {
                        client.Timeout = new TimeSpan(0, 0, 3);
                        ServicePointManager.Expect100Continue = true;
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                        ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;
                        GatherAllNodes(originNodeUrl);
                    }
                }
            }
        }

        private static void GatherAllNodes(string url)
        {
            stack.Push(url);
            while (stack.Count > 0)
            {
                Console.Clear();
                Console.Write(stack.Count);
                GetServerPeers(stack.Pop());
            }
        }

        private static void GetServerPeers(string url)
        {
            if (!tested.Contains(url))
            {
                tested.Add(url);
                string data = null;
                try
                {
                    var t = client.GetStringAsync(url);
                    t.Wait();
                    data = t.Result;
                    dynamic obj = JsonConvert.DeserializeObject(data);
                    string layer = ConverToLayer(obj.server.complete_ledgers.Value as string);
                    if (layer != null)
                    {
                        var server = url.Replace("https://", "").Replace(":51235/crawl", "") + "\t" + layer;
                        fs.WriteLine(server);
                        fs.Flush();
                        for (var i = 0; i < obj.overlay.active.Count; i++)
                        {
                            var peer = obj.overlay.active[i];
                            if (peer["ip"] != null)
                            {
                                string peerUrl = "https://" + peer.ip + ":51235/crawl";
                                peerUrl = peerUrl.Replace("::ffff:", "");
                                if (!tested.Contains(peerUrl))
                                {
                                    stack.Push(peerUrl);
                                }
                            }
                        }
                    }
                }
                catch(Exception ex)
                {
                    fsErrors.WriteLine(url + ":" + ex.Message + (ex.InnerException != null ? ":" + ex.InnerException.Message + (ex.InnerException.InnerException != null ? ":" + ex.InnerException.InnerException.Message : "") : ""));
                    fsErrors.Flush();
                }
            }
        }

        private static string ConverToLayer(string completeLedgers)
        {
            string result = null;
            if (!string.IsNullOrEmpty(completeLedgers) && completeLedgers != "empty")
            {
                string temp = completeLedgers.Substring(0, completeLedgers.IndexOf("-"));
                if (temp == "32570")
                {
                    result = "1";
                }
                else
                {
                    result = "0";
                }
            }
            return result;
        }

        private static void UploadToGithub(string path, string filename)
        {
            string githubOAuthToken = ConfigurationManager.AppSettings.Get("SerivceGithubOAuthToken");
            string githubUploadUser = ConfigurationManager.AppSettings.Get("SerivceGithubUploadUsername");
            string githubUploadRepo = ConfigurationManager.AppSettings.Get("SerivceGithubUploadRepositoryName");
            string githubUploadDir = ConfigurationManager.AppSettings.Get("SerivceGithubUploadDirectoryName");

            if (!string.IsNullOrEmpty(githubOAuthToken))
            {
                var client = new GitHubClient(new ProductHeaderValue("xprl-crawler"));
                var tokenAuth = new Credentials(githubOAuthToken);
                client.Credentials = tokenAuth;

                string data = File.ReadAllText(path + filename);
                var file = client.Repository.Content.GetAllContentsByRef(githubUploadUser, githubUploadRepo, githubUploadDir + filename, "master").GetAwaiter().GetResult();
                var updateRequest = new UpdateFileRequest("Weekly auto update", data, file[0].Sha);

                client.Repository.Content.UpdateFile(githubUploadUser, githubUploadRepo, githubUploadDir + filename, updateRequest).GetAwaiter().GetResult();
            }
        }
    }

    public static class Extenstions
    {
        //Extenstion methods
        public static DateTime Next(this DateTime from, DayOfWeek dayOfWeek, TimeSpan timeOfDay)
        {
            int start = (int)from.DayOfWeek;
            int target = (int)dayOfWeek;
            if (target <= start)
                target += 7;
            DateTime result = from.AddDays(target - start);
            TimeSpan startTime = from.TimeOfDay;
            TimeSpan targetTime = timeOfDay;
            result = result.Add(targetTime - startTime);
            return result;
        }
    }
}
