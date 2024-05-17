using System.Runtime.InteropServices;

namespace TS.NET
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ThunderscopeHardwareConfig             // Idempotent so that UI doesn't have to store state and removes the possibility of config mismatch with multiple actors changing config (e.g. SCPI and Web UI)
    {
        public AdcChannelMode AdcChannelMode;           // The number of channels enabled on ADC. ADC has input mux, e.g. Channel1.Enabled and Channel4.Enabled could have AdcChannels of Two. Useful for UI to know this, in order to clamp maximum sample rate.
        // This is a bit different to "CurrentChannelCount" because it reflects the configuration of the hardware, not the configuration of the memory mapped file. For example: ADC could stream 4 channels, but UI only has 3 enabled.

        //[MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        //public ThunderscopeChannel* Channels;         // Commented out as requires unsafe context but maybe switch to it later?
        public ThunderscopeChannel Channel1;
        public ThunderscopeChannel Channel2;
        public ThunderscopeChannel Channel3;
        public ThunderscopeChannel Channel4;
    }
}
