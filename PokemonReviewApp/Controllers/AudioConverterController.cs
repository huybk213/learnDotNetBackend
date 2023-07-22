using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using AudioApp.Controllers;
using AudioApp.Models;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Timers;
using System.Threading.Tasks;
using System.IO;
using Microsoft.AspNetCore.SignalR.Protocol;
using System.Text;
using audioConverter;
using audioConverter.Services;
using static audioConverter.Services.AudioUrlConverter;

namespace AudioApp.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AudioConverterController : ControllerBase
    {
        [HttpDelete]
        public IActionResult TerminateStreamByUrl(string url)
        {
            DeleteRecordResult ret;

            if (!String.IsNullOrEmpty(url))
            {
                ret = AudioUrlConverter.TerminateRecord(url);
            }
            else
            {
                ret = DeleteRecordResult.InvalidParam;
            }

            switch (ret)
            {
                case DeleteRecordResult.Ok:
                    return Ok("Success");
                case DeleteRecordResult.InvalidParam:
                    return BadRequest();
                case DeleteRecordResult.UrlNotExist:
                    return Ok("Url not exist");
                default: return StatusCode(500);
            }
        }

        [HttpPost] 
        public IActionResult ConvertUrlToM3U8(InputAudioConverter url)
        {
            if (String.IsNullOrEmpty(url.InputUrl) || url.RecordTimeInSec <= 0)
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
                Record = answer.OutputRecordFileUrl,
                Stream = answer.OutputStreamUrl,
            });
        }

        
        [HttpGet] 
        public IActionResult GetAllStreamUrl()
        {
            return Content(AudioUrlConverter.GetAllRecordUrl().ToString(), "application/json");
        }
    }
}
