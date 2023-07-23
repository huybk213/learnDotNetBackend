using AudioApp.Models;
using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Timers;
using Newtonsoft.Json.Linq; 
using Serilog;

namespace audioConverter.Services
{
    public class AudioUrlConverter
    {
        public enum DeleteRecordResult
        {
            InvalidParam,
            UrlNotExist,
            InternalError,
            Ok
        }

        private enum FfmpegFileOutput
        {
            M3u8,
            Mp3,
        }

        public class SingleProcessInfo
        {
            public string TargetTrashPathWillBeClean = String.Empty;
            public Process Process = default!;
            public CancellationTokenSource CancleToken  = default!;
            public int ConvertTimeout;
            public bool RecordToFile = false;
            public int RetiesTime;
        };

        public class ConvertUrlInfo
        {
            public string Result = String.Empty;
            public string OutputStreamUrl = String.Empty;
            public string OutputRecordFileUrl = String.Empty;
            public int RecordTimeoutInSec;
        };


        public string LocalFolderRecorded = String.Empty;
        public int RecordTimeoutInSec;
        public string OutputStreamUrl = String.Empty;
        public string OutputRecordFileUrl = String.Empty;
        public string InputUrl = String.Empty;

        private static object _ensureThreadSafe = new Object();
        private static string _nginxPath = String.Empty;
        private static string _ffmpegPath = String.Empty;
        private static string _prefixUrl = String.Empty;
        public static List<AudioUrlConverter> ListAudioObjInfo = new List<AudioUrlConverter>();
        public static List<string> ListRetryingUrls = new List<string>();
        private List<SingleProcessInfo> _processesInfo = new List<SingleProcessInfo>();
        private const int MAX_TIMEOUT_WAIT_PROCESS_EXIT = 500;   /*ms*/
        private const int MAX_NUMBER_OF_TS_FILE_KEEP_IN_DISK = 12;
        private const int CONVERT_M3U8_FOREVER = Int32.MaxValue;
        private const string M3U8_FILE_NAME = "/audio.m3u8";
        private const string MP3_FILE_NAME = "/audio.mp3";
        private const bool _autoRestartStream = false;
        private const bool AUTO_RESTART_PROCESS_WHEN_EXIT = true;
        private const int DEFAULT_RETRIES_WHEN_PROCESS_FAIL = 1000;     //
        private const int SLEEP_TIME_MS_BEFORE_RESTART_PROCESS = 60000;

        //This function create unique string from a input string
        private static string GetShortPathToSharedNginxFolder(string inputUrl)
        {
            // Use input string to calculate MD5 hash
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(inputUrl);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                return Convert.ToHexString(hashBytes).ToLower();
            }
        }

        private static string GetFullPathToSharedNginxFolder(string inputUrl)
        {
            return _nginxPath + GetShortPathToSharedNginxFolder(inputUrl);
        }

        public static void SetNginxPath(string path, string prefixUrl)
        {
            _nginxPath = path;
            _prefixUrl = prefixUrl;
            Console.WriteLine("Nginx = {0}, url = {1}", _nginxPath, _prefixUrl);
        }

        public static void SetFFmpegBinaryPath(string path)
        {
            _ffmpegPath = path;
            Console.WriteLine("FFmpegPath = {0}", _ffmpegPath);
        }

        public static string GenUrl(string localFileName)
        {
            return _prefixUrl + localFileName;
        }

        private static async Task CloseProcessAsync(Process p, int maxWaitTimeInMs)
        {
            if (p == null)
            {
                return;
            }

            if (p.HasExited)
            {
                Console.WriteLine($"Process {p.Id} already exited");
                return;
            }

            try
            {
                Console.WriteLine($"Killing PID {p.Id}");
                p.Kill();
                
                // Wait until exist or timeout
                int maxWaitTime = maxWaitTimeInMs / 50;
                while (p.HasExited == false && maxWaitTime > 0)
                {
                    await Task.Delay(50);
                    maxWaitTime--;
                }

                if (p.HasExited == false)
                {
                    Console.WriteLine($"Kill PID {p.Id} -> FAILED");
                }
                else
                {
                    Console.WriteLine($"Kill PID {p.Id} -> OK");
                }
            }
            catch (Exception ex) { Console.WriteLine(ex.ToString()); }
        }

