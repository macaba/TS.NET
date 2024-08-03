using System.Runtime.InteropServices;

namespace TS.NET.Driver.LiteX
{
    internal static partial class Interop
    {
        private const string library = "tslitex";
        
        [StructLayout(LayoutKind.Sequential)]
        public struct tsChannelParam_t
        {
            public uint volt_scale_mV;
            public int volt_offset_mV;
            public uint bandwidth;
            public byte coupling;
            public byte term;
            public byte active;
            public byte reserved;
        }

        // [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        // public struct tsDeviceInfo_t
        // {
        //     uint deviceID;
        //     [MarshalAs (UnmanagedType.ByValTStr, SizeConst=256)]
        //     string devicePath;
        //     [MarshalAs (UnmanagedType.ByValTStr, SizeConst=256)]
        //     string identity;
        //     [MarshalAs (UnmanagedType.ByValTStr, SizeConst=256)]
        //     string serialNumber;
        // }

        
        [StructLayout(LayoutKind.Sequential)]
        public struct tsScopeState_t
        {
            public uint adc_sample_rate;
            public uint adc_sample_bits;
            public uint adc_sample_resolution;
            public uint adc_lost_buffer_count;
            public uint flags;
            public uint temp_c;
            public uint vcc_int;
            public uint vcc_aux;
            public uint vcc_bram;
        }

        [LibraryImport(library, EntryPoint = "thunderscopeOpen")]
        public static partial nint Open(uint devIndex);

        [LibraryImport(library, EntryPoint = "thunderscopeClose")]
        public static partial int Close(nint ts);

        // [LibraryImport(library, EntryPoint = "thunderscopeListDevices")]
        // public static partial int ListDevices(uint devIndex, ref tsDeviceInfo_t devInfo);

        [LibraryImport(library, EntryPoint = "thunderscopeChannelConfigGet")]
        public static partial int GetChannelConfig(nint ts, uint channel, ref tsChannelParam_t conf);
        
        [LibraryImport(library, EntryPoint = "thunderscopeChannelConfigSet")]
        public static partial int SetChannelConfig(nint ts, uint channel, ref tsChannelParam_t conf);

        [LibraryImport(library, EntryPoint = "thunderscopeStatusGet")]
        public static partial int GetStatus(nint ts, ref tsScopeState_t conf);

        [LibraryImport(library, EntryPoint = "thunderscopeSampleModeSet")]
        public static partial int SetSampleMode(nint ts, uint rate, uint resolution);

        [LibraryImport(library, EntryPoint = "thunderscopeCalibrationSet")]
        public static partial int SetCalibration(nint ts, uint channel, uint cal);

        [LibraryImport(library, EntryPoint = "thunderscopeDataEnable")]
        public static partial int DataEnable(nint ts, byte enable);

        [LibraryImport(library, EntryPoint = "thunderscopeRead")]
        public static unsafe partial int Read(nint ts, byte* buffer, uint len);
    }
}