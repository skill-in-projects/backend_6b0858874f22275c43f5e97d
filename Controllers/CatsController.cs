using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Backend.Controllers;

/// <summary>Saves the FOMO level of each cat in the database, so the levels stay after a reload.</summary>
[ApiController]
[Route("api/cats")]
public class CatsController : ControllerBase
{
    private static readonly (string Name, int Level)[] StartCats =
    {
        ("Miso", 82),
        ("Biscuit", 35),
        ("Luna", 61),
        ("Pepper", 12),
    };

    private static bool _tableReady;

    private readonly string _connectionString;

    public CatsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
    }

    /// <summary>GET /api/cats - The cats with their saved levels.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT name, level FROM public.cat_fomo_cats ORDER BY position", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var cats = new List<object>();
        while (await reader.ReadAsync())
        {
            cats.Add(new { name = reader.GetString(0), level = reader.GetInt32(1) });
        }
        return Ok(cats);
    }

    /// <summary>PUT /api/cats/{name}/level - Save a new FOMO level (0 to 100) for one cat.</summary>
    [HttpPut("{name}/level")]
    public async Task<IActionResult> SetLevel(string name, [FromBody] LevelRequest request)
    {
        if (request.Level < 0 || request.Level > 100)
            return BadRequest(new { error = "The level must be between 0 and 100." });

        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE public.cat_fomo_cats SET level = @level WHERE name = @name RETURNING level", conn);
        cmd.Parameters.AddWithValue("level", request.Level);
        cmd.Parameters.AddWithValue("name", name);
        var saved = await cmd.ExecuteScalarAsync();
        if (saved == null)
            return NotFound(new { error = "No cat with that name." });

        return Ok(new { name, level = request.Level });
    }

    public record LevelRequest(int Level);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync();
            if (!_tableReady)
                await CreateTableAsync(conn);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    // Creates the table and the four sample cats the first time, then leaves them alone.
    private static async Task CreateTableAsync(NpgsqlConnection conn)
    {
        await using (var create = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS public.cat_fomo_cats (name text PRIMARY KEY, level integer NOT NULL CHECK (level BETWEEN 0 AND 100), position integer NOT NULL)", conn))
        {
            await create.ExecuteNonQueryAsync();
        }

        for (var i = 0; i < StartCats.Length; i++)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO public.cat_fomo_cats (name, level, position) VALUES (@name, @level, @position) ON CONFLICT (name) DO NOTHING", conn);
            insert.Parameters.AddWithValue("name", StartCats[i].Name);
            insert.Parameters.AddWithValue("level", StartCats[i].Level);
            insert.Parameters.AddWithValue("position", i);
            await insert.ExecuteNonQueryAsync();
        }

        _tableReady = true;
    }
}
