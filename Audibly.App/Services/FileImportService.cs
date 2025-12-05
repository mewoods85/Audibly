// Author: rstewa · https://github.com/rstewa
// Created: 04/15/2024
// Updated: 12/05/2025

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using ATL;
using Audibly.App.Extensions;
using Audibly.App.Services.Interfaces;
using Audibly.App.ViewModels;
using Audibly.Models;
using AutoMapper;
using Microsoft.UI.Xaml.Controls;
using Sharpener.Extensions;
using ChapterInfo = Audibly.Models.ChapterInfo;

namespace Audibly.App.Services;

public class FileImportService : IImportFiles
{
    private static IMapper _mapper;

    public FileImportService()
    {
        _mapper = new MapperConfiguration(cfg => { cfg.CreateMap<ATL.ChapterInfo, ChapterInfo>(); }).CreateMapper();
    }

    #region IImportFiles Members

    public event IImportFiles.ImportCompletedHandler? ImportCompleted;

    // TODO: need a better way of checking if a file is one we have already imported
    public async Task ImportDirectoryAsync(string path, CancellationToken cancellationToken,
        Func<int, int, string, bool, Task> progressCallback)
    {
        var didFail = false;

        var files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".m4b", StringComparison.OrdinalIgnoreCase) ||
                           file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var numberOfFiles = files.Count;

        var filesList = files.AsList();

        foreach (var file in files)
        {
            // Check if cancellation was requested
            cancellationToken.ThrowIfCancellationRequested();

            var audiobook = await CreateAudiobook(file);

            if (audiobook == null) didFail = true;

            if (audiobook != null)
            {
                // insert the audiobook into the database
                var result = await App.Repository.Audiobooks.UpsertAsync(audiobook);
                if (result == null) didFail = true;
            }

            var title = audiobook?.Title ?? Path.GetFileNameWithoutExtension(file);

            // report progress
            await progressCallback(filesList.IndexOf(file), numberOfFiles, title, didFail);

            didFail = false;
        }

