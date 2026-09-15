using Ipc.Core.Menu;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Menu;

public sealed class MenuStore
{
    private readonly SqliteObjectStore _objects;
    private readonly SqliteConnectionFactory _factory;

    public MenuStore(SqliteObjectStore objects, SqliteConnectionFactory factory)
    {
        _objects = objects;
        _factory = factory;
    }

    public void SeedDefaults()
    {
        foreach (var menu in SystemMenus.All())
        {
            var existing = TryGet(menu.Name, "QSYS");
            if (existing is null) Register(menu, owner: "QSYS");
            else if (SystemMenus.IsLegacyDefault(existing))
            {
                var descriptor = _objects.GetForAuthorization("QSYS", menu.Name, ObjectType.Menu)!;
                if (descriptor.Owner == "QSYS" && descriptor.Source is null && descriptor.ExtendedAttributes?.ContainsKey("ipc.signature") != true) Register(menu, owner: "QSYS");
            }
        }
    }

    public void Register(ApplicationMenu menu, string owner = "QSYS", string? source = null, bool replace = true)
    {
        MenuDefinition.Validate(menu);
        var library = menu.Library ?? "QSYS";
        var descriptor = new ObjectDescriptor
        {
            Key = new QualifiedName(library, menu.Name), ObjectType = ObjectType.Menu,
            Owner = owner, Description = menu.Title, Source = source, Attribute = "*SBSMENU",
        };
        descriptor.ValidateIdentity();
        var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
        if (_objects.GetForAuthorization(library, menu.Name, ObjectType.Menu) is null) authorization.RequireCreate(descriptor);
        else authorization.RequireObject(library, menu.Name, ObjectType.Menu, AuthorityBit.ObjectManagement);
        new Ipc.Services.Work.JobLockStore(_factory).RequireAccess(library, menu.Name, ObjectType.Menu, AuthorityBit.ObjectManagement);
        using var connection = _factory.Open();
        using var tx = connection.BeginTransaction(deferred: false);
        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = tx;
            exists.CommandText = "SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type='*MENU'";
            exists.Parameters.AddWithValue("$lib", library);
            exists.Parameters.AddWithValue("$name", menu.Name);
            if (Convert.ToInt64(exists.ExecuteScalar()) == 0) _objects.Create(descriptor, connection, tx);
            else
            {
                if (!replace) throw new CpfException("CPF7302", "Menu already exists; specify REPLACE(*YES).");
                authorization.RequireObject(library, menu.Name, ObjectType.Menu, AuthorityBit.ObjectManagement);
            }
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE sys_objects SET description=$title,source=$source,changed=$now,attrs=json_set(json_remove(coalesce(attrs,'{}'),'$."ipc.signature"'),'$."ipc.menu.options"',$policy)
                WHERE lib=$lib AND name=$name AND type='*MENU'
                """;
            update.Parameters.AddWithValue("$lib", library);
            update.Parameters.AddWithValue("$name", menu.Name);
            update.Parameters.AddWithValue("$title", menu.Title);
            update.Parameters.AddWithValue("$policy", System.Text.Json.JsonSerializer.Serialize(menu.Options.ToDictionary(o => o.Number, o => o.RequiredAuthority)));
            update.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            update.ExecuteNonQuery();
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO sys_menus (library, name, title)
                VALUES ($library, $name, $title)
                ON CONFLICT(library, name) DO UPDATE SET title = $title;
                """;
            upsert.Parameters.AddWithValue("$library", library);
            upsert.Parameters.AddWithValue("$name", menu.Name);
            upsert.Parameters.AddWithValue("$title", menu.Title);
            upsert.ExecuteNonQuery();
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM sys_menu_options WHERE library = $library AND menu = $name";
            clear.Parameters.AddWithValue("$library", library);
            clear.Parameters.AddWithValue("$name", menu.Name);
            clear.ExecuteNonQuery();
        }

