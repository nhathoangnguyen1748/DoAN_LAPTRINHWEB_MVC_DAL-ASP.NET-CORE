using System.Net.Http.Json;
using System.Text.Json;
using DoAnLapTrinhWeb.Models.Designer;

namespace DoAnLapTrinhWeb.Services;

public sealed class SchemaReviewService
{
    private static readonly HttpClient HttpClient = new();

    public async Task<AiReviewResult> ReviewAsync(DatabaseSchema schema, CancellationToken cancellationToken = default)
    {
        var deterministic = ReviewWithRules(schema);
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return deterministic;
        }

        try
        {
            var model = Environment.GetEnvironmentVariable("GEMINI_REVIEW_MODEL");
            if (string.IsNullOrWhiteSpace(model))
            {
                model = "gemini-2.5-flash";
            }

            var aiResult = await ReviewWithGeminiAsync(schema, deterministic, apiKey, model, cancellationToken);
            if (aiResult is null)
            {
                return deterministic;
            }

            aiResult.Source = $"Gemini {model} + deterministic database rules";
            MergeRuleIssues(aiResult, deterministic);
            aiResult.Summary = aiResult.Issues.Count == 0
                ? "No database relationship issues were found."
                : $"Found {aiResult.Issues.Count} database design issue(s).";

            return aiResult;
        }
        catch
        {
            deterministic.Source = "Deterministic database rules (AI fallback)";
            return deterministic;
        }
    }

    private static AiReviewResult ReviewWithRules(DatabaseSchema schema)
    {
        var result = new AiReviewResult
        {
            Source = "Deterministic database rules"
        };

        if (schema.Tables.Count == 0)
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "HIGH",
                Type = "EMPTY_SCHEMA",
                Message = "The schema does not contain any tables.",
                Suggestion = "Import SQL or add at least one table before generating ASP.NET Core MVC source code."
            });
        }

        foreach (var duplicateGroup in schema.Tables.GroupBy(table => table.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "HIGH",
                Type = "DUPLICATE_TABLE",
                Table = duplicateGroup.Key,
                Message = $"The table name '{duplicateGroup.Key}' appears more than once.",
                Suggestion = "Rename duplicate tables so each generated model class maps to one database table."
            });
        }

        foreach (var table in schema.Tables)
        {
            ReviewTable(table, schema, result);
        }

        result.Summary = result.Issues.Count == 0
            ? "No database relationship issues were found."
            : $"Found {result.Issues.Count} database design issue(s).";

        return result;
    }

    private static void ReviewTable(SchemaTable table, DatabaseSchema schema, AiReviewResult result)
    {
        if (string.IsNullOrWhiteSpace(table.Name))
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "HIGH",
                Type = "EMPTY_TABLE_NAME",
                Message = "A table has no name.",
                Suggestion = "Give every table a clear singular or plural business name before exporting."
            });
        }

        if (table.Columns.Count == 0)
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "HIGH",
                Type = "EMPTY_TABLE",
                Table = table.Name,
                Message = $"Table '{table.Name}' does not contain any columns.",
                Suggestion = "Add columns before generating a model class for this table."
            });
            return;
        }

        var primaryKeys = table.Columns.Where(column => column.IsPrimaryKey).ToList();
        if (primaryKeys.Count == 0)
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "HIGH",
                Type = "MISSING_PRIMARY_KEY",
                Table = table.Name,
                Message = $"Table '{table.Name}' has no primary key.",
                Suggestion = "Add a primary key column, usually 'Id', so Entity Framework can track rows correctly."
            });
        }

        foreach (var duplicateColumn in table.Columns.GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "HIGH",
                Type = "DUPLICATE_COLUMN",
                Table = table.Name,
                Column = duplicateColumn.Key,
                Message = $"Table '{table.Name}' contains duplicate column '{duplicateColumn.Key}'.",
                Suggestion = "Rename duplicate columns because C# model properties must be unique."
            });
        }

        foreach (var column in table.Columns)
        {
            ReviewColumn(table, column, schema, result);
        }
    }

    private static void ReviewColumn(SchemaTable table, SchemaColumn column, DatabaseSchema schema, AiReviewResult result)
    {
        if (string.IsNullOrWhiteSpace(column.Name))
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "HIGH",
                Type = "EMPTY_COLUMN_NAME",
                Table = table.Name,
                Message = $"Table '{table.Name}' has a column without a name.",
                Suggestion = "Give every column a clear name before export."
            });
        }

        if (column.IsPrimaryKey && column.IsNullable)
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "HIGH",
                Type = "NULLABLE_PRIMARY_KEY",
                Table = table.Name,
                Column = column.Name,
                Message = $"Primary key '{table.Name}.{column.Name}' is nullable.",
                Suggestion = "Primary key columns should be required. Disable Nullable for this column."
            });
        }

        if (string.IsNullOrWhiteSpace(column.ForeignKeyTable))
        {
            return;
        }

        var targetTable = schema.Tables.FirstOrDefault(candidate => string.Equals(candidate.Name, column.ForeignKeyTable, StringComparison.OrdinalIgnoreCase));
        if (targetTable is null)
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "HIGH",
                Type = "FK_TARGET_TABLE_MISSING",
                Table = table.Name,
                Column = column.Name,
                Message = $"Foreign key '{table.Name}.{column.Name}' references missing table '{column.ForeignKeyTable}'.",
                Suggestion = "Create the referenced table or change the relationship target."
            });
            return;
        }

        var targetColumnName = string.IsNullOrWhiteSpace(column.ForeignKeyColumn) ? "id" : column.ForeignKeyColumn;
        var targetColumn = targetTable.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, targetColumnName, StringComparison.OrdinalIgnoreCase));
        if (targetColumn is null)
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "HIGH",
                Type = "FK_TARGET_COLUMN_MISSING",
                Table = table.Name,
                Column = column.Name,
                Message = $"Foreign key '{table.Name}.{column.Name}' references missing column '{targetTable.Name}.{targetColumnName}'.",
                Suggestion = "Point the foreign key to an existing primary or unique column."
            });
            return;
        }

        if (!AreTypesCompatible(column.SqlType, targetColumn.SqlType))
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "HIGH",
                Type = "FK_TYPE_MISMATCH",
                Table = table.Name,
                Column = column.Name,
                Message = $"Foreign key '{table.Name}.{column.Name}' has type '{column.SqlType}' but references '{targetTable.Name}.{targetColumn.Name}' with type '{targetColumn.SqlType}'.",
                Suggestion = "Use the same SQL type for both sides of the relationship."
            });
        }

        if (!targetColumn.IsPrimaryKey && !targetColumn.IsUnique)
        {
            result.Issues.Add(new ReviewIssue
            {
                Severity = "MEDIUM",
                Type = "FK_TARGET_NOT_UNIQUE",
                Table = table.Name,
                Column = column.Name,
                Message = $"Foreign key '{table.Name}.{column.Name}' references '{targetTable.Name}.{targetColumn.Name}', but that target is not primary or unique.",
                Suggestion = "Reference a primary key or mark the target column as Unique."
            });
        }
    }

    private static bool AreTypesCompatible(string left, string right)
    {
        return GetTypeFamily(left) == GetTypeFamily(right);
    }

    private static string GetTypeFamily(string sqlType)
    {
        var normalized = sqlType.Trim().ToLowerInvariant();

        if (normalized.Contains("uniqueidentifier") || normalized.Contains("uuid")) return "guid";
        if (normalized.Contains("bigint") || normalized.Contains("bigserial")) return "long";
        if (normalized.Contains("int") || normalized.Contains("serial")) return "int";
        if (normalized.Contains("char") || normalized.Contains("text") || normalized.Contains("json") || normalized.Contains("xml")) return "string";
        if (normalized.Contains("bit") || normalized.Contains("bool")) return "bool";
        if (normalized.Contains("decimal") || normalized.Contains("numeric") || normalized.Contains("money")) return "decimal";
        if (normalized.Contains("float") || normalized.Contains("double") || normalized.Contains("real")) return "floating";
        if (normalized.Contains("date") || normalized.Contains("time")) return "datetime";
        if (normalized.Contains("binary") || normalized.Contains("blob") || normalized.Contains("bytea")) return "binary";

        return normalized;
    }

    private static async Task<AiReviewResult?> ReviewWithGeminiAsync(
        DatabaseSchema schema,
        AiReviewResult deterministic,
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        var schemaJson = JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
        var ruleJson = JsonSerializer.Serialize(deterministic.Issues, new JsonSerializerOptions { WriteIndented = true });

        var prompt = "You are reviewing only a database schema for an ASP.NET Core MVC + SQL Server project. " +
                     "Do not review UI, APIs, documentation, security policies, or unrelated product ideas. " +
                     "Return strict JSON matching this shape: {\"summary\":string,\"source\":string,\"issues\":[{\"severity\":\"HIGH|MEDIUM|LOW\",\"type\":string,\"table\":string|null,\"column\":string|null,\"message\":string,\"suggestion\":string}]}. " +
                     "Focus on primary keys, foreign keys, relationship direction, type compatibility, nullable relationship columns, duplicate names, and SQL Server/Entity Framework model correctness.\n\n" +
                     "Schema JSON:\n" + schemaJson + "\n\nDeterministic rule issues already found:\n" + ruleJson;

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
                temperature = 0.15,
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
        return JsonSerializer.Deserialize<AiReviewResult>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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

    private static void MergeRuleIssues(AiReviewResult aiResult, AiReviewResult deterministic)
    {
        foreach (var issue in deterministic.Issues)
        {
            var exists = aiResult.Issues.Any(candidate =>
                string.Equals(candidate.Type, issue.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Table, issue.Table, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Column, issue.Column, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                aiResult.Issues.Add(issue);
            }
        }
    }
}
