﻿namespace LiteDB.Engine;

/// <summary>
/// </summary>
[AutoInterface(typeof(IDisposable))]
internal class Transaction : ITransaction
{
    // dependency injection
    private readonly IDiskService _diskService;
    private readonly ILogService _logService;
    private readonly IWalIndexService _walIndexService;
    private readonly IAllocationMapService _allocationMapService;
    private readonly IIndexPageService _indexPageService;
    private readonly IDataPageService _dataPageService;
    private readonly IBufferFactory _bufferFactory;
    private readonly ICacheService _cacheService;
    private readonly ILockService _lockService;

    // count how many locks this transaction contains
    private int _lockCounter = 0;

    // rented reader stream
    private IDiskStream? _reader;

    // local page cache - contains only data/index pages about this collection
    private readonly Dictionary<int, PageBuffer> _localPages = new();

    // all writable collections ID (must be lock on init)
    private readonly byte[] _writeCollections;

    /// <summary>
    /// Read wal version
    /// </summary>
    public int ReadVersion { get; private set; }

    /// <summary>
    /// Incremental transaction ID
    /// </summary>
    public int TransactionID { get; }

    public Transaction(
        IDiskService diskService,
        ILogService logService,
        IBufferFactory bufferFactory,
        ICacheService cacheService,
        IWalIndexService walIndexService,
        IAllocationMapService allocationMapService,
        IIndexPageService indexPageService,
        IDataPageService dataPageService,
        ILockService lockService,
        int transactionID, byte[] writeCollections, int readVersion)
    {
        _diskService = diskService;
        _logService = logService;
        _bufferFactory = bufferFactory;
        _cacheService = cacheService;
        _walIndexService = walIndexService;
        _allocationMapService = allocationMapService;
        _indexPageService = indexPageService;
        _dataPageService = dataPageService;
        _lockService = lockService;

        this.TransactionID = transactionID;
        this.ReadVersion = readVersion; // -1 means not initialized

        _writeCollections = writeCollections;
    }

    /// <summary>
    /// Initialize transaction enter in database read lock
    /// </summary>
    public async Task InitializeAsync()
    {
        // enter transaction lock
        await _lockService.EnterTransactionAsync();

        _lockCounter = 1;

        for(var i = 0; i < _writeCollections.Length; i++)
        {
            // enter in all
            await _lockService.EnterCollectionWriteLockAsync(_writeCollections[i]);

            // increment lockCounter to dispose control
            _lockCounter++;
        }

        // if readVersion is -1 must be initialized with next read version from wal
        if (this.ReadVersion == -1)
        {
            // initialize read version from wal
            this.ReadVersion = _walIndexService.GetNextReadVersion();
        }

        ENSURE(this.ReadVersion >= _walIndexService.MinReadVersion, $"read version do not exists in wal index: {this.ReadVersion} >= {_walIndexService.MinReadVersion}");
    }

    /// <summary>
    /// Try get page from local cache. If page not found, use ReadPage from disk
    /// </summary>
    public async Task<PageBuffer> GetPageAsync(int pageID, bool writable)
    {
        if (_localPages.TryGetValue(pageID, out var page))
        {
            ENSURE(writable, page.ShareCounter == NO_CACHE, "page should not be in cache");

            return page;
        }

        page = await this.ReadPageAsync(pageID, this.ReadVersion, writable);

        _localPages.Add(pageID, page);

        return page;
    }

    /// <summary>
    /// Read a data/index page from disk (data or log). Can return page from global cache
    /// </summary>
    private async Task<PageBuffer> ReadPageAsync(int pageID, int readVersion, bool writable)
    {
        _reader ??= _diskService.RentDiskReader();

        // get disk position (data/log)
        var positionID = _walIndexService.GetPagePositionID(pageID, readVersion, out _);

        // get a page from cache (if writable, this page are not linked to cache anymore)
        var page = writable ? 
            _cacheService.GetPageWrite(positionID) :
            _cacheService.GetPageRead(positionID);

        // if page not found, allocate new page and read from disk
        if (page is null)
        {
            page = _bufferFactory.AllocateNewPage(writable);

            await _reader.ReadPageAsync(positionID, page);
        }

        return page;
    }

