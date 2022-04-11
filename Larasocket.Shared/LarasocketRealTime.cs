﻿using Larasocket.Shared.Models;
using Newtonsoft.Json;
using System;
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
    /// LarasocketClient
    /// The top-level class for the Ably Realtime library.
    /// </summary>
    public class LarasocketClient
    {
        public string ConnectionId { get; private set; }
        private ClientWebSocket client;
        private CancellationTokenSource cts;
        private readonly string _token;
        private readonly string _userUuid = Guid.NewGuid().ToString();
        private readonly Subject<ResponseMessage> _messageReceivedSubject = new Subject<ResponseMessage>();
        public IObservable<ResponseMessage> MessageReceived => _messageReceivedSubject.AsObservable();
        public LarasocketClient(string token)
        {
            _token = token;

            Connect();
        }

        public async void Connect()
        {
            client = new ClientWebSocket();
            cts = new CancellationTokenSource();
            var url = $"wss://ws.larasocket.com?token={_token}&uuid={_userUuid}";

            await client.ConnectAsync(new Uri(url), cts.Token);

            await Listen(client, cts);

            var connectMessage = new LinkMessage
            {
                action = "link",
                token = _token,
                uuid = _userUuid
            };
            await SendMessageAsync(connectMessage);
        }
        

        public async Task SubscribeToPublicChannel(string channelName)
        {
            var messages = new SubscribeMessage
            {
                action = "subscribe",
                channel = channelName,
                connection_id = ConnectionId,
                token = _token
            };

            await SendMessageAsync(messages);
        }
        
        public async Task SendMessageAsync<T>(T message)
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
                            if (rmessage.Text.StartsWith("{\n \"connection_id\":"))
                            {
                                ConnectionId = JsonConvert.DeserializeObject<HandshakeResponseMessage>(rmessage.Text).connection_id;
                            }

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
    }
}
