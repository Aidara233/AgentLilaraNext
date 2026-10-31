using ALN.Contracts;

namespace ALN.Runtime;

/// <summary>
/// Coordinates package unloads inside one host process. This class establishes
/// the readiness barrier; the caller remains responsible for unloading the
/// package's AssemblyLoadContext after the report says Completed.
/// </summary>
public sealed class ModuleUnloadCoordinator
{
    private readonly Dictionary<string, PackageEntry> _packages = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _gate = new();

    public Result RegisterPackage(ModulePackageManifest manifest)
    {
        var invalid = ValidateManifest(manifest);
        if (invalid is not null) return Result.Failure(invalid);

        lock (_gate)
        {
            if (_packages.ContainsKey(manifest.Id))
                return Result.Failure(new(ErrorCode.Conflict, $"Module package already registered: {manifest.Id}"));
            foreach (var dependency in manifest.Dependencies.Where(d => !d.Optional))
                if (_packages.TryGetValue(dependency.Id, out var entry) && entry.State != PackageState.Active)
                    return Result.Failure(new(ErrorCode.Unavailable, $"Dependency is unloading: {dependency.Id}"));
            _packages.Add(manifest.Id, new PackageEntry(manifest));
            return Result.Success();
        }
    }

    public Result UnregisterPackage(string packageId)
    {
        lock (_gate)
        {
            if (!_packages.TryGetValue(packageId, out var package))
                return Result.Failure(new(ErrorCode.Invalid, $"Module package not registered: {packageId}"));
            if (package.State != PackageState.Active || _packages.Values.Any(p => p.Manifest.Dependencies.Any(d => !d.Optional && d.Id == packageId)))
                return Result.Failure(new(ErrorCode.Conflict, $"Module package is active or still required: {packageId}"));
            _packages.Remove(packageId);
            return Result.Success();
        }
    }

    public Result RegisterParticipant(string packageId, IModuleUnloadParticipant participant)
    {
        if (participant is null || string.IsNullOrWhiteSpace(participant.ParticipantId))
            return Result.Failure(new(ErrorCode.Invalid, "Unload participant must have an id."));
        lock (_gate)
        {
            if (!_packages.TryGetValue(packageId, out var package))
                return Result.Failure(new(ErrorCode.Invalid, $"Module package not registered: {packageId}"));
            if (package.State != PackageState.Active)
                return Result.Failure(new(ErrorCode.Unavailable, $"Module package is unloading: {packageId}"));
            if (_packages.Values.SelectMany(p => p.Participants).Any(p => p.ParticipantId == participant.ParticipantId))
                return Result.Failure(new(ErrorCode.Conflict, $"Unload participant already registered: {participant.ParticipantId}"));
            package.Participants.Add(participant);
            return Result.Success();
        }
    }

    public Result UnregisterParticipant(string participantId)
    {
        lock (_gate)
        {
            var owner = _packages.Values.FirstOrDefault(p => p.Participants.Any(x => x.ParticipantId == participantId));
            if (owner is null) return Result.Failure(new(ErrorCode.Invalid, $"Unload participant not found: {participantId}"));
            if (owner.State != PackageState.Active)
                return Result.Failure(new(ErrorCode.Unavailable, $"Cannot unregister a participant while unloading: {participantId}"));
            owner.Participants.RemoveAll(p => p.ParticipantId == participantId);
            return Result.Success();
        }
    }

    public IReadOnlyList<ModulePackageManifest> ListPackages()
    {
        lock (_gate) return _packages.Values.Select(p => p.Manifest).ToArray();
    }

