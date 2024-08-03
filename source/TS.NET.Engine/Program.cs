using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using TS.NET;
using TS.NET.Engine;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // The aim is to have a thread-safe lock-free dataflow architecture (to prevent various classes of bugs).
        // The use of async/await for processing is avoided as the task thread pool is of little use here.
        //   Fire up threads to handle specific loops with extremely high utilisation. These threads are created once only, so the overhead of thread creation isn't important (one of the design goals of async/await).
        //   Optionally pin CPU cores to exclusively process a particular thread, perhaps with high/rt priority.
        //   Task.Factory.StartNew(() => Loop(...TaskCreationOptions.LongRunning) is just a shorthand for creating a new Thread to process a loop, the task thread pool isn't used.
        // The use of hardwareRequestChannel is to prevent 2 classes of bug: locking and thread safety.
        //   By serialising the config-update/data-read it also allows for specific behaviours (like pausing acquisition on certain config updates) and ensuring a perfect match between sample-block & hardware configuration that created it.

        Console.Title = "Engine";
        //using (Process p = Process.GetCurrentProcess())
        //    p.PriorityClass = ProcessPriorityClass.High;

        // To do: have something better than array index. Hardware serial?
        var deviceIndexOption = new Option<int>(name: "-i", description: "The ThunderScope to use if there are multiple connected to the host.", getDefaultValue: () => { return 0; });

        var configurationFilePathOption = new Option<string>(name: "-config", description: "Configuration file to use.", getDefaultValue: () => { return "thunderscope.yaml"; });

        var rootCommand = new RootCommand("TS.Net.Engine"){
            deviceIndexOption,
            configurationFilePathOption
        };

        rootCommand.SetHandler(Start, deviceIndexOption, configurationFilePathOption);
        return await rootCommand.InvokeAsync(args);
    }

    static void Start(int deviceIndex, string configurationFilePath)
    {
        //Console.WriteLine("Going to start thunderscope " + indexThunderscope + " revision " + hwRevision + " and SCPI server @ " + controlPort + ":" + dataPort);

#if DEBUG
        ThunderscopeSettings settings = ThunderscopeSettings.Default();
        var serializer = new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(
                YamlDotNet.Serialization.NamingConventions.PascalCaseNamingConvention.Instance
            )
            .Build();
        string yaml = serializer.Serialize(settings);
        File.WriteAllText("thunderscope (defaults).yaml", yaml);
#endif

        IConfigurationBuilder configurationBuilder = new ConfigurationBuilder().AddJsonFile(
            "appsettings.json"
        );
        var configuration = configurationBuilder.Build();
        var thunderscopeSettings = ThunderscopeSettings.FromYamlFile(configurationFilePath);

        var loggerFactory = LoggerFactory.Create(configure =>
        {
            configure
                .ClearProviders()
                .AddConfiguration(configuration.GetSection("Logging"))
                .AddFile(configuration.GetSection("Logging"))
                .AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                });
        });

        // Instantiate dataflow channels
        const int bufferLength = 120; // 120 = about 1 seconds worth of samples at 1GSPS (each ThunderscopeMemory is 8388608 bytes), 120x = 1006632960

        ThunderscopeMemoryRegion memoryRegion = new(bufferLength);
        BlockingChannel<ThunderscopeMemory> inputChannel = new(bufferLength);
        for (uint i = 0; i < bufferLength; i++)
            inputChannel.Writer.Write(memoryRegion.GetSegment(i));
        BlockingChannel<InputDataDto> processChannel = new();
        BlockingChannel<HardwareRequestDto> hardwareRequestChannel = new();
        BlockingChannel<HardwareResponseDto> hardwareResponseChannel = new();
        BlockingChannel<ProcessingRequestDto> processingRequestChannel = new();
        BlockingChannel<ProcessingResponseDto> processingResponseChannel = new();

        Thread.Sleep(1000);

        IThunderscope thunderscope;
        switch (thunderscopeSettings.HardwareDriver.ToLower())
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
                    if (deviceIndex > devices.Count - 1)
                        throw new Exception($"Invalid thunderscope index ({deviceIndex}). Only {devices.Count} Thunderscopes connected.");
                    var ts = new TS.NET.Driver.XMDA.Thunderscope(loggerFactory);
                    ts.Open(devices[deviceIndex], thunderscopeSettings.Calibration.ToDriver(), thunderscopeSettings.HardwareRevision);
                    thunderscope = ts;
                    break;
                }
            case "litex":
                {
                    var ts = new TS.NET.Driver.LiteX.Thunderscope();
                    ts.Open(0, thunderscopeSettings.Calibration.ToDriver());
                    thunderscope = ts;
                    break;
                }
            default:
                throw new ArgumentException($"{thunderscopeSettings.HardwareDriver} driver not supported");
        }

        string bridgeNamespace = $"ThunderScope.{deviceIndex}";

        // Start threads
        SemaphoreSlim startSemaphore = new(1);

        startSemaphore.Wait();
        ProcessingThread processingThread = new(loggerFactory, thunderscopeSettings, processChannel.Reader, inputChannel.Writer, processingRequestChannel.Reader, processingResponseChannel.Writer, bridgeNamespace);
        processingThread.Start(startSemaphore);

        startSemaphore.Wait();
        HardwareThread hardwareThread = new(loggerFactory, thunderscopeSettings, thunderscope, inputChannel.Reader, processChannel.Writer, hardwareRequestChannel.Reader, hardwareResponseChannel.Writer);
        hardwareThread.Start(startSemaphore);

        startSemaphore.Wait();
        DataServer? dataServer = null;
        if (thunderscopeSettings.DataPortEnabled)
        {
            dataServer = new(loggerFactory, thunderscopeSettings, System.Net.IPAddress.Any, 5026, bridgeNamespace);
            dataServer.Start();
        }

        ScpiServer scpiServer = new(loggerFactory, thunderscopeSettings, System.Net.IPAddress.Any, 5025, hardwareRequestChannel.Writer, hardwareResponseChannel.Reader, processingRequestChannel.Writer, processingResponseChannel.Reader);
        scpiServer.Start();

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

        scpiServer.Stop();
        dataServer?.Stop();
        hardwareThread.Stop();
        processingThread.Stop();
    }
}
