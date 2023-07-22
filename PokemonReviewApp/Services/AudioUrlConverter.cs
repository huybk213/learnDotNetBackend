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
            Ok
        }

        private enum FfmpegFileOutput
        {
            M3u8,
            Mp3,
        }
        public string LocalFolderRecorded = String.Empty;
        public long RecordTimeoutInSec;
        public string OutputStreamUrl = String.Empty;
        public string OutputRecordUrl = String.Empty;
        public string InputUrl = String.Empty;

        private static object _ensureThreadSafe = new Object();
        private static string _nginxPath = String.Empty;
        private static string _ffmpegPath = String.Empty;
        private static string _prefixUrl = String.Empty;
        public static List<AudioUrlConverter> ListAudioConverter = new List<AudioUrlConverter>();
        private List<Process> ConvertProcesses = new List<Process>();
        private const int MAX_TIMEOUT_WAIT_PROCESS_EXIT = 500;   /*ms*/
        private const int MAX_NUMBER_OF_TS_FILE_KEEP_IN_DISK = 12;
        private const string M3U8_FILE_NAME = "/audio.m3u8";
        private const string MP3_FILE_NAME = "/audio.mp3";
        private const bool _autoRestartStream = false;
        private CancellationTokenSource _cancleTaskSource;
        private readonly ILogger _logger;

        public AudioUrlConverter(ILogger<AudioUrlConverter> logger){
            _logger = logger;
        }

        public static async Task WaitTaskCompleteAsync(Process process, long delayInSec, CancellationTokenSource cancleTaskSource)
        {
            for (var i = 0; i < delayInSec; i++)
            {
                await Task.Delay(1000);
                if (cancleTaskSource.IsCancellationRequested)
                {
                    Log.Warning("Task exited by CancellationRequested");
                    break;
                }
                else if (process.HasExited)
                {
                    Log.Warning("Task exited by error code {0}", process.ExitCode);
                    break;
                }
            }
        }

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
            Log.Warning("Nginx = {0}, url = {1}", _nginxPath, _prefixUrl);
        }

        public static void SetFFmpegBinaryPath(string path)
        {
            _ffmpegPath = path;
            Log.Warning("FFmpegPath = {0}", _ffmpegPath);
        }

        public static string GenUrl(string localFileName)
        {
            return _prefixUrl + localFileName;
        }

        private static async Task CloseProcess(Process p, int maxWaitTimeInMs)
        {
            if (p == null)
            {
                return;
            }

            try
            {
                //Close m3u8 audio convert process
                p.Kill(); /*Never null*/
                int maxWaitTime = maxWaitTimeInMs / 50;
                while (p.HasExited == false && maxWaitTime > 0)
                {
                    await Task.Delay(50);
                    maxWaitTime--;
                }

                if (p.HasExited == false)
                {
                    Log.Warning("Killl ffmpeg process failed");
                }
                else
                {
                    Log.Warning("Kill ffmpeg process OK");
                }
            }
            catch (Exception ex) { Log.Warning(ex.ToString()); }
        }

        //Delete audio .*ts trash file, keep .mp3 file
        public static async Task DeleteFileAsync(string parentFolder)
        {
            var audioDir = new DirectoryInfo(parentFolder);

            // foreach (var file in audioDir.EnumerateFiles("*.ts"))
            // {
            //     try
            //     {
            //         await Task.Factory.StartNew(() => file.Delete());
            //     }
            //     catch (Exception e)
            //     {
            //         Log.Warning(e.ToString());
            //     }
            // }
            var masks = new[] { "*.ts", "*.m3u8" };
            var files = masks.SelectMany(audioDir.EnumerateFiles);
            foreach (var file in files)
            {
                try
                {
                    await Task.Factory.StartNew(() => file.Delete());
                }
                catch (Exception e)
                {
                    Log.Warning(e.ToString());
                }
            }
        }

       private static async Task DoTerminateOnlyFileRecordProcesses(AudioUrlConverter obj)
        {
            try
            {
                Log.Warning("DoTerminateOnlyFileRecordProcesses");
                if (obj.ConvertProcesses != null && obj.ConvertProcesses.Count == 2)
                {
                    Log.Warning("Close Mp3 process");
                    await CloseProcess(obj.ConvertProcesses[1], MAX_TIMEOUT_WAIT_PROCESS_EXIT);
                }
                Log.Warning("Closed");

                lock (_ensureThreadSafe)
                {
                    if (obj.ConvertProcesses != null && obj.ConvertProcesses.Count == 2)
                    {
                        obj.ConvertProcesses.RemoveAt(1);       //  0 = process m3u8, 1 = process mp3 record
                    // ListAudioConverter.Remove(obj);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Close subprocess failed {0}", ex.ToString());
            }
        }
        
        private static async Task WaitAllFfmpegProcessesUntilTimeout(AudioUrlConverter obj, long recordTimeout, bool keepM3U8Process)
        {
            try
            {
                if (recordTimeout > 0) 
                {
                    Log.Warning("Convert process in {0}s", recordTimeout);
                    await WaitTaskCompleteAsync(obj.ConvertProcesses[0], recordTimeout, obj._cancleTaskSource);
                }

                if (keepM3U8Process)
                {
                    DoTerminateOnlyFileRecordProcesses(obj);
                }
                else    // Close all
                {
                    for (int i = 0; i < obj.ConvertProcesses.Count; i++)
                    {
                        Log.Warning("Close process {0}", i);
                        await CloseProcess(obj.ConvertProcesses[i], MAX_TIMEOUT_WAIT_PROCESS_EXIT);
                    }
                    Log.Warning("Clean folder {0}", obj.LocalFolderRecorded);
                    await DeleteFileAsync(obj.LocalFolderRecorded);
                    Log.Warning("Done");
                    
                    lock (_ensureThreadSafe)
                    {
                        ListAudioConverter.Remove(obj);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Close subprocess failed {0}", ex.ToString());
            }
        }

        public static void StartAudioConverterThreadAsync(AudioUrlConverter obj)
        {
            obj._cancleTaskSource = new CancellationTokenSource();
            var dontCare = WaitAllFfmpegProcessesUntilTimeout(obj, obj.RecordTimeoutInSec, true);
        }

        public static JObject GetAllRecordUrl()
        {
            var json = JObject.Parse("{}");

            // List<string> records = new List<string>();
            lock (_ensureThreadSafe)
            {
                foreach (var item in ListAudioConverter)
                {
                    json.Add(item.InputUrl, item.OutputStreamUrl);
                }
            }
            return json;
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
            Log.Warning("Ffmpeg exit code = {0}", ((Process)sender).ExitCode);
        }

        private static void OnFfmpegStdoutMsg(Object sender, string msg)
        {
            if (!String.IsNullOrEmpty(msg))
            {
                if (msg.Contains("Server returned 404 Not Found"))
                {
                    Log.Warning("FF STDOUT {0}", msg);
                }
            }
            // {
            //     Log.Warning("FF STDOUT : {0} -> {1}", sender.ToString(), msg);
            // }
            
            // Log.Warning("FF STDOUT {0} -> {1}", sender.ToString(), msg);

        }

        private static void OnFfmpegStderrMsg(Object sender, string msg)
        {
            if (!String.IsNullOrEmpty(msg))
            {
                Log.Warning("FF STDERR {0} -> {1}", sender.ToString(), msg);
                if (msg.Contains("Server returned 404 Not Found"))
                {
                    Log.Warning("FFMEG -> Link 404");
                }
            }
        }

        public static AudioUrlConverter InsertRecord(string inputUrl, bool needRecordToFile, long recordTimeInSec)
        {
            lock (_ensureThreadSafe)
            {
                int index = -1;
                AudioUrlConverter tmp = new AudioUrlConverter();

                //Check if input url already existed in last
                for (var i = 0; i < ListAudioConverter.Count; i++)
                {
                    if (inputUrl.Equals(ListAudioConverter[i].InputUrl))
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

                    ListAudioConverter.Add(tmp);
                    bool[] processCreated = new bool[2];
                    processCreated[0] = false;
                    processCreated[1] = false;

                    // Create stream folder
                    Log.Warning("Create local ffmpeg output folder {0}", tmp.LocalFolderRecorded);
                    System.IO.Directory.CreateDirectory(tmp.LocalFolderRecorded);

                    // Create ffmpeg process : m3u8
                    string ffmpegCmd = BuildFffmpegStreamCmd(inputUrl, tmp.LocalFolderRecorded, FfmpegFileOutput.M3u8);
                    Log.Warning("FFMPEG -> M3U8 cmd = ffmpeg {0}", ffmpegCmd);

                    var m3u8ProcessInfo = new ProcessStartInfo(_ffmpegPath, ffmpegCmd)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    try
                    {
                        var newProcess = System.Diagnostics.Process.Start(m3u8ProcessInfo);
                        if (newProcess != null)
                        {
                            tmp.ConvertProcesses.Add(newProcess);
                            processCreated[0] = true;
                        }
                        else
                        {
                            Log.Warning("Create new process failed");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("FFMPEG process failed {0}", ex.Message);
                    }

                    if (needRecordToFile)
                    {
                        ffmpegCmd = BuildFffmpegStreamCmd(inputUrl, tmp.LocalFolderRecorded, FfmpegFileOutput.Mp3);
                        // Log.Warning("FFMPEG -> MP3 cmd = ffmpeg {0}", ffmpegCmd);
                        var mp3ProcessInfo = new ProcessStartInfo(_ffmpegPath, ffmpegCmd)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = false,
                            RedirectStandardOutput = false,
                            RedirectStandardError = false
                        };

                        try
                        {
                            var newProcess = System.Diagnostics.Process.Start(mp3ProcessInfo);
                            if (newProcess != null)
                            {
                                tmp.ConvertProcesses.Add(newProcess);
                                processCreated[1] = true;
                            }
                            else
                            {
                                Log.Warning("Create new process failed");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning("FFMPEG process failed {0}", ex.Message);
                        }

                        tmp.OutputRecordUrl = tmp.OutputStreamUrl.Replace(".m3u8", ".mp3");
                    }
                    else
                    {
                        tmp.OutputRecordUrl = "";
                    }

                                            
                    // If process convert to m3u8 OK
                    // and 
                    // ((process convert to mp3 OK) OR (dont need record to file)
                    if (processCreated[0] == true 
                        && (processCreated[1] == true || (processCreated[1] == false && needRecordToFile == false)))
                    {
                        Log.Warning("Start audio convert process");
                        AudioUrlConverter.StartAudioConverterThreadAsync(tmp);
                    }
                    else    // delete record
                    {
                        Log.Warning("Close all subprocess");
                        for (var i = 0; i < tmp.ConvertProcesses.Count; i++)
                        {
                            try
                            {
                                var dontCare = CloseProcess(tmp.ConvertProcesses[i], 0);
                            }
                            catch (Exception e)
                            {
                                Log.Warning(e.ToString());
                            }
                        }
                        tmp.OutputRecordUrl = "";
                    }
                }
                else /* Url existed, update new data */
                {
                    Log.Warning("Process already existed");
                    tmp = ListAudioConverter[index];
                }
                return tmp;
            }
        }

        public static DeleteRecordResult TerminateRecord(string inputUrl, bool keepStream)
        {
            DeleteRecordResult ret = DeleteRecordResult.UrlNotExist;
            if (inputUrl != null && inputUrl != String.Empty)
            {
                //Check if input url already existed in last
                for (var i = 0; i < ListAudioConverter.Count; i++)
                {
                    if (inputUrl.Equals(ListAudioConverter[i].InputUrl))
                    {
                        if (keepStream)
                        {
                            Log.Warning("Delete only record mp3 file, keep m3u8 process running", keepStream);
                            var tmp = DoTerminateOnlyFileRecordProcesses(ListAudioConverter[i]);
                        }
                        else
                        {
                            var tmp = WaitAllFfmpegProcessesUntilTimeout(ListAudioConverter[i], 0, false);
                        }
                        ret = DeleteRecordResult.Ok;
                        break;
                    }
                }
            }
            else
            {
                ret = DeleteRecordResult.InvalidParam;
            }
            return ret;
        }
    }

}