        //Delete audio .*ts trash file, keep .mp3 file
        public static async Task DeleteAudioTrashFilesAsync(string targetDirectory)
        {
            var audioDir = new DirectoryInfo(targetDirectory);
            var masks = new[] { "*.ts", "*.m3u8" };
            var files = masks.SelectMany(audioDir.EnumerateFiles);
            foreach (var file in files)
            {
                try
                {
                    // Console.WriteLine($"Delete file {file.ToString()}");
                    await Task.Factory.StartNew(() => file.Delete());
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                }
            }
        }

        private static void RemoveProcessFromList(AudioUrlConverter obj, SingleProcessInfo p)
        {
            lock (_ensureThreadSafe)
            {
                for (int i = 0; i < ListAudioObjInfo.Count; i++)
                {
                    if (obj == ListAudioObjInfo[i])
                    {
                        for (int j = 0; j < ListAudioObjInfo[i]._processesInfo.Count; j++)
                        {
                            if (p == ListAudioObjInfo[i]._processesInfo[j])
                            {
                                Console.WriteLine($"Removed sub process {j}");
                                ListAudioObjInfo[i]._processesInfo.RemoveAt(j);
                            }
                        }
                        if (ListAudioObjInfo[i]._processesInfo.Count == 0)
                        {
                            Console.WriteLine("No more sub-process, remove whole object");
                            ListAudioObjInfo.RemoveAt(i);
                        }
                    }
                    
                }
            }
        }

