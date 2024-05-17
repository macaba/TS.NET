using System.Runtime.InteropServices;

namespace TS.NET
{
    // Ensure this is blitable (i.e. don't use bool)
    // Pack of 1 = No padding.
    // There might be some benefit later to setting a fixed size (64?) for memory alignment if an aligned memorymappedfile can be created.

    // Example memory mapped file layout:
    // [Header Region][Data Region]
    // More detail:
    // [Header Region][Data Region: [Region A: [Channel 1][Channel 2][Channel 3][Channel 4]][Region B: [Channel 1][Channel 2][Channel 3][Channel 4]]]
    // 
    // If CurrentChannelBytes is less than MaxChannelBytes then the channel regions pack together without gaps, leaving spare bytes at the very end e.g. [Header Region][Data Region][Empty Region]
    // Similar logic if CurrentChannelCount is less than MaxChannelCount

    // DataCapacityBytes = MaxChannelCount * MaxChannelDataLength * MaxChannelDataByteWidth * 2
    // Multiply by 2 because there is an "AcquiringRegion" and an "AcquiredRegion" so the engine can be populating a region whilst the UI is using the other region. Standard memory swapping.

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ThunderscopeDataBridgeHeader
    {
        internal byte Version;              // Allows UI to know which ThunderscopeMemoryBridgeHeader version to use, hence the size of the header.
        internal ulong DataCapacityBytes;   // Size of data region (following the header)

        // ===== Set once from config file or hard coded =====
        internal ushort MaxChannelCount;    
        internal uint MaxChannelDataLength;
        internal byte MaxChannelDataByteCount;
        // ===================================================

        internal ThunderscopeMemoryAcquiringRegion AcquiringRegion; // Therefore 'AcquiredRegion' (to be used by UI) is the opposite

        internal ThunderscopeHardwareConfig Hardware;       // Read only from UI perspective, UI uses SCPI interface to change configuration
        internal ThunderscopeProcessingConfig Processing;   // Read only from UI perspective, UI uses SCPI interface to change configuration
        internal ThunderscopeDataMonitoring Monitoring;     // Read only from UI perspective, UI optionally displays these values
    }

    public enum ThunderscopeMemoryAcquiringRegion : byte
    {
        RegionA = 1,
        RegionB = 2
    }

    // Monitoring variables that reset when configuration variables change
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ThunderscopeDataMonitoring
    {
        public ulong TotalAcquisitions;         // All triggers
        public ulong MissedAcquisitions;        // Triggers that weren't displayed
    }
}