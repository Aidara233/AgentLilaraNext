using System.Text.Json;
using ALN.Contracts;
using ALN.Contracts.Archive;
using ALN.Contracts.Models;
using ALN.Contracts.Prompts;
using ALN.Contracts.Sessions;
using ALN.Graph.Contracts;
using ALN.Runtime;
using ALN.Runtime.Hosting;
using ALN.Storage.Sqlite;

var dataArg = Array.FindIndex(args, a => a == "--data-dir");
if (dataArg < 0)
{
    Console.Error.WriteLine("An explicit storage directory is required. Pass --data-dir <path>.");
    return 2;
}
if (dataArg + 1 >= args.Length || string.IsNullOrWhiteSpace(args[dataArg + 1]))
{
    Console.Error.WriteLine("--data-dir requires a non-empty path.");
    return 2;
}
var data = DataDirectory.Open(args[dataArg + 1]);
// The media endpoint: where a picture already on this disk becomes a URL a bridge can fetch — the normal way to send a
// picture to a bridge, because base64 in one WebSocket frame is both slow and, past a few megabytes, something bridges
// have been seen to die on (docs/16 §12.3).
//
// On by default: "works out of the box" is worth more than "off unless configured", given the door is read-only, serves
// only pictures the probe recognises, only from `media` and `workspace`, and only with a per-process random token in the
// URL. What is *not* guessed is the address — `--media-endpoint http://<host>:<port>` overrides it, `--media-endpoint off`
// switches it off, and with nothing said it listens on 5310 and advertises this machine's LAN address, which is the shape
// of a bridge on the same network and the only case where a default can be right.
var mediaEndpointArg = Array.FindIndex(args, a => a == "--media-endpoint");
var mediaEndpointValue = mediaEndpointArg >= 0 && mediaEndpointArg + 1 < args.Length ? args[mediaEndpointArg + 1] : null;
if (mediaEndpointArg >= 0 && string.IsNullOrWhiteSpace(mediaEndpointValue))
{
    Console.Error.WriteLine("--media-endpoint requires a base url (http://<host>:<port>) or 'off'.");
    return 2;
}
var mediaEndpointDisabled = mediaEndpointValue is { } chosen &&
    (string.Equals(chosen, "off", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(chosen, "none", StringComparison.OrdinalIgnoreCase));
var mediaEndpointUrl = mediaEndpointDisabled
    ? null
    : mediaEndpointValue ?? $"http://{LanAddress()}:5310";
// Transport state, not configuration: a file Adapter's mailbox is IO and lives under io/file/<account>/, which the
// adapter package derives from the spec it is handed. The host no longer hands out one mailbox for everybody — that
// made the second file Adapter read the first one's inbox.
var defaultInbox = Path.Combine(data.Root, "io", "file", "file-1", "inbox");
// One file for everything a program writes down: component state, message history, audit records. The
// spec tree stays files — those a person writes and reads.
using var storage = new SqliteStore(Path.Combine(data.Root, "aln.db"));
IStateStore stateStore = storage;
IMessageStore messageStore = storage;
IAuditSink auditSink = storage;
// Nothing reads the old file layout any more; say so instead of silently ignoring what is there.
foreach (var legacy in new[] { "state", "messages", "audit" })
    if (Directory.Exists(Path.Combine(data.Root, legacy)))
        Console.Error.WriteLine($"note: {legacy}/ is left over from file storage and is ignored; aln.db is the source of truth now.");
// The spec trees moved too: an Adapter, Channel or Session spec is a module file under modules/<id>/, and nothing
// reads <DataRoot>/adapters, /channels or /sessions. A spec written there is a no-op, and a no-op looks exactly like
// a spec that was read and rejected on purpose — which is how a person ends up checking the code for a mistake the
// disk made. So the directory gets a line, the same way the old file layout does.
foreach (var stale in new[] { "adapters", "channels", "sessions" })
    if (Directory.Exists(Path.Combine(data.Root, stale)))
        Console.Error.WriteLine($"note: {stale}/ is not read; instance specs live in " +
            $"{ModuleManifestStore.DirectoryName}/<id>/{ModuleManifestStore.FileName}.");
await using var signalLog = SignalLog.Open(Path.Combine(data.Root, "logs"));
var moduleLoggers = new SignalModuleLoggerFactory(signalLog);
var queue = new EventQueue(capacity: 256);
// The address book. Not a module itself: if it were, finding a module would require a module.
var modules = new ModuleCatalog();
var adapterTypes = new AdapterTypeCatalog();
// The spec stores are given the type tree: "is this record mine" is a question about types, not about how a type id
// happens to be spelled (C-04/C-06).
var adapterSpecs = new FileAdapterSpecStore(data.Root, adapterTypes);
var channelSpecs = new FileChannelSpecStore(data.Root, adapterTypes);
var bindings = new InstanceBindingsProxy();
var adapterFactories = new AdapterFactoryRegistry();
// One registry for every tunable: components register themselves, the registry validates, applies, persists.
var parameters = new ParameterRegistry(stateStore);
var adapters = new AdapterManager(adapterTypes, bindings, adapterFactories, adapterSpecs, parameters: parameters);
var artifacts = new FileArtifactStore(Path.Combine(data.Root, "artifacts"));
// Model calls are stored whole — request and answer — because a refused request is only diagnosable with the body that
// caused it. They are also the biggest thing on disk by far (42 MB in one day of light use), and they are a copy: what
// they describe is in the session timeline. A week is long enough to answer "what did it actually send yesterday".
const string ArtifactRetentionCategory = "model-calls";
const int ArtifactRetentionDays = 7;
const int ArtifactRetentionCheckHours = 6;
var modelManager = new ModelManager(artifacts, signalLog, events: queue);
var sessionExtensions = new SessionExtensionRegistry();
var timeSource = sessionExtensions.Register(new TimeContextSource());
if (!timeSource.Ok) { Console.Error.WriteLine(timeSource.Error?.Message); return 1; }
var channelInfoSource = sessionExtensions.Register(new ChannelInfoSource());
if (!channelInfoSource.Ok) { Console.Error.WriteLine(channelInfoSource.Error?.Message); return 1; }
// Who the people in these conversations are (docs/31): one directory per person under identify/, read once here and
// again only when somebody asks. No file watching — an edit caught halfway through being saved would become an
// identity, and a wrong identity is the one kind of mistake that never announces itself.
var personDirectory = new ALN.Identity.PersonDirectory(data.Root);
var peopleReport = await personDirectory.ReloadAsync(CancellationToken.None);
foreach (var problem in peopleReport.Rejected)
    Console.Error.WriteLine($"identity: {problem.Path}: {problem.Reason}");
// The table as a tool, and the same table as a few lines every round: one answers "who is this", the other keeps the
// answer in front of the model so it does not have to ask again after every compaction.
var identityToolsSource = sessionExtensions.Register(new ALN.Identity.IdentityTools(personDirectory));
if (!identityToolsSource.Ok) { Console.Error.WriteLine(identityToolsSource.Error?.Message); return 1; }
var peopleRosterSource = sessionExtensions.Register(new ALN.Identity.PersonRosterSource(personDirectory));
if (!peopleRosterSource.Ok) { Console.Error.WriteLine(peopleRosterSource.Error?.Message); return 1; }
// Everything a model may reach on disk goes through this one port, and the scopes it knows are registered here and
// nowhere else. The memory library is the first; another is added by registering it here and naming it in that
// module's own module.json — never by a module inventing a path.
var fileAccess = new LocalFileSystemAccess(data.Root,
    [FileScopes.Memory, FileScopes.ScriptsDraft, FileScopes.Workspace, FileScopes.Media],
    // Only the workspace accepts bytes: it is the room whose contents nobody reads back, so a tarball there is a
    // tarball and not a memory block somebody's summariser has to make sense of (docs/24 §5).
    //
    // Media is the exception in the other direction: it holds what the model cannot read as text but must be able to
    // look at (a picture) or move (a transfer), and it is written by the host alone — an inbound message, an ingest —
    // never by the model (docs/28 §2).
    binaryScopes: [FileScopes.Workspace, FileScopes.Media],
    // Read-only is the whole point of media: a session's record names the files it looked at, so a caller that could
    // rewrite one could change what it is recorded as having seen. Its own产出 goes to the workspace.
    readOnlyScopes: [FileScopes.Media]);
// Media: the one place bytes that are not text are named and kept. It writes through the port above, into the scope
// nobody but the host may write, and every caller — a message that arrived, a file the model opened — gets back a
// pointer rather than a copy (docs/28 §2, §3).
var mediaStore = new FileMediaStore(fileAccess);
// The door a bridge fetches media through, when the operator said where it can be reached. It starts here because
// this is where the data root and the file port are: a bridge that keeps hanging up on base64 needs a URL, and only the
// host knows how to hand one out (docs/16 §12.4). The file ceiling is a deployment number, so it is a flag rather than
// a constant in the code: what one bridge will swallow in one upload is a fact about that deployment (docs/28 §6.6).
var mediaMaxFileArg = Array.FindIndex(args, a => a == "--media-max-file");
var mediaMaxFileValue = mediaMaxFileArg >= 0 && mediaMaxFileArg + 1 < args.Length ? args[mediaMaxFileArg + 1] : null;
var mediaMaxFile = ALN.Api.MediaEndpoint.DefaultMaxFileBytes;
if (mediaMaxFileValue is { } declaredMaxFile &&
    (!long.TryParse(declaredMaxFile, out mediaMaxFile) || mediaMaxFile <= 0))
{
    Console.Error.WriteLine("--media-max-file must be a positive number of bytes (e.g. 2147483648).");
    return 2;
}
ALN.Api.MediaEndpoint? mediaEndpoint = null;
if (!string.IsNullOrWhiteSpace(mediaEndpointUrl))
{
    var startedEndpoint = await ALN.Api.MediaEndpoint.StartAsync(mediaEndpointUrl, fileAccess, CancellationToken.None,
        maxFileBytes: mediaMaxFile);
    if (startedEndpoint.Ok) mediaEndpoint = startedEndpoint.Value;
    else Console.Error.WriteLine($"note: {startedEndpoint.Error?.Message} {startedEndpoint.Error?.Detail}");
}
// And the policy that decides what may be shown: filled in by the media module when it starts, because that is where
// the decision is written down. Until then it answers with the shipped defaults, never with "no ceiling".
var mediaPolicy = new MediaPolicyBridge();
// Memory is a module of its own: it registers its own line and its own tools, and nothing else knows it exists. It
// reads and writes through the port above, so "the model may tidy memory with file tools" needs no special case:
// they reach the same files by the same rules.
var memoryLibrary = new FileMemoryLibrary(fileAccess);
var memoryModule = new MemoryModule(memoryLibrary, sessionExtensions);
// SSH has its own port for the same reason the file system does: one place that opens a socket, and a module that
// only says which device a Session asked for. Live keys stay in memory, so replacing a device or dropping it after two
// idle hours leaves nothing on disk.
var sshAccess = new ALN.Ssh.SshNetAccess();
var sshLiveKeys = new ALN.Ssh.MemorySecretStore();
// The network door (docs/29), in the same shape as SSH and the file system: the host decides where this process may
// reach, and everything above it can only ask for a target by name. The file is the whole allowlist, so a data root
// that has no target file can reach nowhere — which is what a fresh one gets.
var netTargets = ALN.Net.NetTargetFile.Read(ALN.Net.NetTargetFile.PathIn(data.Root));
foreach (var issue in netTargets.Issues)
    Console.Error.WriteLine($"net: {issue.Entry}: {issue.Problem}");
// A target's credential lives beside its file, readable only by the account running this process — the same plain store
// SSH uses, for the same reason (docs/27 §7): the disk is the last line of defence, and a half-encrypted format whose
// key sits on the same disk buys the feeling of safety rather than the thing.
var netSecrets = new ALN.Ssh.FileSecretStore(Path.Combine(data.Root, "net", "secrets"));
var netAccess = new ALN.Net.HttpNetAccess(netTargets.Targets, auditSink, signalLog, netSecrets);
// The raw door, as a tool: any address, any method, the answer exactly as it came. It sits beside the page reader
// rather than instead of it — one asks "what does this page say", the other "what does this endpoint reply".
var httpToolsSource = sessionExtensions.Register(new ALN.Net.HttpTools(netAccess));
if (!httpToolsSource.Ok) { Console.Error.WriteLine(httpToolsSource.Error?.Message); return 1; }
// Search and fetch, through whichever provider this deployment picked (docs/30). The providers are code and not
// packages: they have no lifecycle of their own, no phase to be ordered in, and nothing to unload — they are called
// when somebody asks and are otherwise not there. The file decides which one does which job, and mixing them (search
// with one, fetch with another) is the arrangement that usually makes most sense.
var webSettings = ALN.Web.WebOptionsFile.Read(ALN.Web.WebOptionsFile.PathIn(data.Root));
foreach (var issue in webSettings.Issues) Console.Error.WriteLine($"web: {issue}");
var webOptions = webSettings.Options;
var webProviders = new ALN.Web.WebProviderRegistry();
// Two providers implement both halves, so the interface is named at the call site: which table a provider sits in is
// a decision the deployment file already made, and the registry should not have to guess it from the type.
var deepSeek = new ALN.Web.DeepSeekProvider(netAccess, webOptions.DeepSeekTarget, webOptions.DeepSeekCount,
    webOptions.DeepSeekLocalization);
webProviders.Add((ALN.Contracts.ISearchProvider)deepSeek);
webProviders.Add((ALN.Contracts.IFetchProvider)deepSeek);
var tavily = new ALN.Web.TavilyProvider(netAccess, webOptions.TavilyTarget, webOptions.MaxResults,
    webOptions.TavilyDepth, webOptions.TavilyTopic, webOptions.TavilyCountry, webOptions.TavilyLanguage);
webProviders.Add((ALN.Contracts.ISearchProvider)tavily);
webProviders.Add((ALN.Contracts.IFetchProvider)tavily);
// The only one that fetches with this process's own socket, and so the only one that can reach a local address.
webProviders.Add(new ALN.Web.LocalProvider(netAccess, webOptions.LocalAllowPrivate, webOptions.LocalMaxBytes,
    webOptions.LocalTimeoutSeconds));
var webGateway = new ALN.Web.WebGateway(webProviders, webOptions, auditSink, signalLog);
var webToolsSource = sessionExtensions.Register(new ALN.Web.WebTools(webGateway, webOptions));
if (!webToolsSource.Ok) { Console.Error.WriteLine(webToolsSource.Error?.Message); return 1; }// The second half of activation: the Session pulls whatever the Channel has not handed over yet at the
// moment it assembles the request, so activation itself can stay a signal with no payload. Its cursor is
// component state, so it goes through the state store rather than a path the host invents.
// The startup receipt: whoever restores something says what it restored, and the host prints it once.
var restore = new RestoreReport();
restore.Note("net", netTargets.Targets.Count == 0
    ? "none — this process may reach nothing (net/targets.json is absent or empty)"
    : $"{netTargets.Targets.Count} target(s): {string.Join(", ", netTargets.Targets.Select(t => t.Name))}" +
      (netTargets.Issues.Count == 0 ? "" : $", {netTargets.Issues.Count} refused entry(ies)"));
restore.Note("web", webOptions.Search is null && webOptions.Fetch is null
    ? "off — no provider chosen (web/search.json is absent); the tools will not be offered"
    : $"search={webOptions.Search ?? "(none)"} fetch={webOptions.Fetch ?? "(none)"}; " +
      $"can search: {string.Join(", ", webProviders.SearchNames)}; can fetch: {string.Join(", ", webProviders.FetchNames)}");
restore.Note("identity", peopleReport.People.Count == 0
    ? $"none — every message arrives from a stranger ({ALN.Identity.PersonDirectory.DirectoryName}/ is absent or empty)"
    : $"{peopleReport.People.Count} person(s): " + string.Join(", ", peopleReport.People.Select(person =>
          person.Self ? $"{person.Name} (self)" : $"{person.Name} (L{(int)person.Level})")) +
      (peopleReport.Rejected.Count == 0 ? "" : $"; {peopleReport.Rejected.Count} refused"));
// Memory is not in the database any more, so its number is a directory listing rather than a count of rows. A
// library that cannot be read at startup is not worth stopping for: the receipt says so and the host carries on.
var memoryGroups = await memoryLibrary.GroupsAsync(CancellationToken.None);
restore.Note("storage", $"sqlite {Path.GetFileName(storage.Path)}: {storage.CountMessages()} message(s), " +
                        $"{storage.CountStateKeys()} state key(s), {storage.CountAuditRecords()} audit record(s)");
restore.Note("memory", memoryGroups.Error is { } memoryError
    ? $"unreadable: {memoryError.Message}"
    : $"{memoryGroups.Value?.Count ?? 0} group(s) under {FileScopes.Memory}/");
// Acknowledgements owed to Adapters: published is not handled, so the mailbox file is only settled after
// the event round it produced is over.
var acks = new InboundAckRegistry();
// Prompts are files a person edits: <dataRoot>/prompts/<group>/<name>.md — shipped defaults live next to the
// binary and the data root overrides them file by file, so an edit needed today is a file, not a release.
var prompts = new PromptLibrary([
    Path.Combine(AppContext.BaseDirectory, "resources", "prompts"),
    Path.Combine(data.Root, "prompts")]);
var catalog = prompts.List();
if (catalog.Groups.Count > 0 || catalog.Warnings.Count > 0)
    restore.Note("prompts", $"{catalog.Groups.Count} group(s), {catalog.Groups.Sum(group => group.Entries.Count)} prompt(s)" +
        (catalog.Warnings.Count == 0 ? "" : $"; skipped {catalog.Warnings.Count}: {string.Join("; ", catalog.Warnings)}"));
// Background work, written down. A job cannot be resumed after a restart — its socket and its reading thread are gone —
// but the row is how the next turn finds out that it is gone instead of waiting on it (docs/28 §9).
var jobs = new FileJobRegistry(storage);
var sessions = new SessionManager(data.Root, extensions: sessionExtensions, signalLog: signalLog, prompts: prompts, catalog: modules, events: queue,
    // A picture in a request is read here, from the store, for that one request: the timeline keeps the reference.
    media: mediaPolicy, files: fileAccess, jobs: jobs);
// Attached here rather than in the constructor because it is the one edge that points the other way: a Session is built
// with the ledger, so the ledger cannot be built with the Session. From this line on, a row that first says "over" wakes
// the session that started it (docs/28 §9.4). Before it — and never, in a host without sessions — the row is still
// written, and the session reads it at its next turn.
jobs.Waker = sessions;
// Which people this conversation keeps in mind. Registered after the Sessions because the roster lives on one: joining
// is the model's own decision (docs/31 §5), and it reaches the Session through the same manager a turn does.
var peopleMemoryTools = sessionExtensions.Register(new ALN.Identity.PersonMemoryTools(personDirectory, sessions));
if (!peopleMemoryTools.Ok) { Console.Error.WriteLine(peopleMemoryTools.Error?.Message); return 1; }
var reloadState = new ModuleReloadStateStore();
// The action table exists before anything runs, because the modules that implement actions are the ones that
// register them: a manager is created (and readied) inside a package load, long before startup finishes, and it
// used to find no control plane to register with. Whoever implements a verb announces it; the host only hands out
// the table. This is also why it is in host services: a module reaches it the same way it reaches the signal log.
var controlPlane = new ControlPlane(auditSink, signalLog);
// The adapter manager is built here rather than inside a package, so it registers here. A manager a package builds
// registers from that package's start — the rule is the same either way: the implementer announces, and a failure to
// register is a startup failure, because a verb that silently does not exist is only discovered by whoever tries it.
var adapterActions = adapters.RegisterActions(controlPlane);
if (!adapterActions.Ok)
{
    Console.Error.WriteLine(adapterActions.Error?.Message ?? "Adapter action registration failed.");
    return 1;
}
// The identity verbs. Registered here rather than inside a package because the directory itself is a host concern:
// no plugin owns the folder, and a table that could be replaced by loading a package would be a table somebody could
// replace by loading a package.
var identityActions = ALN.Identity.IdentityActionSet.Register(controlPlane, personDirectory, personDirectory,
    personDirectory);
if (!identityActions.Ok)
{
    Console.Error.WriteLine(identityActions.Error?.Message ?? "Identity action registration failed.");
    return 1;
}
var hostServices = new HostServiceProvider(
    (typeof(IControlPlane), controlPlane),
    (typeof(IAdapterManager), adapters),
    (typeof(IAdapterFactoryRegistry), adapterFactories),
    (typeof(IModelManager), modelManager),
    (typeof(ISessionManager), sessions),
    (typeof(IPromptLibrary), prompts),
    (typeof(ISessionExtensionRegistry), sessionExtensions),
    (typeof(IEventSink), new RecordingEventSink(queue, messageStore)),
    (typeof(IMessageStore), messageStore),
    (typeof(IStateStore), stateStore),
    (typeof(IAtomicWork), storage),
    (typeof(IQueuedMessageStore), storage),
    (typeof(IRestoreReporter), restore),
    (typeof(IInboundAcks), acks),
    (typeof(IParameterRegistry), parameters),
    (typeof(IFileMailboxFactory), new FileMailboxFactory(data.Root)),
    (typeof(IChannelSpecStore), channelSpecs),
    (typeof(IAdapterSpecStore), adapterSpecs),
    (typeof(IModuleTypeCatalog), adapterTypes),
    (typeof(IModuleTypeRegistry), adapterTypes),
    (typeof(IInstanceBindings), bindings),
    (typeof(ISignalLog), signalLog),
    (typeof(IModuleLoggerFactory), moduleLoggers),
    (typeof(IAuditSink), auditSink),
    (typeof(ICatalog), modules),
    (typeof(IModuleReloadStateStore), reloadState),
    // How a Channel stores what came with a message: brackets first, bytes once, pointer in the row (docs/28 §2).
    (typeof(IMediaStore), mediaStore),
    // The one byte surface, for an Adapter that has to read a picture the model asked it to send. The SSH module is
    // given the same port by constructor; this is it registered under the name a plugin looks up.
    (typeof(IBinaryFileAccess), fileAccess),
    // The table of background work, for anything that starts something long: SSH today, file downloads next.
    (typeof(IJobRegistry), jobs),
    // And the door a bridge fetches a picture through, when one was configured: the adapter asks for it by contract name
    // and gets null when the operator did not open one — which is exactly "keep using base64".
    (typeof(IMediaUrlSource), (object)mediaEndpoint!),
    // Who is who. Two names for one object: the hot path asks only the reading half, and reloading is a separate
    // contract so that nothing which identifies a speaker can rebuild the table underneath itself.
    (typeof(IPersonDirectory), personDirectory),
    (typeof(IPersonDirectoryReload), personDirectory),
    (typeof(ModuleHostPaths), new ModuleHostPaths(AppContext.BaseDirectory, data.Root)));
var moduleServices = new ModuleServiceRegistry(hostServices);
var moduleUnloads = new ModuleUnloadCoordinator();
// The plugin folder is handed to the loader so a version can be resolved by name; the action table is handed to it
// so a package that registered a verb can give it back. Without the second, replacing a package that added an action
// leaves the handler pointing into an assembly nothing can release, and the next version's registration conflicts.
await using var modulePackages = new ModulePackageLoader(moduleUnloads, moduleServices, adapterTypes, reloadState,
    [adapterFactories, modelManager], adapters, bindings,
    pluginsRoot: Path.Combine(data.Root, "plugins"), actionTable: controlPlane);
// The bin comes up first: the bundled runtime registers what it has to offer into it, and anything else that
// piles up (model-call artifacts next, most likely) registers the same way, without knowing this package.
var archivePackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "archive"), ModulePackageKind.Bundled, CancellationToken.None);
if (!archivePackage.Ok)
{
    Console.Error.WriteLine(archivePackage.Error?.Message ?? "Archive module failed to load.");
    return 1;
}
var archiveService = moduleServices.GetService<IArchiveRegistry>(ArchiveService.Registry);
if (!archiveService.Ok || archiveService.Value is null)
{
    Console.Error.WriteLine("The archive module did not provide its registry.");
    return 1;
}
var archive = archiveService.Value;
var deadlines = new SessionDeadlineHandler(archive, sessions);
var bundledPackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "bundled.runtime"), ModulePackageKind.Bundled, CancellationToken.None);
if (!bundledPackage.Ok)
{
    Console.Error.WriteLine(bundledPackage.Error?.Message ?? "Bundled modules failed to load.");
    return 1;
}
var modelPackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "model.openai-chat"), ModulePackageKind.Plugin, CancellationToken.None);
if (!modelPackage.Ok)
{
    Console.Error.WriteLine(modelPackage.Error?.Message ?? "OpenAI Chat provider module failed to load.");
    return 1;
}
// The store's root is the data root, not <root>/models: a provider is a module directory like everything else.
var modelInstances = new FileModelInstanceStore(data.Root, adapterTypes);
var modelConfiguration = await LoadModelConfigurationAsync(modelInstances);
if (!modelConfiguration.Ok)
{
    Console.Error.WriteLine(modelConfiguration.Error?.Message ?? "Model configuration failed to load.");
    return 1;
}
var graphResult = moduleServices.GetService<IGraphRuntime>("graph.runtime");
var channelResult = moduleServices.GetService<IChannelManager>("channel.manager");
var clockResult = moduleServices.GetService<IClockManager>("clock.manager");
if (!graphResult.Ok || graphResult.Value is null || !channelResult.Ok || channelResult.Value is null || !clockResult.Ok || clockResult.Value is null)
{
    Console.Error.WriteLine("Bundled module package did not provide the required Graph, Channel and Clock services.");
    return 1;
}
var graph = graphResult.Value;
var channels = channelResult.Value;
var clock = clockResult.Value;
// The address book comes up before anything else and is filled in as modules appear. Every registration here
// is a *singleton*: the type is the manager class itself — not the category it owns, which is why an adapter
// lookup by type never returns the manager. The category is the namespace it manages.
// One table per concern: what the catalog lists, and which of those own instances the catalog should not copy.
var registered = HostComposition.Register(
    modules,
    [adapters, modelManager, sessions, (IModule)channels!, (IModule)graph!, (IModule)clock!],
    // The instances those managers hold are findable through them, not copied in: the owner's dictionary is the
    // truth, so a destroyed instance is gone from the catalog by being gone from its owner.
    [adapters, modelManager, (IModuleIndex)channels!, sessions]);
