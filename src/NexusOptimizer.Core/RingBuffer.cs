namespace NexusOptimizer.Core;

/// <summary>
/// Fixe Ringpuffer-Implementierung. Speicherbedarf bleibt konstant, unabhängig
/// von der Session-Länge - Grundlage für die 120-Frame-Mittelung und den Median.
/// </summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _next;
    private int _count;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count => _count;
    public bool IsFull => _count == _items.Length;

    public void Add(T item)
    {
        _items[_next] = item;
        _next = (_next + 1) % _items.Length;
        if (_count < _items.Length) _count++;
    }

    /// <summary>Index 0 = ältester Eintrag.</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            int start = _count == _items.Length ? _next : 0;
            return _items[(start + index) % _items.Length];
        }
    }

    public void CopyTo(Span<T> destination)
    {
        if (destination.Length < _count) throw new ArgumentException("Ziel zu klein", nameof(destination));
        for (int i = 0; i < _count; i++) destination[i] = this[i];
    }

    public void Clear()
    {
        Array.Clear(_items);
        _next = 0;
        _count = 0;
    }
}
