namespace Ipc.Core.Catalog;

public static class SystemCatalog
{
    public const int SchemaVersion = 23;

    public const string CreateMigrationHistoryTable = """
        CREATE TABLE IF NOT EXISTS sys_migrations (
            version INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            checksum TEXT NOT NULL,
            applied_at TEXT NOT NULL,
            baseline INTEGER NOT NULL DEFAULT 0 CHECK (baseline IN (0, 1))
        );
        """;

    public const string CreateObjectsTable = """
        CREATE TABLE IF NOT EXISTS sys_objects (
            lib       TEXT NOT NULL,
            name      TEXT NOT NULL,
            type      TEXT NOT NULL,
            owner     TEXT NOT NULL,
            created   TEXT NOT NULL,
            changed   TEXT NOT NULL,
            description TEXT NULL,
            ccsid     INTEGER NOT NULL DEFAULT 37,
            attribute TEXT NULL,
            format    TEXT NULL,
            public_authority INTEGER NOT NULL DEFAULT 0,
            source    TEXT NULL,
            attrs     TEXT NULL,
            PRIMARY KEY (lib, name, type)
        );
        """;

    public const string CreateSystemValuesTable = """
        CREATE TABLE IF NOT EXISTS sys_sysvals (
            name  TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    public const string CreateLibrariesTable = """
        CREATE TABLE IF NOT EXISTS sys_libraries (
            name         TEXT PRIMARY KEY,
            type         TEXT NOT NULL DEFAULT '*PROD',
            created      TEXT NOT NULL,
            description  TEXT NULL
        );
        """;

    public const string CreateLibraryListTable = """
        CREATE TABLE IF NOT EXISTS sys_libl (
            position  INTEGER PRIMARY KEY,
            library   TEXT NOT NULL,
            sysmgt    INTEGER NOT NULL DEFAULT 0
        );
        """;

    public const string CreateJobLogTable = """
        CREATE TABLE IF NOT EXISTS sys_hst (
            seq        INTEGER PRIMARY KEY AUTOINCREMENT,
            at         TEXT NOT NULL,
            job        TEXT NULL,
            message_id TEXT NULL,
            message    TEXT NOT NULL
        );
        """;

    public const string CreateSchemaVersionTable = """
        CREATE TABLE IF NOT EXISTS sys_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    public const string CreateUserProfilesTable = """
        CREATE TABLE IF NOT EXISTS sys_profiles (
            name            TEXT PRIMARY KEY,
            user_class      TEXT NOT NULL DEFAULT '*USER',
            special_auth    INTEGER NOT NULL DEFAULT 0,
            group_profile   TEXT NULL,
            owner           TEXT NULL,
            description     TEXT NULL,
            status          TEXT NOT NULL DEFAULT 'Enabled',
            initial_menu    TEXT NULL,
            initial_program TEXT NULL,
            initial_curlib  TEXT NULL,
            password_changed TEXT NULL,
            password_expires TEXT NULL,
            days_used       INTEGER NOT NULL DEFAULT 0,
            signon_attempts INTEGER NOT NULL DEFAULT 0,
            password_hash   TEXT NULL,
            hash_iterations INTEGER NOT NULL DEFAULT 210000,
            ccsid           INTEGER NOT NULL DEFAULT 37,
            country_id      TEXT NOT NULL DEFAULT 'US',
            locale          TEXT NOT NULL DEFAULT 'US'
        );
        """;

    public const string CreateAuthoritiesTable = """
        CREATE TABLE IF NOT EXISTS sys_authorities (
            lib       TEXT NOT NULL,
            name      TEXT NOT NULL,
            type      TEXT NOT NULL,
            holder    TEXT NOT NULL,
            is_authl  INTEGER NOT NULL DEFAULT 0,
            bits      INTEGER NOT NULL,
            PRIMARY KEY (lib, name, type, holder, is_authl)
        );
        """;

    public const string CreateAuthLMembersTable = """
        CREATE TABLE IF NOT EXISTS sys_authl_members (
            authl  TEXT NOT NULL,
            holder TEXT NOT NULL,
            bits   INTEGER NOT NULL,
            PRIMARY KEY (authl, holder)
        );
        """;

    public const string CreateSubsystemsTable = """
        CREATE TABLE IF NOT EXISTS sys_subsystems (
            name         TEXT PRIMARY KEY,
            description  TEXT NULL,
            status       TEXT NOT NULL DEFAULT 'Stopped',
            max_active   INTEGER NOT NULL DEFAULT 1
        );
        """;

