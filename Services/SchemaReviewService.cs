using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DoAnLapTrinhWeb.Models.Designer;
using Microsoft.Extensions.Configuration;

namespace DoAnLapTrinhWeb.Services;

public sealed class SchemaReviewService
{
    private static readonly HttpClient HttpClient = new();
    private readonly IConfiguration _configuration;

    public SchemaReviewService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALTER", "AND", "AS", "ASC", "AUTHORIZATION", "BACKUP", "BEGIN", "BETWEEN",
        "BREAK", "BROWSE", "BULK", "BY", "CASCADE", "CASE", "CHECK", "CLUSTERED", "COLUMN",
        "COMMIT", "CONSTRAINT", "CREATE", "CURRENT", "DATABASE", "DEFAULT", "DELETE", "DESC",
        "DISTINCT", "DROP", "ELSE", "END", "EXISTS", "FOREIGN", "FROM", "FULL", "GROUP",
        "HAVING", "IDENTITY", "IF", "IN", "INDEX", "INNER", "INSERT", "INTERSECT", "INTO",
        "IS", "JOIN", "KEY", "LEFT", "LIKE", "MERGE", "NOCHECK", "NONCLUSTERED", "NOT",
        "NULL", "ON", "OR", "ORDER", "OUTER", "PRIMARY", "PROCEDURE", "REFERENCES", "RIGHT",
        "ROLLBACK", "ROWCOUNT", "SELECT", "SET", "TABLE", "THEN", "TO", "TOP", "TRAN",
        "TRIGGER", "UNION", "UNIQUE", "UPDATE", "USER", "VALUES", "VIEW", "WHERE", "WHILE",
        "class", "namespace", "public", "private", "protected", "internal", "string", "int",
        "long", "decimal", "double", "float", "bool", "object"
    };

    public async Task<AiReviewResult> ReviewAsync(DatabaseSchema schema, CancellationToken cancellationToken = default)
    {
        var safeSchema = schema ?? new DatabaseSchema();
        var deterministic = ReviewWithRules(safeSchema);
        var apiKey = _configuration["Gemini:ApiKey"]
                     ?? _configuration["GEMINI_API_KEY"]
                     ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");

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

            var aiResult = await ReviewWithGeminiAsync(safeSchema, deterministic, apiKey, model, cancellationToken);
            if (aiResult is null)
            {
                return deterministic;
            }

            aiResult.Issues ??= new List<ReviewIssue>();
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

    public async Task<ReviewFixResult> FixAsync(ReviewFixRequest request, CancellationToken cancellationToken = default)
    {
        var schema = CloneSchema(request?.Schema ?? new DatabaseSchema());
        NormalizeSchemaIdentity(schema);

        var fixedCount = request?.FixAll == true || request?.Issue is null
            ? ApplyAllFixes(schema)
            : ApplySingleFix(schema, request.Issue) ? 1 : 0;

        var review = await ReviewAsync(schema, cancellationToken);
        return new ReviewFixResult
        {
            Schema = schema,
            Review = review,
            FixedCount = fixedCount,
            Message = fixedCount == 0
                ? "No automatic fix was applied."
                : $"Applied {fixedCount} automatic fix(es)."
        };
    }

    private static AiReviewResult ReviewWithRules(DatabaseSchema schema)
    {
        var result = new AiReviewResult
        {
            Source = "Deterministic database rules"
        };

        ReviewProjectName(schema, result);

        if (schema.Tables.Count == 0)
        {
            AddIssue(
                result,
                "HIGH",
                "EMPTY_SCHEMA",
                "The schema does not contain any tables.",
                "Import SQL or add at least one table before generating ASP.NET Core MVC source code.",
                canAutoFix: true,
                fixAction: "Create a starter table with an Id primary key.");
        }

        foreach (var duplicateGroup in schema.Tables.GroupBy(table => table.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            foreach (var table in duplicateGroup.Skip(1))
            {
                AddIssue(
                    result,
                    "HIGH",
                    "DUPLICATE_TABLE",
                    $"The table name '{duplicateGroup.Key}' appears more than once.",
                    "Rename duplicate tables so each generated model class maps to one database table.",
                    table: table.Name,
                    canAutoFix: true,
                    fixAction: "Rename duplicate tables and update foreign-key references.");
            }
        }

        foreach (var collisionGroup in schema.Tables
            .Where(table => !string.IsNullOrWhiteSpace(table.Name))
            .GroupBy(table => ToGeneratedName(Singularize(table.Name), "Item"), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            foreach (var table in collisionGroup.Skip(1))
            {
                AddIssue(
                    result,
                    "MEDIUM",
                    "GENERATED_MODEL_NAME_COLLISION",
                    $"Table '{table.Name}' generates the same C# model name as another table.",
                    "Use table names that generate unique model class names.",
                    table: table.Name,
                    canAutoFix: true,
                    fixAction: "Rename the table to a unique generated model name.");
            }
        }

        foreach (var table in schema.Tables)
        {
            ReviewTable(table, schema, result);
        }

        ReviewRelationshipCycles(schema, result);
        ReviewSemanticRelationships(schema, result);

        result.Summary = result.Issues.Count == 0
            ? "No database relationship issues were found."
            : $"Found {result.Issues.Count} database design issue(s).";

        return result;
    }

    private static void ReviewProjectName(DatabaseSchema schema, AiReviewResult result)
    {
        var projectName = schema.ProjectName?.Trim() ?? string.Empty;
        var safeProjectName = ToSafeIdentifier(projectName, "GeneratedMvcApp");
        if (!string.Equals(projectName, safeProjectName, StringComparison.Ordinal))
        {
            AddIssue(
                result,
                "LOW",
                "INVALID_PROJECT_NAME",
                $"Project name '{projectName}' is not a safe C# project/database identifier.",
                "Use only letters, digits, and underscores, and do not start the name with a digit.",
                canAutoFix: true,
                fixAction: $"Rename the project to '{safeProjectName}'.");
        }
    }

    private static void ReviewTable(SchemaTable table, DatabaseSchema schema, AiReviewResult result)
    {
        var tableName = table.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(tableName))
        {
            var restoreName = ToSafeIdentifier(table.LastValidName, "Table");
            AddIssue(
                result,
                "HIGH",
                "EMPTY_TABLE_NAME",
                "A table has no name.",
                "Give every table a clear singular or plural business name before exporting.",
                table: table.Name,
                canAutoFix: true,
                fixAction: string.IsNullOrWhiteSpace(table.LastValidName)
                    ? "Rename the table to a generated safe name."
                    : $"Restore the table name to '{restoreName}'.");
        }
        else if (!IsSafeIdentifier(tableName))
        {
            AddIssue(
                result,
                "MEDIUM",
                "INVALID_TABLE_NAME",
                $"Table '{tableName}' contains characters that are awkward for generated C# and SQL Server code.",
                "Use letters, digits, and underscores, and do not start the name with a digit.",
                table: table.Name,
                canAutoFix: true,
                fixAction: "Rename the table to a safe identifier and update foreign keys.");
        }
        else if (ReservedNames.Contains(tableName))
        {
            AddIssue(
                result,
                "MEDIUM",
                "RESERVED_TABLE_NAME",
                $"Table '{tableName}' uses a SQL Server or C# reserved word.",
                "Rename the table to avoid quoting problems and generated-code confusion.",
                table: table.Name,
                canAutoFix: true,
                fixAction: "Append a clear suffix to the table name and update foreign keys.");
        }

        if (table.Columns.Count == 0)
        {
            AddIssue(
                result,
                "HIGH",
                "EMPTY_TABLE",
                $"Table '{table.Name}' does not contain any columns.",
                "Add columns before generating a model class for this table.",
                table: table.Name,
                canAutoFix: true,
                fixAction: "Add an Id primary key column.");
            return;
        }

        var primaryKeys = table.Columns.Where(column => column.IsPrimaryKey).ToList();
        if (primaryKeys.Count == 0)
        {
            AddIssue(
                result,
                "HIGH",
                "MISSING_PRIMARY_KEY",
                $"Table '{table.Name}' has no primary key.",
                "Add a primary key column, usually 'Id', so Entity Framework can track rows correctly.",
                table: table.Name,
                canAutoFix: true,
                fixAction: "Use an existing Id column or add a new Id int primary key.");
        }
        else if (primaryKeys.Count > 1)
        {
            AddIssue(
                result,
                "LOW",
                "COMPOSITE_PRIMARY_KEY",
                $"Table '{table.Name}' has a composite primary key.",
                "Composite keys are supported, but simple Id keys are easier for generated MVC forms and routes.",
                table: table.Name);
        }

        foreach (var duplicateColumn in table.Columns.GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            foreach (var column in duplicateColumn.Skip(1))
            {
                AddIssue(
                    result,
                    "HIGH",
                    "DUPLICATE_COLUMN",
                    $"Table '{table.Name}' contains duplicate column '{duplicateColumn.Key}'.",
                    "Rename duplicate columns because C# model properties must be unique.",
                    table: table.Name,
                    column: column.Name,
                    canAutoFix: true,
                    fixAction: "Rename duplicate columns and update foreign-key references.");
            }
        }

        foreach (var collisionGroup in table.Columns
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .GroupBy(column => ToGeneratedName(column.Name, "Property"), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            foreach (var column in collisionGroup.Skip(1))
            {
                AddIssue(
                    result,
                    "MEDIUM",
                    "GENERATED_PROPERTY_NAME_COLLISION",
                    $"Column '{table.Name}.{column.Name}' generates the same C# property name as another column.",
                    "Use column names that generate unique model property names.",
                    table: table.Name,
                    column: column.Name,
                    canAutoFix: true,
                    fixAction: "Rename the column to a unique generated property name.");
            }
        }

        if (table.Columns.GroupBy(column => column.Order).Any(group => group.Count() > 1))
        {
            AddIssue(
                result,
                "LOW",
                "DUPLICATE_COLUMN_ORDER",
                $"Table '{table.Name}' has columns with duplicate display order values.",
                "Keep column order values unique so the diagram and generated SQL remain stable.",
                table: table.Name,
                canAutoFix: true,
                fixAction: "Reorder columns from top to bottom.");
        }

        foreach (var column in table.Columns)
        {
            ReviewColumn(table, column, schema, result);
        }
    }

    private static void ReviewColumn(SchemaTable table, SchemaColumn column, DatabaseSchema schema, AiReviewResult result)
    {
        var columnName = column.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(columnName))
        {
            AddIssue(
                result,
                "HIGH",
                "EMPTY_COLUMN_NAME",
                $"Table '{table.Name}' has a column without a name.",
                "Give every column a clear name before export.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Rename the column to a generated safe name.");
        }
        else if (!IsSafeIdentifier(columnName))
        {
            AddIssue(
                result,
                "MEDIUM",
                "INVALID_COLUMN_NAME",
                $"Column '{table.Name}.{columnName}' contains characters that are awkward for generated C# and SQL Server code.",
                "Use letters, digits, and underscores, and do not start the name with a digit.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Rename the column to a safe identifier and update foreign keys.");
        }
        else if (ReservedNames.Contains(columnName))
        {
            AddIssue(
                result,
                "MEDIUM",
                "RESERVED_COLUMN_NAME",
                $"Column '{table.Name}.{columnName}' uses a SQL Server or C# reserved word.",
                "Rename the column to avoid quoting problems and generated-code confusion.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Append a clear suffix to the column name and update foreign keys.");
        }

        ReviewColumnType(table, column, result);
        ReviewLogicalColumnType(table, column, result);

        if (column.IsPrimaryKey && column.IsNullable)
        {
            AddIssue(
                result,
                "HIGH",
                "NULLABLE_PRIMARY_KEY",
                $"Primary key '{table.Name}.{column.Name}' is nullable.",
                "Primary key columns should be required. Disable Nullable for this column.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Mark the primary key as not nullable.");
        }

        if (column.IsPrimaryKey && IsBadPrimaryKeyType(column.SqlType))
        {
            var canFix = string.Equals(column.Name, "Id", StringComparison.OrdinalIgnoreCase);
            AddIssue(
                result,
                "MEDIUM",
                "PK_UNSUITABLE_TYPE",
                $"Primary key '{table.Name}.{column.Name}' uses type '{column.SqlType}', which is not a good generated Entity Framework key type.",
                "Prefer int, bigint, uniqueidentifier, or a bounded string key.",
                table: table.Name,
                column: column.Name,
                canAutoFix: canFix,
                fixAction: canFix ? "Change the Id primary key type to int." : string.Empty);
        }

        if (string.IsNullOrWhiteSpace(column.ForeignKeyTable))
        {
            ReviewImplicitForeignKey(table, column, schema, result);
            return;
        }

        var targetTable = FindTable(schema, column.ForeignKeyTable);
        if (targetTable is null)
        {
            AddIssue(
                result,
                "HIGH",
                "FK_TARGET_TABLE_MISSING",
                $"Foreign key '{table.Name}.{column.Name}' references missing table '{column.ForeignKeyTable}'.",
                "Create the referenced table or change the relationship target.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Create the missing target table with a compatible key.");
            return;
        }

        var targetColumnName = string.IsNullOrWhiteSpace(column.ForeignKeyColumn) ? "id" : column.ForeignKeyColumn;
        var targetColumn = targetTable.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, targetColumnName, StringComparison.OrdinalIgnoreCase));
        if (targetColumn is null)
        {
            AddIssue(
                result,
                "HIGH",
                "FK_TARGET_COLUMN_MISSING",
                $"Foreign key '{table.Name}.{column.Name}' references missing column '{targetTable.Name}.{targetColumnName}'.",
                "Point the foreign key to an existing primary or unique column.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Create the missing target column with a compatible type.");
            return;
        }

        if (!AreTypesCompatible(column.SqlType, targetColumn.SqlType))
        {
            AddIssue(
                result,
                "HIGH",
                "FK_TYPE_MISMATCH",
                $"Foreign key '{table.Name}.{column.Name}' has type '{column.SqlType}' but references '{targetTable.Name}.{targetColumn.Name}' with type '{targetColumn.SqlType}'.",
                "Use the same SQL type for both sides of the relationship.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Change the foreign-key column type to match the target key.");
        }

        if (!targetColumn.IsPrimaryKey && !targetColumn.IsUnique)
        {
            AddIssue(
                result,
                "MEDIUM",
                "FK_TARGET_NOT_UNIQUE",
                $"Foreign key '{table.Name}.{column.Name}' references '{targetTable.Name}.{targetColumn.Name}', but that target is not primary or unique.",
                "Reference a primary key or mark the target column as Unique.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Mark the referenced target column as Unique.");
        }
    }

    private static void ReviewColumnType(SchemaTable table, SchemaColumn column, AiReviewResult result)
    {
        if (string.IsNullOrWhiteSpace(column.SqlType))
        {
            AddIssue(
                result,
                "HIGH",
                "MISSING_SQL_TYPE",
                $"Column '{table.Name}.{column.Name}' has no SQL type.",
                "Choose a SQL Server type before export.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Infer a safe SQL Server type.");
            return;
        }

        if (!IsKnownSqlType(column.SqlType))
        {
            AddIssue(
                result,
                "HIGH",
                "UNSUPPORTED_SQL_TYPE",
                $"Column '{table.Name}.{column.Name}' uses unsupported SQL type '{column.SqlType}'.",
                "Use a SQL Server-compatible type such as int, bigint, uniqueidentifier, nvarchar, decimal, bit, datetime2, or varbinary.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Replace the type with a safe SQL Server type.");
            return;
        }

        if (IsLegacySqlType(column.SqlType))
        {
            AddIssue(
                result,
                "MEDIUM",
                "LEGACY_SQL_TYPE",
                $"Column '{table.Name}.{column.Name}' uses legacy SQL Server type '{column.SqlType}'.",
                "Use nvarchar(max) instead of text/ntext, and varbinary(max) instead of image.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Replace the legacy type with the modern SQL Server equivalent.");
        }

        if (IsStringType(column.SqlType) && !HasStringLength(column.SqlType) && !IsMaxLength(column.SqlType))
        {
            AddIssue(
                result,
                "MEDIUM",
                "MISSING_STRING_LENGTH",
                $"Column '{table.Name}.{column.Name}' uses '{column.SqlType}' without a length.",
                "Specify a length such as nvarchar(255), or use nvarchar(max) only for long free-form text.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Set the type to nvarchar(255).");
        }

        if (TryReadStringLength(column.SqlType) is <= 0)
        {
            AddIssue(
                result,
                "HIGH",
                "INVALID_STRING_LENGTH",
                $"Column '{table.Name}.{column.Name}' has an invalid string length in '{column.SqlType}'.",
                "Use a positive string length or max.",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Set the type to nvarchar(255).");
        }

        if (IsDecimalType(column.SqlType) && !HasPrecision(column.SqlType))
        {
            AddIssue(
                result,
                "LOW",
                "DECIMAL_WITHOUT_PRECISION",
                $"Column '{table.Name}.{column.Name}' uses '{column.SqlType}' without precision and scale.",
                "Specify precision and scale to avoid provider defaults, for example decimal(18,2).",
                table: table.Name,
                column: column.Name,
                canAutoFix: true,
                fixAction: "Set the type to decimal(18,2).");
        }
    }

    private static void ReviewLogicalColumnType(SchemaTable table, SchemaColumn column, AiReviewResult result)
    {
        var columnName = column.Name?.Trim() ?? string.Empty;
        var sqlType = column.SqlType?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(sqlType))
        {
            return;
        }

        if (columnName.Contains("email", StringComparison.OrdinalIgnoreCase))
        {
            var family = GetTypeFamily(sqlType);
            if (family != "string")
            {
                AddIssue(
                    result,
                    "HIGH",
                    "LOGICAL_TYPE_MISMATCH",
                    $"Column '{table.Name}.{column.Name}' is named '{columnName}' but is typed as '{sqlType}'.",
                    "Email addresses should be stored as string type (e.g. nvarchar(255)).",
                    table: table.Name,
                    column: column.Name,
                    canAutoFix: true,
                    fixAction: "Change column type to nvarchar(255).");
            }
        }
        else if (columnName.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            var family = GetTypeFamily(sqlType);
            if (family != "string" && family != "binary")
            {
                AddIssue(
                    result,
                    "HIGH",
                    "LOGICAL_TYPE_MISMATCH",
                    $"Column '{table.Name}.{column.Name}' is named '{columnName}' but is typed as '{sqlType}'.",
                    "Password hashes/salts should be stored as string or binary type.",
                    table: table.Name,
                    column: column.Name,
                    canAutoFix: true,
                    fixAction: "Change column type to nvarchar(255).");
            }
            else if (family == "string")
            {
                var length = TryReadStringLength(sqlType);
                if (length is not null and < 60 && !IsMaxLength(sqlType))
                {
                    AddIssue(
                        result,
                        "MEDIUM",
                        "PASSWORD_LENGTH_WARNING",
                        $"Column '{table.Name}.{column.Name}' has length {length}, which might be too short for secure password hashing (BCrypt/PBKDF2 require at least 60 characters).",
                        "Change type to nvarchar(255) to support modern password hashing algorithms.",
                        table: table.Name,
                        column: column.Name,
                        canAutoFix: true,
                        fixAction: "Change type to nvarchar(255).");
                }
            }
        }
        else if (columnName.Contains("phone", StringComparison.OrdinalIgnoreCase) || columnName.Contains("telephone", StringComparison.OrdinalIgnoreCase) || columnName.Contains("mobile", StringComparison.OrdinalIgnoreCase))
        {
            var family = GetTypeFamily(sqlType);
            if (family == "int" || family == "long" || family == "decimal" || family == "floating")
            {
                AddIssue(
                    result,
                    "MEDIUM",
                    "LOGICAL_TYPE_MISMATCH",
                    $"Column '{table.Name}.{column.Name}' is named '{columnName}' but uses numeric type '{sqlType}'.",
                    "Phone numbers can contain leading zeros, country codes (+), or formatting (spaces, dashes). Store them as nvarchar(50).",
                    table: table.Name,
                    column: column.Name,
                    canAutoFix: true,
                    fixAction: "Change column type to nvarchar(50).");
            }
        }
        else if (columnName.EndsWith("date", StringComparison.OrdinalIgnoreCase) || columnName.EndsWith("at", StringComparison.Ordinal) || columnName.Contains("time", StringComparison.OrdinalIgnoreCase))
        {
            if (columnName.Equals("Format", StringComparison.OrdinalIgnoreCase) || columnName.Equals("Cat", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var isDateTimeFieldName = columnName.EndsWith("At", StringComparison.Ordinal) ||
                                      columnName.EndsWith("Date", StringComparison.OrdinalIgnoreCase) ||
                                      columnName.Contains("Created", StringComparison.OrdinalIgnoreCase) ||
                                      columnName.Contains("Updated", StringComparison.OrdinalIgnoreCase) ||
                                      columnName.Contains("Deleted", StringComparison.OrdinalIgnoreCase) ||
                                      columnName.Contains("Timestamp", StringComparison.OrdinalIgnoreCase);

            if (isDateTimeFieldName)
            {
                var family = GetTypeFamily(sqlType);
                if (family != "datetime")
                {
                    AddIssue(
                        result,
                        "MEDIUM",
                        "LOGICAL_TYPE_MISMATCH",
                        $"Column '{table.Name}.{column.Name}' is named '{columnName}' but is typed as '{sqlType}'.",
                        "Date or time columns should use datetime2 or datetime type.",
                        table: table.Name,
                        column: column.Name,
                        canAutoFix: true,
                        fixAction: "Change column type to datetime2.");
                }
            }
        }
        else if (columnName.StartsWith("is", StringComparison.OrdinalIgnoreCase) || columnName.StartsWith("has", StringComparison.OrdinalIgnoreCase))
        {
            var family = GetTypeFamily(sqlType);
            if (family != "bool")
            {
                AddIssue(
                    result,
                    "LOW",
                    "LOGICAL_TYPE_MISMATCH",
                    $"Column '{table.Name}.{column.Name}' starts with '{columnName[..2]}' but is typed as '{sqlType}'.",
                    "Boolean indicators should use bit or bool type.",
                    table: table.Name,
                    column: column.Name,
                    canAutoFix: true,
                    fixAction: "Change column type to bit.");
            }
        }
    }

    private static void ReviewImplicitForeignKey(SchemaTable table, SchemaColumn column, DatabaseSchema schema, AiReviewResult result)
    {
        if (column.IsPrimaryKey || !LooksLikeForeignKeyName(column.Name, table.Name, schema))
        {
            return;
        }

        var target = FindImplicitForeignKeyTarget(table, column, schema);
        if (target is null)
        {
            var impliedTableName = InferTableNameFromForeignKey(column.Name);
            AddIssue(
                result,
                "MEDIUM",
                "ORPHAN_FOREIGN_KEY",
                $"Column '{table.Name}.{column.Name}' looks like a foreign key, but the referenced table '{impliedTableName}' does not exist in the schema.",
                $"Create the table '{impliedTableName}' or rename this column if it does not represent a relationship.",
                table: table.Name,
                column: column.Name,
                canAutoFix: false);
            return;
        }

        AddIssue(
            result,
            "LOW",
            "INFERRED_FOREIGN_KEY",
            $"Column '{table.Name}.{column.Name}' looks like a foreign key to '{target.Value.TargetTable.Name}.{target.Value.TargetColumn.Name}' but no relationship is configured.",
            "Configure the relationship so the diagram, generated EF model, and SQL script all include the foreign key.",
            table: table.Name,
            column: column.Name,
            canAutoFix: true,
            fixAction: "Set the inferred foreign-key target table and column.");
    }

    private static void ReviewRelationshipCycles(DatabaseSchema schema, AiReviewResult result)
    {
        var relations = GetValidRelations(schema).ToList();
        foreach (var relation in relations)
        {
            if (relation.Table == relation.TargetTable && !relation.Column.IsNullable)
            {
                AddIssue(
                    result,
                    "MEDIUM",
                    "REQUIRED_SELF_REFERENCE",
                    $"Self-referencing foreign key '{relation.Table.Name}.{relation.Column.Name}' is required.",
                    "Make self-referencing relationships nullable unless every row can always point to an existing parent row.",
                    table: relation.Table.Name,
                    column: relation.Column.Name,
                    canAutoFix: true,
                    fixAction: "Make the self-referencing foreign key nullable.");
            }
        }

        var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relation in relations.Where(relation => !relation.Column.IsNullable))
        {
            var reverse = relations.FirstOrDefault(candidate =>
                !candidate.Column.IsNullable &&
                candidate.Table == relation.TargetTable &&
                candidate.TargetTable == relation.Table);

            if (reverse.Table is null)
            {
                continue;
            }

            var key = string.Compare(relation.Table.Name, relation.TargetTable.Name, StringComparison.OrdinalIgnoreCase) < 0
                ? $"{relation.Table.Name}->{relation.TargetTable.Name}"
                : $"{relation.TargetTable.Name}->{relation.Table.Name}";

            if (!seenPairs.Add(key))
            {
                continue;
            }

            AddIssue(
                result,
                "MEDIUM",
                "REQUIRED_RELATIONSHIP_CYCLE",
                $"Tables '{relation.Table.Name}' and '{relation.TargetTable.Name}' require each other through non-null foreign keys.",
                "Make one side nullable so records can be inserted without violating required relationship order.",
                table: relation.Table.Name,
                column: relation.Column.Name,
                canAutoFix: true,
                fixAction: "Make one side of the cycle nullable.");
        }
    }

    private static readonly (string[] ChildKeywords, string[] ParentKeywords, string DefaultFkName)[] SemanticPairs = new[]
    {
        // SanPham -> NhaCungCap
        (new[] { "SanPham", "Product" }, new[] { "NhaCungCap", "Supplier", "Vendor", "Provider" }, "MaNCC"),
        // SanPham -> DanhMuc
        (new[] { "SanPham", "Product" }, new[] { "DanhMuc", "Category", "Loai" }, "MaDM"),
        // HoaDon/DonHang -> KhachHang
        (new[] { "HoaDon", "DonHang", "Order", "Invoice" }, new[] { "KhachHang", "Customer", "Client" }, "MaKH"),
        // HoaDon/DonHang -> NhanVien
        (new[] { "HoaDon", "DonHang", "Order", "Invoice" }, new[] { "NhanVien", "Employee", "User" }, "MaNV"),
        // NhanVien -> PhongBan
        (new[] { "NhanVien", "Employee" }, new[] { "PhongBan", "Department" }, "MaPB")
    };

    private static bool AreTablesRelated(SchemaTable t1, SchemaTable t2, DatabaseSchema schema)
    {
        // 1. Direct relationship from t1 to t2
        if (t1.Columns.Any(c => string.Equals(c.ForeignKeyTable, t2.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 2. Direct relationship from t2 to t1
        if (t2.Columns.Any(c => string.Equals(c.ForeignKeyTable, t1.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 3. Indirect relationship via join table (N-N)
        foreach (var table in schema.Tables)
        {
            if (table == t1 || table == t2) continue;
            var hasFkToT1 = table.Columns.Any(c => string.Equals(c.ForeignKeyTable, t1.Name, StringComparison.OrdinalIgnoreCase));
            var hasFkToT2 = table.Columns.Any(c => string.Equals(c.ForeignKeyTable, t2.Name, StringComparison.OrdinalIgnoreCase));
            if (hasFkToT1 && hasFkToT2)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDetailTable(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return false;
        return tableName.StartsWith("ChiTiet", StringComparison.OrdinalIgnoreCase) ||
               tableName.EndsWith("Detail", StringComparison.OrdinalIgnoreCase) ||
               tableName.EndsWith("Details", StringComparison.OrdinalIgnoreCase) ||
               tableName.EndsWith("Item", StringComparison.OrdinalIgnoreCase) ||
               tableName.EndsWith("Items", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReviewSemanticRelationships(DatabaseSchema schema, AiReviewResult result)
    {
        // 1. Check dynamic ChiTiet[X] -> [X] relationships
        foreach (var childTable in schema.Tables.Where(t => IsDetailTable(t.Name)))
        {
            string? parentCandidate = null;
            string? defaultFkName = null;

            var name = childTable.Name ?? string.Empty;
            if (name.StartsWith("ChiTiet", StringComparison.OrdinalIgnoreCase) && name.Length > 7)
            {
                parentCandidate = name[7..]; // e.g. ChiTietSanPham -> SanPham
            }
            else if (name.EndsWith("Detail", StringComparison.OrdinalIgnoreCase) && name.Length > 6)
            {
                parentCandidate = name[..^6]; // e.g. OrderDetail -> Order
            }
            else if (name.EndsWith("Details", StringComparison.OrdinalIgnoreCase) && name.Length > 7)
            {
                parentCandidate = name[..^7]; // e.g. OrderDetails -> Order
            }
            else if (name.EndsWith("Item", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
            {
                parentCandidate = name[..^4]; // e.g. OrderItem -> Order
            }
            else if (name.EndsWith("Items", StringComparison.OrdinalIgnoreCase) && name.Length > 5)
            {
                parentCandidate = name[..^5]; // e.g. OrderItems -> Order
            }

            if (parentCandidate != null)
            {
                var parentTable = schema.Tables.FirstOrDefault(t => 
                    string.Equals(t.Name, parentCandidate, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Singularize(t.Name), parentCandidate, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Pluralize(t.Name), parentCandidate, StringComparison.OrdinalIgnoreCase));

                if (parentTable != null && !AreTablesRelated(childTable, parentTable, schema))
                {
                    var parentPk = parentTable.Columns.FirstOrDefault(c => c.IsPrimaryKey);
                    defaultFkName = parentPk?.Name ?? ("Ma" + GetInitials(parentTable.Name));

                    AddIssue(
                        result,
                        "HIGH",
                        "MISSING_RELATIONSHIP",
                        $"Table '{childTable.Name}' and Table '{parentTable.Name}' should be related, but no relationship is configured.",
                        $"Add a foreign key column '{defaultFkName}' to '{childTable.Name}' referencing '{parentTable.Name}'.",
                        table: childTable.Name,
                        column: defaultFkName,
                        canAutoFix: true,
                        fixAction: $"Add foreign key '{childTable.Name}.{defaultFkName}' referencing '{parentTable.Name}'.");
                }
            }
        }

        // 2. Check predefined semantic pairs
        foreach (var pair in SemanticPairs)
        {
            var childTables = schema.Tables.Where(t => 
                pair.ChildKeywords.Any(k => t.Name.Contains(k, StringComparison.OrdinalIgnoreCase)) &&
                !IsDetailTable(t.Name)
            ).ToList();
            var parentTables = schema.Tables.Where(t => pair.ParentKeywords.Any(k => t.Name.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();

            foreach (var childTable in childTables)
            {
                foreach (var parentTable in parentTables)
                {
                    if (childTable == parentTable) continue;

                    if (string.Equals(childTable.Name, parentTable.Name, StringComparison.OrdinalIgnoreCase)) continue;

                    if (!AreTablesRelated(childTable, parentTable, schema))
                    {
                        var parentPk = parentTable.Columns.FirstOrDefault(c => c.IsPrimaryKey);
                        var fkName = parentPk?.Name ?? pair.DefaultFkName;

                        AddIssue(
                            result,
                            "HIGH",
                            "MISSING_RELATIONSHIP",
                            $"Table '{childTable.Name}' and Table '{parentTable.Name}' should be related, but no relationship is configured.",
                            $"Add a foreign key column '{fkName}' to '{childTable.Name}' referencing '{parentTable.Name}'.",
                            table: childTable.Name,
                            column: fkName,
                            canAutoFix: true,
                            fixAction: $"Add foreign key '{childTable.Name}.{fkName}' referencing '{parentTable.Name}'.");
                    }
                }
            }
        }
    }

    private static bool AddMissingRelationship(DatabaseSchema schema, ReviewIssue issue)
    {
        var childTable = FindTable(schema, issue.Table);
        if (childTable is null || string.IsNullOrWhiteSpace(issue.Column))
        {
            return false;
        }

        var match = Regex.Match(issue.Message ?? string.Empty, @"Table '[^']+' and Table '([^']+)' should be related");
        if (!match.Success)
        {
            return false;
        }

        var parentTableName = match.Groups[1].Value;
        var parentTable = FindTable(schema, parentTableName);
        if (parentTable is null)
        {
            return false;
        }

        var parentPk = parentTable.Columns.FirstOrDefault(c => c.IsPrimaryKey)
            ?? parentTable.Columns.FirstOrDefault(c => string.Equals(c.Name, "Id", StringComparison.OrdinalIgnoreCase));
        if (parentPk is null)
        {
            return false;
        }

        var fkColumn = childTable.Columns.FirstOrDefault(c => string.Equals(c.Name, issue.Column, StringComparison.OrdinalIgnoreCase));
        if (fkColumn is null)
        {
            var sqlType = parentPk.SqlType ?? "int";
            if (sqlType.Contains("IDENTITY", StringComparison.OrdinalIgnoreCase))
            {
                sqlType = Regex.Replace(sqlType, @"\bIDENTITY\s*(\([^)]*\))?", "", RegexOptions.IgnoreCase).Trim();
            }
            if (string.IsNullOrWhiteSpace(sqlType))
            {
                sqlType = "int";
            }

            fkColumn = new SchemaColumn
            {
                Name = issue.Column,
                SqlType = sqlType,
                IsPrimaryKey = false,
                IsNullable = false,
                Order = childTable.Columns.Count
            };
            childTable.Columns.Add(fkColumn);
        }

        fkColumn.ForeignKeyTable = parentTable.Name;
        fkColumn.ForeignKeyColumn = parentPk.Name;
        return true;
    }

    private static int ApplyAllFixes(DatabaseSchema schema)
    {
        var fixedCount = 0;

        for (var pass = 0; pass < 8; pass++)
        {
            var issues = ReviewWithRules(schema).Issues
                .Where(issue => issue.CanAutoFix)
                .ToList();

            if (issues.Count == 0)
            {
                break;
            }

            var changed = false;
            foreach (var issue in issues)
            {
                if (TryApplyFix(schema, issue))
                {
                    fixedCount++;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return fixedCount;
    }

    private static bool ApplySingleFix(DatabaseSchema schema, ReviewIssue requestedIssue)
    {
        var currentIssue = ReviewWithRules(schema).Issues
            .FirstOrDefault(issue => MatchesIssue(issue, requestedIssue) && issue.CanAutoFix);

        return currentIssue is not null && TryApplyFix(schema, currentIssue);
    }

    private static bool TryApplyFix(DatabaseSchema schema, ReviewIssue issue)
    {
        switch (issue.Type.ToUpperInvariant())
        {
            case "EMPTY_SCHEMA":
                return AddStarterTable(schema);

            case "INVALID_PROJECT_NAME":
                return SetProjectName(schema, ToSafeIdentifier(schema.ProjectName, "GeneratedMvcApp"));

            case "DUPLICATE_TABLE":
            case "GENERATED_MODEL_NAME_COLLISION":
            case "EMPTY_TABLE_NAME":
            case "INVALID_TABLE_NAME":
            case "RESERVED_TABLE_NAME":
                return NormalizeTableNames(schema);

            case "EMPTY_TABLE":
                return AddPrimaryKeyToEmptyTable(schema, issue.Table);

            case "MISSING_PRIMARY_KEY":
                return AddPrimaryKey(schema, issue.Table);

            case "NULLABLE_PRIMARY_KEY":
                return UpdateColumn(schema, issue.Table, issue.Column, column =>
                {
                    if (!column.IsNullable)
                    {
                        return false;
                    }

                    column.IsNullable = false;
                    return true;
                });

            case "DUPLICATE_COLUMN":
            case "GENERATED_PROPERTY_NAME_COLLISION":
            case "EMPTY_COLUMN_NAME":
            case "INVALID_COLUMN_NAME":
            case "RESERVED_COLUMN_NAME":
                return NormalizeColumnNames(schema, issue.Table);

            case "DUPLICATE_COLUMN_ORDER":
                return ReorderColumns(schema, issue.Table);

            case "MISSING_SQL_TYPE":
            case "UNSUPPORTED_SQL_TYPE":
                return UpdateColumn(schema, issue.Table, issue.Column, column =>
                {
                    var inferred = InferSafeSqlType(schema, issue.Table, column);
                    if (string.Equals(column.SqlType, inferred, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    column.SqlType = inferred;
                    return true;
                });

            case "LEGACY_SQL_TYPE":
                return UpdateColumn(schema, issue.Table, issue.Column, column =>
                {
                    var replacement = IsImageType(column.SqlType) ? "varbinary(max)" : "nvarchar(max)";
                    if (string.Equals(column.SqlType, replacement, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    column.SqlType = replacement;
                    return true;
                });

            case "MISSING_STRING_LENGTH":
            case "INVALID_STRING_LENGTH":
                return UpdateColumn(schema, issue.Table, issue.Column, column =>
                {
                    if (string.Equals(column.SqlType, "nvarchar(255)", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    column.SqlType = "nvarchar(255)";
                    return true;
                });

            case "DECIMAL_WITHOUT_PRECISION":
                return UpdateColumn(schema, issue.Table, issue.Column, column =>
                {
                    if (string.Equals(column.SqlType, "decimal(18,2)", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    column.SqlType = "decimal(18,2)";
                    return true;
                });

            case "PK_UNSUITABLE_TYPE":
                return UpdateColumn(schema, issue.Table, issue.Column, column =>
                {
                    if (!string.Equals(column.Name, "Id", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(column.SqlType, "int", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    column.SqlType = "int";
                    return true;
                });

            case "FK_TARGET_TABLE_MISSING":
                return CreateMissingForeignKeyTable(schema, issue.Table, issue.Column);

            case "FK_TARGET_COLUMN_MISSING":
                return CreateMissingForeignKeyColumn(schema, issue.Table, issue.Column);

            case "FK_TYPE_MISMATCH":
                return AlignForeignKeyType(schema, issue.Table, issue.Column);

            case "FK_TARGET_NOT_UNIQUE":
                return MarkForeignKeyTargetUnique(schema, issue.Table, issue.Column);

            case "INFERRED_FOREIGN_KEY":
                return ApplyInferredForeignKey(schema, issue.Table, issue.Column);

            case "REQUIRED_SELF_REFERENCE":
            case "REQUIRED_RELATIONSHIP_CYCLE":
                return UpdateColumn(schema, issue.Table, issue.Column, column =>
                {
                    if (column.IsNullable)
                    {
                        return false;
                    }

                    column.IsNullable = true;
                    return true;
                });

            case "LOGICAL_TYPE_MISMATCH":
                return UpdateColumn(schema, issue.Table, issue.Column, column =>
                {
                    var name = column.Name?.Trim() ?? string.Empty;
                    var targetType = "nvarchar(255)";
                    if (name.Contains("email", StringComparison.OrdinalIgnoreCase) || name.Contains("password", StringComparison.OrdinalIgnoreCase))
                    {
                        targetType = "nvarchar(255)";
                    }
                    else if (name.Contains("phone", StringComparison.OrdinalIgnoreCase) || name.Contains("telephone", StringComparison.OrdinalIgnoreCase) || name.Contains("mobile", StringComparison.OrdinalIgnoreCase))
                    {
                        targetType = "nvarchar(50)";
                    }
                    else if (name.EndsWith("At", StringComparison.Ordinal) || name.EndsWith("Date", StringComparison.OrdinalIgnoreCase) || name.Contains("Created", StringComparison.OrdinalIgnoreCase) || name.Contains("Updated", StringComparison.OrdinalIgnoreCase) || name.Contains("Deleted", StringComparison.OrdinalIgnoreCase) || name.Contains("Timestamp", StringComparison.OrdinalIgnoreCase))
                    {
                        targetType = "datetime2";
                    }
                    else if (name.StartsWith("is", StringComparison.OrdinalIgnoreCase) || name.StartsWith("has", StringComparison.OrdinalIgnoreCase))
                    {
                        targetType = "bit";
                    }

                    if (string.Equals(column.SqlType, targetType, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    column.SqlType = targetType;
                    return true;
                });

            case "PASSWORD_LENGTH_WARNING":
                return UpdateColumn(schema, issue.Table, issue.Column, column =>
                {
                    if (string.Equals(column.SqlType, "nvarchar(255)", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    column.SqlType = "nvarchar(255)";
                    return true;
                });

            case "MISSING_RELATIONSHIP":
                return AddMissingRelationship(schema, issue);

            default:
                return false;
        }
    }

    private static bool AddStarterTable(DatabaseSchema schema)
    {
        if (schema.Tables.Count > 0)
        {
            return false;
        }

        schema.Tables.Add(new SchemaTable
        {
            Name = "Items",
            LastValidName = "Items",
            X = 80,
            Y = 80,
            Columns =
            {
                new SchemaColumn
                {
                    Name = "Id",
                    SqlType = "int",
                    IsPrimaryKey = true,
                    IsNullable = false,
                    Order = 0
                }
            }
        });
        return true;
    }

    private static bool SetProjectName(DatabaseSchema schema, string projectName)
    {
        if (string.Equals(schema.ProjectName, projectName, StringComparison.Ordinal))
        {
            return false;
        }

        schema.ProjectName = projectName;
        return true;
    }

    private static bool NormalizeTableNames(DatabaseSchema schema)
    {
        var changed = false;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in schema.Tables)
        {
            var originalName = table.Name ?? string.Empty;
            var fallbackName = string.IsNullOrWhiteSpace(table.LastValidName)
                ? "Table"
                : ToSafeIdentifier(table.LastValidName, "Table");
            var baseName = string.IsNullOrWhiteSpace(originalName)
                ? fallbackName
                : ToSafeIdentifier(originalName, fallbackName);
            if (ReservedNames.Contains(baseName))
            {
                baseName += "Table";
            }

            var uniqueName = MakeUnique(baseName, used);
            if (!string.Equals(originalName, uniqueName, StringComparison.Ordinal))
            {
                RenameTable(schema, table, uniqueName);
                changed = true;
            }
            else if (!string.Equals(table.LastValidName, uniqueName, StringComparison.Ordinal))
            {
                table.LastValidName = uniqueName;
                changed = true;
            }
        }

        return changed;
    }

    private static bool NormalizeColumnNames(DatabaseSchema schema, string? tableName)
    {
        var table = FindTable(schema, tableName);
        if (table is null)
        {
            return false;
        }

        var changed = false;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in table.Columns.OrderBy(column => column.Order))
        {
            var originalName = column.Name ?? string.Empty;
            var baseName = ToSafeIdentifier(originalName, "Column");
            if (ReservedNames.Contains(baseName))
            {
                baseName += "Column";
            }

            var uniqueName = MakeUnique(baseName, used);
            if (!string.Equals(originalName, uniqueName, StringComparison.Ordinal))
            {
                RenameColumn(schema, table, column, uniqueName);
                changed = true;
            }
        }

        return changed;
    }

    private static bool AddPrimaryKeyToEmptyTable(DatabaseSchema schema, string? tableName)
    {
        var table = FindTable(schema, tableName);
        if (table is null || table.Columns.Count > 0)
        {
            return false;
        }

        table.Columns.Add(new SchemaColumn
        {
            Name = "Id",
            SqlType = "int",
            IsPrimaryKey = true,
            IsNullable = false,
            Order = 0
        });
        return true;
    }

    private static bool AddPrimaryKey(DatabaseSchema schema, string? tableName)
    {
        var table = FindTable(schema, tableName);
        if (table is null || table.Columns.Any(column => column.IsPrimaryKey))
        {
            return false;
        }

        // Find existing candidate columns in order of preference:
        // 1. "Id"
        // 2. TableName + "Id" (e.g., "DanhMucId" in "DanhMuc")
        // 3. "Ma" + TableName (e.g., "MaDanhMuc" in "DanhMuc")
        // 4. "Ma" + GetInitials(TableName) (e.g., "MaDM" in "DanhMuc")
        // 5. Any column that looks like a foreign key/identifier (starts with "Ma" + uppercase, or ends with "Id")
        var initials = GetInitials(table.Name);
        var pkCandidate = table.Columns.FirstOrDefault(column => 
            string.Equals(column.Name, "Id", StringComparison.OrdinalIgnoreCase))
            ?? table.Columns.FirstOrDefault(column => 
                string.Equals(column.Name, table.Name + "Id", StringComparison.OrdinalIgnoreCase))
            ?? table.Columns.FirstOrDefault(column => 
                string.Equals(column.Name, "Ma" + table.Name, StringComparison.OrdinalIgnoreCase))
            ?? table.Columns.FirstOrDefault(column => 
                !string.IsNullOrEmpty(initials) && string.Equals(column.Name, "Ma" + initials, StringComparison.OrdinalIgnoreCase))
            ?? table.Columns.FirstOrDefault(column => 
                column.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) || IsVietnameseForeignKeyPrefix(column.Name));

        if (pkCandidate is not null)
        {
            pkCandidate.IsPrimaryKey = true;
            pkCandidate.IsNullable = false;
            pkCandidate.IsUnique = false;
            if (string.IsNullOrWhiteSpace(pkCandidate.SqlType))
            {
                pkCandidate.SqlType = "int";
            }

            return true;
        }

        foreach (var column in table.Columns)
        {
            column.Order++;
        }

        table.Columns.Insert(0, new SchemaColumn
        {
            Name = MakeUniqueColumnName(table, "Id"),
            SqlType = "int",
            IsPrimaryKey = true,
            IsNullable = false,
            Order = 0
        });
        return true;
    }

    private static bool ReorderColumns(DatabaseSchema schema, string? tableName)
    {
        var table = FindTable(schema, tableName);
        if (table is null)
        {
            return false;
        }

        var changed = false;
        var index = 0;
        foreach (var column in table.Columns.OrderBy(column => column.Order).ThenBy(column => column.Name).ToList())
        {
            if (column.Order != index)
            {
                column.Order = index;
                changed = true;
            }

            index++;
        }

        return changed;
    }

    private static bool CreateMissingForeignKeyTable(DatabaseSchema schema, string? tableName, string? columnName)
    {
        var table = FindTable(schema, tableName);
        var column = FindColumn(table, columnName);
        if (table is null || column is null)
        {
            return false;
        }

        var requestedTarget = string.IsNullOrWhiteSpace(column.ForeignKeyTable)
            ? InferTableNameFromForeignKey(column.Name)
            : column.ForeignKeyTable!;

        if (string.IsNullOrWhiteSpace(requestedTarget))
        {
            return false;
        }

        var unnamedTarget = schema.Tables.FirstOrDefault(candidate =>
            string.IsNullOrWhiteSpace(candidate.Name) &&
            string.Equals(candidate.LastValidName, requestedTarget, StringComparison.OrdinalIgnoreCase));
        if (unnamedTarget is not null)
        {
            var restoredName = MakeUniqueTableName(schema, ToSafeIdentifier(unnamedTarget.LastValidName, "ReferencedTable"));
            RenameTable(schema, unnamedTarget, restoredName);
            column.ForeignKeyTable = restoredName;
            if (string.IsNullOrWhiteSpace(column.ForeignKeyColumn))
            {
                column.ForeignKeyColumn = unnamedTarget.Columns.FirstOrDefault(candidate => candidate.IsPrimaryKey)?.Name
                    ?? unnamedTarget.Columns.FirstOrDefault()?.Name
                    ?? "Id";
            }

            return true;
        }

        var safeTargetName = MakeUniqueTableName(schema, ToSafeIdentifier(requestedTarget, "ReferencedTable"));
        var targetColumnName = string.IsNullOrWhiteSpace(column.ForeignKeyColumn)
            ? "Id"
            : ToSafeIdentifier(column.ForeignKeyColumn!, "Id");

        schema.Tables.Add(new SchemaTable
        {
            Name = safeTargetName,
            LastValidName = safeTargetName,
            X = table.X + 380,
            Y = table.Y,
            Columns =
            {
                new SchemaColumn
                {
                    Name = targetColumnName,
                    SqlType = string.IsNullOrWhiteSpace(column.SqlType) ? "int" : column.SqlType,
                    IsPrimaryKey = true,
                    IsNullable = false,
                    Order = 0
                }
            }
        });

        column.ForeignKeyTable = safeTargetName;
        column.ForeignKeyColumn = targetColumnName;
        return true;
    }

    private static bool CreateMissingForeignKeyColumn(DatabaseSchema schema, string? tableName, string? columnName)
    {
        var table = FindTable(schema, tableName);
        var column = FindColumn(table, columnName);
        var targetTable = column is null || string.IsNullOrWhiteSpace(column.ForeignKeyTable)
            ? null
            : FindTable(schema, column.ForeignKeyTable);

        if (table is null || column is null || targetTable is null)
        {
            return false;
        }

        var targetColumnName = MakeUniqueColumnName(targetTable, string.IsNullOrWhiteSpace(column.ForeignKeyColumn) ? "Id" : column.ForeignKeyColumn!);
        var targetHasPrimaryKey = targetTable.Columns.Any(candidate => candidate.IsPrimaryKey);
        targetTable.Columns.Add(new SchemaColumn
        {
            Name = targetColumnName,
            SqlType = string.IsNullOrWhiteSpace(column.SqlType) ? "int" : column.SqlType,
            IsPrimaryKey = !targetHasPrimaryKey && string.Equals(targetColumnName, "Id", StringComparison.OrdinalIgnoreCase),
            IsNullable = false,
            IsUnique = targetHasPrimaryKey,
            Order = targetTable.Columns.Count
        });
        column.ForeignKeyColumn = targetColumnName;
        return true;
    }

    private static bool AlignForeignKeyType(DatabaseSchema schema, string? tableName, string? columnName)
    {
        var relation = FindRelation(schema, tableName, columnName);
        if (relation is null || string.Equals(relation.Value.Column.SqlType, relation.Value.TargetColumn.SqlType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        relation.Value.Column.SqlType = relation.Value.TargetColumn.SqlType;
        return true;
    }

    private static bool MarkForeignKeyTargetUnique(DatabaseSchema schema, string? tableName, string? columnName)
    {
        var relation = FindRelation(schema, tableName, columnName);
        if (relation is null || relation.Value.TargetColumn.IsPrimaryKey || relation.Value.TargetColumn.IsUnique)
        {
            return false;
        }

        relation.Value.TargetColumn.IsUnique = true;
        return true;
    }

    private static bool ApplyInferredForeignKey(DatabaseSchema schema, string? tableName, string? columnName)
    {
        var table = FindTable(schema, tableName);
        var column = FindColumn(table, columnName);
        if (table is null || column is null)
        {
            return false;
        }

        var target = FindImplicitForeignKeyTarget(table, column, schema);
        if (target is null)
        {
            return false;
        }

        column.ForeignKeyTable = target.Value.TargetTable.Name;
        column.ForeignKeyColumn = target.Value.TargetColumn.Name;
        return true;
    }

    private static bool UpdateColumn(DatabaseSchema schema, string? tableName, string? columnName, Func<SchemaColumn, bool> update)
    {
        var column = FindColumn(FindTable(schema, tableName), columnName);
        return column is not null && update(column);
    }

    private static void RenameTable(DatabaseSchema schema, SchemaTable table, string newName)
    {
        var oldName = table.Name;
        table.Name = newName;
        table.LastValidName = newName;

        if (string.IsNullOrWhiteSpace(oldName))
        {
            return;
        }

        foreach (var column in schema.Tables.SelectMany(candidate => candidate.Columns))
        {
            if (string.Equals(column.ForeignKeyTable, oldName, StringComparison.OrdinalIgnoreCase))
            {
                column.ForeignKeyTable = newName;
            }
        }
    }

    private static void RenameColumn(DatabaseSchema schema, SchemaTable table, SchemaColumn column, string newName)
    {
        var oldName = column.Name;
        column.Name = newName;

        foreach (var candidate in schema.Tables.SelectMany(item => item.Columns))
        {
            if (string.Equals(candidate.ForeignKeyTable, table.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ForeignKeyColumn, oldName, StringComparison.OrdinalIgnoreCase))
            {
                candidate.ForeignKeyColumn = newName;
            }
        }
    }

    private static void NormalizeSchemaIdentity(DatabaseSchema schema)
    {
        if (string.IsNullOrWhiteSpace(schema.ProjectName))
        {
            schema.ProjectName = "GeneratedMvcApp";
        }

        foreach (var table in schema.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.Id))
            {
                table.Id = Guid.NewGuid().ToString("N");
            }

            if (!string.IsNullOrWhiteSpace(table.Name))
            {
                table.LastValidName = table.Name;
            }

            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                if (string.IsNullOrWhiteSpace(column.Id))
                {
                    column.Id = Guid.NewGuid().ToString("N");
                }

                if (table.Columns.Count == 1 || table.Columns.Count(columnItem => columnItem.Order == column.Order) > 1)
                {
                    column.Order = index;
                }
            }
        }
    }

    private static DatabaseSchema CloneSchema(DatabaseSchema schema)
    {
        var json = JsonSerializer.Serialize(schema);
        return JsonSerializer.Deserialize<DatabaseSchema>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DatabaseSchema();
    }

    private static bool MatchesIssue(ReviewIssue left, ReviewIssue right)
    {
        return string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Table, right.Table, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Column, right.Column, StringComparison.OrdinalIgnoreCase);
    }

    private static (SchemaTable Table, SchemaColumn Column, SchemaTable TargetTable, SchemaColumn TargetColumn)? FindRelation(DatabaseSchema schema, string? tableName, string? columnName)
    {
        var table = FindTable(schema, tableName);
        var column = FindColumn(table, columnName);
        if (table is null || column is null || string.IsNullOrWhiteSpace(column.ForeignKeyTable))
        {
            return null;
        }

        var targetTable = FindTable(schema, column.ForeignKeyTable);
        var targetColumnName = string.IsNullOrWhiteSpace(column.ForeignKeyColumn) ? "Id" : column.ForeignKeyColumn!;
        var targetColumn = FindColumn(targetTable, targetColumnName);
        return targetTable is null || targetColumn is null ? null : (table, column, targetTable, targetColumn);
    }

    private static IEnumerable<(SchemaTable Table, SchemaColumn Column, SchemaTable TargetTable)> GetValidRelations(DatabaseSchema schema)
    {
        foreach (var table in schema.Tables)
        {
            foreach (var column in table.Columns.Where(column => !string.IsNullOrWhiteSpace(column.ForeignKeyTable)))
            {
                var targetTable = FindTable(schema, column.ForeignKeyTable);
                if (targetTable is not null)
                {
                    yield return (table, column, targetTable);
                }
            }
        }
    }

    private static SchemaTable? FindTable(DatabaseSchema schema, string? tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return null;
        }

        return schema.Tables.FirstOrDefault(table => string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase))
            ?? schema.Tables.FirstOrDefault(table =>
                string.IsNullOrWhiteSpace(table.Name) &&
                string.Equals(table.LastValidName, tableName, StringComparison.OrdinalIgnoreCase));
    }

    private static SchemaColumn? FindColumn(SchemaTable? table, string? columnName)
    {
        return table?.Columns.FirstOrDefault(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase));
    }

    private static string MakeUniqueTableName(DatabaseSchema schema, string candidate)
    {
        var used = schema.Tables.Select(table => table.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return MakeUnique(candidate, used);
    }

    private static string MakeUniqueColumnName(SchemaTable table, string candidate)
    {
        var used = table.Columns.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return MakeUnique(ToSafeIdentifier(candidate, "Column"), used);
    }

    private static string MakeUnique(string candidate, HashSet<string> used)
    {
        var unique = string.IsNullOrWhiteSpace(candidate) ? "Item" : candidate;
        var suffix = 2;

        while (!used.Add(unique))
        {
            unique = candidate + suffix++;
        }

        return unique;
    }

    private static string InferSafeSqlType(DatabaseSchema schema, string? tableName, SchemaColumn column)
    {
        var relation = FindRelation(schema, tableName, column.Name);
        if (relation is not null)
        {
            return relation.Value.TargetColumn.SqlType;
        }

        if (column.IsPrimaryKey || 
            string.Equals(column.Name, "Id", StringComparison.OrdinalIgnoreCase) || 
            LooksLikeForeignKeyName(column.Name, tableName, schema) ||
            (tableName != null && (
                string.Equals(column.Name, tableName + "Id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(column.Name, "Ma" + tableName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(column.Name, "Ma" + GetInitials(tableName), StringComparison.OrdinalIgnoreCase)
            )))
        {
            return "int";
        }

        return "nvarchar(255)";
    }

    private static string InferTableNameFromForeignKey(string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return string.Empty;
        }

        var cleanName = columnName;
        if (cleanName.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
        {
            cleanName = cleanName[..^2];
        }
        if (cleanName.EndsWith("_", StringComparison.Ordinal))
        {
            cleanName = cleanName[..^1];
        }
        if (IsVietnameseForeignKeyPrefix(cleanName))
        {
            cleanName = cleanName[2..];
        }
        if (cleanName.StartsWith("_", StringComparison.Ordinal))
        {
            cleanName = cleanName[1..];
        }

        return Pluralize(cleanName);
    }

    private static (SchemaTable TargetTable, SchemaColumn TargetColumn)? FindImplicitForeignKeyTarget(SchemaTable sourceTable, SchemaColumn column, DatabaseSchema schema)
    {
        if (!LooksLikeForeignKeyName(column.Name, sourceTable.Name, schema))
        {
            return null;
        }

        // 1. First search for a matching primary key name in another table (e.g. MaNCC in SanPham matches MaNCC in NhaCungCap)
        foreach (var table in schema.Tables)
        {
            if (table == sourceTable) continue;
            var targetCol = table.Columns.FirstOrDefault(c => c.IsPrimaryKey && string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase));
            if (targetCol is not null)
            {
                return (table, targetCol);
            }
        }

        // 2. Next, strip standard prefixes/suffixes and search by table name/abbreviation
        var cleanName = column.Name;
        if (cleanName.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
        {
            cleanName = cleanName[..^2];
        }
        if (cleanName.EndsWith("_", StringComparison.Ordinal))
        {
            cleanName = cleanName[..^1];
        }
        if (IsVietnameseForeignKeyPrefix(cleanName))
        {
            cleanName = cleanName[2..];
        }
        if (cleanName.StartsWith("_", StringComparison.Ordinal))
        {
            cleanName = cleanName[1..];
        }

        if (string.IsNullOrWhiteSpace(cleanName))
        {
            return null;
        }

        var targetTable = schema.Tables.FirstOrDefault(table =>
        {
            if (table == sourceTable) return false;

            var tableName = table.Name ?? string.Empty;
            var singular = Singularize(tableName);
            var plural = Pluralize(tableName);

            if (string.Equals(tableName, cleanName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(singular, cleanName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plural, cleanName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var initials = GetInitials(tableName);
            if (string.Equals(initials, cleanName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        });

        if (targetTable is null)
        {
            return null;
        }

        var targetColumn = targetTable.Columns.FirstOrDefault(candidate => candidate.IsPrimaryKey)
            ?? targetTable.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, "Id", StringComparison.OrdinalIgnoreCase))
            ?? targetTable.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, "Ma" + targetTable.Name, StringComparison.OrdinalIgnoreCase))
            ?? targetTable.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, "Ma" + GetInitials(targetTable.Name), StringComparison.OrdinalIgnoreCase));

        if (targetColumn is null)
        {
            return null;
        }

        return (targetTable, targetColumn);
    }

    private static bool LooksLikeForeignKeyName(string? columnName, string? currentTableName, DatabaseSchema schema)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return false;

        // If the column name matches the own-table primary key patterns, it's not a foreign key
        if (!string.IsNullOrWhiteSpace(currentTableName))
        {
            var initials = GetInitials(currentTableName);
            if (string.Equals(columnName, "Id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(columnName, currentTableName + "Id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(columnName, "Ma" + currentTableName, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(initials) && string.Equals(columnName, "Ma" + initials, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // 1. Standard English EndsWith("Id")
        if (columnName.Length > 2 && columnName.EndsWith("Id", StringComparison.OrdinalIgnoreCase) && !string.Equals(columnName, "Id", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Vietnamese "Ma" + uppercase abbreviation (e.g. "MaNCC", "MaDM", "MaKH") but NOT "MauSac", "MaTroi"
        if (IsVietnameseForeignKeyPrefix(columnName))
        {
            return true;
        }

        // 3. Exact match with a primary key in another table
        if (schema != null)
        {
            foreach (var table in schema.Tables)
            {
                if (string.Equals(table.Name, currentTableName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hasMatchingPk = table.Columns.Any(col =>
                    col.IsPrimaryKey &&
                    string.Equals(col.Name, columnName, StringComparison.OrdinalIgnoreCase));

                if (hasMatchingPk)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetInitials(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var initials = new string(value.Where(char.IsUpper).ToArray());
        if (string.IsNullOrEmpty(initials))
        {
            return value[0].ToString().ToUpperInvariant();
        }
        return initials.ToUpperInvariant();
    }

    private static bool IsVietnameseForeignKeyPrefix(string? columnName)
    {
        // Vietnamese FK convention: "Ma" followed by an UPPERCASE letter
        // e.g. MaDM ✅, MaNCC ✅, MaSP ✅, MaKH ✅
        // but  MauSac ❌, MaTroi ❌ (lowercase after Ma = regular word)
        return columnName is not null &&
               columnName.Length > 2 &&
               columnName.StartsWith("Ma", StringComparison.Ordinal) &&
               char.IsUpper(columnName[2]);
    }

    private static bool AreTypesCompatible(string left, string right)
    {
        return GetTypeFamily(left) == GetTypeFamily(right);
    }

    private static string GetTypeFamily(string sqlType)
    {
        var normalized = (sqlType ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized.Contains("uniqueidentifier") || normalized.Contains("uuid")) return "guid";
        if (normalized.Contains("bigint") || normalized.Contains("bigserial")) return "long";
        if (normalized.Contains("tinyint") || normalized.Contains("smallint") || normalized.Contains("int") || normalized.Contains("serial")) return "int";
        if (normalized.Contains("char") || normalized.Contains("text") || normalized.Contains("json") || normalized.Contains("xml")) return "string";
        if (normalized.Contains("bit") || normalized.Contains("bool")) return "bool";
        if (normalized.Contains("decimal") || normalized.Contains("numeric") || normalized.Contains("money")) return "decimal";
        if (normalized.Contains("float") || normalized.Contains("double") || normalized.Contains("real")) return "floating";
        if (normalized.Contains("date") || normalized.Contains("time")) return "datetime";
        if (normalized.Contains("binary") || normalized.Contains("blob") || normalized.Contains("bytea") || normalized.Contains("image")) return "binary";

        return normalized;
    }

    private static bool IsKnownSqlType(string sqlType)
    {
        var baseType = GetBaseSqlType(sqlType);
        return baseType is "bigint" or "binary" or "bit" or "char" or "date" or "datetime" or "datetime2"
            or "datetimeoffset" or "decimal" or "float" or "image" or "int" or "money" or "nchar"
            or "ntext" or "numeric" or "nvarchar" or "real" or "smalldatetime" or "smallint"
            or "smallmoney" or "text" or "time" or "tinyint" or "uniqueidentifier" or "varbinary"
            or "varchar" or "xml" or "uuid" or "boolean" or "bool" or "serial" or "bigserial"
            or "integer" or "json" or "jsonb" or "double precision";
    }

    private static string GetBaseSqlType(string sqlType)
    {
        var type = Regex.Replace((sqlType ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
        type = Regex.Replace(type, @"\s*\(.*\)", string.Empty).Trim();
        return type;
    }

    private static bool IsLegacySqlType(string sqlType)
    {
        var baseType = GetBaseSqlType(sqlType);
        return baseType is "text" or "ntext" or "image";
    }

    private static bool IsImageType(string sqlType)
    {
        return string.Equals(GetBaseSqlType(sqlType), "image", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStringType(string sqlType)
    {
        var baseType = GetBaseSqlType(sqlType);
        return baseType is "char" or "varchar" or "nchar" or "nvarchar";
    }

    private static bool IsDecimalType(string sqlType)
    {
        var baseType = GetBaseSqlType(sqlType);
        return baseType is "decimal" or "numeric";
    }

    private static bool HasPrecision(string sqlType)
    {
        return Regex.IsMatch(sqlType ?? string.Empty, @"\(\s*\d+\s*,\s*\d+\s*\)");
    }

    private static bool HasStringLength(string sqlType)
    {
        return Regex.IsMatch(sqlType ?? string.Empty, @"\(\s*\d+\s*\)");
    }

    private static bool IsMaxLength(string sqlType)
    {
        return Regex.IsMatch(sqlType ?? string.Empty, @"\(\s*max\s*\)", RegexOptions.IgnoreCase);
    }

    private static int? TryReadStringLength(string sqlType)
    {
        var match = Regex.Match(sqlType ?? string.Empty, @"\(\s*(-?\d+)\s*\)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static bool IsBadPrimaryKeyType(string sqlType)
    {
        var family = GetTypeFamily(sqlType);
        if (family is "guid" or "long" or "int")
        {
            return false;
        }

        if (family == "string")
        {
            var length = TryReadStringLength(sqlType);
            return IsMaxLength(sqlType) || length is null or <= 0 or > 450;
        }

        return true;
    }

    private static bool IsSafeIdentifier(string value)
    {
        return Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");
    }

    private static string ToSafeIdentifier(string value, string fallback)
    {
        var safe = Regex.Replace(string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(), @"[^A-Za-z0-9_]+", "_").Trim('_');
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

    private static string ToGeneratedName(string value, string fallback)
    {
        var parts = Regex.Split(value ?? string.Empty, @"[^A-Za-z0-9]+")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        var result = parts.Count == 0
            ? fallback
            : string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + (part.Length > 1 ? part[1..] : string.Empty)));

        if (char.IsDigit(result[0]))
        {
            result = fallback + result;
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

    private static void AddIssue(
        AiReviewResult result,
        string severity,
        string type,
        string message,
        string suggestion,
        string? table = null,
        string? column = null,
        bool canAutoFix = false,
        string fixAction = "")
    {
        result.Issues.Add(new ReviewIssue
        {
            Severity = severity,
            Type = type,
            Table = table,
            Column = column,
            Message = message,
            Suggestion = suggestion,
            CanAutoFix = canAutoFix,
            FixAction = fixAction
        });
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

        var prompt = "You are reviewing a database schema for an ASP.NET Core MVC + SQL Server project. " +
                     "Do not review UI, APIs, documentation, security policies, or unrelated product ideas. " +
                     "Return strict JSON matching this shape: {\"summary\":string,\"source\":string,\"issues\":[{\"severity\":\"HIGH|MEDIUM|LOW\",\"type\":string,\"table\":string|null,\"column\":string|null,\"message\":string,\"suggestion\":string,\"canAutoFix\":false,\"fixAction\":string}]}. " +
                     "Only set canAutoFix to false for issues you add yourself; deterministic issues below already include fix metadata. " +
                     "Focus on primary keys, foreign keys, relationship direction, type compatibility, nullable relationship columns, duplicate names, SQL Server type correctness, generated Entity Framework model correctness, and insert-order relationship cycles. " +
                     "CRITICAL: Look carefully for missing or deleted relationships. If a table contains a column ending in 'Id', 'ID', or '_id' (like 'userId', 'user_id') but it has no foreign key relationship configured, or if the relationship points to a table that has been deleted or does not exist, flag it as an issue. " +
                     "Also detect logical/semantic errors in columns (e.g. 'Email' should be string type, 'Password' hashes should be string/binary with sufficient length, 'Phone' should be string to preserve formatting, timestamps/date fields should be datetime/datetime2, boolean flags should be bit/bool), and suggest recommendations to fix them.\n\n" +
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
