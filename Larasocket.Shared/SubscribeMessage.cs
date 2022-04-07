namespace Larasocket.Shared
{
    internal class SubscribeMessage
    {
        public string action { get; set; }
        public string channel { get; set; }
        public string connection_id { get; set; }
        public string token { get; set; }
    }
}