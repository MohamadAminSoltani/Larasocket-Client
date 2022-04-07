using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Larasocket.Shared
{
    /// <summary>
    /// LarasocketRealTime
    /// The top-level class for the Ably Realtime library.
    /// </summary>
    public class LarasocketRealTime
    {

        public string ConnectionId { get; set; }
        private SynchronizationContext _synchronizationContext;
        internal ILogger Logger { get; private set; }
        private ClientWebSocket client;
        private CancellationTokenSource cts;
        private readonly string _token;
        internal volatile bool Disposed;
        private bool _disposing;
        /// <summary>
        /// Enable or disable text message conversion from binary to string (via 'MessageEncoding' property).
        /// Default: true
        /// </summary>
        public bool IsTextMessageConversionEnabled { get; set; } = true;

        internal LarasocketRealTime(string token)
        {
            //Channels = new RealtimeChannels(this, Connection, mobileDevice);

            //State = new RealtimeState(options.GetFallbackHosts()?.Shuffle().ToList(), options.NowFunc);
            _token = token;

            Connect();
        }
       

        public async void Connect()
        {
            if (Disposed)
            {
                throw new ObjectDisposedException("This instance has been disposed. Please create a new one.");
            }


            client = new ClientWebSocket();
            string guid = Guid.NewGuid().ToString();
            var url = $"wss://ws.larasocket.com?token={_token}&uuid={guid}";

            cts = new CancellationTokenSource();
            await client.ConnectAsync(new Uri(url), cts.Token);

            var connectMessage = new LinkMessage
            {
                action = "link",
                token = _token,
                uuid = guid
            };

            await Listen(client, cts);

            SendMessageAsync(connectMessage);
        }
        private readonly Subject<ResponseMessage> _messageReceivedSubject = new Subject<ResponseMessage>();

        public IObservable<ResponseMessage> MessageReceived => _messageReceivedSubject.AsObservable();

        public void SubscribeToPublicChannel(string channelName)
        {
            var messages = new SubscribeMessage
            {
                action = "subscribe",
                channel = channelName,
                connection_id = ConnectionId,
                token = _token
            };

            SendMessageAsync(messages);
        }
        
        public async void SendMessageAsync<T>(T message)
        {

            string serialisedMessage = JsonConvert.SerializeObject(message);

            var byteMessage = Encoding.UTF8.GetBytes(serialisedMessage);
            var segmnet = new ArraySegment<byte>(byteMessage);

            await client.SendAsync(segmnet, WebSocketMessageType.Text, true, cts.Token);

        }

        private async Task Listen(WebSocket client, CancellationTokenSource token)
        {
            await Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    WebSocketReceiveResult result;
                    ResponseMessage rmessage;
                    var message = new ArraySegment<byte>(new byte[4096]);
                    do
                    {
                        result = await client.ReceiveAsync(message, cts.Token);
                        var messageBytes = message.Skip(message.Offset).Take(result.Count).ToArray();
                        string serialisedMessae = Encoding.UTF8.GetString(messageBytes);
                        try
                        {
                            rmessage = ResponseMessage.TextMessage(serialisedMessae);
                            _messageReceivedSubject.OnNext(rmessage);

                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Invalide message format. {ex.Message}");
                            
                        }

                    } while (!result.EndOfMessage);
                }
            }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        }

        /// <summary>
        /// Disposes the current instance.
        /// Once disposed, it closes the connection and the library can't be used again.
        /// </summary>
        /// <param name="disposing">Whether the dispose method triggered it directly.</param>
        public virtual void Dispose(bool disposing)
        {
            if (Disposed)
            {
                return;
            }

            if (disposing)
            {
                try
                {
                    //Connection?.RemoveAllListeners();
                    //Channels?.CleanupChannels();
                    //Push.Dispose();
                }
                catch (Exception e)
                {
                    Logger.Error("Error disposing Ably Realtime", e);
                }
            }

            //Workflow.QueueCommand(DisposeCommand.Create().TriggeredBy($"AblyRealtime.Dispose({disposing}"));

            Disposed = true;
        }
    }
}