    public async ValueTask<Result<ModuleUnloadReport>> UnloadAsync(
        string packageId,
        TimeSpan preparationTimeout,
        bool force,
        CancellationToken cancellationToken,
        bool replacing = false)
    {
        if (preparationTimeout <= TimeSpan.Zero || preparationTimeout > TimeSpan.FromMinutes(10))
            return Result<ModuleUnloadReport>.Failure(new(ErrorCode.Invalid, "Preparation timeout must be greater than zero and at most 10 minutes."));

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PackageEntry> affected;
            List<IModuleUnloadParticipant> participants;
            lock (_gate)
            {
                if (!_packages.TryGetValue(packageId, out var target))
                    return Result<ModuleUnloadReport>.Failure(new(ErrorCode.Invalid, $"Module package not registered: {packageId}"));
                if (target.Manifest.PackageKind != ModulePackageKind.Plugin || target.Manifest.ReloadPolicy == ModuleReloadPolicy.None)
                    return Result<ModuleUnloadReport>.Failure(new(ErrorCode.Denied, $"Module package cannot be unloaded at runtime: {packageId}"));
                if (target.State == PackageState.Active)
                {
                    affected = GetDependentClosure(packageId);
                    if (affected.Any(p => p.State != PackageState.Active))
                        return Result<ModuleUnloadReport>.Failure(new(ErrorCode.Conflict, "An affected package is already in an unload operation."));
                }
                else if (target.State == PackageState.UnloadFailed)
                {
                    affected = GetDependentClosure(packageId);
                    if (affected.Any(p => p.State is not (PackageState.Active or PackageState.UnloadFailed)))
                        return Result<ModuleUnloadReport>.Failure(new(ErrorCode.Conflict, "An affected package is in an incompatible lifecycle state."));
                }
                else
                {
                    return Result<ModuleUnloadReport>.Failure(new(ErrorCode.Conflict, $"Module package is already unloading: {packageId}"));
                }
                var fixedDependents = affected.Where(p => p.Manifest.PackageKind != ModulePackageKind.Plugin ||
                    p.Manifest.ReloadPolicy == ModuleReloadPolicy.None).Select(p => p.Manifest.Id).ToArray();
                if (fixedDependents.Length > 0)
                    return Result<ModuleUnloadReport>.Failure(new(ErrorCode.Conflict,
                        $"Cannot unload while non-reloadable dependents are active: {string.Join(", ", fixedDependents)}"));
                foreach (var entry in affected) entry.State = PackageState.Draining;
                participants = affected.SelectMany(p => p.Participants).ToList();
            }

            var operationId = Guid.NewGuid();
            var deadline = DateTimeOffset.UtcNow + preparationTimeout;
            using var preparationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preparationCts.CancelAfter(preparationTimeout);
            var preparationTasks = participants.Select(p => PrepareAsync(p,
                new ModuleUnloadPreparation(operationId, deadline, replacing), preparationCts.Token)).ToArray();
            var preparationTimedOut = false;
            PreparationOutcome[] readiness;
            try
            {
                readiness = await Task.WhenAll(preparationTasks).WaitAsync(preparationTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                preparationTimedOut = true;
                preparationCts.Cancel();
                readiness = preparationTasks.Select((task, index) => task.IsCompletedSuccessfully
                    ? task.Result
                    : new PreparationOutcome(participants[index], false, "preparation did not stop before the deadline")).ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                preparationCts.Cancel();
                MarkUnloadFailed(affected);
                throw;
            }
            var prepared = readiness.Where(r => r.Ready).Select(r => r.Participant.ParticipantId).ToArray();
            var unready = readiness.Where(r => !r.Ready).Select(r => r.Participant.ParticipantId).ToArray();
            var errors = readiness.Where(r => r.Error is not null).Select(r => $"{r.Participant.ParticipantId}: {r.Error}").ToList();

            if ((unready.Length > 0 || preparationTimedOut) && !force)
            {
                var responsiveParticipants = preparationTasks.Select((task, index) => (task, participant: participants[index]))
                    .Where(x => x.task.IsCompletedSuccessfully).Select(x => x.participant);
                var resumeErrors = await ResumeAllAsync(responsiveParticipants, preparationTimeout).ConfigureAwait(false);
                errors.AddRange(resumeErrors);
                if (preparationTimedOut || resumeErrors.Count > 0) MarkUnloadFailed(affected);
                else RestoreActive(affected);
                return Result<ModuleUnloadReport>.Success(new(packageId, false, false,
                    affected.Select(p => p.Manifest.Id).ToArray(), prepared, [], unready, errors));
            }

            var disconnected = new List<string>();
            var disconnectFailed = false;
            foreach (var participant in participants)
            {
                try
                {
                    var result = await participant.DisconnectAsync(force, cancellationToken).AsTask()
                        .WaitAsync(preparationTimeout, cancellationToken).ConfigureAwait(false);
                    if (!result.Ok)
                    {
                        errors.Add($"{participant.ParticipantId}: {result.Error?.Message ?? "disconnect failed"}");
                        if (!force)
                        {
                            disconnectFailed = true;
                            MarkUnloadFailed(affected);
                            return Result<ModuleUnloadReport>.Success(new(packageId, false, false,
                                affected.Select(p => p.Manifest.Id).ToArray(), prepared, disconnected, unready, errors));
                        }
                    }
                    else disconnected.Add(participant.ParticipantId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    MarkUnloadFailed(affected);
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{participant.ParticipantId}: {ex.Message}");
                    if (!force)
                    {
                        disconnectFailed = true;
                        MarkUnloadFailed(affected);
                        return Result<ModuleUnloadReport>.Success(new(packageId, false, false,
                            affected.Select(p => p.Manifest.Id).ToArray(), prepared, disconnected, unready, errors));
                    }
                }
            }

            if (disconnectFailed)
            {
                MarkUnloadFailed(affected);
                return Result<ModuleUnloadReport>.Success(new(packageId, false, force,
                    affected.Select(p => p.Manifest.Id).ToArray(), prepared, disconnected, unready, errors));
            }

            lock (_gate)
            {
                foreach (var entry in affected) _packages.Remove(entry.Manifest.Id);
            }
            return Result<ModuleUnloadReport>.Success(new(packageId, true, force,
                affected.Select(p => p.Manifest.Id).ToArray(), prepared, disconnected, unready, errors));
        }
        finally { _operation.Release(); }
    }

    private List<PackageEntry> GetDependentClosure(string targetId)
    {
        var result = new List<PackageEntry>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (!visited.Add(id)) return;
            var dependents = _packages.Values
                .Where(p => p.State != PackageState.Unloading && p.Manifest.Dependencies.Any(d => !d.Optional && d.Id == id))
                .OrderBy(p => p.Manifest.Id, StringComparer.Ordinal)
                .ToArray();
            foreach (var dependent in dependents) Visit(dependent.Manifest.Id);
            if (_packages.TryGetValue(id, out var entry)) result.Add(entry);
        }
        Visit(targetId);
        return result;
    }

