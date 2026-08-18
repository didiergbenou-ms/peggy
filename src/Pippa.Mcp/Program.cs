using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pippa.Mcp;

var builder = Host.CreateApplicationBuilder(args);

// MCP speaks JSON-RPC over stdout. Route all logs to stderr so they never
// corrupt the protocol stream.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<IMessageProvider, MockMessageProvider>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
