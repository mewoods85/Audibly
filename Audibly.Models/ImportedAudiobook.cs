// Author: rstewa · https://github.com/rstewa
// Created: 11/20/2024
// Updated: 12/05/2025

namespace Audibly.Models;

public class ImportedAudiobook
{
    public int CurrentTimeMs { get; set; }
    public string CoverImagePath { get; set; }
    public string FilePath { get; set; }
    public double Progress { get; set; }
    public int? CurrentChapterIndex { get; set; }
    public bool IsNowPlaying { get; set; }
    public bool IsCompleted { get; set; }

    // Name of the series this audiobook belongs to (null/empty if standalone)
    public string Series { get; set; }

    // Position in the series (nullable)
    public int? SeriesNumber { get; set; }
}