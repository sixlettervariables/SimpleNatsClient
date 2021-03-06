﻿using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SimpleNatsClient.Connection;
using SimpleNatsClient.Messages;
using Xunit;

namespace SimpleNatsClient.Tests
{
    public class NatsConnectionTests
    {
        private static readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(500);
        private static readonly (string Hostname, int Port)[] _servers = {("localhost", 4222)};

        private static TcpConnectionProvider GetProvider(ITcpConnection tcpConnection = null)
        {
            return (h, p) => Task.FromResult(tcpConnection ?? new MockTcpConnection());
        }

        [Fact(DisplayName = "should send connect message after recieving info message")]
        public async Task ConnectAfterInfo()
        {
            var serverInfo = new ServerInfo();
            var tcpConnection = new MockTcpConnection(serverInfo);

            var options = new NatsConnectionOptions();
            var cancellationToken = new CancellationTokenSource(_timeout).Token;
            using (var connection = await NatsConnection.Connect(_servers, GetProvider(tcpConnection), new NatsOptions(options), cancellationToken))
            {
                var wrote = await tcpConnection.OnWrite.Timeout(_timeout).FirstAsync();
                var connectionMessage = Encoding.UTF8.GetString(wrote);

                Assert.StartsWith("CONNECT ", connectionMessage);

                var sendOptions = JObject.Parse(connectionMessage.Substring(8));
                Assert.Equal(JObject.FromObject(options), sendOptions);

                Assert.Equal(JObject.FromObject(serverInfo), JObject.FromObject(connection.ServerInfo));

                Assert.False(tcpConnection.IsSsl, "should not use ssl tcp connection");

                Assert.Equal(NatsConnectionState.Connected, connection.ConnectionState);
            }

            Assert.True(tcpConnection.IsDisposed, "should dispose tcp connection");
        }

        [Fact(DisplayName = "should use ssl")]
        public async Task ConnectWithSsl()
        {
            var serverInfo = new ServerInfo {SslRequired = true};
            var tcpConnection = new MockTcpConnection(serverInfo);

            var options = new NatsConnectionOptions {SslRequired = true};
            var cancellationToken = new CancellationTokenSource(_timeout).Token;
            using (var connection = await NatsConnection.Connect(_servers, GetProvider(tcpConnection), new NatsOptions(options), cancellationToken))
            {
                var wrote = await tcpConnection.OnWrite.Timeout(_timeout).FirstAsync();
                var connectionMessage = Encoding.UTF8.GetString(wrote);

                Assert.StartsWith("CONNECT ", connectionMessage);

                var sendOptions = JObject.Parse(connectionMessage.Substring(8));
                Assert.Equal(JObject.FromObject(options), sendOptions);

                Assert.Equal(JObject.FromObject(serverInfo), JObject.FromObject(connection.ServerInfo));

                Assert.True(tcpConnection.IsSsl, "should use ssl tcp connection");
                Assert.Equal(NatsConnectionState.Connected, connection.ConnectionState);
            }

            Assert.True(tcpConnection.IsDisposed, "should dispose tcp connection");
        }

        [Fact(DisplayName = "should read messages from server")]
        public async Task ReadMessages()
        {
            const string subject = "some_subject";
            const string subscription = "some_subscription";
            const string replyTo = "reply_to";
            const string expectedMessage = "expected message\r\nwith new lines";
            var size = Encoding.UTF8.GetByteCount(expectedMessage);
            var tcpConnection = new MockTcpConnection();
            tcpConnection.Queue.Enqueue(
                Encoding.UTF8.GetBytes($"MSG {subject} {subscription} {replyTo} {size}\r\n{expectedMessage}\r\n"));

            using (var connection = new NatsConnection(_servers, GetProvider(tcpConnection), new NatsOptions()))
            {
                var messageTask = connection.Messages.OfType<Message<IncomingMessage>>()
                    .Timeout(_timeout)
                    .FirstAsync()
                    .ToTask();
                var cancellationToken = new CancellationTokenSource(_timeout).Token;
                await connection.Connect(cancellationToken);

                var message = await messageTask;
                var incomingMessage = message.Data;
                Assert.Equal(subject, incomingMessage.Subject);
                Assert.Equal(subscription, incomingMessage.SubscriptionId);
                Assert.Equal(replyTo, incomingMessage.ReplyTo);
                Assert.Equal(size, incomingMessage.Size);
                Assert.Equal(expectedMessage, Encoding.UTF8.GetString(incomingMessage.Payload));
            }

            Assert.True(tcpConnection.IsDisposed, "should dispose tcp connection");
        }

        [Fact(DisplayName = "should write to server")]
        public async Task WriteMessages()
        {
            var tcpConnection = new MockTcpConnection();
            var expectedMessage = Encoding.UTF8.GetBytes("some message");
            var cancellationToken = new CancellationTokenSource(_timeout).Token;
            using (var connection = await NatsConnection.Connect(_servers, GetProvider(tcpConnection), new NatsOptions(), cancellationToken))
            {
                await connection.Write(expectedMessage, CancellationToken.None);
                var wrote = await tcpConnection.OnWrite.Timeout(_timeout).Take(2).LastAsync();

                Assert.Equal(expectedMessage, wrote);
            }

            Assert.True(tcpConnection.IsDisposed, "should dispose tcp connection");
        }

