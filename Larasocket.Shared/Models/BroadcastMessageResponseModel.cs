using System.Collections.Generic;

namespace Larasocket.Shared.Models
{
    public class BroadcastMessageResponseModel
    {
        public bool IsSuccessfull { get; set; }
        public string Message { get; set; }
        public BroadcastMessageResponseModelErrors Errors { get; set; }
    }

    public class BroadcastMessageResponseModel500_401
    {
        public string message { get; set; }
    }

    public class BroadcastMessageResponseModel200
    {
        public string status { get; set; }
    }

    public class BroadcastMessageResponseModel422
    {
        public string message { get; set; }
        public BroadcastMessageResponseModelErrors errors { get; set; }
    }
    public class BroadcastMessageResponseModelErrors
    {
        public List<string> channels { get; set; }
        public List<string> @event { get; set; }
    }

}
