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
        public bool NeedRecordToFile = false;

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
        private const bool AUTO_RESTART_PROCESS_WHEN_EXIT = true;
        private const int DEFAULT_RETRIES_WHEN_PROCESS_FAIL = 10000;     //
        private const int SLEEP_TIME_MS_BEFORE_RESTART_PROCESS = 120000;

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
            Log.Information("Nginx = {0}, url = {1}", _nginxPath, _prefixUrl);
        }

        public static void SetFFmpegBinaryPath(string path)
        {
            _ffmpegPath = path;
            Log.Information("FFmpegPath = {0}", _ffmpegPath);
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
                Log.Information($"Process {p.Id} already exited");
                return;
            }

            try
            {
                Log.Information($"Killing PID {p.Id}");
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
                    Log.Information($"Kill PID {p.Id} -> FAILED");
                }
                else
                {
                    Log.Information($"Kill PID {p.Id} -> OK");
                }
            }
            catch (Exception ex) { Log.Information(ex.ToString()); }
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
                    // Log.Information($"Delete file {file.ToString()}");
                    await Task.Factory.StartNew(() => file.Delete());
                }
                catch (Exception e)
                {
                    Log.Warning(e.ToString());
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
                                Log.Information($"Removed sub process {j}, PID = {ListAudioObjInfo[i]._processesInfo[j].Process.Id}");
                                ListAudioObjInfo[i]._processesInfo.RemoveAt(j--);
                            }
                        }
                        if (ListAudioObjInfo[i]._processesInfo.Count == 0)
                        {
                            Log.Information("No more sub-process, remove whole object");
                            ListAudioObjInfo.RemoveAt(i--);
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
                    if (p.ConvertTimeout != CONVERT_M3U8_FOREVER)
                    {
                        if ((p.ConvertTimeout % 30 == 0))
                        {
                            Log.Information($"PID {p.Process.Id} -> stream time remain {p.ConvertTimeout}s");
                        }
                        p.ConvertTimeout--;
                    }

                    int stopRequest = 0;
                    
                    if (p.CancleToken != null && p.CancleToken.IsCancellationRequested)
                    {
                        stopRequest = 1;
                    }

                    if (stopRequest > 0)
                    {
                        Log.Information($"{stopRequest} Tasks exited by CancellationRequested");
                        break;
                    }
                    else if (p.Process.HasExited)
                    {
                        Log.Information($"PID {p.Process.Id} -> exited by error code {p.Process.ExitCode}");
                        try 
                        {
                            string line = p.Process.StandardOutput.ReadToEnd();
                            if (!String.IsNullOrEmpty(line))
                            {
                                Log.Information($"Stdout ->{line}");
                            }

                            line = p.Process.StandardError.ReadToEnd();
                            if (!String.IsNullOrEmpty(line))
                            {
                                Log.Information($"Stderr -> {line}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Information(ex.ToString());
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
                                Log.Information(e.Message);
                            }
                        }
                        break;
                    }
                }

                if (p.ConvertTimeout == 0)
                {
                    Log.Information($"PID = {p.Process.Id} timeout");
                }
                
                if (p.CancleToken != null)
                {
                    Log.Verbose($"PID {p.Process.Id} -> Dispose cancle token");
                    p.CancleToken.Dispose();
                }

                await CloseProcessAsync(p.Process, MAX_TIMEOUT_WAIT_PROCESS_EXIT);
                
                if (!String.IsNullOrEmpty(p.TargetTrashPathWillBeClean))
                {
                    // Remove .ts file
                    Log.Information($"Clean folder -> {p.TargetTrashPathWillBeClean}");
                    await DeleteAudioTrashFilesAsync(p.TargetTrashPathWillBeClean);
                    Log.Information("Done");
                }
                RemoveProcessFromList(obj, p);
            }
            catch (Exception ex)
            {
                Log.Information("Close subprocess failed {0}", ex.ToString());
            }
        }

        public static void StartAudioConverterThreadAsync(AudioUrlConverter obj)
        {
            Log.Information($"Number of process count -> {obj._processesInfo.Count}");
            for (int i = 0; i < obj._processesInfo.Count; i++)
            {
                var tmp = RunProcessUntilTimeout(obj, obj._processesInfo[i]);       // dont care about await
            }
        }

        public static JArray GetAllRecordUrl()
        {
            // var json = JObject.Parse("{}");

            // List<string> records = new List<string>();
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
                    ffmpegCmd = string.Format("-y -hide_banner -i {0} -loglevel error -hls_list_size {1} -hls_flags delete_segments -acodec mp3 {2}{3}",
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
            Log.Information("Ffmpeg exit code = {0}", ((Process)sender).ExitCode);
        }

        private static void OnFfmpegStdoutMsg(Object sender, string msg)
        {
            if (!String.IsNullOrEmpty(msg))
            {
                if (msg.Contains("Server returned 404 Not Found"))
                {
                    Log.Information("FF STDOUT {0}", msg);
                }
            }
            // {
            //     Log.Information("FF STDOUT : {0} -> {1}", sender.ToString(), msg);
            // }
            
            // Log.Information("FF STDOUT {0} -> {1}", sender.ToString(), msg);

        }

        private static void OnFfmpegStderrMsg(Object sender, string msg)
        {
            if (!String.IsNullOrEmpty(msg))
            {
                Log.Information("FF STDERR {0} -> {1}", sender.ToString(), msg);
                if (msg.Contains("Server returned 404 Not Found"))
                {
                    Log.Information("FFMEG -> Link 404");
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
                tmp.NeedRecordToFile = needRecordToFile;
                ListAudioObjInfo.Add(tmp);
                bool[] processCreated = new bool[2];
                processCreated[0] = false;
                processCreated[1] = false;

                // Create stream folder
                Log.Information($"Local ffmpeg output folder -> {tmp.LocalFolderRecorded}");
                System.IO.Directory.CreateDirectory(tmp.LocalFolderRecorded);

                // Create ffmpeg process : m3u8
                string ffmpegCmd = BuildFffmpegStreamCmd(inputUrl, tmp.LocalFolderRecorded, FfmpegFileOutput.M3u8);
                Log.Information("FFMPEG -> M3U8 cmd = ffmpeg {0}", ffmpegCmd);

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
                    
                    var tmpProcess = Process.Start(processInfo);
                    if (tmpProcess != null)
                    {
                        p.Process = tmpProcess;

                        p.ConvertTimeout = CONVERT_M3U8_FOREVER;
                        p.CancleToken = new CancellationTokenSource();
                        p.TargetTrashPathWillBeClean = tmp.LocalFolderRecorded;
                        p.RecordToFile = false;
                        p.RetiesTime = retries;

                        tmp._processesInfo.Add(p);
                        processCreated[0] = true;
                        Log.Information($"PID = {p.Process.Id}");
                    }
                    else
                    {
                        Log.Warning("Create new process failed");
                    }
                }
                catch (Exception ex)
                {
                    Log.Information($"FFMPEG process failed {ex.Message}");
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

                    var tmpProcess = Process.Start(processInfo);
                    if (tmpProcess != null)
                    {
                        p.Process = tmpProcess;

                        p.ConvertTimeout = recordTimeInSec;
                        p.CancleToken = new CancellationTokenSource();
                        p.TargetTrashPathWillBeClean = String.Empty;       // We wont delete mp3 file, only .ts files will be deleted
                        p.RecordToFile = true;
                        p.RetiesTime = retries;
                        tmp._processesInfo.Add(p);
                        
                        processCreated[1] = true;
                        Log.Information($"PID = {p.Process.Id}");
                    }
                    else
                    {
                        Log.Warning("Create new process failed");
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
                    Log.Information("Start audio convert process");
                    AudioUrlConverter.StartAudioConverterThreadAsync(tmp);
                }
                else    // delete record
                {
                    // TODO close record
                    Log.Information("Close all subprocess");
                    for (var i = 0; i < tmp._processesInfo.Count; i++)
                    {
                        try
                        {
                            var dontCare = CloseProcessAsync(tmp._processesInfo[i].Process, 0);
                        }
                        catch (Exception e)
                        {
                            Log.Information(e.ToString());
                        }
                    }
                    result.Result = "Create process failed";
                    tmp.OutputRecordFileUrl = "";
                }
            }
            else /* Url existed, update new data */
            {
                //TODO xu li case edit stream
                result.Result = "Url already existed";
                Log.Information($@"Url {ListAudioObjInfo[index].InputUrl} already existed, converted url = {ListAudioObjInfo[index].OutputStreamUrl}");

                if (ListAudioObjInfo[index].NeedRecordToFile == false && needRecordToFile)
                {
                    Log.Information($"Restart record MP3 service at {ListAudioObjInfo[index].InputUrl}");
                    //Co 2 service la service m3u8 va mp3, neu service mp3 chet -> con duy nhat m3u8
                    //Luc nay moi khoi dong lai service mp3

                    if (ListAudioObjInfo[index]._processesInfo.Count == 1)
                    {
                        Log.Information($"Create new ffmpeg service {ListAudioObjInfo[index].InputUrl}");
                        var ffmpegCmd = BuildFffmpegStreamCmd(inputUrl, ListAudioObjInfo[index].LocalFolderRecorded, FfmpegFileOutput.Mp3);

                        SingleProcessInfo p = new SingleProcessInfo();
                        ProcessStartInfo processInfo = new ProcessStartInfo();

                        processInfo.FileName = _ffmpegPath;
                        processInfo.Arguments = ffmpegCmd;
                        processInfo.UseShellExecute = false;
                        processInfo.CreateNoWindow = false;
                        processInfo.RedirectStandardOutput = true;
                        processInfo.RedirectStandardError = true;

                        var tmpProcess = Process.Start(processInfo);
                        if (tmpProcess != null)
                        {
                            p.Process = tmpProcess;
                            p.ConvertTimeout = recordTimeInSec;
                            p.CancleToken = new CancellationTokenSource();
                            p.TargetTrashPathWillBeClean = String.Empty;       // We wont delete mp3 file, only .ts files will be deleted
                            p.RecordToFile = true;
                            p.RetiesTime = retries;
                            ListAudioObjInfo[index].OutputRecordFileUrl = ListAudioObjInfo[index].OutputStreamUrl.Replace(".m3u8", ".mp3");
                            ListAudioObjInfo[index]._processesInfo.Add(p);
                            
                            Log.Information($"PID = {p.Process.Id}");

                            var dontCare = RunProcessUntilTimeout(ListAudioObjInfo[index], ListAudioObjInfo[index]._processesInfo[1]);       // dont care about await
                        }
                        else
                        {
                            Log.Warning("Create new process failed");
                        }
                    }

                }

                tmp = ListAudioObjInfo[index];
            }

            result.RecordTimeoutInSec = tmp.RecordTimeoutInSec;
            result.OutputStreamUrl = tmp.OutputStreamUrl;
            result.OutputRecordFileUrl = tmp.OutputRecordFileUrl;
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
                                    Log.Information($"Send terminate signal failed {e.ToString()}");
                                    ret = DeleteRecordResult.InternalError;
                                }
                            }
                            // if (keepStreamMp3Recorded)
                            // {
                            //     Log.Information("Delete only record mp3 file, keep m3u8 process running", keepStreamMp3Recorded);
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
                            Log.Information($"Remove item {ListRetryingUrls[i]} from retrying list");
                            ListRetryingUrls.Remove(inputUrl);
                            i--;
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
                Log.Information($"Sleep {SLEEP_TIME_MS_BEFORE_RESTART_PROCESS}ms then restart process, remain {retries} times");
                await Task.Delay(SLEEP_TIME_MS_BEFORE_RESTART_PROCESS);        // Restart every 60s
                
                int doRestartStep = 0;
                lock(_ensureThreadSafe)
                {
                    // Cho nay co the co bug, vi du cung 1 link nhung nhieu output khac nhau
                    // Neu ma check url da ton tai thi chi co ouput dau tien dc convert lai, cac process sau
                    // 
#if true
                    doRestartStep = ListAudioObjInfo.Any(x => x.InputUrl.Equals(url)) ? 1 : 0;
#else
                    for (var i = 0; i < ListAudioObjInfo.Count; i++)
                    {
                        doRestartStep++;
                        if (url.Equals(ListAudioObjInfo[i].InputUrl))
                        {
                            Log.Information($"Url {url} already in list");
                            doRestartStep = 0;
                            break;
                        }
                    }
#endif
                    if (doRestartStep > 0 || ListAudioObjInfo.Count > 0)
                    {
                        for (int i = 0; i < ListRetryingUrls.Count; i++)
                        {
                            doRestartStep = 0;
                            if (url.Equals(ListRetryingUrls[i]))
                            {
                                Log.Information("Valid URL");
                                doRestartStep++;
                                ListRetryingUrls.RemoveAt(i--);
                                break;
                            }
                        }

                        if (doRestartStep > 0)
                        {
                            Log.Information($"Restart {url}, timeout = {timeoutInSec}");
                            InsertRecordNoThreadSafe(url, needRecordToFile, timeoutInSec, retries);
                        }
                    }

                    if (doRestartStep == 0)
                    {
                        Log.Information("Complete finish seq");
                    }
                }
            }

        }
    }

}
