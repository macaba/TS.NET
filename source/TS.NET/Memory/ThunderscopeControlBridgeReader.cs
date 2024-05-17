using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TS.NET.Memory.Unix;
using TS.NET.Memory.Windows;

namespace TS.NET
{
    // This is a shared memory-mapped file between processes, with only a single writer and a single reader with a header struct
    public class ThunderscopeControlBridgeReader : IDisposable
    {
        private readonly IMemoryFile file;
        private readonly MemoryMappedViewAccessor view;
        private unsafe byte* basePointer;
        private ThunderscopeControlBridgeHeader header;
        private readonly IInterprocessSemaphoreWaiter controlRequestSemaphore;
        private readonly IInterprocessSemaphoreReleaser controlResponseSemaphore;

        public unsafe ThunderscopeControlBridgeReader(string memoryName, ushort maxChannelCount, uint maxChannelDataLength, byte maxChannelDataByteCount)
        {
            var bridgeCapacityBytes = (ulong)sizeof(ThunderscopeControlBridgeHeader);
            file = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new MemoryFileWindows(memoryName, bridgeCapacityBytes)
                : new MemoryFileUnix(memoryName, bridgeCapacityBytes);

            try
            {
                view = file.MappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                try
                {
                    basePointer = GetPointer();

                    // Writer sets initial state of header
                    header.Version = 1;

                    header.MaxChannelCount = maxChannelCount;
                    header.MaxChannelDataLength = maxChannelDataLength;
                    header.MaxChannelDataByteCount = maxChannelDataByteCount;

                    SetHeader();

                    controlRequestSemaphore = InterprocessSemaphore.CreateWaiter(memoryName + ".ControlRequest", 0);
                    controlResponseSemaphore = InterprocessSemaphore.CreateReleaser(memoryName + ".ControlResponse", 0);
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

        private void GetHeader()
        {
            unsafe { Unsafe.Copy(ref header, basePointer); }
        }

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
    }
}
