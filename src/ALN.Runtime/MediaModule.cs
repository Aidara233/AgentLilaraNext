using System.Text.Json;
using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// What a deployment decided about pictures: which formats may be shown, how big, how many, and whether an arriving
/// one is shown unasked.
///
/// A record with <c>with</c> rather than a bag of settable properties, because the only thing that changes these is the
/// module (from its file, or from a person tuning it at runtime) and it should change them all at once rather than one
/// field at a time while a request is being assembled.
/// </summary>
/// <summary>
/// The rules, as functions over an <see cref="MediaOptions"/>.
///
/// Split out from the module for one reason: a component that assembles a request needs the rules even when no media
/// module is on the graph (a test, or a host that has not configured one), and it must get the <b>same</b> rules. Two
/// implementations of "may this be shown" is how one of them ends up more permissive than the operator asked for.
/// </summary>
public static class MediaPolicy
{
    public static bool Allows(MediaOptions options, string mediaType) =>
        options.Allowed.Contains(mediaType, StringComparer.OrdinalIgnoreCase);

    public static MediaView Decide(MediaOptions options, int index, string mediaType, long bytes, int alreadyShown)
    {
        if (options.AutoView == MediaViewPolicy.Off)
            return MediaView.No("看图策略是 off，收到图片只登记不自动看");
        if (!Allows(options, mediaType))
            return MediaView.No($"{mediaType} 不在可自动看的格式里（{string.Join(" / ", options.Allowed)}）");
        if (bytes > options.MaxImageBytes)
            return MediaView.No($"{Describe(bytes)}，超过单图上限 {Describe(options.MaxImageBytes)}");
        if (options.AutoView == MediaViewPolicy.First && index > 0)
            return MediaView.No("一次到达多张，只自动看第一张");
        if (alreadyShown >= options.MaxAutoViewsPerTurn)
            return MediaView.No($"这一回合已经自动看了 {alreadyShown} 张（上限 {options.MaxAutoViewsPerTurn}）");
        return MediaView.Yes;
    }

    public static string Describe(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024.0):0.#} MB"
        : $"{bytes / 1024.0:0.#} KB";
}

/// <summary>
/// What a component uses when nobody handed it a policy: the shipped defaults, never "no ceiling".
///
/// The alternative — treating a missing policy as "no budget" — would make the size of a request depend on which
/// constructor a host happened to call, and the failure mode of that is a Session whose every request is refused
/// forever. A default that is merely conservative costs a picture being left out with a sentence saying so.
/// </summary>
public sealed class DefaultMediaPolicy : IMediaPolicy
{
    public static readonly DefaultMediaPolicy Instance = new();

    private static readonly MediaOptions Options = new();

    public MediaViewPolicy AutoView => Options.AutoView;

    public long MaxImageBytes => Options.MaxImageBytes;

    public int MaxAutoViewsPerTurn => Options.MaxAutoViewsPerTurn;

    public int MaxImagesPerRequest => Options.MaxImagesPerRequest;

    public long MaxImageBytesPerRequest => Options.MaxImageBytesPerRequest;

    public bool Allows(string mediaType) => MediaPolicy.Allows(Options, mediaType);

    public MediaView Decide(int index, string mediaType, long bytes, int alreadyShown) =>
        MediaPolicy.Decide(Options, index, mediaType, bytes, alreadyShown);
}

public sealed record MediaOptions
{    /// <summary>The four formats a vision model accepts nearly everywhere — and no SVG, no BMP, no TIFF (docs/28 §6).</summary>
    public IReadOnlyList<string> Allowed { get; init; } = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public MediaViewPolicy AutoView { get; init; } = MediaViewPolicy.First;

    /// <summary>Eight megabytes: a phone photo, a screenshot, a sticker — not a scan of a poster.</summary>
    public long MaxImageBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Six per request. Above that the request is mostly pictures and the conversation is gone.</summary>
    public int MaxImagesPerRequest { get; init; } = 6;

    /// <summary>Twenty-four megabytes per request: the count and the size have to bound each other.</summary>
    public long MaxImageBytesPerRequest { get; init; } = 24L * 1024 * 1024;

    /// <summary>Three unasked per turn. Nine screenshots become one picture and a sentence about the other eight.</summary>
    public int MaxAutoViewsPerTurn { get; init; } = 3;
}

/// <summary>
/// Media as a module: it owns the policy and nothing else.
///
/// It has no tools on purpose. Nothing about a picture is a verb the model needs: what arrived is already in its
/// context as a receipt, looking at one is <c>fs_read</c> (the file is right there under a scope it can read), and
/// sending one is the Channel's business. A module that offered <c>media_view</c> would be a second way to do what
/// <c>fs_read</c> already does, and the second way is the one that forgets the size check.
///
/// What it does own is the answer to "may this be shown, and how big may it be" — a deployment decision, which is what
/// a <c>module.json</c> is for (docs/18). It is also a parameter target, so those numbers can be changed while the
/// process runs: <c>media.local/autoView</c> is exactly the knob somebody wants at 3am without a restart.
/// </summary>
[Module(TypeId, Category = "media")]
public sealed class MediaModule(ModuleId id, IParameterRegistry? parameters) : IKernelModule, IMediaPolicy, IParameterTarget
{
    public const string TypeId = "media.local";

