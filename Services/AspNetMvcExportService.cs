using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DoAnLapTrinhWeb.Models.Designer;

namespace DoAnLapTrinhWeb.Services;

public sealed class AspNetMvcExportService
{
    public byte[] CreateZip(DatabaseSchema inputSchema, string? seedSql = null)
    {
        var schema = NormalizeSchema(inputSchema);
        var entities = BuildEntityInfos(schema);
        var projectName = ToSafeIdentifier(schema.ProjectName, "GeneratedMvcApp");

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, $"{projectName}/{projectName}.csproj", GenerateProjectFile());
            AddEntry(archive, $"{projectName}/Program.cs", GenerateProgram(projectName));
            AddEntry(archive, $"{projectName}/appsettings.json", GenerateAppSettings(projectName, schema.ConnectionString));
            AddEntry(archive, $"{projectName}/appsettings.Development.json", GenerateDevelopmentAppSettings());
            AddEntry(archive, $"{projectName}/Dockerfile", GenerateDockerfile(projectName));
            AddEntry(archive, $"{projectName}/docker-compose.yml", GenerateDockerCompose(projectName));
            AddEntry(archive, $"{projectName}/README.md", GenerateReadme(projectName, schema));
            AddEntry(archive, $"{projectName}/Data/AppDbContext.cs", GenerateDbContext(projectName, entities));
            AddEntry(archive, $"{projectName}/Data/DatabaseBootstrapper.cs", GenerateDatabaseBootstrapper(projectName, !string.IsNullOrWhiteSpace(seedSql)));
            AddEntry(archive, $"{projectName}/Migrations/InitialCreate.sql", GenerateSqlScript(schema));

            if (!string.IsNullOrWhiteSpace(seedSql))
            {
                AddEntry(archive, $"{projectName}/Migrations/SeedData.sql", seedSql);
            }
            AddEntry(archive, $"{projectName}/Controllers/HomeController.cs", GenerateHomeController(projectName));
            AddEntry(archive, $"{projectName}/Views/_ViewImports.cshtml", $"@using {projectName}\n@using {projectName}.Models\n@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers\n");
            AddEntry(archive, $"{projectName}/Views/_ViewStart.cshtml", "@{\n    Layout = \"_Layout\";\n}\n");
            AddEntry(archive, $"{projectName}/Views/Shared/_Layout.cshtml", GenerateLayout(projectName));
            AddEntry(archive, $"{projectName}/Views/Home/Index.cshtml", GenerateHomeView(projectName, schema));
            AddEntry(archive, $"{projectName}/wwwroot/css/site.css", GenerateGeneratedSiteCss());

