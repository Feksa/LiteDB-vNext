﻿using System;
using System.Threading;

namespace LiteDB.Engine;

/// <summary>
/// * Singleton (thread safe)
/// </summary>
[AutoInterface(typeof(IDisposable))]
internal class AllocationMapService : IAllocationMapService
{
    private readonly IServicesFactory _factory;
    private readonly IDiskService _disk;
    private readonly IBufferFactoryService _bufferFactory;

    /// <summary>
    /// List of all allocation map pages, in pageID order
    /// </summary>
    private readonly List<AllocationMapPage> _pages = new();

    /// <summary>
    /// A struct, per colID, to store a list of pages with available space
    /// </summary>
    private readonly CollectionFreePages[] _collectionFreePages = new CollectionFreePages[byte.MaxValue];

    public AllocationMapService(IServicesFactory factory)
    {
        _factory = factory;
        _disk = _factory.GetDisk();
        _bufferFactory = _factory.GetBufferFactory();
    }

    /// <summary>
    /// Initialize allocation map service loading all AM pages into memory and getting
    /// </summary>
    /// <returns></returns>
    public async Task InitializeAsync()
    {
        // read all allocation maps pages on disk
        await foreach (var pageBuffer in _disk.ReadAllocationMapPages())
        {
            // get page buffer from disk
            var page = new AllocationMapPage(pageBuffer);

            // read all collection map in memory
            page.ReadAllocationMap(_collectionFreePages);

            // add AM page to instance
            _pages.Add(page);
        }
    }

    /// <summary>
    /// Return a page ID with space available to store 'length' bytes. Support only DataPages and IndexPages.
    /// Return pageID and a bool that indicates if this page is a new empty page (must be created)
    /// </summary>  
    public (uint, bool) GetFreePageID(byte colID, PageType type, int length)
    {
        //TODO: sombrio, posso retornar uma pagina com tamanho menor do solicitado?
        // o chamador que peça uma nova com o restante (while)
        // ta feio assim, mas tem como ficar bonito e eficiente (sem alocar memoria)

        var freePages = _collectionFreePages[colID];

        if (type == PageType.Data)
        {
            // test if length for SMALL size document length
            if (length < AM_DATA_PAGE_SPACE_SMALL)
            {
                if (freePages.DataPagesSmall.Count > 0) // test for small bucket
                {
                    return (freePages.DataPagesSmall.Dequeue(), false);
                }
                else if (freePages.DataPagesMedium.Count > 0) // test in medium bucket
                {
                    return (freePages.DataPagesMedium.Dequeue(), false);
                }
                else if (freePages.DataPagesLarge.Count > 0) // test in large bucket
                {
                    return (freePages.DataPagesLarge.Dequeue(), false);
                }
            }

            // test if length for MEDIUM size document length
            else if (length < AM_DATA_PAGE_SPACE_MEDIUM)
            {
                if (freePages.DataPagesMedium.Count > 0) // test in medium bucket
                {
                    return (freePages.DataPagesMedium.Dequeue(), false);
                }
                else if (freePages.DataPagesLarge.Count > 0) // test for large bucket
                {
                    return (freePages.DataPagesLarge.Dequeue(), false);
                }
            }

            // test if length for LARGE size document length (considering 1 page block)
            else if (length < AM_DATA_PAGE_SPACE_LARGE)
            {
                if (freePages.DataPagesLarge.Count > 0)
                {
                    return (freePages.DataPagesLarge.Dequeue(), false);
                }
            }
        }
        else // PageType = IndexPage
        {
            if (freePages.IndexPages.Count > 0)
            {
                return (freePages.IndexPages.Dequeue(), false);
            }
        }

        //TODO: nesse ponto eu poderia tentar dar um "Reload" na freePages pra carregar mais (se tiver mais)

        // there is no page available with a best fit - create a new page
        if (freePages.EmptyPages.Count > 0)
        {
            return (freePages.EmptyPages.Dequeue(), true);
        }

        // if there is no empty pages, create new extend for this collection with new 8 pages
        var emptyPageID = this.CreateNewExtend(colID, freePages);

        return (emptyPageID, true);
    }

    /// <summary>
    /// Create a new extend in any allocation map page that contains space available. If all pages are full, create another allocation map page
    /// Return the first empty pageID created for this collection in this new extend
    /// This method populate collectionFreePages[colID] with 8 new empty pages
    /// </summary>
    private uint CreateNewExtend(byte colID, CollectionFreePages freePages)
    {
        //TODO: lock, pois não pode ter 2 threads aqui


        // try create extend in all AM pages already exists
        foreach (var page in _pages)
        {
            // create new extend on page (if this page contains empty extends)
            var created = page.CreateNewExtend(colID, freePages);

            if (created)
            {
                // return first empty page
                return freePages.EmptyPages.Dequeue();
            }
        }

        // if there is no more free extend in any AM page, let's create a new allocation map page
        var pageBuffer = _bufferFactory.AllocateNewBuffer();

        // get a new PageID based on last AM page
        var nextPageID = _pages.Last().PageID + AM_PAGE_STEP;

        // create new AM page and add to list
        var newPage = new AllocationMapPage(nextPageID, pageBuffer);

        _pages.Add(newPage);

        // create new extend for this collection - always return true because it´s a new page
        newPage.CreateNewExtend(colID, freePages);

        // return first empty page
        return freePages.EmptyPages.Dequeue();
    }

    /// <summary>
    /// Update all map position pages based on 
    /// </summary>
    public void UpdateMap(IEnumerable<PageBuffer> modifiedPages)
    {
        // nesse processo deve atualizar _collectionFreePages, adicionando as paginas no lugar certo
        // (não pode pre-existir, pois já foi "dequeue")

        // deve atualizar também o buffer das AM pages envolvidas.
        // Não há criação de AM pages aqui

        foreach(var page in modifiedPages)
        {
            var header = page.Header;

            var allocationMapID = (int)(header.PageID / AM_PAGE_STEP);
            var extendIndex = (int)(header.PageID - 1 - allocationMapID * AM_PAGE_STEP) / AM_EXTEND_SIZE;
            var pageIndex = (int)(header.PageID - 1 - allocationMapID * AM_PAGE_STEP - extendIndex * AM_EXTEND_SIZE);
            byte value = 0; // calcular conforme page.Header.FreeSpace (0-7)

            ENSURE(pageIndex != -1, "PageID cannot be an AM page ID");

            var mapPage = _pages[allocationMapID];

            // update buffer map
            mapPage.UpdateMap(extendIndex, pageIndex, value);

            var freePages = _collectionFreePages[header.ColID];

            switch (value)
            {
                case 0b000: // 0
                    freePages.EmptyPages.Insert(header.PageID);
                    break;
                case 0b001: // 1
                    freePages.DataPagesLarge.Insert(header.PageID);
                    break;
                case 0b010: // 2
                    freePages.DataPagesMedium.Insert(header.PageID);
                    break;
                case 0b011: // 3
                    freePages.DataPagesSmall.Insert(header.PageID);
                    break;
                case 0b100: // 4 - data page full
                    break;
                case 0b101: // 5 - index page with available space
                    freePages.IndexPages.Insert(header.PageID);
                    break;
                case 0b110: // 6 - index page full
                case 0b111: // 7 - reserved
                    break;

            }
        }
    }

    public void Dispose()
    {
        // limpar paginas
    }
}