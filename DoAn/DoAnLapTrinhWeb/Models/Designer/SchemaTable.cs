namespace DoAnLapTrinhWeb.Models.Designer;

public class SchemaTable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string LastValidName { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public List<SchemaColumn> Columns { get; set; } = new();
}
