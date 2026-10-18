using System.Diagnostics.Tracing;

namespace OmenFanPlus.Services
{
    /// <summary>
    /// Small rolling log file in %LOCALAPPDATA%\OmenFanPlus, so BIOS errors are visible somewhere.
    /// </summary>
    internal static class AppLog
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object WriteLock = new();
        private static OmenEventSourceListener? _listener;

        public static string Folder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenFanPlus");

        public static string FilePath { get; } = Path.Combine(Folder, "OmenFanPlus.log");

        /// <summary>Also copy the HP shim library's EventSource messages into the log.</summary>
        public static void Start()
        {
            _listener ??= new OmenEventSourceListener();
            // The build time tells builds of the same version apart.
            var built = "unknown build time";
            try { built = $"built {File.GetLastWriteTime(Application.ExecutablePath):yyyy-MM-dd HH:mm}"; } catch { }
            Info($"Started {Application.ProductName} {Application.ProductVersion} ({built}) from {Application.ExecutablePath}");
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message, Exception? ex = null) =>
            Write("ERROR", ex == null ? message : $"{message}: {ex}");

        internal static void Write(string level, string message)
        {
            try
            {
                lock (WriteLock)
                {
                    Directory.CreateDirectory(Folder);
                    var file = new FileInfo(FilePath);
                    if (file.Exists && file.Length > MaxBytes)
                        File.Move(FilePath, FilePath + ".old", overwrite: true);
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }

        private sealed class OmenEventSourceListener : EventListener
        {
            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (eventSource.Name == "OMENEventSource")
                    EnableEvents(eventSource, EventLevel.Informational);
            }

            protected override void OnEventWritten(EventWrittenEventArgs e)
            {
                var text = e.Message ?? e.EventName ?? "";
                if (e.Payload is { Count: > 0 })
                {
                    try { text = string.Format(text, e.Payload.ToArray()); }
                    catch (FormatException) { text = string.Join(" | ", e.Payload); }
                }

                var level = e.Level switch
                {
                    EventLevel.Critical or EventLevel.Error => "ERROR",
                    EventLevel.Warning => "WARN",
                    _ => "INFO",
                };
                Write(level, "[HP] " + text);
            }
        }
    }
}
