
namespace MES_WebService.Server.Models
{
    public class Log
    {
        public int Id { get; set; }
        public required string SequenceName { get; set; }
        public required long RunningNumber { get; set; }
        public required int RunningNumberIndex { get; set; }
        public required string GeneratedRunningNumber { get; set; }
        public required string LotId { get; set; }
        public bool? DeleteFlag { get; set; }
        public DateTimeOffset? TimeStamp { get; set; }
    }
}
