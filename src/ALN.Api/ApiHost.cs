using System.Text;
using System.Text.Json;
using ALN.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ALN.Api;

/// <summary>
/// The only network surface. It is transport plus identity: it parses a token, resolves an Actor and
/// hands the call to <see cref="IControlPlane"/>. No business logic, no manager access, no redaction
/// rules live here — otherwise a second transport would eventually behave differently.
/// </summary>
public static class ApiHost
{
    /// <summary>Throws <see cref="DecoderFallbackException"/> on bytes that are not valid UTF-8.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task<ApiInstance> StartAsync(ApiSettings settings, IControlPlane plane,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // Urls come from configuration, not from code; the listener is loopback by default in the file.
        builder.WebHost.ConfigureKestrel(options => { });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Join(';', settings.Urls));
        // Cross-origin access is off unless configured: a browser front end runs on its own origin and
        // needs this, but every other caller does not.
        if (settings.Origins.Count > 0) builder.Services.AddCors();

        var app = builder.Build();

        // The policy is per-request and answers preflight before identity: a preflight request never
        // carries the Authorization header, so it can't be authenticated.
        if (settings.Origins.Count > 0)
        {
            app.UseCors(policy => policy
                .WithOrigins([.. settings.Origins])
                .AllowAnyHeader()
                .AllowAnyMethod());
        }

        // Bearer token -> Actor. Anonymous requests never reach an action.
        app.Use(async (context, next) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
            var actor = settings.Authenticate(token);
            if (actor is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized" }, cancellationToken);
                return;
            }
            context.Items["actor"] = actor;
            await next();
        });

        app.MapGet("/actions", (HttpContext context) =>
        {
            var actor = (Actor)context.Items["actor"]!;
            return Results.Json(new
            {
                actor = new { id = actor.Id, kind = actor.Kind.ToString() },
                actions = plane.Catalog.Select(a => new
                {
                    id = a.Id,
                    summary = a.Summary,
                    capability = a.Capability,
                    mutating = a.Mutating,
                    allowed = actor.Allows(a.Capability)
                })
            });
        });

        app.MapPost("/actions/{action}", async (string action, HttpContext context, CancellationToken ct) =>
        {
            var actor = (Actor)context.Items["actor"]!;
            JsonElement arguments = default;
            if (context.Request.ContentLength is > 0)
            {
                try
                {
                    // Decode strictly instead of letting JsonDocument tolerate bad bytes: an argument that
                    // is not UTF-8 would otherwise surface much later, inside whatever action first reads
                    // the string, as an unfixable-looking 502.
                    using var body = new MemoryStream();
                    await context.Request.Body.CopyToAsync(body, ct);
                    var text = StrictUtf8.GetString(body.ToArray());
                    arguments = JsonSerializer.Deserialize<JsonElement>(text);
                }
                catch (DecoderFallbackException)
                {
                    return Results.Json(new { error = "body_must_be_utf8" }, statusCode: StatusCodes.Status400BadRequest);
                }
                catch (JsonException)
                {
                    return Results.Json(new { error = "invalid_json" }, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            var result = await plane.InvokeAsync(actor, action, arguments, ct);
            if (result.Ok) return Results.Json(new { ok = true, data = result.Value });
            var status = result.Error?.Code switch
            {
                ErrorCode.Denied => StatusCodes.Status403Forbidden,
                ErrorCode.Invalid => StatusCodes.Status400BadRequest,
                ErrorCode.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status502BadGateway
            };
            return Results.Json(new { ok = false, code = result.Error?.Code.ToString(), error = result.Error?.Message },
                statusCode: status);
        });

        await app.StartAsync(cancellationToken);
        return new ApiInstance(app, settings.Urls, settings.Origins);
    }
}

/// <summary>Handle for shutting the listener down with the rest of the process.</summary>
public sealed class ApiInstance(WebApplication app, string[] urls, IReadOnlyList<string> origins) : IAsyncDisposable
{
    public string[] Urls { get; } = urls;

    public IReadOnlyList<string> Origins { get; } = origins;

    public async ValueTask DisposeAsync()
    {
        try { await app.StopAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { }
        await app.DisposeAsync();
    }
}
