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

namespace PokemonReviewApp.Controllers
{
    public class AudioUrlConverter : InputAudioConverter
    {
        public string LocalFolderRecorded;
        public long RecordTimeoutInSec { set; get; }
        public long StartRecordTime;
        public string OutputStreamUrl;
        public string OutputRecordUrl;
        private static long _recordCounter = 0;
        static private Thread t_thrMonitorConvertTimeout;
        static private long s_timer1s = 0;
        private static object _synchronizationList = new Object();
        private static List<Process> FfmpegM3U8Process = new List<Process>();
        private static List<Process> FfmpegMP3Process= new List<Process>();
        public static List<AudioUrlConverter> ListAudioConverter = new List<AudioUrlConverter>();
        private static string _nginxPath = "";
        private static string _ffmpegPath = "";
        private static string _prefixUrl = "";

        //Audio timer monitor
        public static long GetTick1s()
        {
            return s_timer1s;
        }

        private static string GetShortPathToSharedNginxFolder(string inputUrl)
        {
            return inputUrl.GetHashCode().ToString().Replace("-", "");
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

        public static void SetFFmpegPath(string path)
        {
            _ffmpegPath = path;
        }

        public static string MakeRecordUrl()
        {
            //var fileRecorded = _nginxPath + "\\" + (_fakeRecordCounter++).ToString();
            //return fileRecorded;
            return String.Empty;
        }
        public static string ConvertedUrl(string localFileName)
        {
            return _prefixUrl + localFileName;
        }

        static private void timerThread()
        {
            while (true)
            {
                lock (_synchronizationList)
                {
                    s_timer1s++;
                }
                Thread.Sleep(1000);
            }
        }

        static private void audioMonitor()
        {
            while (true)
            {
                //TODO: Add thread safe
                lock (_synchronizationList)
                {
                    for (var i = 0; i < ListAudioConverter.Count; i++)
                    {
                        var now = s_timer1s;
                        var streamCount = now - ListAudioConverter[i].StartRecordTime;

                        Boolean terminateFfmpeg = false;
                        //checked ffmpeg stderr
                        if (ListAudioConverter[i].RecordTimeInSec > 0)
                        {
                            StreamReader stderr = FfmpegM3U8Process[i].StandardError;
                            string errMsg = string.Empty;
                            int peekSize;

                            peekSize = stderr.Peek();
                            if (peekSize > 0)
                            {
                                errMsg += stderr.ReadToEnd();
                            }

                            if (errMsg != String.Empty 
                                && (errMsg.Contains("Error opening input") || errMsg.Contains("404 Not Found")))
                            {
                                terminateFfmpeg = true;
                                Console.WriteLine("FFMPEG process error, exit now");
                            }

                            stderr = FfmpegMP3Process[i].StandardError;
                            errMsg = string.Empty;

                            peekSize = stderr.Peek();
                            if (peekSize > 0)
                            {
                                errMsg += stderr.ReadToEnd();
                            }

                            if (errMsg != String.Empty
                                && (errMsg.Contains("Error opening input") || errMsg.Contains("404 Not Found")))
                            {
                                terminateFfmpeg = true;
                                Console.WriteLine("FFMPEG process error, exit now");
                            }
                        }

                        if ((streamCount >= ListAudioConverter[i].RecordTimeInSec) || terminateFfmpeg)
                        {
                            Console.WriteLine("Record {0} over, stream time {1}:{2}", 
                                            ListAudioConverter[i].InputUrl,
                                            streamCount,
                                            ListAudioConverter[i].RecordTimeInSec);
                            //TODO close ffmpeg process
                            try
                            {
                                FfmpegM3U8Process[i].Kill();
                                FfmpegM3U8Process.RemoveAt(i);
                                FfmpegMP3Process[i].Kill();
                                FfmpegMP3Process.RemoveAt(i);
                                Console.WriteLine("Closed");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Close subprocess failed {0}", ex.ToString());
                            }

                            //Delete folder
                            Console.WriteLine("Delete folder {0}", ListAudioConverter[i].LocalFolderRecorded);
                            Task.Factory.StartNew(path => Directory.Delete((string)path, true), ListAudioConverter[i].LocalFolderRecorded);

                            ListAudioConverter.RemoveAt(i);
                            break;
                        }
                    }
                }
                Thread.Sleep(1000);
            }
        }

        public static void CreateAudioConverterThread()
        {
            if (AudioUrlConverter.t_thrMonitorConvertTimeout == null)  /*Only create 1 time*/
            {
                Console.WriteLine("Create audio converter thread");
                Thread timer = new Thread(new ThreadStart(AudioUrlConverter.timerThread));
                timer.IsBackground = true;
                timer.Start();

                t_thrMonitorConvertTimeout = new Thread(new ThreadStart(AudioUrlConverter.audioMonitor));
                t_thrMonitorConvertTimeout.IsBackground = true;
                t_thrMonitorConvertTimeout.Start();
            }
        }

        public static List<string> getAllRecordUrl()
        {
            List<string> record = new List<string>();
            lock (_synchronizationList)
            {
                for (var i = 0; i < ListAudioConverter.Count; i++)
                {
                    record.Add(ListAudioConverter[i].OutputRecordUrl);
                }
            }
            return record;
        }

        public static AudioUrlConverter InsertRecord(string inputUrl, bool needRecordToMp3, long recordTimeInSec)
        {
            List<string> record = new List<string>();

            lock (_synchronizationList)
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
                    tmp.OutputStreamUrl = AudioUrlConverter.ConvertedUrl(GetShortPathToSharedNginxFolder(inputUrl) + "/info.m3u8");

                    ListAudioConverter.Add(tmp);

                    // Create stream folder
                    Console.WriteLine("Create local ffmpeg output folder {0}", tmp.LocalFolderRecorded);
                    System.IO.Directory.CreateDirectory(tmp.LocalFolderRecorded);

                    // Create ffmpeg process : m3u8
                    string ffmpegCmd = string.Format("-i {0} -acodec mp3 {1}", 
                                                    inputUrl, tmp.LocalFolderRecorded + "\\info.m3u8");
                    Console.WriteLine("FFMPEG cmd {0}", ffmpegCmd);
                    var m3u8ProcessInfo = new ProcessStartInfo(_ffmpegPath, ffmpegCmd)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = false,
                        RedirectStandardError = true
                    };

                    try
                    {

                        FfmpegM3U8Process.Add(System.Diagnostics.Process.Start(m3u8ProcessInfo));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("FFMPEG process failed {0}", ex.Message);
                    }

                    if (needRecordToMp3)
                    {
                        //mp3
                        ffmpegCmd = string.Format("-i {0} -acodec libmp3lame -b:a 128k {1}",
                                                       inputUrl, tmp.LocalFolderRecorded + "\\info.mp3");
                        Console.WriteLine("FFMPEG cmd {0}", ffmpegCmd);
                        var mp3ProcessInfo = new ProcessStartInfo(_ffmpegPath, ffmpegCmd)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = false,
                            RedirectStandardError = true
                        };

                        try
                        {

                            FfmpegMP3Process.Add(System.Diagnostics.Process.Start(mp3ProcessInfo));
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
                    AudioUrlConverter.CreateAudioConverterThread();
                }
                else /*Url existed, update new data */
                {
                    tmp = ListAudioConverter[index];
                }
                return tmp;
            }
        }

        public static void terminateRecord(string inputUrl)
        {
            lock (_synchronizationList)
            {
                //Check if input url already existed in last
                for (var i = 0; i < ListAudioConverter.Count; i++)
                {
                    if (inputUrl.Equals(ListAudioConverter[i].InputUrl))
                    {
                        //TODO terminate ffmpeg process
                        ListAudioConverter.RemoveAt(i);
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
            return Ok(AudioUrlConverter.getAllRecordUrl());
        }

        [HttpPost] 
        public IActionResult ConvertUrlToM3U8(InputAudioConverter url)
        {
            //TODO: Verify input
            AudioUrlConverter answer = AudioUrlConverter.InsertRecord(url.InputUrl, url.RecordToMp3, url.RecordTimeInSec);
            return Ok(new
            {
                Sucess =  true, 
                Record = answer.OutputRecordUrl,
                Stream = answer.OutputStreamUrl,
            });
        }
    }
}
