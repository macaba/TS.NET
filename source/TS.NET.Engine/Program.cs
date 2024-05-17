using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TS.NET;
using TS.NET.Engine;

// The aim is to have a thread-safe lock-free dataflow architecture (to prevent various classes of bugs).
// The use of async/await for processing is avoided as the task thread pool is of little use here.
//   Fire up threads to handle specific loops with extremely high utilisation. These threads are created once only, so the overhead of thread creation isn't important (one of the design goals of async/await).
//   Future work might pin CPU cores to exclusively process a particular thread, perhaps with high/rt priority.
//   Task.Factory.StartNew(() => Loop(...TaskCreationOptions.LongRunning) is just a shorthand for creating a new Thread to process a loop, the task thread pool isn't used. 
// The use of hardwareRequestChannel is to prevent 2 classes of bug: locking and thread safety.
//   By serialising the config-update/data-read it also allows for specific behaviours (like pausing acquisition on certain config updates) and ensuring a perfect match between sample-block & hardware configuration that created it.

Console.Title = "Engine";
using (Process p = Process.GetCurrentProcess())
    p.PriorityClass = ProcessPriorityClass.High;

#if DEBUG
ThunderscopeSettings settings = ThunderscopeSettings.Default();
    string json = JsonSerializer.Serialize(settings, SourceGenerationContext.Default.ThunderscopeSettings);
    File.WriteAllText("thunderscope (defaults).json", json);
#endif

IConfigurationBuilder configurationBuilder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json");
var configuration = configurationBuilder.Build();
var thunderscopeSettings = ThunderscopeSettings.FromFile("thunderscope.json");

var loggerFactory = LoggerFactory.Create(configure =>
{
    configure
        .ClearProviders()
        .AddConfiguration(configuration.GetSection("Logging"))
        .AddFile(configuration.GetSection("Logging"))
        .AddSimpleConsole(options => { options.SingleLine = true; options.TimestampFormat = "HH:mm:ss "; });
});

// Instantiate dataflow channels
const int bufferLength = 120;       // 120 = about 1 seconds worth of samples at 1GSPS (each ThunderscopeMemory is 8388608 bytes), 120x = 1006632960
BlockingChannel<ThunderscopeMemory> inputChannel = new(bufferLength);
for (int i = 0; i < bufferLength; i++)
    inputChannel.Writer.Write(new ThunderscopeMemory());
BlockingChannel<InputDataDto> processingChannel = new();
BlockingChannel<HardwareRequestDto> hardwareRequestChannel = new();
BlockingChannel<HardwareResponseDto> hardwareResponseChannel = new();
BlockingChannel<ProcessingRequestDto> processingRequestChannel = new();
BlockingChannel<ProcessingResponseDto> processingResponseChannel = new();

Thread.Sleep(1000);

IThunderscope thunderscope;
switch (thunderscopeSettings.Driver.ToLower())
{
    case "simulator":
        {
            var ts = new TS.NET.Driver.Simulator.Thunderscope();
            thunderscope = ts;
            break;
        }
    case "xdma":
        {
            // Find thunderscope
            var devices = TS.NET.Driver.XMDA.Thunderscope.IterateDevices();
            if (devices.Count == 0)
                throw new Exception("No thunderscopes found");
            var ts = new TS.NET.Driver.XMDA.Thunderscope();
            ts.Open(devices[0], thunderscopeSettings.Calibration);
            thunderscope = ts;
            break;
        }
    default:
        throw new ArgumentException($"{thunderscopeSettings.Driver} driver not supported");
}

// Start threads
EngineTask processingThread = new(loggerFactory, thunderscope, thunderscopeSettings);
processingThread.Start();

bool loop = true;
while (loop)
{
    var key = Console.ReadKey();
    switch (key.Key)
    {
        case ConsoleKey.Escape:
            loop = false;
            break;
    }
}
processingThread.Stop();