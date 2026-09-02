using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Core;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public enum BrowserCollectorRuntimeStatus
{
    Waiting,
    Ready,
    Degraded
}

public sealed record BrowserCollectorAppRuntimeSnapshot(
    string AppHint,
    Guid CollectorInstanceId,
    bool DesiredEnabled,
    BrowserCollectorRuntimeStatus RuntimeStatus,
    string RuntimeStatusDetail,
    string PackageVersion,
    string PackageContentHash);

public sealed record BrowserCollectorRuntimeSnapshot(
    bool IsInstalled,
    string? PackageVersion,
    string? PackageContentHash,
    string? InstallDirectory,
    string? SideloadDirectory,
    bool DesiredEnabled,
    BrowserCollectorRuntimeStatus RuntimeStatus,
    string RuntimeStatusDetail,
    bool ReloadRequired,
    string? PreviousKnownGoodVersion,
    IReadOnlyList<BrowserCollectorAppRuntimeSnapshot> Apps);

/// <summary>
/// Owns the Desktop Hub's exact local browser Package installations and the stable browser App
/// Collector Instances. A copied directory is only an Installation; an App Instance is created
/// only after a stable appHint is discovered or explicitly configured.
/// </summary>
public sealed class BrowserCollectorRuntime
{
    public const string BrowserPackageId = "heartbeat.collector.browser";
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly CollectorRuntime _runtime;
    private readonly BrowserExternalHostBindingOptions _options;
    private readonly SubjectReference _subject;
    private readonly CollectorPackageInstallations _installations;
    private readonly string _statePath;
    private BrowserRuntimeState _state;
    private BrowserCollectorRuntimeStatus _runtimeStatus;
    private string _runtimeStatusDetail;
    private readonly Dictionary<string, AppRuntimeStatus> _appStatuses = new(StringComparer.Ordinal);

    public BrowserCollectorRuntime(
        CollectorRuntime runtime,
        IDeviceIdentity deviceIdentity,
        BrowserExternalHostBindingOptions options)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(deviceIdentity);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
            throw new ArgumentException("Browser Collector Runtime requires a data directory.", nameof(options));
        if (!Guid.TryParse(deviceIdentity.HardwareId, out var subjectId) || subjectId == Guid.Empty)
            throw new InvalidOperationException("Browser Collector requires a UUID machine identity.");

