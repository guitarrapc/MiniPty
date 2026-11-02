#:sdk Aspire.AppHost.Sdk@13.6.4

// AspireTerminal: a shell in the browser (terminal.cs) under an Aspire AppHost, so the Aspire dashboard charts MiniPty's
// meter: open sessions, start time and failures, output read, and stalls on a full output buffer.
//
//   dotnet run samples/AspireTerminal/apphost.cs
//
// Open the dashboard address the AppHost prints, then the "terminal" link on the Resources page; it carries this run's
// access token. Flood the terminal (seq 1 2000000, or dir /s C:\Windows), or press Start on the "load" resource, and
// open Metrics > terminal > MiniPty.

#pragma warning disable ASPIRECSHARPAPPS001 // AddCSharpApp for a file-based app is experimental in Aspire 13.

using System.Security.Cryptography;

var builder = DistributedApplication.CreateBuilder(args);

// Whoever holds this token can type into a shell, so it is made here for this run only and reaches the browser in the
// URL fragment of the dashboard's link, which browsers never send to a server (see LocalAccess in terminal.cs).
var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

var terminal = builder.AddCSharpApp("terminal", "terminal.cs")
    // The app checks that a request's Host names its own port. Aspire's proxy would put another port in front of it.
    .WithHttpEndpoint(name: "http", isProxied: false)
    .WithEnvironment("MINIPTY_TERMINAL_TOKEN", token)
    // OpenTelemetry exports metrics once a minute by default; a second keeps the dashboard's charts live while a flood
    // runs. Started with dotnet run, this AppHost passed no interval of its own.
    .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "1000")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url += "#token=" + token;
        url.DisplayText = "Terminal";
    });

// Browser-like clients that open and close sessions and flood one through a slow reader, so the charts move without
// anyone typing. It starts only when Start is pressed on the dashboard.
builder.AddCSharpApp("load", "load.cs")
    .WithEnvironment("TERMINAL_URL", terminal.GetEndpoint("http"))
    .WithEnvironment("MINIPTY_TERMINAL_TOKEN", token)
    .WithParentRelationship(terminal)
    .WaitFor(terminal)
    .WithExplicitStart();

builder.Build().Run();
