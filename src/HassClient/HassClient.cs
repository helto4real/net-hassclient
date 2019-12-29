using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;


[assembly: InternalsVisibleTo("HassClientIntegrationTests")]
[assembly: InternalsVisibleTo("HassClient.Performance.Tests")]
[assembly: InternalsVisibleTo("HassClient.Unit.Tests")]

namespace JoySoftware.HomeAssistant.Client
{

    /// <summary>
    /// The interface for ws client
    /// </summary>
    public interface IHassClient
    {
        /// <summary>
        /// The current states of the entities. 
        /// </summary>
        /// <remarks>Can be fully loaded when connecting by setting getStatesOnConnect=true</remarks>
        ConcurrentDictionary<string, HassState> States { get; }

        /// <summary>
        /// Connect to Home Assistant
        /// </summary>
        /// <param name="host">The host or ip address of Home Assistant</param>
        /// <param name="port">The port of Home Assistant, typically 8123 or 80</param>
        /// <param name="ssl">Set to true if Home Assistant using ssl (recommended secure setup for Home Assistant)</param>
        /// <param name="token">Authtoken from Home Assistant for access</param>
        /// <param name="getStatesOnConnect">Reads all states initially, this is the default behaviour</param>
        /// <param name="subscribeEvents">Subscribes to all eventchanges, this is the default behaviour</param>
        /// <returns>Returns true if successfully connected</returns>
        Task<bool> ConnectAsync(string host, short port, bool ssl, string token, bool getStatesOnConnect, bool subscribeEvents);

        /// <summary>
        /// Connect to Home Assistant
        /// </summary>
        /// <param name="url">The uri of the websocket, typically ws://ip:8123/api/websocket</param>
        /// <param name="token">Authtoken from Home Assistant for access</param>
        /// <param name="getStatesOnConnect">Reads all states initially, this is the default behaviour</param>
        /// <param name="subscribeEvents">Subscribes to all eventchanges, this is the default behaviour</param>
        /// <returns>Returns true if successfully connected</returns>
        Task<bool> ConnectAsync(Uri url, string token, bool getStatesOnConnect, bool subscribeEvents);

        /// <summary>
        /// Gets the configuration of the connected Home Assistant instance
        /// </summary>
        Task<HassConfig> GetConfig();

        /// <summary>
        /// Returns next incoming event and completes async operation
        /// </summary>
        /// <remarks>Set subscribeEvents=true on ConnectAsync to use.</remarks>
        /// <exception>OperationCanceledException if the operation is canceled.</exception>
        /// <returns>Returns next event</returns>
        Task<HassEvent> ReadEventAsync();

        /// <summary>
        /// Calls a service to home assistant
        /// </summary>
        /// <param name="domain">The domain for the servie, example "light"</param>
        /// <param name="service">The service to call, example "turn_on"</param>
        /// <param name="serviceData">The service data, use anonumous types, se example</param>
        /// <example>
        /// Folowing example turn on light 
        /// <code>
        /// var client = new HassClient();
        /// await client.ConnectAsync("192.168.1.2", 8123, false);
        /// await client.CallService("light", "turn_on", new {entity_id="light.myawesomelight"});
        /// await client.CloseAsync();
        /// </code>
        /// </example>
        /// <returns>True if successfully called service</returns>
        Task<bool> CallService(string domain, string service, object serviceData);

        /// <summary>
        /// Pings Home Assistant to check if connection is alive
        /// </summary>
        /// <param name="timeout">The timeout to wait for Home Assistant to return pong message</param>
        /// <returns>True if connection is alive.</returns>
        Task<bool> PingAsync(int timeout);

        /// <summary>
        /// Gracefully closes the connection to Home Assistant
        /// </summary>
        Task CloseAsync();

    }

    /// <summary>
    /// Hides the internals of websocket connection
    /// to connect, send and receive json messages
    /// This class is threadsafe
    /// </summary>
    public class HassClient : IHassClient, IDisposable
    {
        /// <summary>
        /// The max time we will wait for the socket to gracefully close
        /// </summary>
        private static readonly int _MAX_WAITTIME_SOCKET_CLOSE = 5000; // 5 seconds

        /// <summary>
        /// Default size for channel
        /// </summary>
        private static readonly int _DEFAULT_CHANNEL_SIZE = 200;

        /// <summary>
        /// Default read buffer size for websockets
        /// </summary>
        private static readonly int _DEFAULT_RECIEIVE_BUFFER_SIZE = 1024 * 4;


