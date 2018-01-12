﻿using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SimpleNatsClient.Extensions;
using SimpleNatsClient.Messages;

namespace SimpleNatsClient.Connection
{
    public class NatsConnection : INatsConnection
    {
        private const string PongOp = "PONG";
        private static readonly byte[] Pong = Encoding.UTF8.GetBytes($"{PongOp}\r\n");
        private const string PingOp = "PING";
        private static readonly byte[] Ping = Encoding.UTF8.GetBytes($"{PingOp}\r\n");

        private readonly CompositeDisposable _disposable;
        private readonly Subject<ServerInfo> _onConnect = new Subject<ServerInfo>();
        private readonly NatsParser _parser = new NatsParser();
        private readonly TcpConnectionProvider _tcpConnectionProvider;
        private readonly NatsConnectionOptions _natsConnectionOptions;
        private ITcpConnection _tcpConnection;
        private IDisposable _readFromStream;

        public ServerInfo ServerInfo { get; private set; }

        public NatsConnectionState ConnectionState { get; private set; } = NatsConnectionState.Disconnected;

        public IObservable<Message> Messages { get; }

        public IObservable<ServerInfo> OnConnect => ConnectionState == NatsConnectionState.Connected
            ? _onConnect.StartWith(ServerInfo)
            : _onConnect;

        internal NatsConnection(TcpConnectionProvider tcpConnectionProvider, NatsConnectionOptions options)
        {
            _tcpConnectionProvider = tcpConnectionProvider;
            _natsConnectionOptions = options;

            var messages = _parser.Messages
                .Select(tuple => MessageDeserializer.Deserialize(tuple.Message, tuple.Payload))
                .Publish();

            Messages = messages;

            var connect = messages.OfType<Message<ServerInfo>>()
                .Do(m => ServerInfo = m.Data)
                .Select(m => Observable.FromAsync(async ct =>
                {
                    if (ConnectionState == NatsConnectionState.Connected) return;
                    ConnectionState = NatsConnectionState.Connecting;
                    if (options.SslRequired)
                    {
                        await _tcpConnection.MakeSsl(options.RemoteCertificateValidationCallback, options.Certificates);
                    }
                    await this.Write($"CONNECT {JsonConvert.SerializeObject(options)}", ct);
                    ConnectionState = NatsConnectionState.Connected;
                    _onConnect.OnNext(m.Data);
                }))
                .Switch()
                .Subscribe();

            var pingpong = messages.Where(m => m.Op == PingOp)
                .Subscribe(async _ => await Write(Pong, CancellationToken.None));

            var reconnect = OnConnect
                .Select(_ => Observable.FromAsync(ct => Write(Ping, ct))
                    .DelaySubscription(options.PingPongInterval)
                    .SelectMany(Messages)
                    .FirstAsync(m => m.Op == PongOp)
                    .Timeout(options.PingTimeout)
                    .Repeat()
                    .IgnoreElements()
                    .Select(__ => Unit.Default)
                    .OnErrorResumeNext(Observable.Empty<Unit>())
                    .Concat(Observable.FromAsync(Connect)))
                .Switch()
                .Finally(Dispose)
                .Subscribe();

            _disposable = new CompositeDisposable(
                connect,
                pingpong,
                reconnect,
                messages.Connect(),
                _parser
            );
        }

        internal async Task Connect(CancellationToken cancellationToken)
        {
            if (_disposable.IsDisposed)
            {
                throw new ObjectDisposedException("NatsConnection");
            }
            
            ConnectionState = NatsConnectionState.Connecting;
            _readFromStream?.Dispose();
            _tcpConnection?.Dispose();
            _parser.Reset();

            var i = 0;
            while (true)
            {
                try
                {
                    _tcpConnection =
                        _tcpConnectionProvider(_natsConnectionOptions.Hostname, _natsConnectionOptions.Port);
                    break;
                }
                catch
                {
                    if (i < _natsConnectionOptions.MaxConnectRetry)
                    {
                        i++;
                        continue;
                    }

                    Dispose();
                    throw;
                }
            }

            var buffer = new byte[512];
            _readFromStream = Observable.FromAsync(ct => _tcpConnection.Read(buffer, ct))
                .Where(count => count > 0)
                .Do(count => _parser.Parse(buffer, 0, count))
                .Repeat()
                .Subscribe();

            await OnConnect.FirstAsync().ToTask(cancellationToken);
        }

        public async Task Write(byte[] buffer, CancellationToken cancellationToken)
        {
            if (_tcpConnection == null)
            {
                throw new InvalidOperationException("Not connected to NATS server");
            }
            if (_disposable.IsDisposed)
            {
                throw new ObjectDisposedException("NatsConnection");
            }

            await _tcpConnection.Write(buffer, cancellationToken);
        }

        public void Dispose()
        {
            if (_disposable.IsDisposed) return;

            ConnectionState = NatsConnectionState.Disconnected;

            _tcpConnection?.Dispose();
            _readFromStream?.Dispose();

            _disposable.Dispose();
        }

        internal static async Task<NatsConnection> Connect(TcpConnectionProvider tcpConnectionProvider, NatsConnectionOptions options, CancellationToken cancellationToken = default(CancellationToken))
        {
            var connection = new NatsConnection(tcpConnectionProvider, options);
            await connection.Connect(cancellationToken);
            return connection;
        }

        public static Task<NatsConnection> Connect(NatsConnectionOptions options, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Connect((host, port) => new TcpConnection(host, port), options, cancellationToken);
        }
    }
}