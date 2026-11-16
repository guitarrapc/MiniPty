#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable

// Serve: previews the playground site on this machine as GitHub Pages serves it, files only.
//
//   dotnet playground/Serve.cs [--port 5180]
//
// The page fetches its recordings, which a file:// page cannot, so it needs an HTTP server even locally. Record first
// with Record.cs, or the page has nothing to play. Listens on loopback only.

using Microsoft.Extensions.FileProviders;

var port = args is ["--port", var value] && int.TryParse(value, out var parsed) ? parsed : 5180;
var playgroundDir = AppContext.GetData("EntryPointFileDirectoryPath") as string
    ?? Path.Combine(Directory.GetCurrentDirectory(), "playground");
var site = Path.Combine(playgroundDir, "site");

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.ClearProviders();
var app = builder.Build();

var files = new PhysicalFileProvider(site);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = files,
    // A new recording or an edited page shows on reload.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-store",
});

Console.WriteLine($"Serving {site} at http://127.0.0.1:{port}/");
app.Run();