if (!registered.Ok)
{
    Console.Error.WriteLine(registered.Error?.Message);
    return 1;
}
// Which modules exist is not something anybody can discover from a log, and a catalogue that silently failed to
// register would look exactly like one with nothing in it.
restore.Note("modules", $"{modules.Count} registered: {string.Join(", ", modules.List().Where(row => row.Role is not ModuleRole.Instance).Select(row => row.Id.Value))}");
var channelDelta = new FileChannelDeltaSource(messageStore, stateStore);
var deltaSource = sessionExtensions.Register(new ChannelDeltaSource(channelDelta, channels, personDirectory));
if (!deltaSource.Ok) { Console.Error.WriteLine(deltaSource.Error?.Message); return 1; }
// The manager owns adapter startup and the stop sweep; the clock interval belongs to the Adapter, which is the
// only thing that knows how its transport wants to be polled.
adapters.UseEvents(new RecordingEventSink(queue, messageStore));
var adapterPackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "adapter.file"), ModulePackageKind.Plugin, CancellationToken.None);
if (!adapterPackage.Ok || adapterPackage.Value is null)
{
    Console.Error.WriteLine(adapterPackage.Error?.Message ?? "FileAdapter module load failed.");
    return 1;
}
// The QQ adapter is optional in the sense that no spec may use it; a load failure is still fatal,
// because a package that fails to load but leaves specs referring to it would start up half-blind.
var qqPackage = await modulePackages.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "modules", "adapter.qq"), ModulePackageKind.Plugin, CancellationToken.None);
if (!qqPackage.Ok || qqPackage.Value is null)
{
    Console.Error.WriteLine(qqPackage.Error?.Message ?? "QQ adapter module load failed.");
    return 1;
}

