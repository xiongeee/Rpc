﻿using Rabbit.Rpc.Exceptions;
using Rabbit.Rpc.Logging;
using Rabbit.Rpc.Messages;
using Rabbit.Rpc.Runtime.Server;
using Rabbit.Rpc.Serialization;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Rabbit.Rpc.Transport.Implementation
{
    /// <summary>
    /// 一个默认的传输客户端实现。
    /// </summary>
    public class TransportClient : ITransportClient, IDisposable
    {
        #region Field

        private readonly IMessageSender _messageSender;
        private readonly IMessageListener _messageListener;
        private readonly ILogger _logger;
        private readonly ISerializer<object> _objecSerializer;
        private readonly IServiceExecutor _serviceExecutor;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<TransportMessage>> _resultDictionary = new ConcurrentDictionary<string, TaskCompletionSource<TransportMessage>>();

        #endregion Field

        #region Constructor

        public TransportClient(IMessageSender messageSender, IMessageListener messageListener, ILogger logger, ISerializer<object> objecSerializer) : this(messageSender, messageListener, logger, objecSerializer, null)
        {
        }

        public TransportClient(IMessageSender messageSender, IMessageListener messageListener, ILogger logger, ISerializer<object> objecSerializer, IServiceExecutor serviceExecutor)
        {
            _messageSender = messageSender;
            _messageListener = messageListener;
            _logger = logger;
            _objecSerializer = objecSerializer;
            _serviceExecutor = serviceExecutor;
            messageListener.Received += MessageListener_Received;
        }

        #endregion Constructor

        #region Implementation of ITransportClient

        /// <summary>
        /// 发送消息。
        /// </summary>
        /// <param name="message">远程调用消息模型。</param>
        /// <returns>远程调用消息的传输消息。</returns>
        public async Task<TransportMessage> SendAsync(RemoteInvokeMessage message)
        {
            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug("准备发送消息。");

                var transportMessage = TransportMessage.CreateInvokeMessage(message);

                await _messageSender.SendAndFlushAsync(transportMessage);

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug("消息发送成功。");

                return transportMessage;
            }
            catch (Exception exception)
            {
                if (_logger.IsEnabled(LogLevel.Fatal))
                    _logger.Fatal("消息发送失败。", exception);
                throw;
            }
        }

        /// <summary>
        /// 接受指定消息id的响应消息。
        /// </summary>
        /// <param name="id">消息Id。</param>
        /// <returns>远程调用结果消息模型。</returns>
        public async Task<RemoteInvokeResultMessage> ReceiveAsync(string id)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.Debug($"准备获取Id为：{id}的响应内容。");

            TaskCompletionSource<TransportMessage> task;
            if (_resultDictionary.ContainsKey(id))
            {
                if (_resultDictionary.TryRemove(id, out task))
                {
                    await task.Task;
                }
            }
            else
            {
                task = new TaskCompletionSource<TransportMessage>();
                _resultDictionary.TryAdd(id, task);
                var result = await task.Task;
                return _objecSerializer.Deserialize<object, RemoteInvokeResultMessage>(result.Content);
            }
            return null;
        }

        #endregion Implementation of ITransportClient

        #region Implementation of IDisposable

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            (_messageSender as IDisposable)?.Dispose();
            (_messageListener as IDisposable)?.Dispose();

            foreach (var taskCompletionSource in _resultDictionary.Values)
            {
                taskCompletionSource.TrySetCanceled();
            }
        }

        #endregion Implementation of IDisposable

        #region Private Method

        private void MessageListener_Received(IMessageSender sender, TransportMessage message)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.Information("接收到消息。");

            TaskCompletionSource<TransportMessage> task;
            if (!_resultDictionary.TryGetValue(message.Id, out task))
                return;

            if (message.IsInvokeResultMessage())
            {
                var content = _objecSerializer.Deserialize<object, RemoteInvokeResultMessage>(message.Content);
                if (!string.IsNullOrEmpty(content.ExceptionMessage))
                {
                    task.TrySetException(new RpcRemoteException(content.ExceptionMessage));
                }
                else
                {
                    task.SetResult(message);
                }
            }
            if (message.IsInvokeMessage())
            {
                _serviceExecutor?.ExecuteAsync(sender, message);
            }
        }

        #endregion Private Method
    }
}