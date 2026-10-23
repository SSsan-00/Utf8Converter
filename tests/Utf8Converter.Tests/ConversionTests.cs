using System.Text;
using System.Text.Json;
using Utf8Converter;

namespace Utf8Converter.Tests;

[TestClass]
public sealed class ConversionTests
{
    private string root = null!;
    private string source = null!;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static Encoding Cp932
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
    }

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "Utf8ConverterTests", Guid.NewGuid().ToString("N"));
        source = Path.Combine(root, "日本語 project");
        Directory.CreateDirectory(source);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, recursive: true);

    private static byte[] WithBom(string text) => new byte[] { 0xef, 0xbb, 0xbf }.Concat(StrictUtf8.GetBytes(text)).ToArray();

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [TestMethod]
    [DataRow("Page.cshtml")]
    [DataRow("Page.cshtml.cs")]
    [DataRow("Code.CS")]
    [DataRow("app.js")]
    [DataRow("app.css")]
    [DataRow("index.html")]
    [DataRow("nested/deeper/settings.json")]
    [DataRow("data.xml")]
    [DataRow("app.config")]
    [DataRow("notes.txt")]
    [DataRow("README")]
    [DataRow("text.png")]
    [DataRow("unknown.extension")]
    public void Cp932ConversionPreservesEveryCharacterAndSource(string name)
    {
        var text = "日本語 髙 ①\r\n\t  空白 \n末尾\r";
        var bytes = Cp932.GetBytes(text);
        var path = Write(name, bytes);
        var timestamp = new DateTime(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, timestamp);

        var result = ProjectConverter.Run(source);

        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(1, result.Converted);
        var output = File.ReadAllBytes(Path.Combine(result.OutputDirectory, name));
        CollectionAssert.AreEqual(WithBom(text), output);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(Path.Combine(result.OutputDirectory, name)));
        Assert.IsFalse(File.Exists(result.OutputDirectory + ".incomplete"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("ascii\r\n\t ")]
    [DataRow("あ")]
    [DataRow("😀\r\n\t ")]
    public void ExistingUtf8ReceivesBomWithoutChangingContent(string text)
    {
        var bytes = StrictUtf8.GetBytes(text);
        Write("x.cs", bytes);
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(1, result.Converted);
        CollectionAssert.AreEqual(WithBom(text), File.ReadAllBytes(Path.Combine(result.OutputDirectory, "x.cs")));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(source, "x.cs")));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("日本語😀")]
    [DataRow("\ufeff先頭の文字としてのBOM")]
    public void Utf8BomIsPreserved(string text)
    {
        var bytes = WithBom(text);
        Write("x.cshtml", bytes);
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(0, result.Errors);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(result.OutputDirectory, "x.cshtml")));
    }

    [TestMethod]
    [DataRow(new byte[] { 0xc2, 0xa9 })]
    [DataRow(new byte[] { 0x81 })]
    [DataRow(new byte[] { 0xef, 0xbb, 0xbf, 0xff })]
    [DataRow(new byte[] { 0xff, 0xfe, 0x41 })]
    [DataRow(new byte[] { 0xfe, 0xff, 0 })]
    [DataRow(new byte[] { 0, 0, 0xfe, 0xff, 0 })]
    [DataRow(new byte[] { 0xff, 0xfe, 0, 0, 0, 0, 0x11, 0 })]
    public void UnresolvedInputIsCopiedButRunFails(byte[] bytes)
    {
        Write("x.cs", bytes);
        Write("next.cs", Cp932.GetBytes("日本語"));
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(1, result.Errors);
        Assert.AreEqual(1, result.Converted);
        Assert.IsTrue(File.Exists(result.OutputDirectory + ".incomplete"));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(result.OutputDirectory, "x.cs")));
        using var summary = JsonDocument.Parse(File.ReadLines(result.LogPath).Last());
        Assert.AreEqual("failed", summary.RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public void BinaryFilesAreCopiedExactlyRegardlessOfExtensionOrDirectory()
    {
        var bytes = new byte[] { 0, 0x81, 0xff, 0xfe, 13, 10 };
        var names = new[] { "image.png", "settings.json", ".git/config.cs", ".vs/a.cs", "bin/a.cs", "obj/a.js", "node_modules/a.css" };
        foreach (var name in names) Write(name, bytes);
        Directory.CreateDirectory(Path.Combine(source, "empty/nested"));
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(names.Length, result.Copied);
        foreach (var name in names)
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(result.OutputDirectory, name)));
        Assert.IsTrue(Directory.Exists(Path.Combine(result.OutputDirectory, "empty/nested")));
    }

    [TestMethod]
    public void TextInEveryDirectoryIsConvertedAndEmptyDirectoriesArePreserved()
    {
        var names = new[] { ".git/config", ".vs/a.txt", "bin/a.cs", "obj/a.js", "node_modules/a.css", "nested/deep/README" };
        foreach (var name in names) Write(name, Cp932.GetBytes("日本語\r\n"));
        Directory.CreateDirectory(Path.Combine(source, "empty/nested"));
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(names.Length, result.Converted);
        foreach (var name in names)
            CollectionAssert.AreEqual(WithBom("日本語\r\n"), File.ReadAllBytes(Path.Combine(result.OutputDirectory, name)));
        Assert.IsTrue(Directory.Exists(Path.Combine(result.OutputDirectory, "empty/nested")));
    }

    [TestMethod]
    public void RootWithPreviouslyExcludedNameIsAlsoConverted()
    {
        source = Path.Combine(root, "bin");
        Directory.CreateDirectory(source);
        Write("README", StrictUtf8.GetBytes("ascii"));
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(1, result.Converted);
        CollectionAssert.AreEqual(WithBom("ascii"), File.ReadAllBytes(Path.Combine(result.OutputDirectory, "README")));
    }

    [TestMethod]
    [DataRow("utf16le")]
    [DataRow("utf16be")]
    [DataRow("utf32le")]
    [DataRow("utf32be")]
    public void UnicodeBomInputIsConvertedWithoutLosingTextOrAddingDuplicateBom(string kind)
    {
        Encoding encoding = kind switch
        {
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            "utf32le" => new UTF32Encoding(false, true, true),
            _ => new UTF32Encoding(true, true, true)
        };
        foreach (var text in new[] { "", "日本語😀\r\n\t \n", "\ufeff先頭の文字としてのBOM" })
        {
            var name = Guid.NewGuid().ToString("N");
            Write(name, encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
        }
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(3, result.Converted);
        foreach (var path in Directory.EnumerateFiles(source))
        {
            var original = File.ReadAllBytes(path);
            var text = encoding.GetString(original, encoding.GetPreamble().Length, original.Length - encoding.GetPreamble().Length);
            CollectionAssert.AreEqual(WithBom(text), File.ReadAllBytes(Path.Combine(result.OutputDirectory, Path.GetFileName(path))));
        }
    }

    [TestMethod]
    public void BinaryControlAfterFirstBufferPreventsConversion()
    {
        var bytes = Enumerable.Repeat((byte)'a', 100000).Append((byte)0).ToArray();
        Write("looks-like-text.txt", bytes);
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(1, result.Copied);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(result.OutputDirectory, "looks-like-text.txt")));
    }

    [TestMethod]
    public void SecondRunDoesNotAddAnotherBom()
    {
        Write("README", StrictUtf8.GetBytes("ascii\r\n"));
        var first = ProjectConverter.Run(source);
        var second = ProjectConverter.Run(first.OutputDirectory);
        Assert.AreEqual(0, second.Errors);
        Assert.AreEqual(0, second.Converted);
        CollectionAssert.AreEqual(WithBom("ascii\r\n"), File.ReadAllBytes(Path.Combine(second.OutputDirectory, "README")));
    }

    [TestMethod]
    public void ExistingDestinationIsNeverOverwritten()
    {
        var path = Write("x.cs", Cp932.GetBytes("日本語"));
        var target = Path.Combine(root, "existing.cs");
        File.WriteAllText(target, "keep");
        Assert.ThrowsException<IOException>(() => EncodingConverter.Process(path, target, true));
        Assert.AreEqual("keep", File.ReadAllText(target));
        Directory.CreateDirectory(source + "_utf8");
        Assert.ThrowsException<ArgumentException>(() => ProjectConverter.Run(source));
    }

    [TestMethod]
    public void CancellationLeavesIncompleteMarkerAndSourceUnchanged()
    {
        var bytes = Cp932.GetBytes("日本語");
        var path = Write("x.cs", bytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => ProjectConverter.Run(source, token: cancellation.Token));
        Assert.IsTrue(File.Exists(source + "_utf8.incomplete"));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void CancelledFileDoesNotLeaveUnverifiedOutput()
    {
        var path = Write("x.png", new byte[100000]);
        var target = Path.Combine(root, "target.png");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => EncodingConverter.Process(path, target, false, cancellation.Token));
        Assert.IsFalse(File.Exists(target));
    }

    [TestMethod]
    public void LockedFileDoesNotStopRemainingFiles()
    {
        var path = Write("locked.cs", Cp932.GetBytes("日本語"));
        Write("next.cs", Cp932.GetBytes("日本語"));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(1, result.Errors);
        Assert.AreEqual(1, result.Converted);
        Assert.IsFalse(File.Exists(Path.Combine(result.OutputDirectory, "locked.cs")));
    }

    [TestMethod]
    [DataRow(8191)]
    [DataRow(16383)]
    [DataRow(65535)]
    public void MultibyteCharactersAcrossBuffersArePreserved(int prefixLength)
    {
        var text = new string('a', prefixLength) + "日本語\r\n" + new string('日', 40000);
        Write("x.cs", Cp932.GetBytes(text));
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(text, File.ReadAllText(Path.Combine(result.OutputDirectory, "x.cs"), StrictUtf8));
    }

    [TestMethod]
    public void InvalidByteAtEndOfLargeFileIsNotMissed()
    {
        var bytes = Enumerable.Repeat((byte)'a', 100000).Append((byte)0x81).ToArray();
        Write("x.cs", bytes);
        Assert.AreEqual(1, ProjectConverter.Run(source).Errors);
    }

    [TestMethod]
    public void JunctionIsReportedWithoutFollowingIt()
    {
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(source, "link");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
        }
        else Directory.CreateSymbolicLink(link, outside);
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(1, result.Errors);
        Assert.IsFalse(Directory.Exists(Path.Combine(result.OutputDirectory, "link")));
        Assert.ThrowsException<ArgumentException>(() => ProjectConverter.Run(link));
        Directory.Delete(link);
    }

    [TestMethod]
    [DataRow("cp932")]
    [DataRow("ascii")]
    [DataRow("binary")]
    public void ProgressReportsIntermediateReadsForEveryProcessingStage(string kind)
    {
        var text = new string('a', 100000) + "日本語";
        var bytes = kind == "cp932" ? Cp932.GetBytes(text) : new byte[200000];
        if (kind == "ascii") Array.Fill(bytes, (byte)'a');
        var path = Write("input", bytes);
        var target = Path.Combine(root, "output");
        var updates = new List<(string Stage, long Bytes, long Total)>();

        var result = EncodingConverter.Process(path, target, kind != "binary",
            progress: (stage, read, total) => updates.Add((stage, read, total)));

        var expectedStages = kind switch
        {
            "cp932" => new[] { "UTF-8判定", "CP932判定", "変換", "文字列照合" },
            "ascii" => new[] { "バイナリ判定", "UTF-8判定", "CP932判定", "判定文字列比較", "変換", "文字列照合" },
            _ => new[] { "コピー", "バイト照合" }
        };
        foreach (var stage in expectedStages)
        {
            var reads = updates.Where(x => x.Stage == stage).ToArray();
            Assert.IsTrue(reads.Length > 1, stage);
            Assert.AreEqual(0L, reads[0].Bytes, stage);
            Assert.IsTrue(reads.Any(x => x.Bytes > 0 && x.Bytes < bytes.Length), stage);
            Assert.IsTrue(reads.All(x => x.Total == bytes.Length && x.Bytes <= x.Total), stage);
            CollectionAssert.AreEqual(reads.Select(x => x.Bytes).Order().ToArray(), reads.Select(x => x.Bytes).ToArray(), stage);
        }
        Assert.AreEqual(kind != "binary" ? FileAction.Converted : FileAction.Copied, result.Action);
        CollectionAssert.AreEqual(kind == "binary" ? bytes : WithBom(kind == "cp932" ? text : new string('a', bytes.Length)), File.ReadAllBytes(target));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow("バイナリ判定")]
    [DataRow("CP932判定")]
    [DataRow("変換")]
    [DataRow("文字列照合")]
    [DataRow("コピー")]
    [DataRow("バイト照合")]
    public void CancellationFromIntermediateProgressRemovesPartialOutput(string stageToCancel)
    {
        var bytes = Cp932.GetBytes(new string('a', 100000) + "日本語");
        var path = Write("input", bytes);
        var target = Path.Combine(root, "output");
        using var cancellation = new CancellationTokenSource();
        var candidate = stageToCancel is not ("コピー" or "バイト照合");

        Assert.ThrowsException<OperationCanceledException>(() => EncodingConverter.Process(path, target, candidate,
            cancellation.Token, (stage, read, total) =>
            {
                if (stage == stageToCancel && read > 0 && read < total) cancellation.Cancel();
            }));

        Assert.IsFalse(File.Exists(target));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void ProjectDisplaysIntermediateFileProgressAndKeepsIncompleteMarkerOnCancellation()
    {
        Write("large.cs", Cp932.GetBytes(new string('a', 100000) + "日本語"));
        using var cancellation = new CancellationTokenSource();
        var messages = new List<string>();
        Assert.ThrowsException<OperationCanceledException>(() => ProjectConverter.Run(source, message =>
        {
            messages.Add(message);
            if (message.Contains("読込 ") && !message.Contains("読込 0/")) cancellation.Cancel();
            // 遅い表示先を再現し、実機の処理速度に依存せず表示間隔を経過させる。
            if (message.StartsWith("処理中:") && !cancellation.IsCancellationRequested) Thread.Sleep(300);
        }, cancellation.Token));

        Assert.IsTrue(messages.Any(x => x.Contains("large.cs") && x.Contains("バイナリ判定: 読込 ") && !x.Contains("読込 0/")));
        Assert.IsTrue(File.Exists(source + "_utf8.incomplete"));
    }

    [TestMethod]
    public void SeveralThousandFilesAreProcessedAndLogged()
    {
        for (var i = 0; i < 3000; i++) Write($"folder{i % 10}/file{i}.cs", Cp932.GetBytes("日本語\r\n"));
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(3000, result.Converted);
        Assert.AreEqual(3000, Directory.EnumerateFiles(result.OutputDirectory, "*", SearchOption.AllDirectories).Count());
        Assert.AreEqual(3002, File.ReadLines(result.LogPath).Count());
    }
}