    /// <summary>
    /// What the host writes when no <c>module.json</c> exists yet. Every number here is a decision somebody can
    /// disagree with by editing one file: formats, sizes, counts, and the answer to "show me the picture or tell me
    /// about it".
    /// </summary>
    public const string DefaultConfig = """
        {"autoView":"first","allowed":["image/png","image/jpeg","image/gif","image/webp"],
         "maxImageBytes":8388608,"maxImagesPerRequest":6,"maxImageBytesPerRequest":25165824,"maxAutoViewsPerTurn":3}
        """;

    private MediaOptions _options = new();
    private bool _registered;

    public ModuleId Id { get; } = id;

    public MediaOptions Options => Volatile.Read(ref _options);

    // ---- IMediaPolicy -------------------------------------------------------------------------

    public MediaViewPolicy AutoView => Options.AutoView;

    public long MaxImageBytes => Options.MaxImageBytes;

    public int MaxAutoViewsPerTurn => Options.MaxAutoViewsPerTurn;

    public int MaxImagesPerRequest => Options.MaxImagesPerRequest;

    public long MaxImageBytesPerRequest => Options.MaxImageBytesPerRequest;

    public bool Allows(string mediaType) => MediaPolicy.Allows(Options, mediaType);

    public MediaView Decide(int index, string mediaType, long bytes, int alreadyShown) =>
        MediaPolicy.Decide(Options, index, mediaType, bytes, alreadyShown);

    private static string Describe(long bytes) => MediaPolicy.Describe(bytes);

    // ---- IParameterTarget ---------------------------------------------------------------------

