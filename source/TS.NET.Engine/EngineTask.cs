using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace TS.NET.Engine
{
    internal class EngineTask(ILoggerFactory loggerFactory, IThunderscope thunderscope, ThunderscopeSettings settings)
    {
        private readonly ILogger logger = loggerFactory.CreateLogger(nameof(EngineTask));
        private readonly IThunderscope thunderscope = thunderscope;
        private readonly ThunderscopeSettings settings = settings;

        private CancellationTokenSource? cancelTokenSource;
        private Task? taskLoop;

        public void Start()
        {
            cancelTokenSource = new CancellationTokenSource();
            taskLoop = Task.Factory.StartNew(() => Loop(logger, thunderscope, settings, cancelTokenSource.Token), TaskCreationOptions.LongRunning);
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
            CancellationToken cancelToken)
        {
            Thread.CurrentThread.Name = nameof(EngineTask);
            //Thread.CurrentThread.Priority = ThreadPriority.Highest;
            try
            {
                thunderscope.Start();
                logger.LogDebug("Started");
                logger.LogDebug("Waiting for first block of data...");

                // Variables for reading
                Stopwatch readingPeriodicUpdateTimer = Stopwatch.StartNew();
                uint readCount = 0, periodicReadCount = 0;
                ThunderscopeMemory memory = new();

                // Variables for processing
                byte maxChannelDataByteCount = 1;
                ThunderscopeDataBridgeWriter bridge = new("ThunderScope.1", settings.MaxChannelCount, settings.MaxChannelDataLength, maxChannelDataByteCount);
                // Set some sensible defaults
                var hardwareConfig = new ThunderscopeHardwareConfig
                {
                    AdcChannelMode = AdcChannelMode.Quad
                };
                bridge.Hardware = hardwareConfig;
                var processingConfig = new ThunderscopeProcessingConfig
                {
                    CurrentChannelCount = settings.MaxChannelCount,
                    CurrentChannelDataLength = settings.MaxChannelDataLength,
                    CurrentChannelDataByteCount = maxChannelDataByteCount,
                    HorizontalSumLength = HorizontalSumLength.None,
                    TriggerChannel = TriggerChannel.One,
                    TriggerMode = TriggerMode.Auto,
                    ChannelDataType = ThunderscopeChannelDataType.Byte
                };
                bridge.Processing = processingConfig;
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
                // By setting holdoffSamples to processingConfig.CurrentChannelDataLength, the holdoff is the exact length of the data sent over the bridge which gives near gapless triggering
                RisingEdgeTriggerI8 trigger = new(0, -10, processingConfig.CurrentChannelDataLength);

                DateTimeOffset startTime = DateTimeOffset.UtcNow;
                uint oneSecondHoldoffCount = 0;
                // HorizontalSumUtility.ToDivisor(horizontalSumLength)
                Stopwatch processingPeriodicUpdateTimer = Stopwatch.StartNew();

                var circularBuffer1 = new ChannelCircularAlignedBufferI8((uint)processingConfig.CurrentChannelDataLength + ThunderscopeMemory.Length);
                var circularBuffer2 = new ChannelCircularAlignedBufferI8((uint)processingConfig.CurrentChannelDataLength + ThunderscopeMemory.Length);
                var circularBuffer3 = new ChannelCircularAlignedBufferI8((uint)processingConfig.CurrentChannelDataLength + ThunderscopeMemory.Length);
                var circularBuffer4 = new ChannelCircularAlignedBufferI8((uint)processingConfig.CurrentChannelDataLength + ThunderscopeMemory.Length);

                // Triggering:
                // There are 3 states for Trigger Mode: normal, single, auto.
                // (these only run during Start, not during Stop. Invoking Force will ignore Start/Stop.)
                // Normal: wait for trigger indefinately and run continuously.
                // Single: wait for trigger indefinately and then stop.
                // Auto: wait for trigger indefinately, push update when timeout occurs, and run continously.
                //
                // runTrigger: enables/disables trigger subsystem. 
                // forceTriggerLatch: disregards the Trigger Mode, push update immediately and set forceTrigger to false. If a standard trigger happened at the same time as a force, the force is ignored so the bridge only updates once.
                // singleTriggerLatch: used in Single mode to stop the trigger subsystem after a trigger.

                bool runTrigger = true;
                bool forceTriggerLatch = false;     // "Latch" because it will reset state back to false automatically. If the force is invoked and a trigger happens anyway, it will be reset (effectively ignoring it and only updating the bridge once).
                bool singleTriggerLatch = false;    // "Latch" because it will reset state back to false automatically. When reset, runTrigger will be set to false.
                Stopwatch autoTimer = Stopwatch.StartNew();

                while (true)
                {
                    cancelToken.ThrowIfCancellationRequested();

                    //// Check for configuration requests
                    //if (hardwareRequestChannel.PeekAvailable() != 0)
                    //{
                    //    logger.LogDebug("Stop acquisition and process commands...");
                    //    thunderscope.Stop();

                    //    while (hardwareRequestChannel.TryRead(out var request))
                    //    {
                    //        // Do configuration update, pausing acquisition if necessary
                    //        switch (request)
                    //        {
                    //            case HardwareStartRequest hardwareStartRequest:
                    //                logger.LogDebug("Start request (ignore)");
                    //                break;
                    //            case HardwareStopRequest hardwareStopRequest:
                    //                logger.LogDebug("Stop request (ignore)");
                    //                break;
                    //            case HardwareConfigureChannelDto hardwareConfigureChannelDto:
                    //                var channelIndex = ((HardwareConfigureChannelDto)request).Channel;
                    //                ThunderscopeChannel channel = thunderscope.GetChannel(channelIndex);
                    //                switch (request)
                    //                {
                    //                    case HardwareSetVoltOffsetRequest hardwareSetOffsetRequest:
                    //                        logger.LogDebug($"Set offset request: channel {channelIndex} volt offset {hardwareSetOffsetRequest.VoltOffset}");
                    //                        channel.VoltOffset = hardwareSetOffsetRequest.VoltOffset;
                    //                        break;
                    //                    case HardwareSetVoltFullScaleRequest hardwareSetVdivRequest:
                    //                        logger.LogDebug($"Set vdiv request: channel {channelIndex} volt full scale {hardwareSetVdivRequest.VoltFullScale}");
                    //                        channel.VoltFullScale = hardwareSetVdivRequest.VoltFullScale;
                    //                        break;
                    //                    case HardwareSetBandwidthRequest hardwareSetBandwidthRequest:
                    //                        logger.LogDebug($"Set bw request: channel {channelIndex} bandwidth {hardwareSetBandwidthRequest.Bandwidth}");
                    //                        channel.Bandwidth = hardwareSetBandwidthRequest.Bandwidth;
                    //                        break;
                    //                    case HardwareSetCouplingRequest hardwareSetCouplingRequest:
                    //                        logger.LogDebug($"Set coup request: channel {channelIndex} coupling {hardwareSetCouplingRequest.Coupling}");
                    //                        channel.Coupling = hardwareSetCouplingRequest.Coupling;
                    //                        break;
                    //                    case HardwareSetEnabledRequest hardwareSetEnabledRequest:
                    //                        logger.LogDebug($"Set enabled request: channel {channelIndex} enabled {hardwareSetEnabledRequest.Enabled}");
                    //                        channel.Enabled = hardwareSetEnabledRequest.Enabled;
                    //                        break;
                    //                    default:
                    //                        logger.LogWarning($"Unknown HardwareConfigureChannelDto: {request}");
                    //                        break;
                    //                }
                    //                thunderscope.SetChannel(channel, channelIndex);
                    //                break;
                    //            default:
                    //                logger.LogWarning($"Unknown HardwareRequestDto: {request}");
                    //                break;
                    //        }
                    //        // Signal back to the sender that config update happened.
                    //        // hardwareResponseChannel.TryWrite(new HardwareResponseDto(request));

                    //        if (hardwareRequestChannel.PeekAvailable() == 0)
                    //            Thread.Sleep(150);
                    //    }

                    //    logger.LogDebug("Start again");
                    //    thunderscope.Start();
                    //}

                    while (true)
                    {
                        try
                        {
                            thunderscope.Read(memory, cancelToken);
                            if (readCount == 0)
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
                                logger.LogError(ex, $"{nameof(InputTask)} error");
                                continue;
                            }
                            throw;
                        }
                    }

                    periodicReadCount++;
                    readCount++;

                    //processingChannel.Write(new InputDataDto(thunderscope.GetConfiguration(), memory), cancelToken);

                    if (readingPeriodicUpdateTimer.ElapsedMilliseconds >= 10000)
                    {
                        var enqueuePerSec = periodicReadCount / readingPeriodicUpdateTimer.Elapsed.TotalSeconds;
                        logger.LogDebug($"MB/sec: {(enqueuePerSec * ThunderscopeMemory.Length / 1000 / 1000):F3}, MiB/sec: {(enqueuePerSec * ThunderscopeMemory.Length / 1024 / 1024):F3}, read count: {readCount}");
                        readingPeriodicUpdateTimer.Restart();
                        periodicReadCount = 0;
                    }

                    // ======== PROCESSING ===========
                    int channelLength = (int)processingConfig.CurrentChannelDataLength;
                    switch (hardwareConfig.AdcChannelMode)
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
                            circularBuffer1.Write(memory.SpanI8);
                            // Trigger
                            if (processingConfig.TriggerChannel != TriggerChannel.None)
                            {
                                var triggerChannelBuffer = processingConfig.TriggerChannel switch
                                {
                                    TriggerChannel.One => memory.SpanI8,
                                    _ => throw new ArgumentException("Invalid TriggerChannel value")
                                };
                                trigger.ProcessSimd(input: triggerChannelBuffer, triggerIndices: triggerIndices, out uint triggerCount, holdoffEndIndices: holdoffEndIndices, out uint holdoffEndCount);
                            }
                            break;
                        case AdcChannelMode.Dual:
                            // Shuffle
                            Shuffle.TwoChannels(input: memory.SpanI8, output: shuffleBuffer);
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
                            Shuffle.FourChannels(input: memory.SpanI8, output: shuffleBuffer);
                            // Horizontal sum (EDIT: triggering should happen _before_ horizontal sum)
                            //if (config.HorizontalSumLength != HorizontalSumLength.None)
                            //    throw new NotImplementedException();
                            // Write to circular buffer
                            circularBuffer1.Write(postShuffleCh1_4);
                            circularBuffer2.Write(postShuffleCh2_4);
                            circularBuffer3.Write(postShuffleCh3_4);
                            circularBuffer4.Write(postShuffleCh4_4);
                            // Trigger
                            if (runTrigger && processingConfig.TriggerChannel != TriggerChannel.None)
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
                                    //logger.LogDebug("Trigger fired");
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
                                    forceTriggerLatch = false;      // Ignore the force trigger request, if any, as a non-force trigger happened
                                    autoTimer.Restart();            // Restart the timer so there aren't auto updates if regular triggering is happening.
                                    if (singleTriggerLatch)         // If this was a single trigger, reset the singleTrigger & runTrigger latches
                                    {
                                        singleTriggerLatch = false;
                                        runTrigger = false;
                                    }
                                }
                                else if (processingConfig.TriggerMode == TriggerMode.Auto && autoTimer.ElapsedMilliseconds > 1000)
                                {
                                    //logger.LogDebug("Auto trigger fired");
                                    var bridgeSpan = bridge.AcquiringRegion;
                                    circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), 0);
                                    circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), 0);
                                    circularBuffer3.Read(bridgeSpan.Slice(channelLength + channelLength, channelLength), 0);
                                    circularBuffer4.Read(bridgeSpan.Slice(channelLength + channelLength + channelLength, channelLength), 0);
                                    bridge.DataWritten();
                                    bridge.SwitchRegionIfNeeded();
                                    forceTriggerLatch = false;      // Ignore the force trigger request, if any, as a non-force trigger happened
                                    autoTimer.Restart();            // Restart the timer so we get auto updates at a regular interval
                                }
                                else
                                {
                                    bridge.SwitchRegionIfNeeded();  // To do: add a comment here when the reason for this LoC is discovered...!
                                }

                            }
                            if (forceTriggerLatch)             // If a forceTriggerLatch is still active, send data to the bridge and reset latch.
                            {
                                //logger.LogDebug("Force trigger fired");
                                var bridgeSpan = bridge.AcquiringRegion;
                                circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), 0);
                                circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), 0);
                                circularBuffer3.Read(bridgeSpan.Slice(channelLength + channelLength, channelLength), 0);
                                circularBuffer4.Read(bridgeSpan.Slice(channelLength + channelLength + channelLength, channelLength), 0);
                                bridge.DataWritten();
                                bridge.SwitchRegionIfNeeded();
                                forceTriggerLatch = false;
                            }

                            //logger.LogInformation($"Dequeue #{dequeueCounter++}, Ch1 triggers: {triggerCount1}, Ch2 triggers: {triggerCount2}, Ch3 triggers: {triggerCount3}, Ch4 triggers: {triggerCount4} ");
                            break;
                    }

                    if (processingPeriodicUpdateTimer.ElapsedMilliseconds >= 10000)
                    {
                        logger.LogDebug($"Triggers/sec: {oneSecondHoldoffCount / (processingPeriodicUpdateTimer.Elapsed.TotalSeconds):F2}, trigger count: {bridge.Monitoring.TotalAcquisitions}, UI dropped triggers: {bridge.Monitoring.MissedAcquisitions}");
                        processingPeriodicUpdateTimer.Restart();
                        oneSecondHoldoffCount = 0;
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