        for (var i = 0; i < menu.Options.Count; i++)
        {
            var option = menu.Options[i];
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO sys_menu_options (library, menu, ordinal, number, text, target, kind)
                VALUES ($library, $menu, $ordinal, $number, $text, $target, $kind);
                """;
            insert.Parameters.AddWithValue("$library", library);
            insert.Parameters.AddWithValue("$menu", menu.Name);
            insert.Parameters.AddWithValue("$ordinal", i);
            insert.Parameters.AddWithValue("$number", option.Number);
            insert.Parameters.AddWithValue("$text", option.Text);
            insert.Parameters.AddWithValue("$target", option.Target);
            insert.Parameters.AddWithValue("$kind", option.Kind.ToString());
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public ApplicationMenu? TryGet(string name, string? library = null)
    {
        var qualified = Qualified(name, library);
        var resolved = qualified.Library ?? ResolveLibrary(qualified.Name);
        return resolved is null ? null : Load(resolved, qualified.Name);
    }

    public ApplicationMenu Get(string name, string? library = null) =>
        TryGet(name, library)
        ?? throw new CpfException("CPF9824", $"Menu {name} not found.");

    public ApplicationMenu Go(string name, string? library = null) => Get(name, library);

    public IReadOnlyList<ApplicationMenu> List()
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT library, name FROM sys_menus ORDER BY library, name";
        using var reader = cmd.ExecuteReader();
        var list = new List<ApplicationMenu>();
        while (reader.Read())
        {
            var loaded = Load(reader.GetString(0), reader.GetString(1));
            if (loaded is not null)
            {
                list.Add(loaded);
            }
        }

        return list;
    }

    private static (string? Library, string Name) Qualified(string name, string? library)
    {
        if (library is not null)
        {
            return (library, name);
        }

        var slash = name.IndexOf('/');
        if (slash > 0)
        {
            return (name[..slash], name[(slash + 1)..]);
        }

        return (null, name);
    }

    private string? ResolveLibrary(string name)
    {
        return new[] { "QSYS", "QGPL", "QUSRSYS" }.FirstOrDefault(library => Load(library, name) is not null);
    }

    private ApplicationMenu? Load(string library, string name)
    {
        if (_objects.Get(library, name, ObjectType.Menu) is null) return null;
        using var connection = _factory.Open();
        using var tx = connection.BeginTransaction();

        var descriptor = Ipc.Services.Security.ObjectSigningService.Read(connection, tx, library, name, ObjectType.Menu);
        using (var certificates = new Ipc.Services.Security.CertificateService(_factory))
            new Ipc.Services.Security.ObjectSigningService(_factory, new Ipc.Services.Security.ContentTrustService(_factory, certificates))
                .RequireExecutable(descriptor, connection, tx);

        string? title = null;
        using (var menuCmd = connection.CreateCommand())
        {
            menuCmd.Transaction = tx;
            menuCmd.CommandText = "SELECT title FROM sys_menus WHERE library = $library AND name = $name";
            menuCmd.Parameters.AddWithValue("$library", library);
            menuCmd.Parameters.AddWithValue("$name", name);
            title = menuCmd.ExecuteScalar() as string;
        }

        if (title is null)
        {
            return null;
        }

        var policy = new Dictionary<string, string>();
        if (descriptor.ExtendedAttributes?.TryGetValue("ipc.menu.options", out var encoded) == true)
        {
            try { policy = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(encoded) ?? throw new CpfException("IPC0135", "Invalid menu option policy."); }
            catch (System.Text.Json.JsonException) { throw new CpfException("IPC0135", "Invalid menu option policy."); }
        }
        var options = new List<MenuOption>();
        using (var optionCmd = connection.CreateCommand())
        {
            optionCmd.Transaction = tx;
            optionCmd.CommandText = """
                SELECT number, text, target, kind FROM sys_menu_options
                WHERE library = $library AND menu = $name ORDER BY ordinal;
                """;
            optionCmd.Parameters.AddWithValue("$library", library);
            optionCmd.Parameters.AddWithValue("$name", name);
            using var reader = optionCmd.ExecuteReader();
            while (reader.Read())
            {
                options.Add(new MenuOption
                {
                    Number = reader.GetString(0),
                    RequiredAuthority = policy.GetValueOrDefault(reader.GetString(0), "*NONE"),
                    Text = reader.GetString(1),
                    Target = reader.GetString(2),
                    Kind = Enum.TryParse<MenuOptionKind>(reader.GetString(3), out var kind)
                        ? kind
                        : throw new CpfException("IPC0135", "Invalid menu option kind."),
                });
            }
        }

        var loaded = new ApplicationMenu
        {
            Name = name,
            Library = library,
            Title = title ?? string.Empty,
            Options = options,
        };
        try { MenuDefinition.Validate(loaded); } catch (ArgumentException) { throw new CpfException("IPC0135", "Invalid stored menu definition."); }
        return loaded;
    }
}
