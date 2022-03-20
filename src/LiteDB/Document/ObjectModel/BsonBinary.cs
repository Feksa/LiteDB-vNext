﻿namespace LiteDB;

/// <summary>
/// Represent a Binary value (byte array) in Bson object model
/// </summary>
public class BsonBinary : BsonValue, IComparable<BsonBinary>, IEquatable<BsonBinary>
{
    public byte[] Value { get; }

    public BsonBinary(byte[] value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

        this.Value = value;
    }

    public override BsonType Type => BsonType.Binary;

    public override int GetBytesCount() => this.Value.Length;

    public override int CompareTo(BsonValue other, Collation collation)
    {
        if (other == null) return 1;

        if (other is BsonBinary otherBinary) return this.Value.BinaryCompareTo(otherBinary.Value);

        return this.CompareType(other);
    }

    public int CompareTo(BsonBinary other)
    {
        if (other == null) return 1;

        return this.Value.BinaryCompareTo(other.Value);
    }

    public bool Equals(BsonBinary rhs)
    {
        if (rhs is null) return false;

        return this.Value.BinaryCompareTo(rhs.Value) == 0;
    }

    #region Explicit operators

    public static bool operator ==(BsonBinary lhs, BsonBinary rhs) => lhs.Equals(rhs);

    public static bool operator !=(BsonBinary lhs, BsonBinary rhs) => !lhs.Equals(rhs);

    #endregion

    #region Implicit Ctor

    public static implicit operator byte[](BsonBinary value) => value.Value;

    public static implicit operator BsonBinary(byte[] value) => new BsonBinary(value);

    #endregion

    #region GetHashCode, Equals, ToString override

    public override int GetHashCode() => this.Value.GetHashCode();

    public override bool Equals(object obj) => this.Value.Equals(obj);

    public override string ToString() => String.Join(",",  this.Value.Select(x => x.ToString()));

    #endregion
}
