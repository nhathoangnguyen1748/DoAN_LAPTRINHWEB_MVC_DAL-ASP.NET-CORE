namespace DoAnLapTrinhWeb.Models.Designer;

public class DatabaseSchema
{
    public string ProjectName { get; set; } = "GeneratedMvcApp";
    public List<SchemaTable> Tables { get; set; } = new();
    public bool IncludeMockData { get; set; }
}
