using AudioApp.Models;
using audioConverter.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using radioTranscodeManager.Services;
using System;
using System.Data;

using static radioTranscodeManager.Services.RadioTranscodeManager;

namespace audioConverter.Controllers
{
    [Route("station")]
    [ApiController]
    public class StationTranscodeController : Controller
    {
        private static void LoadStationInfoFromDb()
        {
            using (IDbConnection cnn = new SQLiteConnection(LoadConnectionString()))
            {
                try
                {
                    DataUser_AppVersion_Info output = cnn.Query<DataUser_AppVersion_Info>("select * from AppVersion_Info", new DynamicParameters()).FirstOrDefault();
                    return output;
                }
                catch
                {

                }

                return null;
            }
        }

        [HttpGet]
        [Route("get-all")]
        public IActionResult GetAllRadioTranscodeUrl()
        {
            return Content(RadioTranscodeManager.GetAllTranscodedStationInfo().ToString(), "application/json");
        }

        [Route("get-by-name")]
        [HttpGet]
        public IActionResult GetRadioTranscodeUrlByName(string stationName)
        {
            DeleteTranscodeResult ret;
            String Message = String.Empty;
            String Record = String.Empty;
            String StationName = String.Empty; 
            String Info = String.Empty;
            OutputRadioStationConverter tmp = null;

            if (!String.IsNullOrEmpty(stationName))
            {
                Console.WriteLine($"Remove station {stationName}");
                tmp = GetStationInfoByName(stationName);
                if (tmp == null)
                {
                    Console.WriteLine($"Station {stationName} not existed");
                    ret = DeleteTranscodeResult.StationNotExist;
                }
                else
                {
                    ret = DeleteTranscodeResult.Ok;
                }
            }
            else
            {
                ret = DeleteTranscodeResult.InvalidParam;
            }

            switch (ret)
            {
                case DeleteTranscodeResult.Ok:
                    return Ok(new
                            {
                                InputUrl = tmp.InputUrl,
                                OutputStream = tmp.OutputUrl,
                                StationName = stationName,
                                Description = tmp.Description
                            });

                case DeleteTranscodeResult.InvalidParam:
                    return BadRequest();
                case DeleteTranscodeResult.StationNotExist:
                    return Ok("Station not existed");
                default: return StatusCode(500);
            }
        }


        [Route("delete")]
        [HttpDelete]
        public IActionResult RemoveTranscodeRadioStation(string stationName)
        {
            DeleteTranscodeResult ret;

            if (!String.IsNullOrEmpty(stationName))
            {
                Console.WriteLine($"Remove station {stationName}");
                var tmp = RadioTranscodeManager.GetStationInfoByName(stationName);
                if (tmp == null)
                {
                    Console.WriteLine($"Station {stationName} not existed");
                    ret = DeleteTranscodeResult.StationNotExist;
                }
                else
                {
                    if (RemoveStationInfoByName(stationName))
                    {
                        Console.WriteLine($"Remove station {stationName} success");
                        ret = DeleteTranscodeResult.Ok;
                    }
                    else
                    {
                        Console.WriteLine($"Remove station {stationName} failed");
                        ret = DeleteTranscodeResult.InternalError;
                    }
                }
            }
            else
            {
                ret = DeleteTranscodeResult.InvalidParam;
            }

            switch (ret)
            {
                case DeleteTranscodeResult.Ok:
                    return Ok("Success");
                case DeleteTranscodeResult.InvalidParam:
                    return BadRequest();
                case DeleteTranscodeResult.StationNotExist:
                    return Ok("Station not existed");
                default: return StatusCode(500);
            }
        }

        [Route("edit-name-desc")]
        [HttpPut]
        public IActionResult EditTranscodeRadioStationName(string stationName, string newName, string newDescription)
        {
            DeleteTranscodeResult ret;

            if (!String.IsNullOrEmpty(stationName))
            {
                Console.WriteLine($"Edit station {stationName} to {newName}");
                ret = RadioTranscodeManager.UpdateStationName(stationName, newName, newDescription);
                if (ret == DeleteTranscodeResult.StationNotExist)
                {
                    Console.WriteLine($"Station {stationName} not existed");
                    ret = DeleteTranscodeResult.StationNotExist;
                }
                else
                {
                    Console.WriteLine($"Station {stationName} updated to new name {newName}, {newDescription}");
                    ret = DeleteTranscodeResult.Ok;
                }
            }
            else
            {
                ret = DeleteTranscodeResult.InvalidParam;
            }

            switch (ret)
            {
                case DeleteTranscodeResult.Ok:
                    return Ok("Success");
                case DeleteTranscodeResult.InvalidParam:
                    return BadRequest();
                case DeleteTranscodeResult.StationNotExist:
                    return Ok("Station not existed");
                default: return StatusCode(500);
            }
        }

        [Route("edit-url")]
        [HttpPut]
        public IActionResult EditTranscodeRadioStationUrl(string stationName, string newUrl)
        {
            DeleteTranscodeResult ret;

            if (!String.IsNullOrEmpty(stationName))
            {
                Console.WriteLine($"Edit station {stationName} URL to {newUrl}");
                ret = RadioTranscodeManager.UpdateStationUrl(stationName, newUrl);
                if (ret == DeleteTranscodeResult.StationNotExist)
                {
                    Console.WriteLine($"Station {stationName} not existed");
                    ret = DeleteTranscodeResult.StationNotExist;
                }
                else
                {
                    Console.WriteLine($"Station {stationName} updated to new url : {newUrl}");
                    ret = DeleteTranscodeResult.Ok;
                }
            }
            else
            {
                ret = DeleteTranscodeResult.InvalidParam;
            }

            switch (ret)
            {
                case DeleteTranscodeResult.Ok:
                    return Ok("Success");
                case DeleteTranscodeResult.InvalidParam:
                    return BadRequest();
                case DeleteTranscodeResult.StationNotExist:
                    return Ok("Station not existed");
                default: return StatusCode(500);
            }
        }


        [HttpPost]
        [Route("create-new-station")]
        public IActionResult TranscodeRadioStation(InputRadioStationConverter url)
        {
            if (String.IsNullOrEmpty(url.InputUrl) || String.IsNullOrEmpty(url.StationName))
            {
                return BadRequest("Invalid input url or station name");
            }
            string result = String.Empty;
            string convertUrl = String.Empty;
            string stationName = url.StationName;
            string description = url.Description;

            var tmp = RadioTranscodeManager.GetStationInfoByName(url.StationName);
            if (tmp != null)
            {
                result = "Url already existed";
                convertUrl = tmp.OutputUrl;
                stationName = tmp.StationName;
                description = tmp.Description;
            }
            else
            {

                AudioUrlConverter.ConvertUrlInfo newConvert = AudioUrlConverter.InsertRecord(url.InputUrl, false, 0);
                if (newConvert.OutputStreamUrl == String.Empty)
                {
                    return StatusCode(500);
                }

                //Save output and description
                OutputRadioStationConverter newStation = new OutputRadioStationConverter();
                newStation.InputUrl = url.InputUrl;
                newStation.OutputUrl = newConvert.OutputStreamUrl;
                newStation.StationName = url.StationName;
                newStation.Description = url.Description;
                RadioTranscodeManager.InsertTranscodeInfo(newStation);

                result = "Success";
                convertUrl = newConvert.OutputStreamUrl;
            }

            return Ok(new
            {
                Message = result,
                Record = convertUrl,
                StationName = stationName,
                Info = description
            });
        }
    }
}
