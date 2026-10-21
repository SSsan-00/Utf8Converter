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
        CollectionAssert.AreEqual(StrictUtf8.GetBytes(text), output);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(Path.Combine(result.OutputDirectory, name)));
        Assert.IsFalse(File.Exists(result.OutputDirectory + ".incomplete"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("ascii\r\n\t ")]
    [DataRow("あ")]
    public void ExistingUtf8IsCopiedExactly(string text)
    {
        var bytes = StrictUtf8.GetBytes(text);
        Write("x.cs", bytes);
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(0, result.Converted);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(result.OutputDirectory, "x.cs")));
    }

    [TestMethod]
    public void Utf8BomIsPreserved()
    {
        var bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(StrictUtf8.GetBytes("日本語😀")).ToArray();
        Write("x.cshtml", bytes);
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(0, result.Errors);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(result.OutputDirectory, "x.cshtml")));
    }

    [TestMethod]
    [DataRow(new byte[] { 0xc2, 0xa9 })]
    [DataRow(new byte[] { 0x81 })]
    [DataRow(new byte[] { 0xef, 0xbb, 0xbf, 0xff })]
    [DataRow(new byte[] { 0xff, 0xfe, 0x41, 0 })]
    [DataRow(new byte[] { 0xfe, 0xff, 0, 0x41 })]
    [DataRow(new byte[] { 0, 0, 0xfe, 0xff })]
    [DataRow(new byte[] { 0xff, 0xfe, 0, 0 })]
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
    public void OtherFilesAndSpecialDirectoriesAreCopiedExactly()
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
        var result = ProjectConverter.Run(source);
        Assert.AreEqual(1, result.Errors);
        Assert.IsFalse(Directory.Exists(Path.Combine(result.OutputDirectory, "link")));
        Assert.ThrowsException<ArgumentException>(() => ProjectConverter.Run(link));
        Directory.Delete(link);
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
