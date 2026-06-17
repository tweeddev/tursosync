using System.Data;
using Turso;

namespace TursoSync.Tests;

/// <summary>
/// Pure-logic tests for <see cref="TursoParameter"/> and its collection — value type-mapping
/// (<c>ToBindValue</c>), invariant formatting, direction/size validation. No native engine required.
/// </summary>
[TestClass]
public class TursoParameterTests
{
    [TestMethod]
    public void ToBindValue_MapsIntegralTypesToInt64()
    {
        new TursoParameter("@a", true).ToBindValue().Should().Be(1L);
        new TursoParameter("@a", (byte)7).ToBindValue().Should().Be(7L);
        new TursoParameter("@a", (short)9).ToBindValue().Should().Be(9L);
        new TursoParameter("@a", 42).ToBindValue().Should().Be(42L);
        new TursoParameter("@a", 42L).ToBindValue().Should().Be(42L);
    }

    [TestMethod]
    public void ToBindValue_MapsRealTypesToDouble()
    {
        new TursoParameter("@a", 1.5f).ToBindValue().Should().Be(1.5d);
        new TursoParameter("@a", 2.25d).ToBindValue().Should().Be(2.25d);
    }

    [TestMethod]
    public void ToBindValue_NullAndDbNull_BecomeNull()
    {
        new TursoParameter("@a", null).ToBindValue().Should().BeNull();
        new TursoParameter("@a", DBNull.Value).ToBindValue().Should().BeNull();
    }

    [TestMethod]
    public void ToBindValue_BlobStaysByteArray()
    {
        var bytes = new byte[] { 1, 2, 3 };
        new TursoParameter("@a", bytes).ToBindValue().Should().BeSameAs(bytes);
    }

    [TestMethod]
    public void ToBindValue_TextTypes_FormatInvariantly()
    {
        new TursoParameter("@a", "hello").ToBindValue().Should().Be("hello");
        new TursoParameter("@a", Guid.Parse("00000000-0000-0000-0000-0000000000ab")).ToBindValue()
            .Should().Be("00000000-0000-0000-0000-0000000000ab");

        var dto = new DateTimeOffset(2026, 6, 17, 8, 30, 0, TimeSpan.Zero);
        ((string)new TursoParameter("@a", dto).ToBindValue()!).Should().StartWith("2026-06-17 08:30:00");

        new TursoParameter("@a", 12.5m).ToBindValue().Should().Be("12.5");
    }

    [TestMethod]
    public void Direction_OnlyInputAllowed()
    {
        var p = new TursoParameter();
        p.Direction.Should().Be(ParameterDirection.Input);
        var act = () => p.Direction = ParameterDirection.Output;
        act.Should().Throw<ArgumentException>();
        p.Direction = ParameterDirection.Input; // no-op allowed
    }

    [TestMethod]
    public void Size_RejectsBelowMinusOne()
    {
        var p = new TursoParameter();
        var act = () => p.Size = -2;
        act.Should().Throw<ArgumentOutOfRangeException>();
        p.Size = -1; // sentinel allowed
        p.Size.Should().Be(-1);
    }

    [TestMethod]
    public void ResetDbType_ReturnsToString()
    {
        var p = new TursoParameter { DbType = DbType.Int64 };
        p.ResetDbType();
        p.DbType.Should().Be(DbType.String);
    }

    [TestMethod]
    public void Collection_AddWithValue_AndLookups()
    {
        var c = new TursoParameterCollection();
        var p = c.AddWithValue("@id", 5);
        c.Count.Should().Be(1);
        c.Contains("@id").Should().BeTrue();
        c.IndexOf("@id").Should().Be(0);
        c["@id"].Should().BeSameAs(p);
        ((TursoParameter)c[0]).Value.Should().Be(5);
    }

    [TestMethod]
    public void Collection_RemoveAndClear()
    {
        var c = new TursoParameterCollection();
        c.AddWithValue("@a", 1);
        c.AddWithValue("@b", 2);
        c.RemoveAt("@a");
        c.Count.Should().Be(1);
        c.Contains("@a").Should().BeFalse();
        c.Clear();
        c.Count.Should().Be(0);
    }

    [TestMethod]
    public void Collection_RemoveAt_UnknownName_Throws()
    {
        var c = new TursoParameterCollection();
        var act = () => c.RemoveAt("@missing");
        act.Should().Throw<ArgumentException>();
    }
}
