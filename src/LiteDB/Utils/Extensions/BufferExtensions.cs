﻿namespace LiteDB;

internal static class BufferExtensions
{
    // https://code.google.com/p/freshbooks-api/source/browse/depend/NClassify.Generator/content/ByteArray.cs?r=bbb6c13ec7a01eae082796550f1ddc05f61694b8
    public static int BinaryCompareTo(this byte[] lh, byte[] rh)
    {
        if (lh == null) return rh == null ? 0 : -1;
        if (rh == null) return 1;

        var result = 0;
        var i = 0;
        var stop = Math.Min(lh.Length, rh.Length);

        for (; 0 == result && i < stop; i++)
            result = lh[i].CompareTo(rh[i]);

        if (result != 0) return result < 0 ? -1 : 1;
        if (i == lh.Length) return i == rh.Length ? 0 : -1;
        return 1;
    }

    /// <summary>
    /// Very fast way to check if all byte array is full of zero
    /// </summary>
    public static unsafe bool IsFullZero(this byte[] data)
    {
        fixed (byte* bytes = data)
        {
            int len = data.Length;
            int rem = len % (sizeof(long) * 16);
            long* b = (long*)bytes;
            long* e = (long*)(bytes + len - rem);

            while (b < e)
            {
                if ((*(b) | *(b + 1) | *(b + 2) | *(b + 3) | *(b + 4) |
                    *(b + 5) | *(b + 6) | *(b + 7) | *(b + 8) |
                    *(b + 9) | *(b + 10) | *(b + 11) | *(b + 12) |
                    *(b + 13) | *(b + 14) | *(b + 15)) != 0)
                    return false;
                b += 16;
            }

            for (int i = 0; i < rem; i++)
                if (data[len - 1 - i] != 0)
                    return false;

            return true;
        }
    }
}