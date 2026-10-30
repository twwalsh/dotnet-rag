using Microsoft.EntityFrameworkCore;

namespace SupportRag.Core.Data;

public sealed class RagDbContext(DbContextOptions<RagDbContext> options) : DbContext(options)
{
    public DbSet<Chunk> Chunks => Set<Chunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<Chunk>(e =>
        {
            e.ToTable("chunks");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasMaxLength(64);
            e.Property(c => c.Source).HasMaxLength(32);
            e.Property(c => c.ContentHash).HasMaxLength(64);
            e.Property(c => c.EmbeddingModel).HasMaxLength(128);
            // No fixed dimension: the column accepts whichever embedding model is configured, and a model
            // change is handled by re-embedding, not by a migration. At a few hundred rows an exact scan is
            // faster than any index; an HNSW index needs a fixed dimension and is the first thing to add
            // when the corpus is a few hundred thousand.
            e.Property(c => c.Embedding).HasColumnType("vector");
            e.HasIndex(c => c.Source);
            e.HasGeneratedTsVectorColumn(c => c.SearchVector, "english", c => new { c.Title, c.Text })
             .HasIndex(c => c.SearchVector)
             .HasMethod("GIN");
        });
    }
}
