using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using TS.NET.Memory.Unix;
using TS.NET.Memory.Windows;

namespace TS.NET
{
    // This is a shared memory-mapped file between processes, with only a single writer and a single reader with a header struct
    public class ThunderscopeDataBridgeReader : IDisposable
    {
        private readonly IMemoryFile file;
        private readonly MemoryMappedViewAccessor view;
        private readonly unsafe byte* basePointer;
        private readonly unsafe byte* dataPointer;
        private ThunderscopeDataBridgeHeader header;
        private bool IsHeaderSet { get { GetHeader(); return header.Version != 0; } }
        private readonly IInterprocessSemaphoreReleaser dataRequestSemaphore;           // When data is desired, this is signalled to the engine to gather data.
        private readonly IInterprocessSemaphoreWaiter dataResponseSemaphore;            // When this is signalled, data is ready to be consumed.
        private bool hasSignaledRequest = false;

        public ReadOnlySpan<sbyte> AcquiredRegionI8 { get { return GetAcquiredRegionI8(); } }
        public ReadOnlySpan<byte> AcquiredRegionU8 { get { return GetAcquiredRegionU8(); } }        // Useful for the Socket API which only accepts byte

        public unsafe ThunderscopeDataBridgeReader(string memoryName)
        {
            if (OperatingSystem.IsWindows())
            {
                while (!MemoryFileWindows.Exists(memoryName))
                {
                    Console.WriteLine("Waiting for Thunderscope data bridge writer to create MMF...");
                    Thread.Sleep(1000);
                }

                file = new MemoryFileWindows(memoryName);
            }
            else
            {
                while (!MemoryFileUnix.Exists(memoryName))
                {
                    Console.WriteLine("Waiting for Thunderscope data bridge writer to create MMF...");
                    Thread.Sleep(1000);
                }

                file = new MemoryFileUnix(memoryName, MemoryFileUnix.Size(memoryName));
            }

            try
            {
                view = file.MappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                try
                {
                    basePointer = GetPointer();
                    dataPointer = basePointer + sizeof(ThunderscopeDataBridgeHeader);

                    while (!IsHeaderSet)
                    {
                        Console.WriteLine("Waiting for Thunderscope data bridge writer to set header...");
                        Thread.Sleep(1000);
                    }
                    GetHeader();
                    //Console.WriteLine($"Bridge capacity: {(ulong)sizeof(ThunderscopeDataBridgeHeader) + header.DataCapacityBytes} bytes");
                    dataRequestSemaphore = InterprocessSemaphore.CreateReleaser(memoryName + ".DataRequest", 0);
                    dataResponseSemaphore = InterprocessSemaphore.CreateWaiter(memoryName + ".DataResponse", 0);
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
            get
            {
                GetHeader();
                return header.Hardware;
            }
        }

        public ThunderscopeProcessingConfig Processing
        {
            get
            {
                GetHeader();
                return header.Processing;
            }
        }

        public ThunderscopeDataMonitoring Monitoring
        {
            get
            {
                GetHeader();
                return header.Monitoring;
            }
        }

        public bool RequestAndWaitForData(int millisecondsTimeout)
        {
            // Firstly check if any data has already been loaded
            var existingData = dataResponseSemaphore.Wait(0);
            if (existingData)
                return true;

            if (!hasSignaledRequest)
            {
                // Only signal request once, or we will run up semaphore counter
                dataRequestSemaphore.Release();
                hasSignaledRequest = true;
            }

            bool responded = dataResponseSemaphore.Wait(millisecondsTimeout);

            if (responded)
            {
                // Now that the bridge has tick-tocked, the next request will be 'real'
                // TODO: Should this be a separate method, or part of GetPointer() ?
                hasSignaledRequest = false;
            }

            return responded;
        }

        private void GetHeader()
        {
            unsafe { Unsafe.Copy(ref header, basePointer); }
        }

        //private void SetHeader()
        //{
        //    unsafe { Unsafe.Copy(basePointer, ref header); }
        //}

        private unsafe byte* GetPointer()
        {
            byte* ptr = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            if (ptr == null)
                throw new InvalidOperationException("Failed to acquire a pointer to the memory mapped file view.");

            return ptr;
        }

        private unsafe ReadOnlySpan<sbyte> GetAcquiredRegionI8()
        {
            int regionLength = (int)(header.Processing.CurrentChannelCount * header.Processing.CurrentChannelDataLength * header.Processing.CurrentChannelDataByteCount);
            return header.AcquiringRegion switch
            {
                ThunderscopeMemoryAcquiringRegion.RegionA => new ReadOnlySpan<sbyte>(dataPointer + regionLength, regionLength),        // If acquiring region is Region A, return Region B
                ThunderscopeMemoryAcquiringRegion.RegionB => new ReadOnlySpan<sbyte>(dataPointer, regionLength),                       // If acquiring region is Region B, return Region A
                _ => throw new InvalidDataException("Enum value not handled, add enum value to switch")
            };
        }

        private unsafe ReadOnlySpan<byte> GetAcquiredRegionU8()
        {
            int regionLength = (int)(header.Processing.CurrentChannelCount * header.Processing.CurrentChannelDataLength * header.Processing.CurrentChannelDataByteCount);
            return header.AcquiringRegion switch
            {
                ThunderscopeMemoryAcquiringRegion.RegionA => new ReadOnlySpan<byte>(dataPointer + regionLength, regionLength),        // If acquiring region is Region A, return Region B
                ThunderscopeMemoryAcquiringRegion.RegionB => new ReadOnlySpan<byte>(dataPointer, regionLength),                       // If acquiring region is Region B, return Region A
                _ => throw new InvalidDataException("Enum value not handled, add enum value to switch")
            };
        }

        // For use by TS.NET.Native.BridgeReader only
        public unsafe byte* GetAcquiredRegionPointer()
        {
            int regionLength = (int)(header.Processing.CurrentChannelCount * header.Processing.CurrentChannelDataLength * header.Processing.CurrentChannelDataByteCount);
            return header.AcquiringRegion switch
            {
                ThunderscopeMemoryAcquiringRegion.RegionA => dataPointer + regionLength,        // If acquiring region is Region A, return Region B
                ThunderscopeMemoryAcquiringRegion.RegionB => dataPointer,                       // If acquiring region is Region B, return Region A
                _ => throw new InvalidDataException("Enum value not handled, add enum value to switch")
            };
        }
    }
}