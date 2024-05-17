using System.Runtime.InteropServices;

namespace TS.NET
{
    // Ensure this is blitable (i.e. don't use bool)
    // Pack of 1 = No padding.

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ThunderscopeControlBridgeHeader
    {
        internal byte Version;          // Allows UI to know which ThunderscopeControlBridgeHeader version to use, hence the size of the header.

        // ===== Set once from config file or hard coded =====
        internal ushort MaxChannelCount;
        internal uint MaxChannelDataLength;
        internal byte MaxChannelDataByteCount;
        // ===================================================

        internal ThunderscopeHardwareConfig Hardware;       // Read only from UI perspective, UI uses SCPI interface to change configuration
        internal ThunderscopeProcessingConfig Processing;   // Read only from UI perspective, UI uses SCPI interface to change configuration
    }
}
