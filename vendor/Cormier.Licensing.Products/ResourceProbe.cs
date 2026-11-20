using System.Diagnostics;
using System.Globalization;

namespace Cormier.Licensing.Products;

/// <summary>Reads what the instance may use (container limits first) and what it uses.</summary>
public interface IResourceProbe
{
    /// <summary>Resources available to the instance.</summary>
    ResourceAmount Resources();
    /// <summary>Usage since the previous call.</summary>
    ResourceAmount Usage();
}

/// <summary>
/// cgroup v2/v1 aware probe that measures the process's own cgroup (from /proc/self/cgroup)
/// and applies the tightest limit on its path; falls back to the host and the current process.
/// </summary>
public sealed class ResourceProbe : IResourceProbe
{
    private readonly Func<string, string?> read;
    private readonly string root;
    private readonly bool linux;
    private readonly int version;
    private readonly double hostCores;
    private readonly long hostMemory;
    private readonly TimeProvider time;
    private readonly ResourceAmount? overrideAmount;
    private readonly Dictionary<string, string> membership = new(StringComparer.Ordinal);
    private readonly string? cpuMount, acctMount;
    private (long At, double CpuMicros) last;

    /// <summary>Creates a probe. Parameters exist for tests and operator overrides.</summary>
    public ResourceProbe(
        TimeProvider? time = null,
        ResourceAmount? overrideResources = null,
        Func<string, string?>? readFile = null,
        bool? isLinux = null,
        double? hostCores = null,
        long? hostMemoryBytes = null,
        string cgroupRoot = "/sys/fs/cgroup",
        string procSelfCgroup = "/proc/self/cgroup")
    {
        this.time = time ?? TimeProvider.System;
        read = readFile ?? (path => { try { return File.ReadAllText(path); } catch { return null; } });
        root = cgroupRoot.TrimEnd('/');
        linux = isLinux ?? OperatingSystem.IsLinux();
        this.hostCores = hostCores ?? Environment.ProcessorCount;
        hostMemory = hostMemoryBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        overrideAmount = overrideResources;
        // cgroup v1 may mount CPU controllers together (cpu,cpuacct).
        cpuMount = new[] { "cpu", "cpu,cpuacct", "cpuacct,cpu" }.FirstOrDefault(name => Text($"{name}/cpu.cfs_period_us") is not null);
        acctMount = new[] { "cpuacct", "cpu,cpuacct", "cpuacct,cpu" }.FirstOrDefault(name => Text($"{name}/cpuacct.usage") is not null);
        version = Text("cgroup.controllers") is not null ? 2
            : cpuMount is not null || Text("memory/memory.limit_in_bytes") is not null ? 1 : 0;
        // The process's own cgroup, so a service in a subgroup such as
        // /system.slice/proof.service is measured on its own.
        foreach (var line in (linux ? read(procSelfCgroup) : null)?.Split('\n') ?? [])
        {
            var parts = line.Trim().Split(':', 3);
            if (parts.Length == 3 && parts[2].StartsWith('/'))
                foreach (var name in parts[1].Split(',')) membership[name] = parts[2];
        }
        last = (this.time.GetTimestamp(), CpuMicros());
    }

    private string? Text(string path) => linux ? read($"{root}/{path}")?.Trim() : null;

    private string? At(string dir, string file) => Text(dir.Length > 0 ? $"{dir}/{file}" : file);

    /// <summary>
    /// Directories from the process's cgroup up to the mount root, leaf first, that hold
    /// <paramref name="probe"/>. A listed path that is not mounted (no cgroup namespace) falls back to the root.
    /// </summary>
    private List<string> Chain(string mount, string controller, string probe)
    {
        var segments = (membership.GetValueOrDefault(controller) ?? "/").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var dirs = new List<string>();
        for (var i = segments.Length; i >= 0; i--)
        {
            var dir = string.Join('/', new[] { mount }.Concat(segments.Take(i)).Where(part => part.Length > 0));
            if (At(dir, probe) is not null) dirs.Add(dir);
        }
        return dirs;
    }

