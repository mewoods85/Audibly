// Author: rstewa · https://github.com/rstewa
// Created: 04/15/2024
// Updated: 12/05/2025

namespace Audibly.Models;

/// <summary>
///     Represents an audiobook.
/// </summary>
public class Audiobook : DbObject, IEquatable<Audiobook>
{
    public string Author { get; set; } = string.Empty;
    public string Composer { get; set; } = string.Empty;
    public int CurrentSourceFileIndex { get; set; }
    public DateTime? DateLastPlayed { get; set; }
    public string Description { get; set; } = string.Empty;
    public long Duration { get; set; } // *

    // public int CurrentTimeMs { get; set; } // *
    public string CoverImagePath { get; set; } = string.Empty;

    public string ThumbnailPath { get; set; } = string.Empty;

    public List<SourceFile> SourcePaths { get; set; } = new List<SourceFile>();
    public bool IsNowPlaying { get; set; }
    public double PlaybackSpeed { get; set; }
    public double Progress { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string Title { get; set; } = string.Empty;
    public double Volume { get; set; }
    public int? CurrentChapterIndex { get; set; }
    public bool IsCompleted { get; set; }

    // New: series metadata
    // Name of the series this audiobook belongs to (null/empty if standalone)
    public string Series { get; set; } = string.Empty;

    // Position in the series (nullable)
    public int? SeriesNumber { get; set; }

    public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();

    public bool Equals(Audiobook? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Author, other.Author, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Title, other.Title, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        return obj.GetType() == GetType() && Equals((Audiobook)obj);
    }

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.Add(Author, StringComparer.OrdinalIgnoreCase);
        hashCode.Add(Title, StringComparer.OrdinalIgnoreCase);
        return hashCode.ToHashCode();
    }
}