﻿namespace LiteDB.Engine;

/// <summary>
/// All engine settings used to starts new engine
/// </summary>
public class EngineSettings
{
    /// <summary>
    /// Get/Set custom stream to be used as datafile (can be MemoryStrem or TempStream). Do not use FileStream - to use physical file, use "filename" attribute (and keep DataStrem null)
    /// </summary>
    public Stream DataStream { get; set; } = null;

    /// <summary>
    /// Full path or relative path from DLL directory. Can use ':temp:' for temp database or ':memory:' for in-memory database. (default: null)
    /// </summary>
    public string Filename { get; set; }

    /// <summary>
    /// Get database password to decrypt pages
    /// </summary>
    public string Password { get; set; }

    /// <summary>
    /// If database is new, initialize with allocated space (in bytes) (default: 0)
    /// </summary>
    public long InitialSize { get; set; } = 0;

    /// <summary>
    /// Create database with custom string collection (used only to create database) (default: Collation.Default)
    /// </summary>
    public Collation Collation { get; set; }

    /// <summary>
    /// Indicate that engine will open files in readonly mode (and will not support any database change)
    /// </summary>
    public bool ReadOnly { get; set; } = false;

    /// <summary>
    /// Create new IStreamFactory for datafile
    /// </summary>
    internal IStreamFactory CreateDataFactory()
    {
        if (this.DataStream != null)
        {
            return new StreamFactory(this.DataStream, this.Password);
        }
        else if (this.Filename == ":memory:")
        {
            return new StreamFactory(new MemoryStream(), this.Password);
        }
        else if (this.Filename == ":temp:")
        {
            return new StreamFactory(new TempStream(), this.Password);
        }
        else if (!string.IsNullOrEmpty(this.Filename))
        {
            return new FileStreamFactory(this.Filename, this.Password, false);
        }

        throw new ArgumentException("EngineSettings must have Filename or DataStream as data source");
    }

    /// <summary>
    /// Create new IStreamFactory for temporary file (sort)
    /// </summary>
    internal IStreamFactory CreateTempFactory()
    {
        if (this.DataStream is MemoryStream || this.Filename == ":memory:" || this.ReadOnly)
        {
            return new StreamFactory(new MemoryStream(), null);
        }
        else if (this.Filename == ":temp:")
        {
            return new StreamFactory(new TempStream(), null);
        }
        else if (!string.IsNullOrEmpty(this.Filename))
        {
            var tempName = FileHelper.GetSufixFile(this.Filename, "-tmp", true);

            return new FileStreamFactory(tempName, null, true);
        }

        return new StreamFactory(new TempStream(), null);
    }
}
