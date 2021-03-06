﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgileConfig.Server.Apisite.Filters;
using AgileConfig.Server.IService;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgileConfig.Server.Apisite.Websocket
{
    public class WebsocketHandlerMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;
        private readonly IWebsocketCollection _websocketCollection;

        public WebsocketHandlerMiddleware(
            RequestDelegate next,
            ILoggerFactory loggerFactory
            )
        {
            _next = next;
            _logger = loggerFactory.
                CreateLogger<WebsocketHandlerMiddleware>();
            _websocketCollection = WebsocketCollection.Instance;
        }

        public async Task Invoke(HttpContext context, IAppService appService, IConfigService configService)
        {
            if (context.Request.Path == "/ws")
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var basicAuth = new BasicAuthenticationAttribute(appService);
                    if (!await basicAuth.Valid(context.Request))
                    {
                        await context.Response.WriteAsync("closed");
                        return;
                    }
                    var appId = context.Request.Headers["appid"];
                    WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    var client = new WebsocketClient()
                    {
                        Client = webSocket,
                        Id = Guid.NewGuid().ToString(),
                        AppId = appId
                    };
                    _websocketCollection.AddClient(client);
                    _logger.LogInformation("Websocket client {0} Added ", client.Id);
                    try
                    {
                        await Handle(context, client, configService);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Handle websocket client {0} err .", client.Id);
                        await _websocketCollection.RemoveClient(client, WebSocketCloseStatus.Empty, ex.Message);
                        await context.Response.WriteAsync("closed");
                    }
                }
                else
                {
                    context.Response.StatusCode = 400;
                }
            }
            else
            {
                await _next(context);
            }
        }

        private async Task Handle(HttpContext context, WebsocketClient webSocket, IConfigService configService)
        {
            var buffer = new byte[1024 * 2];
            WebSocketReceiveResult result = null;
            do
            {
                result = await webSocket.Client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                webSocket.LastHeartbeatTime = DateTime.Now;
                var message = await ConvertWebsocketMessage(result, buffer);
                if (message == "ping")
                {
                    //如果是ping，回复本地数据的md5版本
                    var appId = context.Request.Headers["appid"];
                    var md5 = await configService.AppPublishedConfigsMd5Cache(appId);
                    var md5Data = Encoding.UTF8.GetBytes($"V:{md5}");

                    await webSocket.Client.SendAsync(new ArraySegment<byte>(md5Data, 0, md5Data.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                else 
                {
                    //如果不是心跳消息，回复0
                    var zeroData = Encoding.UTF8.GetBytes("0");
                    await webSocket.Client.SendAsync(new ArraySegment<byte>(zeroData, 0, zeroData.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            while (!result.CloseStatus.HasValue);
            _logger.LogInformation($"Websocket close , closeStatus:{result.CloseStatus} closeDesc:{result.CloseStatusDescription}");
            await _websocketCollection.RemoveClient(webSocket, result.CloseStatus, result.CloseStatusDescription);
        }

        private async Task<string> ConvertWebsocketMessage(WebSocketReceiveResult result, ArraySegment<Byte> buffer)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(buffer.Array, buffer.Offset, result.Count);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    using (var reader = new StreamReader(ms, Encoding.UTF8))
                    {
                        return await reader.ReadToEndAsync();
                    }
                }

                return "";
            }
        }
    }
}