        private static async Task RunProcessUntilTimeout(AudioUrlConverter obj, SingleProcessInfo p)
        {
            if (p == null)
            {
                return;
            }
            try
            {
                while (p.ConvertTimeout > 0)
                {
                    await Task.Delay(1000);
                    Console.WriteLine($"PID {p.Process.Id} -> stream time remain {p.ConvertTimeout}s");
                    p.ConvertTimeout--;
                    int stopRequest = 0;
                    
                    if (p.CancleToken != null && p.CancleToken.IsCancellationRequested)
                    {
                        stopRequest = 1;
                    }

                    if (stopRequest > 0)
                    {
                        Console.WriteLine($"{stopRequest} Tasks exited by CancellationRequested");
                        break;
                    }
                    else if (p.Process.HasExited)
                    {
                        Console.WriteLine($"PID {p.Process.Id} -> exited by error code {p.Process.ExitCode}");
                        try 
                        {
                            string line = p.Process.StandardOutput.ReadToEnd();
                            if (!String.IsNullOrEmpty(line))
                            {
                                Console.WriteLine($"Stdout ->{line}");
                            }

                            line = p.Process.StandardError.ReadToEnd();
                            if (!String.IsNullOrEmpty(line))
                            {
                                Console.WriteLine($"Stderr -> {line}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                        }

                        if (AUTO_RESTART_PROCESS_WHEN_EXIT && p.ConvertTimeout > 0 && p.RetiesTime > 0)
                        {
                            ListRetryingUrls.Add(obj.InputUrl);
                            /*await*/ var t = RestartProcess(obj.InputUrl, p.RecordToFile, p.ConvertTimeout, p.RetiesTime - 1);
                        }
                        else
                        {
                            try
                            {
                                ListRetryingUrls.Remove(obj.InputUrl);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.Message);
                            }
                        }
                        break;
                    }
                }

                if (p.ConvertTimeout == 0)
                {
                    Console.WriteLine($"PID = {p.Process.Id} timeout");
                }
                
                if (p.CancleToken != null)
                {
                    Console.WriteLine($"PID {p.Process.Id} -> Dispose cancle token");
                    p.CancleToken.Dispose();
                }

                await CloseProcessAsync(p.Process, MAX_TIMEOUT_WAIT_PROCESS_EXIT);
                
                if (!String.IsNullOrEmpty(p.TargetTrashPathWillBeClean))
                {
                    // Remove .ts file
                    Console.WriteLine($"Clean folder -> {p.TargetTrashPathWillBeClean}");
                    await DeleteAudioTrashFilesAsync(p.TargetTrashPathWillBeClean);
                    Console.WriteLine("Done");
                }
                RemoveProcessFromList(obj, p);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Close subprocess failed {0}", ex.ToString());
            }
        }

        public static void StartAudioConverterThreadAsync(AudioUrlConverter obj)
        {
            Console.WriteLine($"Number of process count -> {obj._processesInfo.Count}");
            for (int i = 0; i < obj._processesInfo.Count; i++)
            {
                var tmp = RunProcessUntilTimeout(obj, obj._processesInfo[i]);       // dont care about await
            }
        }

        public static JArray GetAllRecordUrl()
        {
            // var json = JObject.Parse("{}");

            // // List<string> records = new List<string>();
            var jArray = new JArray();

            lock (_ensureThreadSafe)
            {
                foreach (var item in ListAudioObjInfo)
                {
                    jArray.Add(new JObject (
                        new JProperty("InputUrl", item.InputUrl),
                        new JProperty("OutputStream", item.OutputStreamUrl),
                        new JProperty("RecordFile", item.OutputRecordFileUrl)
                    ));
                }
            }
            // json.Add("url", jArray);
            return jArray;
        }

        //Sample ffmpeg -y -i http://stream.bytech.vn:3000/860262051441926 -acodec libmp3lame  -flush_packets 1 1783043925/info.mp3
        private static string BuildFffmpegStreamCmd(string inputUrl, string destinationFolder, FfmpegFileOutput type)
        {
            string ffmpegCmd = String.Empty;
            switch (type)
            {
                case FfmpegFileOutput.M3u8:
                    ffmpegCmd = string.Format("-y -hide_banner -i {0} -loglevel info -hls_list_size {1} -hls_flags delete_segments -acodec mp3 {2}{3}",
                                            inputUrl, MAX_NUMBER_OF_TS_FILE_KEEP_IN_DISK, destinationFolder, M3U8_FILE_NAME);
                    break;
                case FfmpegFileOutput.Mp3:
                default:
                    ffmpegCmd = string.Format("-y -hide_banner -i {0} -loglevel error -acodec libmp3lame -flush_packets 1 {1}{2}",
                                                       inputUrl, destinationFolder, MP3_FILE_NAME);
                    break;
            }
            return ffmpegCmd;
        }

        private static void OnFfmpegExit(Object sender, System.EventArgs e)
        {
            Console.WriteLine("Ffmpeg exit code = {0}", ((Process)sender).ExitCode);
        }

        private static void OnFfmpegStdoutMsg(Object sender, string msg)
        {
            if (!String.IsNullOrEmpty(msg))
            {
                if (msg.Contains("Server returned 404 Not Found"))
                {
                    Console.WriteLine("FF STDOUT {0}", msg);
                }
            }
            // {
            //     Console.WriteLine("FF STDOUT : {0} -> {1}", sender.ToString(), msg);
            // }
            
            // Console.WriteLine("FF STDOUT {0} -> {1}", sender.ToString(), msg);

        }

        private static void OnFfmpegStderrMsg(Object sender, string msg)
        {
            if (!String.IsNullOrEmpty(msg))
            {
                Console.WriteLine("FF STDERR {0} -> {1}", sender.ToString(), msg);
                if (msg.Contains("Server returned 404 Not Found"))
                {
                    Console.WriteLine("FFMEG -> Link 404");
                }
            }
        }

        private static ConvertUrlInfo InsertRecordNoThreadSafe(string inputUrl, bool needRecordToFile, int recordTimeInSec, int retries)
        {
            int index = -1;
            ConvertUrlInfo result = new ConvertUrlInfo();
            AudioUrlConverter tmp = new AudioUrlConverter();

            //Check if input url already existed in last
            for (var i = 0; i < ListAudioObjInfo.Count; i++)
            {
                if (inputUrl.Equals(ListAudioObjInfo[i].InputUrl))
                {
                    index = i;
                    break;
                }
            }

            //if url not exsited -> create new one
            if (index == -1)
            {
                tmp.InputUrl = inputUrl;
                tmp.RecordTimeoutInSec = recordTimeInSec;
                tmp.LocalFolderRecorded = GetFullPathToSharedNginxFolder(inputUrl);
                tmp.OutputStreamUrl = AudioUrlConverter.GenUrl(GetShortPathToSharedNginxFolder(inputUrl) + M3U8_FILE_NAME);

                ListAudioObjInfo.Add(tmp);
                bool[] processCreated = new bool[2];
                processCreated[0] = false;
                processCreated[1] = false;

                // Create stream folder
                Console.WriteLine($"Local ffmpeg output folder -> {tmp.LocalFolderRecorded}");
                System.IO.Directory.CreateDirectory(tmp.LocalFolderRecorded);

                // Create ffmpeg process : m3u8
                string ffmpegCmd = BuildFffmpegStreamCmd(inputUrl, tmp.LocalFolderRecorded, FfmpegFileOutput.M3u8);
                Console.WriteLine("FFMPEG -> M3U8 cmd = ffmpeg {0}", ffmpegCmd);

                try
                {
                    SingleProcessInfo p = new SingleProcessInfo();
                    var processInfo = new ProcessStartInfo();
                    
                    processInfo.FileName = _ffmpegPath;
                    processInfo.Arguments = ffmpegCmd;
                    processInfo.UseShellExecute = false;
                    processInfo.CreateNoWindow = false;
                    processInfo.RedirectStandardOutput = true;
                    processInfo.RedirectStandardError = true;
                    
                    p.Process = Process.Start(processInfo);
                    if (p.Process != null)
                    {
                        p.ConvertTimeout = CONVERT_M3U8_FOREVER;
                        p.CancleToken = new CancellationTokenSource();
                        p.TargetTrashPathWillBeClean = tmp.LocalFolderRecorded;
                        p.RecordToFile = false;
                        p.RetiesTime = retries;
                        tmp._processesInfo.Add(p);
                        
                        processCreated[0] = true;
                    }
                    else
                    {
                        Console.WriteLine("Create new process failed");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FFMPEG process failed {ex.Message}");
                }

                if (needRecordToFile)
                {
                    ffmpegCmd = BuildFffmpegStreamCmd(inputUrl, tmp.LocalFolderRecorded, FfmpegFileOutput.Mp3);

                    SingleProcessInfo p = new SingleProcessInfo();
                    ProcessStartInfo processInfo = new ProcessStartInfo();
                    
                    processInfo.FileName = _ffmpegPath;
                    processInfo.Arguments = ffmpegCmd;
                    processInfo.UseShellExecute = false;
                    processInfo.CreateNoWindow = false;
                    processInfo.RedirectStandardOutput = true;
                    processInfo.RedirectStandardError = true;
                    
                    p.Process = Process.Start(processInfo);
                    if (p.Process != null)
                    {
                        p.ConvertTimeout = recordTimeInSec;
                        p.CancleToken = new CancellationTokenSource();
                        p.TargetTrashPathWillBeClean = String.Empty;       // We wont delete mp3 file, only .ts files will be deleted
                        p.RecordToFile = true;
                        p.RetiesTime = retries;
                        tmp._processesInfo.Add(p);
                        
                        processCreated[1] = true;
                    }
                    else
                    {
                        Console.WriteLine("Create new process failed");
                    }

                    tmp.OutputRecordFileUrl = tmp.OutputStreamUrl.Replace(".m3u8", ".mp3");
                }
                else
                {
                    tmp.OutputRecordFileUrl = "";
                }

                                        
                // If process convert to m3u8 OK
                // and 
                // ((process convert to mp3 OK) OR (dont need record to file)
                if (processCreated[0] == true 
                    && (processCreated[1] == true || (processCreated[1] == false && needRecordToFile == false)))
                {
                    result.Result = "Success";
                    Console.WriteLine("Start audio convert process");
                    AudioUrlConverter.StartAudioConverterThreadAsync(tmp);
                }
                else    // delete record
                {
                    // TODO close record
                    Console.WriteLine("Close all subprocess");
                    for (var i = 0; i < tmp._processesInfo.Count; i++)
                    {
                        try
                        {
                            var dontCare = CloseProcessAsync(tmp._processesInfo[i].Process, 0);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.ToString());
                        }
                    }
                    result.Result = "Create process failed";
                    tmp.OutputRecordFileUrl = "";
                }
            }
            else /* Url existed, update new data */
            {
                result.Result = "Url already existed";
                Console.WriteLine($"Url {ListAudioObjInfo[index].InputUrl} already existed, converted url = {ListAudioObjInfo[index].OutputStreamUrl}");
                tmp = ListAudioObjInfo[index];
            }

            result.RecordTimeoutInSec = tmp.RecordTimeoutInSec;
            result.OutputStreamUrl = tmp.OutputStreamUrl;
            result.OutputRecordFileUrl = tmp.OutputStreamUrl;
            return result;
        }

        public static ConvertUrlInfo InsertRecord(string inputUrl, bool needRecordToFile, int recordTimeInSec)
        {
            lock (_ensureThreadSafe)
            {
                return InsertRecordNoThreadSafe(inputUrl, needRecordToFile, recordTimeInSec, DEFAULT_RETRIES_WHEN_PROCESS_FAIL);
            }
        }

        public static DeleteRecordResult TerminateRecord(string inputUrl)
        {
            DeleteRecordResult ret = DeleteRecordResult.UrlNotExist;
            if (!String.IsNullOrEmpty(inputUrl))
            {
                //Check if input url already existed in last
                lock(_ensureThreadSafe)
                {
                    // remove record in list
                    for (var i = 0; i < ListAudioObjInfo.Count; i++)
                    {
                        if (inputUrl.Equals(ListAudioObjInfo[i].InputUrl))
                        {
                            for (int processCount = 0; processCount < ListAudioObjInfo[i]._processesInfo.Count; processCount++)
                            {
                                try
                                {
                                    ListAudioObjInfo[i]._processesInfo[processCount].CancleToken.Cancel();
                                    ret = DeleteRecordResult.Ok;
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine($"Send terminate signal failed {e.ToString()}");
                                    ret = DeleteRecordResult.InternalError;
                                }
                            }
                            // if (keepStreamMp3Recorded)
                            // {
                            //     Console.WriteLine("Delete only record mp3 file, keep m3u8 process running", keepStreamMp3Recorded);
                            //     var tmp = DoTerminateOnlyMp3RecordProcesses(ListAudioObjInfo[i]);
                            // }
                            // else        // Delete all
                            // {
                            //     var tmp = RunFfmpegProcessesUntilTimeout(ListAudioObjInfo[i], 0, false);
                            // }
                            break;
                        }
                    }

                    // // Remove record in retry list
                    for (var i = 0; i < ListRetryingUrls.Count; i++)
                    {
                        if (inputUrl.Equals(ListRetryingUrls[i]))
                        {
                            Console.WriteLine($"Remove item {ListRetryingUrls[i]} from retrying list");
                            ListRetryingUrls.Remove(inputUrl);
                        }
                    }
                }
            }
            else
            {
                ret = DeleteRecordResult.InvalidParam;
            }
            return ret;
        }

        private static async Task RestartProcess(string url, bool needRecordToFile, int timeoutInSec, int retries)
        {
            if (!String.IsNullOrEmpty(url) && timeoutInSec > 0 && retries > 0)
            {
                Console.WriteLine($"Sleep {SLEEP_TIME_MS_BEFORE_RESTART_PROCESS}ms then restart process, remain {retries-1} times");
                await Task.Delay(SLEEP_TIME_MS_BEFORE_RESTART_PROCESS);        // Restart every 60s
                int doRestartStep = 0;
                lock(_ensureThreadSafe)
                {
                    for (var i = 0; i < ListAudioObjInfo.Count; i++)
                    {
                        doRestartStep++;
                        if (url.Equals(ListAudioObjInfo[i].InputUrl))
                        {
                            Console.WriteLine($"Url {url} already in list");
                            doRestartStep = 0;
                            break;
                        }
                    }

                    if (doRestartStep > 0)
                    {
                        for (int i = 0; i < ListRetryingUrls.Count; i++)
                        {
                            doRestartStep = 0;
                            if (url.Equals(ListRetryingUrls[i]))
                            {
                                Console.WriteLine("Valid restart URL");
                                doRestartStep++;
                                ListRetryingUrls.RemoveAt(i);
                                break;
                            }
                        }

                        if (doRestartStep > 0)
                        {
                            Console.WriteLine($"Restart {url}, timeout = {timeoutInSec}");
                            InsertRecordNoThreadSafe(url, needRecordToFile, timeoutInSec, retries);
                        }
                    }

                    if (doRestartStep == 0)
                    {
                        Console.WriteLine("Complete finish seq");
                    }
                }
            }

        }
    }

}
