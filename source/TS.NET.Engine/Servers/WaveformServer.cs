using Microsoft.Extensions.Logging;
using NetCoreServer;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TS.NET.Engine
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WaveformHeader
    {
        internal uint seqnum;
        internal ushort numChannels;
        internal ulong fsPerSample;
        internal ulong triggerFs;
        internal double hwWaveformsPerSec;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ChannelHeader
    {
        internal byte chNum;
        internal ulong depth;
        internal float scale;
        internal float offset;
        internal float trigphase;
        internal byte clipping;
    }

    internal class WaveformSession : TcpSession
    {
        private readonly ILogger logger;
        private readonly ThunderscopeDataBridgeReader bridge;
        private readonly CancellationToken cancellationToken;
        private readonly BlockingChannelWriter<HardwareRequestDto> hardwareRequestChannel;
        private readonly BlockingChannelReader<HardwareResponseDto> hardwareResponseChannel;
        private readonly BlockingChannelWriter<ProcessingRequestDto> processingRequestChannel;
        private readonly BlockingChannelReader<ProcessingResponseDto> processingResponseChannel;
        uint sequenceNumber = 0;

        public WaveformSession(
            TcpServer server,
            ILogger logger,
            ThunderscopeDataBridgeReader bridge,
            CancellationToken cancellationToken,
            BlockingChannelWriter<HardwareRequestDto> hardwareRequestChannel,
            BlockingChannelReader<HardwareResponseDto> hardwareResponseChannel,
            BlockingChannelWriter<ProcessingRequestDto> processingRequestChannel,
            BlockingChannelReader<ProcessingResponseDto> processingResponseChannel) : base(server)
        {
            this.logger = logger;
            this.bridge = bridge;
            this.cancellationToken = cancellationToken;
            this.hardwareRequestChannel = hardwareRequestChannel;
            this.hardwareResponseChannel = hardwareResponseChannel;
            this.processingRequestChannel = processingRequestChannel;
            this.processingResponseChannel = processingResponseChannel;
        }

        protected override void OnConnected()
        {
            logger.LogDebug($"Waveform session with Id {Id} connected!");
            //string message = "Hello from TCP chat! Please send a message or '!' to disconnect the client!";
            //SendAsync(message);
        }

        protected override void OnDisconnected()
        {
            logger.LogDebug($"Waveform session with Id {Id} disconnected!");
        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            //string messageStream = Encoding.UTF8.GetString(buffer, (int)offset, (int)size);
            //var messages = messageStream.Split('\n');
            //foreach (var message in messages)
            //{
            //    if (string.IsNullOrWhiteSpace(message)) { continue; }

            //    //string? response = ProcessWaveformCommand(logger, hardwareRequestChannel, hardwareResponseChannel, processingRequestChannel, processingResponseChannel, message.Trim());

            //    if (response != null)
            //    {
            //        logger.LogDebug(" -> Waveform reply: '{String}'", response);
            //        Send(response);
            //    }
            //}
            //Server.Multicast(message);
            //if (message == "!")
            //    Disconnect();

            if (size == 0)
                return;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (bridge.RequestAndWaitForData(500))
                {
                    //logger.LogDebug("Send waveform...");
                    var configuration = bridge.Hardware;
                    var processing = bridge.Processing;
                    var data = bridge.AcquiredRegionU8;

                    // Remember AdcChannelMode reflects the hardware reality - user may only have 3 channels enabled but hardware has to capture 4.
                    ulong femtosecondsPerSample = configuration.AdcChannelMode switch
                    {
                        AdcChannelMode.Single => 1000000,         // 1 GSPS
                        AdcChannelMode.Dual => 1000000 * 2,     // 500 MSPS
                        AdcChannelMode.Quad => 1000000 * 4,    // 250 MSPS
                        _ => throw new NotImplementedException(),
                    };

                    WaveformHeader header = new()
                    {
                        seqnum = sequenceNumber,
                        numChannels = processing.CurrentChannelCount,
                        fsPerSample = femtosecondsPerSample,
                        triggerFs = 0,
                        hwWaveformsPerSec = 1
                    };

                    ChannelHeader chHeader = new()
                    {
                        chNum = 0,
                        depth = processing.CurrentChannelDataLength,
                        scale = 1,
                        offset = 0,
                        trigphase = 0,
                        clipping = 0
                    };

                    unsafe
                    {
                        Send(new ReadOnlySpan<byte>(&header, sizeof(WaveformHeader)));

                        for (byte channelIndex = 0; channelIndex < processing.CurrentChannelCount; channelIndex++)
                        {
                            ThunderscopeChannel thunderscopeChannel = configuration.GetChannel(channelIndex);

                            //float full_scale = ((float)thunderscopeChannel.VoltFullScale) * 5f; // 5 instead of 10 for signed
                            chHeader.chNum = channelIndex;
                            chHeader.scale = (float)(thunderscopeChannel.ActualVoltFullScale / 255.0);
                            chHeader.offset = -((float)thunderscopeChannel.VoltOffset); // needs chHeader.scale * 0x80 for signed

                            // if (ch == 0)
                            //     logger.LogDebug($"ch {ch}: VoltsDiv={tChannel.VoltsDiv} -> .scale={chHeader.scale}, VoltsOffset={tChannel.VoltsOffset} -> .offset = {chHeader.offset}, Coupling={tChannel.Coupling}");

                            // Length of this channel as 'depth'
                            Send(new ReadOnlySpan<byte>(&chHeader, sizeof(ChannelHeader)));
                            Send(data.Slice(channelIndex * (int)processing.CurrentChannelDataLength, (int)processing.CurrentChannelDataLength));
                        }
                    }
                    sequenceNumber++;
                    break;
                }
            }
        }

        protected override void OnError(SocketError error)
        {
            logger.LogDebug($"Chat TCP session caught an error with code {error}");
        }
    }

    class WaveformServer : TcpServer
    {
        private readonly ILogger logger;
        private readonly BlockingChannelWriter<HardwareRequestDto> hardwareRequestChannel;
        private readonly BlockingChannelReader<HardwareResponseDto> hardwareResponseChannel;
        private readonly BlockingChannelWriter<ProcessingRequestDto> processingRequestChannel;
        private readonly BlockingChannelReader<ProcessingResponseDto> processingResponseChannel;
        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly ThunderscopeDataBridgeReader bridge;

        public WaveformServer(ILoggerFactory loggerFactory,
            ThunderscopeSettings settings,
            IPAddress address,
            int port,
            BlockingChannelWriter<HardwareRequestDto> hardwareRequestChannel,
            BlockingChannelReader<HardwareResponseDto> hardwareResponseChannel,
            BlockingChannelWriter<ProcessingRequestDto> processingRequestChannel,
            BlockingChannelReader<ProcessingResponseDto> processingResponseChannel) : base(address, port)
        {
            logger = loggerFactory.CreateLogger(nameof(WaveformServer));
            this.hardwareRequestChannel = hardwareRequestChannel;
            this.hardwareResponseChannel = hardwareResponseChannel;
            this.processingRequestChannel = processingRequestChannel;
            this.processingResponseChannel = processingResponseChannel;
            cancellationTokenSource = new();
            bridge = new("ThunderScope.1");
            logger.LogDebug("Started");
        }

        protected override TcpSession CreateSession()
        {
            // ThunderscopeBridgeReader isn't thread safe so here be dragons if multiple clients request a waveform concurrently.
            return new WaveformSession(this, logger, bridge, cancellationTokenSource.Token, hardwareRequestChannel, hardwareResponseChannel, processingRequestChannel, processingResponseChannel);
        }

        protected override void OnError(SocketError error)
        {
            logger.LogDebug($"Waveform server caught an error with code {error}");
        }

        protected override void OnStopping()
        {
            base.OnStopping();
        }
    }
}
