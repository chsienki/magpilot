using System.Runtime.InteropServices;
using System.Text.Json;

namespace Magpilot.Host;

internal sealed record ConsoleModeSnapshotData(
    bool InputRedirected,
    bool OutputRedirected,
    uint InputFileType,
    uint OutputFileType,
    uint? InputMode,
    uint? OutputMode,
    bool VirtualTerminalInput,
    bool VirtualTerminalOutput,
    uint InputCodePage,
    uint OutputCodePage);

internal static class ConsoleModeSnapshot
{
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(nint hFile);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    public static int Write(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("magpilot: console-mode snapshot is Windows-only.");
            return 2;
        }

        var input = GetStdHandle(STD_INPUT_HANDLE);
        var output = GetStdHandle(STD_OUTPUT_HANDLE);
        var hasInputMode = GetConsoleMode(input, out var inputMode);
        var hasOutputMode = GetConsoleMode(output, out var outputMode);
        var snapshot = new ConsoleModeSnapshotData(
            Console.IsInputRedirected,
            Console.IsOutputRedirected,
            GetFileType(input),
            GetFileType(output),
            hasInputMode ? inputMode : null,
            hasOutputMode ? outputMode : null,
            hasInputMode && (inputMode & ENABLE_VIRTUAL_TERMINAL_INPUT) != 0,
            hasOutputMode && (outputMode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0,
            GetConsoleCP(),
            GetConsoleOutputCP());

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    snapshot,
                    HostGeneralJsonContext.Default.ConsoleModeSnapshotData));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot: failed to write console-mode snapshot '{path}': {ex.Message}");
            return 2;
        }
    }
}