// Modules the operator dropped in live under the data root, not next to the binary: an upgrade replaces the
// binary directory, and a plugin is not part of the upgrade. A broken one is reported and skipped — a
// third-party package must not be able to stop the host from starting.
foreach (var dropped in await DroppedModules.LoadAsync(modulePackages, Path.Combine(data.Root, "plugins"), CancellationToken.None))
{
    // The folder the operator wrote, not the version directory: a receipt that says "from 1.1.0" reads like the
    // package is called 1.1.0.
    var folder = Path.GetFileName(dropped.RootDirectory ?? dropped.Directory);
    if (dropped.Loaded) restore.Note("modules", $"plugin {dropped.Manifest!.Id} {dropped.Manifest.Version} from {folder}");
    else Console.Error.WriteLine($"plugin skipped: {folder} — {dropped.Failure?.Message} {dropped.Failure?.Detail}".TrimEnd());
}

// Before the catalogue is read, and only once per script: the shipped scripts get a copy in the operator's tree,
// where they can be edited and reloaded. Never rewritten afterwards (C-03) — see ScriptSeed for why the copy is
// not byte-identical.
var seed = new ScriptSeed(AppContext.BaseDirectory, data.Root);
var seededScripts = await seed.CopyMissingAsync(CancellationToken.None);
if (seededScripts.AddedAnything)
    restore.Note("scripts", $"copied to scripts/trusted: {string.Join(", ", seededScripts.Copied)}");
