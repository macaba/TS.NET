﻿using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TS.NET.Memory.Unix;
using TS.NET.Memory.Windows;

namespace TS.NET
{
    // This is a shared memory-mapped file between processes, with only a single writer and a single reader with a header struct
    // Not thread safe
    public class ThunderscopeBridgeWriter : IDisposable
    {
        private readonly IMemoryFile file;
        private readonly MemoryMappedViewAccessor view;
        private unsafe byte* basePointer;
        private unsafe byte* dataPointer { get; }
        private ThunderscopeBridgeHeader header;
        private readonly IInterprocessSemaphoreWaiter dataRequestSemaphore;         // When this is signalled, a consumer (UI or intermediary) has requested data.
        private readonly IInterprocessSemaphoreReleaser dataResponseSemaphore;      // When data has been gathered, this is signalled to the consumer to indicate they can consume data.
        private bool dataRequested = false;
        private bool acquiringRegionFilled = false;

        public Span<sbyte> AcquiringRegion { get { return GetAcquiringRegion(); } }
        public ThunderscopeMonitoring Monitoring { get { return header.Monitoring; } }

        public unsafe ThunderscopeBridgeWriter(string memoryName, byte maxChannelCount, ulong maxChannelBytes)
        {
            var dataCapacityBytes = maxChannelCount * maxChannelBytes * 2;      // * 2 as there are 2 regions used in tick-tock fashion
            var bridgeCapacityBytes = (ulong)sizeof(ThunderscopeBridgeHeader) + dataCapacityBytes;
            file = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new MemoryFileWindows(memoryName, bridgeCapacityBytes)
                : new MemoryFileUnix(memoryName, bridgeCapacityBytes, System.IO.Path.GetTempPath());

            try
            {
                view = file.MappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                try
                {
                    basePointer = GetPointer();
                    dataPointer = basePointer + sizeof(ThunderscopeBridgeHeader);

                    // Writer sets initial state of header
                    header.Version = 1;
                    header.DataCapacityBytes = dataCapacityBytes;
                    header.MaxChannelCount = maxChannelCount;
                    header.MaxChannelBytes = maxChannelBytes;
                    header.AcquiringRegion = ThunderscopeMemoryAcquiringRegion.RegionA;
                    SetHeader();
                    dataRequestSemaphore = InterprocessSemaphore.CreateWaiter(memoryName + ".DataRequest");
#if DEBUG
                    ((IInterprocessSemaphoreReleaser)dataRequestSemaphore).Release();       // Always trigger the bridge once. This allows for the Engine to restart without restarting TS.NET.UI.Avalonia.
#endif
                    dataResponseSemaphore = InterprocessSemaphore.CreateReleaser(memoryName + ".DataResponse");
                }
                catch
                {
                    view.Dispose();
                    throw;
                }
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            view.Flush();
            view.Dispose();
            file.Dispose();
        }

        public ThunderscopeConfiguration Configuration
        {
            set
            {
                // This is a shallow copy, but considering the struct should be 100% blitable (i.e. no reference types), this is effectively a full copy
                header.Configuration = value;
                SetHeader();
            }
        }

        public ThunderscopeProcessing Processing
        {
            set
            {
                // This is a shallow copy, but considering the struct should be 100% blitable (i.e. no reference types), this is effectively a full copy
                header.Processing = value;
                SetHeader();
            }
        }

        public void MonitoringReset()
        {
            header.Monitoring.TotalAcquisitions = 0;
            header.Monitoring.MissedAcquisitions = 0;
            SetHeader();
        }

        public void SwitchRegionIfNeeded()
        {
            if (!dataRequested)
                dataRequested = dataRequestSemaphore.Wait(0);       // Only wait on the semaphore once and cache the result, clearing when needed later
            if (dataRequested && acquiringRegionFilled)             // UI has requested data and there is data available to be read...
            {
                dataRequested = false;
                acquiringRegionFilled = false;
                header.AcquiringRegion = header.AcquiringRegion switch
                {
                    ThunderscopeMemoryAcquiringRegion.RegionA => ThunderscopeMemoryAcquiringRegion.RegionB,
                    ThunderscopeMemoryAcquiringRegion.RegionB => ThunderscopeMemoryAcquiringRegion.RegionA,
                    _ => throw new InvalidDataException("Enum value not handled, add enum value to switch")
                };
                SetHeader();
                dataResponseSemaphore.Release();        // Allow UI to use the acquired region
            }
        }

        public void DataWritten()
        {
            header.Monitoring.TotalAcquisitions++;
            if (acquiringRegionFilled)
                header.Monitoring.MissedAcquisitions++;
            acquiringRegionFilled = true;
            SetHeader();
        }

        //private void GetHeader()
        //{
        //    unsafe { Unsafe.Copy(ref header, basePointer); }
        //}

        private void SetHeader()
        {
            unsafe { Unsafe.Copy(basePointer, ref header); }
        }

        private unsafe byte* GetPointer()
        {
            byte* ptr = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            if (ptr == null)
                throw new InvalidOperationException("Failed to acquire a pointer to the memory mapped file view.");

            return ptr;
        }

        private unsafe Span<sbyte> GetAcquiringRegion()
        {
            int regionLength = (int)(header.Processing.CurrentChannelCount * header.Processing.CurrentChannelBytes);
            return header.AcquiringRegion switch
            {
                ThunderscopeMemoryAcquiringRegion.RegionA => new Span<sbyte>(dataPointer, regionLength),
                ThunderscopeMemoryAcquiringRegion.RegionB => new Span<sbyte>(dataPointer + regionLength, regionLength),
                _ => throw new InvalidDataException("Enum value not handled, add enum value to switch")
            };
        }
    }
}
