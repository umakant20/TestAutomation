using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// IMPORTANT: MCP over stdio uses stdout exclusively for the JSON-RPC protocol messages
// exchanged with Copilot. Any stray Console.WriteLine (from your own code, a library, or
// this host) would corrupt the protocol stream. All logging is redirected to stderr instead.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
