using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using KomandioLabs.WpfInspector.Mcp;

Win32Api.EnsureDpiAwareness();

var builder = Host.CreateApplicationBuilder(args);

// stdout is reserved exclusively for MCP protocol messages.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<InspectorTools>();

using var host = builder.Build();
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(InspectorTools.EndAllInspections);
AppDomain.CurrentDomain.ProcessExit += (_, _) => InspectorTools.EndAllInspections();
await host.RunAsync();
