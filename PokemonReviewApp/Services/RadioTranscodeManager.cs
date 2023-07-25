using AudioApp.Models;
using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Timers;
using Newtonsoft.Json.Linq; 
using Serilog;
using AudioApp;
using System.Collections.Generic;
using audioConverter.Services;

namespace radioTranscodeManager.Services
{
    public class OutputRadioStationConverter : InputRadioStationConverter
    {
        public string OutputUrl { get; set; } = default!;
    }
    public class RadioTranscodeManager
    {
        public enum DeleteTranscodeResult
        {
            InvalidParam,
            StationNotExist,
            InternalError,
            Ok
        }
        //So luong thiet bi it nen cache vao RAM cho nhanh
        private static object _ensureThreadSafe = new Object();
        private static List<OutputRadioStationConverter> _outputRadioStationInfo = new List<OutputRadioStationConverter>();

        public static OutputRadioStationConverter? GetStationInfoByName(string stationName)
        {
            OutputRadioStationConverter tmp = null;
            lock (_ensureThreadSafe)
            {
                for (var i = 0; i < _outputRadioStationInfo.Count; i++)
                {
                    if (_outputRadioStationInfo[i].StationName.Equals(stationName))
                    {
                        tmp = _outputRadioStationInfo[i];
                        break;
                    }
                }
            }

            return tmp;
        }


        public static DeleteTranscodeResult UpdateStationName(string stationName, string newName, string newDescription)
        {
            DeleteTranscodeResult ret = DeleteTranscodeResult.StationNotExist;
            lock (_ensureThreadSafe)
            {
                for (var i = 0; i < _outputRadioStationInfo.Count; i++)
                {
                    if (_outputRadioStationInfo[i].StationName.Equals(stationName))
                    {
                        _outputRadioStationInfo[i].StationName = newName;
                        _outputRadioStationInfo[i].Description = newDescription;
                        ret = DeleteTranscodeResult.Ok;
                        break;
                    }
                }
            }
            return ret;
        }


        public static DeleteTranscodeResult UpdateStationUrl(string stationName, string newUrl)
        {
            DeleteTranscodeResult ret = DeleteTranscodeResult.StationNotExist;
            lock (_ensureThreadSafe)
            {
                for (var i = 0; i < _outputRadioStationInfo.Count; i++)
                {
                    if (_outputRadioStationInfo[i].StationName.Equals(stationName))
                    {
                        _outputRadioStationInfo[i].InputUrl = newUrl;
                        AudioUrlConverter.InsertRecord(newUrl, false, 0);
                        ret = DeleteTranscodeResult.Ok;
                        break;
                    }
                }
            }
            return ret;
        }

        public static Boolean RemoveStationInfoByName(string stationName)
        {
            bool retval = false;
            lock (_ensureThreadSafe)
            {
                for (var i = 0; i < _outputRadioStationInfo.Count; i++)
                {
                    if (_outputRadioStationInfo[i].StationName.Equals(stationName))
                    {
                        AudioUrlConverter.TerminateRecord(_outputRadioStationInfo[i].InputUrl);
                        _outputRadioStationInfo.RemoveAt(i);
                        retval = true;
                        break;
                    }
                }
            }
            return retval;
        }
        public static void InsertTranscodeInfo(OutputRadioStationConverter info)
        {
            Console.WriteLine($"Input url {info.InputUrl}");
            Console.WriteLine($"Station name {info.StationName}");
            Console.WriteLine($"Descript url {info.Description}");
            Console.WriteLine($"Output url {info.OutputUrl}");
            lock (_ensureThreadSafe)
            {
                if (!_outputRadioStationInfo.Contains(info))
                {
                    _outputRadioStationInfo.Add(info);
                }
                else
                {
                    Console.WriteLine("Item already existed");
                }
            }
        }

        public static JArray GetAllTranscodedStationInfo()
        {
            var jArray = new JArray();

            lock (_ensureThreadSafe)
            {
                foreach (var item in _outputRadioStationInfo)
                {
                    jArray.Add(new JObject(
                        new JProperty("StationName", item.StationName),
                        new JProperty("InputUrl", item.InputUrl),
                        new JProperty("OutputStream", item.OutputUrl),
                        new JProperty("Description", item.Description)
                    ));
                }
            }
            return jArray;
        }
    }

}
