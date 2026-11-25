using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BackupVerify;

internal enum CheckMode { Complete, Quick, Selected, Random }
internal enum Source { Nas, Usb, Cloud }
internal enum FindingKind { Difference, Error, Unavailable, Recovered }
internal sealed record Finding(Source Source, FindingKind Kind, string Group, string Path, string Detail);
internal sealed record SourceResult(Source Source, long Files, long Folders, int Checked, int Differences, int Errors, int Unavailable)
{
    public string Status => Errors > 0 ? "Error" : Differences > 0 ? "Diferencias" :
        Unavailable > 0 ? "No disponible" : Checked > 0 ? "Correcto" : "Sin verificar";
}
internal sealed record CheckResult(SourceResult[] Sources, Finding[] Findings, string? Output, string? Sample);

internal sealed class Verification(Settings settings, string? usbRoot)
{
    private sealed record Inventory(Dictionary<string, long> Files, HashSet<string> Folders);
    private sealed class Counts
    {
        public long Files;
        public long Folders;
        public int Checked;
    }

    private readonly List<Finding> findings = [];
    private readonly Dictionary<Source, Counts> counts = Enum.GetValues<Source>().ToDictionary(source => source, _ => new Counts());

    public async Task<CheckResult> Run(CheckMode mode, BackupSet[] sets, IProgress<string> progress, CancellationToken token)
    {
        string? output = null;
        string? sample = null;
        if (mode is CheckMode.Selected or CheckMode.Random)
        {
            var files = mode == CheckMode.Selected ? sets.SelectMany(set => set.TestFiles
                .Select(Paths.Relative).Distinct(StringComparer.OrdinalIgnoreCase).Select(path => (Set: set, Path: path))).ToList()
                : PickRandom(sets, token);
            if (files.Count == 0 && findings.Count == 0)
                Add(Source.Nas, FindingKind.Unavailable, "", "", mode == CheckMode.Selected
                    ? "Añade archivos a testFiles en config.json." : "No hay archivos para recuperar.");
            if (files.Count > 0)
            {
                output = Path.Combine(Settings.ChecksPath, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}"[..28]);
                Directory.CreateDirectory(output);
                if (mode == CheckMode.Random)
                    sample = $"{files[0].Set.Name} / {files[0].Path}";
                foreach (var (set, path) in files)
                {
                    progress.Report($"Recuperando · {set.Name} / {path}");
                    await Restore(set, path, output, token);
                }
            }
        }
        else
        {
            foreach (var set in sets)
            {
                token.ThrowIfCancellationRequested();
                progress.Report($"Leyendo NAS · {set.Name}");
                var nas = await Attempt(Source.Nas, set, () => Task.FromResult(ReadLocal(set.NasPath, token)));
                if (nas is null)
                {
                    Add(Source.Usb, FindingKind.Unavailable, set.Name, "", "NAS no disponible para comparar.");
                    Add(Source.Cloud, FindingKind.Unavailable, set.Name, "", "NAS no disponible para comparar.");
                    continue;
                }
                counts[Source.Nas].Checked++;
                counts[Source.Nas].Files += nas.Files.Count;
                counts[Source.Nas].Folders += nas.Folders.Count;

                progress.Report($"Verificando USB · {set.Name}");
                if (usbRoot is null)
                    Add(Source.Usb, FindingKind.Unavailable, set.Name, "", "Conecta o selecciona el disco USB.");
                else
                    await VerifyMirror(Source.Usb, set, nas, mode, token);

                progress.Report($"Verificando OneDrive · {set.Name}");
                await VerifyMirror(Source.Cloud, set, nas, mode, token);
            }
        }

        return new CheckResult(counts.Select(entry => new SourceResult(entry.Key, entry.Value.Files,
            entry.Value.Folders, entry.Value.Checked,
            findings.Count(finding => finding.Source == entry.Key && finding.Kind == FindingKind.Difference),
            findings.Count(finding => finding.Source == entry.Key && finding.Kind == FindingKind.Error),
            findings.Count(finding => finding.Source == entry.Key && finding.Kind == FindingKind.Unavailable)))
            .ToArray(), findings.ToArray(), output, sample);
    }

