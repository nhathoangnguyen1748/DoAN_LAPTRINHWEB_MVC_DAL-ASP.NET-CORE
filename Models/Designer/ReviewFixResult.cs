namespace DoAnLapTrinhWeb.Models.Designer;

public class ReviewFixResult
{
    public DatabaseSchema Schema { get; set; } = new();
    public AiReviewResult Review { get; set; } = new();
    public int FixedCount { get; set; }
    public string Message { get; set; } = string.Empty;
}
