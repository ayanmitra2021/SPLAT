using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Splat.Data;

/// <summary>
/// A block of memory aligned to 64 bytes (a cache line, and the widest vector register), reference counted, and optionally
/// backed by a memory-mapped file. Every tensor stores its elements in one of these, so table columns (Phase 19) can share
/// the same storage without a rewrite.
/// <para>
/// Lifetime: a buffer starts with one reference. <see cref="Retain"/> adds one and <see cref="Release"/> drops one; the
/// memory is freed when the count reaches zero. A buffer nobody releases is freed by the finalizer once the GC finds it
/// unreachable, so the interpreter (which has no explicit tensor lifetimes before Phase 17) can simply drop references.
/// </para>
/// </summary>
public sealed unsafe class AlignedBuffer : IDisposable
{
    /// <summary>The alignment, in bytes, of <see cref="Address"/>.</summary>
    public const int Alignment = 64;

    private static long _nextId;

    private byte* _ptr;
    private int _refs = 1;
    private readonly MemoryMappedFile? _file;
    private readonly MemoryMappedViewAccessor? _view;

    private AlignedBuffer(byte* ptr, long length, MemoryMappedFile? file, MemoryMappedViewAccessor? view)
    {
        _ptr = ptr;
        Length = length;
        _file = file;
        _view = view;
        Id = Interlocked.Increment(ref _nextId);
        if (file is null && length > 1_000_000) GC.AddMemoryPressure(length);
    }

    /// <summary>A process-unique number for this allocation (the <c>BufferId</c> the numeric engine uses to prove two tensors do not alias).</summary>
    public long Id { get; }

    /// <summary>Size in bytes.</summary>
    public long Length { get; }

    /// <summary>True when the bytes come from a memory-mapped file rather than the native heap.</summary>
    public bool IsMapped => _file is not null;

    /// <summary>True once every reference was released and the memory is gone.</summary>
    public bool IsFreed => _ptr is null;

    /// <summary>The base address (always a multiple of <see cref="Alignment"/>).</summary>
    public nuint Address => (nuint)_ptr;

    /// <summary>Allocates <paramref name="bytes"/> bytes, zero-filled unless <paramref name="zero"/> is false.</summary>
    public static AlignedBuffer Allocate(long bytes, bool zero = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var ptr = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(bytes, 1), Alignment);
        if (zero) NativeMemory.Clear(ptr, (nuint)Math.Max(bytes, 1));
        return new AlignedBuffer(ptr, bytes, null, null);
    }

    /// <summary>
    /// Maps <paramref name="length"/> bytes of a file starting at <paramref name="offset"/>. The view is read-only by default.
    /// Mapped addresses are page aligned, so the 64-byte guarantee holds as long as <paramref name="offset"/> is a multiple of the
    /// allocation granularity (the mapper rounds it down and adjusts internally).
    /// </summary>
    public static AlignedBuffer MapFile(string path, long offset = 0, long length = 0, bool writable = false)
    {
        var access = writable ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
        var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, access);
        MemoryMappedViewAccessor? view = null;
        try
        {
            long size = new FileInfo(path).Length;
            if (length <= 0) length = size - offset;
            view = file.CreateViewAccessor(offset, length, access);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            p += view.PointerOffset;
            if (((nuint)p & (Alignment - 1)) != 0)
                throw new InvalidOperationException($"mapped view of '{path}' at offset {offset} is not {Alignment}-byte aligned; use an offset that is a multiple of the allocation granularity");
            return new AlignedBuffer(p, length, file, view);
        }
        catch
        {
            view?.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>Adds a reference and returns this buffer.</summary>
    public AlignedBuffer Retain()
    {
        int r;
        do
        {
            r = Volatile.Read(ref _refs);
            if (r <= 0) throw new ObjectDisposedException(nameof(AlignedBuffer));
        } while (Interlocked.CompareExchange(ref _refs, r + 1, r) != r);
        return this;
    }

    /// <summary>Drops a reference; frees the memory when the last one goes.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0) Free();
    }

    /// <summary>The current reference count (for tests).</summary>
    public int RefCount => Volatile.Read(ref _refs);

    void IDisposable.Dispose() => Release();

    private void Free()
    {
        var p = _ptr;
        _ptr = null;
        if (p is null) return;
        if (_file is not null)
        {
            _view?.SafeMemoryMappedViewHandle.ReleasePointer();
            _view?.Dispose();
            _file.Dispose();
        }
        else
        {
            NativeMemory.AlignedFree(p);
            if (Length > 1_000_000) GC.RemoveMemoryPressure(Length);
        }
        GC.SuppressFinalize(this);
    }

    ~AlignedBuffer() => Free();

    /// <summary>The whole buffer viewed as elements of type <typeparamref name="T"/> (the byte length is rounded down).</summary>
    public Span<T> AsSpan<T>() where T : unmanaged => AsSpan<T>(0, Length / sizeof(T));

    /// <summary>A window of <paramref name="count"/> elements starting at element <paramref name="start"/>.</summary>
    public Span<T> AsSpan<T>(long start, long count) where T : unmanaged
    {
        if (_ptr is null) throw new ObjectDisposedException(nameof(AlignedBuffer));
        if (start < 0 || count < 0 || (start + count) * sizeof(T) > Length)
            throw new ArgumentOutOfRangeException(nameof(count), $"window [{start}, {start + count}) is outside a buffer of {Length / sizeof(T)} elements");
        return new Span<T>(_ptr + start * sizeof(T), checked((int)count));
    }
}
