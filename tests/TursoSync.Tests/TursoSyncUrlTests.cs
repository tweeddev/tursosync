using Turso.Sync;

namespace TursoSync.Tests;

/// <summary>
/// Pure coverage for the remote-URL helpers that shape every sync request: scheme normalization, base+path
/// joining, and the namespace-prefixed Host header. Native-free, so they always run.
/// </summary>
[TestClass]
public class TursoSyncUrlTests
{
    [TestMethod]
    public void NormalizeUrl_RewritesLibsqlSchemeToHttps()
    {
        TursoSyncDatabase.NormalizeUrl("libsql://db.turso.io").Should().Be("https://db.turso.io");
        TursoSyncDatabase.NormalizeUrl("LIBSQL://db.turso.io").Should().Be("https://db.turso.io");
    }

    [TestMethod]
    public void NormalizeUrl_LeavesHttpAndHttpsUntouched()
    {
        TursoSyncDatabase.NormalizeUrl("https://db.turso.io").Should().Be("https://db.turso.io");
        TursoSyncDatabase.NormalizeUrl("http://localhost:8080").Should().Be("http://localhost:8080");
    }

    [TestMethod]
    public void JoinUrl_AddsLeadingSlashAndTrimsTrailing()
    {
        TursoSyncDatabase.JoinUrl("https://db.turso.io", "info").Should().Be("https://db.turso.io/info");
        TursoSyncDatabase.JoinUrl("https://db.turso.io/", "/info").Should().Be("https://db.turso.io/info");
        TursoSyncDatabase.JoinUrl("https://db.turso.io", "/pull-updates").Should().Be("https://db.turso.io/pull-updates");
    }

    [TestMethod]
    public void BuildHost_WithoutNamespace_UsesUriHost()
    {
        TursoSyncDatabase.BuildHost("https://db.turso.io", ns: null).Should().Be("db.turso.io");
        TursoSyncDatabase.BuildHost("https://db.turso.io:443", ns: "").Should().Be("db.turso.io");
    }

    [TestMethod]
    public void BuildHost_WithNamespace_PrefixesHost()
    {
        TursoSyncDatabase.BuildHost("https://turso.io", ns: "tenant").Should().Be("tenant.turso.io");
    }

    [TestMethod]
    public void BuildHost_InvalidOrEmptyBaseUrl_IsNull()
    {
        TursoSyncDatabase.BuildHost("", ns: "tenant").Should().BeNull();
        TursoSyncDatabase.BuildHost("not-a-url", ns: null).Should().BeNull();
    }
}
