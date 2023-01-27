﻿namespace LiteDB.Engine;

/// <summary>
/// Implement custom fast/in memory mapped disk access
/// [ThreadSafe]
/// </summary>
internal class DiskService : IDisposable
{
    private readonly SemaphoreSlim _locker = new (1, 1);

    private readonly IStreamFactory _streamFactory;

    private readonly StreamPool _streamPool;

    private long _logStartPosition;
    private long _logEndPosition;

    public bool IsNew { get; }

    public DiskService(IStreamFactory streamFactory, bool readOnly)
    { 
        // get new stream factory based on EngineSettings
        _streamFactory = streamFactory;

        // create stream pool
        _streamPool = new StreamPool(_streamFactory, readOnly);

        // checks if is a new file
        this.IsNew = readOnly == false && _streamPool.Writer.Length == 0;

        // will be update later
        _logStartPosition = 0;
        _logEndPosition = 0;
    }

    /// <summary>
    /// Get log length
    /// </summary>
    public long LogLength => _logEndPosition - _logStartPosition;

    /// <summary>
    /// Get current file length (with log inclued)
    /// </summary>
    public long FileLength => 
        _streamPool.Writer?.Length ?? // get filesize direct from writer stream
        _streamFactory.GetLength(); // if readonly, get by os descriptor

    /// <summary>
    /// Get a new instance for read data/log pages. This instance are not thread-safe - must request 1 per thread (used in Transaction)
    /// Must call IDispose() after use to return to Pool
    /// </summary>
    public DiskReader GetReader()
    {
        return new DiskReader(_streamPool);
    }

    /// <summary>
    /// Write all pages buffers into disk DATA and flush after saved
    /// </summary>
    public async Task WriteDataAsync(IEnumerable<PageDataLocation> pages, CancellationToken cancellationToken = default)
    {
        var stream = _streamPool.Writer;

        // get exclusive lock in write operations
        await _locker.WaitAsync(cancellationToken);

        try
        {
            foreach(var page in pages)
            {
                var dataPosition = BasePage.GetPagePosition(page.PageID);

                ENSURE(_logEndPosition != 0, dataPosition >= _logStartPosition, "Data pages must be saved before LOG");

                //TODO: faz CRC8 aqui?

                stream.Position = dataPosition;

                await stream.WriteAsync(page.Buffer, cancellationToken);
            }

            // flush data into disk
            await stream.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw ERR_DISK_WRITE_FAILURE(ex);
        }
        finally
        {
            _locker.Release();
        }
    }

    /// <summary>
    /// Write memory pages inside LOG (only) disk and flush after save pages 
    /// Will update all PageLocation with final disk (log) position
    /// It´s thread safe
    /// </summary>
    public async Task WriteLogAsync(IEnumerable<PageLogLocation> pages, CancellationToken cancellationToken = default)
    {
        ENSURE(_logStartPosition > 0, "Disk WAL not initialized");

        var stream = _streamPool.Writer;

        // get exclusive lock in write operations
        await _locker.WaitAsync(cancellationToken);

        try
        {
            var position = _logEndPosition;

            using var cursor = pages.GetEnumerator();

            // using cursor because loop need change page.Position struct (foreach don't work)
            while (cursor.MoveNext())
            {
                var page = cursor.Current;

                var dataPosition = BasePage.GetPagePosition(page.PageID);

                // checks for override WAL with PageID > Log position
                while (position < dataPosition)
                {
                    // checks if position are no an allocation map page
                    if (AllocationMapPage.IsAllocationMapPageID((uint)(position / PAGE_SIZE)) == false)
                    {
                        position += PAGE_SIZE;
                    }

                    position += PAGE_SIZE;
                }

                // checks if current position is not a PFS page
                if (AllocationMapPage.IsAllocationMapPageID((uint)(position / PAGE_SIZE)))
                {
                    position += PAGE_SIZE;
                }

                // update position position and stream position to write
                page.Position = stream.Position = position;

                //TODO: faz crc8 aqui?

                await stream.WriteAsync(page.Buffer, cancellationToken);

                position += PAGE_SIZE;

            }

            // flush data into disk
            await stream.FlushAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            throw ERR_DISK_WRITE_FAILURE(ex);
        }
        finally
        {
            _locker.Release();
        }
    }

    public void Dispose()
    {
        // dispose Stream pools
        _streamPool?.Dispose();
    }
}
