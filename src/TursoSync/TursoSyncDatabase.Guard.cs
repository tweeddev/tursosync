namespace Turso.Sync;

public sealed partial class TursoSyncDatabase
{
    /// <summary>
    /// One schema-guard window around a state-rewriting sync operation: the pre-operation user-table list
    /// (+ optionally a file backup), verified against the post-operation list. See
    /// <see cref="TursoSchemaGuardMode"/>. Null scope = guard inactive (Off, local-only, or nothing on disk
    /// to protect).
    /// </summary>
    private sealed class GuardScope
    {
        private readonly List<string> _before;
        private readonly string _mainPath;
        // A pre-operation copy of the replica files: kept for DetectAndBackup; for Detect (create) it is
        // provisional — promoted to a kept backup when a drop is detected, deleted otherwise. Null when no
        // copy was taken (Detect around a pull: the files are live-open, the table list is the guard).
        private readonly string? _copyDir;
        private readonly bool _copyIsProvisional;

        private GuardScope(List<string> before, string mainPath, string? copyDir, bool copyIsProvisional)
        {
            _before = before;
            _mainPath = mainPath;
            _copyDir = copyDir;
            _copyIsProvisional = copyIsProvisional;
        }

        /// <summary>Guard window for create/open of an existing replica bound to a remote.</summary>
        internal static GuardScope? BeforeCreate(TursoSyncConfig config)
        {
            if (config.SchemaGuard == TursoSchemaGuardMode.Off
                || string.IsNullOrEmpty(config.RemoteUrl)
                || !File.Exists(config.Path))
            {
                return null;
            }

            // The copy sits beside the database (same volume → the promote rename below cannot fail on a
            // cross-volume move). Provisional for Detect; already the kept backup for DetectAndBackup.
            var keep = config.SchemaGuard == TursoSchemaGuardMode.DetectAndBackup;
            var copyDir = TursoSchemaGuard.BackupDirFor(config.Path, DateTime.UtcNow) + (keep ? "" : "-pending");
            TursoSchemaGuard.CopyReplicaFiles(config.Path, copyDir);
            var before = TursoSchemaGuard.UserTablesOfCopy(Path.Combine(copyDir, Path.GetFileName(config.Path)));
            return new GuardScope(before, config.Path, copyDir, copyIsProvisional: !keep);
        }

        /// <summary>Guard window for applying a pulled change-set on <paramref name="database"/>.</summary>
        internal static GuardScope? BeforeApply(TursoSyncDatabase database)
        {
            var config = database._config;
            if (config is null
                || config.SchemaGuard == TursoSchemaGuardMode.Off
                || string.IsNullOrEmpty(config.RemoteUrl))
            {
                return null;
            }

            List<string> before;
            using (var connection = TursoRawConnection.Open(database))
            {
                before = TursoSchemaGuard.UserTables(connection);
            }

            string? copyDir = null;
            if (config.SchemaGuard == TursoSchemaGuardMode.DetectAndBackup)
            {
                copyDir = TursoSchemaGuard.BackupDirFor(config.Path, DateTime.UtcNow);
                TursoSchemaGuard.CopyReplicaFiles(config.Path, copyDir);
            }

            return new GuardScope(before, config.Path, copyDir, copyIsProvisional: false);
        }

        /// <summary>Compare against the post-operation table list; throw on a drop, clean up otherwise.</summary>
        internal void VerifyAfter(TursoSyncDatabase database, string operation)
        {
            List<string> after;
            using (var connection = TursoRawConnection.Open(database))
            {
                after = TursoSchemaGuard.UserTables(connection);
            }

            var dropped = TursoSchemaGuard.Dropped(_before, after);
            if (dropped.Count == 0)
            {
                if (_copyIsProvisional && _copyDir is not null)
                {
                    try { Directory.Delete(_copyDir, recursive: true); } catch { /* best-effort cleanup */ }
                }

                return;
            }

            var backupDir = _copyDir;
            if (_copyIsProvisional && backupDir is not null)
            {
                // Promote the provisional copy to a kept backup — it is now the only pre-drop image.
                var kept = backupDir[..^"-pending".Length];
                try
                {
                    Directory.Move(backupDir, kept);
                    backupDir = kept;
                }
                catch
                {
                    // Keep the pending dir under its provisional name rather than lose the image.
                }
            }

            throw new TursoSchemaGuardException(operation, dropped, backupDir);
        }
    }
}
