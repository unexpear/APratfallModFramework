using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace PratfallModFramework;

// Per-mod typed settings store with JSON persistence + change events.
// API surface intentionally mirrors BepInEx ConfigFile so authors moving
// between framework ecosystems recognize the shape.
//
// Usage:
//   var cfg = ModConfig.For("MyMod");
//   var maxFlares = cfg.Bind("Combat", "MaxFlares", 3,
//       new ConfigDescription { Tooltip = "How many flares to allow",
//           Constraint = new AcceptableValueRange<int>(1, 100) });
//   maxFlares.OnChange += newValue => GD.Print($"max flares is now {newValue}");
//   maxFlares.Value = 50;   // persists to file, fires OnChange, validates against constraint
//
// File: <userData>/modframework-config/<sanitized_modId>.json
// Format: nested JSON keyed by Section then Key. Enums serialize as string names.
// Type support in v1: bool, int, long, float, double, string, enums.
public static class ModConfig
{
    private static readonly object _filesLock = new();
    private static readonly Dictionary<string, ModConfigFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public static ModConfigFile For(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("modId is required", nameof(modId));

        lock (_filesLock)
        {
            if (!_files.TryGetValue(modId, out var file))
            {
                file = new ModConfigFile(modId);
                _files[modId] = file;
            }
            return file;
        }
    }

    // Discovery for the future in-game editor UI.
    public static IReadOnlyList<IConfigEntry> GetAllEntries(string modId)
    {
        lock (_filesLock)
        {
            return _files.TryGetValue(modId, out var file) ? file.GetAllEntries() : Array.Empty<IConfigEntry>();
        }
    }

    // Iterate every ConfigEntry across every mod that has Synced=true on its
    // ConfigDescription. Used by ModManager to build the on-join CSync snapshot
    // when this peer is the host.
    public static IEnumerable<(string ModId, IConfigEntry Entry)> EnumerateSyncedEntries()
    {
        lock (_filesLock)
        {
            foreach (var (modId, file) in _files)
            {
                foreach (var entry in file.GetAllEntries())
                {
                    if (entry.Description?.Synced == true)
                        yield return (modId, entry);
                }
            }
        }
    }

    // Internal event fired from ConfigEntry<T>.SetInternal when a Synced entry's
    // value changed via a normal setter (NOT from a host-sync apply). ModManager
    // subscribes and broadcasts the delta if this peer is the host. Decoupled
    // here so ModConfig doesn't depend on the network layer.
    internal static event Action<string, string, string, object?>? OnSyncedValueChanged;

    internal static void RaiseSyncedValueChanged(string modId, string section, string key, object? newValue)
    {
        try { OnSyncedValueChanged?.Invoke(modId, section, key, newValue); }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] ModConfig OnSyncedValueChanged handler threw for {modId}/[{section}].{key}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Filesystem-safe per-mod file basename. Same rule as elsewhere.
    internal static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_');
        return sb.ToString();
    }

    internal static string? ResolveConfigFolder()
    {
        try
        {
            var platform = global::Game.Platform;
            if (platform == null) return null;
            var raw = platform.GetUserDataPath();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var globalized = ProjectSettings.GlobalizePath(raw);
            if (string.IsNullOrWhiteSpace(globalized)) globalized = raw;
            var dir = Path.Combine(globalized, "modframework-config");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return null;
        }
    }
}

// Per-mod config file. Holds the loaded JSON values and the bound ConfigEntry
// instances. Lazily reads the file on first Bind so we can defer until
// Game.Platform is up.
public sealed class ModConfigFile
{
    private const int CurrentSchemaVersion = 1;

