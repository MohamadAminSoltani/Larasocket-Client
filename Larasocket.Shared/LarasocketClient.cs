using Larasocket.Shared.Exceptions;
using Larasocket.Shared.Logging;
using Larasocket.Shared.Models;
using Larasocket.Shared.Threading;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
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
    /// LarasocketClient
    /// The top-level class for the Ably Realtime library.
    /// </summary>
    public partial class LarasocketClient : ILarasocketClient
    {
        private static readonly ILog Logger = GetLogger();

        private readonly WebsocketAsyncLock _locker = new WebsocketAsyncLock();
        private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connectionFactory;

        private Uri _url;
        private Timer _lastChanceTimer;
        private DateTime _lastReceivedMsg = DateTime.UtcNow;

        private bool _disposing;
        private bool _reconnecting;
        private bool _stopping;
        private bool _isReconnectionEnabled = true;
        private WebSocket _client;
        private CancellationTokenSource _cancellation;
        private CancellationTokenSource _cancellationTotal;

        public string ConnectionId { get; private set; }
        private readonly string _token;
        private static string _channel;
        private readonly string _userUuid = Guid.NewGuid().ToString();

        private readonly Subject<ResponseMessage> _messageReceivedSubject = new Subject<ResponseMessage>();
        private readonly Subject<ReconnectionInfo> _reconnectionSubject = new Subject<ReconnectionInfo>();
        private readonly Subject<DisconnectionInfo> _disconnectedSubject = new Subject<DisconnectionInfo>();



        /// <summary>
        /// A simple Larasocket client with built-in reconnection and error handling
        /// </summary>
        /// <param name="url">Target websocket url (wss://)</param>
        /// <param name="clientFactory">Optional factory for native ClientWebSocket, use it whenever you need some custom features (proxy, settings, etc)</param>
        public LarasocketClient(string token, Func<ClientWebSocket> clientFactory = null)
            : this(token, GetClientFactory(clientFactory))
        {
        }

        /// <summary>
        /// A simple Larasocket client with built-in reconnection and error handling
        /// </summary>
        /// <param name="url">Target websocket url (wss://)</param>
        /// <param name="clientFactory">Optional factory for native ClientWebSocket, use it whenever you need some custom features (proxy, settings, etc)</param>
        public LarasocketClient(string token, Func<Uri, CancellationToken, Task<WebSocket>> connectionFactory)
        {
            _token = token;
            var url = $"wss://ws.larasocket.com?token={_token}&uuid={_userUuid}";
            _url = new Uri(url);

            _connectionFactory = connectionFactory ?? (async (uri, webtoken) =>
            {
                var client = new ClientWebSocket();
                await client.ConnectAsync(new Uri(url), webtoken).ConfigureAwait(false);
                return client;
            });

        }

        /// <summary>
        /// Stream with received message (raw format)
        /// </summary>
        public IObservable<ResponseMessage> MessageReceived => _messageReceivedSubject.AsObservable();

        /// <summary>
        /// Stream for reconnection event (triggered after the new connection) 
        /// </summary>
        public IObservable<ReconnectionInfo> ReconnectionHappened => _reconnectionSubject.AsObservable();

        /// <summary>
        /// Stream for disconnection event (triggered after the connection was lost) 
        /// </summary>
        public IObservable<DisconnectionInfo> DisconnectionHappened => _disconnectedSubject.AsObservable();

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if no message comes from server.
        /// Set null to disable this feature. 
        /// Default: 1 minute
        /// </summary>
        public TimeSpan? ReconnectTimeout { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if last reconnection failed.
        /// Set null to disable this feature. 
        /// Default: 1 minute
        /// </summary>
        public TimeSpan? ErrorReconnectTimeout { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Enable or disable reconnection functionality (enabled by default)
        /// </summary>
        public bool IsReconnectionEnabled
        {
            get => _isReconnectionEnabled;
            set
            {
                _isReconnectionEnabled = value;

                if (IsStarted)
                {
                    if (_isReconnectionEnabled)
                    {
                        ActivateLastChance();
                    }
                    else
                    {
                        DeactivateLastChance();
                    }
                }
            }
        }

        /// <summary>
        /// Get or set the name of the current websocket client instance.
        /// For logging purpose (in case you use more parallel websocket clients and want to distinguish between them)
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Returns true if Start() method was called at least once. False if not started or disposed
        /// </summary>
        public bool IsStarted { get; private set; }

        /// <summary>
        /// Returns true if client is running and connected to the server
        /// </summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Enable or disable text message conversion from binary to string (via 'MessageEncoding' property).
        /// Default: true
        /// </summary>
        public bool IsTextMessageConversionEnabled { get; set; } = true;

        public Encoding MessageEncoding { get; set; }

        public ClientWebSocket NativeClient => GetSpecificOrThrow(_client);

        /// <summary>
        /// Terminate the websocket connection and cleanup everything
        /// </summary>
        public void Dispose()
        {
            _disposing = true;
            Logger.Debug(L("Disposing.."));
            try
            {
                _messagesTextToSendQueue?.Writer.Complete();
                _messagesBinaryToSendQueue?.Writer.Complete();
                _lastChanceTimer?.Dispose();
                _cancellation?.Cancel();
                _cancellationTotal?.Cancel();
                _client?.Abort();
                _client?.Dispose();
                _cancellation?.Dispose();
                _cancellationTotal?.Dispose();
                _messageReceivedSubject.OnCompleted();
                _reconnectionSubject.OnCompleted();
            }
            catch (Exception e)
            {
                Logger.Error(e, L($"Failed to dispose client, error: {e.Message}"));
            }

            IsRunning = false;
            IsStarted = false;
            _disconnectedSubject.OnNext(DisconnectionInfo.Create(DisconnectionType.Exit, _client, null));
            _disconnectedSubject.OnCompleted();
        }

        /// <summary>
        /// Start listening to the websocket stream on the background thread.
        /// In case of connection error it doesn't throw an exception.
        /// Only streams a message via 'DisconnectionHappened' and logs it. 
        /// </summary>
        public Task Start()
        {
            return StartInternal(false);
        }

        /// <summary>
        /// Start listening to the websocket stream on the background thread. 
        /// In case of connection error it throws an exception.
        /// Fail fast approach. 
        /// </summary>
        public Task StartOrFail()
        {
            return StartInternal(true);
        }

        /// <summary>
        /// Stop/close websocket connection with custom close code.
        /// Method doesn't throw exception, only logs it and mark client as closed. 
        /// </summary>
        /// <returns>Returns true if close was initiated successfully</returns>
        public async Task<bool> Stop(WebSocketCloseStatus status, string statusDescription)
        {
            var result = await StopInternal(
                _client,
                status,
                statusDescription,
                null,
                false,
                false).ConfigureAwait(false);
            _disconnectedSubject.OnNext(DisconnectionInfo.Create(DisconnectionType.ByUser, _client, null));
            return result;
        }

        /// <summary>
        /// Stop/close websocket connection with custom close code.
        /// Method could throw exceptions, but client is marked as closed anyway.
        /// </summary>
        /// <returns>Returns true if close was initiated successfully</returns>
        public async Task<bool> StopOrFail(WebSocketCloseStatus status, string statusDescription)
        {
            var result = await StopInternal(
                _client,
                status,
                statusDescription,
                null,
                true,
                false).ConfigureAwait(false);
            _disconnectedSubject.OnNext(DisconnectionInfo.Create(DisconnectionType.ByUser, _client, null));
            return result;
        }
        private static Func<Uri, CancellationToken, Task<WebSocket>> GetClientFactory(Func<ClientWebSocket> clientFactory)
        {
            if (clientFactory == null)
                return null;

            return (async (uri, token) =>
            {
                var client = clientFactory();
                await client.ConnectAsync(uri, token).ConfigureAwait(false);
                return client;
            });
        }

        private async Task StartInternal(bool failFast)
        {
            if (_disposing)
            {
                throw new LarasocketException(L("Client is already disposed, starting not possible"));
            }

            if (IsStarted)
            {
                Logger.Debug(L("Client already started, ignoring.."));
                return;
            }

            IsStarted = true;

            Logger.Debug(L("Starting.."));
            _cancellation = new CancellationTokenSource();
            _cancellationTotal = new CancellationTokenSource();

            await StartClient(_url, _cancellation.Token, ReconnectionType.Initial, failFast).ConfigureAwait(false);

            StartBackgroundThreadForSendingText();
            StartBackgroundThreadForSendingBinary();
        }

        private async Task<bool> StopInternal(WebSocket client, WebSocketCloseStatus status, string statusDescription,
            CancellationToken? cancellation, bool failFast, bool byServer)
        {
            if (_disposing)
            {
                throw new LarasocketException(L("Client is already disposed, stopping not possible"));
            }

            if (!IsRunning)
            {
                Logger.Info(L("Client is already stopped"));

                return false;
            }

            var result = false;
            if (client == null)
            {
                IsStarted = false;
                IsRunning = false;
                return false;
            }

            DeactivateLastChance();

            try
            {
                var cancellationToken = cancellation ?? CancellationToken.None;
                _stopping = true;
                if (byServer)
                    await client.CloseOutputAsync(status, statusDescription, cancellationToken);
                else
                    await client.CloseAsync(status, statusDescription, cancellationToken);
                result = true;
            }
            catch (Exception e)
            {
                Logger.Error(e, L($"Error while stopping client, message: '{e.Message}'"));

                if (failFast)
                {
                    // fail fast, propagate exception
                    throw new LarasocketException($"Failed to stop Websocket client, error: '{e.Message}'", e);
                }
            }
            finally
            {
                IsStarted = false;
                IsRunning = false;
                _stopping = false;
            }

            return result;
        }
        private async Task StartClient(Uri uri, CancellationToken token, ReconnectionType type, bool failFast)
        {
            DeactivateLastChance();

            try
            {
                _client = await _connectionFactory(uri, token).ConfigureAwait(false);
                _ = Listen(_client, token);
                IsRunning = true;
                IsStarted = true;
                _reconnectionSubject.OnNext(ReconnectionInfo.Create(type));
                _lastReceivedMsg = DateTime.UtcNow;
                ActivateLastChance();

                await Connect();
            }
            catch (Exception e)
            {
                var info = DisconnectionInfo.Create(DisconnectionType.Error, _client, e);
                _disconnectedSubject.OnNext(info);

                if (info.CancelReconnection)
                {
                    // reconnection canceled by user, do nothing
                    Logger.Error(e, L($"Exception while connecting. " +
                                      $"Reconnecting canceled by user, exiting. Error: '{e.Message}'"));
                    return;
                }

                if (failFast)
                {
                    // fail fast, propagate exception
                    // do not reconnect
                    throw new LarasocketException($"Failed to start Websocket client, error: '{e.Message}'", e);
                }

                if (ErrorReconnectTimeout == null)
                {
                    Logger.Error(e, L($"Exception while connecting. " +
                                      $"Reconnecting disabled, exiting. Error: '{e.Message}'"));
                    return;
                }

                var timeout = ErrorReconnectTimeout.Value;
                Logger.Error(e, L($"Exception while connecting. " +
                                  $"Waiting {timeout.TotalSeconds} sec before next reconnection try. Error: '{e.Message}'"));
                await Task.Delay(timeout, token).ConfigureAwait(false);
                await Reconnect(ReconnectionType.Error, false, e).ConfigureAwait(false);
            }
        }

        private bool IsClientConnected()
        {
            return _client.State == WebSocketState.Open;
        }


        private bool ShouldIgnoreReconnection(WebSocket client)
        {
            // reconnection already in progress or client stopped/ disposed,
            var inProgress = _disposing || _reconnecting || _stopping;

            // already reconnected
            var differentClient = client != _client;

            return inProgress || differentClient;
        }

        private Encoding GetEncoding()
        {
            if (MessageEncoding == null)
                MessageEncoding = Encoding.UTF8;
            return MessageEncoding;
        }


        private ClientWebSocket GetSpecificOrThrow(WebSocket client)
        {
            if (client == null)
                return null;
            var specific = client as ClientWebSocket;
            if (specific == null)
                throw new LarasocketException("Cannot cast 'WebSocket' client to 'ClientWebSocket', " +
                                             "provide correct type via factory or don't use this property at all.");
            return specific;
        }


        private DisconnectionType TranslateTypeToDisconnection(ReconnectionType type)
        {
            // beware enum indexes must correspond to each other
            return (DisconnectionType)type;
        }

        private async Task Connect()
        {

            var connectMessageModel = new LinkMessage
            {
                action = "link",
                token = _token,
                uuid = _userUuid
            };
            string msg = JsonConvert.SerializeObject(connectMessageModel);

            await Task.Run(() => Send(msg));
        }

        private async Task Listen(WebSocket client, CancellationToken token)
        {
            Exception causedException = null;

            await Task.Factory.StartNew(async () =>
            {
                try
                {
                    while (true)
                    {
                        WebSocketReceiveResult result;
                        ResponseMessage rmessage;
                        var message = new ArraySegment<byte>(new byte[4096]);
                        do
                        {
                            result = await client.ReceiveAsync(message, token);
                            var messageBytes = message.Skip(message.Offset).Take(result.Count).ToArray();
                            string serialisedMessae = Encoding.UTF8.GetString(messageBytes);
                            try
                            {
                                rmessage = ResponseMessage.TextMessage(serialisedMessae);
                                if (rmessage.Text.StartsWith("{\n    \"connection_id\":"))
                                {
                                    ConnectionId = JsonConvert.DeserializeObject<HandshakeResponseMessage>(rmessage.Text).connection_id;
                                    if(!string.IsNullOrEmpty(_channel))
                                        await SubscribeToPublicChannel(_channel);
                                }
                                _messageReceivedSubject.OnNext(rmessage);

                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Invalide message format. {ex.Message}");

                            }

                        } while (!result.EndOfMessage);
                    }
                }
                catch (TaskCanceledException e)
                {
                    // task was canceled, ignore
                    causedException = e;
                }
                catch (OperationCanceledException e)
                {
                    // operation was canceled, ignore
                    causedException = e;
                }
                catch (ObjectDisposedException e)
                {
                    // client was disposed, ignore
                    causedException = e;
                }
                catch (Exception e)
                {
                    Logger.Error(e, L($"Error while listening to websocket stream, error: '{e.Message}'"));
                    causedException = e;
                }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            if (ShouldIgnoreReconnection(client) || !IsStarted)
            {
                // reconnection already in progress or client stopped/disposed, do nothing
                return;
            }

            // listening thread is lost, we have to reconnect
            _ = ReconnectSynchronized(ReconnectionType.Lost, false, causedException);
        }

        private async Task ListenR(WebSocket client, CancellationToken token)
        {
            Exception causedException = null;
            try
            {
                // define buffer here and reuse, to avoid more allocation
                const int chunkSize = 1024 * 4;
                var buffer = new ArraySegment<byte>(new byte[chunkSize]);

                do
                {
                    WebSocketReceiveResult result;
                    byte[] resultArrayWithTrailing = null;
                    var resultArraySize = 0;
                    var isResultArrayCloned = false;
                    MemoryStream ms = null;

                    while (true)
                    {
                        result = await client.ReceiveAsync(buffer, token);
                        var currentChunk = buffer.Array;
                        var currentChunkSize = result.Count;

                        var isFirstChunk = resultArrayWithTrailing == null;
                        if (isFirstChunk)
                        {
                            // first chunk, use buffer as reference, do not allocate anything
                            resultArraySize += currentChunkSize;
                            resultArrayWithTrailing = currentChunk;
                            isResultArrayCloned = false;
                        }
                        else if (currentChunk == null)
                        {
                            // weird chunk, do nothing
                        }
                        else
                        {
                            // received more chunks, lets merge them via memory stream
                            if (ms == null)
                            {
                                // create memory stream and insert first chunk
                                ms = new MemoryStream();
                                ms.Write(resultArrayWithTrailing, 0, resultArraySize);
                            }

                            // insert current chunk
                            ms.Write(currentChunk, buffer.Offset, currentChunkSize);
                        }

                        if (result.EndOfMessage)
                        {
                            break;
                        }

                        if (isResultArrayCloned)
                            continue;

                        // we got more chunks incoming, need to clone first chunk
                        resultArrayWithTrailing = resultArrayWithTrailing?.ToArray();
                        isResultArrayCloned = true;
                    }

                    ms?.Seek(0, SeekOrigin.Begin);

                    ResponseMessage message;
                    if (result.MessageType == WebSocketMessageType.Text && IsTextMessageConversionEnabled)
                    {
                        var data = ms != null ?
                            GetEncoding().GetString(ms.ToArray()) :
                            resultArrayWithTrailing != null ?
                                GetEncoding().GetString(resultArrayWithTrailing, 0, resultArraySize) :
                                null;

                        message = ResponseMessage.TextMessage(data);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Logger.Trace(L($"Received close message"));

                        if (!IsStarted || _stopping)
                        {
                            return;
                        }

                        var info = DisconnectionInfo.Create(DisconnectionType.ByServer, client, null);
                        _disconnectedSubject.OnNext(info);

                        if (info.CancelClosing)
                        {
                            // closing canceled, reconnect if enabled
                            if (IsReconnectionEnabled)
                            {
                                throw new OperationCanceledException("Websocket connection was closed by server");
                            }

                            continue;
                        }

                        await StopInternal(client, WebSocketCloseStatus.NormalClosure, "Closing",
                            token, false, true);

                        // reconnect if enabled
                        if (IsReconnectionEnabled && !ShouldIgnoreReconnection(client))
                        {
                            _ = ReconnectSynchronized(ReconnectionType.Lost, false, null);
                        }

                        return;
                    }
                    else
                    {
                        if (ms != null)
                        {
                            message = ResponseMessage.BinaryMessage(ms.ToArray());
                        }
                        else
                        {
                            Array.Resize(ref resultArrayWithTrailing, resultArraySize);
                            message = ResponseMessage.BinaryMessage(resultArrayWithTrailing);
                        }
                    }

                    ms?.Dispose();

                    Logger.Trace(L($"Received:  {message}"));
                    _lastReceivedMsg = DateTime.UtcNow;
                    if (message.Text.StartsWith("{\n    \"connection_id\":"))
                    {
                        ConnectionId = JsonConvert.DeserializeObject<HandshakeResponseMessage>(message.Text).connection_id;
                    }
                    _messageReceivedSubject.OnNext(message);

                } while (client.State == WebSocketState.Open && !token.IsCancellationRequested);
            }
            catch (TaskCanceledException e)
            {
                // task was canceled, ignore
                causedException = e;
            }
            catch (OperationCanceledException e)
            {
                // operation was canceled, ignore
                causedException = e;
            }
            catch (ObjectDisposedException e)
            {
                // client was disposed, ignore
                causedException = e;
            }
            catch (Exception e)
            {
                Logger.Error(e, L($"Error while listening to websocket stream, error: '{e.Message}'"));
                causedException = e;
            }


            if (ShouldIgnoreReconnection(client) || !IsStarted)
            {
                // reconnection already in progress or client stopped/disposed, do nothing
                return;
            }

            // listening thread is lost, we have to reconnect
            _ = ReconnectSynchronized(ReconnectionType.Lost, false, causedException);
        }

        public async Task SubscribeToPublicChannel(string channelName)
        {
            _channel = channelName;
            var messageModel = new SubscribeMessage
            {
                action = "subscribe",
                channel = channelName,
                connection_id = ConnectionId,
                token = _token
            };

            string msg = JsonConvert.SerializeObject(messageModel);

            while (ConnectionId == null)
            {
                await Task.Delay(1000);
            }

            await Task.Run(() => Send(msg));
        }

        //public async Task SendMessageAsync<T>(T message)
        //{
        //    string serialisedMessage = JsonConvert.SerializeObject(message);

        //    var byteMessage = Encoding.UTF8.GetBytes(serialisedMessage);
        //    var segmnet = new ArraySegment<byte>(byteMessage);

        //    await client.SendAsync(segmnet, WebSocketMessageType.Text, true, cts.Token);
        //}

        //private async Task Listen(WebSocket client, CancellationTokenSource token)
        //{
        //    await Task.Factory.StartNew(async () =>
        //    {
        //        while (true)
        //        {
        //            WebSocketReceiveResult result;
        //            ResponseMessage rmessage;
        //            var message = new ArraySegment<byte>(new byte[4096]);
        //            do
        //            {
        //                result = await client.ReceiveAsync(message, cts.Token);
        //                var messageBytes = message.Skip(message.Offset).Take(result.Count).ToArray();
        //                string serialisedMessae = Encoding.UTF8.GetString(messageBytes);
        //                try
        //                {
        //                    rmessage = ResponseMessage.TextMessage(serialisedMessae);
        //                    if (rmessage.Text.StartsWith("{\n \"connection_id\":"))
        //                    {
        //                        ConnectionId = JsonConvert.DeserializeObject<HandshakeResponseMessage>(rmessage.Text).connection_id;
        //                    }

        //                    _messageReceivedSubject.OnNext(rmessage);

        //                }
        //                catch (Exception ex)
        //                {
        //                    Console.WriteLine($"Invalide message format. {ex.Message}");

        //                }

        //            } while (!result.EndOfMessage);
        //        }
        //    }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        //}


        private static ILog GetLogger()
        {
            try
            {
                return LogProvider.GetCurrentClassLogger();
            }
            catch (Exception e)
            {
                Trace.WriteLine($"[WEBSOCKET] Failed to initialize logger, disabling.. " +
                                $"Error: {e}");
                return LogProvider.NoOpLogger.Instance;
            }
        }

        private string L(string msg)
        {
            var name = Name ?? "CLIENT";
            return $"[WEBSOCKET {name}] {msg}";
        }
    }
}
