using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Threading;

namespace AudiobookPlayer;

/// JSON state in %APPDATA%\Audiobook Player. Saves are debounced (1 s).
public sealed class Store
{
    public AppState State { get; private set; } = new();
    public string BaseDir { get; }
    public string CoversDir { get; }

    private readonly string file;
    private readonly Dispatcher ui;
    private readonly Timer debounce;
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Store(Dispatcher dispatcher)
    {
        ui = dispatcher;
        BaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Audiobook Player");
        CoversDir = Path.Combine(BaseDir, "Covers");
        Directory.CreateDirectory(CoversDir);
        file = Path.Combine(BaseDir, "state.json");
        try
        {
            if (File.Exists(file))
                State = JsonSerializer.Deserialize<AppState>(File.ReadAllText(file), Json) ?? new AppState();
        }
        catch { State = new AppState(); }
        debounce = new Timer(_ => ui.InvokeAsync(Flush), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Save() => debounce.Change(1000, Timeout.Infinite);

    public void Flush()
    {
        try
        {
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(State, Json));
            File.Move(tmp, file, overwrite: true);
        }
        catch { /* best effort */ }
    }
}
