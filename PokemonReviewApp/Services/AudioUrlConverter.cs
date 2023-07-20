using AudioApp.Models;
using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Timers;

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
        public static async Task SupperLongDelay(long delayInSec)
        {
            delayInSec *= 1000; /*Sec to mSec*/
            while (delayInSec > 0)
            {
                var currentDelay = delayInSec > int.MaxValue ? int.MaxValue : (int)delayInSec;
                await Task.Delay(currentDelay);
                delayInSec -= currentDelay;
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
        }

        public static void SetFFmpegBinaryPath(string path)
        {
            _ffmpegPath = path;
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
                    Console.WriteLine("Killl ffmpeg process failed");
                }
                else
                {
                    Console.WriteLine("Kill ffmpeg process OK");
                }
            }
            catch (Exception ex) { Console.WriteLine(ex.ToString()); }
        }

        //Delete audio .*ts trash file, keep .mp3 file
        public static async Task DeleteFileAsync(string parentFolder)
        {
            var audioDir = new DirectoryInfo(parentFolder);

            foreach (var file in audioDir.EnumerateFiles("*.ts"))
            {
                try
                {
                    await Task.Factory.StartNew(() => file.Delete());
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                }
            }
        }

        private static async Task DoTerminateFfmpegProcessAsyncWithTimeout(AudioUrlConverter obj, long timeout)
        {
            try
            {
                if (timeout > 0) 
                {
                    await SupperLongDelay(timeout);
                }
                for (int i = 0; i < obj.ConvertProcesses.Count; i++)
                {
                    Console.WriteLine("Wait for process {0} close", i);
                    await CloseProcess(obj.ConvertProcesses[i], MAX_TIMEOUT_WAIT_PROCESS_EXIT);
                }

                Console.WriteLine("Clean folder {0}", obj.LocalFolderRecorded);
                await DeleteFileAsync(obj.LocalFolderRecorded);
                Console.WriteLine("Closed ffmpeg");

                lock (_ensureThreadSafe)
                {
                    ListAudioConverter.Remove(obj);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Close subprocess failed {0}", ex.ToString());
            }
        }

        private static async Task DoTerminateFfmpegProcessAsync(AudioUrlConverter obj)
        {
            var tmp = DoTerminateFfmpegProcessAsyncWithTimeout(obj, obj.RecordTimeoutInSec);
        }

        public static void StartAudioConverterThreadAsync(AudioUrlConverter obj)
        {
            var dontCare = DoTerminateFfmpegProcessAsync(obj); /* to avoid warning*/
        }

        public static List<string> GetAllRecordUrl()
        {
            List<string> records = new List<string>();
            lock (_ensureThreadSafe)
            {
                for (var i = 0; i < ListAudioConverter.Count; i++)
                {
                    records.Add(ListAudioConverter[i].OutputRecordUrl);
                }
            }
            return records;
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
                    tmp.OutputStreamUrl = AudioUrlConverter.GenUrl(GetShortPathToSharedNginxFolder(inputUrl) + "/audio.m3u8");

                    ListAudioConverter.Add(tmp);

                    // Create stream folder
                    Console.WriteLine("Create local ffmpeg output folder {0}", tmp.LocalFolderRecorded);
                    System.IO.Directory.CreateDirectory(tmp.LocalFolderRecorded);

                    // Create ffmpeg process : m3u8
                    string ffmpegCmd = string.Format("-y -i {0} -acodec mp3 {1}",
                                                    inputUrl, tmp.LocalFolderRecorded + "/audio.m3u8");
                    Console.WriteLine("FFMPEG cmd {0}", ffmpegCmd);


                    var m3u8ProcessInfo = new ProcessStartInfo(_ffmpegPath, ffmpegCmd)
                    {
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    };

                    
                    try
                    {
                        var newProcess = System.Diagnostics.Process.Start(m3u8ProcessInfo);
                        if (newProcess != null)
                        {
                            tmp.ConvertProcesses.Add(newProcess);
                        }
                        else
                        {
                            Console.WriteLine("Create new process failed");
                            tmp.OutputStreamUrl = String.Empty;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("FFMPEG process failed {0}", ex.Message);
                    }

                    if (needRecordToFile)
                    {
                        //mp3
                        //Sample ffmpeg -y -i http://stream.bytech.vn:3000/860262051441926 -acodec libmp3lame  -flush_packets 1 1783043925\info.mp3
                        ffmpegCmd = string.Format("-y -i {0} -acodec libmp3lame -flush_packets 1 {1}",
                                                       inputUrl, tmp.LocalFolderRecorded + "\\info.mp3");
                        Console.WriteLine("FFMPEG cmd {0}", ffmpegCmd);
                        var mp3ProcessInfo = new ProcessStartInfo(_ffmpegPath, ffmpegCmd)
                        {
                            UseShellExecute = true,
                            CreateNoWindow = true,
                            RedirectStandardOutput = false,
                            RedirectStandardError = false
                        };

                        try
                        {
                            var newProcess = System.Diagnostics.Process.Start(mp3ProcessInfo);
                            if (newProcess != null)
                            {
                                tmp.ConvertProcesses.Add(newProcess);
                            }
                            else
                            {
                                Console.WriteLine("Create new process failed");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("FFMPEG process failed {0}", ex.Message);
                        }

                        tmp.OutputRecordUrl = tmp.OutputStreamUrl.Replace(".m3u8", ".mp3");

                        if (tmp.ConvertProcesses.Count > 0)
                        {
                            AudioUrlConverter.StartAudioConverterThreadAsync(tmp);
                        }
                    }
                    else
                    {
                        tmp.OutputRecordUrl = "";
                    }
                }
                else /* Url existed, update new data */
                {
                    tmp = ListAudioConverter[index];
                }
                return tmp;
            }
        }

        public static DeleteRecordResult TerminateRecord(string inputUrl)
        {
            DeleteRecordResult ret = DeleteRecordResult.UrlNotExist;
            if (inputUrl != null && inputUrl != String.Empty)
            {
                //Check if input url already existed in last
                for (var i = 0; i < ListAudioConverter.Count; i++)
                {
                    if (inputUrl.Equals(ListAudioConverter[i].InputUrl))
                    {
                        var tmp = DoTerminateFfmpegProcessAsyncWithTimeout(ListAudioConverter[i], 0);
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