        /// <summary>
        /// The default timeout for websockets 
        /// </summary>
        private static readonly int _DEFAULT_TIMEOUT = 5000; // 5 seconds

        /// <summary>
        /// Default Json serialization options, Hass expects intended
        /// </summary>
        private readonly JsonSerializerOptions defaultSerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            IgnoreNullValues = true
        };

        /// <summary>
        /// The underlying currently connected socket or null if not connected
        /// </summary>
        private IClientWebSocket? _ws = null;

        private readonly IClientWebSocketFactory? _wsFactory = null;

        /// <summary>
        /// Used to cancel all asyncronus work
        /// </summary>
        private CancellationTokenSource _cancelSource = new CancellationTokenSource();

        /// <summary>
        /// Async task to read all incoming messages
        /// </summary>
        private Task? _readMessagePumpTask = null;

        /// <summary>
        /// Async task to write messages
        /// </summary>
        private Task? _writeMessagePumpTask = null;

        /// <summary>
        /// Channel used as a async thread safe way to wite messages to the websocket
        /// </summary>
        private readonly Channel<HassMessageBase> _writeChannel = Channel.CreateBounded<HassMessageBase>(_DEFAULT_CHANNEL_SIZE);

        /// <summary>
        /// Channel used as a async thread safe way to read resultmessages from the websocket
        /// </summary>
        private Channel<HassMessage> _messageChannel = Channel.CreateBounded<HassMessage>(_DEFAULT_CHANNEL_SIZE);

        /// <summary>
        /// Channel used as a async thread safe way to read messages from the websocket
        /// </summary>
        private Channel<HassEvent> _eventChannel = Channel.CreateBounded<HassEvent>(_DEFAULT_CHANNEL_SIZE);

        /// <summary>
        /// The logger to use
        /// </summary>
        private readonly ILogger? _logger = null;

        /// <summary>
        /// Message id sent in command messages
        /// </summary>
        /// <remarks>Message id need to be increased everytime it sends an command</remarks>
        private int _messageId = 1;

        /// <summary>
        /// Thread safe dicitionary that holds information about all command and command id:s
        /// Is used to correclty deserialize the result messages from commands.
        /// </summary>
        /// <typeparam name="int">The message id sen in command message</typeparam>
        /// <typeparam name="string">The message type</typeparam>
        private ConcurrentDictionary<int, string> _commandsSent = new ConcurrentDictionary<int, string>(32, 200);        /// <summary>

        /// <summary>
        /// The current states of the entities.
        /// </summary>
        public ConcurrentDictionary<string, HassState> States { get; } = new ConcurrentDictionary<string, HassState>(Environment.ProcessorCount * 2, _DEFAULT_CHANNEL_SIZE);

        /// <summary>
        /// Internal property for tests to access the timeout during unit testing
        /// </summary>
        internal int SocketTimeout { get; set; } = _DEFAULT_TIMEOUT;

        /// <summary>
        /// Instance a new HassClient
        /// </summary>
        /// <param name="logFactory">The LogFactory to use for logging, null uses default values from config.</param>
        /// <param name="wsFactory">The factory to use for websockets, mainly for testing purposes</param>
        internal HassClient(ILoggerFactory? logFactory = null, IClientWebSocketFactory? wsFactory = null)
        {
            logFactory ??= _getDefaultLoggerFactory;
            wsFactory ??= new ClientWebSocketFactory(); ;

            _logger = logFactory.CreateLogger<HassClient>();
            _wsFactory = wsFactory;
        }

        /// <summary>
        /// Instance a new HassClient
        /// </summary>
        public HassClient()
        {
            _logger = _getDefaultLoggerFactory.CreateLogger<HassClient>();
            _wsFactory = new ClientWebSocketFactory();
        }

        /// <summary>
        /// The default logger
        /// </summary>
        private static ILoggerFactory _getDefaultLoggerFactory => LoggerFactory.Create(builder =>
                                                                               {
                                                                                   builder
                                                                                       .ClearProviders()
                                                                                       .AddFilter("HassClient.HassClient", LogLevel.Information)
                                                                                       .AddConsole();
                                                                               });

        /// <summary>
        /// Returns next incoming event and completes async operation
        /// </summary>
        /// <remarks>Set subscribeEvents=true on ConnectAsync to use.</remarks>
        /// <exception>OperationCanceledException if the operation is canceled.</exception>
        /// <returns>Returns next event</returns>
        public async Task<HassEvent> ReadEventAsync() => await _eventChannel.Reader.ReadAsync(_cancelSource.Token);

