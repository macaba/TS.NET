﻿using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace TS.NET.Engine
{
    public class ProcessingThread
    {
        private readonly ILogger logger;
        private readonly ThunderscopeSettings settings;
        private readonly BlockingChannelReader<InputDataDto> processChannel;
        private readonly BlockingChannelWriter<ThunderscopeMemory> inputChannel;
        private readonly BlockingChannelReader<ProcessingRequestDto> processingRequestChannel;
        private readonly BlockingChannelWriter<ProcessingResponseDto> processingResponseChannel;
        private readonly string bridgeNamespace;

        private CancellationTokenSource? cancelTokenSource;
        private Task? taskLoop;

        public ProcessingThread(
            ILoggerFactory loggerFactory,
            ThunderscopeSettings settings,
            BlockingChannelReader<InputDataDto> processChannel,
            BlockingChannelWriter<ThunderscopeMemory> inputChannel,
            BlockingChannelReader<ProcessingRequestDto> processingRequestChannel,
            BlockingChannelWriter<ProcessingResponseDto> processingResponseChannel,
            string bridgeNamespace)
        {
            logger = loggerFactory.CreateLogger(nameof(ProcessingThread));
            this.settings = settings;
            this.processChannel = processChannel;
            this.inputChannel = inputChannel;
            this.processingRequestChannel = processingRequestChannel;
            this.processingResponseChannel = processingResponseChannel;
            this.bridgeNamespace = bridgeNamespace;
        }

        public void Start(SemaphoreSlim startSemaphore)
        {
            cancelTokenSource = new CancellationTokenSource();
            taskLoop = Task.Factory.StartNew(() => Loop(logger, settings, processChannel, inputChannel, processingRequestChannel, processingResponseChannel, startSemaphore, bridgeNamespace, cancelTokenSource.Token), TaskCreationOptions.LongRunning);
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
            BlockingChannelReader<InputDataDto> processChannel,
            BlockingChannelWriter<ThunderscopeMemory> inputChannel,
            BlockingChannelReader<ProcessingRequestDto> processingRequestChannel,
            BlockingChannelWriter<ProcessingResponseDto> processingResponseChannel,
            SemaphoreSlim startSemaphore,
            string bridgeNamespace,
            CancellationToken cancelToken)
        {
            try
            {
                Thread.CurrentThread.Name = nameof(ProcessingThread);
                if (settings.ProcessingThreadProcessorAffinity > -1)
                {
                    Thread.BeginThreadAffinity();
                    OsThread.SetThreadAffinity(settings.ProcessingThreadProcessorAffinity);
                    logger.LogDebug($"{nameof(ProcessingThread)} thread processor affinity set to {settings.ProcessingThreadProcessorAffinity}");
                }

                ThunderscopeDataBridgeConfig bridgeConfig = new()
                {
                    MaxChannelCount = settings.MaxChannelCount,
                    MaxChannelDataLength = settings.MaxChannelDataLength,
                    ChannelDataType = ThunderscopeChannelDataType.I8
                };
                ThunderscopeDataBridgeWriter bridge = new(bridgeNamespace, bridgeConfig);

                ThunderscopeHardwareConfig cachedHardwareConfig = default;

                // Set some sensible defaults
                var processingConfig = new ThunderscopeProcessingConfig
                {
                    CurrentChannelCount = settings.MaxChannelCount,
                    CurrentChannelDataLength = settings.MaxChannelDataLength,
                    TriggerChannel = TriggerChannel.Channel1,
                    TriggerMode = TriggerMode.Auto,
                    TriggerType = TriggerType.RisingEdge,
                    TriggerDelayFs = 0,
                    TriggerHysteresis = 5,
                    TriggerHoldoff = 0,
                    BoxcarAveraging = BoxcarAveraging.None

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
                Span<uint> captureEndIndices = new uint[ThunderscopeMemory.Length / 1000];  // 1000 samples is the minimum window width

                // Periodic debug display variables
                DateTimeOffset startTime = DateTimeOffset.UtcNow;
                ulong totalDequeueCount = 0;
                ulong cachedTotalDequeueCount = 0;
                ulong cachedTotalAcquisitions = 0;
                ulong cachedMissedAcquisitions = 0;
                Stopwatch periodicUpdateTimer = Stopwatch.StartNew();

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

                AdcChannelMode cachedAdcChannelMode = AdcChannelMode.Quad;
                RisingEdgeTriggerI8_v2 risingEdgeTrigger = new(5, processingConfig.TriggerHysteresis, processingConfig.CurrentChannelDataLength, 0, processingConfig.TriggerHoldoff);
                FallingEdgeTriggerI8_v2 fallingEdgeTrigger = new(5, processingConfig.TriggerHysteresis, processingConfig.CurrentChannelDataLength, 0, processingConfig.TriggerHoldoff);
                bool runMode = true;
                bool forceTriggerLatch = false;     // "Latch" because it will reset state back to false. If the force is invoked and a trigger happens anyway, it will be reset (effectively ignoring it and only updating the bridge once).
                bool singleTriggerLatch = false;    // "Latch" because it will reset state back to false. When reset, runTrigger will be set to false.

                // Variables for Auto triggering
                Stopwatch autoTimeoutTimer = Stopwatch.StartNew();
                int streamSampleCounter = 0;
                long autoTimeout = 500;

                logger.LogInformation("Started");
                startSemaphore.Release();

                while (true)
                {
                    cancelToken.ThrowIfCancellationRequested();

                    // Check for processing requests
                    if (processingRequestChannel.TryRead(out var request))
                    {
                        switch (request)
                        {
                            case ProcessingRunDto processingStartTriggerDto:
                                runMode = true;
                                logger.LogDebug($"{nameof(ProcessingRunDto)}");
                                break;
                            case ProcessingStopDto processingStopTriggerDto:
                                runMode = false;
                                logger.LogDebug($"{nameof(ProcessingStopDto)}");
                                break;
                            case ProcessingForceTriggerDto processingForceTriggerDto:
                                forceTriggerLatch = true;
                                logger.LogDebug($"{nameof(ProcessingForceTriggerDto)}");
                                break;
                            case ProcessingSetTriggerModeDto processingSetTriggerModeDto:
                                processingConfig.TriggerMode = processingSetTriggerModeDto.Mode;
                                switch (processingSetTriggerModeDto.Mode)
                                {
                                    case TriggerMode.Normal:
                                    case TriggerMode.Stream:
                                        singleTriggerLatch = false;
                                        break;
                                    case TriggerMode.Single:
                                        singleTriggerLatch = true;
                                        break;
                                    case TriggerMode.Auto:
                                        singleTriggerLatch = false;
                                        autoTimeoutTimer.Restart();
                                        break;
                                }
                                logger.LogDebug($"{nameof(ProcessingSetTriggerModeDto)} (mode: {processingConfig.TriggerMode})");
                                break;
                            case ProcessingSetDepthDto processingSetDepthDto:
                                if (processingConfig.CurrentChannelDataLength != processingSetDepthDto.Samples)
                                {
                                    processingConfig.CurrentChannelDataLength = processingSetDepthDto.Samples;
                                    UpdateTriggerHorizontalPosition();
                                    logger.LogDebug($"{nameof(ProcessingSetDepthDto)} ({processingConfig.CurrentChannelDataLength})");
                                }
                                logger.LogDebug($"{nameof(ProcessingSetDepthDto)} (no change)");
                                break;
                            case ProcessingSetRateDto processingSetRateDto:
                                var rate = processingSetRateDto.SamplingHz;
                                logger.LogWarning($"{nameof(ProcessingSetRateDto)} [Not implemented]");
                                break;
                            case ProcessingSetTriggerSourceDto processingSetTriggerSourceDto:
                                processingConfig.TriggerChannel = processingSetTriggerSourceDto.Channel;
                                logger.LogDebug($"{nameof(ProcessingSetTriggerSourceDto)} (channel: {processingConfig.TriggerChannel})");
                                break;
                            case ProcessingSetTriggerDelayDto processingSetTriggerDelayDto:
                                if (processingConfig.TriggerDelayFs != processingSetTriggerDelayDto.Femtoseconds)
                                {
                                    processingConfig.TriggerDelayFs = processingSetTriggerDelayDto.Femtoseconds;
                                    UpdateTriggerHorizontalPosition();
                                    logger.LogDebug($"{nameof(ProcessingSetTriggerDelayDto)} (femtoseconds: {processingConfig.TriggerDelayFs})");
                                }
                                logger.LogDebug($"{nameof(ProcessingSetTriggerDelayDto)} (no change)");
                                break;
                            case ProcessingSetTriggerLevelDto processingSetTriggerLevelDto:
                                var requestedTriggerLevel = processingSetTriggerLevelDto.LevelVolts;
                                // Convert the voltage to Int8

                                var triggerChannel = cachedHardwareConfig.GetTriggerChannelFrontend(processingConfig.TriggerChannel);

                                if ((requestedTriggerLevel > triggerChannel.ActualVoltFullScale / 2) || (requestedTriggerLevel < -triggerChannel.ActualVoltFullScale / 2))
                                {
                                    logger.LogWarning($"Could not set trigger level {requestedTriggerLevel}");
                                    break;
                                }

                                sbyte triggerLevel = (sbyte)((requestedTriggerLevel / (triggerChannel.ActualVoltFullScale / 2)) * 127f);
                                if (triggerLevel != processingConfig.TriggerLevel)
                                {
                                    processingConfig.TriggerLevel = triggerLevel;
                                    risingEdgeTrigger.SetVertical(triggerLevel, processingConfig.TriggerHysteresis);
                                    fallingEdgeTrigger.SetVertical(triggerLevel, processingConfig.TriggerHysteresis);
                                    logger.LogDebug($"{nameof(ProcessingSetTriggerLevelDto)} (level: {triggerLevel}, hysteresis: {processingConfig.TriggerHysteresis})");
                                }
                                logger.LogDebug($"{nameof(ProcessingSetTriggerLevelDto)} (no change)");
                                break;
                            case ProcessingSetTriggerTypeDto processingSetTriggerTypeDto:
                                processingConfig.TriggerType = processingSetTriggerTypeDto.Type;
                                logger.LogDebug($"{nameof(ProcessingSetTriggerTypeDto)} (type: {processingConfig.TriggerType})");
                                break;
                            case ProcessingGetRateRequestDto processingGetRateRequestDto:
                                logger.LogDebug($"{nameof(ProcessingGetRateRequestDto)}");
                                switch (cachedHardwareConfig.AdcChannelMode)
                                {
                                    case AdcChannelMode.Single:
                                        processingResponseChannel.Write(new ProcessingGetRateResponseDto(1000000000));
                                        break;
                                    case AdcChannelMode.Dual:
                                        processingResponseChannel.Write(new ProcessingGetRateResponseDto(500000000));
                                        break;
                                    case AdcChannelMode.Quad:
                                        processingResponseChannel.Write(new ProcessingGetRateResponseDto(250000000));
                                        break;
                                }
                                logger.LogDebug($"{nameof(ProcessingGetRateResponseDto)}");
                                break;
                            default:
                                logger.LogWarning($"Unknown ProcessingRequestDto: {request}");
                                break;
                        }

                        bridge.Processing = processingConfig;
                    }

                    if (processChannel.TryRead(out InputDataDto inputDataDto, 10, cancelToken))
                    {
                        cachedHardwareConfig = inputDataDto.HardwareConfig;
                        bridge.Hardware = inputDataDto.HardwareConfig;
                        totalDequeueCount++;

                        if (inputDataDto.HardwareConfig.AdcChannelMode != cachedAdcChannelMode)
                        {
                            // If the AdcChannelMode changes (i.e. sample rate changes) then update horizontal trigger positions
                            cachedAdcChannelMode = inputDataDto.HardwareConfig.AdcChannelMode;
                            UpdateTriggerHorizontalPosition();
                        }

                        int channelLength = (int)processingConfig.CurrentChannelDataLength;
                        switch (inputDataDto.HardwareConfig.AdcChannelMode)
                        {
                            // Processing pipeline:
                            // Shuffle (if needed)
                            // Horizontal sum (EDIT: triggering should happen _before_ horizontal sum)
                            // Write to circular buffer
                            // Trigger
                            // Data segment on trigger (if needed)
                            case AdcChannelMode.Single:
                                // Copy
                                inputDataDto.Memory.SpanI8.CopyTo(shuffleBuffer);
                                // Finished with the memory, return it
                                inputChannel.Write(inputDataDto.Memory);
                                // Write to circular buffer
                                circularBuffer1.Write(shuffleBuffer);
                                streamSampleCounter += shuffleBuffer.Length;
                                // Trigger
                                if (runMode)
                                {
                                    switch (processingConfig.TriggerMode)
                                    {
                                        case TriggerMode.Normal:
                                        case TriggerMode.Single:
                                        case TriggerMode.Auto:
                                            if (cachedHardwareConfig.IsTriggerChannelAnEnabledChannel(processingConfig.TriggerChannel))
                                            {
                                                var triggerChannelBuffer = shuffleBuffer;

                                                uint captureEndCount = 0;
                                                switch (processingConfig.TriggerType)
                                                {
                                                    case TriggerType.RisingEdge:
                                                        risingEdgeTrigger.ProcessSimd(input: triggerChannelBuffer, captureEndIndices: captureEndIndices, out captureEndCount);
                                                        break;
                                                    case TriggerType.FallingEdge:
                                                        fallingEdgeTrigger.ProcessSimd(input: triggerChannelBuffer, captureEndIndices: captureEndIndices, out captureEndCount);
                                                        break;
                                                }

                                                if (captureEndCount > 0)
                                                {
                                                    for (int i = 0; i < captureEndCount; i++)
                                                    {
                                                        var bridgeSpan = bridge.AcquiringRegionI8;
                                                        uint endOffset = (uint)triggerChannelBuffer.Length - captureEndIndices[i];
                                                        circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), endOffset);
                                                        bridge.DataWritten(triggered: true);
                                                        bridge.SwitchRegionIfNeeded();

                                                        if (singleTriggerLatch)         // If this was a single trigger, reset the singleTrigger & runTrigger latches
                                                        {
                                                            singleTriggerLatch = false;
                                                            runMode = false;
                                                            break;
                                                        }
                                                    }
                                                    streamSampleCounter = 0;
                                                    autoTimeoutTimer.Restart();     // Restart the auto timeout as a normal trigger happened
                                                }
                                            }
                                            if (forceTriggerLatch) // This will always run, despite whether a trigger has happened or not (so from the user perspective, the UI might show one misaligned waveform during normal triggering; this is intended)
                                            {
                                                var bridgeSpan = bridge.AcquiringRegionI8;
                                                circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), 0);
                                                bridge.DataWritten(triggered: false);
                                                bridge.SwitchRegionIfNeeded();
                                                forceTriggerLatch = false;

                                                streamSampleCounter = 0;
                                                autoTimeoutTimer.Restart();     // Restart the auto timeout as a force trigger happened
                                            }
                                            if (processingConfig.TriggerMode == TriggerMode.Auto && autoTimeoutTimer.ElapsedMilliseconds > autoTimeout)
                                            {
                                                SingleChannelStream(channelLength);
                                            }
                                            break;
                                        case TriggerMode.Stream:
                                            SingleChannelStream(channelLength);
                                            break;
                                    }
                                }
                                break;
                            case AdcChannelMode.Dual:
                                // Shuffle
                                Shuffle.TwoChannels(input: inputDataDto.Memory.SpanI8, output: shuffleBuffer);
                                // Finished with the memory, return it
                                inputChannel.Write(inputDataDto.Memory);
                                // Write to circular buffer
                                circularBuffer1.Write(postShuffleCh1_2);
                                circularBuffer2.Write(postShuffleCh2_2);
                                streamSampleCounter += postShuffleCh1_2.Length;
                                // Trigger
                                if (runMode)
                                {
                                    switch (processingConfig.TriggerMode)
                                    {
                                        case TriggerMode.Normal:
                                        case TriggerMode.Single:
                                        case TriggerMode.Auto:
                                            if (cachedHardwareConfig.IsTriggerChannelAnEnabledChannel(processingConfig.TriggerChannel))
                                            {
                                                var triggerChannelBuffer = postShuffleCh2_2;
                                                if (cachedHardwareConfig.DualChannelModeIsTriggerChannelInFirstPosition(processingConfig.TriggerChannel))
                                                    triggerChannelBuffer = postShuffleCh1_2;

                                                uint captureEndCount = 0;
                                                switch (processingConfig.TriggerType)
                                                {
                                                    case TriggerType.RisingEdge:
                                                        risingEdgeTrigger.ProcessSimd(input: triggerChannelBuffer, captureEndIndices: captureEndIndices, out captureEndCount);
                                                        break;
                                                    case TriggerType.FallingEdge:
                                                        fallingEdgeTrigger.ProcessSimd(input: triggerChannelBuffer, captureEndIndices: captureEndIndices, out captureEndCount);
                                                        break;
                                                }

                                                if (captureEndCount > 0)
                                                {
                                                    for (int i = 0; i < captureEndCount; i++)
                                                    {
                                                        var bridgeSpan = bridge.AcquiringRegionI8;
                                                        uint endOffset = (uint)triggerChannelBuffer.Length - captureEndIndices[i];
                                                        circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), endOffset);
                                                        circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), endOffset);
                                                        bridge.DataWritten(triggered: true);
                                                        bridge.SwitchRegionIfNeeded();

                                                        if (singleTriggerLatch)         // If this was a single trigger, reset the singleTrigger & runTrigger latches
                                                        {
                                                            singleTriggerLatch = false;
                                                            runMode = false;
                                                            break;
                                                        }
                                                    }
                                                    streamSampleCounter = 0;
                                                    autoTimeoutTimer.Restart();     // Restart the auto timeout as a normal trigger happened
                                                }
                                            }
                                            if (forceTriggerLatch) // This will always run, despite whether a trigger has happened or not (so from the user perspective, the UI might show one misaligned waveform during normal triggering; this is intended)
                                            {
                                                var bridgeSpan = bridge.AcquiringRegionI8;
                                                circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), 0);
                                                circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), 0);
                                                bridge.DataWritten(triggered: false);
                                                bridge.SwitchRegionIfNeeded();
                                                forceTriggerLatch = false;

                                                streamSampleCounter = 0;
                                                autoTimeoutTimer.Restart();     // Restart the auto timeout as a force trigger happened
                                            }
                                            if (processingConfig.TriggerMode == TriggerMode.Auto && autoTimeoutTimer.ElapsedMilliseconds > autoTimeout)
                                            {
                                                DualChannelStream(channelLength);
                                            }
                                            break;
                                        case TriggerMode.Stream:
                                            DualChannelStream(channelLength);
                                            break;
                                    }
                                }
                                break;
                            case AdcChannelMode.Quad:
                                // Shuffle
                                Shuffle.FourChannels(input: inputDataDto.Memory.SpanI8, output: shuffleBuffer);
                                // Finished with the memory, return it
                                inputChannel.Write(inputDataDto.Memory);
                                // Write to circular buffer
                                circularBuffer1.Write(postShuffleCh1_4);
                                circularBuffer2.Write(postShuffleCh2_4);
                                circularBuffer3.Write(postShuffleCh3_4);
                                circularBuffer4.Write(postShuffleCh4_4);
                                streamSampleCounter += postShuffleCh1_4.Length;
                                // Trigger
                                if (runMode)
                                {
                                    switch (processingConfig.TriggerMode)
                                    {
                                        case TriggerMode.Normal:
                                        case TriggerMode.Single:
                                        case TriggerMode.Auto:
                                            if (processingConfig.TriggerChannel != TriggerChannel.NotSet)
                                            {
                                                var triggerChannelBuffer = processingConfig.TriggerChannel switch
                                                {
                                                    TriggerChannel.Channel1 => postShuffleCh1_4,
                                                    TriggerChannel.Channel2 => postShuffleCh2_4,
                                                    TriggerChannel.Channel3 => postShuffleCh3_4,
                                                    TriggerChannel.Channel4 => postShuffleCh4_4,
                                                    _ => throw new ArgumentException("Invalid TriggerChannel value")
                                                };

                                                uint captureEndCount = 0;
                                                switch (processingConfig.TriggerType)
                                                {
                                                    case TriggerType.RisingEdge:
                                                        risingEdgeTrigger.ProcessSimd(input: triggerChannelBuffer, captureEndIndices: captureEndIndices, out captureEndCount);
                                                        break;
                                                    case TriggerType.FallingEdge:
                                                        fallingEdgeTrigger.ProcessSimd(input: triggerChannelBuffer, captureEndIndices: captureEndIndices, out captureEndCount);
                                                        break;
                                                }

                                                if (captureEndCount > 0)
                                                {
                                                    for (int i = 0; i < captureEndCount; i++)
                                                    {
                                                        var bridgeSpan = bridge.AcquiringRegionI8;
                                                        uint endOffset = (uint)triggerChannelBuffer.Length - captureEndIndices[i];
                                                        circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), endOffset);
                                                        circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), endOffset);
                                                        circularBuffer3.Read(bridgeSpan.Slice(channelLength + channelLength, channelLength), endOffset);
                                                        circularBuffer4.Read(bridgeSpan.Slice(channelLength + channelLength + channelLength, channelLength), endOffset);
                                                        bridge.DataWritten(triggered: true);
                                                        bridge.SwitchRegionIfNeeded();

                                                        if (singleTriggerLatch)         // If this was a single trigger, reset the singleTrigger & runTrigger latches
                                                        {
                                                            singleTriggerLatch = false;
                                                            runMode = false;
                                                            break;
                                                        }
                                                    }
                                                    streamSampleCounter = 0;
                                                    autoTimeoutTimer.Restart();     // Restart the auto timeout as a normal trigger happened
                                                }
                                            }
                                            if (forceTriggerLatch) // This will always run, despite whether a trigger has happened or not (so from the user perspective, the UI might show one misaligned waveform during normal triggering; this is intended)
                                            {
                                                var bridgeSpan = bridge.AcquiringRegionI8;
                                                circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), 0);
                                                circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), 0);
                                                circularBuffer3.Read(bridgeSpan.Slice(channelLength + channelLength, channelLength), 0);
                                                circularBuffer4.Read(bridgeSpan.Slice(channelLength + channelLength + channelLength, channelLength), 0);
                                                bridge.DataWritten(triggered: false);
                                                bridge.SwitchRegionIfNeeded();
                                                forceTriggerLatch = false;

                                                streamSampleCounter = 0;
                                                autoTimeoutTimer.Restart();     // Restart the auto timeout as a force trigger happened
                                            }
                                            if (processingConfig.TriggerMode == TriggerMode.Auto && autoTimeoutTimer.ElapsedMilliseconds > autoTimeout)
                                            {
                                                QuadChannelStream(channelLength);
                                            }
                                            break;
                                        case TriggerMode.Stream:
                                            QuadChannelStream(channelLength);
                                            break;
                                    }
                                }
                                break;
                        }
                        bridge.SwitchRegionIfNeeded();      // This ensures the semaphore is serviced on every loop iteration
                        if (periodicUpdateTimer.ElapsedMilliseconds >= 10000)
                        {
                            var dequeueCount = totalDequeueCount - cachedTotalDequeueCount;
                            var totalAcquisitions = bridge.Monitoring.TotalAcquisitions - cachedTotalAcquisitions;
                            var missedAcquisitions = bridge.Monitoring.DroppedAcquisitions - cachedMissedAcquisitions;
                            var uiUpdates = totalAcquisitions - missedAcquisitions;

                            //logger.LogDebug($"Outstanding frames: {processChannel.PeekAvailable()}, dequeues/sec: {dequeueCount / periodicUpdateTimer.Elapsed.TotalSeconds:F2}, dequeue count: {totalDequeueCount}");
                            logger.LogDebug($"Bridge writes/sec: {totalAcquisitions / periodicUpdateTimer.Elapsed.TotalSeconds:F2}, Bridge reads/sec: {uiUpdates / periodicUpdateTimer.Elapsed.TotalSeconds:F2}, total: {bridge.Monitoring.TotalAcquisitions}, dropped: {bridge.Monitoring.DroppedAcquisitions}");
                            periodicUpdateTimer.Restart();

                            cachedTotalDequeueCount = totalDequeueCount;
                            cachedTotalAcquisitions = bridge.Monitoring.TotalAcquisitions;
                            cachedMissedAcquisitions = bridge.Monitoring.DroppedAcquisitions;
                        }
                    }
                }

                // Locally scoped methods for deduplication
                void UpdateTriggerHorizontalPosition()
                {
                    ulong femtosecondsPerSample = cachedHardwareConfig.AdcChannelMode switch
                    {
                        AdcChannelMode.Single => 1000000,         // 1 GSPS
                        AdcChannelMode.Dual => 1000000 * 2,     // 500 MSPS
                        AdcChannelMode.Quad => 1000000 * 4,    // 250 MSPS
                        _ => throw new NotImplementedException(),
                    };

                    var windowTriggerPosition = processingConfig.TriggerDelayFs / femtosecondsPerSample;
                    risingEdgeTrigger.SetHorizontal(processingConfig.CurrentChannelDataLength, windowTriggerPosition, processingConfig.TriggerHoldoff);
                    fallingEdgeTrigger.SetHorizontal(processingConfig.CurrentChannelDataLength, windowTriggerPosition, processingConfig.TriggerHoldoff);
                }

                void SingleChannelStream(int channelLength)
                {
                    while (streamSampleCounter > channelLength)
                    {
                        streamSampleCounter -= channelLength;
                        var bridgeSpan = bridge.AcquiringRegionI8;
                        circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), 0);        // TODO - work out if this should be zero?
                        bridge.DataWritten(triggered: false);
                        bridge.SwitchRegionIfNeeded();
                    }
                }

                void DualChannelStream(int channelLength)
                {
                    while (streamSampleCounter > channelLength)
                    {
                        streamSampleCounter -= channelLength;
                        var bridgeSpan = bridge.AcquiringRegionI8;
                        circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), 0);        // TODO - work out if this should be zero?
                        circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), 0);
                        bridge.DataWritten(triggered: false);
                        bridge.SwitchRegionIfNeeded();
                    }
                }

                void QuadChannelStream(int channelLength)
                {
                    while (streamSampleCounter > channelLength)
                    {
                        streamSampleCounter -= channelLength;
                        var bridgeSpan = bridge.AcquiringRegionI8;
                        circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), 0);        // TODO - work out if this should be zero?
                        circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), 0);
                        circularBuffer3.Read(bridgeSpan.Slice(channelLength + channelLength, channelLength), 0);
                        circularBuffer4.Read(bridgeSpan.Slice(channelLength + channelLength + channelLength, channelLength), 0);
                        bridge.DataWritten(triggered: false);
                        bridge.SwitchRegionIfNeeded();
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
