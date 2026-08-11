using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Services;

public class AdministrationOptions
{
    public const string Section = "Administration";
    public List<string> Subjects { get; set; } = [];
}

public class AdminAuthorizationService(IOptions<AdministrationOptions> options)
{
    private readonly HashSet<string> _subjects = options.Value.Subjects
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim())
        .ToHashSet(StringComparer.Ordinal);

    public bool IsAdmin(string subject) => _subjects.Contains(subject);
}
