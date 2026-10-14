namespace WhiteListApi.Storage;

using System.IO.MemoryMappedFiles;

public sealed class WhiteListBinaryStore : IDisposable
{
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly unsafe byte* _pointer;
    private readonly int _elementsCount;

    public int Iterations { get; }
    public int ElementsCount => _elementsCount;

    public unsafe WhiteListBinaryStore(string filePath, int iterations = 5000)
    {
        Iterations = iterations;
        var fileInfo = new FileInfo(filePath);
        _elementsCount = (int)(fileInfo.Length / 16);

        if (_elementsCount == 0)
        {
            _mmf = null;
            _accessor = null;
            _pointer = null;
            return;
        }

        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, mapName: null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
    }

    public unsafe bool ContainsHash(UInt128 targetHash)
    {
        if (_pointer == null || _elementsCount == 0) return false;
        var span = new ReadOnlySpan<UInt128>(_pointer, _elementsCount);
        return span.BinarySearch(targetHash) >= 0;
    }

    public unsafe void Dispose()
    {
        if (_pointer != null && _accessor != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        _accessor?.Dispose();
        _mmf?.Dispose();
    }
}
