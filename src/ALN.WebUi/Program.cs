using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A static file host, and nothing else. It serves the UI, tells the page where the API lives, and
// stops there: no proxy, no token checking, no cached copy of core state. Anything that answered a
// question in this process would be a second implementation of the core, drifting quietly.

var port = 5200;
var apiBase = "http://127.0.0.1:5199";
string? contentRoot = null;
var listenAll = false;

for (var index = 0; index < args.Length; index++)
{
    var current = args[index];
    string? Next()
    {
        if (index + 1 >= args.Length) return null;
        return args[++index];
    }

    switch (current)
    {
        case "--port":
            var portText = Next();
            if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
            {
                Console.Error.WriteLine($"--port needs a number between 1 and 65535, got '{portText}'.");
                return 2;
            }
            break;
        case "--api":
            var apiText = Next();
            if (!Uri.TryCreate(apiText, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                Console.Error.WriteLine($"--api needs an absolute http(s) URL, got '{apiText}'.");
                return 2;
            }
            apiBase = parsed.GetLeftPart(UriPartial.Authority);
            break;
        case "--root":
            contentRoot = Next();
            if (string.IsNullOrWhiteSpace(contentRoot))
            {
                Console.Error.WriteLine("--root needs a directory.");
                return 2;
            }
            break;
        // Loopback by default: the page keeps a bearer token in the tab, so publishing it on every
        // interface is a decision somebody has to make on purpose.
        case "--listen-all":
            listenAll = true;
            break;
        case "-h":
        case "--help":
            Console.WriteLine(Usage());
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument '{current}'.");
            Console.Error.WriteLine(Usage());
            return 2;
    }
}

// What to serve: an explicit --root, otherwise the copy that was placed next to the assembly. The
// copy is deliberate rather than "find the sources": the running process serves exactly what was
// built, and live editing is an explicit choice (run-webui.cmd passes --root for that).
var webRoot = contentRoot is not null
    ? Path.GetFullPath(contentRoot)
    : Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!File.Exists(Path.Combine(webRoot, "index.html")))
{
    Console.Error.WriteLine($"No index.html in '{webRoot}'. Pass --root <dir> to serve another directory.");
    return 1;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = webRoot
});
builder.Logging.ClearProviders();
builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey,
    listenAll ? $"http://0.0.0.0:{port}" : $"http://127.0.0.1:{port}");

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // No cache: an edited module that a reload doesn't pick up costs more time than a re-download.
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, must-revalidate";
    }
});

// The one dynamic answer: which API to talk to by default. The page still lets a human change it,
// because a UI that only works from the port it was started on is a UI you can't point at staging.
app.MapGet("/config.json", () => Results.Json(new { api = apiBase }));

Console.WriteLine($"WebUI on {(listenAll ? "0.0.0.0" : "127.0.0.1")}:{port} (files: {webRoot}); default API {apiBase}.");
if (!listenAll) Console.WriteLine($"Cross-origin calls require 'cors': [\"http://127.0.0.1:{port}\"] in api/server.json.");
await app.RunAsync();
return 0;

static string Usage() =>
    """
    ALN WebUI — serves the static front end.

      --port <n>      listen port, default 5200
      --api <url>     default API base handed to the page, default http://127.0.0.1:5199
      --root <dir>    serve files from here instead of the bundled wwwroot
      --listen-all    bind 0.0.0.0 instead of loopback
      --help          this text
    """;
