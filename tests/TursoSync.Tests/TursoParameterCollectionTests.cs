using System.Data.Common;
using Turso;

namespace TursoSync.Tests;

/// <summary>
/// Covers the <see cref="TursoParameterCollection"/> surface that isn't exercised by the binding-focused
/// <see cref="TursoParameterTests"/> — the list-like CRUD, lookups, and the by-index / by-name accessors the
/// ADO.NET base class routes through. No native library required (pure managed collection logic).
/// </summary>
[TestClass]
public class TursoParameterCollectionTests
{
    private static TursoParameterCollection Collection(params TursoParameter[] parameters)
    {
        var collection = new TursoParameterCollection();
        foreach (var parameter in parameters)
        {
            collection.Add(parameter);
        }

        return collection;
    }

    [TestMethod]
    public void Add_NonTursoParameter_WrapsAsValue()
    {
        var collection = new TursoParameterCollection();
        var index = collection.Add(42);

        index.Should().Be(0);
        collection.Count.Should().Be(1);
        ((TursoParameter)collection[0]).Value.Should().Be(42);
    }

    [TestMethod]
    public void AddRange_AddsAll()
    {
        var collection = new TursoParameterCollection();
        collection.AddRange(new object[] { new TursoParameter("a", 1), new TursoParameter("b", 2) });

        collection.Count.Should().Be(2);
        collection.IndexOf("b").Should().Be(1);
    }

    [TestMethod]
    public void Insert_PutsParameterAtIndex()
    {
        var collection = Collection(new TursoParameter("a", 1), new TursoParameter("c", 3));
        collection.Insert(1, new TursoParameter("b", 2));

        collection.Count.Should().Be(3);
        collection.IndexOf("b").Should().Be(1);
        collection.IndexOf("c").Should().Be(2);
    }

    [TestMethod]
    public void Contains_ByReference_AndByValue()
    {
        var byRef = new TursoParameter("a", 1);
        var collection = Collection(byRef, new TursoParameter("b", 2));

        collection.Contains(byRef).Should().BeTrue();           // TursoParameter → reference identity
        collection.Contains(2).Should().BeTrue();               // raw value → value equality
        collection.Contains(99).Should().BeFalse();
        collection.Contains("b").Should().BeTrue();             // by name
        collection.Contains("missing").Should().BeFalse();
    }

    [TestMethod]
    public void IndexOf_ByValue_AndByName_AreCaseInsensitive()
    {
        var collection = Collection(new TursoParameter("Alpha", 1), new TursoParameter("Beta", 2));

        collection.IndexOf(2).Should().Be(1);                   // by value
        collection.IndexOf("alpha").Should().Be(0);             // by name, case-insensitive
        collection.IndexOf("nope").Should().Be(-1);
    }

    [TestMethod]
    public void CopyTo_CopiesIntoArray()
    {
        var collection = Collection(new TursoParameter("a", 1), new TursoParameter("b", 2));
        var target = new TursoParameter[2];

        collection.CopyTo(target, 0);

        target[0].ParameterName.Should().Be("a");
        target[1].ParameterName.Should().Be("b");
    }

    [TestMethod]
    public void GetEnumerator_YieldsAllParameters()
    {
        var collection = Collection(new TursoParameter("a", 1), new TursoParameter("b", 2));

        var names = new List<string>();
        foreach (TursoParameter parameter in collection)
        {
            names.Add(parameter.ParameterName);
        }

        names.Should().Equal("a", "b");
    }

    [TestMethod]
    public void Indexer_ByIndex_GetAndSet()
    {
        var collection = Collection(new TursoParameter("a", 1));

        collection[0] = new TursoParameter("z", 9);

        ((TursoParameter)collection[0]).ParameterName.Should().Be("z");
    }

    [TestMethod]
    public void Indexer_ByName_GetAndSet()
    {
        DbParameterCollection collection = Collection(new TursoParameter("a", 1));

        collection["a"].Value.Should().Be(1);

        collection["a"] = new TursoParameter("a", 5);
        collection["a"].Value.Should().Be(5);
    }

    [TestMethod]
    public void Indexer_ByName_SetWhenMissing_Adds()
    {
        DbParameterCollection collection = new TursoParameterCollection();

        collection["new"] = new TursoParameter("new", 7);

        collection.Count.Should().Be(1);
        collection["new"].Value.Should().Be(7);
    }

    [TestMethod]
    public void GetParameter_ByName_UnknownThrows()
    {
        var collection = Collection(new TursoParameter("a", 1));

        var act = () => _ = collection["missing"];
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Remove_ByValue_NotFoundThrows()
    {
        var collection = Collection(new TursoParameter("a", 1));

        var act = () => collection.Remove(new TursoParameter("b", 2));
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Remove_ByReference_Removes()
    {
        var target = new TursoParameter("a", 1);
        var collection = Collection(target, new TursoParameter("b", 2));

        collection.Remove(target);

        collection.Count.Should().Be(1);
        collection.IndexOf("a").Should().Be(-1);
    }

    [TestMethod]
    public void RemoveAt_ByIndex_Removes()
    {
        var collection = Collection(new TursoParameter("a", 1), new TursoParameter("b", 2));

        collection.RemoveAt(0);

        collection.Count.Should().Be(1);
        collection.IndexOf("b").Should().Be(0);
    }

    [TestMethod]
    public void SyncRoot_IsStable()
    {
        var collection = new TursoParameterCollection();
        collection.SyncRoot.Should().BeSameAs(collection.SyncRoot);
    }
}
