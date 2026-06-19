using DoAnLapTrinhWeb.Models.Designer;
using DoAnLapTrinhWeb.Models.Projects;
using Microsoft.EntityFrameworkCore;

namespace DoAnLapTrinhWeb.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DesignerProject> Projects => Set<DesignerProject>();
    public DbSet<ProjectCollaborator> ProjectCollaborators => Set<ProjectCollaborator>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DesignerProject>(entity =>
        {
            entity.ToTable("DesignerProjects");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.OwnerEmail).HasMaxLength(256).IsRequired();
            entity.Property(e => e.CreatedAt);
            entity.Property(e => e.UpdatedAt);

            entity.Property(e => e.Schema)
                  .HasColumnType("nvarchar(max)")
                  .HasConversion(
                      v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                      v => System.Text.Json.JsonSerializer.Deserialize<DatabaseSchema>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new DatabaseSchema());

            entity.Ignore(e => e.CollaboratorEmails);

            entity.HasMany<ProjectCollaborator>()
                  .WithOne()
                  .HasForeignKey(c => c.ProjectId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.OwnerEmail);
        });

        modelBuilder.Entity<ProjectCollaborator>(entity =>
        {
            entity.ToTable("ProjectCollaborators");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProjectId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(256).IsRequired();

            entity.HasIndex(e => new { e.ProjectId, e.Email }).IsUnique();
            entity.HasIndex(e => e.Email);
        });
    }
}
