using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TS.NET.Memory.Unix;
using TS.NET.Memory.Windows;

namespace TS.NET
{
    // This is a shared memory-mapped file between processes, with only a single writer and a single reader with a header struct
    // Not thread safe
    public class ThunderscopeDataBridgeWriter : IDisposable
    {
        private readonly IMemoryFile file;
        private readonly MemoryMappedViewAccessor view;
        private unsafe byte* basePointer;
        private unsafe byte* dataPointer { get; }
        private ThunderscopeDataBridgeHeader header;
        private readonly IInterprocessSemaphoreWaiter dataRequestSemaphore;         // When this is signalled, a consumer (UI or intermediary) has requested data.
        private readonly IInterprocessSemaphoreReleaser dataResponseSemaphore;      // When data has been gathered, this is signalled to the consumer to indicate they can consume data.
        private bool firstRun = true;           // Data bridge writer will always write an initial waveform, to unblock a UI that was running before Engine was started
        private bool dataRequested = false;
        private bool acquiringRegionFilled = false;

        public Span<sbyte> AcquiringRegion { get { return GetAcquiringRegion(); } }
        public ThunderscopeDataMonitoring Monitoring { get { return header.Monitoring; } }

        public unsafe ThunderscopeDataBridgeWriter(string memoryName, ushort maxChannelCount, uint maxChannelDataLength, byte maxChannelDataByteCount)
        {
            var dataCapacityBytes = maxChannelCount * maxChannelDataLength * maxChannelDataByteCount * 2;   // * 2 as there are 2 regions used in tick-tock fashion
            var bridgeCapacityBytes = (ulong)sizeof(ThunderscopeDataBridgeHeader) + dataCapacityBytes;
            //Console.WriteLine($"Bridge capacity: {bridgeCapacityBytes} bytes");
            file = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new MemoryFileWindows(memoryName, bridgeCapacityBytes)
                : new MemoryFileUnix(memoryName, bridgeCapacityBytes);

            try
            {
                view = file.MappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                try
                {
                    basePointer = GetPointer();
                    dataPointer = basePointer + sizeof(ThunderscopeDataBridgeHeader);

                    // Writer sets initial state of header
                    header.Version = 1;
                    header.DataCapacityBytes = dataCapacityBytes;

                    header.MaxChannelCount = maxChannelCount;
                    header.MaxChannelDataLength = maxChannelDataLength;
                    header.MaxChannelDataByteCount = maxChannelDataByteCount;

                    header.AcquiringRegion = ThunderscopeMemoryAcquiringRegion.RegionA;

                    SetHeader();

                    dataRequestSemaphore = InterprocessSemaphore.CreateWaiter(memoryName + ".DataRequest", 0);
                    dataResponseSemaphore = InterprocessSemaphore.CreateReleaser(memoryName + ".DataResponse", 0);
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

        public ThunderscopeHardwareConfig Hardware
        {
            set
            {
                // This is a shallow copy, but considering the struct should be 100% blitable (i.e. no reference types), this is effectively a full copy
                header.Hardware = value;
                SetHeader();
            }
        }

        public ThunderscopeProcessingConfig Processing
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
                dataRequested = dataRequestSemaphore.Wait(0);           // Only wait on the semaphore once and cache the result if true, clearing when needed later
            if ((firstRun || dataRequested) && acquiringRegionFilled)   // UI has requested data and there is data available to be read...
            {
                firstRun = false;
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
            int regionLength = (int)(header.Processing.CurrentChannelCount * header.Processing.CurrentChannelDataLength * header.Processing.CurrentChannelDataByteCount);
            return header.AcquiringRegion switch
            {
                ThunderscopeMemoryAcquiringRegion.RegionA => new Span<sbyte>(dataPointer, regionLength),
                ThunderscopeMemoryAcquiringRegion.RegionB => new Span<sbyte>(dataPointer + regionLength, regionLength),
                _ => throw new InvalidDataException("Enum value not handled, add enum value to switch")
            };
        }
    }
}
