
namespace Larasocket.Shared.Models
{
    public class BroadcastMessageModel
    {
        public string @event { get; set; }
        public string channels { get; set; }
        public string payload { get; set; }
        public string connection_id { get; set; }
    }
}
