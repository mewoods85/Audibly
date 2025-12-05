// Adds series properties and a display helper to AudiobookViewModel
// Created: 12/05/2025

using System;

namespace Audibly.App.ViewModels;

public partial class AudiobookViewModel
{
    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    public string Series
    {
        get => Model.Series;
        set
        {
            if (value == Model.Series) return;
            Model.Series = value;
            IsModified = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SeriesDisplay));
        }
    }

    /// <summary>
    /// Gets or sets the series number (nullable).
    /// </summary>
    public int? SeriesNumber
    {
        get => Model.SeriesNumber;
        set
        {
            if (value == Model.SeriesNumber) return;
            Model.SeriesNumber = value;
            IsModified = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SeriesDisplay));
        }
    }

    /// <summary>
    /// Returns a compact string for display, e.g. "My Series #2" or empty if no series.
    /// </summary>
    public string SeriesDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model.Series)) return string.Empty;
            return Model.SeriesNumber.HasValue ? $"{Model.Series} #{Model.SeriesNumber}" : Model.Series;
        }
    }
}