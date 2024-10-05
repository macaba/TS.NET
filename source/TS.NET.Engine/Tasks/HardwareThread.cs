﻿using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace TS.NET.Engine
{
    // The job of this task is to read from the thunderscope as fast as possible with minimal jitter
    internal class HardwareThread
    {
        private readonly ILogger logger;
        private readonly IThunderscope thunderscope;
        private readonly ThunderscopeSettings settings;
        private readonly BlockingChannelReader<ThunderscopeMemory> inputChannel;
        private readonly BlockingChannelWriter<InputDataDto> processChannel;
        private readonly BlockingChannelReader<HardwareRequestDto> hardwareRequestChannel;
        private readonly BlockingChannelWriter<HardwareResponseDto> hardwareResponseChannel;

        private CancellationTokenSource? cancelTokenSource;
        private Task? taskLoop;

        public HardwareThread(ILoggerFactory loggerFactory,
            ThunderscopeSettings settings,
            IThunderscope thunderscope,
            BlockingChannelReader<ThunderscopeMemory> inputChannel,
            BlockingChannelWriter<InputDataDto> processChannel,
            BlockingChannelReader<HardwareRequestDto> hardwareRequestChannel,
            BlockingChannelWriter<HardwareResponseDto> hardwareResponseChannel)
        {
            logger = loggerFactory.CreateLogger(nameof(HardwareThread));
            this.settings = settings;
            this.thunderscope = thunderscope;
            this.inputChannel = inputChannel;
            this.processChannel = processChannel;
            this.hardwareRequestChannel = hardwareRequestChannel;
            this.hardwareResponseChannel = hardwareResponseChannel;
        }

        public void Start(SemaphoreSlim startSemaphore)
        {
            cancelTokenSource = new CancellationTokenSource();
            taskLoop = Task.Factory.StartNew(() => Loop(logger, thunderscope, settings, inputChannel, processChannel, hardwareRequestChannel, hardwareResponseChannel, startSemaphore, cancelTokenSource.Token), TaskCreationOptions.LongRunning);
        }

        public void Stop()
        {
            cancelTokenSource?.Cancel();
            taskLoop?.Wait();
        }

        private static void Loop(
            ILogger logger,
            IThunderscope thunderscope,
            ThunderscopeSettings settings,
            BlockingChannelReader<ThunderscopeMemory> inputChannel,
            BlockingChannelWriter<InputDataDto> processChannel,
            BlockingChannelReader<HardwareRequestDto> hardwareRequestChannel,
            BlockingChannelWriter<HardwareResponseDto> hardwareResponseChannel,
            SemaphoreSlim startSemaphore,
            CancellationToken cancelToken)
        {
            Thread.CurrentThread.Name = nameof(HardwareThread);
            //Thread.CurrentThread.Priority = ThreadPriority.Highest;
            if (settings.HardwareThreadProcessorAffinity > -1 && OperatingSystem.IsWindows())
            {
                Thread.BeginThreadAffinity();
                OsThread.SetThreadAffinity(settings.HardwareThreadProcessorAffinity);
                logger.LogDebug($"{nameof(HardwareThread)} thread processor affinity set to {settings.HardwareThreadProcessorAffinity}");
            }

            try
            {
                thunderscope.Start();
                logger.LogInformation("Started");
                startSemaphore.Release();

                logger.LogDebug("Waiting for first block of data...");

                Stopwatch periodicUpdateTimer = Stopwatch.StartNew();
                uint periodicEnqueueCount = 0;
                uint enqueueCounter = 0;

                while (true)
                {
                    cancelToken.ThrowIfCancellationRequested();

                    // Check for configuration requests
                    if (hardwareRequestChannel.PeekAvailable() != 0)
                    {
                        logger.LogDebug("Stop acquisition and process commands...");
                        thunderscope.Stop();

                        while (hardwareRequestChannel.TryRead(out var request))
                        {
                            // Do configuration update, pausing acquisition if necessary
                            switch (request)
                            {
                                case HardwareStartRequest hardwareStartRequest:
                                    logger.LogDebug("Start request (ignore)");
                                    break;
                                case HardwareStopRequest hardwareStopRequest:
                                    logger.LogDebug("Stop request (ignore)");
                                    break;
                                case HardwareSetChannelFrontendRequest hardwareConfigureChannelFrontendDto:
                                    {
                                        var channelIndex = ((HardwareSetChannelFrontendRequest)request).ChannelIndex;
                                        var channelFrontend = thunderscope.GetChannelFrontend(channelIndex);
                                        channelFrontend.PgaConfigWordOverride = false;
                                        switch (request)
                                        {
                                            case HardwareSetVoltOffsetRequest hardwareSetOffsetRequest:
                                                logger.LogDebug($"{nameof(HardwareSetVoltOffsetRequest)} (channel: {channelIndex}, offset: {hardwareSetOffsetRequest.VoltOffset})");
                                                channelFrontend.VoltOffset = hardwareSetOffsetRequest.VoltOffset;
                                                break;
                                            case HardwareSetVoltFullScaleRequest hardwareSetVdivRequest:
                                                logger.LogDebug($"{nameof(HardwareSetVoltFullScaleRequest)} (channel: {channelIndex}, scale: {hardwareSetVdivRequest.VoltFullScale})");
                                                channelFrontend.VoltFullScale = hardwareSetVdivRequest.VoltFullScale;
                                                break;
                                            case HardwareSetBandwidthRequest hardwareSetBandwidthRequest:
                                                logger.LogDebug($"{nameof(HardwareSetBandwidthRequest)} (channel: {channelIndex}, bandwidth: {hardwareSetBandwidthRequest.Bandwidth})");
                                                channelFrontend.Bandwidth = hardwareSetBandwidthRequest.Bandwidth;
                                                break;
                                            case HardwareSetCouplingRequest hardwareSetCouplingRequest:
                                                logger.LogDebug($"{nameof(HardwareSetCouplingRequest)} (channel: {channelIndex}, coupling: {hardwareSetCouplingRequest.Coupling})");
                                                channelFrontend.Coupling = hardwareSetCouplingRequest.Coupling;
                                                break;
                                            case HardwareSetEnabledRequest hardwareSetEnabledRequest:
                                                logger.LogDebug($"{nameof(HardwareSetEnabledRequest)} (channel: {channelIndex}, enabled: {hardwareSetEnabledRequest.Enabled})");
                                                thunderscope.SetChannelEnable(channelIndex, hardwareSetEnabledRequest.Enabled);
                                                break;
                                            case HardwareSetTerminationRequest hardwareSetTerminationRequest:
                                                logger.LogDebug($"{nameof(HardwareSetTerminationRequest)} (channel: {channelIndex}, termination: {hardwareSetTerminationRequest.Termination})");
                                                channelFrontend.Termination = hardwareSetTerminationRequest.Termination;
                                                break;
                                            default:
                                                logger.LogWarning($"Unknown {nameof(HardwareSetChannelFrontendRequest)}: {request}");
                                                break;
                                        }
                                        thunderscope.SetChannelFrontend(channelIndex, channelFrontend);
                                        break;
                                    }
                                case HardwareSetRateRequest hardwareSetRateRequest:
                                    {
                                        thunderscope.SetRate(hardwareSetRateRequest.rate);
                                        logger.LogDebug($"{nameof(hardwareSetRateRequest)} (rate: {hardwareSetRateRequest.rate})");
                                        break;
                                    }
                                case HardwareGetRatesRequest hardwareGetRatesRequest:
                                    {
                                        logger.LogDebug($"{nameof(HardwareGetRatesRequest)}");
                                        var config = thunderscope.GetConfiguration();
                                        switch (config.AdcChannelMode)
                                        {
                                            case AdcChannelMode.Single:
                                                hardwareResponseChannel.Write(new HardwareGetRatesResponse(config.SampleRateHz));
                                                break;
                                            case AdcChannelMode.Dual:
                                                hardwareResponseChannel.Write(new HardwareGetRatesResponse(config.SampleRateHz/2));
                                                break;
                                            case AdcChannelMode.Quad:
                                                hardwareResponseChannel.Write(new HardwareGetRatesResponse(config.SampleRateHz/4));
                                                break;
                                        }
                                        logger.LogDebug($"{nameof(HardwareGetRatesResponse)}");
                                        break;
                                    }
                                case HardwareSetChannelCalibrationRequest hardwareSetChannelCalibrationDto:
                                    {
                                        var channelIndex = ((HardwareSetChannelCalibrationRequest)request).ChannelIndex;
                                        var channelCalibration = thunderscope.GetChannelCalibration(channelIndex);
                                        switch (request)
                                        {
                                            case HardwareSetOffsetVoltageLowGainRequest hardwareSetOffsetVoltageLowGainRequest:
                                                logger.LogDebug($"{nameof(HardwareSetOffsetVoltageLowGainRequest)} (channel: {channelIndex})");
                                                channelCalibration.HardwareOffsetVoltageLowGain = hardwareSetOffsetVoltageLowGainRequest.OffsetVoltage;
                                                break;
                                            case HardwareSetOffsetVoltageHighGainRequest hardwareSetOffsetVoltageHighGainRequest:
                                                logger.LogDebug($"{nameof(HardwareSetOffsetVoltageHighGainRequest)} (channel: {channelIndex})");
                                                channelCalibration.HardwareOffsetVoltageHighGain = hardwareSetOffsetVoltageHighGainRequest.OffsetVoltage;
                                                break;
                                        }
                                        thunderscope.SetChannelCalibration(channelIndex, channelCalibration);
                                        break;
                                    }
                                case HardwareSetChannelFrontendOverrideRequest hardwareSetChannelFrontendOverrideRequest:
                                    {
                                        var channelIndex = ((HardwareSetChannelFrontendOverrideRequest)request).ChannelIndex;
                                        var channelFrontend = thunderscope.GetChannelFrontend(channelIndex);
                                        switch (request)
                                        {
                                            case HardwareSetPgaConfigWordOverrideRequest hardwareSetPgaWordOverrideRequest:
                                                logger.LogDebug($"{nameof(HardwareSetPgaConfigWordOverrideRequest)} (channel: {channelIndex})");
                                                channelFrontend.PgaConfigWordOverride = true;
                                                ushort pgaConfigWord = hardwareSetPgaWordOverrideRequest.PgaConfigWord;
                                                pgaConfigWord &= 0x01FF;    // Mask off the top 7 bits
                                                pgaConfigWord |= 0x400;     // Add on mandatory Aux Hi-Z bit
                                                channelFrontend.PgaConfigWord = pgaConfigWord;
                                                break;
                                        }
                                        thunderscope.SetChannelFrontend(channelIndex, channelFrontend);
                                        break;
                                    }
                                default:
                                    logger.LogWarning($"Unknown {nameof(HardwareRequestDto)}: {request}");
                                    break;
                            }
                            if (hardwareRequestChannel.PeekAvailable() == 0)
                                Thread.Sleep(150);
                        }

                        logger.LogDebug("Start again");
                        thunderscope.Start();
                    }

                    //logger.LogDebug($"Requesting memory block {enqueueCounter}");
                    var memory = inputChannel.Read(cancelToken);
                    //logger.LogDebug($"Memory block {enqueueCounter}");
                    while (true)
                    {
                        try
                        {
                            thunderscope.Read(memory, cancelToken);
                            if (enqueueCounter == 0)
                                logger.LogDebug("First block of data received");
                            //logger.LogDebug($"Acquisition block {enqueueCounter}");
                            break;
                        }
                        catch (ThunderscopeMemoryOutOfMemoryException)
                        {
                            logger.LogWarning("Scope ran out of memory - reset buffer pointers and continue");
                            ((Driver.XMDA.Thunderscope)thunderscope).ResetBuffer();
                            continue;
                        }
                        catch (ThunderscopeFifoOverflowException)
                        {
                            logger.LogWarning("Scope had FIFO overflow - ignore and continue");
                            continue;
                        }
                        catch (ThunderscopeNotRunningException)
                        {
                            // logger.LogWarning("Tried to read from stopped scope");
                            continue;
                        }
                        catch (Exception ex)
                        {
                            if (ex.Message == "ReadFile - failed (1359)")
                            {
                                logger.LogError(ex, $"{nameof(HardwareThread)} error");
                                continue;
                            }
                            throw;
                        }
                    }

                    periodicEnqueueCount++;
                    enqueueCounter++;

                    processChannel.Write(new InputDataDto(thunderscope.GetConfiguration(), memory), cancelToken);

                    if (periodicUpdateTimer.ElapsedMilliseconds >= 10000)
                    {
                        var oneSecondEnqueueCount = periodicEnqueueCount / periodicUpdateTimer.Elapsed.TotalSeconds;
                        logger.LogDebug($"Enqueues/sec: {oneSecondEnqueueCount:F2}, MB/sec: {(oneSecondEnqueueCount * ThunderscopeMemory.Length / 1000 / 1000):F3}, MiB/sec: {(oneSecondEnqueueCount * ThunderscopeMemory.Length / 1024 / 1024):F3}, enqueue count: {enqueueCounter}");

                        if(thunderscope is Driver.LiteX.Thunderscope liteXThunderscope)
                        {
                            var status = liteXThunderscope.GetStatus();
                            logger.LogDebug($"Lost Sample Buffers: {status.AdcSamplesLost}, FPGA Temperature: {status.FpgaTemp:F2}, VCC Int: {status.VccInt:F3}, VCC Aux: {status.VccAux:F3}, VCC BRAM: {status.VccBram:F3}");
                        }

                        periodicUpdateTimer.Restart();
                        periodicEnqueueCount = 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Stopping...");
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Error");
                throw;
            }
            finally
            {
                thunderscope.Stop();
                logger.LogDebug("Stopped");
            }
        }
    }
}