foreach (var (seedId, seedReason) in seededScripts.Failed)
    restore.Note("scripts", $"could not copy {seedId}: {seedReason}");
if (await seed.EnsureDraftGuideAsync(CancellationToken.None))
    restore.Note("scripts", $"updated scripts/draft/{ScriptSeed.GuideFileName} " +
        $"(the previous one is kept as {ScriptSeed.GuidePreviousFileName})");

var scriptCatalog = moduleServices.GetService<IGraphScriptCatalog>("graph.script-catalog");
if (!scriptCatalog.Ok || scriptCatalog.Value is null)
{
    Console.Error.WriteLine(scriptCatalog.Error?.Message ?? "Graph script catalog is unavailable.");
    return 1;
}
var scripts = await scriptCatalog.Value.LoadAsync(CancellationToken.None);
if (!scripts.Ok || scripts.Value is null)
{
    Console.Error.WriteLine(scripts.Error?.Message ?? "Script loading failed.");
    return 1;
}
var loaded = await graph.LoadAsync(scripts.Value, CancellationToken.None);

// A refused script used to leave one trace: a good that vanished from the shelf. The reason was collected into a list
// nobody printed, so "why is this script not loaded" cost four rounds of reading code. One line per refusal, at the
// moment it happens, is the whole fix.
foreach (var issue in graph.RejectedScripts)
    Console.WriteLine($"scripts: {issue.Path} 被拒 — {issue.Reason}");
if (!loaded.Ok)
{
    Console.Error.WriteLine($"Graph loading failed: {loaded.Error?.Message} {loaded.Error?.Detail}");
    return 1;
}

// Configuration, not startup: make sure the file Adapter has a spec to revive. Starting it is the
// kernel's job, not the host's.
var storedAdapters = await adapterSpecs.LoadAllAsync(CancellationToken.None);
if (!storedAdapters.Ok)
{
    Console.Error.WriteLine(storedAdapters.Error?.Message ?? "Stored Adapter specs could not be read.");
    return 1;
}
var adapterSpec = storedAdapters.Value!.FirstOrDefault(s => s.TypeId == "adapter.file")
    ?? new AdapterSpec(new Address("adapter", "file", "file-1"), new AdapterDescriptor("file", "file-1", AdapterDirection.Both), "adapter.file");
if (storedAdapters.Value!.Count == 0)
{
    var seeded = await adapterSpecs.SaveAsync(adapterSpec, CancellationToken.None);
    if (!seeded.Ok)
    {
        Console.Error.WriteLine(seeded.Error?.Message ?? "Adapter spec could not be seeded.");
        return 1;
    }
}

using var shutdown = new CancellationTokenSource();
using var pumpStop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
// Instructions: the one stage between an Adapter and a Channel. An instruction is answered here and the message goes
// no further — no Channel row, no "accepted but unfinished" marker, no delta, no Session turn — which is only possible
// before the first dispatcher has seen it. Everything an instruction does, it does by calling the control plane as the
// person who typed it, so a level is still the only thing that decides depth.
var commandOptions = ALN.Commands.CommandOptions.FromEnvironment();
var commandRegistry = new ALN.Commands.CommandRegistry(commandOptions, signalLog, auditSink);
var commandSeed = ALN.Commands.BuiltinCommands.Register(commandRegistry, controlPlane);
if (!commandSeed.Ok)
{
    Console.Error.WriteLine(commandSeed.Error?.Message ?? "Command registration failed.");
    return 1;
}
var commandInterceptor = new ALN.Commands.CommandInterceptor(commandRegistry, adapters, personDirectory);
Console.WriteLine($"instructions: prefix '{commandOptions.Prefix}', {commandRegistry.Visible(AccessLevel.Operator).Count} " +
                  $"for a person, scripted {(commandOptions.Scripted ? "on" : "off")}");

// The purse: Lilara's own money, one number for the whole framework. The shelf and the bag are separate files, and
// what a purchase yields is decided by the good's script — never by the catalogue.
var coinScripts = new ALN.Coin.GraphCoinScripts(graph);
var coinCatalogue = ALN.Coin.CoinCatalogueFile.Load(data.Root, coinScripts);
foreach (var issue in coinCatalogue.Issues) Console.WriteLine($"market: {issue}");
var coinLedger = new ALN.Coin.CoinLedger(stateStore, storage, signalLog, auditSink);
var coinMarket = new ALN.Coin.CoinMarket(coinLedger, coinCatalogue, coinScripts);
// A coin somebody gives Lilara is a fact about a conversation as well as about the purse, so it is announced into the
// Session behind whichever Channel that instruction arrived in. Built here because the two ends it needs — a Channel to
// find the conversation and a Session to put the note in — are host objects, and the coin module holds neither.
var coinNotice = new ALN.Coin.CoinNotice(new ALN.Coin.SessionConversations(channels, sessions), signalLog);
var purse = ALN.Coin.CoinService.Install(sessionExtensions, commandRegistry, controlPlane, coinLedger, coinMarket,
    notice: coinNotice);
