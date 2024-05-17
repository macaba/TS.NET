namespace TS.NET
{
    public interface IThunderscope
    {
        void Start();
        void Stop();
        ThunderscopeChannel GetChannel(int channelIndex);
        void SetChannel(ThunderscopeChannel channel, int channelIndex);
        void Read(ThunderscopeMemory data, CancellationToken cancellationToken);
        ThunderscopeHardwareConfig GetConfiguration();
    }
}