    public const string CreateJobQueuesTable = """
        CREATE TABLE IF NOT EXISTS sys_jobqs (
            name         TEXT PRIMARY KEY,
            library      TEXT NOT NULL DEFAULT 'QGPL',
            description  TEXT NULL
        );
        """;

    public const string CreateJobsTable = """
        CREATE TABLE IF NOT EXISTS sys_jobs (
            number         INTEGER NOT NULL,
            name           TEXT NOT NULL,
            user           TEXT NOT NULL,
            type           TEXT NOT NULL,
            status         TEXT NOT NULL,
            subsystem      TEXT NULL,
            jobq           TEXT NULL,
            priority       INTEGER NOT NULL DEFAULT 9,
            user_profile   TEXT NULL,
            current_lib    TEXT NULL,
            libl           TEXT NULL,
            ccsid          INTEGER NOT NULL DEFAULT 37,
            submitter_name TEXT NULL,
            submitter_user TEXT NULL,
            submitted_at   TEXT NULL,
            started_at     TEXT NULL,
            completed_at   TEXT NULL,
            description    TEXT NULL,
            routing_data   TEXT NULL,
            completion     TEXT NULL,
            completion_msg TEXT NULL,
            PRIMARY KEY (number, name, user)
        );
        """;

    public const string CreateJobLogEntriesTable = """
        CREATE TABLE IF NOT EXISTS sys_joblog (
            job_number   INTEGER NOT NULL,
            job_name     TEXT NOT NULL,
            job_user     TEXT NOT NULL,
            seq          INTEGER NOT NULL,
            time         TEXT NOT NULL,
            message_id   TEXT NULL,
            severity     INTEGER NOT NULL DEFAULT 0,
            message_type TEXT NOT NULL,
            text         TEXT NULL,
            PRIMARY KEY (job_number, job_name, job_user, seq)
        );
        """;

    public const string CreateRoutingTable = """
        CREATE TABLE IF NOT EXISTS sys_routing (
            subsystem    TEXT NOT NULL,
            seq          INTEGER NOT NULL,
            compare_value TEXT NOT NULL DEFAULT '*ANY',
            program      TEXT NOT NULL,
            user_class   TEXT NOT NULL DEFAULT '*USER',
            PRIMARY KEY (subsystem, seq)
        );
        """;

    public const string CreateMenusTable = """
        CREATE TABLE IF NOT EXISTS sys_menus (
            library  TEXT NOT NULL DEFAULT 'QSYS',
            name     TEXT NOT NULL,
            title    TEXT NULL,
            PRIMARY KEY (library, name)
        );
        """;

    public const string CreateMenuOptionsTable = """
        CREATE TABLE IF NOT EXISTS sys_menu_options (
            library TEXT NOT NULL,
            menu    TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            number  TEXT NOT NULL,
            text    TEXT NOT NULL,
            target  TEXT NOT NULL,
            kind    TEXT NOT NULL DEFAULT 'SubMenu',
            PRIMARY KEY (library, menu, ordinal)
        );
        """;

    public const string CreateFileDefinitionsTable = """
        CREATE TABLE IF NOT EXISTS sys_file_defs (
            lib  TEXT NOT NULL,
            name TEXT NOT NULL,
            type TEXT NOT NULL,
            def  TEXT NOT NULL,
            PRIMARY KEY (lib, name, type)
        );
        """;

    public const string CreateFileMembersTable = """
        CREATE TABLE IF NOT EXISTS sys_file_members (
            lib     TEXT NOT NULL,
            name    TEXT NOT NULL,
            type    TEXT NOT NULL,
            mbr     TEXT NOT NULL,
            created TEXT NOT NULL,
            PRIMARY KEY (lib, name, type, mbr)
        );
        """;

    public static readonly string[] All =
    {
        CreateSchemaVersionTable,
        CreateObjectsTable,
        CreateSystemValuesTable,
        CreateLibrariesTable,
        CreateLibraryListTable,
        CreateJobLogTable,
        CreateUserProfilesTable,
        CreateAuthoritiesTable,
        CreateAuthLMembersTable,
        CreateSubsystemsTable,
        CreateJobQueuesTable,
        CreateJobsTable,
        CreateRoutingTable,
        CreateJobLogEntriesTable,
        CreateMenusTable,
        CreateMenuOptionsTable,
        CreateFileDefinitionsTable,
        CreateFileMembersTable,
        CreateMigrationHistoryTable,
    };
}