    /// <summary>
    /// Get a page with free space avaiable to store, at least, bytesLength
    /// </summary>
    public async Task<PageBuffer> GetFreePageAsync(byte colID, PageType pageType, int bytesLength)
    {
        // first check if exists in localPages (TODO: como indexar isso??)
        var localPage = _localPages.Values
            .Where(x => x.Header.PageType == pageType && x.Header.FreeBytes >= bytesLength)
            .FirstOrDefault();

        if (localPage is not null) return localPage;

        // request for allocation map service a new PageID for this collection
        var (pageID, isNew) = _allocationMapService.GetFreePageID(colID, pageType, bytesLength);

        if (isNew)
        {
            var page = _bufferFactory.AllocateNewPage(true);

            // initialize empty page as data/index page
            if (pageType == PageType.Data)
            {
                _dataPageService.InitializeDataPage(page, pageID, colID);
            }
            else if (pageType == PageType.Index)
            {
                _indexPageService.InitializeIndexPage(page, pageID, colID);
            }
            else throw new NotSupportedException();

            // add in local cache
            _localPages.Add(pageID, page);

            return page;
        }
        else
        {
            var page = await this.GetPageAsync(pageID, true);

            return page;
        }
    }

    /// <summary>
    /// </summary>
    public async Task CommitAsync()
    {
        // get dirty pages only //TODO: can be re-used array?
        var dirtyPages = _localPages.Values
            .Where(x => x.IsDirty)
            .ToArray();

        for (var i = 0; i < dirtyPages.Length; i++)
        {
            var page = dirtyPages[i];

            ENSURE(page.ShareCounter == NO_CACHE, "page should not be on cache when saving");
            ENSURE(page.PositionID == int.MaxValue, "page must be empty position id");

            // update page header
            page.Header.TransactionID = this.TransactionID;
            page.Header.IsConfirmed = i == (dirtyPages.Length - 1);
        }

        // write pages on disk and flush data
        await _logService.WriteLogPagesAsync(dirtyPages);

        // update allocation map with all dirty pages
        _allocationMapService.UpdateMap(dirtyPages);

        // add pages to cache or decrement sharecount
        foreach(var page in _localPages.Values)
        {
            // page already in cache (was not changed)
            if (page.ShareCounter > 0)
            {
                _cacheService.ReturnPage(page);
            }
            else
            {
                // try add this page in cache
                var added = _cacheService.AddPageInCache(page);

                if (!added)
                {
                    _bufferFactory.DeallocatePage(page);
                }
            }
        }

        // update wal index with this new version
        var pagePositions = dirtyPages
            .Select(x => (x.Header.PageID, x.PositionID))
#if DEBUG
            .ToArray()
#endif
            ;

        _walIndexService.AddVersion(this.ReadVersion, pagePositions);

        // clear page buffer references
        _localPages.Clear();
    }

    public void Rollback()
    {
        // add pages to cache or decrement sharecount
        foreach (var page in _localPages.Values)
        {
            if (page.IsDirty)
            {
                _bufferFactory.DeallocatePage(page);
            }
            else
            {
                // test if page is came from the cache
                if (page.ShareCounter > 0)
                {
                    // return page to cache
                    _cacheService.ReturnPage(page);
                }
                else
                {
                    // try add this page in cache
                    var added = _cacheService.AddPageInCache(page);

                    if (!added)
                    {
                        _bufferFactory.DeallocatePage(page);
                    }
                }
            }
        }

        // clear page buffer references
        _localPages.Clear();
    }

    public void Dispose()
    {
        ENSURE(_localPages.Count == 0, $"no pages in transaction before dispose");

        // return reader if used
        if (_reader is not null)
        {
            _diskService.ReturnDiskReader(_reader);
        }

        if (_lockCounter == 0) return; // no locks

        while (_lockCounter > 1)
        {
            _lockService.ExitCollectionWriteLock(_writeCollections[_lockCounter - 2]);
            _lockCounter--;
        }

        // exit lock transaction
        _lockService.ExitTransaction();

        _lockCounter--;

        ENSURE(_localPages.Count == 0, $"missing dispose pages in transaction {this}");
        ENSURE(_lockCounter == 0, $"missing release lock in transaction {this}");
    }
}