        ImportCompleted?.Invoke();
    }

    public async Task ImportFromJsonAsync(StorageFile file, CancellationToken cancellationToken,
        Func<int, int, string, bool, Task> progressCallback)
    {
        // read the json string from the file
        var json = FileIO.ReadTextAsync(file).AsTask().Result;

        if (string.IsNullOrEmpty(json))
        {
            // log the error
            App.ViewModel.LoggingService.LogError(new Exception("Failed to read the json file"), true);
            ImportCompleted?.Invoke();
            return;
        }

        // deserialize the json string to a list of audiobooks
        var importedAudiobooks = JsonSerializer.Deserialize<List<ImportedAudiobook>>(json);

        if (importedAudiobooks == null)
        {
            // log the error
            App.ViewModel.LoggingService.LogError(new Exception("Failed to deserialize the json file"), true);
            return;
        }

        var didFail = false;
        var numberOfFiles = importedAudiobooks.Count;

        foreach (var importedAudiobook in importedAudiobooks)
        {
            // Check if cancellation was requested
            cancellationToken.ThrowIfCancellationRequested();

            // verify that the audiobook file exists
            if (!File.Exists(importedAudiobook.FilePath))
            {
                // log the error
                App.ViewModel.LoggingService.LogError(new Exception("Audiobook file does not exist"));
                App.ViewModel.EnqueueNotification(new Notification
                {
                    Message = $"Audiobook file was moved or deleted: {importedAudiobook.FilePath}",
                    Severity = InfoBarSeverity.Warning
                });

                didFail = true;
                continue;
            }

            var audiobook = await CreateAudiobook(importedAudiobook.FilePath, importedAudiobook);

            if (audiobook == null)
            {
                didFail = true;
            }
            else
            {
                // insert the audiobook into the database
                var result = await App.Repository.Audiobooks.UpsertAsync(audiobook);
                if (result == null) didFail = true;
            }

            var title = audiobook?.Title ?? Path.GetFileNameWithoutExtension(importedAudiobook.FilePath);

            // report progress
            await progressCallback(importedAudiobooks.IndexOf(importedAudiobook), numberOfFiles, title, didFail);

            didFail = false;
        }

        ImportCompleted?.Invoke();
    }

    public async Task ImportFromMultipleFilesAsync(string[] paths, CancellationToken cancellationToken,
        Func<int, int, string, bool, Task> progressCallback)
    {
        var didFail = false;

        // todo: need to see if we can call progressCallback from the CreateAudiobook function
        var numberOfFiles = 1; // paths.Length;

        // Check if cancellation was requested
        cancellationToken.ThrowIfCancellationRequested();

        var audiobook = await CreateAudiobookFromMultipleFiles(paths);

        if (audiobook == null) didFail = true;

        if (audiobook != null)
        {
            var existingAudioBook = await App.Repository.Audiobooks.GetByTitleAuthorComposerAsync(audiobook.Title,
                audiobook.Author,
                audiobook.Composer);
            if (existingAudioBook != null)
            {
                // log the error
                App.ViewModel.LoggingService.LogError(new Exception("Audiobook already exists in the database"));
                App.ViewModel.EnqueueNotification(new Notification
                {
                    Message = $"Audiobook is already in the library: {existingAudioBook.Title}",
                    Severity = InfoBarSeverity.Warning
                });

                didFail = true;

                await progressCallback(numberOfFiles, numberOfFiles, audiobook.Title, didFail);

                ImportCompleted?.Invoke();

                return;
            }

            // insert the audiobook into the database
            var result = await App.Repository.Audiobooks.UpsertAsync(audiobook);
            if (result == null) didFail = true;
        }

        var title = audiobook?.Title ?? Path.GetFileNameWithoutExtension(paths.First());

        // report progress
        await progressCallback(numberOfFiles, numberOfFiles, title, didFail);

        ImportCompleted?.Invoke();
    }

    public async Task ImportFileAsync(string path, CancellationToken cancellationToken,
        Func<int, int, string, bool, Task> progressCallback)
    {
        // Check if cancellation was requested
        cancellationToken.ThrowIfCancellationRequested();

        var didFail = false;
        var audiobook = await CreateAudiobook(path);

        if (audiobook == null) didFail = true;

        // insert the audiobook into the database
        if (audiobook != null)
        {
            var result = await App.Repository.Audiobooks.UpsertAsync(audiobook);
            if (result == null) didFail = true;
        }

        var title = audiobook?.Title ?? Path.GetFileNameWithoutExtension(path);

        // report progress
        // NOTE: keeping this bc this function will be used in the future to import 1-to-many files
        await progressCallback(1, 1, title, didFail);

        ImportCompleted?.Invoke();
    }

    #endregion

    private static async Task<Audiobook?> CreateAudiobookFromMultipleFiles(string[] paths)
    {
        try
        {
            var audiobook = new Audiobook
            {
                CurrentSourceFileIndex = 0,
                SourcePaths = [],
                PlaybackSpeed = 1.0,
                Volume = 1.0,
                IsCompleted = false
            };

            string? seriesFromTags = null;
            int? seriesNumberFromTags = null;

            var sourceFileIndex = 0;
            var chapterIndex = 0;
            foreach (var path in paths)
            {
                var track = new Track(path);

                // check if this is the 1st file
                if (audiobook.SourcePaths.Count == 0)
                {
                    audiobook.Title = track.Title;
                    audiobook.Composer = track.Composer;
                    audiobook.Author = track.Artist;
                    audiobook.Description =
                        track.Description.IsNullOrEmpty()
                            ? track.Comment.IsNullOrEmpty()
                                ? track.AdditionalFields.TryGetValue("\u00A9des", out var value) ? value : track.Comment
                                : track.Comment
                            : track.Description;
                    audiobook.ReleaseDate = track.Date;

                    // parse series from common tags (Album / ©alb)
                    seriesFromTags = track.Album;
                    if (string.IsNullOrWhiteSpace(seriesFromTags) && track.AdditionalFields.TryGetValue("\u00A9alb", out var alb))
                        seriesFromTags = alb;

                    // try parse series number from partofset / track / tracknumber
                    if (track.AdditionalFields.TryGetValue("partofset", out var partOfSetRaw) ||
                        track.AdditionalFields.TryGetValue("track", out partOfSetRaw) ||
                        track.AdditionalFields.TryGetValue("tracknumber", out partOfSetRaw))
                    {
                        var parts = partOfSetRaw.Split('/');
                        if (int.TryParse(parts[0], out var parsed)) seriesNumberFromTags = parsed;
                    }
                }

                var sourceFile = new SourceFile
                {
                    Index = sourceFileIndex++,
                    FilePath = path,
                    Duration = track.Duration,
                    CurrentTimeMs = 0
                };

                audiobook.SourcePaths.Add(sourceFile);

                // read in the chapters
                foreach (var ch in track.Chapters)
                {
                    var tmp = _mapper.Map<ChapterInfo>(ch);
                    tmp.Index = chapterIndex++;
                    tmp.ParentSourceFileIndex = sourceFile.Index;
                    audiobook.Chapters.Add(tmp);
                }

                if (track.Chapters.Count == 0)
                    // create a single chapter for the entire book
                    audiobook.Chapters.Add(new ChapterInfo
                    {
                        StartTime = 0,
                        EndTime = Convert.ToUInt32(audiobook.SourcePaths[sourceFileIndex - 1].Duration * 1000),
                        StartOffset = 0,
                        EndOffset = 0,
                        UseOffset = false,
                        Title = track.Title,
                        Index = chapterIndex++,
                        ParentSourceFileIndex = sourceFile.Index
                    });
            }

            // get duration of the entire audiobook
            audiobook.Duration = audiobook.SourcePaths.Sum(x => x.Duration);

            // assign series values (default to empty string for Series to avoid nulls)
            audiobook.Series = seriesFromTags ?? string.Empty;
            audiobook.SeriesNumber = seriesNumberFromTags;

            // save the cover image somewhere
            var imageBytes = new Track(paths.First()).EmbeddedPictures.FirstOrDefault()?.PictureData;

            // generate hash from title, author, and composer
            var hash = $"{audiobook.Title}{audiobook.Author}{audiobook.Composer}".GetSha256Hash();

            (audiobook.CoverImagePath, audiobook.ThumbnailPath) =
                await App.ViewModel.AppDataService.WriteCoverImageAsync(hash, imageBytes);

            audiobook.CurrentChapterIndex = 0;

            return audiobook;
        }
        catch (Exception e)
        {
            // log the error
            App.ViewModel.LoggingService.LogError(e, true);
            return null;
        }
    }

    private static async Task<Audiobook?> CreateAudiobook(string path, ImportedAudiobook? importedAudiobook = null)
    {
        try
        {
            var track = new Track(path);

            var existingAudioBook =
                await App.Repository.Audiobooks.GetByTitleAuthorComposerAsync(track.Title, track.Artist,
                    track.Composer);
            if (existingAudioBook != null)
            {
                // log the error
                App.ViewModel.LoggingService.LogError(new Exception("Audiobook already exists in the database"));
                App.ViewModel.EnqueueNotification(new Notification
                {
                    Message = "Audiobook is already in the library.",
                    Severity = InfoBarSeverity.Warning
                });
                return null;
            }

            var sourceFile = new SourceFile
            {
                Index = 0,
                FilePath = path,
                Duration = track.Duration,
                CurrentTimeMs = importedAudiobook?.CurrentTimeMs ?? 0
            };

            // parse series information (prefer importedAudiobook values when available)
            string? seriesFromTags = importedAudiobook?.Series ?? track.Album;
            if (string.IsNullOrWhiteSpace(seriesFromTags) && track.AdditionalFields.TryGetValue("\u00A9alb", out var alb2))
                seriesFromTags = alb2;

            int? seriesNumberFromTags = importedAudiobook?.SeriesNumber;
            if (seriesNumberFromTags == null)
            {
                if (track.AdditionalFields.TryGetValue("partofset", out var part) ||
                    track.AdditionalFields.TryGetValue("track", out part) ||
                    track.AdditionalFields.TryGetValue("tracknumber", out part))
                {
                    var parts = part.Split('/');
                    if (int.TryParse(parts[0], out var p2)) seriesNumberFromTags = p2;
                }
            }

            var audiobook = new Audiobook
            {
                CurrentSourceFileIndex = 0,
                Title = track.Title,
                Composer = track.Composer,
                CurrentChapterIndex = importedAudiobook?.CurrentChapterIndex ?? 0,
                Duration = track.Duration,
                Author = track.Artist,
                Description =
                    track.Description.IsNullOrEmpty()
                        ? track.Comment.IsNullOrEmpty()
                            ? track.AdditionalFields.TryGetValue("\u00A9des", out var value) ? value : track.Comment
                            : track.Comment
                        : track.Description,
                PlaybackSpeed = 1.0,
                Progress = importedAudiobook?.Progress ?? 0,
                ReleaseDate = track.Date,
                Volume = 1.0,
                IsCompleted = importedAudiobook?.IsCompleted ?? false,
                IsNowPlaying = importedAudiobook?.IsNowPlaying ?? false,
                SourcePaths =
                [
                    sourceFile
                ],
                // series metadata
                Series = seriesFromTags ?? string.Empty,
                SeriesNumber = seriesNumberFromTags
            };

            // save the cover image somewhere
            var imageBytes = track.EmbeddedPictures.FirstOrDefault()?.PictureData;

            // generate hash from title, author, and composer
            var hash = $"{audiobook.Title}{audiobook.Author}{audiobook.Composer}".GetSha256Hash();

            (audiobook.CoverImagePath, audiobook.ThumbnailPath) =
                await App.ViewModel.AppDataService.WriteCoverImageAsync(hash, imageBytes);

            // read in the chapters
            var chapterIndex = 0;
            foreach (var ch in track.Chapters)
            {
                var tmp = _mapper.Map<ChapterInfo>(ch);
                tmp.Index = chapterIndex++;
                tmp.ParentSourceFileIndex = sourceFile.Index;
                audiobook.Chapters.Add(tmp);
            }

            if (audiobook.Chapters.Count == 0)
                // create a single chapter for the entire book
                audiobook.Chapters.Add(new ChapterInfo
                {
                    StartTime = 0,
                    EndTime = Convert.ToUInt32(audiobook.SourcePaths.First().Duration * 1000),
                    StartOffset = 0,
                    EndOffset = 0,
                    UseOffset = false,
                    Title = audiobook.Title,
                    Index = 0,
                    ParentSourceFileIndex = sourceFile.Index
                });

            return audiobook;
        }
        catch (Exception e)
        {
            // log the error
            App.ViewModel.LoggingService.LogError(e, true);
            return null;
        }
    }
}