    private async Task VerifyMirror(Source source, BackupSet set, Inventory nas, CheckMode mode, CancellationToken token)
    {
        await Attempt(source, set, async () =>
        {
            var root = source == Source.Usb ? Paths.Local(usbRoot!, set.UsbPath) : set.CloudPath;
            var mirror = source == Source.Usb ? ReadLocal(root, token) : await ReadCloud(root, token);
            counts[source].Files += mirror.Files.Count;
            counts[source].Folders += mirror.Folders.Count;
            foreach (var path in nas.Folders.Except(mirror.Folders, StringComparer.OrdinalIgnoreCase))
                Add(source, FindingKind.Difference, set.Name, path, "Carpeta faltante");
            foreach (var path in mirror.Folders.Except(nas.Folders, StringComparer.OrdinalIgnoreCase))
                Add(source, FindingKind.Difference, set.Name, path, "Carpeta sobrante");

            if (source == Source.Cloud && mode == CheckMode.Complete)
                await CheckCloud(set, nas, mirror, token);
            else
            {
                foreach (var (path, length) in nas.Files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!mirror.Files.TryGetValue(path, out var mirrorLength))
                        Add(source, FindingKind.Difference, set.Name, path, "Archivo faltante");
                    else if (length != mirrorLength)
                        Add(source, FindingKind.Difference, set.Name, path, "Tamaño distinto");
                    else if (mode == CheckMode.Complete)
                    {
                        try
                        {
                            var original = await Hash(Paths.Local(set.NasPath, path), token);
                            var copy = await Hash(Paths.Local(root, path), token);
                            if (!original.SequenceEqual(copy))
                                Add(source, FindingKind.Difference, set.Name, path, "Contenido distinto");
                        }
                        catch (Exception error) when (IsCheckError(error))
                        {
                            Add(source, FindingKind.Error, set.Name, path, error.Message);
                        }
                    }
                }
                foreach (var path in mirror.Files.Keys.Except(nas.Files.Keys, StringComparer.OrdinalIgnoreCase))
                    Add(source, FindingKind.Difference, set.Name, path, "Archivo sobrante");
            }
            counts[source].Checked++;
            return true;
        });
    }

    private List<(BackupSet Set, string Path)> PickRandom(BackupSet[] sets, CancellationToken token)
    {
        (BackupSet Set, string Path)? choice = null;
        long seen = 0;
        foreach (var set in sets)
        {
            try
            {
                foreach (var entry in LocalEntries(set.NasPath, token))
                {
                    if (entry is FileInfo && Random.Shared.NextInt64(++seen) == 0)
                        choice = (set, RelativeTo(set.NasPath, entry));
                }
            }
            catch (Exception error) when (IsCheckError(error))
            {
                Add(Source.Nas, FindingKind.Error, set.Name, "", error.Message);
                return [];
            }
        }
        return choice is { } file ? [file] : [];
    }

    private async Task Restore(BackupSet set, string path, string output, CancellationToken token)
    {
        byte[]? original = null;
        if (usbRoot is null)
            Add(Source.Usb, FindingKind.Unavailable, set.Name, path, "Conecta o selecciona el disco USB.");
        foreach (var source in Enum.GetValues<Source>())
        {
            token.ThrowIfCancellationRequested();
            if (source == Source.Usb && usbRoot is null) continue;
            await Attempt(source, set, async () =>
            {
                var suffix = source.ToString().ToLowerInvariant();
                var relative = Path.ChangeExtension(path, null) + "_" + suffix + Path.GetExtension(path);
                var target = Paths.Local(Paths.Local(output, set.Name), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    if (source == Source.Cloud)
                    {
                        var result = await Rclone(["copyto", Paths.Cloud(set.CloudPath, path), temporary], token);
                        result.EnsureSuccess();
                    }
                    else
                    {
                        var root = source == Source.Nas ? set.NasPath : Paths.Local(usbRoot!, set.UsbPath);
                        EnsureNoLinks(root, path);
                        await using var input = File.OpenRead(Paths.Local(root, path));
                        await using var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
                        await input.CopyToAsync(destination, token);
                    }
                    var hash = await Hash(temporary, token);
                    File.Move(temporary, target);
                    if (source == Source.Nas) original = hash;
                    counts[source].Files++;
                    counts[source].Checked++;
                    Add(source, FindingKind.Recovered, set.Name, path, Path.GetFileName(target));
                    if (source != Source.Nas && original is null)
                        Add(source, FindingKind.Unavailable, set.Name, path, "Recuperado. Sin original para comparar.");
                    else if (original is not null && !hash.SequenceEqual(original))
                        Add(source, FindingKind.Difference, set.Name, path, "El archivo recuperado difiere del NAS.");
                    return true;
                }
                finally { File.Delete(temporary); }
            }, path);
        }
    }

    private static void EnsureNoLinks(string root, string path)
    {
        var current = root;
        foreach (var part in path.Split('/'))
        {
            current = Path.Combine(current, part);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"Enlace omitido: {path}");
        }
    }

    private static string RelativeTo(string root, FileSystemInfo entry) => Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');

    private static IEnumerable<FileSystemInfo> LocalEntries(string root, CancellationToken token)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"Enlace omitido: {entry.FullName}");
                yield return entry;
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
    }

    private static Inventory ReadLocal(string root, CancellationToken token)
    {
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in LocalEntries(root, token))
        {
            var path = RelativeTo(root, entry);
            if (entry is FileInfo file)
            {
                if (!files.TryAdd(path, file.Length)) throw new IOException($"Ruta duplicada: {path}");
            }
            else if (!folders.Add(path)) throw new IOException($"Ruta duplicada: {path}");
        }
        return new Inventory(files, folders);
    }

    private async Task<Inventory> ReadCloud(string root, CancellationToken token)
    {
        var result = await Rclone(["lsjson", root, "--recursive", "--no-modtime", "--no-mimetype"], token);
        result.EnsureSuccess();
        using var document = JsonDocument.Parse(result.Output);
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var path = entry.GetProperty("Path").GetString() ?? throw new InvalidDataException("Ruta de OneDrive vacía.");
            var added = entry.GetProperty("IsDir").GetBoolean() ? folders.Add(path)
                : files.TryAdd(path, entry.GetProperty("Size").GetInt64());
            if (!added) throw new IOException($"Ruta duplicada: {path}");
        }
        return new Inventory(files, folders);
    }

    private async Task CheckCloud(BackupSet set, Inventory nas, Inventory mirror, CancellationToken token)
    {
        var result = await Rclone(["cryptcheck", set.NasPath.TrimEnd('\\'), set.CloudPath, "--combined", "-", "--size-only=false", "--log-level", "NOTICE"], token);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (result.Error.Contains("hashes could not be checked", StringComparison.Ordinal))
            Add(Source.Cloud, FindingKind.Error, set.Name, "", "OneDrive no proporcionó un hash compatible para todos los archivos.");
        var reportedFailure = false;
        using var report = new StringReader(result.Output);
        while (report.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            if (line.Length < 3 || line[1] != ' ' || !"=+-*!".Contains(line[0])) continue;
            var path = line[2..];
            seen.Add(path);
            switch (line[0])
            {
                case '+': Add(Source.Cloud, FindingKind.Difference, set.Name, path, "Archivo faltante"); break;
                case '-': Add(Source.Cloud, FindingKind.Difference, set.Name, path, "Archivo sobrante"); break;
                case '*': Add(Source.Cloud, FindingKind.Difference, set.Name, path, "Contenido distinto"); break;
                case '!': Add(Source.Cloud, FindingKind.Error, set.Name, path, "Error de lectura o de hash"); break;
            }
            reportedFailure |= line[0] is '+' or '-' or '*' or '!';
        }

        if (result.ExitCode != 0 && (!reportedFailure || result.ExitCode != 1))
            result.EnsureSuccess();
        foreach (var path in nas.Files.Keys.Union(mirror.Files.Keys, StringComparer.Ordinal).Except(seen, StringComparer.Ordinal))
            Add(Source.Cloud, FindingKind.Error, set.Name, path, "rclone no verificó este archivo.");
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error)
    {
        public void EnsureSuccess()
        {
            if (ExitCode == 0) return;
            var detail = Error.Trim();
            throw new IOException(detail.Length == 0 ? $"rclone terminó con código {ExitCode}." : detail[^Math.Min(detail.Length, 1200)..]);
        }
    }

    private async Task<CommandResult> Rclone(string[] args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var config = Path.GetFullPath(settings.RcloneConfigPath, AppContext.BaseDirectory);
        var executable = Path.Combine(AppContext.BaseDirectory, "rclone.exe");
        if (!File.Exists(config)) throw new IOException("No se encuentra rclone.conf. Revisa rcloneConfigPath.");
        if (!File.Exists(executable)) throw new IOException("No se encuentra rclone.exe junto a BackupVerify.exe.");
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(config);
        start.ArgumentList.Add("--ask-password=false");
        using var process = Process.Start(start) ?? throw new IOException("No se pudo iniciar rclone.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        return new CommandResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<byte[]> Hash(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, token);
    }

    private async Task<T?> Attempt<T>(Source source, BackupSet set, Func<Task<T>> action, string path = "")
    {
        try { return await action(); }
        catch (Exception error) when (IsCheckError(error))
        {
            Add(source, FindingKind.Error, set.Name, path, error.Message);
            return default;
        }
    }

    private static bool IsCheckError(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or JsonException or System.ComponentModel.Win32Exception or ArgumentException;

    private void Add(Source source, FindingKind kind, string group, string path, string detail) =>
        findings.Add(new Finding(source, kind, group, path, detail));
}
