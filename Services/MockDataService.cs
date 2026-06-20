using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DoAnLapTrinhWeb.Models.Designer;

namespace DoAnLapTrinhWeb.Services;

/// <summary>
/// Dịch vụ tạo dữ liệu mẫu (mock data) cho cơ sở dữ liệu.
/// Ưu tiên sử dụng Gemini AI để sinh dữ liệu thực tế.
/// Tự động chuyển sang chế độ fallback (sinh tuần tự) nếu API không khả dụng.
/// </summary>
public sealed class MockDataService
{
    private static readonly HttpClient HttpClient = new();
    private readonly IConfiguration _configuration;
    private const int RowsPerTable = 3;

    public MockDataService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Sinh mã SQL INSERT cho toàn bộ schema.
    /// Trả về chuỗi SQL chứa các câu lệnh INSERT sắp xếp theo thứ tự topo (bảng cha trước bảng con).
    /// </summary>
    public async Task<string> GenerateMockDataSqlAsync(DatabaseSchema schema, CancellationToken cancellationToken = default)
    {
        var sortedTables = TopologicalSort(schema);

        var apiKey = _configuration["Gemini:ApiKey"]
                     ?? _configuration["GEMINI_API_KEY"]
                     ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        Dictionary<string, List<Dictionary<string, object>>>? aiData = null;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                aiData = await GenerateWithGeminiAsync(schema, sortedTables, apiKey, cancellationToken);
            }
            catch
            {
                // Fallback nếu AI thất bại
            }
        }

        aiData ??= GenerateDeterministicData(schema, sortedTables);

        return BuildSqlScript(schema, sortedTables, aiData);
    }

    // ─── Gemini AI ────────────────────────────────────────────────────────

    private static async Task<Dictionary<string, List<Dictionary<string, object>>>?> GenerateWithGeminiAsync(
        DatabaseSchema schema,
        List<SchemaTable> sortedTables,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var model = Environment.GetEnvironmentVariable("GEMINI_REVIEW_MODEL");
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gemini-2.5-flash";
        }

        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";

        var schemaDescription = BuildSchemaDescription(schema, sortedTables);

        var prompt =
            "You are a database seeding expert. Generate exactly 3 rows of realistic Vietnamese mock data for each table in the SQL Server database described below. " +
            "The data must respect ALL foreign key constraints: child table values must reference valid parent IDs that you generated. " +
            "For identity/int primary key columns, use values 1, 2, 3. " +
            "For uniqueidentifier primary keys, generate valid GUIDs. " +
            "For string columns, generate realistic Vietnamese data (e.g. Vietnamese names, addresses, emails). " +
            "For datetime2 columns, use ISO 8601 format (e.g. '2025-01-15T10:30:00'). " +
            "For bit/boolean columns, use 0 or 1. " +
            "For decimal/money columns, use reasonable numeric values. " +
            "Return ONLY strict JSON matching this shape, with NO markdown fences:\n" +
            "{\"tables\":{\"TableName1\":[{\"ColumnName\":value,...},...],\"TableName2\":[...]}}\n\n" +
            "Database schema:\n" + schemaDescription;

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = prompt } }
                }
            },
            generationConfig = new
            {
                temperature = 0.7,
                responseMimeType = "application/json"
            }
        };

        using var response = await HttpClient.PostAsJsonAsync(endpoint, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var text = document.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = StripJsonFence(text);
        return ParseAiResponse(text, schema);
    }

    private static Dictionary<string, List<Dictionary<string, object>>>? ParseAiResponse(string json, DatabaseSchema schema)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement tablesElement;
            if (root.TryGetProperty("tables", out tablesElement))
            {
                // Format: {"tables":{"TableName":[...]}}
            }
            else
            {
                // Format: {"TableName":[...]}
                tablesElement = root;
            }

            var result = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);

            foreach (var table in schema.Tables)
            {
                if (!tablesElement.TryGetProperty(table.Name, out var rowsElement))
                {
                    continue;
                }

                var rows = new List<Dictionary<string, object>>();
                foreach (var rowElement in rowsElement.EnumerateArray())
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (var column in table.Columns)
                    {
                        if (rowElement.TryGetProperty(column.Name, out var value))
                        {
                            row[column.Name] = JsonElementToObject(value);
                        }
                    }
                    rows.Add(row);
                }
                result[table.Name] = rows;
            }

            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static object JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.Null => DBNull.Value,
            _ => element.GetRawText()
        };
    }

    // ─── Deterministic fallback ───────────────────────────────────────────

    private static Dictionary<string, List<Dictionary<string, object>>> GenerateDeterministicData(
        DatabaseSchema schema,
        List<SchemaTable> sortedTables)
    {
        var result = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in sortedTables)
        {
            var rows = new List<Dictionary<string, object>>();

            for (var rowIndex = 0; rowIndex < RowsPerTable; rowIndex++)
            {
                var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                foreach (var column in table.Columns.OrderBy(c => c.Order))
                {
                    row[column.Name] = GenerateColumnValue(column, table, rowIndex, result);
                }

                rows.Add(row);
            }

            result[table.Name] = rows;
        }

        return result;
    }

    private static object GenerateColumnValue(
        SchemaColumn column,
        SchemaTable table,
        int rowIndex,
        Dictionary<string, List<Dictionary<string, object>>> existingData)
    {
        // Khóa ngoại: lấy giá trị từ bảng cha
        if (!string.IsNullOrWhiteSpace(column.ForeignKeyTable) &&
            !string.IsNullOrWhiteSpace(column.ForeignKeyColumn) &&
            existingData.TryGetValue(column.ForeignKeyTable, out var parentRows) &&
            parentRows.Count > 0)
        {
            var parentRowIndex = rowIndex % parentRows.Count;
            if (parentRows[parentRowIndex].TryGetValue(column.ForeignKeyColumn, out var parentValue))
            {
                return parentValue;
            }
        }

        var sqlType = NormalizeSqlType(column.SqlType);
        var name = column.Name.ToLowerInvariant();
        var seq = rowIndex + 1;

        // Khóa chính số: sử dụng 1, 2, 3
        if (column.IsPrimaryKey && IsIntegerType(sqlType))
        {
            return seq;
        }

        // Uniqueidentifier
        if (sqlType.Contains("uniqueidentifier"))
        {
            return Guid.NewGuid().ToString();
        }

        // Bit / Boolean
        if (sqlType.Contains("bit"))
        {
            return rowIndex % 2 == 0 ? 1 : 0;
        }

        // Integer types
        if (IsIntegerType(sqlType))
        {
            return seq * 10;
        }

        // Decimal / Money
        if (sqlType.Contains("decimal") || sqlType.Contains("numeric") || sqlType.Contains("money"))
        {
            return Math.Round(seq * 99.5 + 0.5, 2);
        }

        // Float / Real
        if (sqlType.Contains("float") || sqlType.Contains("real"))
        {
            return Math.Round(seq * 1.5, 2);
        }

        // DateTime
        if (sqlType.Contains("date") || sqlType.Contains("time"))
        {
            return new DateTime(2025, 1 + rowIndex, 15, 10, 30, 0).ToString("yyyy-MM-ddTHH:mm:ss");
        }

        // String types — sinh dữ liệu theo ngữ nghĩa tên cột
        if (IsStringType(sqlType))
        {
            return GenerateStringValue(name, table.Name, seq);
        }

        // Binary
        if (sqlType.Contains("binary") || sqlType.Contains("image"))
        {
            return DBNull.Value;
        }

        return $"Sample{seq}";
    }

    private static string GenerateStringValue(string columnName, string tableName, int seq)
    {
        // Email
        if (columnName.Contains("email"))
            return $"user{seq}@example.com";

        // Password
        if (columnName.Contains("password") || columnName.Contains("hash"))
            return "$2a$11$SampleHashedPasswordValue" + seq;

        // Phone
        if (columnName.Contains("phone") || columnName.Contains("dienthoai") || columnName.Contains("sdt"))
            return $"090{seq:D7}";

        // Address
        if (columnName.Contains("address") || columnName.Contains("diachi"))
        {
            string[] addresses = { "123 Nguyễn Huệ, Q1, TP.HCM", "456 Lê Lợi, Q5, TP.HCM", "789 Trần Hưng Đạo, Q1, TP.HCM" };
            return addresses[(seq - 1) % addresses.Length];
        }

        // Name (tên người)
        if (columnName.Contains("name") || columnName.Contains("ten") || columnName.Contains("hoten"))
        {
            string[] names = { "Nguyễn Văn A", "Trần Thị B", "Lê Hoàng C" };
            return names[(seq - 1) % names.Length];
        }

        // Description / MoTa
        if (columnName.Contains("description") || columnName.Contains("mota") || columnName.Contains("ghichu") || columnName.Contains("note"))
            return $"Mô tả mẫu số {seq} cho {tableName}";

        // Title / TieuDe
        if (columnName.Contains("title") || columnName.Contains("tieude"))
            return $"Tiêu đề mẫu {seq}";

        // Image / URL
        if (columnName.Contains("image") || columnName.Contains("hinh") || columnName.Contains("anh") || columnName.Contains("avatar"))
            return $"/images/sample{seq}.jpg";

        if (columnName.Contains("url") || columnName.Contains("link"))
            return $"https://example.com/page{seq}";

        // ID dạng chuỗi
        if (columnName == "id" || columnName.EndsWith("id"))
            return $"{tableName.ToUpperInvariant()}{seq:D3}";

        // Mặc định
        return $"{ToPascalCase(columnName)} {seq}";
    }

    // ─── SQL Builder ──────────────────────────────────────────────────────

    private static string BuildSqlScript(
        DatabaseSchema schema,
        List<SchemaTable> sortedTables,
        Dictionary<string, List<Dictionary<string, object>>> data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- ============================================================");
        sb.AppendLine("-- Dữ liệu mẫu (Mock Data) được tạo tự động.");
        sb.AppendLine("-- Mỗi bảng có 3 bản ghi mẫu.");
        sb.AppendLine("-- ============================================================");
        sb.AppendLine();

        foreach (var table in sortedTables)
        {
            if (!data.TryGetValue(table.Name, out var rows) || rows.Count == 0)
            {
                continue;
            }

            var hasIdentityPk = table.Columns.Any(c => c.IsPrimaryKey && IsIntegerType(NormalizeSqlType(c.SqlType)) && string.IsNullOrWhiteSpace(c.ForeignKeyTable));

            sb.AppendLine($"-- Bảng: {table.Name}");
            sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM [{EscapeSql(table.Name)}])");
            sb.AppendLine("BEGIN");

            if (hasIdentityPk)
            {
                sb.AppendLine($"    SET IDENTITY_INSERT [{EscapeSql(table.Name)}] ON;");
            }

            foreach (var row in rows)
            {
                var columns = table.Columns
                    .OrderBy(c => c.Order)
                    .Where(c => row.ContainsKey(c.Name) && row[c.Name] != DBNull.Value)
                    .ToList();

                if (columns.Count == 0) continue;

                var colNames = string.Join(", ", columns.Select(c => $"[{EscapeSql(c.Name)}]"));
                var colValues = string.Join(", ", columns.Select(c => FormatSqlValue(row[c.Name], c.SqlType)));

                sb.AppendLine($"    INSERT INTO [{EscapeSql(table.Name)}] ({colNames}) VALUES ({colValues});");
            }

            if (hasIdentityPk)
            {
                sb.AppendLine($"    SET IDENTITY_INSERT [{EscapeSql(table.Name)}] OFF;");
            }

            sb.AppendLine("END");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatSqlValue(object value, string sqlType)
    {
        if (value is DBNull)
        {
            return "NULL";
        }

        var type = NormalizeSqlType(sqlType);

        if (value is long or int or double or float or decimal)
        {
            // Bit type — đảm bảo chỉ 0 hoặc 1
            if (type.Contains("bit"))
            {
                var intVal = Convert.ToInt32(value);
                return intVal != 0 ? "1" : "0";
            }

            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "0";
        }

        var strValue = value.ToString() ?? string.Empty;
        return $"N'{EscapeSqlString(strValue)}'";
    }

    // ─── Topological Sort ─────────────────────────────────────────────────

    private static List<SchemaTable> TopologicalSort(DatabaseSchema schema)
    {
        var tableMap = schema.Tables.ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in schema.Tables)
        {
            inDegree.TryAdd(table.Name, 0);
            adjacency.TryAdd(table.Name, new List<string>());
        }

        foreach (var table in schema.Tables)
        {
            foreach (var column in table.Columns.Where(c => !string.IsNullOrWhiteSpace(c.ForeignKeyTable)))
            {
                var parentName = column.ForeignKeyTable!;
                if (string.Equals(parentName, table.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Bỏ qua self-reference
                }

                if (!tableMap.ContainsKey(parentName))
                {
                    continue;
                }

                adjacency.TryAdd(parentName, new List<string>());
                if (!adjacency[parentName].Contains(table.Name, StringComparer.OrdinalIgnoreCase))
                {
                    adjacency[parentName].Add(table.Name);
                    inDegree[table.Name] = inDegree.GetValueOrDefault(table.Name) + 1;
                }
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = new List<SchemaTable>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (tableMap.TryGetValue(current, out var table))
            {
                sorted.Add(table);
            }

            if (adjacency.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0)
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        // Thêm các bảng còn lại (nếu có chu kỳ)
        foreach (var table in schema.Tables)
        {
            if (!sorted.Any(t => string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase)))
            {
                sorted.Add(table);
            }
        }

        return sorted;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static string BuildSchemaDescription(DatabaseSchema schema, List<SchemaTable> sortedTables)
    {
        var sb = new StringBuilder();
        foreach (var table in sortedTables)
        {
            sb.AppendLine($"Table: {table.Name}");
            foreach (var column in table.Columns.OrderBy(c => c.Order))
            {
                var flags = new List<string>();
                if (column.IsPrimaryKey) flags.Add("PK");
                if (!column.IsNullable) flags.Add("NOT NULL");
                if (column.IsUnique) flags.Add("UNIQUE");
                if (!string.IsNullOrWhiteSpace(column.ForeignKeyTable))
                    flags.Add($"FK -> {column.ForeignKeyTable}.{column.ForeignKeyColumn}");

                sb.AppendLine($"  - {column.Name} {column.SqlType} {string.Join(", ", flags)}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string NormalizeSqlType(string sqlType)
    {
        return string.IsNullOrWhiteSpace(sqlType) ? "nvarchar(255)" : sqlType.Trim().ToLowerInvariant();
    }

    private static bool IsIntegerType(string normalizedType)
    {
        return normalizedType.Contains("int") || normalizedType.Contains("smallint") || normalizedType.Contains("tinyint") || normalizedType.Contains("bigint");
    }

    private static bool IsStringType(string normalizedType)
    {
        return normalizedType.Contains("char") || normalizedType.Contains("text");
    }

    private static string EscapeSql(string value)
    {
        return value.Replace("]", "]]");
    }

    private static string EscapeSqlString(string value)
    {
        return value.Replace("'", "''");
    }

    private static string StripJsonFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewLine >= 0 && lastFence > firstNewLine)
        {
            return trimmed.Substring(firstNewLine + 1, lastFence - firstNewLine - 1).Trim();
        }

        return trimmed;
    }

    private static string ToPascalCase(string value)
    {
        var parts = Regex.Split(value, @"[^A-Za-z0-9]+")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        return parts.Count == 0
            ? "Item"
            : string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..] : string.Empty)));
    }
}