    private static double? Positive(double value) => double.IsFinite(value) && value > 0 ? value : null;
    private static double? Lower(double? current, double? value) => value is null ? current : Math.Min(current ?? double.PositiveInfinity, value.Value);
    private static double Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

    /// <inheritdoc />
    public ResourceAmount Resources()
    {
        if (overrideAmount is { } o && (o.Cores > 0 || o.MemoryBytes > 0))
            return new()
            {
                Cores = Math.Min(o.Cores > 0 ? o.Cores : hostCores, hostCores),
                MemoryBytes = Math.Min(o.MemoryBytes > 0 ? o.MemoryBytes : hostMemory, hostMemory),
                Source = "configured",
            };
        double? cores = null, memory = null;
        if (version == 2)
        {
            foreach (var dir in Chain("", "", "cpu.max"))
            {
                var parts = (At(dir, "cpu.max") ?? "max").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts[0] != "max" && parts.Length > 1) cores = Lower(cores, Positive(Number(parts[0]) / Number(parts[1])));
            }
            foreach (var dir in Chain("", "", "memory.max"))
            {
                var max = At(dir, "memory.max");
                if (max is not null && max != "max") memory = Lower(memory, Positive(Number(max)));
            }
        }
        else if (version == 1)
        {
            foreach (var dir in cpuMount is null ? [] : Chain(cpuMount, "cpu", "cpu.cfs_quota_us"))
            {
                var quota = Number(At(dir, "cpu.cfs_quota_us"));
                if (quota > 0) cores = Lower(cores, Positive(quota / Number(At(dir, "cpu.cfs_period_us"))));
            }
            foreach (var dir in Chain("memory", "memory", "memory.limit_in_bytes"))
                memory = Lower(memory, Positive(Number(At(dir, "memory.limit_in_bytes"))));
        }
        return new()
        {
            Cores = Math.Min(cores ?? hostCores, hostCores),
            MemoryBytes = (long)Math.Min(memory ?? hostMemory, hostMemory),
            Source = cores is null && memory is null ? "host" : version == 2 ? "cgroup-v2" : "cgroup-v1",
        };
    }

    private double CpuMicros()
    {
        if (version == 2 && Chain("", "", "cpu.stat") is [var dir, ..])
        {
            foreach (var line in (At(dir, "cpu.stat") ?? "").Split('\n'))
                if (line.StartsWith("usage_usec ", StringComparison.Ordinal)) return Number(line[11..]);
        }
        else if (version == 1 && acctMount is not null && Chain(acctMount, "cpuacct", "cpuacct.usage") is [var acct, ..])
        {
            var nanos = Number(At(acct, "cpuacct.usage"));
            if (double.IsFinite(nanos) && nanos >= 0) return nanos / 1000;
        }
        return Process.GetCurrentProcess().TotalProcessorTime.TotalMicroseconds;
    }

    private long MemoryBytes()
    {
        var value = version switch
        {
            2 when Chain("", "", "memory.current") is [var dir, ..] => Number(At(dir, "memory.current")),
            1 when Chain("memory", "memory", "memory.usage_in_bytes") is [var dir, ..] => Number(At(dir, "memory.usage_in_bytes")),
            _ => double.NaN,
        };
        return double.IsFinite(value) && value >= 0 ? (long)value : Environment.WorkingSet;
    }

    /// <inheritdoc />
    public ResourceAmount Usage()
    {
        var at = time.GetTimestamp();
        var cpu = CpuMicros();
        var elapsed = time.GetElapsedTime(last.At, at).TotalMicroseconds;
        var cores = elapsed > 0 ? Math.Max(0, (cpu - last.CpuMicros) / elapsed) : 0;
        last = (at, cpu);
        return new() { Cores = Math.Round(cores, 3), MemoryBytes = MemoryBytes(), MeasuredAt = time.GetUtcNow() };
    }
}
