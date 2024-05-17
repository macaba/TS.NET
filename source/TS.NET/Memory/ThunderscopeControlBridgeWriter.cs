using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using TS.NET.Memory.Unix;
using TS.NET.Memory.Windows;

namespace TS.NET
{
    // This is a shared memory-mapped file between processes, with only a single writer and a single reader with a header struct
    // Not thread safe
    public class ThunderscopeControlBridgeWriter : IDisposable
    {
        private readonly IMemoryFile file;
        private readonly MemoryMappedViewAccessor view;
        private unsafe byte* basePointer;
        private ThunderscopeControlBridgeHeader header;
        private bool IsHeaderSet { get { GetHeader(); return header.Version != 0; } }
        private readonly IInterprocessSemaphoreReleaser controlRequestSemaphore;
        private readonly IInterprocessSemaphoreWaiter controlResponseSemaphore;

        public unsafe ThunderscopeControlBridgeWriter(string memoryName)
        {
            if (OperatingSystem.IsWindows())
            {
                while (!MemoryFileWindows.Exists(memoryName))
                {
                    Console.WriteLine("Waiting for Thunderscope control bridge reader to create MMF...");
                    Thread.Sleep(1000);
                }

                file = new MemoryFileWindows(memoryName);
            }
            else
            {
                while (!MemoryFileUnix.Exists(memoryName))
                {
                    Console.WriteLine("Waiting for Thunderscope control bridge reader to create MMF...");
                    Thread.Sleep(1000);
                }

                file = new MemoryFileUnix(memoryName, (ulong)sizeof(ThunderscopeControlBridgeHeader));
            }

            try
            {
                view = file.MappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                try
                {
                    basePointer = GetPointer();

                    while (!IsHeaderSet)
                    {
                        Console.WriteLine("Waiting for Thunderscope control bridge reader to set header...");
                        Thread.Sleep(1000);
                    }
                    GetHeader();
                    controlRequestSemaphore = InterprocessSemaphore.CreateReleaser(memoryName + ".ControlRequest", 0);
                    controlResponseSemaphore = InterprocessSemaphore.CreateWaiter(memoryName + ".ControlResponse", 0);
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
