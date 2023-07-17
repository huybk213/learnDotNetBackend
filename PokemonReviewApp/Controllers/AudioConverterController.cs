using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PokemonReviewApp.Controllers;
using PokemonReviewApp.Models;
using System;
using System.Reflection;
using System.Threading;
using System.Timers;

namespace PokemonReviewApp.Controllers
{
    public class AudioUrlConverter : InputAudioConverter
    {
        public string convertedUrl { set; get; }
        public string fileRecordUrl { set; get; }
        public long recordTimeout { set; get; }
        public long startRecordTime;

        private static long fakeRecordCounter = 0;
        private static long fakeConvertCounter = 0;
        static private Thread threadMonitorConvertTimeout = null;
        static private long timer1s = 0;
        private static object synchronizationList = new Object();
        
        public static List<AudioUrlConverter> listAudioConverter = new List<AudioUrlConverter>();

        public static long getTick1s()
        {
            return timer1s;
        }
        public static string createRecordUrl()
        {
            return (fakeRecordCounter++).ToString();
        }
        public static string createConvertedUrl()
        {
            return (fakeConvertCounter++).ToString();
        }

        static private void timerThread()
        {
            while (true)
            {
                lock (synchronizationList)
                {
                    timer1s++;
                }
                Thread.Sleep(1000);
            }
        }

        static private void audioMonitor()
        {
            while (true)
            {
                //TODO: Add thread safe
                lock (synchronizationList)
                {
                    for (var i = 0; i < listAudioConverter.Count; i++)
                    {
                        var now = timer1s;
                        if (listAudioConverter[i].needRecord == true
                            && (now - listAudioConverter[i].startRecordTime >= listAudioConverter[i].recordTimeout))
                        {
                            System.Diagnostics.Debug.WriteLine("Record timeout");
                            listAudioConverter[i].needRecord = false;
                            //TODO close ffmpeg process
                            listAudioConverter.RemoveAt(i);
                            break;
                        }
                    }
                }
                Thread.Sleep(1000);
            }
        }

        public static void createConverterThread()
        {
            if (AudioUrlConverter.threadMonitorConvertTimeout == null)  /*Only create 1 time*/
            {
                System.Diagnostics.Debug.WriteLine("Create audio converter thread");
                Thread timer = new Thread(new ThreadStart(AudioUrlConverter.timerThread));
                timer.IsBackground = true;
                timer.Start();

                threadMonitorConvertTimeout = new Thread(new ThreadStart(AudioUrlConverter.audioMonitor));
                threadMonitorConvertTimeout.IsBackground = true;
                threadMonitorConvertTimeout.Start();
            }
        }

        public static List<string> getAllRecordUrl()
        {
            List<string> record = new List<string>();
            lock (synchronizationList)
            {
                for (var i = 0; i < listAudioConverter.Count; i++)
                {
                    record.Add(listAudioConverter[i].fileRecordUrl);
                }
            }
            return record;
        }

        public static AudioUrlConverter insertRecord(string inputUrl, Boolean needRecord, long recordTimeInSec)
        {
            List<string> record = new List<string>();

            lock (synchronizationList)
            {
                int index = -1;
                AudioUrlConverter tmp = new AudioUrlConverter();

                //Check if input url already existed in last
                for (var i = 0; i < listAudioConverter.Count; i++)
                {
                    if (inputUrl.Equals(listAudioConverter[i].inputUrl))
                    {
                        index = i;
                        break;
                    }
                }

                //if url not exsited -> create new one
                if (index == -1)
                {
                    tmp.inputUrl = inputUrl;
                    if (needRecord && recordTimeInSec > 0)
                    {
                        tmp.fileRecordUrl = AudioUrlConverter.createRecordUrl();
                        tmp.maxRecordTimeInSec = recordTimeInSec;
                        tmp.startRecordTime = AudioUrlConverter.getTick1s();
                        tmp.convertedUrl = AudioUrlConverter.createConvertedUrl();
                        tmp.needRecord = true;
                    }
                    else
                    {
                        tmp.maxRecordTimeInSec = 0;
                        tmp.fileRecordUrl = string.Empty;
                        tmp.convertedUrl = string.Empty;
                        tmp.needRecord = false;
                    }
                    listAudioConverter.Add(tmp);
                }
                else /*Url existed, update new data */
                {
                    tmp = listAudioConverter[index];
                }

                AudioUrlConverter.createConverterThread();
                return tmp;
            }
        }
    }


    [Route("api/[controller]")]
    [ApiController]
    public class AudioConverterController : ControllerBase
    {

        [HttpGet]
        public IActionResult getAllInputUrl()
        {
            return Ok(AudioUrlConverter.getAllRecordUrl());
        }

        [HttpPost] 
        public IActionResult convertUrlToM3U8(InputAudioConverter url)
        {
            //TODO: Verify input
            AudioUrlConverter answer = AudioUrlConverter.insertRecord(url.inputUrl, url.needRecord, url.maxRecordTimeInSec);

            return Ok(new
            {
                Sucess =  true, 
                record = answer.fileRecordUrl,
                targetUrl = answer.convertedUrl,
            });
        }
    }
}