        [Fact(DisplayName = "should reply to ping request")]
        public async Task PingPong()
        {
            var tcpConnection = new MockTcpConnection();
            tcpConnection.Queue.Enqueue(Encoding.UTF8.GetBytes("PING\r\n"));

            var cancellationToken = new CancellationTokenSource(_timeout).Token;
            using (await NatsConnection.Connect(_servers, GetProvider(tcpConnection), new NatsOptions(), cancellationToken))
            {
                var wrote = await tcpConnection.OnWrite.Timeout(_timeout).Take(2).LastAsync();
                var pongMessage = Encoding.UTF8.GetString(wrote);

                Assert.Equal("PONG\r\n", pongMessage);
            }

            Assert.True(tcpConnection.IsDisposed, "should dispose tcp connection");
        }

        [Fact(DisplayName = "should reconnect when ping times out")]
        public async Task Reconnect()
        {
            const int reconnectCount = 5;
            var options = new NatsOptions
            {
                PingTimeout = TimeSpan.FromMilliseconds(5),
                PingPongInterval = TimeSpan.FromMilliseconds(5),
            };
            var cancellationToken = new CancellationTokenSource(_timeout).Token;
            using (var connection = await NatsConnection.Connect(_servers, GetProvider(), options, cancellationToken))
            {
                var connectionCount = await connection.OnConnect
                    .Take(reconnectCount)
                    .ToArray()
                    .Timeout(_timeout);

                Assert.Equal(reconnectCount, connectionCount.Length);
            }
        }

        [Theory(DisplayName = "should retry when unable to connect")]
        [InlineData(5, 2)]
        [InlineData(2, 2)]
        public async Task Retry(int maxRetry, int retryCount)
        {
            var options = new NatsOptions
            {
                MaxConnectRetry = maxRetry,
                ConnectRetryDelay = TimeSpan.Zero,
            };

            var currentRetryCount = 0;

            Task<ITcpConnection> Provider(string h, int p)
            {
                try
                {
                    if (currentRetryCount == retryCount) return Task.FromResult<ITcpConnection>(new MockTcpConnection());
                    throw new Exception();
                }
                finally
                {
                    currentRetryCount++;
                }
            }

            var cancellationToken = new CancellationTokenSource(_timeout).Token;

            using (var connection = await NatsConnection.Connect(_servers, Provider, options, cancellationToken))
            {
                await connection.OnConnect.FirstAsync().Timeout(_timeout);
            }

            Assert.Equal(retryCount + 1, currentRetryCount);
        }

        [Fact(DisplayName = "should stop retrying after max retry")]
        public async Task RetryFailed()
        {
            const int maxRetry = 3;
            var options = new NatsOptions
            {
                MaxConnectRetry = maxRetry,
                ConnectRetryDelay = TimeSpan.Zero,
            };

            var currentRetryCount = 0;

            Task<ITcpConnection> Provider(string h, int p)
            {
                try
                {
                    if (currentRetryCount > maxRetry) return Task.FromResult<ITcpConnection>(new MockTcpConnection());
                    throw new Exception();
                }
                finally
                {
                    currentRetryCount++;
                }
            }

            var cancellationToken = new CancellationTokenSource(_timeout).Token;

            var connection = new NatsConnection(_servers, Provider, options);

            await Assert.ThrowsAsync<Exception>(() => connection.Connect(cancellationToken));

            Assert.Equal(maxRetry + 1, currentRetryCount);
            Assert.Equal(NatsConnectionState.Disconnected, connection.ConnectionState);
        }

        [Fact(DisplayName = "should try connected servers when reconnecting")]
        public async Task ConnectedUrls()
        {
            const string expectedServer = "somehost:12345";

            var options = new NatsOptions
            {
                PingTimeout = TimeSpan.FromMilliseconds(5),
                PingPongInterval = TimeSpan.FromMilliseconds(5),
                ConnectRetryDelay = TimeSpan.Zero,
            };

            var connectRequests = new List<string>();

            Task<ITcpConnection> Provider(string h, int p)
            {
                if (h == "localhost" && connectRequests.Count > 0) throw new Exception();

                connectRequests.Add($"{h}:{p}");
                var tcpConnection = new MockTcpConnection(new ServerInfo {ConnectUrls = new[] {expectedServer}});
                return Task.FromResult<ITcpConnection>(tcpConnection);
            }

            var cancellationToken = new CancellationTokenSource(_timeout).Token;
            using (var connection = await NatsConnection.Connect(_servers, Provider, options, cancellationToken))
            {
                await connection.OnConnect.Take(2).Timeout(_timeout);
            }

            Assert.Contains("localhost:4222", connectRequests);
            Assert.Contains(expectedServer, connectRequests);
        }
    }
}