using System.Collections.ObjectModel;

namespace FluentSerial.Core;

/// <summary>Append-only local audit trail: connections, errors, recording and data edits.</summary>
public sealed class AuditService
{
    private const int MaxEntries = 800;
    private DataStore? _store;

    public ObservableCollection<AuditEvent> Items => _store?.Data.Audit ?? new ObservableCollection<AuditEvent>();

    public event Action? Changed;

    public void Initialize(DataStore store)
    {
        _store = store;
        NormalizeOrder();
    }

    public void Add(AuditLevel level, string category, string message, string? detail = null)
    {
        if (_store is null) return;
        var entry = new AuditEvent
        {
            Level = level,
            Category = category,
            Message = message,
            Detail = detail
        };
        _store.Data.Audit.Insert(0, entry);
        while (_store.Data.Audit.Count > MaxEntries)
            _store.Data.Audit.RemoveAt(_store.Data.Audit.Count - 1);
        _store.Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_store is null) return;
        _store.Data.Audit.Clear();
        _store.Save();
        Changed?.Invoke();
    }

    public int Count => _store?.Data.Audit.Count ?? 0;

    private void NormalizeOrder()
    {
        if (_store is null) return;
        var ordered = _store.Data.Audit.OrderByDescending(e => e.At).ToList();
        if (ordered.Count != _store.Data.Audit.Count) return;
        var alreadyOrdered = _store.Data.Audit.Zip(ordered, (a, b) => ReferenceEquals(a, b)).All(same => same);
        if (alreadyOrdered) return;
        _store.Data.Audit.Clear();
        foreach (var entry in ordered) _store.Data.Audit.Add(entry);
    }
}