    public string ParameterScope => TypeId;

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("autoView", ParameterKind.Text, nameof(MediaViewPolicy.First),
            "收到图片时自动给模型看多少：off 一张不看 / first 只看每批第一张 / all 都看（仍受次数与大小限制）。下一轮生效。",
            ParameterApply.Immediate),
        new("maxAutoViewsPerTurn", ParameterKind.Integer, "3",
            "一回合最多自动看几张图；超出的只留回执。下一轮生效。",
            ParameterApply.Immediate, Secret: false, Minimum: 0, Maximum: 100),
        new("maxImageBytes", ParameterKind.Integer, (8L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "单张图片能进请求的字节上限；更大的只留回执。下一轮生效。",
            ParameterApply.Immediate, Secret: false, Minimum: 1, Maximum: IMediaStore.MaxIngestBytes),
        new("maxImagesPerRequest", ParameterKind.Integer, "6",
            "一次请求最多带几张图（含模型自己要看的）。下一轮生效。",
            ParameterApply.Immediate, Secret: false, Minimum: 0, Maximum: 100),
        new("maxImageBytesPerRequest", ParameterKind.Integer, (24L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "一次请求图片总字节上限，从最旧的开始降级成文字。下一轮生效。",
            ParameterApply.Immediate, Secret: false, Minimum: 0, Maximum: IMediaStore.MaxIngestBytes * 4),
        new("allowed", ParameterKind.Text, "image/png,image/jpeg,image/gif,image/webp",
            "可自动看的媒体类型，逗号分隔。下一轮生效。",
            ParameterApply.Immediate)
    ];

    public string? ReadParameter(string name)
    {
        var options = Options;
        return name switch
        {
            "autoView" => options.AutoView.ToString().ToLowerInvariant(),
            "maxAutoViewsPerTurn" => options.MaxAutoViewsPerTurn.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "maxImageBytes" => options.MaxImageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "maxImagesPerRequest" => options.MaxImagesPerRequest.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "maxImageBytesPerRequest" => options.MaxImageBytesPerRequest.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "allowed" => string.Join(",", options.Allowed),
            _ => null
        };
    }

    public ValueTask<Result> ApplyParameterAsync(string name, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Options;
        MediaOptions next;
        switch (name)
        {
            case "autoView":
                if (!Enum.TryParse<MediaViewPolicy>(value?.Trim(), ignoreCase: true, out var policy))
                    return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid,
                        $"autoView must be one of off, first, all: {value}")));
                next = current with { AutoView = policy };
                break;
            case "allowed":
                var types = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (types.Length == 0)
                    return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid,
                        "allowed needs at least one media type; to show none, set autoView to off.")));
                next = current with { Allowed = [.. types] };
                break;
            case "maxAutoViewsPerTurn":
            case "maxImageBytes":
            case "maxImagesPerRequest":
            case "maxImageBytesPerRequest":
                if (!long.TryParse(value, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 0)
                    return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, $"{name} must be a non-negative integer: {value}")));
                next = name switch
                {
                    "maxAutoViewsPerTurn" => current with { MaxAutoViewsPerTurn = (int)number },
                    "maxImageBytes" => current with { MaxImageBytes = number },
                    "maxImagesPerRequest" => current with { MaxImagesPerRequest = (int)number },
                    _ => current with { MaxImageBytesPerRequest = number }
                };
                break;
            default:
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, $"Unknown parameter: {name}")));
        }
        // One write, so a request being assembled never sees half a decision.
        Volatile.Write(ref _options, next);
        return ValueTask.FromResult(Result.Success());
    }

    // ---- configuration ------------------------------------------------------------------------

    /// <summary>
    /// Reads the numbers out of the module's own file. Refused rather than guessed: a value that cannot be understood
    /// is a person's typo, and a typo that silently becomes "the default" is indistinguishable from a file nobody read.
    /// </summary>
    public static Result<MediaOptions> Parse(string? configJson, string id)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return Result<MediaOptions>.Success(new MediaOptions());
        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Result<MediaOptions>.Failure(new(ErrorCode.Invalid, $"{id}: config must be a JSON object."));
            var root = document.RootElement;
            var options = new MediaOptions();

            if (root.TryGetProperty("autoView", out var autoView))
            {
                if (autoView.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse<MediaViewPolicy>(autoView.GetString(), ignoreCase: true, out var policy))
                    return Result<MediaOptions>.Failure(new(ErrorCode.Invalid,
                        $"{id}: \"autoView\" must be one of off, first, all."));
                options = options with { AutoView = policy };
            }
            if (root.TryGetProperty("allowed", out var allowed))
            {
                if (allowed.ValueKind != JsonValueKind.Array || allowed.GetArrayLength() == 0)
                    return Result<MediaOptions>.Failure(new(ErrorCode.Invalid,
                        $"{id}: \"allowed\" must be a non-empty array of media types."));
                var types = new List<string>();
                foreach (var item in allowed.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                        return Result<MediaOptions>.Failure(new(ErrorCode.Invalid,
                            $"{id}: every entry in \"allowed\" must be a non-empty string."));
                    types.Add(item.GetString()!);
                }
                options = options with { Allowed = types.AsReadOnly() };
            }
            foreach (var (name, apply) in new (string, Func<MediaOptions, long, MediaOptions>)[]
                     {
                         ("maxImageBytes", (o, n) => o with { MaxImageBytes = n }),
                         ("maxImagesPerRequest", (o, n) => o with { MaxImagesPerRequest = (int)n }),
                         ("maxImageBytesPerRequest", (o, n) => o with { MaxImageBytesPerRequest = n }),
                         ("maxAutoViewsPerTurn", (o, n) => o with { MaxAutoViewsPerTurn = (int)n })
                     })
            {
                if (!root.TryGetProperty(name, out var element)) continue;
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var number) || number < 0)
                    return Result<MediaOptions>.Failure(new(ErrorCode.Invalid,
                        $"{id}: \"{name}\" must be a non-negative integer."));
                options = apply(options, number);
            }
            return Result<MediaOptions>.Success(options);
        }
        catch (JsonException ex)
        {
            return Result<MediaOptions>.Failure(new(ErrorCode.Invalid, $"{id}: config is not valid JSON.", ex.Message));
        }
    }

    public static Result<IKernelModule> FromManifest(ModuleManifest manifest, IParameterRegistry? parameters,
        MediaPolicyBridge? bridge = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Type, TypeId, StringComparison.Ordinal))
            return Result<IKernelModule>.Failure(new(ErrorCode.Invalid,
                $"This factory builds {TypeId}, not {manifest.Type}."));
        var parsed = Parse(manifest.ConfigJson, manifest.Id.ToString());
        if (!parsed.Ok || parsed.Value is null) return Result<IKernelModule>.Failure(parsed.Error!);
        var module = new MediaModule(manifest.Id, parameters);
        Volatile.Write(ref module._options, parsed.Value);
        // The rest of the process asks the bridge, and the bridge answers with this module: one policy, read live, so a
        // parameter change reaches the next request instead of the next restart (docs/28 §4).
        bridge?.Current = module;
        return Result<IKernelModule>.Success(module);
    }

    // ---- lifecycle ----------------------------------------------------------------------------

    public ValueTask<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parameters is null) return ValueTask.FromResult(Result.Success());
        var registered = parameters.Register(this);
        _registered = registered.Ok;
        // A duplicate scope is not this module's failure to report: the other one is already answering. Everything else
        // is, because a policy nobody can tune while running is a promise the settings page would be quietly breaking.
        return ValueTask.FromResult(registered.Error is { Code: ErrorCode.Conflict } ? Result.Success() : registered);
    }

    public ValueTask<Result> ReadyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        if (_registered && parameters is not null)
        {
            parameters.Unregister(ParameterScope);
            _registered = false;
        }
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> AbortAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    public ValueTask<Result> OnErrorAsync(ModuleError error, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}
