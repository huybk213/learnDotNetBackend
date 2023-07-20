using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PokemonReviewApp.Controllers;
using PokemonReviewApp.Models;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Timers;
using Minio;
using Minio.DataModel;
using System.Threading.Tasks;
using System.IO;
using Microsoft.AspNetCore.SignalR.Protocol;
using System.Text;
using System.IO.Hashing;

namespace PokemonReviewApp.Controllers
{
    public class AudioUrlConverter : InputAudioConverter
    {
        public string LocalFolderRecorded = String.Empty;
        public long RecordTimeoutInSec;
        public long StartRecordTime;
        public string OutputStreamUrl = String.Empty;
        public string OutputRecordUrl = String.Empty;
        static private Thread t_thrMonitorTimeout;
        static private long _timer1s = 0;
        private static object _ensureThreadSafe = new Object();
        private static List<Process> _ffmpegM3U8Processes = new List<Process>();
        private static List<Process> _ffmpegMp3Processes = new List<Process>();
        private static string _nginxPath = String.Empty;
        private static string _ffmpegPath = String.Empty;
        private static string _prefixUrl = String.Empty;
        public static List<AudioUrlConverter> ListAudioConverter = new List<AudioUrlConverter>();
        private const int MAX_TIMEOUT_WAIT_PROCESS_EXIT = 500;   /*ms*/

        //Audio timer monitor
        public static long GetTick1s()
        {
            return _timer1s;
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

        static private void TimerThread()
        {
            while (true)
            {
                lock (_ensureThreadSafe)
                {
                    _timer1s++;
                }
                Thread.Sleep(1000);
            }
        }
        static private void DoTerminateFfmpegProcessAt(int pos)
        {
            try
            {
                //Close m3u8 audio convert process
                _ffmpegM3U8Processes[pos].Kill(); /*Never null*/
                int maxWaitTime = MAX_TIMEOUT_WAIT_PROCESS_EXIT / 50;
                while (_ffmpegM3U8Processes[pos].HasExited && maxWaitTime > 0)
                {
                    Thread.Sleep(50);
                    maxWaitTime--;
                }
                _ffmpegM3U8Processes.RemoveAt(pos);

                //Close mp3 audio convert process
                if (_ffmpegMp3Processes != null)
                {
                    maxWaitTime = MAX_TIMEOUT_WAIT_PROCESS_EXIT / 50;
                    _ffmpegMp3Processes[pos].Kill();
                    while (_ffmpegMp3Processes[pos].HasExited && maxWaitTime > 0)
                    {
                        Thread.Sleep(50);
                        maxWaitTime--;
                    }
                    _ffmpegMp3Processes.RemoveAt(pos);
                }

                Console.WriteLine("Closed ffmpeg");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Close subprocess failed {0}", ex.ToString());
            }

            //Delete audio .*ts trash file, keep .mp3 file
            var audioDir = new DirectoryInfo(ListAudioConverter[pos].LocalFolderRecorded);

            foreach (var file in audioDir.EnumerateFiles("*.ts"))
            {
                try
                {
                    file.Delete();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                }
            }
            ListAudioConverter.RemoveAt(pos);
        }
        static private void audioMonitor()
        {
            while (true)
            {
                //TODO: Add thread safe
                lock (_ensureThreadSafe)
                {
                    for (var i = 0; i < ListAudioConverter.Count; i++)
                    {
                        var now = _timer1s;
                        var streamCount = now - ListAudioConverter[i].StartRecordTime;

                        if ((streamCount >= ListAudioConverter[i].RecordTimeInSec))
                        {
                            Console.WriteLine("Record {0} over, stream time {1}:{2}", 
                                            ListAudioConverter[i].InputUrl,
                                            streamCount,
                                            ListAudioConverter[i].RecordTimeInSec);
                            DoTerminateFfmpegProcessAt(i);
                            break;
                        }
                    }
                }
                Thread.Sleep(1000);
            }
        }

        public static void StartAudioConverterThread()
        {
            if (AudioUrlConverter.t_thrMonitorTimeout == null)  /* Only create 1 time */
            {
                Console.WriteLine("Create audio converter thread");
                Thread timer = new Thread(new ThreadStart(AudioUrlConverter.TimerThread));
                timer.IsBackground = true;
                timer.Start();

                t_thrMonitorTimeout = new Thread(new ThreadStart(AudioUrlConverter.audioMonitor));
                t_thrMonitorTimeout.IsBackground = true;
                t_thrMonitorTimeout.Start();
            }
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
                    tmp.RecordTimeInSec = recordTimeInSec;
                    tmp.StartRecordTime = AudioUrlConverter.GetTick1s();
                    tmp.LocalFolderRecorded = GetFullPathToSharedNginxFolder(inputUrl);
                    tmp.OutputStreamUrl = AudioUrlConverter.GenUrl(GetShortPathToSharedNginxFolder(inputUrl) + "/audio.m3u8");

                    ListAudioConverter.Add(tmp);

                    // Create stream folder
                    Console.WriteLine("Create local ffmpeg output folder {0}", tmp.LocalFolderRecorded);
                    System.IO.Directory.CreateDirectory(tmp.LocalFolderRecorded);

                    // Create ffmpeg process : m3u8
                    string ffmpegCmd = string.Format("-y -i {0} -acodec mp3 {1}", 
                                                    inputUrl, tmp.LocalFolderRecorded + "\\audio.m3u8");
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
                            _ffmpegM3U8Processes.Add(newProcess);
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
                                _ffmpegMp3Processes.Add(newProcess);
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
                    }
                    else
                    {
                        tmp.OutputRecordUrl = "";
                    }
                    AudioUrlConverter.StartAudioConverterThread();
                }
                else /*Url existed, update new data */
                {
                    tmp = ListAudioConverter[index];
                }
                return tmp;
            }
        }

        public static void TerminateRecord(string inputUrl)
        {
            lock (_ensureThreadSafe)
            {
                //Check if input url already existed in last
                for (var i = 0; i < ListAudioConverter.Count; i++)
                {
                    if (inputUrl.Equals(ListAudioConverter[i].InputUrl))
                    {
                        DoTerminateFfmpegProcessAt(i);
                        break;
                    }
                }
            }
        }
    }


    [Route("api/[controller]")]
    [ApiController]
    public class AudioConverterController : ControllerBase
    {

        [HttpGet]
        public IActionResult GetAllInputUrl()
        {
            return Ok(AudioUrlConverter.GetAllRecordUrl());
        }

        [HttpPost] 
        public IActionResult ConvertUrlToM3U8(InputAudioConverter url)
        {
            if (url.InputUrl == null || url.InputUrl == String.Empty || url.RecordTimeInSec <= 0)
            {
                return BadRequest();
            }

            AudioUrlConverter answer = AudioUrlConverter.InsertRecord(url.InputUrl, url.RecordToMp3, url.RecordTimeInSec);
            if (answer.OutputStreamUrl == String.Empty)
            {
                return StatusCode(500);
            }

            return Ok(new
            {
                Sucess =  true, 
                Record = answer.OutputRecordUrl,
                Stream = answer.OutputStreamUrl,
            });
        }
    }
}
