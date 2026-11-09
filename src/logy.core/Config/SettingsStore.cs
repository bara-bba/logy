namespace Logy.Core.Config;

/// <summary>
/// Settings persistence as read-modify-write: every save reloads the file, applies only the
/// caller's change and writes it back, so one logy window does not overwrite fields another
/// window changed. Two saves in the same instant can still race.
/// </summary>
public sealed class SettingsStore
{
    private readonly JsonStore _store;

    public SettingsStore(JsonStore store)
    {
        _store = store;
    }

    public LogySettings Load() => _store.Load(LogySettings.FileName, () => new LogySettings());

    public LogySettings Update(Action<LogySettings> change)
    {
        var fresh = Load();
        change(fresh);
        _store.Save(LogySettings.FileName, fresh);
        return fresh;
    }
}
