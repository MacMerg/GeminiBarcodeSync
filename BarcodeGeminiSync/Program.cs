using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MacMerg.BarcodeGeminiSync;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("logs\\barcodesync-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .CreateLogger();

try
{
    Log.Information("Starting host");

    IHost host = Host.CreateDefaultBuilder(args)
        .UseSerilog() // Attach Serilog to the generic host
        .ConfigureServices(services =>
        {
            // 1. THIS IS THE CRITICAL LINE
            services.AddHttpClient("gemini", c =>
            {
                c.Timeout = TimeSpan.FromMinutes(5); // increase to 5 minutes
            }); 
            
            // 2. Register your worker
            services.AddHostedService<BarcodeWorker>();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
