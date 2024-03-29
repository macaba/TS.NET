﻿using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace TS.NET.Engine
{
    public class ProcessingTask
    {
        private readonly ILogger logger;
        private readonly ThunderscopeSettings settings;
        private readonly BlockingChannelReader<InputDataDto> processingChannel;
        private readonly BlockingChannelWriter<ThunderscopeMemory> inputChannel;
        private readonly BlockingChannelReader<ProcessingRequestDto> processingRequestChannel;
        private readonly BlockingChannelWriter<ProcessingResponseDto> processingResponseChannel;

        private CancellationTokenSource? cancelTokenSource;
        private Task? taskLoop;

        public ProcessingTask(
            ILoggerFactory loggerFactory,
            ThunderscopeSettings settings,
            BlockingChannelReader<InputDataDto> processingChannel,
            BlockingChannelWriter<ThunderscopeMemory> inputChannel,
            BlockingChannelReader<ProcessingRequestDto> processingRequestChannel,
            BlockingChannelWriter<ProcessingResponseDto> processingResponseChannel)
        {
            logger = loggerFactory.CreateLogger(nameof(ProcessingTask));
            this.settings = settings;
            this.processingChannel = processingChannel;
            this.inputChannel = inputChannel;
            this.processingRequestChannel = processingRequestChannel;
            this.processingResponseChannel = processingResponseChannel;
        }

        public void Start()
        {
            cancelTokenSource = new CancellationTokenSource();
            taskLoop = Task.Factory.StartNew(() => Loop(logger, settings, processingChannel, inputChannel, processingRequestChannel, processingResponseChannel, cancelTokenSource.Token), TaskCreationOptions.LongRunning);
        }

        public void Stop()
        {
            cancelTokenSource?.Cancel();
            taskLoop?.Wait();
        }

        // The job of this task - pull data from scope driver/simulator, shuffle if 2/4 channels, horizontal sum, trigger, and produce window segments.
        private static void Loop(
            ILogger logger,
            ThunderscopeSettings settings,
            BlockingChannelReader<InputDataDto> processingInputChannel,
            BlockingChannelWriter<ThunderscopeMemory> inputChannel,
            BlockingChannelReader<ProcessingRequestDto> processingRequestChannel,
            BlockingChannelWriter<ProcessingResponseDto> processingResponseChannel,
            CancellationToken cancelToken)
        {
            try
            {
                Thread.CurrentThread.Name = "TS.NET Processing";

                // Bridge is cross-process shared memory for the UI to read triggered acquisitions
                // The trigger point is _always_ in the middle of the channel block, and when the UI sets positive/negative trigger point, it's just moving the UI viewport
                ThunderscopeBridgeWriter bridge = new("ThunderScope.1", 4, settings.MaxChannelBytes);

                ThunderscopeConfiguration cachedThunderscopeConfiguration = default;

                // Set some sensible defaults
                var processingConfig = new ThunderscopeProcessing
                {
                    CurrentChannelCount = 4,
                    CurrentChannelBytes = settings.MaxChannelBytes,
                    HorizontalSumLength = HorizontalSumLength.None,
                    TriggerChannel = TriggerChannel.One,
                    TriggerMode = TriggerMode.Normal,
                    ChannelDataType = ThunderscopeChannelDataType.Byte
                };
                bridge.Processing = processingConfig;

                // Reset monitoring
                bridge.MonitoringReset();

                // Various buffers allocated once and reused forevermore.
                //Memory<byte> hardwareBuffer = new byte[ThunderscopeMemory.Length];
                // Shuffle buffers. Only needed for 2/4 channel modes.
                Span<sbyte> shuffleBuffer = new sbyte[ThunderscopeMemory.Length];
                // --2 channel buffers
                int blockLength_2 = (int)ThunderscopeMemory.Length / 2;
                Span<sbyte> postShuffleCh1_2 = shuffleBuffer.Slice(0, blockLength_2);
                Span<sbyte> postShuffleCh2_2 = shuffleBuffer.Slice(blockLength_2, blockLength_2);
                // --4 channel buffers
                int blockLength_4 = (int)ThunderscopeMemory.Length / 4;
                Span<sbyte> postShuffleCh1_4 = shuffleBuffer.Slice(0, blockLength_4);
                Span<sbyte> postShuffleCh2_4 = shuffleBuffer.Slice(blockLength_4, blockLength_4);
                Span<sbyte> postShuffleCh3_4 = shuffleBuffer.Slice(blockLength_4 * 2, blockLength_4);
                Span<sbyte> postShuffleCh4_4 = shuffleBuffer.Slice(blockLength_4 * 3, blockLength_4);

                Span<uint> triggerIndices = new uint[ThunderscopeMemory.Length / 1000];     // 1000 samples is the minimum holdoff
                Span<uint> holdoffEndIndices = new uint[ThunderscopeMemory.Length / 1000];  // 1000 samples is the minimum holdoff
                RisingEdgeTriggerI8 trigger = new(0, -10, processingConfig.CurrentChannelBytes);

                DateTimeOffset startTime = DateTimeOffset.UtcNow;
                uint dequeueCounter = 0;
                uint oneSecondHoldoffCount = 0;
                uint oneSecondDequeueCount = 0;
                // HorizontalSumUtility.ToDivisor(horizontalSumLength)
                Stopwatch periodicUpdateTimer = Stopwatch.StartNew();

                var circularBuffer1 = new ChannelCircularAlignedBuffer((uint)processingConfig.CurrentChannelBytes + ThunderscopeMemory.Length);
                var circularBuffer2 = new ChannelCircularAlignedBuffer((uint)processingConfig.CurrentChannelBytes + ThunderscopeMemory.Length);
                var circularBuffer3 = new ChannelCircularAlignedBuffer((uint)processingConfig.CurrentChannelBytes + ThunderscopeMemory.Length);
                var circularBuffer4 = new ChannelCircularAlignedBuffer((uint)processingConfig.CurrentChannelBytes + ThunderscopeMemory.Length);

                bool triggerRunning = true;
                bool forceTrigger = false;
                bool singleTrigger = false;
                logger.LogInformation("Started");

                while (true)
                {
                    cancelToken.ThrowIfCancellationRequested();

                    // Check for processing requests
                    if (processingRequestChannel.TryRead(out var request))
                    {
                        switch (request)
                        {
                            case ProcessingStartTriggerDto processingStartTriggerDto:
                                triggerRunning = true;
                                singleTrigger = false;
                                forceTrigger = false;
                                logger.LogDebug(nameof(ProcessingStartTriggerDto));
                                break;
                            case ProcessingStopTriggerDto processingStopTriggerDto:
                                triggerRunning = false;
                                singleTrigger = false;
                                forceTrigger = false;
                                logger.LogDebug(nameof(ProcessingStopTriggerDto));
                                break;
                            case ProcessingForceTriggerDto processingForceTriggerDto:
                                if (triggerRunning)
                                    forceTrigger = true;
                                logger.LogDebug(nameof(ProcessingForceTriggerDto));
                                break;
                            case ProcessingSingleTriggerDto processingSingleTriggerDto:
                                triggerRunning = true;
                                singleTrigger = true;
                                logger.LogDebug(nameof(ProcessingSingleTriggerDto));
                                break;
                            case ProcessingSetDepthDto processingSetDepthDto:
                                var depth = processingSetDepthDto.Samples;
                                break;
                            case ProcessingSetRateDto processingSetRateDto:
                                var rate = processingSetRateDto.SamplingHz;
                                break;
                            case ProcessingSetTriggerSourceDto processingSetTriggerSourceDto:
                                var channel = processingSetTriggerSourceDto.Channel;
                                processingConfig.TriggerChannel = channel;
                                break;
                            case ProcessingSetTriggerDelayDto processingSetTriggerDelayDto:
                                var fs = processingSetTriggerDelayDto.Femtoseconds;
                                break;
                            case ProcessingSetTriggerLevelDto processingSetTriggerLevelDto:
                                var requestedTriggerLevel = processingSetTriggerLevelDto.LevelVolts;
                                // Convert the voltage to Int8

                                var triggerChannel = cachedThunderscopeConfiguration.GetTriggerChannel(processingConfig.TriggerChannel);

                                if (requestedTriggerLevel > triggerChannel.ActualVoltFullScale / 2)
                                {
                                    logger.LogWarning($"Could not set trigger level {requestedTriggerLevel}");
                                    break;
                                }
                                if (requestedTriggerLevel < -triggerChannel.ActualVoltFullScale / 2)
                                {
                                    logger.LogWarning($"Could not set trigger level {requestedTriggerLevel}");
                                    break;
                                }

                                sbyte triggerLevel = (sbyte)((requestedTriggerLevel / (triggerChannel.ActualVoltFullScale / 2)) * 127f);

                                if (triggerLevel == sbyte.MinValue)
                                    triggerLevel+=10;     // Coerce so that the trigger arm level is correct

                                logger.LogDebug($"Setting trigger level to {triggerLevel}");
                                trigger.Reset(triggerLevel, triggerLevel-=10, processingConfig.CurrentChannelBytes);
                                break;
                            case ProcessingSetTriggerEdgeDirectionDto processingSetTriggerEdgeDirectionDto:
                                // var edges = processingSetTriggerEdgeDirectionDto.Edges;
                                break;
                            default:
                                logger.LogWarning($"Unknown ProcessingRequestDto: {request}");
                                break;
                        }

                        bridge.Processing = processingConfig;
                    }

                    InputDataDto inputDataDto = processingInputChannel.Read(cancelToken);
                    cachedThunderscopeConfiguration = inputDataDto.Configuration;
                    bridge.Configuration = inputDataDto.Configuration;
                    dequeueCounter++;
                    oneSecondDequeueCount++;

                    int channelLength = (int)processingConfig.CurrentChannelBytes;
                    switch (inputDataDto.Configuration.AdcChannelMode)
                    {
                        // Processing pipeline:
                        // Shuffle (if needed)
                        // Horizontal sum (EDIT: triggering should happen _before_ horizontal sum)
                        // Write to circular buffer
                        // Trigger
                        // Data segment on trigger (if needed)
                        case AdcChannelMode.Single:
                            // Horizontal sum (EDIT: triggering should happen _before_ horizontal sum)
                            //if (config.HorizontalSumLength != HorizontalSumLength.None)
                            //    throw new NotImplementedException();
                            // Write to circular buffer
                            circularBuffer1.Write(inputDataDto.Memory.Span);
                            // Trigger
                            if (processingConfig.TriggerChannel != TriggerChannel.None)
                            {
                                var triggerChannelBuffer = processingConfig.TriggerChannel switch
                                {
                                    TriggerChannel.One => inputDataDto.Memory.Span,
                                    _ => throw new ArgumentException("Invalid TriggerChannel value")
                                };
                                trigger.ProcessSimd(input: triggerChannelBuffer, triggerIndices: triggerIndices, out uint triggerCount, holdoffEndIndices: holdoffEndIndices, out uint holdoffEndCount);
                            }
                            // Finished with the memory, return it
                            inputChannel.Write(inputDataDto.Memory);
                            break;
                        case AdcChannelMode.Dual:
                            // Shuffle
                            Shuffle.TwoChannels(input: inputDataDto.Memory.Span, output: shuffleBuffer);
                            // Finished with the memory, return it
                            inputChannel.Write(inputDataDto.Memory);
                            // Horizontal sum (EDIT: triggering should happen _before_ horizontal sum)
                            //if (config.HorizontalSumLength != HorizontalSumLength.None)
                            //    throw new NotImplementedException();
                            // Write to circular buffer
                            circularBuffer1.Write(postShuffleCh1_2);
                            circularBuffer2.Write(postShuffleCh2_2);
                            // Trigger
                            if (processingConfig.TriggerChannel != TriggerChannel.None)
                            {
                                var triggerChannelBuffer = processingConfig.TriggerChannel switch
                                {
                                    TriggerChannel.One => postShuffleCh1_2,
                                    TriggerChannel.Two => postShuffleCh2_2,
                                    _ => throw new ArgumentException("Invalid TriggerChannel value")
                                };
                                trigger.ProcessSimd(input: triggerChannelBuffer, triggerIndices: triggerIndices, out uint triggerCount, holdoffEndIndices: holdoffEndIndices, out uint holdoffEndCount);
                            }
                            break;
                        case AdcChannelMode.Quad:
                            // Shuffle
                            Shuffle.FourChannels(input: inputDataDto.Memory.Span, output: shuffleBuffer);
                            // Finished with the memory, return it
                            inputChannel.Write(inputDataDto.Memory);
                            // Horizontal sum (EDIT: triggering should happen _before_ horizontal sum)
                            //if (config.HorizontalSumLength != HorizontalSumLength.None)
                            //    throw new NotImplementedException();
                            // Write to circular buffer
                            circularBuffer1.Write(postShuffleCh1_4);
                            circularBuffer2.Write(postShuffleCh2_4);
                            circularBuffer3.Write(postShuffleCh3_4);
                            circularBuffer4.Write(postShuffleCh4_4);
                            // Trigger
                            if (triggerRunning && processingConfig.TriggerChannel != TriggerChannel.None)
                            {
                                var triggerChannelBuffer = processingConfig.TriggerChannel switch
                                {
                                    TriggerChannel.One => postShuffleCh1_4,
                                    TriggerChannel.Two => postShuffleCh2_4,
                                    TriggerChannel.Three => postShuffleCh3_4,
                                    TriggerChannel.Four => postShuffleCh4_4,
                                    _ => throw new ArgumentException("Invalid TriggerChannel value")
                                };
                                trigger.ProcessSimd(input: triggerChannelBuffer, triggerIndices: triggerIndices, out uint triggerCount, holdoffEndIndices: holdoffEndIndices, out uint holdoffEndCount);
                                oneSecondHoldoffCount += holdoffEndCount;
                                if (holdoffEndCount > 0)
                                {
                                    // logger.LogDebug("Trigger Fired");
                                    for (int i = 0; i < holdoffEndCount; i++)
                                    {
                                        var bridgeSpan = bridge.AcquiringRegion;
                                        uint holdoffEndIndex = (uint)postShuffleCh1_4.Length - holdoffEndIndices[i];
                                        circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), holdoffEndIndex);
                                        circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), holdoffEndIndex);
                                        circularBuffer3.Read(bridgeSpan.Slice(channelLength + channelLength, channelLength), holdoffEndIndex);
                                        circularBuffer4.Read(bridgeSpan.Slice(channelLength + channelLength + channelLength, channelLength), holdoffEndIndex);
                                        bridge.DataWritten();
                                        bridge.SwitchRegionIfNeeded();
                                    }
                                    forceTrigger = false;       // Ignore the force trigger request, a normal trigger happened
                                    if (singleTrigger) triggerRunning = false;
                                }
                                else if (forceTrigger)
                                {
                                    // logger.LogDebug("Force Trigger fired");
                                    var bridgeSpan = bridge.AcquiringRegion;
                                    circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), 0);
                                    circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), 0);
                                    circularBuffer3.Read(bridgeSpan.Slice(channelLength + channelLength, channelLength), 0);
                                    circularBuffer4.Read(bridgeSpan.Slice(channelLength + channelLength + channelLength, channelLength), 0);
                                    bridge.DataWritten();
                                    bridge.SwitchRegionIfNeeded();
                                    forceTrigger = false;
                                    if (singleTrigger) triggerRunning = false;
                                }
                                else
                                {
                                    bridge.SwitchRegionIfNeeded();
                                }

                            }
                            //logger.LogInformation($"Dequeue #{dequeueCounter++}, Ch1 triggers: {triggerCount1}, Ch2 triggers: {triggerCount2}, Ch3 triggers: {triggerCount3}, Ch4 triggers: {triggerCount4} ");
                            break;
                    }

                    if (periodicUpdateTimer.ElapsedMilliseconds >= 10000)
                    {
                        logger.LogDebug($"Outstanding frames: {processingInputChannel.PeekAvailable()}, dequeues/sec: {oneSecondDequeueCount / (periodicUpdateTimer.Elapsed.TotalSeconds):F2}, dequeue count: {dequeueCounter}");
                        logger.LogDebug($"Triggers/sec: {oneSecondHoldoffCount / (periodicUpdateTimer.Elapsed.TotalSeconds):F2}, trigger count: {bridge.Monitoring.TotalAcquisitions}, UI dropped triggers: {bridge.Monitoring.MissedAcquisitions}");
                        periodicUpdateTimer.Restart();
                        oneSecondHoldoffCount = 0;
                        oneSecondDequeueCount = 0;
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
                logger.LogDebug("Stopped");
            }
        }
    }
}