if (!purse.Ok) Console.Error.WriteLine(purse.Error?.Message ?? "The purse did not install.");
else
    Console.WriteLine($"purse: {coinCatalogue.Goods.Count} goods, {coinCatalogue.Items.Count} items on the shelf");

// The larder: one bowl per round for the whole framework. A refusal closes the turn, not the Session.
var riceLedger = new ALN.Rice.RiceLedger(stateStore, storage, signalLog, auditSink);
var larder = ALN.Rice.RiceService.Install(sessionExtensions, commandRegistry, controlPlane, riceLedger);
if (!larder.Ok) Console.Error.WriteLine(larder.Error?.Message ?? "The larder did not install.");

var pump = new EventPump(queue, graph.DispatchAsync, signalLog,
    // Two things are owed after a round: the mailbox file stays claimed until the event it carries is done,
    // and a Channel message stops being "accepted but unfinished" so a restart has nothing to replay.
    new EventSettlers(new ChannelAcceptSettler(channels), new InboundAckSettler(acks)),
    // Before dispatch: an instruction is taken out of the stream here, so nothing downstream can see it.
    commandInterceptor);
var pumpTask = pump.RunAsync(pumpStop.Token);
var clockTask = clock.RunAsync(shutdown.Token);
// Deadlines a Session declared with wait. Started here, next to the other long-running loops, so that stopping
// the host stops it too — a sweep that outlived the host would act on state nothing else is maintaining.
var deadlineSweepTask = sessions.RunDeadlineSweepAsync(deadlines, TimeProvider.System,
    SessionDeadlineHandler.Interval, shutdown.Token);
// Schedules are the other half of "when": a row somebody wrote down, fired by a sweep rather than by a Session's own
// wait. Same place in the lifecycle as the deadline loop, for the same reason — a sweep that outlived the host would
// act on state nothing else is maintaining.
var scheduleStore = new ScheduleStore(stateStore, signalLog);
var scheduleSweep = new ScheduleSweep(scheduleStore, new HostScheduleTargets(sessions, graph), signalLog);
var scheduleSweepTask = scheduleSweep.RunAsync(TimeProvider.System, shutdown.Token);
// Stored model calls are derived data — a copy of a request that has already been answered — and they were the largest
// thing on disk by far: 42 MB in one day of light use. Deleted on a timer rather than at exit, because the host this is
// for is the one that does not exit.
var retentionTask = RunRetentionAsync(shutdown.Token);

// The host's only startup act: build the graph and hand it to the kernel, which decides the phases, the order
// and what to do about a module that fails. The host does not know what a wave is.
// The kernel is told which types this process builds itself: every instance has a module.json too (docs/22 §2), and
// its type belongs to a manager, so the loader must not treat the directory as a module it failed to understand.
// The kernel is told which types this process builds itself. That used to be a predicate written here, next to the
// kernel; now it is a fact registered on the factory table: a type whose instances a manager builds is "owned", so
// the loader reads its file as that instance's configuration instead of reporting a module it failed to understand.
var factories = new ModuleFactoryRegistry();
// The file system module is built from its own file, like every other module that a person configures: the factory
// gets the manifest and decides what its config means. The whitelist is therefore an edit, not a recompile.
factories.Register(FileSystemModule.TypeId,
    manifest => FileSystemModule.FromManifest(manifest, fileAccess, sessionExtensions, mediaStore));
// SSH: same shape, one file to decide the timings, and no device list — a device is something a user hands over in a
// conversation, not something a default file invents.
factories.Register(SshModule.TypeId,
    manifest => SshModule.FromManifest(manifest, sshAccess, sshLiveKeys, sessionExtensions,
        audit: auditSink, signals: signalLog, binaryFiles: fileAccess,
        // The ledger, so that a build still running when the host stops is a fact the next turn reads (docs/28 §9).
        jobs: jobs,
        savedStore: stateStore, savedKeys: new ALN.Ssh.FileSecretStore(Path.Combine(data.Root, "ssh", "keys"))));
// Media: one file decides which formats may be shown to a model, how big, how many, and whether an arriving picture is
// shown unasked. It has no tools — looking at one is fs_read and the file is under a scope the model can read — so what
// is left for a module to own is the policy, which is exactly the kind of decision a module.json is for (docs/28 §4).
factories.Register(MediaModule.TypeId, manifest => MediaModule.FromManifest(manifest, parameters, mediaPolicy));
// The script control tools are a module too: "who may reload scripts" is answerable by looking at the graph instead of
// at the host's source, and the two tools it offers read whatever the graph holds at the moment they are called.
factories.Register(ScriptToolsModule.TypeId,
    manifest => ScriptToolsModule.FromManifest(manifest, graph, scriptCatalog.Value!, sessionExtensions,
        new ScriptLibrary(data.Root), modules, controlPlane));
// The action table reaches the model through a module too, and the plane it registers into came up long before this:
// a module that offers tools is a module like any other, and the tools read the table as it is when they are called.
factories.Register(ActionToolsModule.TypeId,
    manifest => ActionToolsModule.FromManifest(manifest, controlPlane, sessionExtensions));
// The read-only half of the schedules, offered as a module for the same reason the other tool modules are.
factories.Register(ScheduleTools.TypeId,
    manifest => ScheduleToolsModule.FromManifest(manifest, scheduleStore, sessionExtensions));
// And the table of background work, read-only: "what is this conversation still waiting on" is a question the model
// should be able to answer from the record, including after a restart. Two verbs and no third — stopping work belongs
// to the tool that started it, not to the table that remembers it (docs/28 §9.3).
factories.Register(JobTools.TypeId,
    manifest => JobToolsModule.FromManifest(manifest, jobs, sessionExtensions));
// The two verbs a Session needs in order to know the others exist: who is here (and what each one is for), and how to
// hand one of them a message. A module like the other tool modules, so "may this Session see — and write to — the rest
// of the graph" is answered by the graph rather than by a line in the host's source.
factories.Register(SessionToolsModule.TypeId,
    manifest => SessionToolsModule.FromManifest(manifest, sessions, controlPlane, sessionExtensions));
// Files that are on the platform rather than in this process: listed, searched and fetched through the adapter. Where
// they land is the model's answer and the byte port's rule, so this module needs both ends and neither policy.
factories.Register(FilesGetTools.TypeId,
    manifest => FilesToolsModule.FromManifest(manifest, channels, adapters, fileAccess, sessionExtensions, jobs));
foreach (var type in adapterTypes.ListTypes().Where(type => adapterTypes.IsConcrete(type.TypeId)))
    factories.RegisterOwned(type.TypeId);
foreach (var type in modelManager.ListTypes())
    factories.RegisterOwned(type.TypeId);
factories.RegisterOwned(SessionManager.SessionTypeId);

var kernel = new Kernel(data.Root, factories, signalLog, hostOwnsTypes: factories.IsOwned);
// The kernel's table is the truth for what exists. The catalog looks there last — after its own entries and the
// declared owners — so nothing that answers today changes answer, and a module on the graph is findable without being
// registered twice.
modules.UseSource(id => kernel.Describe(id) is { } row && kernel.Find(id) is { } found && found is IModule module
    ? new LocatedModule(module, row)
    : null);
modules.UseRows(() => kernel.Instances
    .Select(info => new CatalogEntry(info.Id, info.Type, info.Category, info.Role))
    .ToArray());