        _runtime = runtime;
        _options = options;
        _subject = new SubjectReference(subjectId, SubjectKind.Machine);
        _installations = new CollectorPackageInstallations(
            Path.Combine(Path.GetFullPath(options.DataDirectory), "collector-packages"));
        _statePath = Path.Combine(Path.GetFullPath(options.DataDirectory), "browser-package-state.json");
        // Browser 是可选 Collector：本地安装账本读不出来或 schema 不认，只能让 Browser 自己 Degraded，
        // 绝不能把宿主组合打断（ADR-048）。坏账本保持原样不覆写，等 owner 显式重新安装。
        string? installationValidationError;
        try
        {
            _state = LoadState();
            installationValidationError = ValidatePersistedState();
        }
        catch (CollectorRuntimeStateException exception)
        {
            _state = new BrowserRuntimeState();
            installationValidationError = $"本地 Package 安装状态不可用：{exception.Message}";
        }
        _runtimeStatus = installationValidationError is null
            ? BrowserCollectorRuntimeStatus.Waiting
            : BrowserCollectorRuntimeStatus.Degraded;
        _runtimeStatusDetail = installationValidationError ?? (PendingReloadAfterKnownGood()
            ? "新版本已安装；请在浏览器扩展页重新加载。"
            : "等待浏览器加载旁加载目录并建立连接。");
    }

    public BrowserCollectorRuntimeSnapshot Current
    {
        get
        {
            lock (_gate)
                return BuildSnapshotLocked();
        }
    }

    public event Action<BrowserCollectorRuntimeSnapshot>? Changed;
    internal event Action<string, bool>? AppDesiredEnabledChanged;

    /// <summary>
    /// 若宿主随身带了一份本地 Package source，就把它安装成运行用 Installation。Browser 独立发布，
    /// 所以"没有配置 source"、"source 目录不存在"、"source 目录内容损坏"都是合法状态：宿主分别保持
    /// 未安装或 Degraded，不抛异常（ADR-048）。
    /// </summary>
    public BrowserCollectorRuntimeSnapshot EnsureBundledPackageInstalled()
    {
        var source = _options.PackageDirectory;
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            return Current;
        try
        {
            return Import(source);
        }
        catch (Exception exception) when (exception
            is PackageValidationException
            or CollectorRuntimeStateException
            or IOException
            or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _runtimeStatus = BrowserCollectorRuntimeStatus.Degraded;
                _runtimeStatusDetail = $"随宿主分发的 Package source 不可用：{exception.Message}";
                return BuildSnapshotLocked();
            }
        }
    }

    public BrowserCollectorRuntimeSnapshot Import(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        var sourcePackage = LocalCollectorPackage.Load(packageDirectory);
        ValidateBrowserPackage(sourcePackage);
        var sourceSideloadRelativePath = ResolveSideloadRelativePath(sourcePackage);

        // 复制、tree hash 校验、目录布局与失败清理归共享 Installation module；这里只补 browser 专属
        // 契约：已安装副本仍必须解析出同一个 sideload 目录。
        var installation = _installations.Install(sourcePackage.PackageDirectory);
        var installed = installation.Package;
        ValidateBrowserPackage(installed);
        if (ResolveSideloadRelativePath(installed) != sourceSideloadRelativePath)
            throw new PackageValidationException(
                "Installed browser Collector Package resolves a different sideload directory.");

        BrowserCollectorRuntimeSnapshot snapshot;
        lock (_gate)
        {
            var nextInstallation = new PackageInstallationState
            {
                Version = installation.Reference.Version,
                PackageContentHash = installation.Reference.PackageContentHash,
                TreeContentHash = installation.TreeContentHash,
                InstallDirectory = installation.Directory,
                SideloadRelativePath = sourceSideloadRelativePath
            };
            var previousKnownGood = _state.PreviousKnownGood;
            if (_state.KnownGood is not null &&
                _state.KnownGood.PackageContentHash != nextInstallation.PackageContentHash)
                previousKnownGood = _state.KnownGood;
            _state = new BrowserRuntimeState
            {
                Current = nextInstallation,
                KnownGood = _state.KnownGood,
                PreviousKnownGood = previousKnownGood
            };
            SaveStateLocked();
            if (PendingReloadAfterKnownGood() && _runtimeStatus == BrowserCollectorRuntimeStatus.Ready)
            {
                _runtimeStatusDetail = "当前浏览器 Activation 仍 Ready；新版本等待用户 reload。";
            }
            else
            {
                _runtimeStatus = BrowserCollectorRuntimeStatus.Waiting;
                _runtimeStatusDetail = PendingReloadAfterKnownGood()
                    ? "新版本已安装；等待用户 reload 后建立新 Activation。"
                    : "Package 已安装；请在浏览器扩展页旁加载所示目录。";
            }
            snapshot = BuildSnapshotLocked();
        }
        Changed?.Invoke(snapshot);
        return snapshot;
    }

    public void SetAppDesiredEnabled(string appHint, bool enabled)
    {
        BrowserCollectorRuntimeSnapshot snapshot;
        lock (_gate)
        {
            var package = LoadCurrentPackageLocked();
            var instance = GetOrCreateAppInstanceLocked(appHint, package);
            if (ReadDesiredEnabled(instance) == enabled)
                return;
            using var config = JsonDocument.Parse(
                $$"""{"enabled":{{enabled.ToString().ToLowerInvariant()}},"flushPeriodMs":{{_options.FlushPeriodMilliseconds}}}""");
            _runtime.UpdateInstanceSpec(instance.CollectorInstanceId, 1, config.RootElement.Clone());
            _appStatuses[NormalizeAppHint(appHint)] = new AppRuntimeStatus(
                BrowserCollectorRuntimeStatus.Waiting,
                enabled ? "已启用；等待浏览器建立连接。" : "已停用；App Instance 已保留。");
            snapshot = BuildSnapshotLocked();
        }
        Changed?.Invoke(snapshot);
        AppDesiredEnabledChanged?.Invoke(NormalizeAppHint(appHint), enabled);
    }

    public void SetAllAppsDesiredEnabled(bool enabled)
    {
        string[] appHints;
        lock (_gate)
            appHints = FindAppInstancesLocked().Select(instance => instance.InstanceKey!).ToArray();
        foreach (var appHint in appHints)
            SetAppDesiredEnabled(appHint, enabled);
    }

    internal LocalCollectorPackage ResolvePackage(string artifactId, string artifactHash)
    {
        lock (_gate)
        {
            foreach (var installation in _installations.List(BrowserPackageId))
            {
                var package = installation.Package;
                try
                {
                    ValidateBrowserPackage(package);
                }
                catch (PackageValidationException)
                {
                    continue;
                }
                if (package.Artifacts.Any(artifact =>
                        artifact.ArtifactId == artifactId &&
                        artifact.ContentHash == artifactHash))
                    return package;
            }
            throw new PackageValidationException(
                "ExternalHost Artifact does not match any installed browser Collector Package.");
        }
    }

    internal CollectorInstance GetOrCreateAppInstance(string appHint, LocalCollectorPackage package)
    {
        lock (_gate)
            return GetOrCreateAppInstanceLocked(appHint, package);
    }

    internal bool IsAppDesiredEnabled(string appHint)
    {
        lock (_gate)
        {
            var instance = _runtime.FindInstance(BrowserPackageId, _subject, NormalizeAppHint(appHint));
            return instance is null || ReadDesiredEnabled(instance);
        }
    }

    internal void MarkReady(string appHint, string packageContentHash)
    {
        BrowserCollectorRuntimeSnapshot snapshot;
        lock (_gate)
        {
            if (_state.Current is not null && _state.Current.PackageContentHash == packageContentHash)
            {
                _state = new BrowserRuntimeState
                {
                    Current = _state.Current,
                    KnownGood = _state.Current,
                    PreviousKnownGood = _state.PreviousKnownGood
                };
                SaveStateLocked();
            }
            _appStatuses[NormalizeAppHint(appHint)] = new AppRuntimeStatus(
                BrowserCollectorRuntimeStatus.Ready,
                "浏览器 App Collector 已完成协议协商并就绪。");
            snapshot = BuildSnapshotLocked();
        }
        Changed?.Invoke(snapshot);
    }

    internal void MarkWaiting(string appHint, string detail)
    {
        BrowserCollectorRuntimeSnapshot snapshot;
        lock (_gate)
        {
            _appStatuses[NormalizeAppHint(appHint)] = new AppRuntimeStatus(
                BrowserCollectorRuntimeStatus.Waiting,
                detail);
            snapshot = BuildSnapshotLocked();
        }
        Changed?.Invoke(snapshot);
    }

    internal void MarkDegraded(string appHint, string detail)
    {
        BrowserCollectorRuntimeSnapshot snapshot;
        lock (_gate)
        {
            _appStatuses[NormalizeAppHint(appHint)] = new AppRuntimeStatus(
                BrowserCollectorRuntimeStatus.Degraded,
                detail);
            snapshot = BuildSnapshotLocked();
        }
        Changed?.Invoke(snapshot);
    }

    private CollectorInstance GetOrCreateAppInstanceLocked(string appHint, LocalCollectorPackage package)
    {
        var key = NormalizeAppHint(appHint);
        var existing = _runtime.FindInstance(BrowserPackageId, _subject, key);
        if (existing is not null)
            return existing;
        using var config = JsonDocument.Parse(
            $$"""{"enabled":true,"flushPeriodMs":{{_options.FlushPeriodMilliseconds}}}""");
        return _runtime.CreateInstance(
            package,
            _subject,
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()),
            key);
    }

    private BrowserCollectorRuntimeSnapshot BuildSnapshotLocked()
    {
        // 未安装同样可以是 Degraded：安装账本坏掉或随宿主的 source 装不上时，要把原因带给 owner，
        // 而不是让 UI 看起来像"从来没装过"。
        if (_state.Current is null)
            return new BrowserCollectorRuntimeSnapshot(
                false, null, null, null, null, true,
                _runtimeStatus == BrowserCollectorRuntimeStatus.Degraded
                    ? BrowserCollectorRuntimeStatus.Degraded
                    : BrowserCollectorRuntimeStatus.Waiting,
                _runtimeStatus == BrowserCollectorRuntimeStatus.Degraded
                    ? _runtimeStatusDetail
                    : "尚未导入 browser Collector Package。",
                false,
                null,
                []);

        try
        {
            _ = LoadCurrentPackageLocked();
            return BuildInstalledSnapshotLocked();
        }
        // 安装副本与登记事实不一致（CollectorRuntimeStateException），以及安装副本自身校验失败
        // （PackageValidationException），都只降级为 Degraded：Installation 坏掉不该把 host 打崩。
        catch (Exception exception) when (exception is PackageValidationException or CollectorRuntimeStateException)
        {
            _runtimeStatus = BrowserCollectorRuntimeStatus.Degraded;
            _runtimeStatusDetail = $"Installed Package content validation failed: {exception.Message}";
            return BuildInstalledSnapshotLocked();
        }
    }

    private BrowserCollectorRuntimeSnapshot BuildInstalledSnapshotLocked()
    {
        var apps = FindAppInstancesLocked()
            .Select(instance =>
            {
                var status = _appStatuses.GetValueOrDefault(
                    instance.InstanceKey!,
                    new AppRuntimeStatus(
                        BrowserCollectorRuntimeStatus.Waiting,
                        "等待该浏览器 App 的 ExternalHost 建立连接。"));
                return new BrowserCollectorAppRuntimeSnapshot(
                    instance.InstanceKey!,
                    instance.CollectorInstanceId,
                    ReadDesiredEnabled(instance),
                    status.Status,
                    status.Detail,
                    instance.PackageVersion,
                    instance.PackageContentHash);
            })
            .OrderBy(app => app.AppHint, StringComparer.Ordinal)
            .ToArray();
        var aggregateStatus = apps.Any(app => app.RuntimeStatus == BrowserCollectorRuntimeStatus.Degraded)
            ? BrowserCollectorRuntimeStatus.Degraded
            : apps.Any(app => app.RuntimeStatus == BrowserCollectorRuntimeStatus.Ready)
                ? BrowserCollectorRuntimeStatus.Ready
                : _runtimeStatus;
        var aggregateDetail = apps.Length == 0
            ? _runtimeStatusDetail
            : $"已发现 {apps.Length} 个浏览器 App Instance。";
        return new BrowserCollectorRuntimeSnapshot(
            true,
            _state.Current!.Version,
            _state.Current.PackageContentHash,
            _state.Current.InstallDirectory,
            Path.Combine(_state.Current.InstallDirectory, PathFromPortable(_state.Current.SideloadRelativePath)),
            apps.Length == 0 || apps.All(app => app.DesiredEnabled),
            aggregateStatus,
            aggregateDetail,
            _state.KnownGood is not null &&
                _state.KnownGood.PackageContentHash != _state.Current.PackageContentHash,
            _state.PreviousKnownGood?.Version,
            apps);
    }

    private LocalCollectorPackage LoadCurrentPackageLocked()
    {
        if (_state.Current is null)
            throw new InvalidOperationException("No browser Collector Package is installed.");
        return LoadAndVerifyInstallation(_state.Current);
    }

    private static bool ReadDesiredEnabled(CollectorInstance instance) =>
        !instance.Spec.Config.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean();

    private IReadOnlyList<CollectorInstance> FindAppInstancesLocked() =>
        _runtime.FindInstances(BrowserPackageId, _subject)
            .Where(instance => instance.InstanceKey is not null)
            .ToArray();

    internal static string NormalizeAppHint(string appHint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHint);
        var normalized = appHint.Trim().ToLowerInvariant();
        if (normalized.Length > 64 || !char.IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            throw new ArgumentException(
                "appHint must be a stable slug of at most 64 ASCII letters, digits, '.', '_' or '-'.",
                nameof(appHint));
        return normalized;
    }

    private bool PendingReloadAfterKnownGood() =>
        _state.Current is not null &&
        _state.KnownGood is not null &&
        _state.Current.PackageContentHash != _state.KnownGood.PackageContentHash;

    private static void ValidateBrowserPackage(LocalCollectorPackage package)
    {
        if (package.Manifest.PackageId != BrowserPackageId)
            throw new PackageValidationException(
                $"Expected browser Collector Package '{BrowserPackageId}', found '{package.Manifest.PackageId}'.");
        var os = CurrentOperatingSystem();
        var architecture = CurrentArchitecture();
        var candidates = package.Manifest.Artifacts.Count(artifact =>
            artifact.Driver == "externalHost" &&
            artifact.OperatingSystems.Contains(os, StringComparer.Ordinal) &&
            artifact.Architectures.Contains(architecture, StringComparer.Ordinal));
        if (candidates != 1)
            throw new PackageValidationException(
                $"Browser Collector Package must select exactly one current platform externalHost Artifact; found {candidates} for {os}/{architecture}.");
    }

    private static string ResolveSideloadRelativePath(LocalCollectorPackage package)
    {
        var os = CurrentOperatingSystem();
        var architecture = CurrentArchitecture();
        var artifact = package.Manifest.Artifacts.Single(candidate =>
            candidate.Driver == "externalHost" &&
            candidate.OperatingSystems.Contains(os, StringComparer.Ordinal) &&
            candidate.Architectures.Contains(architecture, StringComparer.Ordinal));
        var verifiedArtifact = package.Artifacts.Single(candidate => candidate.ArtifactId == artifact.ArtifactId);
        using var document = JsonDocument.Parse(verifiedArtifact.Content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["kind", "entrypoint", "files"]) is false ||
            !root.TryGetProperty("kind", out var kind) ||
            kind.GetString() != "heartbeat.browser.external-host" ||
            !root.TryGetProperty("entrypoint", out var entrypointElement) ||
            entrypointElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array ||
            filesElement.GetArrayLength() == 0)
            throw new PackageValidationException("Browser externalHost Artifact descriptor is invalid.");
        var entrypoint = entrypointElement.GetString()!;
        var manifestPath = ResolvePortablePackageFile(package.PackageDirectory, entrypoint);
        if (!File.Exists(manifestPath) ||
            !string.Equals(Path.GetFileName(manifestPath), "manifest.json", StringComparison.Ordinal))
            throw new PackageValidationException("Browser sideload entrypoint must resolve to an existing manifest.json.");
        var sideloadRelativePath = Path.GetDirectoryName(entrypoint)!.Replace(Path.DirectorySeparatorChar, '/');
        var sideloadDirectory = Path.Combine(package.PackageDirectory, PathFromPortable(sideloadRelativePath));
        var declaredFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            if (fileElement.ValueKind != JsonValueKind.Object ||
                fileElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(["path", "size", "contentHash"]) is false ||
                !fileElement.TryGetProperty("path", out var pathElement) ||
                pathElement.ValueKind != JsonValueKind.String ||
                !fileElement.TryGetProperty("size", out var sizeElement) ||
                !sizeElement.TryGetInt64(out var expectedSize) || expectedSize < 0 ||
                !fileElement.TryGetProperty("contentHash", out var hashElement) ||
                hashElement.ValueKind != JsonValueKind.String ||
                hashElement.GetString() is not { } expectedHash || !IsSha256(expectedHash))
                throw new PackageValidationException("Browser externalHost Artifact file declaration is invalid.");
            var relativePath = pathElement.GetString()!;
            if (!declaredFiles.Add(relativePath) ||
                !relativePath.StartsWith(sideloadRelativePath + "/", StringComparison.Ordinal))
                throw new PackageValidationException("Browser externalHost Artifact files must uniquely belong to the sideload directory.");
            var path = ResolvePortablePackageFile(package.PackageDirectory, relativePath);
            if (!File.Exists(path))
                throw new PackageValidationException($"Declared browser Artifact file does not exist: {relativePath}.");
            var content = File.ReadAllBytes(path);
            var actualHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
            if (content.LongLength != expectedSize || actualHash != expectedHash)
                throw new PackageValidationException(
                    $"Declared browser Artifact file '{relativePath}' does not match its size or content hash.");
        }

        var metadataPath = Path.Combine(sideloadDirectory, "collector-artifact-ref.json");
        if (!File.Exists(metadataPath))
            throw new PackageValidationException("Browser sideload directory is missing collector-artifact-ref.json.");
        using (var metadata = JsonDocument.Parse(File.ReadAllBytes(metadataPath)))
        {
            var metadataRoot = metadata.RootElement;
            if (metadataRoot.ValueKind != JsonValueKind.Object ||
                metadataRoot.EnumerateObject().Count() != 1 ||
                !metadataRoot.TryGetProperty("artifactHash", out var artifactHash) ||
                artifactHash.GetString() != verifiedArtifact.ContentHash)
                throw new PackageValidationException("Browser collector-artifact-ref.json does not identify the verified Artifact descriptor.");
        }

        var actualPayloadFiles = Directory.EnumerateFiles(sideloadDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(package.PackageDirectory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !CollectorPackageInstallations.IsIgnorableMetadataFile(path))
            .Where(path => path != $"{sideloadRelativePath}/collector-artifact-ref.json")
            .ToHashSet(StringComparer.Ordinal);
        if (!actualPayloadFiles.SetEquals(declaredFiles))
            throw new PackageValidationException("Browser sideload directory contains undeclared executable payload files.");
        return sideloadRelativePath;
    }

    private static string ResolvePortablePackageFile(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
            throw new PackageValidationException("Browser Artifact path must be package-relative.");
        var segments = relativePath.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new PackageValidationException("Browser Artifact path escapes or aliases the Package root.");
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new PackageValidationException("Browser Artifact path escapes the Package root.");
        return path;
    }

    private BrowserRuntimeState LoadState()
    {
        if (!File.Exists(_statePath))
            return new BrowserRuntimeState();
        try
        {
            return JsonSerializer.Deserialize<BrowserRuntimeState>(
                File.ReadAllBytes(_statePath),
                StateJsonOptions) ?? throw new JsonException("Browser Package state is null.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new CollectorRuntimeStateException("Unable to load browser Package installation state.", exception);
        }
    }

    private string? ValidatePersistedState()
    {
        if (_state.SchemaVersion != 1)
            throw new CollectorRuntimeStateException(
                $"Unsupported browser Package state schemaVersion {_state.SchemaVersion}.");
        foreach (var installation in new[] { _state.Current, _state.KnownGood, _state.PreviousKnownGood })
        {
            if (installation is null)
                continue;
            if (string.IsNullOrWhiteSpace(installation.Version) ||
                !IsSha256(installation.PackageContentHash) ||
                !IsSha256(installation.TreeContentHash) ||
                string.IsNullOrWhiteSpace(installation.InstallDirectory) ||
                string.IsNullOrWhiteSpace(installation.SideloadRelativePath))
                throw new CollectorRuntimeStateException("Browser Package installation state is invalid.");
            try
            {
                _ = LoadAndVerifyInstallation(installation);
            }
            catch (Exception exception) when (exception is CollectorRuntimeStateException or PackageValidationException)
            {
                return $"Installed Package content validation failed: {exception.Message}";
            }
        }
        return null;
    }

    private LocalCollectorPackage LoadAndVerifyInstallation(PackageInstallationState installation)
    {
        CollectorPackageInstallation opened;
        try
        {
            opened = _installations.Open(new CollectorPackageReference(
                BrowserPackageId,
                installation.Version,
                installation.PackageContentHash));
        }
        catch (PackageValidationException exception)
        {
            throw new CollectorRuntimeStateException(
                "Installed browser Package content does not match persisted state.", exception);
        }
        if (opened.TreeContentHash != installation.TreeContentHash ||
            !string.Equals(opened.Directory, installation.InstallDirectory, StringComparison.Ordinal))
            throw new CollectorRuntimeStateException("Installed browser Package content does not match persisted state.");
        var package = opened.Package;
        ValidateBrowserPackage(package);
        _ = ResolveSideloadRelativePath(package);
        return package;
    }

    private void SaveStateLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(_state, StateJsonOptions));
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string PathFromPortable(string path) =>
        Path.Combine(path.Split('/'));

    private static bool IsSha256(string value) =>
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(char.IsAsciiHexDigitLower);

    private static string CurrentOperatingSystem() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        throw new PlatformNotSupportedException("Unsupported Collector Package operating system.");

    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("Unsupported Collector Package architecture.")
    };

    private sealed class BrowserRuntimeState
    {
        public int SchemaVersion { get; init; } = 1;
        public PackageInstallationState? Current { get; init; }
        public PackageInstallationState? KnownGood { get; init; }
        public PackageInstallationState? PreviousKnownGood { get; init; }
    }

    private sealed class PackageInstallationState
    {
        public string Version { get; init; } = string.Empty;
        public string PackageContentHash { get; init; } = string.Empty;
        public string TreeContentHash { get; init; } = string.Empty;
        public string InstallDirectory { get; init; } = string.Empty;
        public string SideloadRelativePath { get; init; } = string.Empty;
    }

    private sealed record AppRuntimeStatus(BrowserCollectorRuntimeStatus Status, string Detail);
}
