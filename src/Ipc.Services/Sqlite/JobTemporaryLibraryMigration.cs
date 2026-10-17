namespace Ipc.Services.Sqlite;

internal static class JobTemporaryLibraryMigration
{
    internal const string Sql = """
        CREATE TABLE sys_job_temp_libraries (
            job_number INTEGER PRIMARY KEY REFERENCES sys_jobs(number) ON DELETE RESTRICT,
            library TEXT NOT NULL UNIQUE CHECK(length(library)=10 AND substr(library,1,2)='@T'),
            container_lib TEXT NOT NULL DEFAULT 'QSYS' CHECK(container_lib='QSYS'),
            container_type TEXT NOT NULL DEFAULT '*LIB' CHECK(container_type='*LIB'),
            FOREIGN KEY(container_lib,library,container_type) REFERENCES sys_objects(lib,name,type) ON DELETE RESTRICT ON UPDATE RESTRICT
        );
        """;
}