// One table, one loop: these are the modules the host builds itself, so nothing on disk could produce them. Every
// one of them is a master that holds instances (or, for memory, a store) — none of them has phases worth ordering.
var declaredModules = new DeclaredModule[]
{
    new(new LifecycleModule(adapters, new ModuleId(ModuleNames.AdapterManager)), ModuleNames.AdapterManager,
        "Adapter manager"),
    // No dependency between adapters and channels: availability is checked when a message is delivered, so neither
    // manager has to wait for the other.
    new(new LifecycleModule((ILifecycleParticipant)channels!, new ModuleId(ModuleNames.ChannelManager)), ModuleNames.ChannelManager,
        "Channel manager"),
    new(memoryModule, MemoryModule.TypeId, "Memory"),
    // The model manager holds provider instances, so it is a master and has to be an instance. It has no phases of its
    // own — providers are built from their files — so it is a carrier.
    new(new PassiveInstance(new ModuleId(ModuleNames.ModelManager), modelManager), ModuleNames.ModelManager,
        "Model manager"),
    // Sessions, for the same reason as models: the manager holds them, so a Session is an instance the moment it is
    // loaded. No phase of its own either — a Session is loaded on demand, never by a wave.
    new(new PassiveInstance(new ModuleId(ModuleNames.SessionManager), sessions), ModuleNames.SessionManager,
        "Session manager")
};
var declaredCore = HostComposition.Declare(kernel, declaredModules);
if (!declaredCore.Ok)
{
    Console.Error.WriteLine(declaredCore.Error?.Message ?? "Module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// Instances are announced to the graph by the manager that holds them, so "what is mounted on this Channel" stops
// being a question only the manager can answer (docs/23 §4 step 1). Installed before startup, because reviving a
// stored Channel happens *during* startup — wiring it afterwards would announce only the ones created later.
if (channels is IInstanceSink sink)
    sink.UseInstanceHost(new InstanceHost(kernel, new ModuleId(ModuleNames.ChannelManager)));
// The same for Adapters - and it has to be here, not after startup: reviving the stored ones happens inside the
// kernel's Ready phase, and an Adapter that is not a instance never gets attached to the clock.
if (adapters is IInstanceSink adapterNodes)
    adapterNodes.UseInstanceHost(new InstanceHost(kernel, new ModuleId(ModuleNames.AdapterManager)));
// Model providers were built from their files long before the graph existed, so nothing announced them at the time.
// Announcing them now — before startup — makes them ordinary declarations: startup initializes, wires and starts
// them, and the receipt counts their edges like everybody else's.
var modelNodeHost = new InstanceHost(kernel, new ModuleId(ModuleNames.ModelManager));
if (modelManager is IInstanceSink modelNodes)
    modelNodes.UseInstanceHost(modelNodeHost);
foreach (var provider in modelManager.Entries())
{
    var announced = await modelNodeHost.AttachAsync(new ModuleId(ModuleNames.ModelManager), provider.Id,
        provider.TypeId, null, CancellationToken.None);
    if (announced.Ok) continue;
    Console.Error.WriteLine(announced.Error?.Message ?? $"Model provider {provider.Id} could not be announced.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// Sessions are announced by their manager when it loads one, so the sink is installed before startup — a revived
// Session is loaded inside a Ready phase, not after it.
if (sessions is IInstanceSink sessionNodes)
    sessionNodes.UseInstanceHost(new InstanceHost(kernel, new ModuleId(ModuleNames.SessionManager)));
// The clock is a instance too, so the graph can answer "who is on a schedule". It has no phases of its own - the host
// runs its loop - so it is a passive carrier rather than a participant.
var clockDeclared = kernel.Declare(new PassiveInstance(new ModuleId(ModuleNames.Clock), clock), typeId: ModuleNames.Clock);
if (!clockDeclared.Ok)
{
    Console.Error.WriteLine(clockDeclared.Error?.Message ?? "Clock module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// The file system module reads its whitelist from its own file, so the file has to exist before the scan. Written
// once and never rewritten (C-03): from then on the whitelist is whatever a person left there.
var fileSystemFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(FileSystemModule.TypeId), FileSystemModule.TypeId, Enabled: true,
        ConfigJson: FileSystemModule.DefaultConfig), CancellationToken.None);
if (!fileSystemFile.Ok)
{
    Console.Error.WriteLine(fileSystemFile.Error?.Message ?? "File system module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// The script control tools have no configuration, but they still get a file: a module that is on the graph is findable
// there, and one the host invents silently cannot be removed by editing a file.
var scriptToolsFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(ScriptToolsModule.TypeId), ScriptToolsModule.TypeId, Enabled: true),
    CancellationToken.None);
if (!scriptToolsFile.Ok)
{
    Console.Error.WriteLine(scriptToolsFile.Error?.Message ?? "Script control module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// The action table, offered to the model the same way: a module with a file, so "may the model reach the actions"
// is a question with an answer on disk rather than a switch in the source.
var actionToolsFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(ActionToolsModule.TypeId), ActionToolsModule.TypeId, Enabled: true),
    CancellationToken.None);
if (!actionToolsFile.Ok)
{
    Console.Error.WriteLine(actionToolsFile.Error?.Message ?? "Action tool module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// The schedules, readable by the model through the same mechanism: a module with a file, so "may this Session see when
// the host wakes up on its own" is answered by the graph. Writing a row is not offered here — that is an operator's.
var scheduleToolsFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(ScheduleTools.TypeId), ScheduleTools.TypeId, Enabled: true),
    CancellationToken.None);
if (!scheduleToolsFile.Ok)
{
    Console.Error.WriteLine(scheduleToolsFile.Error?.Message ?? "Schedule tool module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// The job table, readable the same way and for the same reason. It reads through to the row as it stands when a tool is
// called, so a module file written today cannot go stale about what is running.
var jobToolsFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(JobTools.TypeId), JobTools.TypeId, Enabled: true),
    CancellationToken.None);
if (!jobToolsFile.Ok)
{
    Console.Error.WriteLine(jobToolsFile.Error?.Message ?? "Job tool module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// The session roster and the session message, offered the same way and for the same reason: a file on disk that
// somebody can delete, so "the model may see the other Sessions" is a fact about this deployment rather than a
// compile-time default. Both tools read the roster as it is when they are called.
var sessionToolsFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(SessionToolsModule.TypeId), SessionToolsModule.TypeId, Enabled: true),
    CancellationToken.None);
if (!sessionToolsFile.Ok)
{
    Console.Error.WriteLine(sessionToolsFile.Error?.Message ?? "Session tool module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// And the file tool, which reads the platform's own file area. A module like the others: whether a Session may pull files
// off a platform is answered by the graph, not by a setting inside the tool.
var filesToolsFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(FilesGetTools.TypeId), FilesGetTools.TypeId, Enabled: true),
    CancellationToken.None);
if (!filesToolsFile.Ok)
{
    Console.Error.WriteLine(filesToolsFile.Error?.Message ?? "File tool module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
// What the file system module will actually offer, said out loud. The scopes this host registers and the scopes that
// module's own file declares are two different lists, and only their intersection is reachable — and the file is never
// rewritten (C-03), so a module.json written before a scope existed keeps the old list forever. Without this line,
// "the tools do not offer it" is indistinguishable from "the code forgot it", which is exactly the confusion this
// project already paid for once.
var declaredScopes = ReadDeclaredScopes(Path.Combine(data.Root, ModuleManifestStore.DirectoryName,
    FileSystemModule.TypeId, ModuleManifestStore.FileName));
var offeredScopes = declaredScopes.Length == 0
    ? fileAccess.Scopes
    : fileAccess.Scopes.Where(scope => declaredScopes.Contains(scope, StringComparer.Ordinal)).ToArray();
restore.Note("filesystem", $"offers {string.Join(", ", offeredScopes)}");
var undeclaredScopes = declaredScopes.Length == 0
    ? []
    : fileAccess.Scopes.Where(scope => !declaredScopes.Contains(scope, StringComparer.Ordinal)).ToArray();
if (undeclaredScopes.Length > 0)
{
    Console.Error.WriteLine(
        $"filesystem.local's {ModuleManifestStore.FileName} does not declare {string.Join(", ", undeclaredScopes)}: the file "
        + "tools will not offer it. Add it to that file's config.scopes to grant it — the host will not rewrite the file.");
}

// SSH reads its timings from its own file, written once and never rewritten for the same reason: from then on the
// numbers are whatever a person left there. It carries no device list — a device arrives from a conversation.
var mediaFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(MediaModule.TypeId), MediaModule.TypeId, Enabled: true,
        ConfigJson: MediaModule.DefaultConfig), CancellationToken.None);
if (!mediaFile.Ok)
{
    Console.Error.WriteLine(mediaFile.Error?.Message ?? "Media module declaration failed.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
var sshFile = await new ModuleManifestStore(data.Root).WriteIfMissingAsync(
    new ModuleManifest(new ModuleId(SshModule.TypeId), SshModule.TypeId, Enabled: true,
        ConfigJson: SshModule.DefaultConfig), CancellationToken.None);
if (!sshFile.Ok)
{
    Console.Error.WriteLine(sshFile.Error?.Message ?? "SSH module file could not be written.");
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
var started = await kernel.StartAsync(shutdown.Token);
if (!started.Ok || started.Value is null)
{
    Console.Error.WriteLine(started.Error?.Message ?? "Module startup failed.");
    await kernel.StopAsync(CancellationToken.None);
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
var receipt = started.Value;
// Channels register their tunables when they are revived, so persisted overrides are applied after startup,
// before anything reads a value.
var restored = await parameters.RestoreAsync(CancellationToken.None);
if (!restored.Ok)
    Console.Error.WriteLine($"Warning: a persisted parameter could not be restored: {restored.Error?.Message} {restored.Error?.Detail}");
// Messages that were accepted but whose round never finished: replay them now that Channels are alive.
var replayed = await channels.ReplayPendingAsync(CancellationToken.None);
if (!replayed.Ok)
    Console.Error.WriteLine($"Warning: unfinished messages could not be replayed: {replayed.Error?.Message} {replayed.Error?.Detail}");
// A deadline nobody can see is a deadline nobody can trust, and "the sweep is alive and found nothing" would
// otherwise look exactly like "the sweep never ran". This line is the difference, for a person reading startup.
var waiting = await sessions.ListAsync(CancellationToken.None);
if (waiting.Ok && waiting.Value is not null)
{
    var declared = waiting.Value.Where(session => session.WaitUntil is not null).Select(session => session.WaitUntil!.Value).ToArray();
    restore.Note("deferred", declared.Length == 0
        ? "no Session is waiting on a deadline"
        : $"{declared.Length} Session(s) waiting, earliest {declared.Min():O}");
}
foreach (var outcome in receipt.Instances.Where(instance => instance.Phase != ModulePhase.Ready))
    Console.Error.WriteLine($"startup {outcome.Phase}: {outcome.Id} — {outcome.Failure}");
foreach (var edge in receipt.BadEdges)
    Console.Error.WriteLine($"startup bad edge: {edge.Master} -> {edge.Slave} ({edge.Reason}): {edge.Detail}");
foreach (var cycle in receipt.Cycles)
    Console.Error.WriteLine($"startup cycle (ready together): {string.Join(" <-> ", cycle.Members)}");
foreach (var issue in receipt.Issues)
    Console.Error.WriteLine($"startup issue: {issue.Path} — {issue.Message}");
restore.Note("graph", $"{receipt.Ready}/{receipt.Instances.Count} ready, {receipt.WiredEdges} edge(s) wired" +
    (receipt.Degraded == 0 ? "" : $", degraded {receipt.Degraded}"));
// A trigger is not an edge, so it does not show up in the line above; printing it is what keeps "who pushes this
// adapter" from being a fact only the clock knows (docs/22 §1.4).
foreach (var rhythm in receipt.Rhythms)
    restore.Note("rhythm", rhythm.Line);
// What the address book can answer for once everything is running, instances included. A catalog whose instances
// are only in somebody else's dictionary would look identical from the outside — until something asks.
var liveInstances = modules.List()
    .Where(row => row.Role is ModuleRole.Instance)
    .GroupBy(row => row.Category, StringComparer.Ordinal)
    .Select(group => $"{group.Key} {group.Count()}")
    .Order(StringComparer.Ordinal)
    .ToArray();
restore.Note("instances", liveInstances.Length == 0 ? "none" : string.Join(", ", liveInstances));
foreach (var note in restore.Snapshot())
    Console.WriteLine($"restored {note.Component}: {note.Summary}");
if (receipt.Ready == 0)
{
    // Nothing came up at all: that is the only startup failure that justifies refusing to run.
    await kernel.StopAsync(CancellationToken.None);
    shutdown.Cancel();
    queue.Complete();
    try { await pumpTask; } catch (OperationCanceledException) { }
    try { await clockTask; } catch (OperationCanceledException) { }
    try { await deadlineSweepTask; } catch (OperationCanceledException) { }
    try { await scheduleSweepTask; } catch (OperationCanceledException) { }
    try { await retentionTask; } catch (OperationCanceledException) { }
    return 1;
}

// "The whole tree is up" as an event, because an event is the only thing a script can listen for — and an event is
// dispatched to one instance, so a single global one would reach nobody. Published per instance rather than once,
// which is also what makes the two questions different: "my instance started" is lifecycle.start, "everything this
// host has is up" is this one. Nothing waits on it; a failed publish is a warning, not a startup failure.
foreach (var instance in graph.Instances)
{
    var announced = await queue.PublishAsync(new(EventNames.LifecycleStarted, instance, Guid.NewGuid(),
        JsonSerializer.Serialize(new LifecycleSignal(LifecyclePhase.Start, DateTimeOffset.UtcNow).For(instance),
            LifecycleJson.Options), instance), CancellationToken.None);
    if (!announced.Ok)
        Console.Error.WriteLine($"Warning: {instance} could not be told the tree finished starting: {announced.Error?.Message}");
}

// The network surface is opt-in: no api/server.json, no listener. The core knows nothing about the
// WebUI itself — it exposes actions, and a separate project consumes them. The table itself came up long before
// startup (see host services); this is where the host adds the verbs it owns itself.
var modelActions = modelManager.RegisterActions(controlPlane, modelInstances);
if (!modelActions.Ok)
{
    Console.Error.WriteLine(modelActions.Error?.Message ?? "Model action registration failed.");
    return 1;
}
var sessionActions = sessions.RegisterActions(controlPlane);
if (!sessionActions.Ok)
{
    Console.Error.WriteLine(sessionActions.Error?.Message ?? "Session action registration failed.");
    return 1;
}
var scheduleActions = ScheduleActions.Register(controlPlane, scheduleStore);
if (!scheduleActions.Ok)
{
    Console.Error.WriteLine(scheduleActions.Error?.Message ?? "Schedule action registration failed.");
    return 1;
}
var scriptActions = ScriptActions.Register(controlPlane, graph, scriptCatalog.Value, new ScriptLibrary(data.Root));
if (!scriptActions.Ok)
{
    Console.Error.WriteLine(scriptActions.Error?.Message ?? "Script action registration failed.");
    return 1;
}
// Package management: switching a dropped-in package to another of its versions. Registered here rather than inside a
// package because it is the host's own machinery — a package that could replace other packages would be able to
// replace the thing watching it.
var packageActions = PackageActions.Register(controlPlane, modulePackages, graph, scriptCatalog.Value);
// The allowlist as verbs. Reading it is agent-level — a model asked "can you read that page" should be able to answer
// with a name instead of guessing a hostname — and changing it is an operator's. Both write here, so the file and the
// running process cannot disagree about where this process may reach.
var netActions = ALN.Net.NetActions.Register(controlPlane, netAccess, netAccess, netSecrets,
    ALN.Net.NetTargetFile.PathIn(data.Root));
if (!netActions.Ok)
{
    // A verb that failed to register is a door that is not there while everything else runs, which is exactly the
    // state that reads as "the feature is broken" three days later. Stop instead.
    Console.Error.WriteLine(netActions.Error?.Message ?? "The network verbs could not be registered.");
    return 1;
}
// The raw door as a verb too, so a script (ctx.actions.call) and a person (the web UI) reach the same rules the model
// does. Nothing is checked against a list here — the audit line is the control.
var httpActions = ALN.Net.HttpActions.Register(controlPlane, netAccess);
if (!httpActions.Ok)
{
    Console.Error.WriteLine(httpActions.Error?.Message ?? "http.request could not be registered.");
    return 1;
}
if (!packageActions.Ok)
{
    Console.Error.WriteLine(packageActions.Error?.Message ?? "Package action registration failed.");
    return 1;
}
var promptActions = PromptActions.Register(controlPlane, prompts);
if (!promptActions.Ok)
{
    Console.Error.WriteLine(promptActions.Error?.Message ?? "Prompt action registration failed.");
    return 1;
}
var actionsRegistered = CoreActions.RegisterAll(controlPlane, adapters, channels, modelManager, signalLog, graph, parameters, modulePackages, sessions, archive, prompts, adapterTypes, graphStatus: () => kernel.Status, kernel: kernel, scriptCatalog: scriptCatalog.Value, packages: modulePackages);
if (!actionsRegistered.Ok)
{
    Console.Error.WriteLine(actionsRegistered.Error?.Message ?? "Action registration failed.");
    await kernel.StopAsync(CancellationToken.None);
    shutdown.Cancel();
    queue.Complete();
    return 1;
}
ALN.Api.ApiInstance? api = null;
var apiSettings = ALN.Api.ApiSettings.Load(data.Root);
if (apiSettings is not null)
{
    foreach (var rejected in apiSettings.RejectedOrigins)
        Console.Error.WriteLine($"Warning: cors entry '{rejected}' is not an origin (scheme://host[:port]); ignored.");
    api = await ALN.Api.ApiHost.StartAsync(apiSettings, controlPlane, shutdown.Token);
    Console.WriteLine($"API listening on {string.Join(", ", api.Urls)}; {controlPlane.Catalog.Count} actions; authentication required." +
                      (api.Origins.Count == 0 ? " No cross-origin callers allowed." : $" CORS: {string.Join(", ", api.Origins)}."));
}
else
{
    Console.WriteLine("API disabled: no api/server.json.");
}

Console.WriteLine($"ALN is running. Data: {data.Root}; inbox: {defaultInbox}; create conversation subdirectories before starting. Ctrl+C to stop.");
try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
finally
{
    if (api is not null) await api.DisposeAsync();
    if (mediaEndpoint is not null) await mediaEndpoint.DisposeAsync();
    // Sessions are told they are stopping here, and dispatched inline, for one reason: ordering. Every Channel and
    // Adapter is still up at this point, so a script that answers a Session's stop can still speak — which is the
    // whole point of saying goodbye. Through the queue it would land behind the shutdown it is about, and through a
    // phase of the session manager it could not run at all (a session has no phase machine; the manager is passive).
    // Nothing is awaited per script: the dispatch is the graph's, and a script that runs long is bounded by its own
    // timeout, like every other event.
    foreach (var stopping in sessions.LoadedSessions(DateTimeOffset.UtcNow))
    {
        var target = stopping.Signal.Branch is { IsInstance: true } branch ? branch : (Address?)null;
        if (target is not { } address) continue;
        var beforeStop = await graph.DispatchAsync(new(EventNames.LifecycleStop, address, Guid.NewGuid(),
            JsonSerializer.Serialize(stopping, new JsonSerializerOptions(JsonSerializerDefaults.Web)), address),
            CancellationToken.None).ConfigureAwait(false);
        if (!beforeStop.Ok)
            Console.Error.WriteLine($"Warning: {stopping.SessionId} could not be told it is stopping: {beforeStop.Error?.Message}");
    }
    await kernel.StopAsync(CancellationToken.None);
    shutdown.Cancel();
    try { await clockTask; } catch (OperationCanceledException) { }
    try { await deadlineSweepTask; } catch (OperationCanceledException) { }
    try { await scheduleSweepTask; } catch (OperationCanceledException) { }
    try { await retentionTask; } catch (OperationCanceledException) { }
    queue.Complete();
    try { await pumpTask.WaitAsync(TimeSpan.FromSeconds(10)); }
    catch (TimeoutException) { pumpStop.Cancel(); try { await pumpTask; } catch (OperationCanceledException) { } }
    catch (OperationCanceledException) { }
    foreach (var profile in modelManager.ListProfiles()) modelManager.RemoveProfile(profile.Id);
    foreach (var provider in modelManager.List()) await modelManager.DestroyAsync(provider.Address, CancellationToken.None);
    await modulePackages.UnloadAsync(modelPackage.Value!.Id, TimeSpan.FromSeconds(10), force: true, CancellationToken.None);
    await modulePackages.UnloadAsync(adapterPackage.Value.Id, TimeSpan.FromSeconds(10), force: true, CancellationToken.None);
}
return 0;

/// <summary>
/// This machine's address on the local network: the one a bridge on the same network can dial.
///
/// Chosen by shape rather than by name — a private IPv4 that is up and not loopback, preferring the RFC 1918 ranges — because
/// there is no configuration to consult at this point and "127.0.0.1" is exactly the address a container cannot reach. A
/// machine with several adapters may well pick the wrong one; that is what <c>--media-endpoint</c> is for, and the receipt
/// prints the address it chose so a wrong pick is visible instead of mysterious.
/// </summary>
static string LanAddress()
{
    var candidates = new List<System.Net.IPAddress>();
    foreach (var network in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
    {
        if (network.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
        if (network.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
        foreach (var address in network.GetIPProperties().UnicastAddresses)
        {
            if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            candidates.Add(address.Address);
        }
    }
    var priv = candidates.FirstOrDefault(address =>
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);
    });
    return (priv ?? candidates.FirstOrDefault() ?? System.Net.IPAddress.Loopback).ToString();
}

/// <summary>
/// Deletes derived artifacts older than the retention window, now and every few hours.
///
/// A pass that fails is reported and then waited out: cleanup is maintenance, and a host that stopped because it could
/// not delete a file would be a worse outcome than the file staying.
/// </summary>
async Task RunRetentionAsync(CancellationToken cancellationToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromHours(ArtifactRetentionCheckHours));
    while (true)
    {
        var pruned = await artifacts.PruneAsync(ArtifactRetentionCategory, ArtifactRetentionDays,
            DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken).ConfigureAwait(false);
        if (pruned.Error is { } failure) restore.Note("artifacts", $"cleanup failed: {failure.Message}");
        else if (pruned.Value is { Removed: > 0 } report)
            restore.Note("artifacts", $"removed {report.Removed} day folder(s) of {ArtifactRetentionCategory}, " +
                $"{report.Bytes / (1024 * 1024)} MB freed, {report.Kept} kept");
        try { if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) return; }
        catch (OperationCanceledException) { return; }
    }
}

async Task<Result> LoadModelConfigurationAsync(IModelInstanceStore store){
    var loaded = await store.LoadAllAsync(CancellationToken.None);
    if (!loaded.Ok || loaded.Value is null) return Result.Failure(loaded.Error!);
    foreach (var entry in loaded.Value)
    {
        if (!entry.Spec.Enabled) continue;
        // The type comes from the file, not from a category lookup: the file is what somebody wrote, and a loader
        // that re-derives the type here would be a second opinion about which provider this directory configures.
        var createdProvider = await modelManager.CreateAsync(
            new ModelProviderSpec(entry.Provider, entry.Spec.Configuration.GetRawText(), entry.TypeId),
            CancellationToken.None);
        if (!createdProvider.Ok || createdProvider.Value is null) return Result.Failure(createdProvider.Error!);
        var providerAddress = createdProvider.Value.Address;
        foreach (var profile in entry.Spec.Profiles ?? [])
        {
            // `with` rather than a fresh record: the file names every field, and this line's only business is swapping in
            // the provider address the process just created. Rebuilding it field by field is how Capabilities was lost —
            // a profile that declared Vision in its module.json came back as a model that could not see, and every
            // picture became a receipt with no way to tell why.
            var registered = modelManager.RegisterProfile(profile with { Provider = providerAddress });
            if (!registered.Ok) return registered;
        }
    }
    // A window nobody declared is planned against the floor, and the receipt says which profiles are in that state:
    // "it silently assumed a number" and "somebody chose that number" look identical from outside otherwise.
    var profiles = modelManager.ListProfiles();
    var assumed = profiles.Where(profile => profile.ContextWindowAssumed).Select(profile => profile.Id).ToArray();
    // Eyes, said out loud. A profile that cannot see is not a broken deployment — most models cannot — but it decides
    // that every picture becomes a receipt, and until now the only way to learn that was to send one and wonder. The
    // asymmetry the design rests on (a missing sentence costs a sentence, a refused request costs every request after
    // it) is only a good trade if the fact is visible somewhere.
    var seeing = profiles.Where(profile => profile.CanSeeImages).Select(profile => profile.Id).ToArray();
    restore.Note("models", $"{profiles.Count} profile(s)" + (assumed.Length == 0
        ? ", all declare their context window"
        : $"; context window unsaid for {string.Join(", ", assumed)} — those plan against {ModelProfile.AssumedContextWindow / 1024}k")
        + (seeing.Length == 0
            ? "; no profile declares Vision — every picture will arrive as a receipt"
            : $"; can see pictures: {string.Join(", ", seeing)}"));
    return Result.Success();
}

// Channels are created by scripts and persisted; startup only revives what already exists.
// A conversation folder with no channel is not a channel — the next message routes into a new one.
// Channels are revived by their manager when the lifecycle signal arrives; the host keeps no list.

/// <summary>
/// The scopes a module's own file declares, in either shape that file has ever had: the object written now, or the
/// escaped string an older version wrote. Read-only and best-effort — this answers "what will be offered", so a file
/// that cannot be read is reported by the caller as "nothing declared" rather than taken as an empty whitelist.
/// </summary>
static string[] ReadDeclaredScopes(string manifestPath)
{
    if (!File.Exists(manifestPath)) return [];
    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (root.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
            return ScopesOf(config);
        if (root.TryGetProperty("configJson", out var legacy) && legacy.ValueKind == JsonValueKind.String)
        {
            using var inner = JsonDocument.Parse(legacy.GetString()!);
            return inner.RootElement.ValueKind == JsonValueKind.Object ? ScopesOf(inner.RootElement) : [];
        }
        return [];
    }
    catch (Exception ex) when (ex is IOException or JsonException)
    {
        return [];
    }
}

static string[] ScopesOf(JsonElement config) =>
    config.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
        ? scopes.EnumerateArray().Where(scope => scope.ValueKind == JsonValueKind.String)
            .Select(scope => scope.GetString()!).ToArray()
        : [];

sealed class HostServiceProvider(params (Type Type, object Service)[] services) : IServiceProvider
{
    private readonly IReadOnlyDictionary<Type, object> _services = services.ToDictionary(x => x.Type, x => x.Service);
    public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
}
