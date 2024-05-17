using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TS.NET
{
    public static class ThunderscopeBridgeHeaderExtensions
    {
        // By-value return
        public unsafe static ThunderscopeChannel GetTriggerChannel(this ThunderscopeHardwareConfig configuration, TriggerChannel triggerChannel)
        {
            return triggerChannel switch
            {
                TriggerChannel.One => configuration.Channel1,
                TriggerChannel.Two => configuration.Channel2,
                TriggerChannel.Three => configuration.Channel3,
                TriggerChannel.Four => configuration.Channel4,
                _ => throw new NotImplementedException()
            };
        }

        // By-value return
        public static ThunderscopeChannel GetChannel(this ref ThunderscopeHardwareConfig configuration, int channelIndex)
        {
            // channelIndex is zero-indexed
            return channelIndex switch
            {
                0 => configuration.Channel1,
                1 => configuration.Channel2,
                2 => configuration.Channel3,
                3 => configuration.Channel4,
                _ => throw new ArgumentException("channel out of range")
            };
    }

        public static void SetChannel(this ref ThunderscopeHardwareConfig configuration, ref ThunderscopeChannel channel, int channelIndex)
        {
            // channelIndex is zero-indexed
            switch (channelIndex)
            {
                case 0:
                    configuration.Channel1 = channel;
                    break;
                case 1:
                    configuration.Channel2 = channel;
                    break;
                case 2:
                    configuration.Channel3 = channel;
                    break;
                case 3:
                    configuration.Channel4 = channel;
                    break;
                default:
                    throw new ArgumentException("channelIndex out of range");
            }
        }
    }
}
