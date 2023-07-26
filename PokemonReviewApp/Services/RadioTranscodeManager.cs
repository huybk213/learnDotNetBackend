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
        private static RadioTranscodeManager? _radioTranscode;

        public enum DeleteTranscodeResult
        {
            InvalidParam,
            StationNotExist,
            InternalError,
            Ok
        }
        public RadioTranscodeManager GetInstance()
        {
            if (_radioTranscode == null)
            {
                _radioTranscode = new RadioTranscodeManager();
                Log.Information("Create new station manager");
                var tmp = new StationDB();
            }

            return _radioTranscode;
        }


        public static OutputRadioStationConverter? GetStationInfoByName(string stationName)
        {
            OutputRadioStationConverter? tmp = StationDB.ReadItemInDbByName(stationName);
            if (tmp == null)
            {
                Log.Information($"Not found item {stationName} in db");
            }

            return tmp;
        }


        public static DeleteTranscodeResult UpdateStationNameAndDesc(string stationName, string newName, string newDescription)
        {
            DeleteTranscodeResult ret = DeleteTranscodeResult.StationNotExist;
            OutputRadioStationConverter? tmp = GetStationInfoByName(stationName) ;
            if (tmp != null)
            {
                Log.Information("Station exited, update new one");
                tmp.StationName = newName;
                tmp.Description = newDescription;

                if (StationDB.EditItemInDb(stationName, tmp))
                {
                    ret = DeleteTranscodeResult.Ok;
                }
                else
                {
                    ret = DeleteTranscodeResult.InternalError;
                }
            }
            
            return ret;
        }


        public static DeleteTranscodeResult UpdateStationUrl(string stationName, string newUrl)
        {
            DeleteTranscodeResult ret = DeleteTranscodeResult.StationNotExist;
            OutputRadioStationConverter ?tmp = GetStationInfoByName(stationName);
            if (tmp != null)
            {
                tmp.InputUrl = newUrl;
                if (StationDB.EditItemInDb(stationName, tmp))
                {
                    ret = DeleteTranscodeResult.Ok;
                }
                else
                {
                    ret = DeleteTranscodeResult.InternalError;
                }
            }

            return ret;
        }

        public static DeleteTranscodeResult RemoveStationInfoByName(string stationName)
        {
            DeleteTranscodeResult ret = DeleteTranscodeResult.StationNotExist;
            
            if (StationDB.RemoveItemInDb(stationName))
            {
                ret = DeleteTranscodeResult.Ok;
            }
            else
            {
                ret = DeleteTranscodeResult.InternalError;
            }

            return ret;
        }
        public static DeleteTranscodeResult InsertTranscodeInfo(OutputRadioStationConverter info)
        {
            Log.Information($"Input url {info.InputUrl}");
            Log.Information($"Station name {info.StationName}");
            Log.Information($"Descript url {info.Description}");
            Log.Information($"Output url {info.OutputUrl}");

            DeleteTranscodeResult ret = DeleteTranscodeResult.StationNotExist;
            if (StationDB.WriteNewItemToDb(info))
            {
                ret = DeleteTranscodeResult.Ok;
            }
            else
            {
                ret = DeleteTranscodeResult.InternalError;
            }
            return ret;
        }

        public static JArray GetAllTranscodedStationInfo()
        {
            var outputRadioStationInfo = StationDB.GetAllItemsInDb();
            var jArray = new JArray();

            if (outputRadioStationInfo != null)
            {
                foreach (var item in outputRadioStationInfo)
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
