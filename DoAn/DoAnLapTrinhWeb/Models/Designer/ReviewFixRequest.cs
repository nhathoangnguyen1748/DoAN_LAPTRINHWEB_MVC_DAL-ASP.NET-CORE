namespace DoAnLapTrinhWeb.Models.Designer;

public class ReviewFixRequest
{
    public DatabaseSchema Schema { get; set; } = new();
    public ReviewIssue? Issue { get; set; }
    public bool FixAll { get; set; }
}