    // Cloned from JsonSerializerOptions.Default so the TypeInfoResolver is
    // populated — required when Godot's runtime has JsonSerializerIsReflection-
    // EnabledByDefault=false (otherwise a fresh JsonSerializerOptions throws on
    // first use: "must specify a TypeInfoResolver before being marked read-only").
    private static readonly JsonSerializerOptions WriteOptions =
        new(JsonSerializerOptions.Default) { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _modId;
    private readonly List<IConfigEntry> _entries = new();
    private JsonObject _loadedJson = new();
    private bool _loaded;
    private string? _filePath;

    internal ModConfigFile(string modId)
    {
        _modId = modId;
    }

    // Cached per (section, key) — calling Bind twice with the same key returns the
    // same entry. The second call's defaultValue and description are ignored (the
    // first registration wins). Type-mismatch on the second call throws.
    public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription? description = null)
    {
        if (string.IsNullOrWhiteSpace(section)) throw new ArgumentException("section is required", nameof(section));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required", nameof(key));

        lock (_lock)
        {
            EnsureLoaded();

            // Re-Bind returns the cached entry if the (section, key) was already bound.
            foreach (var existing in _entries)
            {
                if (string.Equals(existing.Section, section, StringComparison.Ordinal) &&
                    string.Equals(existing.Key, key, StringComparison.Ordinal))
                {
                    if (existing is ConfigEntry<T> typed) return typed;
                    throw new InvalidOperationException(
                        $"Config entry [{section}].{key} already bound as {existing.ValueType.Name}, " +
                        $"refusing to re-bind as {typeof(T).Name}");
                }
            }

            var entry = new ConfigEntry<T>(this, section, key, defaultValue, description);
            // Seed from file if present, else write the default on first save.
            if (TryReadFromFile<T>(section, key, out var fileValue))
                entry.SetInternal(fileValue, persist: false, fireOnChange: false);
            else
                entry.SetInternal(defaultValue, persist: true, fireOnChange: false);

            _entries.Add(entry);
            return entry;
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            EnsureLoaded();
            WriteFile();
        }
    }

    // Re-read file from disk. Values that have changed in-memory are overwritten
    // by the file's values, and OnChange fires for any that actually changed.
    public void Reload()
    {
        lock (_lock)
        {
            _loaded = false;
            EnsureLoaded();
            // Re-apply file values to each registered entry (internal-interface dispatch).
            foreach (var entry in _entries)
                if (entry is IConfigEntryInternal inner) inner.ReapplyFromFile();
        }
    }

