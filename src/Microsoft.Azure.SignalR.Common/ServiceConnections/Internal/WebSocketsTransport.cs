// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure.SignalR.Common;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.SignalR.Connections.Client.Internal
{
    /// <summary>
    /// Copied from aspnetcore repo, TODO: refactor
    /// </summary>
    internal partial class WebSocketsTransport : IDuplexPipe
    {
        public static PipeOptions DefaultOptions = new PipeOptions(writerScheduler: PipeScheduler.ThreadPool, readerScheduler: PipeScheduler.ThreadPool, useSynchronizationContext: false, pauseWriterThreshold: 0, resumeWriterThreshold: 0);

        private readonly WebSocketMessageType _webSocketMessageType = WebSocketMessageType.Binary;
        private readonly ClientWebSocket _webSocket;
        private readonly Func<Task<string>> _accessTokenProvider;
        private IDuplexPipe _application;
        private readonly ILogger _logger;
        private readonly TimeSpan _closeTimeout;
        private volatile bool _aborted;

        private IDuplexPipe _transport;

        internal Task Running { get; private set; } = Task.CompletedTask;

        public PipeReader Input => _transport.Input;

        public PipeWriter Output => _transport.Output;

        public WebSocketsTransport(WebSocketConnectionOptions connectionOptions, ILoggerFactory loggerFactory, Func<Task<string>> accessTokenProvider)
        {
            _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<WebSocketsTransport>();
            _webSocket = new ClientWebSocket();

            // Issue in ClientWebSocket prevents user-agent being set - https://github.com/dotnet/corefx/issues/26627
            //_webSocket.Options.SetRequestHeader("User-Agent", Constants.UserAgentHeader.ToString());

            if (connectionOptions != null)
            {
                if (connectionOptions.Headers != null)
                {
                    foreach (var header in connectionOptions.Headers)
                    {
                        _webSocket.Options.SetRequestHeader(header.Key, header.Value);
                    }
                }

                if (connectionOptions.Cookies != null)
                {
                    _webSocket.Options.Cookies = connectionOptions.Cookies;
                }

                if (connectionOptions.ClientCertificates != null)
                {
                    _webSocket.Options.ClientCertificates.AddRange(connectionOptions.ClientCertificates);
                }

                if (connectionOptions.Credentials != null)
                {
                    _webSocket.Options.Credentials = connectionOptions.Credentials;
                }

                if (connectionOptions.Proxy != null)
                {
                    _webSocket.Options.Proxy = connectionOptions.Proxy;
                }

                if (connectionOptions.UseDefaultCredentials != null)
                {
                    _webSocket.Options.UseDefaultCredentials = connectionOptions.UseDefaultCredentials.Value;
                }

                connectionOptions.WebSocketConfiguration?.Invoke(_webSocket.Options);

                _closeTimeout = connectionOptions.CloseTimeout;
            }

            // Set this header so the server auth middleware will set an Unauthorized instead of Redirect status code
            // See: https://github.com/aspnet/Security/blob/ff9f145a8e89c9756ea12ff10c6d47f2f7eb345f/src/Microsoft.AspNetCore.Authentication.Cookies/Events/CookieAuthenticationEvents.cs#L42
            _webSocket.Options.SetRequestHeader("X-Requested-With", "XMLHttpRequest");

            // Ignore the HttpConnectionOptions access token provider. We were given an updated delegate from the HttpConnection.
            _accessTokenProvider = accessTokenProvider;
        }

        public async Task StartAsync(Uri url, CancellationToken cancellationToken = default)
        {
            if (url == null)
            {
                throw new ArgumentNullException(nameof(url));
            }

            var resolvedUrl = ResolveWebSocketsUrl(url);

            // We don't need to capture to a local because we never change this delegate.
            if (_accessTokenProvider != null)
            {
                var accessToken = await _accessTokenProvider();
                if (!string.IsNullOrEmpty(accessToken))
                {
                    _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
                }
            }

            Log.StartTransport(_logger, _webSocketMessageType, resolvedUrl);

            try
            {
                await _webSocket.ConnectAsync(resolvedUrl, cancellationToken);
            }
            catch (Exception e)
            {
                _webSocket.Dispose();
                throw e.WrapAsAzureSignalRException();
            }

            Log.StartedTransport(_logger);

            // Create the pipe pair (Application's writer is connected to Transport's reader, and vice versa)
            var options = DefaultOptions;
            var pair = DuplexPipe.CreateConnectionPair(options, options);

            _transport = pair.Transport;
            _application = pair.Application;

            // TODO: Handle TCP connection errors
            // https://github.com/SignalR/SignalR/blob/1fba14fa3437e24c204dfaf8a18db3fce8acad3c/src/Microsoft.AspNet.SignalR.Core/Owin/WebSockets/WebSocketHandler.cs#L248-L251
            Running = ProcessSocketAsync(_webSocket);
        }

        private async Task ProcessSocketAsync(WebSocket socket)
        {
            using (socket)
            {
                // Begin sending and receiving.
                var receiving = StartReceiving(socket);
                var sending = StartSending(socket);

                // Wait for send or receive to complete
                var trigger = await Task.WhenAny(receiving, sending);

                if (trigger == receiving)
                {
                    // We're waiting for the application to finish and there are 2 things it could be doing
                    // 1. Waiting for application data
                    // 2. Waiting for a websocket send to complete

                    // Cancel the application so that ReadAsync yields
                    _application.Input.CancelPendingRead();

                    using (var delayCts = new CancellationTokenSource())
                    {
                        var resultTask = await Task.WhenAny(sending, Task.Delay(_closeTimeout, delayCts.Token));

                        if (resultTask != sending)
                        {
                            _aborted = true;

                            // Abort the websocket if we're stuck in a pending send to the client
                            socket.Abort();
                        }
                        else
                        {
                            // Cancel the timeout
                            delayCts.Cancel();
                        }
                    }
                }
                else
                {
                    // We're waiting on the websocket to close and there are 2 things it could be doing
                    // 1. Waiting for websocket data
                    // 2. Waiting on a flush to complete (backpressure being applied)

                    _aborted = true;

                    // Abort the websocket if we're stuck in a pending receive from the client
                    socket.Abort();

                    // Cancel any pending flush so that we can quit
                    _application.Output.CancelPendingFlush();
                }
            }
        }

        private async Task StartReceiving(WebSocket socket)
        {
            try
            {
                while (true)
                {
#if !NETSTANDARD2_0
                    // Do a 0 byte read so that idle connections don't allocate a buffer when waiting for a read
                    var result = await socket.ReceiveAsync(Memory<byte>.Empty, CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log.WebSocketClosed(_logger, _webSocket.CloseStatus);

                        if (_webSocket.CloseStatus != WebSocketCloseStatus.NormalClosure)
                        {
                            throw new InvalidOperationException($"Websocket closed with error: {_webSocket.CloseStatus}.");
                        }

                        return;
                    }
#endif
                    var memory = _application.Output.GetMemory(2048);
#if !NETSTANDARD2_0
                    // Because we checked the CloseStatus from the 0 byte read above, we don't need to check again after reading
                    var receiveResult = await socket.ReceiveAsync(memory, CancellationToken.None);
#else
                    var isArray = MemoryMarshal.TryGetArray<byte>(memory, out var arraySegment);
                    Debug.Assert(isArray);

                    // Exceptions are handled above where the send and receive tasks are being run.
                    var receiveResult = await socket.ReceiveAsync(arraySegment, CancellationToken.None);
#endif
                    // Need to check again for netstandard2.1 because a close can happen between a 0-byte read and the actual read
                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        Log.WebSocketClosed(_logger, _webSocket.CloseStatus);

                        if (_webSocket.CloseStatus != WebSocketCloseStatus.NormalClosure)
                        {
                            throw new InvalidOperationException($"Websocket closed with error: {_webSocket.CloseStatus}.");
                        }

                        return;
                    }

                    Log.MessageReceived(_logger, receiveResult.MessageType, receiveResult.Count, receiveResult.EndOfMessage);

                    _application.Output.Advance(receiveResult.Count);

                    var flushResult = await _application.Output.FlushAsync();

                    // We canceled in the middle of applying back pressure
                    // or if the consumer is done
                    if (flushResult.IsCanceled || flushResult.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log.ReceiveCanceled(_logger);
            }
            catch (Exception ex)
            {
                if (!_aborted)
                {
                    _application.Output.Complete(ex);
                }
            }
            finally
            {
                // We're done writing
                _application.Output.Complete();

                Log.ReceiveStopped(_logger);
            }
        }

        private async Task StartSending(WebSocket socket)
        {
            Exception error = null;

            try
            {
                while (true)
                {
                    var result = await _application.Input.ReadAsync();
                    var buffer = result.Buffer;

                    // Get a frame from the application

                    try
                    {
                        if (result.IsCanceled)
                        {
                            break;
                        }

                        if (!buffer.IsEmpty)
                        {
                            try
                            {
                                Log.ReceivedFromApp(_logger, buffer.Length);

                                if (WebSocketCanSend(socket))
                                {
                                    await socket.SendAsync(buffer, _webSocketMessageType);
                                }
                                else
                                {
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (!_aborted)
                                {
                                    Log.ErrorSendingMessage(_logger, ex);
                                }
                                break;
                            }
                        }
                        else if (result.IsCompleted)
                        {
                            break;
                        }
                    }
                    finally
                    {
                        _application.Input.AdvanceTo(buffer.End);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                if (WebSocketCanSend(socket))
                {
                    try
                    {
                        // We're done sending, send the close frame to the client if the websocket is still open
                        await socket.CloseOutputAsync(error != null ? WebSocketCloseStatus.InternalServerError : WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Log.ClosingWebSocketFailed(_logger, ex);
                    }
                }

                _application.Input.Complete();

                Log.SendStopped(_logger);
            }
        }

        private static bool WebSocketCanSend(WebSocket ws)
        {
            return !(ws.State == WebSocketState.Aborted ||
                   ws.State == WebSocketState.Closed ||
                   ws.State == WebSocketState.CloseSent);
        }

        private static Uri ResolveWebSocketsUrl(Uri url)
        {
            var uriBuilder = new UriBuilder(url);
            if (url.Scheme == "http")
            {
                uriBuilder.Scheme = "ws";
            }
            else if (url.Scheme == "https")
            {
                uriBuilder.Scheme = "wss";
            }

            return uriBuilder.Uri;
        }

        public async Task StopAsync()
        {
            Log.TransportStopping(_logger);

            if (_application == null)
            {
                // We never started
                return;
            }

            _transport.Output.Complete();
            _transport.Input.Complete();

            // Cancel any pending reads from the application, this should start the entire shutdown process
            _application.Input.CancelPendingRead();

            try
            {
                await Running;
            }
            catch (Exception ex)
            {
                Log.TransportStopped(_logger, ex);
                // exceptions have been handled in the Running task continuation by closing the channel with the exception
                return;
            }
            finally
            {
                _webSocket.Dispose();
            }

            Log.TransportStopped(_logger, null);
        }
    }
}
