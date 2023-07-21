namespace AudioApp.Models
{
    public class InputAudioConverter
    {
        public string InputUrl { get; set; } = default!;
        public bool RecordToMp3 { get; set; }
        public long RecordTimeInSec { get; set; }
    }
}
