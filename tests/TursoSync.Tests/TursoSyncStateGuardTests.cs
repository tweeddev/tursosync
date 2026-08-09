using Turso.Sync;

namespace TursoSync.Tests;

/// <summary>
/// The pre-open sync-state guard: foreign or unusable on-disk sync state must surface as a typed,
/// catchable <see cref="TursoSyncStateException"/> BEFORE the native engine touches the files — the
/// engine's own response can be a Rust panic that aborts the whole process (the 2026-08-08 Tweed
/// incident: an app bundling a different engine vintage rewrote <c>-info</c>; every subsequent launch
/// died in <c>turso_sync_operation_resume</c> → <c>abort()</c> with no managed frame able to catch it).
/// </summary>
[TestClass]
public class TursoSyncStateGuardTests
{
    // The -info shape from the actual incident: parseable JSON, fields a different engine vintage wrote.
    // The metadata version is what the guard keys on — here it claims a version this engine doesn't speak.
    private const string ForeignVersionInfo =
        """{"version":"v2","client_unique_id":"tursosync-x","synced_revision":null}""";

    [TestMethod]
    public void Create_UnparseableInfo_ThrowsTypedException()
    {
        var db = TempDb();
        try
        {
            File.WriteAllText(db + "-info", "{not json at all");
            var act = () => TursoSyncDatabase.Create(new TursoSyncConfig { Path = db });
            act.Should().Throw<TursoSyncStateException>()
                .Which.StatePath.Should().Be(db + "-info");
        }
        finally
        {
            Cleanup(db);
        }
    }

    [TestMethod]
    public void Create_ForeignMetadataVersion_ThrowsTypedException()
    {
        var db = TempDb();
        try
        {
            File.WriteAllText(db + "-info", ForeignVersionInfo);
            var act = () => TursoSyncDatabase.Create(new TursoSyncConfig { Path = db });
            act.Should().Throw<TursoSyncStateException>()
                .WithMessage("*version 'v2'*");
        }
        finally
        {
            Cleanup(db);
        }
    }

    [TestMethod]
    public void Create_TypedException_IsCatchableAsTursoException()
    {
        var db = TempDb();
        try
        {
            File.WriteAllText(db + "-info", "{not json at all");
            var act = () => TursoSyncDatabase.Create(new TursoSyncConfig { Path = db });
            act.Should().Throw<TursoException>(); // existing catch sites keep working
        }
        finally
        {
            Cleanup(db);
        }
    }

    [TestMethod]
    public void Create_EmptyInfo_IsTreatedAsFresh()
    {
        RequireNative();
        var db = TempDb();
        try
        {
            // The engine's FullRead contract treats a missing file as empty, so an empty file is
            // "no state yet" — the open must proceed, not refuse.
            File.WriteAllText(db + "-info", "");
            using var sync = TursoSyncDatabase.Create(new TursoSyncConfig { Path = db });
        }
        finally
        {
            Cleanup(db);
        }
    }

    [TestMethod]
    public void Create_StampsEngineVersion_AndReopens()
    {
        RequireNative();
        var db = TempDb();
        try
        {
            using (TursoSyncDatabase.Create(new TursoSyncConfig { Path = db }))
            {
            }

            var stamp = db + TursoSyncStateGuard.StampSuffix;
            File.Exists(stamp).Should().BeTrue("a successful open records which engine owns the state");
            File.ReadAllText(stamp).Should().Contain(TursoSyncStateGuard.EngineVersion);

            // Same engine re-opens its own state freely.
            using (TursoSyncDatabase.Create(new TursoSyncConfig { Path = db }))
            {
            }
        }
        finally
        {
            Cleanup(db);
        }
    }

    [TestMethod]
    public void Create_StateStampedByNewerEngine_ThrowsTypedException()
    {
        RequireNative();
        var db = TempDb();
        try
        {
            using (TursoSyncDatabase.Create(new TursoSyncConfig { Path = db }))
            {
            }

            File.WriteAllText(db + TursoSyncStateGuard.StampSuffix, """{"Engine":"999.0.0"}""");
            var act = () => TursoSyncDatabase.Create(new TursoSyncConfig { Path = db });
            act.Should().Throw<TursoSyncStateException>()
                .WithMessage("*newer than the engine now opening it*");
        }
        finally
        {
            Cleanup(db);
        }
    }

    [TestMethod]
    public void Create_StateStampedByOlderEngine_Opens()
    {
        RequireNative();
        var db = TempDb();
        try
        {
            using (TursoSyncDatabase.Create(new TursoSyncConfig { Path = db }))
            {
            }

            // Newer-on-older is the allowed direction: engines migrate their own state forward.
            File.WriteAllText(db + TursoSyncStateGuard.StampSuffix, """{"Engine":"0.0.1"}""");
            using (TursoSyncDatabase.Create(new TursoSyncConfig { Path = db }))
            {
            }

            // …and the stamp is refreshed to the current engine.
            File.ReadAllText(db + TursoSyncStateGuard.StampSuffix)
                .Should().Contain(TursoSyncStateGuard.EngineVersion);
        }
        finally
        {
            Cleanup(db);
        }
    }

    [TestMethod]
    public void Create_CorruptStamp_IsIgnoredAndRegenerated()
    {
        RequireNative();
        var db = TempDb();
        try
        {
            using (TursoSyncDatabase.Create(new TursoSyncConfig { Path = db }))
            {
            }

            File.WriteAllText(db + TursoSyncStateGuard.StampSuffix, "garbage");
            using (TursoSyncDatabase.Create(new TursoSyncConfig { Path = db }))
            {
            }

            File.ReadAllText(db + TursoSyncStateGuard.StampSuffix)
                .Should().Contain(TursoSyncStateGuard.EngineVersion);
        }
        finally
        {
            Cleanup(db);
        }
    }

    [TestMethod]
    public void Create_EscapeHatch_SkipsValidation()
    {
        RequireNative();
        var db = TempDb();
        try
        {
            using (TursoSyncDatabase.Create(new TursoSyncConfig { Path = db }))
            {
            }

            File.WriteAllText(db + TursoSyncStateGuard.StampSuffix, """{"Engine":"999.0.0"}""");
            Environment.SetEnvironmentVariable("TURSOSYNC_IGNORE_STATE_GUARD", "1");
            try
            {
                using var sync = TursoSyncDatabase.Create(new TursoSyncConfig { Path = db });
            }
            finally
            {
                Environment.SetEnvironmentVariable("TURSOSYNC_IGNORE_STATE_GUARD", null);
            }
        }
        finally
        {
            Cleanup(db);
        }
    }

    private static void RequireNative()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
        }
    }

    private static string TempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tursosync-guard-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "store.db");
    }

    private static void Cleanup(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