        /// <summary>
        /// Send message and correctly handle message id counter
        /// </summary>
        /// <param name="message">The message to send</param>
        /// <returns>True if successful</returns>
        private bool sendMessage(HassMessageBase message)
        {
            _logger.LogTrace($"Sends message {message.Type}");
            if (message is CommandMessage commandMessage)
            {
                commandMessage.Id = ++_messageId;
                //We save the type of command so we can deserialize the correct message later
                _commandsSent[_messageId] = commandMessage.Type;
            }
            return _writeChannel.Writer.TryWrite(message);
        }

        /// <summary>
        /// Connect to Home Assistant
        /// </summary>
        /// <param name="host">The host or ip address of Home Assistant</param>
        /// <param name="port">The port of Home Assistant, typically 8123 or 80</param>
        /// <param name="ssl">Set to true if Home Assistant using ssl (recommended secure setup for Home Assistant)</param>
        /// <param name="token">Authtoken from Home Assistant for access</param>
        /// <param name="getStatesOnConnect">Reads all states initially, this is the default behaviour</param>
        /// <param name="subscribeEvents">Subscribes to all eventchanges, this is the default behaviour</param>
        /// <returns>Returns true if successfully connected</returns>
        public Task<bool> ConnectAsync(string host, short port, bool ssl, string token, bool getStatesOnConnect, bool subscribeEvents) =>
            ConnectAsync(new Uri($"{(ssl ? "ws" : "wss")}://{host}:{port}/api/websocket"), token, getStatesOnConnect, subscribeEvents);

        /// <summary>
        /// Connect to Home Assistant
        /// </summary>
        /// <param name="url">The uri of the websocket</param>
        /// <param name="token">Authtoken from Home Assistant for access</param>
        /// <param name="getStatesOnConnect">Reads all states initially, this is the default behaviour</param>
        /// <param name="subscribeToEvents">Subscribes to all eventchanges, this is the default behaviour</param>
        /// <returns>Returns true if successfully connected</returns>
        public async Task<bool> ConnectAsync(Uri url, string token,
            bool getStatesOnConnect = true, bool subscribeToEvents = true)
        {
            if (url == null)
            {
                throw new ArgumentNullException(nameof(url), "Expected url to be provided");
            }

            // Check if we already have a websocket running
            if (_ws != null)
            {
                throw new InvalidOperationException("Allready connected to the remote websocket.");
            }

            try
            {
                IClientWebSocket ws = _wsFactory?.New()!;
                using var timerTokenSource = new CancellationTokenSource(SocketTimeout);
                // Make a combined token source with timer and the general cancel token source
                // The operations will cancel from ether one
                using var connectTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    timerTokenSource.Token, _cancelSource.Token);

                await ws.ConnectAsync(url, connectTokenSource.Token);

                if (ws.State == WebSocketState.Open)
                {
                    // Initialize the correct states when successfully connecting to the websocket
                    initStatesOnConnect(ws);

                    // Do the authenticate and get the auhtorization response
                    HassMessage result = await handleConnectAndAuthenticate(token, connectTokenSource);

                    switch (result.Type)
                    {
                        case "auth_ok":
                            if (getStatesOnConnect)
                            {
                                await GetStates(connectTokenSource);

                            }
                            if (subscribeToEvents)
                            {
                                await this.SubscribeToEvents(connectTokenSource);

                            }

                            _logger.LogTrace($"Connected to websocket ({url})");
                            return true;

                        case "auth_invalid":
                            _logger.LogError($"Failed to athenticate ({result.Message})");
                            await DoNormalClosureOfWebSocket();
                            return false;

                        default:
                            _logger.LogError($"Unexpected response ({result.Type})");
                            return false;
                    }

                }
                _logger.LogDebug($"Failed to connect to websocket socket state: {ws.State}");
                return false;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to connect to Home Assistant on {url}");
                _logger.LogDebug(e, $"Failed to connect to Home Assistant on {url}");
            }

