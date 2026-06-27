using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Base;

/// <summary>
/// An immutable, type-keyed set of out-of-band metadata: at most one value per CLR type, written and
/// read by that type. A general-purpose carrier for describing otherwise-opaque data (content type,
/// origin, classification, cryptographic context) without binding the description into the data itself.
/// </summary>
/// <remarks>
/// <para>
/// Using <see cref="Type"/> as the key gives compile-time safety and avoids magic strings: a value is
/// stored with <see cref="With{T}(T)"/> and retrieved with <see cref="Get{T}"/>/<see cref="TryGet{T}"/>,
/// both inferring the key from the generic argument. Because the key is always the value's own static
/// type, a key/value mismatch is unrepresentable — unlike a raw <c>(Type, object)</c> map.
/// </para>
/// <para>
/// The backing store is a private <see cref="FrozenDictionary{TKey, TValue}"/>; the type is immutable,
/// and every mutator returns a new <see cref="Tag"/>. Equality is <strong>by content</strong> and
/// order-independent — two tags with the same set of type/value entries are equal and hash equal,
/// regardless of how they were built. The hash is an in-memory bucketing hash, randomized per process, so
/// it must not be persisted or used as a durable identifier; equality itself is deterministic.
/// <see cref="Entries"/> exposes a read-only projection for emitting the metadata elsewhere (for example as
/// OpenTelemetry or CBOM attributes keyed by the type name).
/// </para>
/// <para>
/// One value per type is the model. To carry several values of the same kind (for example multiple
/// purposes), store a single composite value (a record or collection) under one key — making the
/// multiplicity explicit rather than a silent overwrite.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerView,nq}")]
public sealed class Tag: IEquatable<Tag>
{
    private FrozenDictionary<Type, object> Data { get; }


    /// <summary>An empty tag with no metadata.</summary>
    public static Tag Empty { get; } = new(FrozenDictionary<Type, object>.Empty);


    private Tag(FrozenDictionary<Type, object> data)
    {
        Data = data;
    }


    /// <summary>Gets the number of entries in the tag.</summary>
    public int Count => Data.Count;


    /// <summary>
    /// Gets the entries as a read-only projection, for emitting the metadata elsewhere — for example
    /// building OpenTelemetry or CBOM attributes by mapping each <see cref="Type"/> key to a name. This
    /// is a one-way projection; values are still written and read through the typed members.
    /// </summary>
    public IReadOnlyCollection<KeyValuePair<Type, object>> Entries => Data;


    /// <summary>Creates a tag holding a single value, keyed by its type.</summary>
    /// <typeparam name="T">The type used as the key.</typeparam>
    /// <param name="value">The value to store.</param>
    /// <returns>A new <see cref="Tag"/> containing <paramref name="value"/>.</returns>
    public static Tag Create<T>(T value) where T : notnull
    {
        var dict = new Dictionary<Type, object>(1)
        {
            [typeof(T)] = value
        };

        return new Tag(dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns a new tag containing every entry of this tag plus <paramref name="value"/>, keyed by
    /// <typeparamref name="T"/>; an existing value of the same type is replaced.
    /// </summary>
    /// <typeparam name="T">The type used as the key.</typeparam>
    /// <param name="value">The value to add or replace.</param>
    /// <returns>A new <see cref="Tag"/> with the entry added or replaced.</returns>
    public Tag With<T>(T value) where T : notnull
    {
        var dict = new Dictionary<Type, object>(Data.Count + 1);
        foreach(KeyValuePair<Type, object> existing in Data)
        {
            dict[existing.Key] = existing.Value;
        }

        dict[typeof(T)] = value;

        return new Tag(dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns a new tag without the entry keyed by <typeparamref name="T"/>, or this tag unchanged when
    /// no such entry exists.
    /// </summary>
    /// <typeparam name="T">The type key of the entry to remove.</typeparam>
    /// <returns>A new <see cref="Tag"/> without the entry, or this tag if none was present.</returns>
    public Tag Without<T>()
    {
        if(!Data.ContainsKey(typeof(T)))
        {
            return this;
        }

        var dict = new Dictionary<Type, object>(Data.Count - 1);
        foreach(KeyValuePair<Type, object> existing in Data)
        {
            if(existing.Key != typeof(T))
            {
                dict[existing.Key] = existing.Value;
            }
        }

        return dict.Count == 0
            ? Empty
            : new Tag(dict.ToFrozenDictionary());
    }


    /// <summary>Retrieves the value keyed by <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type of the value to retrieve, also the key.</typeparam>
    /// <returns>The value keyed by <typeparamref name="T"/>.</returns>
    /// <exception cref="KeyNotFoundException">No value of type <typeparamref name="T"/> is present.</exception>
    public T Get<T>()
    {
        if(Data.TryGetValue(typeof(T), out object? stored))
        {
            return (T)stored;
        }

        throw new KeyNotFoundException($"No value of type '{typeof(T)}' is present in the tag.");
    }


    /// <summary>Attempts to retrieve the value keyed by <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type of the value to retrieve, also the key.</typeparam>
    /// <param name="value">When successful, the retrieved value.</param>
    /// <returns><see langword="true"/> when a value of type <typeparamref name="T"/> is present.</returns>
    public bool TryGet<T>([MaybeNullWhen(false)] out T value)
    {
        if(Data.TryGetValue(typeof(T), out object? stored) && stored is T typed)
        {
            value = typed;

            return true;
        }

        value = default;

        return false;
    }


    /// <summary>Determines whether the tag contains an entry keyed by <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type key to test for.</typeparam>
    /// <returns><see langword="true"/> when an entry of type <typeparamref name="T"/> is present.</returns>
    public bool Contains<T>()
    {
        return Data.ContainsKey(typeof(T));
    }


    /// <inheritdoc/>
    public bool Equals(Tag? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        if(Data.Count != other.Data.Count)
        {
            return false;
        }

        foreach(KeyValuePair<Type, object> entry in Data)
        {
            if(!other.Data.TryGetValue(entry.Key, out object? otherValue) || !object.Equals(entry.Value, otherValue))
            {
                return false;
            }
        }

        return true;
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is Tag other && Equals(other);
    }


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        //XOR the per-entry hashes so the result is independent of entry order, matching the
        //order-independent, content-based equality.
        int hash = 0;
        foreach(KeyValuePair<Type, object> entry in Data)
        {
            hash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        return hash;
    }


    /// <summary>Determines whether two tags have equal content.</summary>
    public static bool operator ==(Tag? left, Tag? right)
    {
        return left is null ? right is null : left.Equals(right);
    }


    /// <summary>Determines whether two tags differ in content.</summary>
    public static bool operator !=(Tag? left, Tag? right)
    {
        return !(left == right);
    }


    /// <inheritdoc />
    public override string ToString()
    {
        return TagString;
    }


    private string TagString => Data.Count == 0
        ? "Tag: (empty)"
        : $"Tag: [{string.Join(", ", Data.Select(entry => $"{entry.Key.Name}={entry.Value}"))}]";


    private string DebuggerView
    {
        get
        {
            try
            {
                return TagString;
            }
            catch
            {
                return "Tag: (error)";
            }
        }
    }
}