    public IReadOnlyList<IConfigEntry> GetAllEntries()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }

    internal string ModId => _modId;

    // Used by ConfigEntry.Value setter to persist immediately.
    internal void PersistValueAndSave<T>(string section, string key, T value)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var sectionObj = _loadedJson[section] as JsonObject;
            if (sectionObj == null)
            {
                sectionObj = new JsonObject();
                _loadedJson[section] = sectionObj;
            }
            sectionObj[key] = SerializeValue(value);
            WriteFile();
        }
    }

    // Used by Reload to re-seed entries from refreshed JSON.
    internal bool TryReadFromFile<T>(string section, string key, out T value)
    {
        value = default!;
        if (_loadedJson[section] is not JsonObject sectionObj) return false;
        if (sectionObj[key] is not JsonNode node) return false;
        try
        {
            value = DeserializeValue<T>(node);
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] ModConfig {_modId}: type mismatch for [{section}].{key} " +
                        $"({typeof(T).Name}): {ex.GetType().Name}: {ex.Message}. Using default.");
            return false;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        var folder = ModConfig.ResolveConfigFolder();
        if (folder == null) return; // platform not up yet — defer (next access retries)
        _filePath = Path.Combine(folder, $"{ModConfig.Sanitize(_modId)}.json");

        if (!File.Exists(_filePath))
        {
            _loadedJson = new JsonObject { ["_schema_version"] = CurrentSchemaVersion };
            return;
        }

        try
        {
            var text = File.ReadAllText(_filePath, Encoding.UTF8);
            var parsed = JsonNode.Parse(text) as JsonObject;
            _loadedJson = parsed ?? new JsonObject { ["_schema_version"] = CurrentSchemaVersion };
        }
        catch (Exception ex)
        {
            // Corrupt file — back it up so we don't lose the user's data, fall back to empty.
            GD.PrintErr($"[ModFramework] ModConfig {_modId}: corrupt config file at {_filePath} " +
                        $"({ex.GetType().Name}: {ex.Message}). Backing up + falling back to defaults.");
            try { File.Copy(_filePath, _filePath + ".bad", overwrite: true); } catch { }
            _loadedJson = new JsonObject { ["_schema_version"] = CurrentSchemaVersion };
        }
    }

    private void WriteFile()
    {
        if (_filePath == null)
        {
            // Try one more time — platform may have come up since first Bind.
            var folder = ModConfig.ResolveConfigFolder();
            if (folder == null) return;
            _filePath = Path.Combine(folder, $"{ModConfig.Sanitize(_modId)}.json");
        }

        // Ensure the _schema_version field is present (always written, never user-editable).
        _loadedJson["_schema_version"] = CurrentSchemaVersion;

        try
        {
            var json = _loadedJson.ToJsonString(WriteOptions);
            File.WriteAllText(_filePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] ModConfig {_modId}: failed to write {_filePath}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static JsonNode SerializeValue<T>(T value)
    {
        if (value == null) return JsonValue.Create<string?>(null)!;
        var type = typeof(T);
        if (type.IsEnum)
            return JsonValue.Create(value.ToString())!;
        return JsonValue.Create(value)!;
    }

    private static T DeserializeValue<T>(JsonNode node)
    {
        var type = typeof(T);
        if (type.IsEnum)
        {
            var str = node.GetValue<string>();
            return (T)Enum.Parse(type, str);
        }
        return node.GetValue<T>();
    }
}

// Non-generic interface so UI / discovery code can iterate entries without
// knowing the generic type upfront. Boxing is acceptable here — UI is not hot.
public interface IConfigEntry
{
    string Section { get; }
    string Key { get; }
    Type ValueType { get; }
    object? BoxedValue { get; set; }
    object? BoxedDefaultValue { get; }
    ConfigDescription? Description { get; }
    void ResetToDefault();
}

// Internal-only — used by ModConfigFile.Reload and CSync receive to dispatch
// over entries without knowing T. Kept off IConfigEntry so neither leaks as
// public API.
internal interface IConfigEntryInternal
{
    void ReapplyFromFile();
    // Set the value from a CSync host broadcast. Bypasses local file save
    // (host is source of truth) and bypasses the OnSyncedValueChanged hook
    // (so the peer doesn't echo the change back to the host). Still fires
    // ConfigEntry<T>.OnChange so mod code reacts.
    void SetFromHost(object? boxedValue);
}

public sealed class ConfigEntry<T> : IConfigEntry, IConfigEntryInternal
{
    private readonly ModConfigFile _file;
    private T _value;

    public string Section { get; }
    public string Key { get; }
    public T DefaultValue { get; }
    public ConfigDescription? Description { get; }
    public Type ValueType => typeof(T);

    public T Value
    {
        get => _value;
        set => SetInternal(value, persist: true, fireOnChange: true);
    }

    public event Action<T>? OnChange;

    internal ConfigEntry(ModConfigFile file, string section, string key, T defaultValue, ConfigDescription? description)
    {
        _file = file;
        Section = section;
        Key = key;
        DefaultValue = defaultValue;
        Description = description;
        _value = defaultValue;
    }

    public void ResetToDefault() => Value = DefaultValue;

    // Internal setter used during initial load (persist: false) and from Reload.
    // fromSync=true bypasses the OnSyncedValueChanged broadcast hook (used when
    // the value comes from a host-sync apply — we don't want to echo it back).
    internal void SetInternal(T newValue, bool persist, bool fireOnChange, bool fromSync = false)
    {
        if (Description?.Constraint is IConstraint c && !c.IsValid(newValue!))
            throw new ArgumentOutOfRangeException(nameof(newValue),
                $"Value {newValue} fails constraint {c.GetType().Name} on [{Section}].{Key}");

        var old = _value;
        _value = newValue;
        if (persist)
            _file.PersistValueAndSave(Section, Key, newValue);
        var actuallyChanged = !EqualityComparer<T>.Default.Equals(old, newValue);
        if (fireOnChange && actuallyChanged)
        {
            try { OnChange?.Invoke(newValue); }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModFramework] ModConfig OnChange for {_file.ModId}/[{Section}].{Key} threw: " +
                            $"{ex.GetType().Name}: {ex.Message}");
            }
        }
        // CSync broadcast hook — only fires for Synced entries when the change
        // came from a normal setter (not from receiving a host sync). ModManager
        // subscribes and broadcasts to peers if this peer is the host.
        if (actuallyChanged && !fromSync && Description?.Synced == true)
            ModConfig.RaiseSyncedValueChanged(_file.ModId, Section, Key, newValue);
    }

    // Called from ModConfigFile.Reload via IConfigEntryInternal — refresh value
    // from file, fire OnChange if changed. Explicit impl keeps it off the public
    // IConfigEntry contract.
    void IConfigEntryInternal.ReapplyFromFile()
    {
        if (_file.TryReadFromFile<T>(Section, Key, out var fileValue))
        {
            SetInternal(fileValue, persist: false, fireOnChange: true);
        }
        // If file doesn't have the key, keep the current in-memory value.
    }

    // Called from ModManager when a CSync broadcast arrives. The value comes
    // from the host's authoritative state; we apply it locally without
    // persisting (host owns the value for the session) and without re-broadcasting
    // (would create a cycle). The mod's OnChange handler still fires so reactive
    // code reacts.
    void IConfigEntryInternal.SetFromHost(object? boxedValue)
    {
        if (boxedValue is null && default(T) != null) return; // can't unbox null to value-type
        T typed;
        try { typed = (T)boxedValue!; }
        catch (InvalidCastException)
        {
            GD.PrintErr($"[ModFramework] ModConfig SetFromHost: value type mismatch for {_file.ModId}/[{Section}].{Key} — expected {typeof(T).Name}, got {boxedValue?.GetType().Name}");
            return;
        }
        try
        {
            SetInternal(typed, persist: false, fireOnChange: true, fromSync: true);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // Host pushed a value that fails our constraint. Log + leave the
            // local value unchanged; on next host re-send (rejoin) we'll
            // converge again if the host fixed the value.
            GD.PrintErr($"[ModFramework] ModConfig SetFromHost: constraint rejection for {_file.ModId}/[{Section}].{Key}: {ex.Message}");
        }
    }

    // IConfigEntry implementation for UI iteration.
    object? IConfigEntry.BoxedValue
    {
        get => _value;
        set => Value = (T)value!;
    }
    object? IConfigEntry.BoxedDefaultValue => DefaultValue;
}

