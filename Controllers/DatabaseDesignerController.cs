using DoAnLapTrinhWeb.Models.Designer;
using DoAnLapTrinhWeb.Services;
using Microsoft.AspNetCore.Mvc;

namespace DoAnLapTrinhWeb.Controllers;

public class DatabaseDesignerController : Controller
{
    private readonly SqlSchemaParser _parser;
    private readonly SchemaReviewService _reviewService;
    private readonly AspNetMvcExportService _exportService;

    public DatabaseDesignerController(
        SqlSchemaParser parser,
        SchemaReviewService reviewService,
        AspNetMvcExportService exportService)
    {
        _parser = parser;
        _reviewService = reviewService;
        _exportService = exportService;
    }

    public IActionResult Index()
    {
        return View();
    }

    [HttpPost]
    public IActionResult ImportSql([FromBody] SqlImportRequest request)
    {
        var schema = _parser.Parse(request.Sql, request.ProjectName);
        return Json(schema);
    }

    [HttpPost]
    public async Task<IActionResult> Review([FromBody] DatabaseSchema schema, CancellationToken cancellationToken)
    {
        var result = await _reviewService.ReviewAsync(schema, cancellationToken);
        return Json(result);
    }

    [HttpPost]
    public async Task<IActionResult> FixReview([FromBody] ReviewFixRequest request, CancellationToken cancellationToken)
    {
        var result = await _reviewService.FixAsync(request, cancellationToken);
        return Json(result);
    }

    [HttpPost]
    public IActionResult Export([FromBody] DatabaseSchema schema)
    {
        if (schema.Tables.Count == 0)
        {
            return BadRequest(new { message = "Add or import at least one table before exporting." });
        }

        var projectName = string.IsNullOrWhiteSpace(schema.ProjectName) ? "GeneratedMvcApp" : schema.ProjectName.Trim();
        var zip = _exportService.CreateZip(schema);
        return File(zip, "application/zip", $"{projectName}-aspnet-mvc-sqlserver.zip");
    }
}
