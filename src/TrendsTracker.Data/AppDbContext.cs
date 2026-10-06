using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TrendsTracker.Data;

/// <summary>
/// EF Core context for the TrendsTracker Postgres database. Holds the saved
/// company analyses and the news feed.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<CompanyAnalysis> Companies => Set<CompanyAnalysis>();
    public DbSet<NewsItem> NewsItems => Set<NewsItem>();
    public DbSet<TranscriptChunk> TranscriptChunks => Set<TranscriptChunk>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Enable the pgvector extension for similarity search over transcript chunks.
        b.HasPostgresExtension("vector");

        b.Entity<TranscriptChunk>(e =>
        {
            e.ToTable("transcript_chunks");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Stock);
            e.Property(x => x.Stock).HasMaxLength(32);
            e.Property(x => x.TranscriptDate).HasMaxLength(32);
            // nomic-embed-text produces 768-dimensional vectors.
            e.Property(x => x.Embedding).HasColumnType("vector(768)");
        });

        b.Entity<CompanyAnalysis>(e =>
        {
            e.ToTable("companies");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Stock).IsUnique();   // one row per stock; upsert on re-analysis
            e.Property(x => x.Stock).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.SignalType).HasMaxLength(32);

            // Store the string lists as JSON text columns, with a value comparer so
            // EF change-tracking works correctly on the collections.
            var listConverter = new ValueConverter<List<string>, string>(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new());

            var listComparer = new ValueComparer<List<string>>(
                (a, c) => (a ?? new()).SequenceEqual(c ?? new()),
                v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                v => v == null ? new() : v.ToList());

            e.Property(x => x.EvidenceQuotes).HasConversion(listConverter, listComparer).HasColumnType("jsonb");
            e.Property(x => x.Sources).HasConversion(listConverter, listComparer).HasColumnType("jsonb");
        });

        b.Entity<NewsItem>(e =>
        {
            e.ToTable("news_items");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Url);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Source).HasMaxLength(128);
        });
    }
}
