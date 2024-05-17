using System.Runtime.InteropServices;

namespace TS.NET.Native.BridgeReader
{
    public class UnmanagedMethods
    {
        [UnmanagedCallersOnly(EntryPoint = "add")]
        public static int Add(int a, int b)
        {
            return a + b;
        }

        // Returns pointer to ThunderscopeBridgeReader, pass this into subsequent methods
        [UnmanagedCallersOnly(EntryPoint = "init")]
        public static IntPtr Init()
        {
            ThunderscopeDataBridgeReader bridgeReader = new("ThunderScope.1");

            GCHandle handle = GCHandle.Alloc(bridgeReader, GCHandleType.Pinned);
            return handle.AddrOfPinnedObject();
        }

        [UnmanagedCallersOnly(EntryPoint = "dispose")]
        public static void Dispose(IntPtr bridgeReaderPtr)
        {
            var handle = GCHandle.FromIntPtr(bridgeReaderPtr);
            if (handle.Target != null)
            {
                var bridgeReader = (ThunderscopeDataBridgeReader)handle.Target;
                bridgeReader.Dispose();
            }
            handle.Free();   // Free the managed object
        }

        // Returns true if new data is available, false if timeout
        [UnmanagedCallersOnly(EntryPoint = "requestAndWaitForData")]
        public static bool RequestAndWaitForData(IntPtr bridgeReaderPtr, int millisecondsTimeout)
        {
            var handle = GCHandle.FromIntPtr(bridgeReaderPtr);
            if (handle.Target != null)
            {
                var bridgeReader = (ThunderscopeDataBridgeReader)handle.Target;
                return bridgeReader.RequestAndWaitForData(millisecondsTimeout);
            }
            return false;
        }

        // Returns the length of the data
        [UnmanagedCallersOnly(EntryPoint = "getDataLength")]
        public static int GetDataLength(IntPtr bridgeReaderPtr)
        {
            var handle = GCHandle.FromIntPtr(bridgeReaderPtr);
            if (handle.Target != null)
            {
                var bridgeReader = (ThunderscopeDataBridgeReader)handle.Target;
                var acquiredRegionLength = bridgeReader.AcquiredRegionI8.Length;
                return acquiredRegionLength;
            }
            return 0;
        }

        // Attempt at zero copy by allowing caller to get a pointer to read the memory region directly.
        // Returns a pointer to the memory location that contains the data.
        // GetDataLength should be used to get the allowable length. (hopefully this can be combined into a single method in future once UnmanagedCallersOnly behaviour is understood better)
        [UnmanagedCallersOnly(EntryPoint = "getDataPointer")]
        public static unsafe byte* GetDataPointer(IntPtr bridgeReaderPtr)
        {
            var handle = GCHandle.FromIntPtr(bridgeReaderPtr);
            if (handle.Target != null)
            {
                var bridgeReader = (ThunderscopeDataBridgeReader)handle.Target;
                return bridgeReader.GetAcquiredRegionPointer();
            }
            return default;
        }
    }
}