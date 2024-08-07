﻿using YamlDotNet.Serialization;

namespace TS.NET
{
    [YamlSerializable]
    public class ThunderscopeCalibrationSettings
    {
        public ThunderscopeChannelCalibrationSettings Channel1 { get; set; }
        public ThunderscopeChannelCalibrationSettings Channel2 { get; set; }
        public ThunderscopeChannelCalibrationSettings Channel3 { get; set; }
        public ThunderscopeChannelCalibrationSettings Channel4 { get; set; }

        public static ThunderscopeCalibrationSettings Default()
        {
            return new ThunderscopeCalibrationSettings()
            {
                Channel1 = ThunderscopeChannelCalibrationSettings.Default(),
                Channel2 = ThunderscopeChannelCalibrationSettings.Default(),
                Channel3 = ThunderscopeChannelCalibrationSettings.Default(),
                Channel4 = ThunderscopeChannelCalibrationSettings.Default(),
            };
        }
    }

    // This has the potential to be a complex structure with per-PGA-gain-setting gain & offset, etc. Bodge for now.
    [YamlSerializable]
    public class ThunderscopeChannelCalibrationSettings
    {
        public double AttenuatorGainHighZ { get; set; }
        public double AttenuatorGainFiftyOhm { get; set; }
        public double BufferGain { get; set; }
        public double PgaPreampLowGain { get; set; }
        public double PgaPreampHighGain { get; set; }
        public double PgaAttenuatorGain0 { get; set; }
        public double PgaAttenuatorGain1 { get; set; }
        public double PgaAttenuatorGain2 { get; set; }
        public double PgaAttenuatorGain3 { get; set; }
        public double PgaAttenuatorGain4 { get; set; }
        public double PgaAttenuatorGain5 { get; set; }
        public double PgaAttenuatorGain6 { get; set; }
        public double PgaAttenuatorGain7 { get; set; }
        public double PgaAttenuatorGain8 { get; set; }
        public double PgaAttenuatorGain9 { get; set; }
        public double PgaAttenuatorGain10 { get; set; }
        public double PgaOutputAmpGain { get; set; }
        public double HardwareOffsetVoltageLowGain { get; set; }
        public double HardwareOffsetVoltageHighGain { get; set; }

        public ThunderscopeChannelCalibration ToDriver()
        {
            return new ThunderscopeChannelCalibration()
            {
                AttenuatorGainHighZ = this.AttenuatorGainHighZ,
                AttenuatorGainFiftyOhm = this.AttenuatorGainFiftyOhm,
                BufferGain = this.BufferGain,
                PgaPreampLowGain = this.PgaPreampLowGain,
                PgaPreampHighGain = this.PgaPreampHighGain,
                PgaAttenuatorGain0 = this.PgaAttenuatorGain0,
                PgaAttenuatorGain1 = this.PgaAttenuatorGain1,
                PgaAttenuatorGain2 = this.PgaAttenuatorGain2,
                PgaAttenuatorGain3 = this.PgaAttenuatorGain3,
                PgaAttenuatorGain4 = this.PgaAttenuatorGain4,
                PgaAttenuatorGain5 = this.PgaAttenuatorGain5,
                PgaAttenuatorGain6 = this.PgaAttenuatorGain6,
                PgaAttenuatorGain7 = this.PgaAttenuatorGain7,
                PgaAttenuatorGain8 = this.PgaAttenuatorGain8,
                PgaAttenuatorGain9 = this.PgaAttenuatorGain9,
                PgaAttenuatorGain10 = this.PgaAttenuatorGain10,
                PgaOutputAmpGain = this.PgaOutputAmpGain,
                HardwareOffsetVoltageLowGain = this.HardwareOffsetVoltageLowGain,
                HardwareOffsetVoltageHighGain = this.HardwareOffsetVoltageHighGain
            };
        }

        public static ThunderscopeChannelCalibrationSettings Default()
        {
            return new ThunderscopeChannelCalibrationSettings()
            {
                AttenuatorGainHighZ = -33.9794,
                AttenuatorGainFiftyOhm = -13.9794,
                BufferGain = 0,
                PgaPreampLowGain = 10,
                PgaPreampHighGain = 30,
                PgaAttenuatorGain0 = 0,
                PgaAttenuatorGain1 = -2,
                PgaAttenuatorGain2 = -4,
                PgaAttenuatorGain3 = -6,
                PgaAttenuatorGain4 = -8,
                PgaAttenuatorGain5 = -10,
                PgaAttenuatorGain6 = -12,
                PgaAttenuatorGain7 = -14,
                PgaAttenuatorGain8 = -16,
                PgaAttenuatorGain9 = -18,
                PgaAttenuatorGain10 = -20,
                PgaOutputAmpGain = 8.86,
                HardwareOffsetVoltageLowGain = 2.525,
                HardwareOffsetVoltageHighGain = 2.525
            };
        }
    }
}
