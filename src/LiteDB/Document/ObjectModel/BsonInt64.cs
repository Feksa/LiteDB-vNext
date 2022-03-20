﻿namespace LiteDB;

/// <summary>
/// Represent an Int64 value in Bson object model
/// </summary>
public class BsonInt64 : BsonValue, IComparable<BsonInt64>, IEquatable<BsonInt64>
{
    public Int64 Value { get; }

    public BsonInt64(Int64 value)
    {
        this.Value = value;
    }

    public override BsonType Type => BsonType.Int64;

    public override int GetBytesCount() => sizeof(Int64);

    public override int CompareTo(BsonValue other, Collation collation)
    {
        if (other == null) return 1;

        if (other is BsonInt64 otherInt64) return this.Value.CompareTo(otherInt64.Value);
        if (other is BsonInt32 otherInt32) return this.Value.CompareTo(otherInt32.Value);

        // compare to other numbers types

        return this.CompareType(other);
    }

    public int CompareTo(BsonInt64 other)
    {
        if (other == null) return 1;

        return this.Value.CompareTo(other.Value);
    }

    public bool Equals(BsonInt64 rhs)
    {
        if (rhs is null) return false;

        return this.Value == rhs.Value;
    }

    #region Explicit operators

    public static bool operator ==(BsonInt64 lhs, BsonInt64 rhs) => lhs.Equals(rhs);

    public static bool operator !=(BsonInt64 lhs, BsonInt64 rhs) => !lhs.Equals(rhs);

    #endregion

    #region Implicit Ctor

    public static implicit operator Int64(BsonInt64 value) => value.Value;

    public static implicit operator BsonInt64(Int64 value) => new BsonInt64(value);

    #endregion

    #region GetHashCode, Equals, ToString override

    public override int GetHashCode() => this.Value.GetHashCode();

    public override bool Equals(object obj) => this.Value.Equals(obj);

    public override string ToString() => this.Value.ToString();

    #endregion
}