    private static async Task<PreparationOutcome> PrepareAsync(IModuleUnloadParticipant participant,
        ModuleUnloadPreparation preparation, CancellationToken cancellationToken)
    {
        try
        {
            var result = await participant.PrepareUnloadAsync(preparation, cancellationToken).ConfigureAwait(false);
            if (!result.Ok) return new(participant, false, result.Error?.Message ?? "preparation failed");
            var readiness = result.Value;
            return readiness is { Ready: true }
                ? new(participant, true, null)
                : new(participant, false, readiness?.Detail ?? "participant is not ready");
        }
        catch (Exception ex)
        {
            return new(participant, false, ex.Message);
        }
    }

    private static async Task<List<string>> ResumeAllAsync(IEnumerable<IModuleUnloadParticipant> participants, TimeSpan timeout)
    {
        var errors = new List<string>();
        foreach (var participant in participants)
        {
            try
            {
                var result = await participant.ResumeAfterUnloadCancelledAsync(CancellationToken.None).AsTask()
                    .WaitAsync(timeout).ConfigureAwait(false);
                if (!result.Ok) errors.Add($"{participant.ParticipantId}: resume failed: {result.Error?.Message}");
            }
            catch (Exception ex) { errors.Add($"{participant.ParticipantId}: resume failed: {ex.Message}"); }
        }
        return errors;
    }

    private void RestoreActive(IEnumerable<PackageEntry> affected)
    {
        lock (_gate) foreach (var entry in affected) entry.State = PackageState.Active;
    }

    private void MarkUnloadFailed(IEnumerable<PackageEntry> affected)
    {
        lock (_gate) foreach (var entry in affected) entry.State = PackageState.UnloadFailed;
    }

    private static Error? ValidateManifest(ModulePackageManifest? manifest)
    {
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Version) ||
            manifest.Provides is null || manifest.Dependencies is null ||
            manifest.Provides.Any(t => t is null || string.IsNullOrWhiteSpace(t.TypeId)) || manifest.Dependencies.Any(d => d is null ||
                string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.VersionRange)))
            return new(ErrorCode.Invalid, "Module package manifest is incomplete.");
        if (manifest.Provides.Select(t => t.TypeId).Distinct(StringComparer.Ordinal).Count() != manifest.Provides.Count ||
            manifest.Dependencies.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Dependencies.Count)
            return new(ErrorCode.Conflict, "Module package manifest contains duplicate provides or dependencies.");
        if (manifest.PackageKind == ModulePackageKind.Plugin && manifest.ReloadPolicy == ModuleReloadPolicy.None)
            return new(ErrorCode.Invalid, "Plugin package must declare a runtime reload policy.");
        if (manifest.PackageKind != ModulePackageKind.Plugin && manifest.ReloadPolicy != ModuleReloadPolicy.None)
            return new(ErrorCode.Invalid, "Core and bundled packages cannot declare runtime replacement.");
        return null;
    }

    private sealed class PackageEntry(ModulePackageManifest manifest)
    {
        public ModulePackageManifest Manifest { get; } = manifest;
        public List<IModuleUnloadParticipant> Participants { get; } = [];
        public PackageState State { get; set; } = PackageState.Active;
    }

    private enum PackageState { Active, Draining, UnloadFailed, Unloading }
    private sealed record PreparationOutcome(IModuleUnloadParticipant Participant, bool Ready, string? Error);
}
