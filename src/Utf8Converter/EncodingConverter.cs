using System.Text;

namespace Utf8Converter;

public enum FileAction { Copied, Converted, Unresolved }
public record ConversionResult(FileAction Action, string Reason);

public static class EncodingConverter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Utf8Bom = new UTF8Encoding(true, true);
    private record Plan(FileAction Action, string Reason, Encoding? SourceEncoding = null, int SourceOffset = 0);
    private static readonly Encoding Cp932 = CreateCp932();

    private static Encoding CreateCp932()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static ConversionResult Process(string source, string destination, bool candidate,
        CancellationToken cancellationToken = default, Action<string, long, long>? progress = null)
    {
        // Windowsでは、検証が終わるまで書き込み・削除のためのオープンを許さない。
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var originalLength = input.Length;
        void Report(string stage, long bytes) => progress?.Invoke(stage, bytes, originalLength);
        var originalWriteTime = File.GetLastWriteTimeUtc(source);
        var result = candidate ? Classify(input, cancellationToken, Report) : new Plan(FileAction.Copied, "対象外");
        var outputCreated = false;
        try
        {
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                outputCreated = true;
                input.Position = 0;
                if (result.Action == FileAction.Converted)
                {
                    Report("変換", 0);
                    input.Position = result.SourceOffset;
                    using (var reader = Reader(input, result.SourceEncoding!))
                    using (var writer = new StreamWriter(output, Utf8Bom, 16384, leaveOpen: true))
                    {
                        var chars = new char[16384];
                        int count;
                        while ((count = reader.Read(chars, 0, chars.Length)) != 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            writer.Write(chars, 0, count);
                            Report("変換", input.Position);
                        }
                    }
                    output.Flush();
                    if (!SameText(input, result.SourceEncoding!, output, Utf8, cancellationToken, bytes => Report("文字列照合", bytes),
                        leftOffset: result.SourceOffset, rightOffset: 3))
                        throw new IOException("変換前後の文字列が一致しません。");
                }
                else
                {
                    Report("コピー", 0);
                    var buffer = new byte[65536];
                    int count;
                    while ((count = input.Read(buffer)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, count);
                        Report("コピー", input.Position);
                    }
                    output.Flush();
                    if (!SameBytes(input, output, cancellationToken, bytes => Report("バイト照合", bytes)))
                        throw new IOException("コピー前後のバイト列が一致しません。");
                }
                if (input.Length != originalLength || File.GetLastWriteTimeUtc(source) != originalWriteTime)
                    throw new IOException("処理中に入力ファイルが変更されました。");
            }
            File.SetLastWriteTimeUtc(destination, originalWriteTime);
            return new(result.Action, result.Reason);
        }
        catch
        {
            // 未検証の出力を正常なファイルとして残さない。既存ファイルは削除しない。
            if (outputCreated) File.Delete(destination);
            throw;
        }
    }

    private static Plan Classify(FileStream input, CancellationToken token, Action<string, long> report)
    {
        Span<byte> header = stackalloc byte[4];
        input.Position = 0;
        var count = input.Read(header);
        if (count >= 3 && header[..3].SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }))
            return Valid(input, Utf8, token, bytes => report("UTF-8判定", bytes))
                ? new(FileAction.Copied, "UTF-8（BOMあり）")
                : new(FileAction.Unresolved, "UTF-8 BOMがありますが内容が不正です。");

        // UTF-32 LEのBOMはUTF-16 LEと同じ2バイトで始まるため、先に判定する。
        Encoding? unicode = null;
        var preambleLength = 0;
        if (count >= 4 && header.SequenceEqual(new byte[] { 0xff, 0xfe, 0, 0 }))
        { unicode = new UTF32Encoding(false, false, true); preambleLength = 4; }
        else if (count >= 4 && header.SequenceEqual(new byte[] { 0, 0, 0xfe, 0xff }))
        { unicode = new UTF32Encoding(true, false, true); preambleLength = 4; }
        else if (count >= 2 && header[..2].SequenceEqual(new byte[] { 0xff, 0xfe }))
        { unicode = new UnicodeEncoding(false, false, true); preambleLength = 2; }
        else if (count >= 2 && header[..2].SequenceEqual(new byte[] { 0xfe, 0xff }))
        { unicode = new UnicodeEncoding(true, false, true); preambleLength = 2; }
        if (unicode is not null)
            return Valid(input, unicode, token, bytes => report("Unicode判定", bytes), preambleLength)
                ? new(FileAction.Converted, "BOM付きUTF-16/UTF-32 → UTF-8（BOMあり）", unicode, preambleLength)
                : new(FileAction.Unresolved, "UTF-16/UTF-32 BOMがありますが内容が不正です。");

        // 拡張子には依存せず、全体を走査してバイナリの制御バイトを検出する。
        if (HasBinaryControls(input, token, bytes => report("バイナリ判定", bytes)))
            return new(FileAction.Copied, "バイナリと判定（制御バイトあり）");

        var utf8 = Valid(input, Utf8, token, bytes => report("UTF-8判定", bytes));
        var cp932 = Valid(input, Cp932, token, bytes => report("CP932判定", bytes));
        if (utf8 && cp932)
        {
            using var other = new FileStream(input.Name, FileMode.Open, FileAccess.Read, FileShare.Read);
            return SameText(input, Utf8, other, Cp932, token, bytes => report("判定文字列比較", bytes))
                ? new(FileAction.Converted, "UTF-8/CP932の文字列一致 → UTF-8（BOMあり）", Utf8)
                : new(FileAction.Unresolved, "UTF-8とCP932の両方で成立し、文字列が異なります。");
        }
        if (utf8) return new(FileAction.Converted, "UTF-8（BOMなし） → UTF-8（BOMあり）", Utf8);
        if (cp932) return new(FileAction.Converted, "CP932 → UTF-8（BOMあり）", Cp932);
        return new(FileAction.Unresolved, "UTF-8/CP932のどちらとしても不正です。");
    }

    private static bool HasBinaryControls(Stream input, CancellationToken token, Action<long> progress)
    {
        input.Position = 0;
        progress(0);
        var buffer = new byte[65536];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            progress(input.Position);
            foreach (var value in buffer.AsSpan(0, count))
                if (value is < 9 or 11 or (>= 14 and < 32) or 127) return true;
        }
        return false;
    }

    private static StreamReader Reader(Stream stream, Encoding encoding) =>
        new(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 16384, leaveOpen: true);

    private static bool Valid(Stream input, Encoding encoding, CancellationToken token, Action<long> progress, int offset = 0)
    {
        input.Position = offset;
        progress(0);
        using var reader = Reader(input, encoding);
        var buffer = new char[16384];
        try
        {
            while (reader.Read(buffer, 0, buffer.Length) != 0)
            {
                token.ThrowIfCancellationRequested();
                progress(input.Position);
            }
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    private static bool SameText(Stream left, Encoding leftEncoding, Stream right, Encoding rightEncoding,
        CancellationToken token, Action<long> progress, int leftOffset = 0, int rightOffset = 0)
    {
        left.Position = leftOffset;
        right.Position = rightOffset;
        progress(0);
        using var a = Reader(left, leftEncoding);
        using var b = Reader(right, rightEncoding);
        var x = new char[16384];
        var y = new char[16384];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var n = a.ReadBlock(x, 0, x.Length);
            var m = b.ReadBlock(y, 0, y.Length);
            if (n != m || !x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, m))) return false;
            if (n == 0) return true;
            progress(left.Position);
        }
    }

    private static bool SameBytes(Stream left, Stream right, CancellationToken token, Action<long> progress)
    {
        left.Position = right.Position = 0;
        progress(0);
        var x = new byte[65536];
        var y = new byte[65536];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var n = left.ReadAtLeast(x, x.Length, throwOnEndOfStream: false);
            var m = right.ReadAtLeast(y, y.Length, throwOnEndOfStream: false);
            if (n != m || !x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, m))) return false;
            if (n == 0) return true;
            progress(left.Position);
        }
    }
}
