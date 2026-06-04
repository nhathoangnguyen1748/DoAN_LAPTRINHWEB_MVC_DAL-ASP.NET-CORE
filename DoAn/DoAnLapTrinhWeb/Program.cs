using DoAnLapTrinhWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<SqlSchemaParser>();
builder.Services.AddSingleton<SchemaReviewService>();
builder.Services.AddSingleton<AspNetMvcExportService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=DatabaseDesigner}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
