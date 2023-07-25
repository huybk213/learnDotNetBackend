using System.ComponentModel.DataAnnotations;

namespace AudioApp.Models
{
    public class InputAudioConverter
    {
        public string InputUrl { get; set; } = default!;
        public bool RecordToMp3 { get; set; }
        public int RecordTimeInSec { get; set; }
    }

    public class InputRadioStationConverter
    {
        public string InputUrl { get; set; } = default!;
        public string StationName { get; set; } = default!;
        public string Description { get; set; } = default!;
    }
}