            return false;
        }

        private async ValueTask<HassMessage> sendCommandAndWaitForResponse(CommandMessage message)
        {
            using var timerTokenSource = new CancellationTokenSource(SocketTimeout);
            // Make a combined token source with timer and the general cancel token source
            // The operations will cancel from ether one
            using var sendCommandTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                timerTokenSource.Token, _cancelSource.Token);

            try
            {
                sendMessage(message);
                while (true)
                {
                    HassMessage result = await _messageChannel.Reader.ReadAsync(sendCommandTokenSource.Token);
                    if (result.Id == message.Id)
                    {
                        return result;
                    }
                    else
                    {
                        // Not the response, push message back
                        bool res = _messageChannel.Writer.TryWrite(result);

                        if (!res)
                        {
                            throw new Exception("What the fuuuuuck!");
                        }
                        // Delay for a short period to let the message arrive we are searching for
                        await Task.Delay(10);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogError($"Fail to send command {message.Type} and receive correct command within timeout. ");
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError($"Fail to send command {message.Type}. ");
                _logger.LogDebug(e, $"Fail to send command.");
                throw e;
            }

        }

        /// <summary>
        /// Gets the configuration of the connected Home Assistant instance
        /// </summary>
        public async Task<HassConfig> GetConfig()
        {
            HassMessage hassResult = await sendCommandAndWaitForResponse(new GetConfigCommand());

            object resultMessage = hassResult.Result ?? throw new NullReferenceException("Result cant be null!");
            var result = resultMessage as HassConfig;
            if (result != null)
            {
                return result as HassConfig;
            }
            else
            {
                throw new Exception($"The result not expected! {resultMessage}");
            }
        }

        /// <summary>
        /// Calls a service to home assistant
        /// </summary>
        /// <param name="domain">The domain for the servie, example "light"</param>
        /// <param name="service">The service to call, example "turn_on"</param>
        /// <param name="serviceData">The service data, use anonumous types, se example</param>
        /// <example>
        /// Folowing example turn on light 
        /// <code>
        /// var client = new HassClient();
        /// await client.ConnectAsync("192.168.1.2", 8123, false);
        /// await client.CallService("light", "turn_on", new {entity_id="light.myawesomelight"});
        /// await client.CloseAsync();
        /// </code>
        /// </example>
        /// <returns>True if successfully called service</returns>
        public async Task<bool> CallService(string domain, string service, object serviceData)
        {
            try
            {
                HassMessage result = await sendCommandAndWaitForResponse(new CallServiceCommand()
                {
                    Domain = domain,
                    Service = service,
                    ServiceData = serviceData
                });
                return result.Success ?? false;


            }
            catch (OperationCanceledException)
            {
                if (_cancelSource.IsCancellationRequested)
                {
                    throw;
                }
                else
                {
                    return false; // Just timeout not canceled 
                }
            }
            catch
            {
                // We already logged in sendCommand 
                return false;
            }

        }


        /// <summary>
        /// Pings Home Assistant to check if connection is alive
        /// </summary>
        /// <param name="timeout">The timeout to wait for Home Assistant to return pong message</param>
        /// <returns>True if connection is alive.</returns>
        public async Task<bool> PingAsync(int timeout)
        {
            using var timerTokenSource = new CancellationTokenSource(SocketTimeout);
            // Make a combined token source with timer and the general cancel token source
            // The operations will cancel from ether one
            using var pingTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                timerTokenSource.Token, _cancelSource.Token);

            try
            {
                sendMessage(new HassPingCommand());
                HassMessage result = await _messageChannel.Reader.ReadAsync(pingTokenSource.Token);
                if (result.Type == "pong")
                {
                    return true;
                }

            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception e)
            {
                _logger.LogError($"Fail to ping Home Assistant");
                _logger.LogDebug(e, $"Fail to ping Home Assistant");
            }
            return false;
        }

        private async Task SubscribeToEvents(CancellationTokenSource connectTokenSource)
        {
            sendMessage(new SubscribeEventCommand { });
            HassMessage result = await _messageChannel.Reader.ReadAsync(connectTokenSource.Token);
            if (result.Type != "result" && result.Success != true)
            {
                _logger.LogError($"Unexpected response from subscribe events ({result.Type}, {result.Success})");

            }
        }

        private async Task GetStates(CancellationTokenSource connectTokenSource)
        {
            sendMessage(new GetStatesCommand { });
            HassMessage result = await _messageChannel.Reader.ReadAsync(connectTokenSource.Token);
            var wsResult = result?.Result as List<HassState>;
            if (wsResult != null)
            {
                foreach (HassState state in wsResult)
                {
                    States[state.EntityId] = state;
                }
            }
        }

        private async Task<HassMessage> handleConnectAndAuthenticate(string token, CancellationTokenSource connectTokenSource)
        {
            HassMessage result = await _messageChannel.Reader.ReadAsync(connectTokenSource.Token);
            if (result.Type == "auth_required")
            {
                sendMessage(new HassAuthMessage { AccessToken = token });
                result = await _messageChannel.Reader.ReadAsync(connectTokenSource.Token);
            }

            return result;
        }

        private void initStatesOnConnect(IClientWebSocket ws)
        {
            _ws = ws;
            _messageId = 1;

            _isClosing = false;

            // Make sure we have new channels so we are not have old messages
            _messageChannel = Channel.CreateBounded<HassMessage>(HassClient._DEFAULT_CHANNEL_SIZE);
            _eventChannel = Channel.CreateBounded<HassEvent>(HassClient._DEFAULT_CHANNEL_SIZE);

            _cancelSource = new CancellationTokenSource();
            _readMessagePumpTask = Task.Run(ReadMessagePump);
            _writeMessagePumpTask = Task.Run(WriteMessagePump);
        }

        /// <summary>
        /// Indicates if we are in the process of closing the socket and cleaning up resources
        /// Avoids recursive states
        /// </summary>
        private bool _isClosing = false;

        /// <summary>
        /// Close the websocket gracefully
        /// </summary>
        /// <remarks>
        /// The code waits for the server to return closed state.
        ///
        /// There was problems using the CloseAsync only. It did not properly work as expected
        /// The code is using CloseOutputAsync instead and wait for status closed
        /// </remarks>
        /// <returns></returns>
        private async Task DoNormalClosureOfWebSocket()
        {
            _logger.LogTrace($"Do normal close of websocket");

            var timeout = new CancellationTokenSource(HassClient._MAX_WAITTIME_SOCKET_CLOSE);

            if (_ws != null &&
                (_ws.State == WebSocketState.CloseReceived ||
                _ws.State == WebSocketState.Open))
            {

                try
                {
                    // Send close message (some bug n CloseAsync makes we have to do it this way)
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", timeout.Token);
                    // Wait for readpump finishing when receiving the close message
                }
                catch (OperationCanceledException)
                {
                    // normal upon task/token cancellation, disregard
                    _logger.LogTrace($"Close operations took more than 5 seconds.. closing hard!");
                }
            }
            _readMessagePumpTask?.Wait(timeout.Token);
        }
        /// <summary>
        /// Closes the websocket
        /// </summary>
        public async Task CloseAsync()
        {
            lock (this)
            {
                if (_isClosing)
                {
                    return;
                }
            }

            _logger.LogTrace($"Async close websocket");

            // First do websocket close management
            await DoNormalClosureOfWebSocket();
            // Cancel all async stuff
            _cancelSource.Cancel();

            // Wait for read and write tasks to complete max 5 seconds
            if (_readMessagePumpTask != null && _writeMessagePumpTask != null)
            {
                Task.WaitAll(new Task[] { _readMessagePumpTask, _writeMessagePumpTask },
                    HassClient._MAX_WAITTIME_SOCKET_CLOSE, CancellationToken.None);
            }

            _ws?.Dispose();
            _ws = null;

            _isClosing = false;
            _cancelSource = new CancellationTokenSource();

            _logger.LogTrace($"Async close websocket done");
        }

        /// <summary>
        /// Dispose the WSCLient
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            // This object will be cleaned up by the Dispose method.
            GC.SuppressFinalize(this);
        }

        private bool disposed = false;
        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                // If disposing equals true, dispose all managed
                // and unmanaged resources.
                //
                if (disposing)
                {
                    _ws?.Dispose();
                    _cancelSource.Dispose();
                }
                disposed = true;
            }
        }

        /// <summary>
        /// A pump that reads incoming messages and put them on the read channel.
        /// </summary>
        private async void ReadMessagePump()
        {
            _logger?.LogTrace($"Start ReadMessagePump");

            if (_ws == null)
            {
                throw new MissingMemberException("_ws is null!");
            }

            var pipe = new Pipe();

            // While not canceled and websocket is not closed
            while (!_cancelSource.IsCancellationRequested && !_ws.CloseStatus.HasValue)
            {
                try
                {

                    await ProcessNextMessage();
                }
                catch (System.OperationCanceledException)
                {
                    // Canceled the thread just leave
                    break;
                }
                catch (Exception e)
                {
                    _logger?.LogError(e, "Major failure in readpump, exit...");
                }

            }
            _logger?.LogTrace($"Exit ReadMessagePump");
        }

        /// <summary>
        /// Process next message from Home Assistant
        /// </summary>
        /// <remarks>
        /// Uses Pipes to allocate memory where the websocket writes to and 
        /// Write the read message to a channel.
        /// </remarks>
        /// <returns></returns>
        private async Task ProcessNextMessage()
        {
            var pipe = new Pipe();

            await Task.WhenAll(
                Task.Run(ReadFromClientSocket),
                Task.Run(WriteMessagesToChannel)
                );

            // Task that reads the next message from websocket
            async Task ReadFromClientSocket()
            {
                if (_ws == null)
                {
                    return;
                }

                while (!_cancelSource.Token.IsCancellationRequested && !_ws.CloseStatus.HasValue)
                {

                    Memory<byte> memory = pipe.Writer.GetMemory(HassClient._DEFAULT_RECIEIVE_BUFFER_SIZE);

                    ValueWebSocketReceiveResult result = await _ws.ReceiveAsync(memory, _cancelSource.Token);

                    if (result.MessageType == WebSocketMessageType.Close || result.Count == 0)
                    {
                        await CloseAsync();
                        // Remote disconnected just leave the readpump
                        return;
                    }
                    // Advance writer to the read ne of bytes
                    pipe.Writer.Advance(result.Count);

                    await pipe.Writer.FlushAsync();

                    if (result.EndOfMessage)
                    {
                        // We have successfully read the whole message, make available to reader
                        await pipe.Writer.CompleteAsync();
                        return;
                    }
                }
            }
            // Task that deserializes the message and write the finnished message to a channel
            async Task WriteMessagesToChannel()
            {
                try
                {


                    HassMessage m = await JsonSerializer.DeserializeAsync<HassMessage>(pipe.Reader.AsStream());
                    await pipe.Reader.CompleteAsync().ConfigureAwait(false);
                    switch (m.Type)
                    {
                        case "event":
                            if (m.Event != null)
                            {
                                _eventChannel.Writer.TryWrite(m.Event);
                            }

                            break;
                        case "auth_required":
                        case "auth_ok":
                        case "auth_invalid":
                        case "call_service":
                        case "get_config":
                        case "pong":
                            _messageChannel.Writer.TryWrite(m);
                            break;
                        case "result":

                            _messageChannel.Writer.TryWrite(getResultMessage(m));
                            break;
                        default:
                            _logger.LogDebug($"Unexpected eventtype {m.Type}, discarding message!");
                            break;
                    }
                    return;

                }
                catch (System.Exception e)
                {
                    // Todo: Log the seralizer error here later but continue receive
                    // messages from the server. Then we can survive the server
                    // Sending bad json messages
                    _logger?.LogDebug(e, "Error deserialize json response");
                    // Make sure we put a small delay incase we have severe error so the loop
                    // doesnt kill the server
                    await Task.Delay(20);
                }
            }
        }

        /// <summary>
        /// Get the correct result message from HassMessage
        /// </summary>
        private HassMessage getResultMessage(HassMessage m)
        {
            if (m.Id > 0)
            {
                // It is an command response, get command
                if (_commandsSent.Remove(m.Id, out string? command))
                {
                    switch (command)
                    {
                        case "get_states":
                            m.Result = m.ResultElement?.ToObject<List<HassState>>();
                            break;

                        case "get_config":
                            m.Result = m.ResultElement?.ToObject<HassConfig>();
                            break;
                        case "subscribe_events":
                            break; // Do nothing
                        case "call_service":
                            break; // Do nothing
                        default:
                            _logger.LogError($"The result message {command} is not supported");
                            break;
                    }
                }
                else
                {
                    return m;
                }
            }

            return m;
        }

        private async void WriteMessagePump()
        {
            _logger?.LogTrace($"Start WriteMessagePump");
            if (_ws == null)
            {
                throw new MissingMemberException("client_ws is null!");
            }

            while (!_cancelSource.IsCancellationRequested && !_ws.CloseStatus.HasValue)
            {
                try
                {
                    HassMessageBase nextMessage = await _writeChannel.Reader.ReadAsync(_cancelSource.Token);
                    byte[] result = JsonSerializer.SerializeToUtf8Bytes(nextMessage, nextMessage.GetType(), defaultSerializerOptions);

                    await _ws.SendAsync(result, WebSocketMessageType.Text, true, _cancelSource.Token);
                }
                catch (System.OperationCanceledException)
                {
                    // Canceled the thread
                    break;
                }
                catch (System.Exception e)
                {
                    _logger?.LogWarning($"Exit WriteMessagePump");
                    await Task.Delay(20); // Incase we are looping add a delay
                    throw e;
                }

            }
            _logger?.LogTrace($"Exit WriteMessagePump");
        }

    }

}