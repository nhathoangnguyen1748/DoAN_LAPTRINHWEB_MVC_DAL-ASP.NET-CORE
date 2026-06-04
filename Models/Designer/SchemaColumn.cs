namespace DoAnLapTrinhWeb.Models.Designer;

public class SchemaColumn
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string SqlType { get; set; } = "nvarchar(255)";
    public bool IsPrimaryKey { get; set; }
    public bool IsNullable { get; set; } = true;
    public bool IsUnique { get; set; }
    public string? ForeignKeyTable { get; set; }
    public string? ForeignKeyColumn { get; set; }
    public int Order { get; set; }
}
