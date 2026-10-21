using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Utf8Converter;

public record RunResult(string OutputDirectory, string LogPath, int Converted, int Copied, int Errors);

public static class ProjectConverter
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".cshtml", ".js", ".css", ".html" };
    private static readonly HashSet<string> CopyOnlyDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", "bin", "obj", "node_modules" };
    private record Entry(string Path, bool Candidate, long Length);

    public static RunResult Run(string folder, Action<string>? progress = null, CancellationToken token = default)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(source)) throw new ArgumentException("入力フォルダが存在しません。");
        if (Directory.GetParent(source) is null) throw new ArgumentException("ドライブのルートは指定できません。");
        // 親がリンクの場合も、別名経由で入力と出力が重なる可能性がある。
        for (var parent = new DirectoryInfo(source); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("入力パスとその親にリンク・ジャンクションは指定できません。");

        var output = source + "_utf8";
        var marker = output + ".incomplete";
        if (Path.Exists(output) || Path.Exists(marker))
            throw new ArgumentException("出力先または未完了マーカーが既に存在します。");
        var logPath = output + "." + Guid.NewGuid().ToString("N") + ".jsonl";
        using var log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false));
        log.AutoFlush = true;
        void Log(object item) => log.WriteLine(JsonSerializer.Serialize(item));
        using (var status = new FileStream(marker, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(status)) writer.WriteLine($"未完了。ログ: {logPath}");
        Directory.CreateDirectory(output);
        Log(new { type = "start", source, output, status = "incomplete", utc = DateTime.UtcNow });

        var entries = new List<Entry>();
        var directories = new List<string> { "" };
        var errors = 0;
        var converted = 0;
        var copied = 0;
        var watch = Stopwatch.StartNew();
        var lastProgress = -TimeSpan.FromSeconds(1);
        void Progress(string message, bool force = false)
        {
            if (force || watch.Elapsed - lastProgress >= TimeSpan.FromMilliseconds(250))
            {
                progress?.Invoke(message);
                lastProgress = watch.Elapsed;
            }
        }
        void Error(string path, string message)
        {
            errors++;
            Log(new { type = "error", path, message });
        }
        void Scan(string directory, bool copyOnly)
        {
            token.ThrowIfCancellationRequested();
            Progress($"集計中: {entries.Count:N0} ファイル / {Path.GetRelativePath(source, directory)}");
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            Error(Path.GetRelativePath(source, path), "リンク・ジャンクションは対象外です（コピーしません）。");
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            directories.Add(Path.GetRelativePath(source, path));
                            Scan(path, copyOnly || CopyOnlyDirectories.Contains(Path.GetFileName(path)));
                        }
                        else entries.Add(new(path, !copyOnly && Extensions.Contains(Path.GetExtension(path)), new FileInfo(path).Length));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { Error(Path.GetRelativePath(source, path), ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Error(Path.GetRelativePath(source, directory), ex.Message); }
        }

        try
        {
            Scan(source, CopyOnlyDirectories.Contains(Path.GetFileName(source)));
            foreach (var directory in directories)
            {
                token.ThrowIfCancellationRequested();
                try { Directory.CreateDirectory(Path.Combine(output, directory)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Error(directory, ex.Message); }
            }
            var totalBytes = entries.Sum(x => x.Length);
            long processedBytes = 0;
            var processed = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, entry.Path);
                Progress($"処理中: {processed:N0}/{entries.Count:N0} ({(entries.Count == 0 ? 100 : processed * 100.0 / entries.Count):F1}%) / {processedBytes:N0}/{totalBytes:N0} bytes / {relative}");
                try
                {
                    var result = EncodingConverter.Process(entry.Path, Path.Combine(output, relative), entry.Candidate, token);
                    if (result.Action == FileAction.Converted) converted++;
                    else copied++;
                    if (result.Action == FileAction.Unresolved) errors++;
                    Log(new { type = "file", path = relative, action = result.Action.ToString(), reason = result.Reason, verified = true });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { Error(relative, ex.Message); }
                processed++;
                processedBytes += entry.Length;
            }
            Log(new { type = "summary", status = errors == 0 ? "success" : "failed", converted, copied, errors, seconds = watch.Elapsed.TotalSeconds });
            if (errors == 0) File.Delete(marker);
            Progress($"終了: {processed:N0}/{entries.Count:N0} (100%) / {processedBytes:N0}/{totalBytes:N0} bytes / 変換 {converted:N0} / コピー {copied:N0} / 問題 {errors:N0} / {watch.Elapsed.TotalSeconds:F2} 秒", true);
            return new(output, logPath, converted, copied, errors);
        }
        catch (OperationCanceledException)
        {
            Log(new { type = "summary", status = "cancelled", converted, copied, errors });
            throw;
        }
    }
}