public sealed class ConfigDescription
{
    public string? Tooltip { get; init; }
    public IConstraint? Constraint { get; init; }
    // Reserved for Phase 3 (CSync). Today it's just metadata the UI/sync code can read.
    public bool Synced { get; init; }
    public string[]? Tags { get; init; }
}

public interface IConstraint
{
    // Untyped to support polymorphic constraints. Implementations cast appropriately
    // and return false for type-mismatched values (defensive — shouldn't happen given
    // ConfigEntry<T>'s generic constraints, but better than throwing in the validator).
    bool IsValid(object value);
    // For UI to render bounds (e.g., slider min/max).
    object? Lower { get; }
    object? Upper { get; }
    // For UI to render dropdown options.
    IReadOnlyList<object>? AllowedValues { get; }
}

public sealed class AcceptableValueRange<T> : IConstraint where T : IComparable<T>
{
    public T Min { get; }
    public T Max { get; }

    public AcceptableValueRange(T min, T max)
    {
        if (min.CompareTo(max) > 0) throw new ArgumentException("min must be <= max");
        Min = min;
        Max = max;
    }

    public bool IsValid(object value)
    {
        if (value is not T typed) return false;
        return typed.CompareTo(Min) >= 0 && typed.CompareTo(Max) <= 0;
    }
    public object? Lower => Min;
    public object? Upper => Max;
    public IReadOnlyList<object>? AllowedValues => null;
}

public sealed class AcceptableValueList<T> : IConstraint
{
    public T[] Values { get; }

    public AcceptableValueList(params T[] values)
    {
        if (values == null || values.Length == 0)
            throw new ArgumentException("must provide at least one allowed value", nameof(values));
        Values = values;
    }

    public bool IsValid(object value)
    {
        if (value is not T typed) return false;
        return Array.IndexOf(Values, typed) >= 0;
    }
    public object? Lower => null;
    public object? Upper => null;
    public IReadOnlyList<object>? AllowedValues => Values.Cast<object>().ToArray();
}