            foreach (var entity in entities)
            {
                AddEntry(archive, $"{projectName}/Models/{entity.ClassName}.cs", GenerateModel(projectName, entity));
            }
        }

        return stream.ToArray();
    }

    private static DatabaseSchema NormalizeSchema(DatabaseSchema inputSchema)
    {
        var schema = new DatabaseSchema
        {
            ProjectName = ToSafeIdentifier(inputSchema.ProjectName, "GeneratedMvcApp"),
            ConnectionString = inputSchema.ConnectionString,
            Tables = inputSchema.Tables
                .Where(table => !string.IsNullOrWhiteSpace(table.Name))
                .Select(table => new SchemaTable
                {
                    Id = string.IsNullOrWhiteSpace(table.Id) ? Guid.NewGuid().ToString("N") : table.Id,
                    Name = table.Name.Trim(),
                    LastValidName = string.IsNullOrWhiteSpace(table.LastValidName) ? table.Name.Trim() : table.LastValidName.Trim(),
                    X = table.X,
                    Y = table.Y,
                    Columns = table.Columns
                        .Where(column => !string.IsNullOrWhiteSpace(column.Name))
                        .Select((column, index) => new SchemaColumn
                        {
                            Id = string.IsNullOrWhiteSpace(column.Id) ? Guid.NewGuid().ToString("N") : column.Id,
                            Name = column.Name.Trim(),
                            SqlType = NormalizeSqlServerType(column.SqlType),
                            IsPrimaryKey = column.IsPrimaryKey,
                            IsNullable = column.IsPrimaryKey ? false : column.IsNullable,
                            IsUnique = column.IsUnique,
                            ForeignKeyTable = string.IsNullOrWhiteSpace(column.ForeignKeyTable) ? null : column.ForeignKeyTable.Trim(),
                            ForeignKeyColumn = string.IsNullOrWhiteSpace(column.ForeignKeyColumn) ? null : column.ForeignKeyColumn.Trim(),
                            Order = column.Order == 0 ? index : column.Order
                        })
                        .ToList()
                })
                .ToList()
        };

        return schema;
    }

    private static List<EntityInfo> BuildEntityInfos(DatabaseSchema schema)
    {
        var entities = new List<EntityInfo>();
        var usedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedDbSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in schema.Tables)
        {
            var className = MakeUnique(ToPascalCase(Singularize(table.Name)), usedClasses);
            var dbSetName = MakeUnique(ToPascalCase(Pluralize(table.Name)), usedDbSets);
            var entity = new EntityInfo(table, className, dbSetName);
            var usedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var column in table.Columns.OrderBy(column => column.Order))
            {
                entity.PropertyByColumnId[column.Id] = MakeUnique(ToPascalCase(column.Name), usedProperties);
            }

            entities.Add(entity);
        }

        return entities;
    }

    private static string GenerateProjectFile()
    {
        return """
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="10.0.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
""";
    }

    private static string GenerateProgram(string projectName)
    {
        return $$"""
using Microsoft.EntityFrameworkCore;
using {{projectName}}.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

await DatabaseBootstrapper.ApplyAsync(app);

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
""";
    }

    private static string GenerateAppSettings(string projectName, string connectionString)
    {
        var cs = string.IsNullOrWhiteSpace(connectionString)
            ? $"Server=103.72.56.55,2025;Database={projectName}Db;User Id=sa;Password=!Pass123;TrustServerCertificate=True;MultipleActiveResultSets=true"
            : connectionString;

        var escapedCs = cs.Replace("\\", "\\\\").Replace("\"", "\\\"");

        return $$"""
{
  "ConnectionStrings": {
    "DefaultConnection": "{{escapedCs}}"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
""";
    }

    private static string GenerateDevelopmentAppSettings()
    {
        return """
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
""";
    }

    private static string GenerateDockerfile(string projectName)
    {
        return $$"""
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["{{projectName}}.csproj", "./"]
RUN dotnet restore "{{projectName}}.csproj"
COPY . .
RUN dotnet publish "{{projectName}}.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "{{projectName}}.dll"]
""";
    }

    private static string GenerateDockerCompose(string projectName)
    {
        return $$"""
services:
  app:
    build: .
    container_name: {{projectName.ToLowerInvariant()}}-app
    ports:
      - "5157:8080"
    environment:
      ASPNETCORE_URLS: http://+:8080
      ASPNETCORE_ENVIRONMENT: Production
      AUTO_APPLY_DATABASE: "true"
      ConnectionStrings__DefaultConnection: "Server=db,1433;Database={{projectName}}Db;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True;MultipleActiveResultSets=true"
    depends_on:
      - db

  db:
    image: mcr.microsoft.com/mssql/server:2022-latest
    container_name: {{projectName.ToLowerInvariant()}}-sqlserver
    ports:
      - "14333:1433"
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "YourStrong!Passw0rd"
    volumes:
      - sqlserver_data:/var/opt/mssql

volumes:
  sqlserver_data:
""";
    }

    private static string GenerateReadme(string projectName, DatabaseSchema schema)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {projectName}");
        builder.AppendLine();
        builder.AppendLine("Generated ASP.NET Core MVC + SQL Server project from the database designer.");
        builder.AppendLine();
        builder.AppendLine("## Run with Docker");
        builder.AppendLine();
        builder.AppendLine("```bash");
        builder.AppendLine("docker compose up -d --build");
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("Open `http://localhost:5157` after the containers start.");
        builder.AppendLine();
        builder.AppendLine("## Database Login");
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine("| Host | localhost |");
        builder.AppendLine("| Port | 14333 |");
        builder.AppendLine($"| Database | {projectName}Db |");
        builder.AppendLine("| User | sa |");
        builder.AppendLine("| Password | YourStrong!Passw0rd |");
        builder.AppendLine("| Docker host | db:1433 |");
        builder.AppendLine();
        builder.AppendLine("## Entity Framework");
        builder.AppendLine();
        builder.AppendLine("The project includes `Models`, `Data/AppDbContext.cs`, SQL Server configuration, and `Migrations/InitialCreate.sql`.");
        builder.AppendLine("Docker sets `AUTO_APPLY_DATABASE=true`, so the database schema is created on startup with `EnsureCreated()`.");
        builder.AppendLine();
        builder.AppendLine("To switch to explicit EF migrations later:");
        builder.AppendLine();
        builder.AppendLine("```bash");
        builder.AppendLine("dotnet tool install --global dotnet-ef");
        builder.AppendLine("dotnet ef migrations add InitialCreate");
        builder.AppendLine("dotnet ef database update");
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("## Tables");
        builder.AppendLine();
        foreach (var table in schema.Tables)
        {
            builder.AppendLine($"- `{table.Name}`: {table.Columns.Count} columns");
        }

        return builder.ToString();
    }

    private static string GenerateDbContext(string projectName, IReadOnlyList<EntityInfo> entities)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using Microsoft.EntityFrameworkCore;");
        builder.AppendLine($"using {projectName}.Models;");
        builder.AppendLine();
        builder.AppendLine($"namespace {projectName}.Data;");
        builder.AppendLine();
        builder.AppendLine("public class AppDbContext : DbContext");
        builder.AppendLine("{");
        builder.AppendLine("    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)");
        builder.AppendLine("    {");
        builder.AppendLine("    }");
        builder.AppendLine();

        foreach (var entity in entities)
        {
            builder.AppendLine($"    public DbSet<{entity.ClassName}> {entity.DbSetName} => Set<{entity.ClassName}>();");
        }

        builder.AppendLine();
        builder.AppendLine("    protected override void OnModelCreating(ModelBuilder modelBuilder)");
        builder.AppendLine("    {");
        builder.AppendLine("        base.OnModelCreating(modelBuilder);");
        builder.AppendLine();

        foreach (var entity in entities)
        {
            builder.AppendLine($"        modelBuilder.Entity<{entity.ClassName}>(entity =>");
            builder.AppendLine("        {");
            builder.AppendLine($"            entity.ToTable(\"{EscapeCSharp(entity.Table.Name)}\");");
            AppendKeyConfiguration(builder, entity);
            AppendPropertyConfiguration(builder, entity);
            AppendRelationshipConfiguration(builder, entity, entities);
            builder.AppendLine("        });");
            builder.AppendLine();
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static void AppendKeyConfiguration(StringBuilder builder, EntityInfo entity)
    {
        var primaryKeys = entity.Table.Columns.Where(column => column.IsPrimaryKey).ToList();
        if (primaryKeys.Count == 0)
        {
            builder.AppendLine("            entity.HasNoKey();");
            return;
        }

        if (primaryKeys.Count == 1)
        {
            builder.AppendLine($"            entity.HasKey(e => e.{entity.PropertyByColumnId[primaryKeys[0].Id]});");
            return;
        }

        var keyProperties = string.Join(", ", primaryKeys.Select(column => $"e.{entity.PropertyByColumnId[column.Id]}"));
        builder.AppendLine($"            entity.HasKey(e => new {{ {keyProperties} }});");
    }

    private static void AppendPropertyConfiguration(StringBuilder builder, EntityInfo entity)
    {
        foreach (var column in entity.Table.Columns.OrderBy(column => column.Order))
        {
            var propertyName = entity.PropertyByColumnId[column.Id];
            var line = $"            entity.Property(e => e.{propertyName}).HasColumnName(\"{EscapeCSharp(column.Name)}\").HasColumnType(\"{EscapeCSharp(NormalizeSqlServerType(column.SqlType))}\")";

            var maxLength = TryReadLength(column.SqlType);
            if (maxLength is > 0 and <= 4000 && IsStringType(column.SqlType))
            {
                line += $".HasMaxLength({maxLength.Value})";
            }

            if (!column.IsNullable || column.IsPrimaryKey)
            {
                line += ".IsRequired()";
            }

            builder.AppendLine(line + ";");

            if (column.IsUnique)
            {
                builder.AppendLine($"            entity.HasIndex(e => e.{propertyName}).IsUnique();");
            }
        }
    }

    private static void AppendRelationshipConfiguration(StringBuilder builder, EntityInfo entity, IReadOnlyList<EntityInfo> entities)
    {
        foreach (var column in entity.Table.Columns.Where(column => !string.IsNullOrWhiteSpace(column.ForeignKeyTable)))
        {
            var targetEntity = entities.FirstOrDefault(candidate => string.Equals(candidate.Table.Name, column.ForeignKeyTable, StringComparison.OrdinalIgnoreCase));
            if (targetEntity is null)
            {
                continue;
            }

            var targetColumnName = string.IsNullOrWhiteSpace(column.ForeignKeyColumn) ? "id" : column.ForeignKeyColumn;
            var targetColumn = targetEntity.Table.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, targetColumnName, StringComparison.OrdinalIgnoreCase));
            if (targetColumn is null)
            {
                continue;
            }

            builder.AppendLine($"            entity.HasOne<{targetEntity.ClassName}>()");
            builder.AppendLine("                .WithMany()");
            builder.AppendLine($"                .HasForeignKey(e => e.{entity.PropertyByColumnId[column.Id]})");
            builder.AppendLine($"                .HasPrincipalKey(e => e.{targetEntity.PropertyByColumnId[targetColumn.Id]})");
            builder.AppendLine("                .OnDelete(DeleteBehavior.Restrict);");
        }
    }

    private static string GenerateDatabaseBootstrapper(string projectName, bool hasSeedData = false)
    {
        var seedBlock = !hasSeedData
            ? string.Empty
            : $$"""

            // Chèn dữ liệu mẫu (Seed Data)
            var seedPath = Path.Combine(AppContext.BaseDirectory, "Migrations", "SeedData.sql");
            if (File.Exists(seedPath))
            {
                var seedSql = await File.ReadAllTextAsync(seedPath);
                var batches = seedSql.Split(new[] { "\nGO\n", "\nGO\r\n", "\r\nGO\r\n", "\r\nGO\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var batch in batches)
                {
                    var trimmed = batch.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        await context.Database.ExecuteSqlRawAsync(trimmed);
                    }
                }
            }
""";

        return $$"""
using Microsoft.EntityFrameworkCore;

namespace {{projectName}}.Data;

public static class DatabaseBootstrapper
{
    public static async Task ApplyAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                await context.Database.EnsureCreatedAsync();
{{seedBlock}}
                return;
            }
            catch when (attempt < 20)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }
}
""";
    }

    private static string GenerateSqlScript(DatabaseSchema schema)
    {
        var builder = new StringBuilder();
        builder.AppendLine("-- Initial SQL Server schema generated by the database designer.");
        builder.AppendLine();

        foreach (var table in schema.Tables)
        {
            builder.AppendLine($"CREATE TABLE {SqlName(table.Name)} (");
            var definitions = new List<string>();

            foreach (var column in table.Columns.OrderBy(column => column.Order))
            {
                definitions.Add($"    {SqlName(column.Name)} {NormalizeSqlServerType(column.SqlType)} {(column.IsNullable && !column.IsPrimaryKey ? "NULL" : "NOT NULL")}");
            }

            var primaryKeys = table.Columns.Where(column => column.IsPrimaryKey).ToList();
            if (primaryKeys.Count > 0)
            {
                definitions.Add($"    CONSTRAINT {SqlName("PK_" + table.Name)} PRIMARY KEY ({string.Join(", ", primaryKeys.Select(column => SqlName(column.Name)))})");
            }

            definitions.AddRange(table.Columns.Where(column => column.IsUnique).Select(column => $"    CONSTRAINT {SqlName("UQ_" + table.Name + "_" + column.Name)} UNIQUE ({SqlName(column.Name)})"));
            builder.AppendLine(string.Join(",\n", definitions));
            builder.AppendLine(");");
            builder.AppendLine();
        }

        foreach (var table in schema.Tables)
        {
            foreach (var column in table.Columns.Where(column => !string.IsNullOrWhiteSpace(column.ForeignKeyTable)))
            {
                var targetColumn = string.IsNullOrWhiteSpace(column.ForeignKeyColumn) ? "id" : column.ForeignKeyColumn;
                builder.AppendLine($"ALTER TABLE {SqlName(table.Name)} ADD CONSTRAINT {SqlName("FK_" + table.Name + "_" + column.ForeignKeyTable + "_" + column.Name)} FOREIGN KEY ({SqlName(column.Name)}) REFERENCES {SqlName(column.ForeignKeyTable!)}({SqlName(targetColumn)});");
            }
        }

        return builder.ToString();
    }

    private static string GenerateHomeController(string projectName)
    {
        return $$"""
using Microsoft.AspNetCore.Mvc;

namespace {{projectName}}.Controllers;

public class HomeController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
""";
    }

    private static string GenerateLayout(string projectName)
    {
        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"] - {{projectName}}</title>
    <link rel="stylesheet" href="~/css/site.css" />
</head>
<body>
    <header class="app-header">
        <div>
            <p class="eyebrow">ASP.NET Core MVC</p>
            <h1>{{projectName}}</h1>
        </div>
        <span class="status-pill">SQL Server ready</span>
    </header>
    <main class="page-shell">
        @RenderBody()
    </main>
</body>
</html>
""";
    }

    private static string GenerateHomeView(string projectName, DatabaseSchema schema)
    {
        var builder = new StringBuilder();
        builder.AppendLine("@{");
        builder.AppendLine("    ViewData[\"Title\"] = \"Home\";");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("<section class=\"hero-card\">");
        builder.AppendLine($"    <p class=\"eyebrow\">Generated backend</p>");
        builder.AppendLine($"    <h2>{projectName} is ready</h2>");
        builder.AppendLine("    <p>This project contains production-friendly Models, AppDbContext, SQL Server configuration, Docker setup, and an initial schema script.</p>");
        builder.AppendLine("</section>");
        builder.AppendLine("<section class=\"table-grid\">");

        foreach (var table in schema.Tables)
        {
            builder.AppendLine("    <article class=\"table-card\">");
            builder.AppendLine($"        <h3>{HtmlEncode(table.Name)}</h3>");
            builder.AppendLine("        <ul>");
            foreach (var column in table.Columns.OrderBy(column => column.Order))
            {
                var badges = new List<string>();
                if (column.IsPrimaryKey) badges.Add("PK");
                if (!string.IsNullOrWhiteSpace(column.ForeignKeyTable)) badges.Add("FK");
                if (column.IsUnique) badges.Add("UQ");
                builder.AppendLine($"            <li><span>{HtmlEncode(column.Name)}</span><small>{HtmlEncode(column.SqlType)} {string.Join(" ", badges)}</small></li>");
            }
            builder.AppendLine("        </ul>");
            builder.AppendLine("    </article>");
        }

        builder.AppendLine("</section>");
        return builder.ToString();
    }

    private static string GenerateGeneratedSiteCss()
    {
        return """
:root {
    --ink: #102033;
    --muted: #64748b;
    --line: #d8e3f2;
    --surface: #ffffff;
    --blue: #1d6fe8;
    --blue-soft: #eaf3ff;
}

* { box-sizing: border-box; }
body {
    margin: 0;
    min-height: 100vh;
    color: var(--ink);
    font-family: "Trebuchet MS", "Aptos", sans-serif;
    background: radial-gradient(circle at top left, #e9f4ff, transparent 32rem), linear-gradient(135deg, #f7fbff, #eef4fb);
}
.app-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    padding: 2rem clamp(1rem, 4vw, 4rem);
}
.app-header h1 { margin: .2rem 0 0; font-size: clamp(2rem, 4vw, 4rem); }
.eyebrow { margin: 0; color: var(--blue); text-transform: uppercase; letter-spacing: .16em; font-weight: 800; }
.status-pill { border: 1px solid #9ad3b7; color: #047857; background: #ecfdf5; padding: .7rem 1rem; border-radius: 999px; font-weight: 800; }
.page-shell { width: min(1180px, calc(100% - 2rem)); margin: 0 auto 3rem; }
.hero-card, .table-card { background: rgba(255,255,255,.86); border: 1px solid var(--line); border-radius: 28px; box-shadow: 0 24px 70px rgba(38, 67, 101, .12); }
.hero-card { padding: clamp(1.5rem, 3vw, 3rem); margin-bottom: 1.25rem; }
.hero-card h2 { margin: .2rem 0 .75rem; font-size: clamp(1.8rem, 3vw, 3rem); }
.hero-card p { color: var(--muted); font-size: 1.08rem; }
.table-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 1rem; }
.table-card { padding: 1.2rem; }
.table-card h3 { margin: 0 0 1rem; }
.table-card ul { list-style: none; margin: 0; padding: 0; display: grid; gap: .55rem; }
.table-card li { display: flex; justify-content: space-between; gap: .75rem; border: 1px solid var(--line); background: #f8fbff; border-radius: 14px; padding: .75rem; }
.table-card small { color: var(--muted); font-weight: 700; }
""";
    }

    private static string GenerateModel(string projectName, EntityInfo entity)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using System.ComponentModel.DataAnnotations.Schema;");
        builder.AppendLine();
        builder.AppendLine($"namespace {projectName}.Models;");
        builder.AppendLine();
        builder.AppendLine($"[Table(\"{EscapeCSharp(entity.Table.Name)}\")]");
        builder.AppendLine($"public class {entity.ClassName}");
        builder.AppendLine("{");

        foreach (var column in entity.Table.Columns.OrderBy(column => column.Order))
        {
            var propertyName = entity.PropertyByColumnId[column.Id];
            var propertyType = ToCSharpType(column.SqlType, column.IsNullable && !column.IsPrimaryKey);
            builder.AppendLine($"    [Column(\"{EscapeCSharp(column.Name)}\")]");

            if (propertyType == "string")
            {
                builder.AppendLine($"    public string {propertyName} {{ get; set; }} = string.Empty;");
            }
            else if (propertyType == "byte[]")
            {
                builder.AppendLine($"    public byte[] {propertyName} {{ get; set; }} = Array.Empty<byte>();");
            }
            else
            {
                builder.AppendLine($"    public {propertyType} {propertyName} {{ get; set; }}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string ToCSharpType(string sqlType, bool nullable)
    {
        var type = NormalizeSqlServerType(sqlType).ToLowerInvariant();
        var baseType = "string";

        if (type.Contains("uniqueidentifier")) baseType = "Guid";
        else if (type.Contains("bigint")) baseType = "long";
        else if (type.Contains("int")) baseType = "int";
        else if (type.Contains("bit")) baseType = "bool";
        else if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("money")) baseType = "decimal";
        else if (type.Contains("real")) baseType = "float";
        else if (type.Contains("float")) baseType = "double";
        else if (type.Contains("date") || type.Contains("time")) baseType = "DateTime";
        else if (type.Contains("binary") || type.Contains("image") || type.Contains("varbinary")) baseType = "byte[]";

        if (!nullable || baseType is "string" or "byte[]")
        {
            return nullable && baseType == "string" ? "string?" : nullable && baseType == "byte[]" ? "byte[]?" : baseType;
        }

        return baseType + "?";
    }

    private static string NormalizeSqlServerType(string sqlType)
    {
        var type = string.IsNullOrWhiteSpace(sqlType) ? "nvarchar(255)" : sqlType.Trim().ToLowerInvariant();
        type = type.Replace("[", "").Replace("]", "").Replace("\"", "").Replace("`", "");
        type = Regex.Replace(type, @"\s+", " ");

        if (type is "uuid") return "uniqueidentifier";
        if (type is "boolean" or "bool") return "bit";
        if (type is "serial" or "integer") return "int";
        if (type is "bigserial") return "bigint";
        if (type is "text" or "json" or "jsonb" or "xml") return "nvarchar(max)";
        if (type.StartsWith("varchar", StringComparison.OrdinalIgnoreCase)) return "n" + type;
        if (type.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase)) return "datetime2";
        if (type == "datetime") return "datetime2";
        if (type == "double precision") return "float";

        return type;
    }

    private static bool IsStringType(string sqlType)
    {
        var type = NormalizeSqlServerType(sqlType).ToLowerInvariant();
        return type.Contains("char") || type.Contains("text");
    }

    private static int? TryReadLength(string sqlType)
    {
        var match = Regex.Match(sqlType, @"\((\d+)\)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static string ToPascalCase(string value)
    {
        var parts = Regex.Split(value, @"[^A-Za-z0-9]+")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        var result = parts.Count == 0
            ? "Item"
            : string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + (part.Length > 1 ? part[1..] : string.Empty)));

        if (char.IsDigit(result[0]))
        {
            result = "Item" + result;
        }

        return result;
    }

    private static string Singularize(string value)
    {
        if (value.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && value.Length > 3)
        {
            return value[..^3] + "y";
        }

        if (value.EndsWith("ses", StringComparison.OrdinalIgnoreCase))
        {
            return value[..^2];
        }

        if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !value.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
        {
            return value[..^1];
        }

        return value;
    }

    private static string Pluralize(string value)
    {
        if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return value + "s";
    }

    private static string MakeUnique(string candidate, HashSet<string> used)
    {
        candidate = ToSafeIdentifier(candidate, "Item");
        var unique = candidate;
        var suffix = 2;

        while (!used.Add(unique))
        {
            unique = candidate + suffix++;
        }

        return unique;
    }

    private static string ToSafeIdentifier(string value, string fallback)
    {
        var safe = Regex.Replace(string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(), @"[^A-Za-z0-9_]", string.Empty);
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = fallback;
        }

        if (char.IsDigit(safe[0]))
        {
            safe = fallback + safe;
        }

        return safe;
    }

    private static string EscapeCSharp(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string HtmlEncode(string value)
    {
        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static string SqlName(string value)
    {
        return "[" + value.Replace("]", "]] ").Replace("]] ", "]]") + "]";
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path.Replace('\\', '/'), CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content.Replace("\r\n", "\n"));
    }

    private sealed record EntityInfo(SchemaTable Table, string ClassName, string DbSetName)
    {
        public Dictionary<string, string> PropertyByColumnId { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

