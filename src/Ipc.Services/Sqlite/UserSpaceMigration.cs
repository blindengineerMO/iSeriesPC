namespace Ipc.Services.Sqlite;

internal static class UserSpaceMigration
{
    internal const string Sql = """
        CREATE TABLE sys_user_spaces (
            space_lib TEXT NOT NULL,space_name TEXT NOT NULL,
            space_type TEXT NOT NULL DEFAULT '*USRSPC' CHECK(space_type='*USRSPC'),
            identity TEXT NOT NULL,
            initial_byte INTEGER NOT NULL CHECK(initial_byte BETWEEN 0 AND 255),
            auto_extend INTEGER NOT NULL CHECK(auto_extend IN (0,1)),
            transfer_size INTEGER NOT NULL CHECK(transfer_size BETWEEN 0 AND 32),
            aligned INTEGER NOT NULL CHECK(aligned IN (0,1)),
            domain TEXT NOT NULL CHECK(domain IN ('*USER','*SYSTEM')),
            data BLOB NOT NULL CHECK(typeof(data)='blob' AND length(data)>=1 AND
                length(data)<=CASE WHEN aligned=1 THEN 16773120 ELSE 16776704 END),
            PRIMARY KEY(space_lib,space_name),
            FOREIGN KEY(space_lib,space_name,space_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE CASCADE
        );
        """;
}
