using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Folio.Services;

/// <summary>
/// Lokale Kommunikation über eine Named Pipe (kein Netzwerk, kein Port; nur derselbe Benutzer).
/// Nachrichten sind einzelne Zeilen:
///   O \t datei   – zweiter Folio-Start: Datei als Tab öffnen (leer = nur Fenster nach vorn)
///   U \t exePfad – eine neuere Folio.exe wurde gestartet: Update anbieten
/// </summary>
public sealed class IpcServer : IDisposable
{
    readonly string _name;
    readonly CancellationTokenSource _cts = new();

    public event Action<string>? OpenRequested;
    public event Action<string>? UpdateOffered;

    public IpcServer(string name) => _name = name;

    public void Start() => Task.Run(Loop);

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(_name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex)
            {
                AppPaths.Log("Pipe-Server: " + ex.Message);
                await Task.Delay(1000);
                continue;
            }
            try
            {
                await server.WaitForConnectionAsync(_cts.Token);
            }
            catch
            {
                server.Dispose();
                if (_cts.IsCancellationRequested) return;
                continue;
            }
            _ = Task.Run(() => Handle(server));
        }
    }

    async Task Handle(NamedPipeServerStream s)
    {
        try
        {
            using (s)
            using (var reader = new StreamReader(s, new UTF8Encoding(false)))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (line.StartsWith("O\t")) OpenRequested?.Invoke(line[2..]);
                    else if (line.StartsWith("U\t")) UpdateOffered?.Invoke(line[2..]);
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Pipe-Verbindung: " + ex.Message);
        }
    }

    public static bool Send(string pipeName, string message, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var bytes = new UTF8Encoding(false).GetBytes(message.Replace("\r", " ").Replace("\n", " ") + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _cts.Cancel();
}
