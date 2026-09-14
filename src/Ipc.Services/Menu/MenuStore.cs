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
        Register(SystemMenus.Main(), owner: "QSYS");
        Register(SystemMenus.Major(), owner: "QSYS");
    }

    public void Register(ApplicationMenu menu, string owner = "QSYS")
    {
        var library = menu.Library ?? "QSYS";
        if (!_objects.Exists(library, menu.Name, "*MENU"))
        {
            _objects.Create(new ObjectDescriptor
            {
                Key = new QualifiedName(library, menu.Name),
                ObjectType = "*MENU",
                Owner = owner,
                Description = menu.Title,
                Attribute = "*SBSMENU",
            });
        }

        using var connection = _factory.Open();
        using var tx = connection.BeginTransaction();

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
        if (Load("QSYS", name) is not null)
        {
            return "QSYS";
        }

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT library FROM sys_menus WHERE name = $name ORDER BY library LIMIT 1";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() as string;
    }

    private ApplicationMenu? Load(string library, string name)
    {
        using var connection = _factory.Open();
        using var tx = connection.BeginTransaction();

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
                    Text = reader.GetString(1),
                    Target = reader.GetString(2),
                    Kind = Enum.TryParse<MenuOptionKind>(reader.GetString(3), out var kind)
                        ? kind
                        : MenuOptionKind.SubMenu,
                });
            }
        }

        return new ApplicationMenu
        {
            Name = name,
            Library = library,
            Title = title ?? string.Empty,
            Options = options,
        };
    }
}