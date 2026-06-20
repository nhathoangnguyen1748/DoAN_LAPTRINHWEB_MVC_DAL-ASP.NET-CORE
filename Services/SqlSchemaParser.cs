using System.Text;
using System.Text.RegularExpressions;
using DoAnLapTrinhWeb.Models.Designer;

namespace DoAnLapTrinhWeb.Services;

public sealed class SqlSchemaParser
{
    private static readonly HashSet<string> ColumnStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONSTRAINT", "PRIMARY", "FOREIGN", "REFERENCES", "UNIQUE", "NULL", "NOT", "DEFAULT",
        "CHECK", "COLLATE", "GENERATED", "IDENTITY", "AUTO_INCREMENT", "COMMENT", "ON", "ENABLE"
    };

    private static readonly HashSet<string> TableConstraintWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "KEY", "CONSTRAINT"
    };

    public DatabaseSchema Parse(string sql, string? projectName = null)
    {
        var schema = new DatabaseSchema
        {
            ProjectName = NormalizeProjectName(projectName)
        };

        if (string.IsNullOrWhiteSpace(sql))
        {
            return schema;
        }

        var cleaned = StripComments(sql);
        var blocks = FindCreateTableBlocks(cleaned);

        foreach (var (rawTableName, body) in blocks)
        {
            var table = new SchemaTable
            {
                Name = NormalizeIdentifier(GetLastIdentifierPart(rawTableName))
            };
            table.LastValidName = table.Name;

            var definitions = SplitTopLevel(body, ',');
            var pendingPrimaryKeys = new List<string>();
            var pendingForeignKeys = new List<(List<string> LocalColumns, string TargetTable, List<string> TargetColumns)>();
            var pendingUniqueKeys = new List<string>();
            var order = 0;

            foreach (var definition in definitions.Select(item => item.Trim()).Where(item => item.Length > 0))
            {
                var normalizedDefinition = StripConstraintName(definition).Trim();
                var firstWord = ReadFirstKeyword(normalizedDefinition);

                if (TableConstraintWords.Contains(firstWord))
                {
                    if (StartsWithKeyword(normalizedDefinition, "PRIMARY"))
                    {
                        pendingPrimaryKeys.AddRange(ReadColumnsFromFirstParentheses(normalizedDefinition));
                    }
                    else if (StartsWithKeyword(normalizedDefinition, "FOREIGN"))
                    {
                        var foreignKey = ParseTableForeignKey(normalizedDefinition);
                        if (foreignKey.LocalColumns.Count > 0 && !string.IsNullOrWhiteSpace(foreignKey.TargetTable))
                        {
                            pendingForeignKeys.Add(foreignKey);
                        }
                    }
                    else if (StartsWithKeyword(normalizedDefinition, "UNIQUE"))
                    {
                        pendingUniqueKeys.AddRange(ReadColumnsFromFirstParentheses(normalizedDefinition));
                    }

                    continue;
                }

                var column = ParseColumnDefinition(definition, order++);
                if (!string.IsNullOrWhiteSpace(column.Name))
                {
                    table.Columns.Add(column);
                }
            }

            foreach (var primaryKey in pendingPrimaryKeys)
            {
                var column = FindColumn(table, primaryKey);
                if (column is not null)
                {
                    column.IsPrimaryKey = true;
                    column.IsNullable = false;
                }
            }

            foreach (var uniqueKey in pendingUniqueKeys)
            {
                var column = FindColumn(table, uniqueKey);
                if (column is not null)
                {
                    column.IsUnique = true;
                }
            }

            foreach (var foreignKey in pendingForeignKeys)
            {
                for (var index = 0; index < foreignKey.LocalColumns.Count; index++)
                {
                    var localColumn = FindColumn(table, foreignKey.LocalColumns[index]);
                    if (localColumn is null)
                    {
                        continue;
                    }

                    localColumn.ForeignKeyTable = NormalizeIdentifier(GetLastIdentifierPart(foreignKey.TargetTable));
                    localColumn.ForeignKeyColumn = index < foreignKey.TargetColumns.Count
                        ? NormalizeIdentifier(foreignKey.TargetColumns[index])
                        : "id";
                }
            }

            if (table.Columns.Count > 0)
            {
                schema.Tables.Add(table);
            }
        }

        ApplyReadableLayout(schema);
        return schema;
    }

    private static SchemaColumn ParseColumnDefinition(string definition, int order)
    {
        var columnName = ExtractQualifiedIdentifier(definition, 0, out var nameEnd);
        var rest = nameEnd < definition.Length ? definition[nameEnd..].Trim() : string.Empty;
        var sqlType = ReadSqlType(rest);
        var references = ParseReferencesClause(rest);

        return new SchemaColumn
        {
            Name = NormalizeIdentifier(GetLastIdentifierPart(columnName)),
            SqlType = string.IsNullOrWhiteSpace(sqlType) ? "nvarchar(255)" : NormalizeSqlType(sqlType),
            IsPrimaryKey = Regex.IsMatch(rest, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase),
            IsNullable = !Regex.IsMatch(rest, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(rest, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase),
            IsUnique = Regex.IsMatch(rest, @"\bUNIQUE\b", RegexOptions.IgnoreCase),
            ForeignKeyTable = references.TargetTable,
            ForeignKeyColumn = references.TargetColumn,
            Order = order
        };
    }

    private static (List<string> LocalColumns, string TargetTable, List<string> TargetColumns) ParseTableForeignKey(string definition)
    {
        var localColumns = ReadColumnsFromFirstParentheses(definition);
        var references = ParseReferencesClause(definition);
        var targetColumns = string.IsNullOrWhiteSpace(references.TargetColumn)
            ? new List<string> { "id" }
            : new List<string> { references.TargetColumn };

        var referenceIndex = IndexOfKeyword(definition, "REFERENCES");
        if (referenceIndex >= 0)
        {
            var afterReferences = referenceIndex + "REFERENCES".Length;
            _ = ExtractQualifiedIdentifier(definition, afterReferences, out var afterTableIndex);
            var columnText = afterTableIndex < definition.Length ? definition[afterTableIndex..] : string.Empty;
            var columns = ReadColumnsFromFirstParentheses(columnText);
            if (columns.Count > 0)
            {
                targetColumns = columns;
            }
        }

        return (localColumns, references.TargetTable ?? string.Empty, targetColumns);
    }

    private static (string? TargetTable, string? TargetColumn) ParseReferencesClause(string text)
    {
        var referencesIndex = IndexOfKeyword(text, "REFERENCES");
        if (referencesIndex < 0)
        {
            return (null, null);
        }

        var tableName = ExtractQualifiedIdentifier(text, referencesIndex + "REFERENCES".Length, out var tableEnd);
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return (null, null);
        }

        var remaining = tableEnd < text.Length ? text[tableEnd..] : string.Empty;
        var columns = ReadColumnsFromFirstParentheses(remaining);

        return (
            NormalizeIdentifier(GetLastIdentifierPart(tableName)),
            columns.Count > 0 ? NormalizeIdentifier(columns[0]) : "id"
        );
    }

    private static List<(string TableName, string Body)> FindCreateTableBlocks(string sql)
    {
        var blocks = new List<(string TableName, string Body)>();
        var createTablePattern = new Regex(@"\bCREATE\s+TABLE\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var match = createTablePattern.Match(sql);

        while (match.Success)
        {
            var cursor = match.Index + match.Length;
            cursor = SkipWhitespace(sql, cursor);

            var remainingSql = sql[cursor..];
            var ifNotExistsMatch = Regex.Match(remainingSql, @"^IF\s+NOT\s+EXISTS\b", RegexOptions.IgnoreCase);
            if (ifNotExistsMatch.Success)
            {
                cursor += ifNotExistsMatch.Length;
            }

            var tableName = ExtractQualifiedIdentifier(sql, cursor, out var tableNameEnd);
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                var openIndex = SkipWhitespace(sql, tableNameEnd);
                if (openIndex < sql.Length && sql[openIndex] == '(')
                {
                    var closeIndex = FindMatchingParenthesis(sql, openIndex);
                    if (closeIndex > openIndex)
                    {
                        blocks.Add((tableName, sql.Substring(openIndex + 1, closeIndex - openIndex - 1)));
                        match = createTablePattern.Match(sql, closeIndex + 1);
                        continue;
                    }
                }
            }

            match = createTablePattern.Match(sql, match.Index + match.Length);
        }

        return blocks;
    }

    private static string StripComments(string sql)
    {
        var withoutBlockComments = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"--.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var builder = new StringBuilder();
        var depth = 0;
        char? quote = null;
        var inBracket = false;

        foreach (var character in text)
        {
            if (inBracket)
            {
                builder.Append(character);
                if (character == ']')
                {
                    inBracket = false;
                }
                continue;
            }

            if (quote is not null)
            {
                builder.Append(character);
                if (character == quote)
                {
                    quote = null;
                }
                continue;
            }

            if (character == '[')
            {
                inBracket = true;
                builder.Append(character);
                continue;
            }

            if (character is '\'' or '"' or '`')
            {
                quote = character;
                builder.Append(character);
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && depth > 0)
            {
                depth--;
            }

            if (character == separator && depth == 0)
            {
                parts.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(character);
            }
        }

        if (builder.Length > 0)
        {
            parts.Add(builder.ToString());
        }

        return parts;
    }

    private static int FindMatchingParenthesis(string text, int openIndex)
    {
        var depth = 0;
        char? quote = null;
        var inBracket = false;

        for (var index = openIndex; index < text.Length; index++)
        {
            var character = text[index];

            if (inBracket)
            {
                if (character == ']')
                {
                    inBracket = false;
                }
                continue;
            }

            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
                continue;
            }

            if (character == '[')
            {
                inBracket = true;
                continue;
            }

            if (character is '\'' or '"' or '`')
            {
                quote = character;
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static string ExtractQualifiedIdentifier(string text, int startIndex, out int endIndex)
    {
        var parts = new List<string>();
        var cursor = SkipWhitespace(text, startIndex);

        while (cursor < text.Length)
        {
            var part = ReadIdentifierToken(text, cursor, out var partEnd);
            if (string.IsNullOrWhiteSpace(part))
            {
                break;
            }

            parts.Add(part);
            cursor = SkipWhitespace(text, partEnd);

            if (cursor < text.Length && text[cursor] == '.')
            {
                cursor++;
                continue;
            }

            break;
        }

        endIndex = cursor;
        return string.Join('.', parts);
    }

    private static string ReadIdentifierToken(string text, int startIndex, out int endIndex)
    {
        endIndex = SkipWhitespace(text, startIndex);
        if (endIndex >= text.Length)
        {
            return string.Empty;
        }

        var first = text[endIndex];
        if (first == '[')
        {
            var close = text.IndexOf(']', endIndex + 1);
            if (close >= 0)
            {
                var token = text.Substring(endIndex, close - endIndex + 1);
                endIndex = close + 1;
                return token;
            }
        }

        if (first is '"' or '`')
        {
            var close = text.IndexOf(first, endIndex + 1);
            if (close >= 0)
            {
                var token = text.Substring(endIndex, close - endIndex + 1);
                endIndex = close + 1;
                return token;
            }
        }

        var cursor = endIndex;
        while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]) && text[cursor] is not '(' and not ')' and not ',' and not ';')
        {
            cursor++;
        }

        var result = text[endIndex..cursor];
        endIndex = cursor;
        return result;
    }

    private static string ReadSqlType(string rest)
    {
        var tokens = Tokenize(rest);
        var typeTokens = new List<string>();

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index].Trim();
            var keyword = token.Trim(',', ';').ToUpperInvariant();

            var parenIndex = keyword.IndexOf('(');
            var baseKeyword = parenIndex >= 0 ? keyword[..parenIndex].Trim() : keyword;

            if (ColumnStopWords.Contains(baseKeyword))
            {
                break;
            }

            typeTokens.Add(token);
        }

        return string.Join(' ', typeTokens).Trim();
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        var depth = 0;
        char? quote = null;

        foreach (var character in text)
        {
            if (quote is not null)
            {
                builder.Append(character);
                if (character == quote)
                {
                    quote = null;
                }
                continue;
            }

            if (character is '\'' or '"' or '`')
            {
                quote = character;
                builder.Append(character);
                continue;
            }

            if (character == '(')
            {
                depth++;
                builder.Append(character);
                continue;
            }

            if (character == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }
                builder.Append(character);
                continue;
            }

            if (char.IsWhiteSpace(character) && depth == 0)
            {
                if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                }
                continue;
            }

            AddIfSeparator(tokens, character, builder, depth);
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }

        return tokens;
    }

    private static void AddIfSeparator(List<string> tokens, char character, StringBuilder builder, int depth)
    {
        if (depth == 0 && character is ',' or ';')
        {
            if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
                builder.Clear();
            }

            return;
        }

        builder.Append(character);
    }

    private static List<string> ReadColumnsFromFirstParentheses(string text)
    {
        var open = text.IndexOf('(');
        if (open < 0)
        {
            return new List<string>();
        }

        var close = FindMatchingParenthesis(text, open);
        if (close <= open)
        {
            return new List<string>();
        }

        return SplitTopLevel(text.Substring(open + 1, close - open - 1), ',')
            .Select(column => NormalizeIdentifier(GetLastIdentifierPart(column.Trim())))
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .ToList();
    }

    private static string StripConstraintName(string definition)
    {
        if (!StartsWithKeyword(definition, "CONSTRAINT"))
        {
            return definition;
        }

        _ = ExtractQualifiedIdentifier(definition, "CONSTRAINT".Length, out var constraintNameEnd);
        return constraintNameEnd < definition.Length ? definition[constraintNameEnd..] : string.Empty;
    }

    private static bool StartsWithKeyword(string text, string keyword)
    {
        return Regex.IsMatch(text.TrimStart(), $@"^{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase);
    }

    private static int IndexOfKeyword(string text, string keyword)
    {
        var match = Regex.Match(text, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Index : -1;
    }

    private static string ReadFirstKeyword(string text)
    {
        var match = Regex.Match(text.TrimStart(), @"^[A-Za-z_]+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static bool MatchesPhrase(string text, int index, string phrase)
    {
        if (index + phrase.Length > text.Length)
        {
            return false;
        }

        return string.Equals(text.Substring(index, phrase.Length), phrase, StringComparison.OrdinalIgnoreCase);
    }

    private static SchemaColumn? FindColumn(SchemaTable table, string columnName)
    {
        return table.Columns.FirstOrDefault(column => string.Equals(column.Name, NormalizeIdentifier(columnName), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeIdentifier(string identifier)
    {
        var normalized = identifier.Trim().Trim(';');

        if (normalized.StartsWith('[') && normalized.EndsWith(']'))
        {
            normalized = normalized[1..^1];
        }
        else if ((normalized.StartsWith('"') && normalized.EndsWith('"')) || (normalized.StartsWith('`') && normalized.EndsWith('`')))
        {
            normalized = normalized[1..^1];
        }

        return normalized.Trim();
    }

    private static string GetLastIdentifierPart(string identifier)
    {
        var parts = SplitTopLevel(identifier, '.')
            .Select(NormalizeIdentifier)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        return parts.LastOrDefault() ?? NormalizeIdentifier(identifier);
    }

    private static string NormalizeSqlType(string sqlType)
    {
        var cleaned = sqlType.Replace("[", "").Replace("]", "").Replace("\"", "").Replace("`", "");
        return Regex.Replace(cleaned.Trim(), @"\s+", " ");
    }

    private static string NormalizeProjectName(string? projectName)
    {
        var value = string.IsNullOrWhiteSpace(projectName) ? "GeneratedMvcApp" : projectName.Trim();
        value = Regex.Replace(value, @"[^A-Za-z0-9_]", string.Empty);
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "GeneratedMvcApp";
        }

        if (char.IsDigit(value[0]))
        {
            value = "App" + value;
        }

        return value;
    }

    private static void ApplyReadableLayout(DatabaseSchema schema)
    {
        const double startX = 80;
        const double startY = 80;
        const double columnGap = 380;
        const double rowGap = 320;
        const int columnsPerRow = 3;

        for (var index = 0; index < schema.Tables.Count; index++)
        {
            var table = schema.Tables[index];
            table.X = startX + (index % columnsPerRow) * columnGap;
            table.Y = startY + (index / columnsPerRow) * rowGap;
        }
    }
}

