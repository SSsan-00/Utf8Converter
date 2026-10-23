using System.Text;
using Utf8Converter;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.WriteLine("使い方: Utf8Converter <フォルダパス>\n拡張子に関係なく再帰的にテキストをBOM付きUTF-8に変換します。\n元ファイルを保持し、隣の <名前>_utf8 に検証済みの結果を生成します。");
    return 0;
}
if (args.Length != 1)
{
    Console.Error.WriteLine("使い方: Utf8Converter <フォルダパス>");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var result = ProjectConverter.Run(args[0], Console.WriteLine, cancellation.Token);
    Console.WriteLine($"出力先: {result.OutputDirectory}\nログ: {result.LogPath}");
    if (result.Errors != 0) Console.Error.WriteLine("未解決・失敗があります。出力は未完了です。ログを確認してください。");
    return result.Errors == 0 ? 0 : 1;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("中断しました。出力と未完了マーカーを残します。");
    return 1;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
{
    Console.Error.WriteLine($"処理できませんでした: {ex.Message}");
    return 1;
}
