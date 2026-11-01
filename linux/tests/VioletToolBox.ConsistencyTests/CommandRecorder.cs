namespace VioletToolBox.ConsistencyTests;

/// <summary>
/// Records the external process invocations a service attempts, replacing the real
/// ToolLocator/ProcessRunner resolution. This is what lets us assert *command-level*
/// parity with the upstream Windows tool without any device attached.
/// </summary>
public sealed class CommandRecorder
{
    public sealed record Invocation(string Exe, string Args);

    private readonly List<Invocation> _invocations = [];

    public IReadOnlyList<Invocation> Invocations => _invocations;

    internal void Record(string exe, string args) => _invocations.Add(new(exe, args));

    /// <summary>All invocations flattened as "exe args" lines for diff-friendly assertions.</summary>
    public IEnumerable<string> Script => _invocations.Select(i => $"{i.Exe} {i.Args}".Trim());
}
